using System.Text.Json;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

/// <summary>Host-only normalized intent, not an RPC DTO enum. Center is Toggle; retained Ensure values keep existing callers stable.</summary>
public enum DesiredAction { EnsureOn = 1, EnsureOff = 2, Toggle = 4 }

/// <summary>
/// One press as Root's trusted input layer normalized it. SessionId is opaque: compared ordinally, never parsed. Sequence is the
/// input layer's event sequence, strictly increasing within one context. NeutralArmSequence is that layer's fact that the control
/// was seen at neutral, given as the sequence of that observation (0 = none); it arms this press only when it lies after the
/// previous press of the same context (or the context's floor) and before this one.
/// </summary>
public sealed record NativeMenuCaseIntent(string SessionId, long ContextGeneration, long ProfileGeneration, long Sequence,
    DateTimeOffset Timestamp, DesiredAction Action, long NeutralArmSequence);

/// <summary>One Host connection: its RGB proxy and, when available, the screen proxy of that same connection.</summary>
public sealed record NativeMenuCaseHost(IRgbService Rgb, IScreenService? Screens);

public enum NativeMenuRejection
{
    NoContext, OtherSession, StaleContext, UnknownContext, ProfileMismatch, Replayed, SequenceOutOfRange,
    NoNeutralArm, StaleNeutralArm, Expired, Ambiguous, QueueFull, NoHost, Stopped,
}

/// <summary>Accepted (Outcome set) means acknowledged: the intent runs in order or its outcome says why it did not.</summary>
public sealed record NativeMenuSubmitResult(NativeMenuRejection? Rejection, string Message, Task<NativeMenuCaseOutcome>? Outcome)
{
    public bool Accepted => Outcome is not null;
}

public enum NativeMenuCaseOutcomeState { Applied, Unchanged, Failed, Unknown, NotSent }

public enum NativeMenuNotSentReason { ContextRetired, HostChanged, NoHost, EarlierWriteUnanswered, Stopped }

/// <summary>
/// Applied: the Host answered the intent's one scene write (software acceptance, never optical or physical confirmation).
/// Unchanged: the case already was in the desired state and nothing was sent. Failed: an error answer. Unknown: no answer
/// (deadline, cancellation, lost connection), so the write may have applied; it is never resent. NotSent: no case write was
/// requested. Readback is a fresh observation on the intent's own connection, or null when none was taken. Stale: the intent's
/// context was retired before this outcome; it must not be presented as current feedback.
/// </summary>
public sealed record NativeMenuCaseOutcome(NativeMenuCaseIntent Intent, NativeMenuCaseOutcomeState State, string Message,
    CaseStatusObservation? Readback = null, NativeMenuNotSentReason? NotSentReason = null, bool Stale = false,
    DesiredAction? ResolvedAction = null);

/// <summary>Suppressed: queued intents of the retired context, which never write. InFlight: the one the consumer already holds.</summary>
public sealed record NativeMenuContextChange(bool Applied, string Message, int Suppressed = 0, NativeMenuCaseIntent? InFlight = null);

/// <summary>
/// Joined: the consumer has stopped. UnansweredWrite: a backend call ignored cancellation and is still running, so its outcome
/// stays unknown; null when the consumer could not be joined and this cannot be told.
/// </summary>
public sealed record NativeMenuStopResult(bool Joined, bool? UnansweredWrite, string Message);

/// <summary>
/// App-layer bridge from Root's trusted, already validated native-menu input to the existing <see cref="CasePowerCoordinator"/>:
/// EnsureOff runs its Blackout and EnsureOn its Restore, so both are idempotent (up, up writes once) and an intent sends at most
/// one scene. Not registered or wired anywhere; Root's input layer is its only intended caller.
/// Submit admits an intent only for the active context (session, context and profile generation), with a strictly increasing,
/// bounded sequence, a fresh neutral arm, a timestamp within <see cref="MaxIntentAge"/> and a Host connection to bind. Every
/// in-scope press consumes its arm whether admitted or not, so a held, repeated, rejected or overflowing press needs a new neutral.
/// Admitted intents run one at a time in admission order on a single consumer, each on the connection captured at admission and
/// never on a newer one. Invalidate, a superseding Begin and Stop retire the context: its queued intents end NotSent without any
/// backend call and the running one is cancelled. A call that ignores cancellation is not awaited past its deadline, and that
/// connection sends nothing more until the call ends. Case and screens are only read back, never written here.
/// </summary>
public sealed class NativeMenuCaseActionCoordinator : IAsyncDisposable
{
    public const int MaxPending = 4;
    public const long MaxSequenceAdvance = 1024;
    public const int MaxSessionIdLength = 256;
    public static readonly TimeSpan MaxIntentAge = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(5);
    private const int RememberedSessions = 32;

    private readonly Func<NativeMenuCaseHost?> _host;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(new() { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<string> _retiredOrder = new(); // guarded by _sync, with _retired
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly List<Task> _abandoned = []; // guarded by _sync
    private readonly Task _consumer;
    private Context? _context; // guarded by _sync: the latest context, active or ended
    private Pending? _running; // guarded by _sync
    private int _pending; // guarded by _sync: admitted and not yet finished
    private bool _closing; // guarded by _sync
    private Lane? _lane; // consumer only

    /// <param name="host">The current Host connection, or null (or a throw) when there is none. Called once per Submit and once
    /// before each run; it must not block.</param>
    public NativeMenuCaseActionCoordinator(Func<NativeMenuCaseHost?> host, TimeProvider? time = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _time = time ?? TimeProvider.System;
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Starts a context and retires the previous one. Within one session the context generation must increase; a session that was
    /// superseded by another or disconnected is refused. Presses and neutral observations at or below
    /// <paramref name="sequenceFloor"/> belong to before this context.
    /// </summary>
    public NativeMenuContextChange Begin(string sessionId, long contextGeneration, long profileGeneration, long sequenceFloor = 0)
    {
        ValidateSession(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(profileGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceFloor);
        NativeMenuContextChange change;
        Context? retired;
        lock (_sync)
        {
            if (_closing) return new(false, "Stopping; no context was started.");
            if (_retired.Contains(sessionId)) return new(false, "That session was superseded or disconnected; no context was started.");
            if (_context is { } last && last.Session == sessionId && contextGeneration <= last.Generation)
                return new(false, $"Context generation {contextGeneration} is not newer than {last.Generation}; nothing changed.");
            if (_context is { } previous && previous.Session != sessionId) RetireSession(previous.Session);
            (retired, var suppressed, var inFlight) = RetireContext();
            _context = new(sessionId, contextGeneration, profileGeneration, sequenceFloor);
            change = new(true, $"Context {contextGeneration} began; {suppressed} queued request(s) of the previous context will not be sent.",
                suppressed, inFlight);
        }
        Cancel(retired);
        return change;
    }

    /// <summary>
    /// Ends the active context of the session: its queued intents never write and its later outcomes are Stale. With
    /// <paramref name="disconnected"/> the session itself ends and is never accepted again.
    /// </summary>
    public NativeMenuContextChange Invalidate(string sessionId, long contextGeneration, bool disconnected)
    {
        ValidateSession(sessionId);
        NativeMenuContextChange change;
        Context? retired;
        lock (_sync)
        {
            if (disconnected) RetireSession(sessionId);
            // An older generation of the active session no longer matters, unless the whole session ended.
            if (_context is not { Active: true } active || active.Session != sessionId || (!disconnected && contextGeneration < active.Generation))
                return new(false, disconnected ? "Session retired; it had no active context." : "Not the active context; nothing changed.");
            (retired, var suppressed, var inFlight) = RetireContext();
            change = new(true, $"Context {active.Generation} ended; {suppressed} queued request(s) will not be sent.", suppressed, inFlight);
        }
        Cancel(retired);
        return change;
    }

    /// <summary>Admits or rejects one intent synchronously, without any backend call; the outcome of an admitted one follows.</summary>
    public NativeMenuSubmitResult Submit(NativeMenuCaseIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var host = CurrentHost(); // The connection this intent is bound to, read before the checks: no RPC is made here.
        lock (_sync)
        {
            if (_closing) return Reject(NativeMenuRejection.Stopped, "Stopping; nothing was queued.");
            if (_context is not { } context) return Reject(NativeMenuRejection.NoContext, "No native-menu context has begun.");
            if (intent.SessionId != context.Session) return Reject(NativeMenuRejection.OtherSession, "Not the active session.");
            if (intent.ContextGeneration < context.Generation || (intent.ContextGeneration == context.Generation && !context.Active))
                return Reject(NativeMenuRejection.StaleContext, "That context was retired.");
            if (intent.ContextGeneration > context.Generation)
                return Reject(NativeMenuRejection.UnknownContext, "That context has not begun.");
            if (intent.ProfileGeneration != context.Profile)
                return Reject(NativeMenuRejection.ProfileMismatch, "Profile generation does not match the active context.");
            if (intent.Sequence <= context.LastSequence)
                return Reject(NativeMenuRejection.Replayed, $"Sequence {intent.Sequence} was already seen in this context.");
            if (intent.Sequence - context.LastSequence > MaxSequenceAdvance)
                return Reject(NativeMenuRejection.SequenceOutOfRange, $"Sequence {intent.Sequence} jumps more than {MaxSequenceAdvance} ahead.");
            // In scope with a fresh sequence: whatever follows, this press has consumed its arm.
            var previous = context.LastSequence;
            context.LastSequence = intent.Sequence;
            if (intent.Action is not (DesiredAction.EnsureOn or DesiredAction.EnsureOff or DesiredAction.Toggle))
                return Reject(NativeMenuRejection.Ambiguous, $"Action {(int)intent.Action} is not a known case intent.");
            var now = _time.GetUtcNow();
            if (intent.Timestamp > now + MaxClockSkew)
                return Reject(NativeMenuRejection.Ambiguous, "The timestamp lies in the future.");
            if (context.LastTimestamp is { } last && intent.Timestamp < last)
                return Reject(NativeMenuRejection.Ambiguous, "The timestamp precedes the previous press of this context.");
            context.LastTimestamp = intent.Timestamp;
            if (now - intent.Timestamp > MaxIntentAge)
                return Reject(NativeMenuRejection.Expired, $"Older than {MaxIntentAge.TotalSeconds:0.#} s.");
            if (intent.NeutralArmSequence <= 0)
                return Reject(NativeMenuRejection.NoNeutralArm, "No neutral observation armed this press.");
            if (intent.NeutralArmSequence <= previous || intent.NeutralArmSequence >= intent.Sequence)
                return Reject(NativeMenuRejection.StaleNeutralArm, "The neutral observation does not lie between the previous press and this one.");
            if (host is null) return Reject(NativeMenuRejection.NoHost, "No Host connection; nothing was queued.");
            if (_pending == MaxPending)
                return Reject(NativeMenuRejection.QueueFull, $"{MaxPending} requests are already pending; this one was not queued and the next press needs a new neutral.");
            var pending = new Pending(intent, context, host);
            _pending++;
            context.Waiting++;
            _queue.Writer.TryWrite(pending); // Unbounded and only completed under _closing, which was checked under this lock.
            return new(null, "Accepted; the outcome follows on its task.", pending.Completion.Task);
        }
    }

    /// <summary>
    /// Stops for good: Begin and Submit are refused, the context is retired (queued intents never write), the running intent is
    /// cancelled, then the consumer is awaited. When <paramref name="ct"/> ends that wait first, Joined is false: the consumer is
    /// still inside a provider or backend call that ignored cancellation, and once that returns it sends nothing further.
    /// </summary>
    public async Task<NativeMenuStopResult> StopAsync(CancellationToken ct = default)
    {
        Context? retired = null;
        bool first;
        lock (_sync)
        {
            first = !_closing;
            if (first)
            {
                _closing = true;
                (retired, _, _) = RetireContext();
                _queue.Writer.TryComplete();
            }
        }
        if (retired is not null) await retired.Retired.CancelAsync().ConfigureAwait(false);
        if (first) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _consumer.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(false, null, "The consumer has not stopped yet; the outcome of the intent it holds stays unknown, and nothing queued will be sent.");
        }
        bool unanswered;
        lock (_sync) unanswered = _abandoned.Exists(t => !t.IsCompleted);
        return new(true, unanswered, unanswered
            ? "Stopped; a backend call ignored cancellation and is still running, so its outcome stays unknown."
            : "Stopped; no backend call is outstanding.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                NativeMenuCaseOutcome outcome;
                try { outcome = await RunAsync(pending).ConfigureAwait(false); }
#pragma warning disable CA1031 // Every acknowledged intent must report, and nothing may end the consumer.
                catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
                {
                    outcome = Finish(pending, NativeMenuCaseOutcomeState.Unknown, "Internal error; whether a scene was sent is unknown: " + ex.Message);
                }
                finally
                {
                    lock (_sync)
                    {
                        _pending--;
                        if (_running == pending) _running = null;
                    }
                }
                pending.Completion.TrySetResult(outcome);
            }
        }
        finally { _lane?.Coordinator.Dispose(); } // A call still running there finishes under its own token.
    }

    private async Task<NativeMenuCaseOutcome> RunAsync(Pending pending)
    {
        CancellationTokenSource retired;
        lock (_sync)
        {
            pending.Context.Waiting--;
            if (_closing) return NotSent(pending, NativeMenuNotSentReason.Stopped, "Stopped before it ran; nothing was sent.");
            if (!pending.Context.Active) return NotSent(pending, NativeMenuNotSentReason.ContextRetired, "Its context was retired before it ran; nothing was sent.");
            _running = pending;
            retired = pending.Context.Retired;
        }
        // Never transferred: once the provider shows another connection (or none), this intent is not sent at all.
        var current = CurrentHost();
        if (current is null) return NotSent(pending, NativeMenuNotSentReason.NoHost, "The Host connection is gone; nothing was sent.");
        if (!ReferenceEquals(current.Rgb, pending.Host.Rgb))
            return NotSent(pending, NativeMenuNotSentReason.HostChanged, "The Host connection changed after it was accepted; nothing was sent to the new one.");
        var lane = LaneFor(pending.Host);
        if (lane.Unanswered is { IsCompleted: false })
            return NotSent(pending, NativeMenuNotSentReason.EarlierWriteUnanswered,
                "An earlier call on this connection is still unanswered; nothing was sent and nothing is retried.");
        using var deadline = new CancellationTokenSource(WriteTimeout, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, retired.Token, _stop.Token);
        lock (_sync)
        {
            if (_closing) return NotSent(pending, NativeMenuNotSentReason.Stopped, "Stopped before it ran; nothing was sent.");
            if (!pending.Context.Active) return NotSent(pending, NativeMenuNotSentReason.ContextRetired, "Its context was retired before it ran; nothing was sent.");
        }
        NativeMenuCaseOutcomeState state;
        string message;
        DesiredAction? resolvedAction = pending.Intent.Action == DesiredAction.Toggle ? null : pending.Intent.Action;
        Task<CasePowerIntentResult>? call = null;
        try
        {
            call = lane.Coordinator.ExecuteAsync(pending.Intent.Action switch
            {
                DesiredAction.EnsureOff => CasePowerIntent.Blackout,
                DesiredAction.EnsureOn => CasePowerIntent.Restore,
                DesiredAction.Toggle => CasePowerIntent.Toggle,
                _ => throw new InvalidOperationException("Unknown admitted case intent"),
            }, bound.Token);
            // A backend task may ignore cancellation, so the deadline also ends the wait itself.
            var result = await call.WaitAsync(bound.Token).ConfigureAwait(false);
            if (result.ResolvedIntent is { } concrete)
                resolvedAction = concrete == CasePowerIntent.Restore ? DesiredAction.EnsureOn : DesiredAction.EnsureOff;
            (state, message) = (result.Changed ? NativeMenuCaseOutcomeState.Applied : NativeMenuCaseOutcomeState.Unchanged, result.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or StreamJsonRpc.ConnectionLostException)
        {
            var why = deadline.IsCancellationRequested ? $"no answer within {WriteTimeout.TotalSeconds:0} s"
                : retired.IsCancellationRequested ? "its context was retired while it ran"
                : _stop.IsCancellationRequested ? "stopping" : "the call ended without an answer";
            (state, message) = (NativeMenuCaseOutcomeState.Unknown, $"Outcome unknown ({why}): a scene may or may not have been applied; it was not resent.");
        }
#pragma warning disable CA1031 // An error answer is reported as is; the readback, not a retry, shows the current state.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            (state, message) = (NativeMenuCaseOutcomeState.Failed, "The case coordinator reported an error: " + ex.Message);
        }
        if (call is { IsCompleted: false })
        {
            lane.Unanswered = call;
            _ = call.ContinueWith(static t => t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            lock (_sync) _abandoned.Add(call);
        }
        // Feedback of a retired context is not refreshed: no readback once it has ended.
        var readback = IsCurrent(pending.Context) ? await ReadBackAsync(pending.Host, retired.Token).ConfigureAwait(false) : null;
        return Finish(pending, state, message, readback, resolvedAction: resolvedAction);
    }

    // The two-read observation the control panel and the keyboard display use: a scene that changed between the reads is pending.
    private async Task<CaseStatusObservation> ReadBackAsync(NativeMenuCaseHost host, CancellationToken retired)
    {
        using var deadline = new CancellationTokenSource(ReadbackTimeout, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, retired, _stop.Token);
        var ct = bound.Token;
        try
        {
            var before = await host.Rgb.GetSceneAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            var controllers = await host.Rgb.ListControllersAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            ScreenSceneDto? screenScene = null;
            IReadOnlyList<ScreenDeviceDto>? screens = null;
            if (host.Screens is { } service)
            {
                screenScene = await service.GetScreenSceneAsync(ct).WaitAsync(ct).ConfigureAwait(false);
                screens = await service.ListScreensAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            }
            var after = await host.Rgb.GetSceneAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            return SameScene(before, after) ? CaseStatusSummary.Describe(after, controllers, screenScene, screens)
                : new(CaseStatusState.Pending, "场景正在变化", "等待新的后台回读");
        }
#pragma warning disable CA1031 // A failed or late readback is reported as unknown, never as the requested state.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return new(CaseStatusState.Unknown, "状态未读到", "回读失败或超时，结果未确认");
        }
    }

    private static bool SameScene(SceneDto before, SceneDto after) =>
        JsonSerializer.SerializeToUtf8Bytes(before, ContractsJsonContext.Default.SceneDto).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after, ContractsJsonContext.Default.SceneDto));

    // One CasePowerCoordinator per connection; a newer connection replaces it and the old one never receives another intent.
    private Lane LaneFor(NativeMenuCaseHost host)
    {
        if (_lane is { } lane && ReferenceEquals(lane.Rgb, host.Rgb)) return lane;
        _lane?.Coordinator.Dispose();
        return _lane = new(host.Rgb);
    }

    private NativeMenuCaseHost? CurrentHost()
    {
        try { return _host() is { Rgb: not null } host ? host : null; }
#pragma warning disable CA1031 // A provider that throws (not connected) means there is no Host to bind or send to.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
#pragma warning restore CA1031
    }

    // Under _sync. The caller cancels the returned context's token after leaving the lock, so no continuation runs inside it.
    private (Context? Retired, int Suppressed, NativeMenuCaseIntent? InFlight) RetireContext()
    {
        if (_context is not { Active: true } context) return (null, 0, null);
        context.Active = false;
        return (context, context.Waiting, _running?.Context == context ? _running.Intent : null);
    }

    // Under _sync.
    private void RetireSession(string sessionId)
    {
        if (!_retired.Add(sessionId)) return;
        _retiredOrder.Enqueue(sessionId);
        if (_retiredOrder.Count > RememberedSessions) _retired.Remove(_retiredOrder.Dequeue());
    }

    // Callbacks (the running intent's wait) run on the thread pool, never inline on the input layer's thread.
    private static void Cancel(Context? retired)
    {
        if (retired is not null) _ = retired.Retired.CancelAsync();
    }

    private bool IsCurrent(Context context)
    {
        lock (_sync) return context.Active && !_closing;
    }

    private NativeMenuCaseOutcome NotSent(Pending pending, NativeMenuNotSentReason reason, string message) =>
        Finish(pending, NativeMenuCaseOutcomeState.NotSent, message, null, reason);

    private NativeMenuCaseOutcome Finish(Pending pending, NativeMenuCaseOutcomeState state, string message,
        CaseStatusObservation? readback = null, NativeMenuNotSentReason? reason = null, DesiredAction? resolvedAction = null) =>
        new(pending.Intent, state, message, readback, reason, !IsCurrent(pending.Context), resolvedAction);

    private static NativeMenuSubmitResult Reject(NativeMenuRejection reason, string message) => new(reason, message, null);

    private static void ValidateSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sessionId.Length, MaxSessionIdLength, nameof(sessionId));
    }

    private sealed class Context(string session, long generation, long profile, long floor)
    {
        public string Session { get; } = session;
        public long Generation { get; } = generation;
        public long Profile { get; } = profile;
        public CancellationTokenSource Retired { get; } = new();
        public long LastSequence { get; set; } = floor; // guarded by _sync, like the members below
        public DateTimeOffset? LastTimestamp { get; set; }
        public bool Active { get; set; } = true;
        public int Waiting { get; set; }
    }

    private sealed class Pending(NativeMenuCaseIntent intent, Context context, NativeMenuCaseHost host)
    {
        public NativeMenuCaseIntent Intent { get; } = intent;
        public Context Context { get; } = context;
        public NativeMenuCaseHost Host { get; } = host;
        public TaskCompletionSource<NativeMenuCaseOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lane(IRgbService rgb)
    {
        public IRgbService Rgb { get; } = rgb;
        public CasePowerCoordinator Coordinator { get; } = new(rgb);
        public Task? Unanswered { get; set; } // consumer only
    }
}
