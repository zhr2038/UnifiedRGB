using Microsoft.Extensions.Hosting;

namespace UnifiedRgb.Host.Services;

internal sealed record CodexLaunchIntent(string Session, uint Epoch, ulong Cookie, byte Profile,
    ushort Sequence, ushort NeutralSequence, DateTimeOffset ReceivedAt, byte Page, byte Flags);

internal readonly record struct CodexActivationResult(string Status, int? HResult = null, uint? ProcessId = null,
    string? Stage = null, string? ExceptionType = null, bool DispatchAttempted = false);

internal interface ICodexWindowActivator
{
    Task<CodexActivationResult> RequestAsync();
}

/// <summary>One slot (including running), no retry, and a two-second launch rate limit. An uninterruptible broker call
/// keeps the slot until it actually ends; stopping never abandons it to spawn another worker. No raw input/RPC seam.</summary>
internal sealed class CodexLaunchCoordinator : IHostedService, IDisposable
{
    private readonly ICodexWindowActivator _activator;
    private readonly TimeProvider _time;
    private readonly PeripheralConfigurationGate? _gate;
    private readonly Func<Func<Task>, Task> _schedule;
    private readonly object _sync = new();
    private long _revision;
    private bool _stopped;
    private Work? _work;
    private Work? _scheduledWork;
    private Task _scheduleTask = Task.CompletedTask;
    private sealed class Work(long revision)
    {
        public long Revision { get; } = revision;
        public bool Started;
        public bool Checking;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Task> Scheduled { get; } = new();
        public Task Observed { get; set; } = Task.CompletedTask;
    }
    private long? _lastAt;
    private CodexLaunchIntent? _last;
    private string _status = "NotRequested";
    private CodexActivationResult? _lastResult;

    public CodexLaunchCoordinator(ICodexWindowActivator activator, TimeProvider time,
        PeripheralConfigurationGate? gate = null, Func<Func<Task>, Task>? schedule = null)
    {
        _activator = activator; _time = time; _gate = gate; _schedule = schedule ?? (work => Task.Run(work));
        if (gate is not null) gate.RogAcquisitionChanged += Invalidate;
    }

    public string Status { get { lock (_sync) return _status; } }
    internal CodexActivationResult? LastResult { get { lock (_sync) return _lastResult; } }
    internal bool Busy { get { lock (_sync) return _work is not null; } }
    internal Task Completion { get { lock (_sync) return _work?.Done.Task ?? Task.CompletedTask; } }
    internal Task SchedulerCompletion { get { lock (_sync) return _scheduleTask; } }

    internal bool Submit(CodexLaunchIntent intent, Func<bool> stillAdmitted)
    {
        long revision;
        lock (_sync) revision = _revision;
        // Actor admission can lock the actor. Never call it while holding the coordinator lock:
        // retirement takes actor -> coordinator, so the reverse order would deadlock.
        if (!stillAdmitted()) return false;
        Work work;
        lock (_sync)
        {
            if (_stopped || _work is not null || _scheduledWork is not null || revision != _revision || _gate?.RogAcquisitionAllowed == false || intent.Page != 10 || (intent.Flags & 0x40) == 0
                || intent.Cookie == 0 || intent.NeutralSequence == 0 || intent.Sequence != intent.NeutralSequence + 1
                || !Fresh(intent)) return false;
            if (_last is { } last && last.Session == intent.Session && last.Epoch == intent.Epoch && last.Cookie == intent.Cookie
                && intent.Sequence <= last.Sequence) return false;
            var now = _time.GetTimestamp();
            if (_lastAt is { } at && _time.GetElapsedTime(at, now) < TimeSpan.FromSeconds(2)) return false;
            _last = intent; _lastAt = now; _status = "Queued"; _lastResult = null;
            _work = work = new(_revision);
            _scheduledWork = work;
            _scheduleTask = work.Observed = ObserveScheduleAsync(work);
        }
        try
        {
            var scheduled = _schedule(() => RunAsync(work, intent, stillAdmitted));
            work.Scheduled.TrySetResult(scheduled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { ScheduleFailed(work); work.Scheduled.TrySetResult(Task.CompletedTask); }
        return true;
    }

    private async Task ObserveScheduleAsync(Work work)
    {
        try
        {
            var scheduled = await work.Scheduled.Task.ConfigureAwait(false);
            await scheduled.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ScheduleFailed(work); }
        finally { lock (_sync) if (_scheduledWork == work) _scheduledWork = null; }
    }

    private void ScheduleFailed(Work work)
    {
        lock (_sync)
        {
            if (_work != work) return;
            _status = work.Started ? "Unknown" : "Failed";
            if (!work.Started) Finish(work);
        }
    }

    private async Task RunAsync(Work work, CodexLaunchIntent intent, Func<bool> stillAdmitted)
    {
        var owns = false;
        try
        {
            lock (_sync)
            {
                if (_work != work || work.Checking) return;
                work.Checking = owns = true;
            }
            var admitted = stillAdmitted(); // outside both coordinator and native broker locks
            lock (_sync)
            {
                if (_work != work || work.Started) return;
                if (_stopped || work.Revision != _revision || _gate?.RogAcquisitionAllowed == false || !Fresh(intent) || !admitted)
                { _status = "Cancelled"; Finish(work); return; }
                work.Started = true;
                _lastAt = _time.GetTimestamp();
                _status = "Unknown"; // committed; cancellation cannot undo or retry this call
            }
            CodexActivationDiagnostics.Stage("Coordinator.ActivatorCall");
            var result = await _activator.RequestAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _status = result.Status is "Requested" or "Failed" or "Unsupported" ? result.Status : "Unknown";
                _lastResult = result with { Status = _status };
            }
            CodexActivationDiagnostics.Result(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = CodexActivationDiagnostics.Failure("Coordinator.ActivatorCall", ex, work.Started);
            lock (_sync) if (_work == work) { _status = "Unknown"; _lastResult = failure; }
            CodexActivationDiagnostics.Result(failure);
        }
        finally { lock (_sync) if (owns && _work == work) Finish(work); }
    }

    private void Finish(Work work) { _work = null; work.Done.TrySetResult(); }

    private bool Fresh(CodexLaunchIntent intent)
    {
        var age = _time.GetUtcNow() - intent.ReceivedAt;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(750);
    }

    internal void Invalidate()
    {
        lock (_sync)
        {
            _revision++;
            if (_work is { } work)
            {
                _status = work.Started ? "Unknown" : "Cancelled";
                if (!work.Started) Finish(work);
            }
        }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task completion;
        Task scheduler;
        lock (_sync)
        {
            _stopped = true;
            // Capture both owned lifetimes before invalidation can finish a queued item and clear _work.
            // Its scheduler may still be pending even when there is no committed activation to join.
            completion = _work?.Done.Task ?? Task.CompletedTask;
            scheduler = _scheduleTask;
            Invalidate();
        }
        // Cancellation throws rather than claiming stopped: Busy/Completion still describe the owned call.
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        await scheduler.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose()
    {
        StopAsync(default).GetAwaiter().GetResult();
        if (_gate is not null) _gate.RogAcquisitionChanged -= Invalidate;
    }
}
