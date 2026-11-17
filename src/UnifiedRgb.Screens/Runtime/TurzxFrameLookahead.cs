using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;

namespace UnifiedRgb.Screens.Runtime;

internal readonly record struct LookaheadEncoderVariant(bool SingleSlice, int Quality, int InternalRotation);

internal sealed record LookaheadJob(
    long Epoch,
    INativeVideoSession Session,
    ScreenFamilyInfo Family,
    ScreenAssignmentDto RenderAssignment,
    string? AssetId,
    int ExpectedFrameIndex,
    long ExpectedElapsedMs,
    double PeriodMs,
    int AssetDurationMs,
    string? ExpectedTimelineSource,
    long TelemetryGeneration,
    PcTelemetryDto? TelemetryReading,
    DateTimeOffset TelemetryEvalUtc,
    bool ExpectedTelemetryStale,
    VideoTelemetry TelemetryMode,
    LookaheadEncoderVariant Variant);

internal sealed record LookaheadMatchCriteria(
    INativeVideoSession Session,
    ScreenAssignmentDto RenderAssignment,
    string? AssetId,
    int ActualFrameIndex,
    long ActualElapsedMs,
    long PrevElapsedMs,
    double PeriodMs,
    int AssetDurationMs,
    string? ActualTimelineSource,
    long TelemetryGeneration,
    bool ActualTelemetryStale,
    VideoTelemetry TelemetryMode,
    LookaheadEncoderVariant Variant);

internal sealed record LookaheadResult(
    LookaheadJob Job,
    ScreenImage Frame,
    double ComposeMs,
    double EncodeMs,
    bool Still);

internal readonly record struct LookaheadStats(
    long Submitted,
    long Completed,
    long Consumed,
    long Discarded,
    long InFlightWaits,
    long Errors);

/// <summary>
/// Bounded depth-4 async CPU pipeline for <c>TurzxCompositeTelemetry</c>:
/// Stage 1 runs ALL <c>ScreenCompositor.Compose</c> (both async lookahead and priority synchronous compose + idle source prefetch)
/// sequentially on one STA thread so <see cref="SequenceFrameReader"/> is single-owned with an 8-frame sliding ring and zero
/// skip-decode ping-pong. Stage 2 fans encoded rotation work out to up to 2 persistent STA <see cref="MfH264IntraEncoder"/> threads,
/// keeping CPU throughput well inside a 16.67 ms (60 FPS) WinUSB budget while matching strictly on <c>ExpectedFrameIndex</c>.
/// </summary>
internal sealed class TurzxFrameLookahead : IDisposable
{
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromMilliseconds(500);
    internal const int MaxPipelineDepth = 4;
    private const int MaxEncoderThreads = 2;
    private const int MaxPooledRotateFrames = 6;
    private const int MaxIdlePrefetch = 2;

    private sealed record SequencedJob(long Seq, LookaheadJob Job);
    private sealed record ComposedWork(long Seq, LookaheadJob Job, PixelFrame RotatedBuffer, double ComposeMs, bool Still);
    private sealed record SequencedResult(long Seq, LookaheadResult Result);

    private sealed class SyncComposeWork(
        ScreenFamilyInfo family,
        ScreenAssignmentDto renderAssignment,
        long elapsedMs,
        PcTelemetryDto? telemetry,
        DateTimeOffset evalUtc,
        int internalRotation)
    {
        public ScreenFamilyInfo Family { get; } = family;
        public ScreenAssignmentDto RenderAssignment { get; } = renderAssignment;
        public long ElapsedMs { get; } = elapsedMs;
        public PcTelemetryDto? Telemetry { get; } = telemetry;
        public DateTimeOffset EvalUtc { get; } = evalUtc;
        public int InternalRotation { get; } = internalRotation;
        public PixelFrame? RotatedBuffer;
        public ComposeResult? WarningResult;
        public double ComposeMs;
        public bool Still;
        public bool Done;
        public bool Cancelled;
        public Exception? Error;
    }

    private readonly AssetStore _assets;
    private readonly long _cacheBytes;
    private readonly object _gate = new();
    private readonly List<SequencedJob> _pendingCompose = new(MaxPipelineDepth);
    private readonly List<ComposedWork> _pendingEncode = new(MaxPipelineDepth);
    private readonly List<SequencedResult> _completed = new(MaxPipelineDepth);
    private readonly Stack<PixelFrame> _freeRotateBuffers = new(MaxPooledRotateFrames);
    private readonly SequencedJob?[] _encoding = new SequencedJob?[MaxEncoderThreads];
    private readonly Thread?[] _encodeThreads = new Thread?[MaxEncoderThreads];
    private readonly int[] _idlePrefetchFrames = new int[MaxIdlePrefetch];
    private Thread? _composeThread;
    private SequencedJob? _composing;
    private SyncComposeWork? _syncCompose;
    private string? _idlePrefetchAssetId;
    private int _idlePrefetchCount;
    private bool _prefetching;
    private bool _clearCompositorCache;
    private bool _stopping;
    private bool _disposed;
    private long _epoch = 1;
    private long _nextSeq = 1;
    private long _submitted, _completedCount, _consumed, _discarded, _inFlightWaits, _errors;
    private long _sequenceSeeks, _sequenceRingHits;

    public TurzxFrameLookahead(AssetStore assets, long cacheBytes)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
        _cacheBytes = cacheBytes;
    }

    public long Epoch
    {
        get { lock (_gate) return _epoch; }
    }

    public LookaheadStats Stats
    {
        get { lock (_gate) return new(_submitted, _completedCount, _consumed, _discarded, _inFlightWaits, _errors); }
    }

    internal (long Seeks, long RingHits) SequenceStats
    {
        get { lock (_gate) return (_sequenceSeeks, _sequenceRingHits); }
    }

    internal bool IsPrefetchSettled
    {
        get { lock (_gate) return !_prefetching && _idlePrefetchCount == 0; }
    }

    /// <summary>Invalidates any pending, in-flight or held prefetched result without blocking on stale work.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            InvalidateLocked();
        }
    }

    /// <summary>Submits a single predicted frame (clears mismatched queued/completed targets).</summary>
    public void Submit(LookaheadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        LookaheadJob[] single = [job];
        Schedule(single, null, default);
    }

    /// <summary>Synchronizes the bounded depth-4 prefetch pipeline with the upcoming target window via strict 1-to-1 slot claiming.</summary>
    public void Schedule(ReadOnlySpan<LookaheadJob> jobs) => Schedule(jobs, null, default);

    /// <summary>
    /// Synchronizes the bounded depth-4 prefetch pipeline with the upcoming target window via strict 1-to-1 slot claiming and
    /// optionally queues idle source-frame warmup past telemetry-refresh boundaries.
    /// </summary>
    public void Schedule(ReadOnlySpan<LookaheadJob> jobs, string? idlePrefetchAssetId, ReadOnlySpan<int> idlePrefetchFrames)
    {
        lock (_gate)
        {
            if (_disposed || _stopping)
            {
                return;
            }

            SetIdlePrefetchLocked(idlePrefetchAssetId, idlePrefetchFrames);
            if (jobs.IsEmpty)
            {
                if (_idlePrefetchCount > 0)
                {
                    EnsureComposeThreadLocked();
                    Monitor.PulseAll(_gate);
                }

                return;
            }

            var count = Math.Min(jobs.Length, MaxPipelineDepth);
            var window = jobs[..count];
            EnsureThreadsLocked(count);

            Span<bool> slotClaimed = stackalloc bool[MaxPipelineDepth];
            var claimed = slotClaimed[..count];

            for (var i = 0; i < _completed.Count;)
            {
                var item = _completed[i].Result.Job;
                if (item.Epoch == _epoch && TryClaimSlot(window, claimed, _epoch, item))
                {
                    i++;
                }
                else
                {
                    _completed.RemoveAt(i);
                    _discarded++;
                }
            }

            for (var i = 0; i < _encoding.Length; i++)
            {
                if (_encoding[i] is { } enc && enc.Job.Epoch == _epoch)
                {
                    _ = TryClaimSlot(window, claimed, _epoch, enc.Job);
                }
            }

            for (var i = 0; i < _pendingEncode.Count;)
            {
                var item = _pendingEncode[i].Job;
                if (item.Epoch == _epoch && TryClaimSlot(window, claimed, _epoch, item))
                {
                    i++;
                }
                else
                {
                    ReleaseRotateBufferLocked(_pendingEncode[i].RotatedBuffer);
                    _pendingEncode.RemoveAt(i);
                    _discarded++;
                }
            }

            if (_composing is { } comp && comp.Job.Epoch == _epoch)
            {
                _ = TryClaimSlot(window, claimed, _epoch, comp.Job);
            }

            for (var i = 0; i < _pendingCompose.Count;)
            {
                var item = _pendingCompose[i].Job;
                if (item.Epoch == _epoch && TryClaimSlot(window, claimed, _epoch, item))
                {
                    i++;
                }
                else
                {
                    _pendingCompose.RemoveAt(i);
                    _discarded++;
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (claimed[i] || window[i].Epoch < _epoch)
                {
                    continue;
                }

                if (_pendingCompose.Count >= MaxPipelineDepth)
                {
                    _pendingCompose.RemoveAt(0);
                    _discarded++;
                }

                _pendingCompose.Add(new SequencedJob(_nextSeq++, window[i]));
                _submitted++;
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Executes a synchronous compose+rotate on the single owner STA compositor thread so no second decoder is ever opened.</summary>
    public bool TryComposeSync(
        ScreenFamilyInfo family,
        ScreenAssignmentDto renderAssignment,
        long elapsedMs,
        PcTelemetryDto? telemetry,
        DateTimeOffset evalUtc,
        int internalRotation,
        out PixelFrame? rotatedBuffer,
        out ComposeResult? warningResult,
        out double composeMs,
        out bool still,
        Func<bool> shouldAbort)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(renderAssignment);
        ArgumentNullException.ThrowIfNull(shouldAbort);

        SyncComposeWork req;
        lock (_gate)
        {
            if (_disposed || _stopping || shouldAbort())
            {
                rotatedBuffer = null;
                warningResult = null;
                composeMs = 0;
                still = false;
                return false;
            }

            EnsureComposeThreadLocked();
            req = new SyncComposeWork(family, renderAssignment, elapsedMs, telemetry, evalUtc, internalRotation);
            _syncCompose = req;
            Monitor.PulseAll(_gate);

            while (!req.Done && !_disposed && !_stopping && !shouldAbort())
            {
                Monitor.Wait(_gate, 5);
            }

            if (!req.Done)
            {
                req.Cancelled = true;
                if (ReferenceEquals(_syncCompose, req))
                {
                    _syncCompose = null;
                }

                rotatedBuffer = null;
                warningResult = null;
                composeMs = 0;
                still = false;
                return false;
            }
        }

        if (req.Error is not null)
        {
            ExceptionDispatchInfo.Capture(req.Error).Throw();
        }

        rotatedBuffer = req.RotatedBuffer;
        warningResult = req.WarningResult;
        composeMs = req.ComposeMs;
        still = req.Still;
        return true;
    }

    /// <summary>Returns a rotated buffer acquired from <see cref="TryComposeSync"/> back to the lookahead pool.</summary>
    public void ReleaseRotateBuffer(PixelFrame? frame)
    {
        if (frame is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed && !_stopping)
            {
                ReleaseRotateBufferLocked(frame);
            }
        }
    }

    /// <summary>
    /// Validates live state against any completed or matching in-flight frame. Returns a valid result immediately (or
    /// after bounded completion of a matching in-flight frame) or prunes stale entries while preserving valid future work.
    /// </summary>
    public LookaheadResult? TryTake(LookaheadMatchCriteria criteria, Func<bool> shouldAbort)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(shouldAbort);
        lock (_gate)
        {
            if (_disposed || _stopping || shouldAbort())
            {
                InvalidateLocked();
                return null;
            }

            if (TryPopCompletedLocked(criteria) is { } ready)
            {
                return ready;
            }

            if (!HasMatchingInFlightLocked(criteria))
            {
                PruneStaleForCriteriaLocked(criteria);
                return null;
            }

            _inFlightWaits++;
            while (!_disposed && !_stopping && !shouldAbort())
            {
                if (TryPopCompletedLocked(criteria) is { } arrived)
                {
                    return arrived;
                }

                if (!HasMatchingInFlightLocked(criteria))
                {
                    PruneStaleForCriteriaLocked(criteria);
                    return null;
                }

                Monitor.Wait(_gate, 2);
            }

            InvalidateLocked();
            return null;
        }
    }

    internal static bool Matches(LookaheadJob job, LookaheadMatchCriteria actual)
    {
        if (!ReferenceEquals(job.Session, actual.Session)) return false;
        if (job.RenderAssignment != actual.RenderAssignment) return false;
        if (!string.Equals(job.AssetId, actual.AssetId, StringComparison.Ordinal)) return false;
        if (job.ExpectedFrameIndex != actual.ActualFrameIndex) return false;
        if (!string.Equals(job.ExpectedTimelineSource, actual.ActualTimelineSource, StringComparison.Ordinal)) return false;
        if (job.TelemetryGeneration != actual.TelemetryGeneration) return false;
        if (job.ExpectedTelemetryStale != actual.ActualTelemetryStale) return false;
        if (job.TelemetryMode != actual.TelemetryMode) return false;
        if (job.Variant != actual.Variant) return false;

        var tolerance = Math.Max(4.0, actual.PeriodMs * 1.5);
        var duration = actual.AssetDurationMs;
        double elapsedDiff;
        if (actual.RenderAssignment.TimelineId is not null)
        {
            var wrapped = duration > 0
                && actual.PrevElapsedMs > duration - (actual.PeriodMs * 2.5)
                && actual.ActualElapsedMs < (actual.PeriodMs * 2.5);
            if (actual.ActualElapsedMs < actual.PrevElapsedMs && !wrapped)
            {
                return false;
            }

            elapsedDiff = ModularDistanceMs(job.ExpectedElapsedMs, actual.ActualElapsedMs, duration);
        }
        else
        {
            if (actual.ActualElapsedMs < actual.PrevElapsedMs)
            {
                return false;
            }

            elapsedDiff = Math.Abs(actual.ActualElapsedMs - job.ExpectedElapsedMs);
        }

        return elapsedDiff <= tolerance;
    }

    private static double ElapsedDistanceMs(LookaheadJob job, LookaheadMatchCriteria actual) =>
        actual.RenderAssignment.TimelineId is not null
            ? ModularDistanceMs(job.ExpectedElapsedMs, actual.ActualElapsedMs, actual.AssetDurationMs)
            : Math.Abs(actual.ActualElapsedMs - job.ExpectedElapsedMs);

    private static double ModularDistanceMs(long aMs, long bMs, int durationMs)
    {
        if (durationMs <= 0)
        {
            return Math.Abs(aMs - bMs);
        }

        var aMod = ((aMs % durationMs) + durationMs) % durationMs;
        var bMod = ((bMs % durationMs) + durationMs) % durationMs;
        var diff = Math.Abs(aMod - bMod);
        return Math.Min(diff, durationMs - diff);
    }

    private static bool TryClaimSlot(ReadOnlySpan<LookaheadJob> window, Span<bool> claimed, long currentEpoch, LookaheadJob existing)
    {
        for (var j = 0; j < window.Length; j++)
        {
            if (!claimed[j] && window[j].Epoch >= currentEpoch && SamePredictedTarget(existing, window[j]))
            {
                claimed[j] = true;
                return true;
            }
        }

        return false;
    }

    internal static bool SamePredictedTarget(LookaheadJob existing, LookaheadJob desired)
    {
        if (!ReferenceEquals(existing.Session, desired.Session)) return false;
        if (existing.RenderAssignment != desired.RenderAssignment) return false;
        if (!string.Equals(existing.AssetId, desired.AssetId, StringComparison.Ordinal)) return false;
        if (existing.ExpectedFrameIndex != desired.ExpectedFrameIndex) return false;
        if (!string.Equals(existing.ExpectedTimelineSource, desired.ExpectedTimelineSource, StringComparison.Ordinal)) return false;
        if (existing.TelemetryGeneration != desired.TelemetryGeneration) return false;
        if (existing.ExpectedTelemetryStale != desired.ExpectedTelemetryStale) return false;
        if (existing.TelemetryMode != desired.TelemetryMode) return false;
        if (existing.Variant != desired.Variant) return false;

        var tolerance = Math.Max(8.0, desired.PeriodMs * 1.25);
        var elapsedDelta = desired.RenderAssignment.TimelineId is not null
            ? ModularDistanceMs(existing.ExpectedElapsedMs, desired.ExpectedElapsedMs, desired.AssetDurationMs)
            : Math.Abs(existing.ExpectedElapsedMs - desired.ExpectedElapsedMs);
        return elapsedDelta <= tolerance;
    }

    private static bool IsFutureCandidate(LookaheadJob job, LookaheadMatchCriteria actual)
    {
        if (!ReferenceEquals(job.Session, actual.Session)) return false;
        if (job.RenderAssignment != actual.RenderAssignment) return false;
        if (!string.Equals(job.AssetId, actual.AssetId, StringComparison.Ordinal)) return false;
        if (!string.Equals(job.ExpectedTimelineSource, actual.ActualTimelineSource, StringComparison.Ordinal)) return false;
        if (job.TelemetryGeneration != actual.TelemetryGeneration) return false;
        if (job.ExpectedTelemetryStale != actual.ActualTelemetryStale) return false;
        if (job.TelemetryMode != actual.TelemetryMode) return false;
        if (job.Variant != actual.Variant) return false;

        var maxForward = Math.Max(16.0, actual.PeriodMs * 4.5);
        var duration = actual.AssetDurationMs;
        if (actual.RenderAssignment.TimelineId is not null && duration > 0)
        {
            var cap = Math.Min(maxForward, duration * 0.5);
            var forward = (((job.ExpectedElapsedMs - actual.ActualElapsedMs) % duration) + duration) % duration;
            return forward <= cap;
        }

        var linearForward = job.ExpectedElapsedMs - actual.ActualElapsedMs;
        return linearForward >= 0 && linearForward <= maxForward;
    }

    private LookaheadResult? TryPopCompletedLocked(LookaheadMatchCriteria criteria)
    {
        for (var i = _completed.Count - 1; i >= 0; i--)
        {
            if (_completed[i].Result.Job.Epoch != _epoch)
            {
                _completed.RemoveAt(i);
                _discarded++;
            }
        }

        var hitIdx = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _completed.Count; i++)
        {
            var job = _completed[i].Result.Job;
            if (!Matches(job, criteria))
            {
                continue;
            }

            var dist = ElapsedDistanceMs(job, criteria);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                hitIdx = i;
            }
        }

        if (hitIdx < 0)
        {
            return null;
        }

        var chosen = _completed[hitIdx].Result;
        _completed.RemoveAt(hitIdx);
        _consumed++;

        for (var i = _completed.Count - 1; i >= 0; i--)
        {
            if (!IsFutureCandidate(_completed[i].Result.Job, criteria))
            {
                _completed.RemoveAt(i);
                _discarded++;
            }
        }

        return chosen;
    }

    private bool HasMatchingInFlightLocked(LookaheadMatchCriteria criteria)
    {
        for (var i = 0; i < _encoding.Length; i++)
        {
            if (_encoding[i] is { } enc && enc.Job.Epoch == _epoch && Matches(enc.Job, criteria))
            {
                return true;
            }
        }

        for (var i = 0; i < _pendingEncode.Count; i++)
        {
            var pe = _pendingEncode[i].Job;
            if (pe.Epoch == _epoch && Matches(pe, criteria))
            {
                return true;
            }
        }

        if (_composing is { } comp && comp.Job.Epoch == _epoch && Matches(comp.Job, criteria))
        {
            return true;
        }

        for (var i = 0; i < _pendingCompose.Count; i++)
        {
            var pc = _pendingCompose[i].Job;
            if (pc.Epoch == _epoch && Matches(pc, criteria))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyEncodingLocked()
    {
        for (var i = 0; i < _encoding.Length; i++)
        {
            if (_encoding[i] is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void InsertCompletedSortedLocked(SequencedResult entry)
    {
        var idx = _completed.Count;
        for (var i = 0; i < _completed.Count; i++)
        {
            if (entry.Seq < _completed[i].Seq)
            {
                idx = i;
                break;
            }
        }

        _completed.Insert(idx, entry);
        while (_completed.Count > MaxPipelineDepth)
        {
            _completed.RemoveAt(0);
            _discarded++;
        }
    }

    private PixelFrame? PopMatchingRotateBufferLocked(int width, int height)
    {
        while (_freeRotateBuffers.TryPop(out var frame))
        {
            if (frame.Width == width && frame.Height == height)
            {
                return frame;
            }
        }

        return null;
    }

    private void ReleaseRotateBufferLocked(PixelFrame? frame)
    {
        if (frame is not null && _freeRotateBuffers.Count < MaxPooledRotateFrames)
        {
            _freeRotateBuffers.Push(frame);
        }
    }

    private void PruneStaleForCriteriaLocked(LookaheadMatchCriteria criteria)
    {
        for (var i = _completed.Count - 1; i >= 0; i--)
        {
            var job = _completed[i].Result.Job;
            if (job.Epoch != _epoch || (!Matches(job, criteria) && !IsFutureCandidate(job, criteria)))
            {
                _completed.RemoveAt(i);
                _discarded++;
            }
        }

        for (var i = _pendingEncode.Count - 1; i >= 0; i--)
        {
            var job = _pendingEncode[i].Job;
            if (job.Epoch != _epoch || (!Matches(job, criteria) && !IsFutureCandidate(job, criteria)))
            {
                ReleaseRotateBufferLocked(_pendingEncode[i].RotatedBuffer);
                _pendingEncode.RemoveAt(i);
                _discarded++;
            }
        }

        for (var i = _pendingCompose.Count - 1; i >= 0; i--)
        {
            var job = _pendingCompose[i].Job;
            if (job.Epoch != _epoch || (!Matches(job, criteria) && !IsFutureCandidate(job, criteria)))
            {
                _pendingCompose.RemoveAt(i);
                _discarded++;
            }
        }
    }

    private void SetIdlePrefetchLocked(string? assetId, ReadOnlySpan<int> frames)
    {
        if (assetId is null || frames.IsEmpty)
        {
            _idlePrefetchAssetId = null;
            _idlePrefetchCount = 0;
            return;
        }

        var n = Math.Min(frames.Length, MaxIdlePrefetch);
        _idlePrefetchAssetId = assetId;
        _idlePrefetchCount = n;
        for (var i = 0; i < n; i++)
        {
            _idlePrefetchFrames[i] = frames[i];
        }
    }

    private void InvalidateLocked()
    {
        _epoch++;
        _clearCompositorCache = true;
        _idlePrefetchAssetId = null;
        _idlePrefetchCount = 0;
        if (_pendingCompose.Count > 0)
        {
            _discarded += _pendingCompose.Count;
            _pendingCompose.Clear();
        }

        if (_pendingEncode.Count > 0)
        {
            foreach (var item in _pendingEncode)
            {
                ReleaseRotateBufferLocked(item.RotatedBuffer);
            }

            _discarded += _pendingEncode.Count;
            _pendingEncode.Clear();
        }

        if (_completed.Count > 0)
        {
            _discarded += _completed.Count;
            _completed.Clear();
        }

        Monitor.PulseAll(_gate);
    }

    private void EnsureComposeThreadLocked()
    {
        if (_composeThread is null)
        {
            var compose = new Thread(RunCompose) { IsBackground = true, Name = "TURZX lookahead compose" };
            compose.SetApartmentState(ApartmentState.STA);
            _composeThread = compose;
            compose.Start();
        }
    }

    private void EnsureThreadsLocked(int requiredEncoders)
    {
        EnsureComposeThreadLocked();

        var count = Math.Clamp(requiredEncoders, 1, MaxEncoderThreads);
        for (var i = 0; i < count; i++)
        {
            if (_encodeThreads[i] is not null)
            {
                continue;
            }

            var workerIndex = i;
            var encode = new Thread(() => RunEncode(workerIndex)) { IsBackground = true, Name = $"TURZX lookahead encode #{workerIndex}" };
            encode.SetApartmentState(ApartmentState.STA);
            _encodeThreads[workerIndex] = encode;
            encode.Start();
        }
    }

    private void RunCompose()
    {
        using var compositor = new ScreenCompositor(_assets, _cacheBytes);
        var composeBuffers = new ScreenComposeBuffers();
        try
        {
            while (true)
            {
                SyncComposeWork? syncWork = null;
                SequencedJob work = null!;
                string? prefetchAssetId = null;
                var prefetchFrame = -1;
                var clearCache = false;
                lock (_gate)
                {
                    while (!_stopping && !_disposed
                        && _syncCompose is null
                        && !_clearCompositorCache
                        && !(_pendingCompose.Count > 0 && _pendingEncode.Count < MaxPipelineDepth)
                        && !(_pendingCompose.Count == 0 && _idlePrefetchCount > 0))
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping || _disposed)
                    {
                        break;
                    }

                    if (_clearCompositorCache)
                    {
                        clearCache = true;
                        _clearCompositorCache = false;
                    }

                    if (_syncCompose is { } pendingSync)
                    {
                        syncWork = pendingSync;
                        _syncCompose = null;
                    }
                    else if (_pendingCompose.Count > 0 && _pendingEncode.Count < MaxPipelineDepth)
                    {
                        work = _pendingCompose[0];
                        _pendingCompose.RemoveAt(0);
                        if (work.Job.Epoch != _epoch)
                        {
                            _discarded++;
                            Monitor.PulseAll(_gate);
                            continue;
                        }

                        _composing = work;
                        Monitor.PulseAll(_gate);
                    }
                    else if (_pendingCompose.Count == 0 && _idlePrefetchCount > 0 && _idlePrefetchAssetId is { } assetId)
                    {
                        prefetchAssetId = assetId;
                        prefetchFrame = _idlePrefetchFrames[0];
                        if (_idlePrefetchCount > 1)
                        {
                            _idlePrefetchFrames[0] = _idlePrefetchFrames[1];
                        }

                        _idlePrefetchCount--;
                        _prefetching = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (clearCache)
                {
                    compositor.ClearCache();
                }

                if (syncWork is not null)
                {
                    PixelFrame? ownedRot = null;
                    try
                    {
                        if (!syncWork.Cancelled)
                        {
                            var composeStart = Stopwatch.GetTimestamp();
                            var composed = compositor.Compose(syncWork.Family, syncWork.RenderAssignment, syncWork.ElapsedMs,
                                syncWork.Telemetry, syncWork.EvalUtc, composeBuffers);
                            syncWork.ComposeMs = Stopwatch.GetElapsedTime(composeStart).TotalMilliseconds;
                            syncWork.Still = !compositor.IsDynamic(syncWork.RenderAssignment);
                            if (composed.Warning is not null)
                            {
                                syncWork.WarningResult = new ComposeResult(composed.Canvas.Clone(), composed.Warning);
                            }
                            else if (!syncWork.Cancelled)
                            {
                                var rotatedW = syncWork.InternalRotation is 90 or 270 ? composed.Canvas.Height : composed.Canvas.Width;
                                var rotatedH = syncWork.InternalRotation is 90 or 270 ? composed.Canvas.Width : composed.Canvas.Height;
                                lock (_gate)
                                {
                                    ownedRot = PopMatchingRotateBufferLocked(rotatedW, rotatedH);
                                }

                                ownedRot ??= new PixelFrame(rotatedW, rotatedH);
                                PixelOps.RotateInto(composed.Canvas, ownedRot, syncWork.InternalRotation);
                            }
                        }
                    }
#pragma warning disable CA1031 // propagate compose exceptions cleanly to caller without crashing STA compose thread
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        syncWork.Error = ex;
                    }

                    lock (_gate)
                    {
                        _sequenceSeeks = compositor.SequenceSeekCount;
                        _sequenceRingHits = compositor.SequenceRingHits;
                        if (syncWork.Cancelled || syncWork.Error is not null || syncWork.WarningResult is not null)
                        {
                            ReleaseRotateBufferLocked(ownedRot);
                            syncWork.RotatedBuffer = null;
                        }
                        else
                        {
                            syncWork.RotatedBuffer = ownedRot;
                        }

                        syncWork.Done = true;
                        Monitor.PulseAll(_gate);
                    }

                    continue;
                }

                if (prefetchAssetId is not null)
                {
                    try
                    {
                        compositor.PrefetchSequenceFrame(prefetchAssetId, prefetchFrame);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _sequenceSeeks = compositor.SequenceSeekCount;
                            _sequenceRingHits = compositor.SequenceRingHits;
                            _prefetching = false;
                            Monitor.PulseAll(_gate);
                        }
                    }

                    continue;
                }

                PixelFrame? rotateBuf = null;
                double composeMs = 0;
                var still = false;
                var ok = false;
                try
                {
                    var composeStart = Stopwatch.GetTimestamp();
                    var composed = compositor.Compose(work.Job.Family, work.Job.RenderAssignment, work.Job.ExpectedElapsedMs,
                        work.Job.TelemetryReading, work.Job.TelemetryEvalUtc, composeBuffers);
                    composeMs = Stopwatch.GetElapsedTime(composeStart).TotalMilliseconds;
                    if (composed.Warning is null && !IsEpochStale(work.Job.Epoch))
                    {
                        var rotatedW = work.Job.Variant.InternalRotation is 90 or 270 ? composed.Canvas.Height : composed.Canvas.Width;
                        var rotatedH = work.Job.Variant.InternalRotation is 90 or 270 ? composed.Canvas.Width : composed.Canvas.Height;
                        lock (_gate)
                        {
                            rotateBuf = PopMatchingRotateBufferLocked(rotatedW, rotatedH);
                        }

                        rotateBuf ??= new PixelFrame(rotatedW, rotatedH);
                        PixelOps.RotateInto(composed.Canvas, rotateBuf, work.Job.Variant.InternalRotation);
                        still = !compositor.IsDynamic(work.Job.RenderAssignment);
                        ok = true;
                    }
                }
#pragma warning disable CA1031 // lookahead errors must never crash background STA thread; worker falls back synchronously
                catch (Exception)
#pragma warning restore CA1031
                {
                    lock (_gate) _errors++;
                    ok = false;
                }

                lock (_gate)
                {
                    _sequenceSeeks = compositor.SequenceSeekCount;
                    _sequenceRingHits = compositor.SequenceRingHits;
                    _composing = null;
                    if (ok && !_stopping && !_disposed && work.Job.Epoch == _epoch)
                    {
                        if (_pendingEncode.Count >= MaxPipelineDepth)
                        {
                            ReleaseRotateBufferLocked(_pendingEncode[0].RotatedBuffer);
                            _pendingEncode.RemoveAt(0);
                            _discarded++;
                        }

                        _pendingEncode.Add(new ComposedWork(work.Seq, work.Job, rotateBuf!, composeMs, still));
                    }
                    else
                    {
                        ReleaseRotateBufferLocked(rotateBuf);
                        _discarded++;
                    }

                    Monitor.PulseAll(_gate);
                }
            }
        }
        finally
        {
            Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }

    private void RunEncode(int workerIndex)
    {
        MfH264IntraEncoder? encoder = null;
        LookaheadEncoderVariant? encoderVariant = null;
        (int Width, int Height)? encoderSize = null;
        try
        {
            while (true)
            {
                ComposedWork work;
                lock (_gate)
                {
                    while (_pendingEncode.Count == 0 && !_stopping && !_disposed)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping || _disposed)
                    {
                        break;
                    }

                    work = _pendingEncode[0];
                    _pendingEncode.RemoveAt(0);
                    if (work.Job.Epoch != _epoch)
                    {
                        ReleaseRotateBufferLocked(work.RotatedBuffer);
                        _discarded++;
                        Monitor.PulseAll(_gate);
                        continue;
                    }

                    _encoding[workerIndex] = new SequencedJob(work.Seq, work.Job);
                    Monitor.PulseAll(_gate);
                }

                LookaheadResult? result = null;
                try
                {
                    var encodeStart = Stopwatch.GetTimestamp();
                    var w = work.RotatedBuffer.Width;
                    var h = work.RotatedBuffer.Height;
                    if (encoder is null || encoderVariant != work.Job.Variant || encoderSize != (w, h))
                    {
                        encoder?.Dispose();
                        encoder = new MfH264IntraEncoder(w, h, fullRange: true,
                            singleSlice: work.Job.Variant.SingleSlice, quality: work.Job.Variant.Quality);
                        encoderVariant = work.Job.Variant;
                        encoderSize = (w, h);
                    }

                    var bytes = encoder.Encode(work.RotatedBuffer);
                    var encodeMs = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                    if (!IsEpochStale(work.Job.Epoch))
                    {
                        var note = $"H.264 all-IDR Baseline Q{work.Job.Variant.Quality}, {encoder.LastSliceCount} slices; host/shared-clock-selected picture; "
                            + ScreenWorker.TelemetryNote(work.Job.TelemetryMode);
                        result = new LookaheadResult(work.Job, new ScreenImage(TurzxVideoProtocol.DataCommand, bytes, note),
                            work.ComposeMs, encodeMs, work.Still);
                    }
                }
#pragma warning disable CA1031 // lookahead errors must never crash background STA thread; worker falls back synchronously
                catch (Exception)
#pragma warning restore CA1031
                {
                    lock (_gate) _errors++;
                    result = null;
                }

                lock (_gate)
                {
                    _encoding[workerIndex] = null;
                    ReleaseRotateBufferLocked(work.RotatedBuffer);
                    if (!_stopping && !_disposed && result is not null && work.Job.Epoch == _epoch)
                    {
                        InsertCompletedSortedLocked(new SequencedResult(work.Seq, result));
                        _completedCount++;
                    }
                    else
                    {
                        _discarded++;
                    }

                    Monitor.PulseAll(_gate);
                }
            }
        }
        finally
        {
            encoder?.Dispose();
            Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }

    private bool IsEpochStale(long jobEpoch)
    {
        lock (_gate) return _stopping || _disposed || jobEpoch != _epoch;
    }

    public void Dispose()
    {
        Thread? compose;
        Thread?[] encoders;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            InvalidateLocked();
            _freeRotateBuffers.Clear();
            compose = _composeThread;
            encoders = [.. _encodeThreads];
            Monitor.PulseAll(_gate);
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(JoinTimeout.TotalSeconds * Stopwatch.Frequency);
        JoinWithin(compose, deadline);
        foreach (var enc in encoders)
        {
            JoinWithin(enc, deadline);
        }

        static void JoinWithin(Thread? t, long deadlineTicks)
        {
            if (t is null || Thread.CurrentThread == t)
            {
                return;
            }

            var remainingMs = (int)Math.Max(0, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadlineTicks).TotalMilliseconds);
            t.Join(remainingMs);
        }
    }
}
