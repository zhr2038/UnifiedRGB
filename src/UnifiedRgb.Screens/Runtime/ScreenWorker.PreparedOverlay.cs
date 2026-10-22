using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// The explicit PreparedOverlay lab path (TURZX native trials that set it; default off: no preparer object, thread, request, extra
/// write or per-picture allocation). The worker still renders the overlay layer on its own STA thread; the ONE preparer of this
/// worker (lazily started, kept across native sessions, disposed with a bounded 500 ms join when the worker ends) runs the exact
/// inline pixel operations and PNG rules on that immutable layer, and the worker sends a ready result after the picture's access
/// unit through the same single-packet SendOverlay. Data pictures, pacing, cache, encoder and their bytes are untouched.
/// <para>
/// Structural generation: the LAST OBSERVED (full assignment record, PNG variant, native session, trial) key, updated when observed;
/// a change, a mode flip, a disabled overlay or a stop raises it and poisons every older pending, in-flight and held result. A request
/// is made only when the layer reference or the generation changes or after a stale result (the preparer also deduplicates).
/// The plane flag records whether the device PNG plane may show content this session sent: a disabled overlay clears it at once.
/// </para>
/// <para>
/// Fallback: a failed encode, a preparer that is not running, or no progress within <see cref="TurzxOverlayPreparer.DefaultMaxAge"/>
/// while newer content is outstanding (also an encode that never returns) switches THIS trial to the inline path for good (cleared
/// only by another trial id), re-sending the current overlay inline in the same picture. A static overlay with nothing outstanding
/// never times out. The worker never ends the trial, restarts or replaces the preparer for this.
/// </para>
/// <para>
/// Lifetime: TURZX layers are at most 1920x480 RGBA (3,686,400 B). The renderer returns a new raster whenever its text changes and
/// never writes a returned one, so the preparer may read it while the worker renders the next; the preparer references at most two
/// such layers. Result bytes are taken once, copied into one packet and never reused.
/// </para>
/// </summary>
internal sealed partial class ScreenWorker
{
    /// <summary>Tells the inline block, after a prepared phase, that the plane may show content: never encoded, sent or compared by value.</summary>
    private static readonly PixelFrame DirtyOverlayPlane = new(1, 1);

    private TurzxOverlayPreparer? _overlayPreparer;
    private bool _overlayPreparerStarted;
    private long _overlayGeneration;
    private bool _overlayPreparedMode;
    private bool _overlayPlaneDirty;
    private string? _overlayTrial;
    private string? _overlayFallback;
    private ScreenAssignmentDto? _observedOverlayAssignment;
    private bool _observedOverlaySimplePng;
    private INativeVideoSession? _observedOverlaySession;
    private string? _observedOverlayTrial;
    private PixelFrame? _requestedOverlay;
    private long _requestedOverlayGeneration = -1;
    private bool _overlayRetry;
    private bool _overlayWaiting;
    private long _overlayWaitingSince;
    private long _overlayRequests, _overlayQueuedStamp, _overlayAwaitStamp, _overlaySentStamp, _overlayPreparedSends, _overlayClearSends,
        _overlayStaleTakes, _overlayQueries;
    private long _videoSessionStart;
    private PreparedOverlayState? _preparedOverlayState;
    private OverlayQueueFact? _lastOverlayQueue;

    /// <summary>Published on prepared-overlay events only; combined with the preparer's own snapshot on a diagnostic read.</summary>
    private sealed record PreparedOverlayState(string TrialId, string? FallbackReason, long Generation, long Requests, long LastQueuedStamp,
        long LastSentStamp, long PreparedSends, long ClearSends, long StaleTakes, bool Outstanding, long OutstandingSince);

    /// <summary>The latest measured overlay of one trial in this session; published per measured PNG only.</summary>
    private sealed record OverlayQueueFact(string TrialId, NativeVideoOverlayQueueDto Value);

    private void OnVideoSessionBegan()
    {
        _overlayPlaneDirty = false; // Begin sent its transparent PNG
        _videoSessionStart = _ctx.Time.GetTimestamp();
        _overlayQueries = 0;
        Volatile.Write(ref _lastOverlayQueue, null);
    }

    /// <summary>
    /// After this picture's access unit and layer render. False: the caller's existing inline block decides the overlay (every default
    /// trial, a sticky fallback, a flip back to inline). True: the prepared path did (a ready result, a clear, or nothing new).
    /// </summary>
    private bool PreparedOverlayStep(INativeVideoSession session, PixelFrame? layer, ScreenAssignmentDto assignment, bool simplePng,
        NativeVideoTrialDto? trial)
    {
        var wanted = trial is { PreparedOverlay: true } && session is TurzxVideoSession;
        if (!wanted && !_overlayPreparedMode && _preparedOverlayState is null) return false;
        var trialId = trial?.TrialId;
        if (!string.Equals(trialId, _overlayTrial, StringComparison.Ordinal))
        {
            _overlayTrial = trialId;
            _overlayFallback = null; // sticky for one trial only
            _overlayRequests = _overlayPreparedSends = _overlayClearSends = _overlayStaleTakes = 0;
            Volatile.Write(ref _preparedOverlayState, null);
        }

        if (!wanted || _overlayFallback is not null)
        {
            if (_overlayPreparedMode) LeavePreparedOverlay();
            if (!wanted) Volatile.Write(ref _preparedOverlayState, null); // an inline trial reports no prepared-overlay facts
            else if (_preparedOverlayState is null) PublishPreparedOverlay(); // the sticky fallback stays visible
            return false;
        }

        var changed = false;
        if (!_overlayPreparedMode)
        {
            _overlayPreparedMode = true;
            _overlayPlaneDirty = _lastVideoOverlay is not null; // what the inline path left on the plane
            _observedOverlaySession = null; // a mode flip is a structural change: a fresh exact current overlay follows
            changed = true;
        }

        if (assignment != _observedOverlayAssignment || simplePng != _observedOverlaySimplePng
            || !ReferenceEquals(session, _observedOverlaySession) || !string.Equals(trialId, _observedOverlayTrial, StringComparison.Ordinal))
        {
            _observedOverlayAssignment = assignment;
            _observedOverlaySimplePng = simplePng;
            _observedOverlaySession = session;
            _observedOverlayTrial = trialId;
            NewOverlayGeneration();
            changed = true;
        }

        if (layer is null)
        {
            if (_requestedOverlay is not null || _overlayWaiting)
            {
                NewOverlayGeneration(); // pending non-transparent output can never be sent after the clear
                changed = true;
            }

            _overlayWaiting = false;
            _overlayAwaitStamp = 0;
            if (_overlayPlaneDirty)
            {
                SendVideoOverlay(session, _transparentVideoLayer!, clear: true, prepared: true, trial);
                _overlayPlaneDirty = false;
                _overlayClearSends++;
                changed = true;
            }

            _videoOverlayEncodingNote = null;
            if (changed) PublishPreparedOverlay();
            return true;
        }

        var preparer = _overlayPreparer;
        if (preparer is null)
        {
            preparer = new TurzxOverlayPreparer(_ctx.Time, TurzxOverlayPreparer.DefaultMaxAge, _ctx.PrepareOverlayForTest);
            Volatile.Write(ref _overlayPreparer, preparer);
        }

        if (!_overlayPreparerStarted)
        {
            var started = preparer.Start();
            if (started is not (OverlayStartOutcome.Started or OverlayStartOutcome.AlreadyRunning)) return FailPreparedOverlay("preparer start " + started);
            _overlayPreparerStarted = true;
        }

        if (!ReferenceEquals(layer, _requestedOverlay) || _requestedOverlayGeneration != _overlayGeneration || _overlayRetry)
        {
            _overlayRequests++;
            var outcome = preparer.Request(new OverlayPreparationRequest(_overlayGeneration, layer, assignment, _ctx.Options.TurzxLandscapeRotation, simplePng));
            if (outcome == OverlayRequestOutcome.Queued)
            {
                _overlayAwaitStamp = ++_overlayQueuedStamp; // the preparer's own stamp: this worker is its only caller
                if (!_overlayWaiting)
                {
                    _overlayWaiting = true;
                    _overlayWaitingSince = _ctx.Time.GetTimestamp();
                }
            }
            else if (outcome != OverlayRequestOutcome.Deduplicated)
            {
                return FailPreparedOverlay("request " + outcome);
            }

            _requestedOverlay = layer;
            _requestedOverlayGeneration = _overlayGeneration;
            _overlayRetry = false;
            changed = true;
        }

        switch (preparer.TryTake(_overlayGeneration, out var taken))
        {
            case OverlayTakeOutcome.Ready:
                SendVideoOverlay(session, taken!.Bytes!, clear: false, prepared: true, trial);
                _overlayPlaneDirty = true;
                _overlayPreparedSends++;
                _overlaySentStamp = taken.Stamp;
                _videoOverlayEncodingNote = taken.Note;
                if (taken.Stamp >= _overlayAwaitStamp)
                {
                    _overlayWaiting = false;
                    _overlayAwaitStamp = 0;
                }
                else
                {
                    _overlayWaitingSince = _ctx.Time.GetTimestamp(); // progress; newer content is still outstanding
                }

                changed = true;
                break;
            case OverlayTakeOutcome.Failed:
                return FailPreparedOverlay("encode failed: " + taken!.Error);
            case OverlayTakeOutcome.Stale:
                _overlayRetry = true; // dropped as too old; the current layer is requested again next picture
                _overlayStaleTakes++;
                changed = true;
                break;
            case OverlayTakeOutcome.StaleCaller:
                return FailPreparedOverlay("generation mismatch");
        }

        if (_overlayWaiting && _ctx.Time.GetElapsedTime(_overlayWaitingSince) > TurzxOverlayPreparer.DefaultMaxAge)
            return FailPreparedOverlay("no prepared overlay within 2 s");
        if (changed) PublishPreparedOverlay();
        return true;
    }

    /// <summary>Raises the structural generation: nothing prepared before it can be handed over or sent.</summary>
    private void NewOverlayGeneration()
    {
        _overlayGeneration++;
        _overlayPreparer?.Invalidate(_overlayGeneration);
        _requestedOverlay = null;
        _overlayRetry = false;
    }

    private bool FailPreparedOverlay(string reason)
    {
        _overlayFallback = reason;
        LeavePreparedOverlay();
        PublishPreparedOverlay();
        LogPreparedOverlayFallback(_ctx.Log, _device.Id, _overlayTrial ?? "", reason);
        return false; // the inline block re-sends the current overlay in this same picture
    }

    /// <summary>Back to the inline path: it re-encodes the CURRENT layer (or clears the plane); no older prepared result is sent.</summary>
    private void LeavePreparedOverlay()
    {
        _overlayPreparedMode = false;
        NewOverlayGeneration();
        _overlayWaiting = false;
        _overlayAwaitStamp = 0;
        _observedOverlaySession = null;
        _lastVideoOverlay = _overlayPlaneDirty ? DirtyOverlayPlane : null;
        _lastVideoOverlayAssignment = null;
    }

    /// <summary>StopVideo: a fast invalidation before the session stops; never a join on the sender or blackout path.</summary>
    private void StopPreparedOverlay()
    {
        if (_overlayPreparedMode || _requestedOverlay is not null || _overlayWaiting) NewOverlayGeneration();
        _overlayPreparedMode = false;
        _overlayPlaneDirty = false;
        _overlayWaiting = false;
        _overlayAwaitStamp = 0;
        _observedOverlayAssignment = null;
        _observedOverlaySession = null;
        Volatile.Write(ref _lastOverlayQueue, null);
    }

    /// <summary>Worker exit only: the bounded 500 ms join; an encode that never returns is abandoned (a background thread), not replaced.</summary>
    private void DisposeOverlayPreparer()
    {
        if (_overlayPreparer is not { } preparer) return;
        preparer.Dispose();
        if (preparer.Snapshot() is { JoinTimeouts: > 0 })
            LogOverlayPreparerAbandoned(_ctx.Log, _device.Id);
    }

    /// <summary>
    /// One worker-issued overlay PNG. Only for a trial with MeasureQueueAfterOverlay: the ONE strict queue-status query right after it
    /// (never after Begin's own transparent PNG, a data picture or a picture that sent no PNG). Default: exactly SendOverlay.
    /// </summary>
    private void SendVideoOverlay(INativeVideoSession session, byte[] png, bool clear, bool prepared, NativeVideoTrialDto? trial)
    {
        if (trial is not { MeasureQueueAfterOverlay: true } || session is not TurzxVideoSession turzx)
        {
            session.SendOverlay(png);
            return;
        }

        var before = turzx.LastQueueDepth;
        var units = turzx.AccessUnits;
        var start = turzx.Work;
        turzx.SendOverlay(png);
        var written = turzx.Work;
        var after = turzx.QueryQueueStatus();
        var queried = turzx.Work;
        if (!string.Equals(_lastOverlayQueue?.TrialId, trial.TrialId, StringComparison.Ordinal)) _overlayQueries = 0;
        _overlayQueries++;
        Volatile.Write(ref _lastOverlayQueue, new OverlayQueueFact(trial.TrialId, new(_overlayQueries, units, before, after, clear, prepared,
            png.Length, written.OverlayWrite - start.OverlayWrite, written.OverlayRead - start.OverlayRead,
            queried.ControlWrite + queried.ControlRead - written.ControlWrite - written.ControlRead,
            _ctx.Time.GetElapsedTime(_videoSessionStart).TotalMilliseconds)));
    }

    private void PublishPreparedOverlay() => Volatile.Write(ref _preparedOverlayState, new PreparedOverlayState(_overlayTrial ?? "", _overlayFallback,
        _overlayGeneration, _overlayRequests, _overlayQueuedStamp, _overlaySentStamp, _overlayPreparedSends, _overlayClearSends, _overlayStaleTakes,
        _overlayWaiting, _overlayWaitingSince));

    /// <summary>
    /// The session's facts plus the optional lab facts of the ACTIVE native trial (<paramref name="activeVideoTrialId"/>), composed on a
    /// diagnostic read (any thread); an ended or replaced trial's lab facts are not shown.
    /// </summary>
    private NativeVideoPerformanceDto? NativeVideoFacts(string? activeVideoTrialId)
    {
        var facts = Volatile.Read(ref _videoFacts);
        if (facts is null || activeVideoTrialId is null) return facts;
        var state = Volatile.Read(ref _preparedOverlayState);
        var queue = Volatile.Read(ref _lastOverlayQueue);
        if (!string.Equals(state?.TrialId, activeVideoTrialId, StringComparison.Ordinal)) state = null;
        if (!string.Equals(queue?.TrialId, activeVideoTrialId, StringComparison.Ordinal)) queue = null;
        if (state is null && queue is null) return facts;
        NativeVideoPreparedOverlayDto? prepared = null;
        if (state is not null)
        {
            var p = Volatile.Read(ref _overlayPreparer)?.Snapshot() ?? default;
            prepared = new(state.TrialId, state.FallbackReason is null ? "prepared" : "inline-fallback", state.FallbackReason, state.Generation,
                state.Requests, state.LastQueuedStamp, state.LastSentStamp, state.PreparedSends, state.ClearSends, state.StaleTakes, state.Outstanding,
                state.Outstanding ? _ctx.Time.GetElapsedTime(state.OutstandingSince).TotalMilliseconds : 0, p.Thread.ToString(), p.ThreadsCreated,
                p.Encodes, p.Completed, p.Failed, p.Poisoned, p.Deduplicated, p.NotNewer, p.JoinTimeouts, p.RetainedLayers, p.RetainedLayerBytes,
                p.LastEncodeMs, p.MaxEncodeMs, p.LastAllocatedBytes, p.MaxAllocatedBytes);
        }

        return facts with { PreparedOverlay = prepared, LastOverlayQueue = queue?.Value };
    }

    /// <summary>Test seam: the worker's preparer, null until a PreparedOverlay trial first needs it.</summary>
    internal TurzxOverlayPreparer? OverlayPreparerForTest => Volatile.Read(ref _overlayPreparer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen {Id} prepared overlay (trial {Trial}): {Reason}; inline overlay for the rest of this trial")]
    private static partial void LogPreparedOverlayFallback(ILogger logger, string id, string trial, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen {Id} overlay preparer still encoding after its bounded 500 ms join; abandoned (a background thread, never replaced)")]
    private static partial void LogOverlayPreparerAbandoned(ILogger logger, string id);
}
