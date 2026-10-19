using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Native bring-up sessions (NATIVE design §6; overnight addendum §4, §9). A session belongs to the connection
/// (<c>owner</c>) that began it: only that owner's steps, renew, end or disconnect act on it; other connections are refused
/// and their queries/disconnects never touch it. Lease expiry, a failed step and Host stop also end it. Ending is idempotent
/// and runs once: the adapter's release, then the writer and render-loop holds are lifted. Nothing here changes takeover or
/// starts/stops services; ControlVerified is never set by a session result. Steps of one session run one at a time, and an
/// end waits for the running step, so a release never interleaves with a probe or marker. After the owner's End, a
/// disconnect, expiry or a failed/cancelled step a verified controller's writer is reopened; Host stop and takeover-off
/// relinquish it instead (nothing reopened). A step cancelled while waiting to run leaves the session as it was.
/// </summary>
public sealed partial class BringupManager : IAsyncDisposable
{
    private readonly AdapterHost _adapters;
    private readonly RenderLoop _loop;
    private readonly IWriterGuard _guard;
    private readonly BringupOptions _options;
    private readonly ProbeLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<ControllerId, Session> _sessions = [];

    public BringupManager(AdapterHost adapters, RenderLoop loop, IWriterGuard guard, BringupOptions options, ProbeLog log,
        TimeProvider? time = null, ILogger? logger = null)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.LeaseSeconds, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLeaseSeconds, options.LeaseSeconds, nameof(options));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public ProbeLog ProbeJournal => _log;

    /// <summary>Running sessions (controller, owner); for status and tests.</summary>
    public IReadOnlyList<(ControllerId Id, string Owner)> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.Where(s => !s.IsEnded).Select(s => (s.Id, s.Owner)).ToArray();
            }
        }
    }

    public async Task<BringupSessionDto> BeginAsync(string owner, string controllerId, int leaseSeconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var entry = Entry(controllerId);
        var id = entry.Descriptor.Id;
        var cls = entry.Descriptor.Class;
        if (entry.Adapter is not INativeBringup native)
        {
            throw new ArgumentException($"{controllerId} ({cls}) has no native bring-up.", nameof(controllerId));
        }

        if (!_guard.IsTakeoverEnabled(cls))
        {
            throw new InvalidOperationException($"Takeover is off for {cls}; a bring-up session needs it on.");
        }

        var verdict = _guard.Check(cls);
        if (verdict.State != GuardState.Clear)
        {
            throw new InvalidOperationException($"{cls} is {verdict.State} by {verdict.Writer}; no bring-up session.");
        }

        var lease = leaseSeconds <= 0 ? _options.LeaseSeconds : Math.Min(leaseSeconds, _options.MaxLeaseSeconds);
        Session s;
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var existing))
            {
                if (existing.IsEnded)
                {
                    throw new InvalidOperationException($"{controllerId}: the previous bring-up session is still releasing; try again.");
                }

                if (!string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{controllerId} is in a bring-up session of another connection.");
                }

                return RenewLocked(existing);
            }

            s = new Session(id, entry, native, owner, lease); // its step lock starts taken: held until the open completes
            _sessions[id] = s;
        }

        try
        {
            _loop.Hold(id);
            _adapters.PauseForBringup(entry, owner);
            await native.OpenForBringupAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Journal(s, "begin", false, $"open failed: {ex.Message}", [], new Dictionary<string, string> { ["comm"] = "not-attempted" });
            await EndCoreAsync(s, "open failed", stepsHeld: true, relinquish: false).ConfigureAwait(false);
            s.Steps.Release();
            throw;
        }

        bool ended;
        lock (_gate)
        {
            ended = s.IsEnded;
            if (!ended)
            {
                s.ExpiresAt = _time.GetUtcNow().AddSeconds(lease);
                s.Timer = _time.CreateTimer(OnExpire, s, TimeSpan.FromSeconds(lease), Timeout.InfiniteTimeSpan);
            }
        }

        s.Steps.Release();
        if (ended)
        {
            throw new InvalidOperationException($"{controllerId}: the bring-up session ended while it was opening.");
        }

        LogBegan(id.Value, owner, lease);
        Journal(s, "begin", true, $"session begun, lease {lease} s", [], new Dictionary<string, string>
        {
            ["holdBlackOnRelease"] = _options.HoldBlackOnRelease ? "true" : "false",
            ["controlVerified"] = native.IsControlVerified(id) ? "true" : "false",
        });
        return Dto(s);
    }

    public Task<ProbeResultDto> RunProbeAsync(string owner, string controllerId, string probe, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(probe);
        return StepAsync(owner, controllerId, probe, (n, id, c) => n.RunProbeAsync(id, probe, c), ct);
    }

    public Task<ProbeResultDto> BeginMarkerAsync(string owner, MarkerDto marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var color = DtoMapper.ParseColor(marker.Rgb);
        if (_options.HoldBlackOnRelease && color != Rgb.Black)
        {
            // addendum §9: refused before any packet; the session continues
            throw new ArgumentException("Hold-black is active: only black (#000000) markers are accepted.", nameof(marker));
        }

        var m = new NativeMarker(marker.Start, marker.Count, color, marker.Channel);
        return StepAsync(owner, marker.ControllerId, "marker", (n, id, c) => n.BeginMarkerAsync(id, m, c), ct);
    }

    public Task<ProbeResultDto> EndMarkerAsync(string owner, string controllerId, CancellationToken ct) =>
        StepAsync(owner, controllerId, "end-marker", (n, id, c) => n.EndMarkerAsync(id, c), ct);

    public Task<BringupSessionDto> RenewAsync(string owner, string controllerId)
    {
        var s = Owned(owner, controllerId);
        lock (_gate)
        {
            if (s.IsEnded)
            {
                throw new InvalidOperationException($"{controllerId}: the bring-up session has ended.");
            }

            return Task.FromResult(RenewLocked(s));
        }
    }

    /// <summary>The owner's End: releases and ends; no session → <c>AlreadyEnded</c>; another owner's session → refused.</summary>
    public async Task<BringupEndDto> EndAsync(string owner, string controllerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var id = new ControllerId(controllerId);
        Session? s;
        lock (_gate)
        {
            _sessions.TryGetValue(id, out s);
        }

        if (s is null)
        {
            return new BringupEndDto(controllerId, AlreadyEnded: true);
        }

        if (!string.Equals(s.Owner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{controllerId} is in a bring-up session of another connection.");
        }

        var release = await EndCoreAsync(s, "ended by owner", stepsHeld: false, relinquish: false).ConfigureAwait(false);
        return new BringupEndDto(controllerId, release is null, release);
    }

    /// <summary>The connection <paramref name="owner"/> closed: its sessions end; other owners' sessions are untouched.</summary>
    public async Task DisconnectAsync(string owner)
    {
        Session[] mine;
        lock (_gate)
        {
            mine = _sessions.Values.Where(s => string.Equals(s.Owner, owner, StringComparison.Ordinal)).ToArray();
        }

        foreach (var s in mine)
        {
            await EndCoreAsync(s, "connection closed", stepsHeld: false, relinquish: false).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the sessions of one class (takeover switched off for it); their writers are relinquished, not reopened.</summary>
    public async Task EndClassAsync(DeviceClass cls, string reason)
    {
        Session[] those;
        lock (_gate)
        {
            those = _sessions.Values.Where(s => s.Entry.Descriptor.Class == cls).ToArray();
        }

        foreach (var s in those)
        {
            await EndCoreAsync(s, reason, stepsHeld: false, relinquish: true).ConfigureAwait(false);
        }
    }

    /// <summary>Host stop: every session ends (GPU first, then ENE, then Aura), before the adapters close; nothing is reopened.</summary>
    public async Task EndAllAsync(string reason)
    {
        Session[] all;
        lock (_gate)
        {
            all = _sessions.Values.OrderBy(s => AdapterHost.StopOrder(s.Entry.Descriptor.Class)).ToArray();
        }

        foreach (var s in all)
        {
            await EndCoreAsync(s, reason, stepsHeld: false, relinquish: true).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await EndAllAsync("disposed").ConfigureAwait(false);

    /// <summary>Adds the overnight evidence defaults a result does not set itself (addendum §2).</summary>
    public static IReadOnlyDictionary<string, string> WithEvidenceDefaults(IReadOnlyDictionary<string, string>? fields)
    {
        var f = fields is null ? new Dictionary<string, string>() : new Dictionary<string, string>(fields);
        f.TryAdd("playback", "unobserved");
        f.TryAdd("optical", "pending-owner");
        f.TryAdd("topology", "unknown");
        return f;
    }

    private static ProbeResultDto ToDto(ControllerId id, NativeProbeResult r, bool ended) =>
        new(id.Value, r.Probe, r.Ok, r.Summary,
            r.Rows.Select(x => new ProbeRowDto(x.Step, x.RequestHex, x.Status, x.ResponseHex, Math.Round(x.WallMs, 3), x.Note)).ToArray(),
            WithEvidenceDefaults(r.Fields), ended);

    private BringupSessionDto RenewLocked(Session s)
    {
        s.ExpiresAt = _time.GetUtcNow().AddSeconds(s.LeaseSeconds);
        s.Timer?.Change(TimeSpan.FromSeconds(s.LeaseSeconds), Timeout.InfiniteTimeSpan);
        return Dto(s);
    }

    private BringupSessionDto Dto(Session s) =>
        new(s.Id.Value, s.LeaseSeconds, s.ExpiresAt, s.Native.Probes, _options.HoldBlackOnRelease);

    private ControllerEntry Entry(string controllerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(controllerId);
        var id = new ControllerId(controllerId);
        return _adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id == id)
            ?? throw new ArgumentException($"No discovered controller {controllerId}.", nameof(controllerId));
    }

    private Session Owned(string owner, string controllerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(controllerId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(new ControllerId(controllerId), out var s) || s.IsEnded)
            {
                throw new InvalidOperationException($"{controllerId} has no bring-up session; begin one first.");
            }

            return string.Equals(s.Owner, owner, StringComparison.Ordinal)
                ? s
                : throw new InvalidOperationException($"{controllerId} is in a bring-up session of another connection.");
        }
    }

    private async Task<ProbeResultDto> StepAsync(string owner, string controllerId, string step,
        Func<INativeBringup, ControllerId, CancellationToken, Task<NativeProbeResult>> run, CancellationToken ct)
    {
        var s = Owned(owner, controllerId);
        await s.Steps.WaitAsync(ct).ConfigureAwait(false); // cancelled while waiting: nothing ran, the session is unchanged
        try
        {
            if (s.IsEnded)
            {
                throw new InvalidOperationException($"{controllerId}: the bring-up session has ended.");
            }

            ct.ThrowIfCancellationRequested(); // still before any packet: the session is unchanged

            NativeProbeResult r;
            var verdict = _guard.Check(s.Entry.Descriptor.Class);
            if (verdict.State != GuardState.Clear)
            {
                r = new NativeProbeResult(step, false, $"{verdict.State} by {verdict.Writer}: step not run", [])
                {
                    Fields = new Dictionary<string, string> { ["comm"] = "not-attempted" },
                };
            }
            else
            {
                try
                {
                    r = await run(s.Native, s.Id, ct).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    throw; // a malformed request: nothing was sent, the session continues
                }
                catch (OperationCanceledException)
                {
                    // cancelled inside the step: packets may have been sent, so the session is released with an uncancelled
                    // token and ended (no retry), then the cancellation is passed on
                    Journal(s, step, false, "cancelled during the step", [], new Dictionary<string, string> { ["comm"] = "unknown(cancelled)" });
                    LogStepFailed(s.Id.Value, step, "cancelled");
                    await EndCoreAsync(s, $"{step} cancelled", stepsHeld: true, relinquish: false).ConfigureAwait(false);
                    throw;
                }
#pragma warning disable CA1031 // a failing step is a result: the session is released and ended, nothing retried
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    r = new NativeProbeResult(step, false, $"{ex.GetType().Name}: {ex.Message}", [])
                    {
                        Fields = new Dictionary<string, string> { ["comm"] = "error(exception)" },
                    };
                }
            }

            Journal(s, step, r.Ok, r.Summary, r.Rows, r.Fields);
            if (r.Ok)
            {
                return ToDto(s.Id, r, ended: false);
            }

            LogStepFailed(s.Id.Value, step, r.Summary);
            await EndCoreAsync(s, $"{step} failed", stepsHeld: true, relinquish: false).ConfigureAwait(false);
            return ToDto(s.Id, r, ended: true);
        }
        finally
        {
            s.Steps.Release();
        }
    }

    private void OnExpire(object? state)
    {
        var s = (Session)state!;
        _ = ExpireAsync(s);
    }

    private async Task ExpireAsync(Session s)
    {
        try
        {
            await EndCoreAsync(s, "lease expired", stepsHeld: false, relinquish: false).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a timer callback must not crash the Host; EndCore already logged the release outcome
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogExpireFailed(s.Id.Value, ex);
        }
    }

    /// <summary>
    /// Runs once per session (later calls return null): stops the lease timer, waits for a running step unless the caller
    /// holds it, runs the adapter release (failures are results, never retried), lifts the holds and forgets the session.
    /// The writer is reopened unless <paramref name="relinquish"/> (Host stop, takeover off).
    /// </summary>
    private async Task<ProbeResultDto?> EndCoreAsync(Session s, string reason, bool stepsHeld, bool relinquish)
    {
        lock (_gate)
        {
            if (s.IsEnded)
            {
                return null;
            }

            s.IsEnded = true;
            s.Timer?.Dispose();
            s.Timer = null;
        }

        if (!stepsHeld)
        {
            await s.Steps.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            NativeProbeResult r;
            try
            {
                r = await s.Native.ReleaseAsync(s.Id, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // the outcome is reported; the session ends regardless
            catch (Exception ex)
#pragma warning restore CA1031
            {
                r = new NativeProbeResult("release", false, $"{ex.GetType().Name}: {ex.Message}", [])
                {
                    Fields = new Dictionary<string, string> { ["comm"] = "error(exception)" },
                };
            }

            Journal(s, "release", r.Ok, $"{reason}: {r.Summary}", r.Rows, r.Fields);
            LogEnded(s.Id.Value, s.Owner, reason, r.Fields.TryGetValue("release", out var rel) ? rel : "?");
            return ToDto(s.Id, r, ended: true);
        }
        finally
        {
            await _adapters.ResumeAfterBringupAsync(s.Entry, relinquish, reason).ConfigureAwait(false);
            _loop.ReleaseHold(s.Id);
            lock (_gate)
            {
                if (_sessions.TryGetValue(s.Id, out var cur) && ReferenceEquals(cur, s))
                {
                    _sessions.Remove(s.Id);
                }
            }

            if (!stepsHeld)
            {
                s.Steps.Release();
            }
        }
    }

    private void Journal(Session s, string step, bool ok, string summary, IReadOnlyList<NativeProbeRow> rows, IReadOnlyDictionary<string, string> fields) =>
        _log.Append(new ProbeLogEntry(_time.GetUtcNow(), s.Id.Value, s.Entry.Descriptor.Class.ToString(), s.Owner, step, ok, summary,
            rows.Select(x => new ProbeRowDto(x.Step, x.RequestHex, x.Status, x.ResponseHex, Math.Round(x.WallMs, 3), x.Note)).ToArray(),
            WithEvidenceDefaults(fields)));

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information, Message = "Bring-up session on {Id} begun by {Owner}, lease {Lease} s")]
    private partial void LogBegan(string id, string owner, int lease);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Information, Message = "Bring-up session on {Id} of {Owner} ended ({Reason}); release {Release}")]
    private partial void LogEnded(string id, string owner, string reason, string release);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Warning, Message = "Bring-up step {Step} on {Id} failed: {Summary}; session released and ended")]
    private partial void LogStepFailed(string id, string step, string summary);

    [LoggerMessage(EventId = 2016, Level = LogLevel.Error, Message = "Bring-up lease expiry on {Id} failed")]
    private partial void LogExpireFailed(string id, Exception ex);

    private sealed class Session(ControllerId id, ControllerEntry entry, INativeBringup native, string owner, int leaseSeconds)
    {
        public ControllerId Id { get; } = id;

        public ControllerEntry Entry { get; } = entry;

        public INativeBringup Native { get; } = native;

        public string Owner { get; } = owner;

        public int LeaseSeconds { get; } = leaseSeconds;

        /// <summary>One step at a time; created taken (count 0) so nothing runs before the open completes.</summary>
        public SemaphoreSlim Steps { get; } = new(0, 1);

        public DateTimeOffset ExpiresAt { get; set; }

        public ITimer? Timer { get; set; }

        public bool IsEnded { get; set; }
    }
}
