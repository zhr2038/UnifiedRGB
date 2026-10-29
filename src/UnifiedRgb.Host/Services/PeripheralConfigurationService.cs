using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.Services;

internal interface IPeripheralConfigurationResources
{
    Task ReleaseAsync(CancellationToken ct);
    Task ResumeAsync(CancellationToken ct);
}

public sealed class PeripheralConfigurationService : IPeripheralConfigurationService
{
    private readonly TimeSpan _operationTimeout;
    private readonly PeripheralConfigurationGate _gate;
    private readonly PeripheralConfigurationMarker _marker;
    private readonly IPeripheralConfigurationResources _resources;
    private readonly string _journalPath;
    private PeripheralConfigurationStatusDto _status;
    private Task? _releaseInFlight;
    private Task? _resumeInFlight;
    private bool _resumeReadyToCommit;

    internal PeripheralConfigurationService(UnifiedRgbOptions options, PeripheralConfigurationGate gate,
        IPeripheralConfigurationResources resources, TimeSpan? operationTimeout = null)
    {
        _gate = gate;
        _marker = PeripheralConfigurationMarker.For(options);
        _resources = resources;
        _journalPath = Path.Combine(options.ResolvedDataDirectory, "peripherals", "keyboard-display-session.json");
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        var recoveryPending = !gate.RogAcquisitionAllowed && File.Exists(_journalPath);
        const string recoveryError = "A keyboard recovery journal remains after restart; matching recorded-transport restoration requires an explicit Enter reattempt.";
        _status = gate.InitialError is { } error
            ? new(true, "Error", error, false, DateTimeOffset.UtcNow, error)
            : recoveryPending
                ? new(true, "Error", recoveryError, false, DateTimeOffset.UtcNow, recoveryError)
                : gate.RogAcquisitionAllowed
                    ? new(false, "Active", "ROG peripherals use fresh current settings; no configuration handoff is requested.", false, DateTimeOffset.UtcNow, null)
                    : new(true, "Ready", "Persistent handoff was loaded before any ROG session was opened.", true, DateTimeOffset.UtcNow, null);
    }

    public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _status));
    }

    public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) =>
        _gate.RunExclusiveAsync(async _ =>
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _status) is { ConfigurationMode: true, State: "Ready" } ready) return ready;
            var previouslyAllowed = _gate.RogAcquisitionAllowed;
            _gate.BlockRogAcquisition();
            _resumeReadyToCommit = false;
            Publish(new(true, "Entering", "Persisting the handoff, restoring original keyboard settings, joining I/O and disposing ROG sessions.", false, DateTimeOffset.UtcNow, null));
            try { _marker.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (previouslyAllowed) _gate.AllowRogAcquisition();
                return Publish(new(!previouslyAllowed, "Error", "The peripheral-mode marker could not be persisted; no handoff was claimed.", false, DateTimeOffset.UtcNow, Error(ex)));
            }

            try
            {
                await ObserveReleaseAsync().ConfigureAwait(false);
                return Publish(new(true, "Ready", "ROG keyboard OLED/lighting, mouse and receiver sessions are disposed; further reports and reacquisition are stopped.", true, DateTimeOffset.UtcNow, null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Publish(new(true, "Error", FailureDetail(ex, "ROG release or original keyboard restoration did not complete; recovery journals are retained and ROG remains blocked."), false, DateTimeOffset.UtcNow, Error(ex)));
            }
        }, ct);

    public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) =>
        _gate.RunExclusiveAsync(async _ =>
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _status) is { ConfigurationMode: false, State: "Active" } active) return active;
            if (File.Exists(_journalPath))
            {
                if (Volatile.Read(ref _status) is { State: "Error", ConfigurationMode: true } pending) return pending;
                const string error = "A keyboard recovery journal requires verified matching restoration before normal resume.";
                return Publish(new(true, "Error", error, false, DateTimeOffset.UtcNow, error));
            }

            _gate.BlockRogAcquisition();
            Publish(new(true, "Resuming", "Proving fresh current-settings sessions before committing the end of the handoff; no pre-web snapshot will be replayed.", false, DateTimeOffset.UtcNow, null));
            if (!_resumeReadyToCommit)
            {
                _gate.BeginFreshResume();
                try
                {
                    await ObserveResumeAsync().ConfigureAwait(false);
                    _resumeReadyToCommit = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Publish(new(true, "Error", FailureDetail(ex, "Fresh ROG resume was not proven; the requested handoff and acquisition block remain in force."), false, DateTimeOffset.UtcNow, Error(ex)));
                }
                finally { _gate.EndFreshResume(); }
            }

            try { _marker.Remove(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Publish(new(true, "Error", "Fresh sessions succeeded, but the persistent handoff marker could not be removed; ordinary ROG acquisition remains blocked.", false, DateTimeOffset.UtcNow, Error(ex)));
            }
            _resumeReadyToCommit = false;
            _gate.AllowRogAcquisition();
            return Publish(new(false, "Active", "Fresh ROG sessions captured current settings; truly absent or asleep devices remain offline for normal rechecks.", false, DateTimeOffset.UtcNow, null));
        }, ct);

    private async Task ObserveReleaseAsync()
    {
        var task = _releaseInFlight ??= StartBounded(_resources.ReleaseAsync);
        try { await task.WaitAsync(_operationTimeout).ConfigureAwait(false); _releaseInFlight = null; }
        catch { if (task.IsCompleted) _releaseInFlight = null; throw; }
    }

    private async Task ObserveResumeAsync()
    {
        var task = _resumeInFlight ??= StartBounded(_resources.ResumeAsync);
        try { await task.WaitAsync(_operationTimeout).ConfigureAwait(false); _resumeInFlight = null; }
        catch { if (task.IsCompleted) _resumeInFlight = null; throw; }
    }

    private Task StartBounded(Func<CancellationToken, Task> operation)
    {
        var bounded = new CancellationTokenSource(_operationTimeout);
        try
        {
            var task = operation(bounded.Token);
            _ = task.ContinueWith(_ => bounded.Dispose());
            return task;
        }
        catch
        {
            bounded.Dispose();
            throw;
        }
    }

    private PeripheralConfigurationStatusDto Publish(PeripheralConfigurationStatusDto status)
    {
        Volatile.Write(ref _status, status);
        return status;
    }

    private static string Error(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static string FailureDetail(Exception ex, string prefix) => ex switch
    {
        OperationCanceledException or TimeoutException => prefix + " The observable operation timed out or a device remained busy/asleep; later completion cannot publish a late success.",
        IOException or InvalidDataException => prefix + " A device was offline, asleep, disconnected, busy or returned a partial response.",
        _ => prefix,
    };
}

internal sealed class RogPeripheralConfigurationResources(LightingEngine engine, KeyboardDisplayService keyboard)
    : IPeripheralConfigurationResources
{
    public async Task ReleaseAsync(CancellationToken ct)
    {
        var errors = new List<Exception>();
        try { await engine.Bringup.EndClassAsync(DeviceClass.AsusRog, "peripheral configuration handoff").ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        try { await engine.Adapters.ReleaseForPeripheralConfigurationAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        try { await keyboard.ReleaseForPeripheralConfigurationAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        engine.RefreshControllers();
        if (errors.Count != 0) throw new AggregateException("ROG peripheral release was incomplete.", errors);
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await keyboard.ResumeAfterPeripheralConfigurationAsync(ct).ConfigureAwait(false);
        await engine.Adapters.ResumeAfterPeripheralConfigurationAsync(ct).ConfigureAwait(false);
        engine.RefreshControllers();
    }
}
