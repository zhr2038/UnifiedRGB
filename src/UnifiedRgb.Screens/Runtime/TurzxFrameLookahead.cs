using System.Diagnostics;
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
/// Bounded 1-frame async CPU lookahead for <c>TurzxCompositeTelemetry</c>: runs <c>compose + rotate + MFT H.264 encode</c>
/// on its own STA thread using dedicated reusable ping-pong buffers and a dedicated <see cref="MfH264IntraEncoder"/> so
/// CPU work overlaps cleanly with synchronous WinUSB Write+ACK on the worker thread without cross-thread COM/WIC sharing.
/// Stale prefetched frames are invalidated and dropped immediately without waiting.
/// </summary>
internal sealed class TurzxFrameLookahead : IDisposable
{
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromMilliseconds(500);

    private readonly AssetStore _assets;
    private readonly long _cacheBytes;
    private readonly object _gate = new();
    private Thread? _thread;
    private bool _stopping;
    private bool _disposed;
    private long _epoch = 1;
    private LookaheadJob? _pending;
    private LookaheadJob? _inFlight;
    private LookaheadResult? _completed;
    private long _submitted, _completedCount, _consumed, _discarded, _inFlightWaits, _errors;

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

    /// <summary>Invalidates any pending, in-flight or held prefetched result without blocking on stale work.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _epoch++;
            if (_pending is not null) { _pending = null; _discarded++; }
            if (_completed is not null) { _completed = null; _discarded++; }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Submits the next predicted frame for async CPU encode (at most 1 pending slot).</summary>
    public void Submit(LookaheadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (_disposed || _stopping) return;
            EnsureThreadLocked();
            if (job.Epoch < _epoch) return;
            if (_pending is not null) _discarded++;
            if (_completed is not null) { _completed = null; _discarded++; }
            _pending = job;
            _submitted++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Validates live state against any completed or matching in-flight frame. Returns a valid result immediately (or
    /// after bounded completion of a matching in-flight frame) or invalidates and discards stale work with zero wait.
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

            if (_completed is { } ready)
            {
                _completed = null;
                if (ready.Job.Epoch == _epoch && Matches(ready.Job, criteria))
                {
                    _consumed++;
                    return ready;
                }

                _discarded++;
            }

            var pendingMatches = _pending is { } p && p.Epoch == _epoch && Matches(p, criteria);
            var inFlightMatches = _inFlight is { } f && f.Epoch == _epoch && Matches(f, criteria);
            if (!pendingMatches && !inFlightMatches)
            {
                if (_pending is not null || _inFlight is not null)
                {
                    InvalidateLocked();
                }

                return null;
            }

            _inFlightWaits++;
            while (!_disposed && !_stopping && !shouldAbort())
            {
                if (_completed is { } arrived)
                {
                    _completed = null;
                    if (arrived.Job.Epoch == _epoch && Matches(arrived.Job, criteria) && !shouldAbort())
                    {
                        _consumed++;
                        return arrived;
                    }

                    _discarded++;
                    return null;
                }

                var stillRelevant = (_inFlight is { } active && active.Epoch == _epoch && Matches(active, criteria))
                    || (_pending is { } queued && queued.Epoch == _epoch && Matches(queued, criteria));
                if (!stillRelevant)
                {
                    return null;
                }

                Monitor.Wait(_gate, 5);
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
        if (actual.RenderAssignment.TimelineId is not null)
        {
            var wrapped = duration > 0
                && actual.PrevElapsedMs > duration - (actual.PeriodMs * 2.5)
                && actual.ActualElapsedMs < (actual.PeriodMs * 2.5);
            if (actual.ActualElapsedMs < actual.PrevElapsedMs && !wrapped)
            {
                return false;
            }

            if (duration > 0)
            {
                var expMod = ((job.ExpectedElapsedMs % duration) + duration) % duration;
                var actMod = ((actual.ActualElapsedMs % duration) + duration) % duration;
                var diff = Math.Abs(actMod - expMod);
                var modDist = Math.Min(diff, duration - diff);
                if (modDist > tolerance) return false;
            }
            else if (Math.Abs(actual.ActualElapsedMs - job.ExpectedElapsedMs) > tolerance)
            {
                return false;
            }
        }
        else if (actual.ActualElapsedMs < actual.PrevElapsedMs || Math.Abs(actual.ActualElapsedMs - job.ExpectedElapsedMs) > tolerance)
        {
            return false;
        }

        return true;
    }

    private void InvalidateLocked()
    {
        _epoch++;
        if (_pending is not null) { _pending = null; _discarded++; }
        if (_completed is not null) { _completed = null; _discarded++; }
        Monitor.PulseAll(_gate);
    }

    private void EnsureThreadLocked()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "TURZX frame lookahead" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        using var compositor = new ScreenCompositor(_assets, _cacheBytes);
        var composeBuffers = new ScreenComposeBuffers();
        PixelFrame? rotateBuffer = null;
        MfH264IntraEncoder? encoder = null;
        LookaheadEncoderVariant? encoderVariant = null;
        (int Width, int Height)? encoderSize = null;
        try
        {
            while (true)
            {
                LookaheadJob job;
                lock (_gate)
                {
                    while (_pending is null && !_stopping && !_disposed)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping || _disposed)
                    {
                        break;
                    }

                    job = _pending!;
                    _pending = null;
                    if (job.Epoch != _epoch)
                    {
                        _discarded++;
                        continue;
                    }

                    _inFlight = job;
                    Monitor.PulseAll(_gate);
                }

                LookaheadResult? result = null;
                try
                {
                    var composeStart = Stopwatch.GetTimestamp();
                    var composed = compositor.Compose(job.Family, job.RenderAssignment, job.ExpectedElapsedMs,
                        job.TelemetryReading, job.TelemetryEvalUtc, composeBuffers);
                    var composeMs = Stopwatch.GetElapsedTime(composeStart).TotalMilliseconds;
                    if (composed.Warning is null && !IsEpochStale(job.Epoch))
                    {
                        var encodeStart = Stopwatch.GetTimestamp();
                        var rotatedW = job.Variant.InternalRotation is 90 or 270 ? composed.Canvas.Height : composed.Canvas.Width;
                        var rotatedH = job.Variant.InternalRotation is 90 or 270 ? composed.Canvas.Width : composed.Canvas.Height;
                        if (rotateBuffer is null || rotateBuffer.Width != rotatedW || rotateBuffer.Height != rotatedH)
                        {
                            rotateBuffer = new PixelFrame(rotatedW, rotatedH);
                        }

                        PixelOps.RotateInto(composed.Canvas, rotateBuffer, job.Variant.InternalRotation);
                        if (encoder is null || encoderVariant != job.Variant || encoderSize != (rotateBuffer.Width, rotateBuffer.Height))
                        {
                            encoder?.Dispose();
                            encoder = new MfH264IntraEncoder(rotateBuffer.Width, rotateBuffer.Height, fullRange: true,
                                singleSlice: job.Variant.SingleSlice, quality: job.Variant.Quality);
                            encoderVariant = job.Variant;
                            encoderSize = (rotateBuffer.Width, rotateBuffer.Height);
                        }

                        var bytes = encoder.Encode(rotateBuffer);
                        var encodeMs = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                        var note = $"H.264 all-IDR Baseline Q{job.Variant.Quality}, {encoder.LastSliceCount} slices; host/shared-clock-selected picture; "
                            + ScreenWorker.TelemetryNote(job.TelemetryMode);
                        var still = !compositor.IsDynamic(job.RenderAssignment);
                        result = new LookaheadResult(job, new ScreenImage(TurzxVideoProtocol.DataCommand, bytes, note), composeMs, encodeMs, still);
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
                    _inFlight = null;
                    if (!_stopping && !_disposed && result is not null && job.Epoch == _epoch && _pending is null)
                    {
                        _completed = result;
                        _completedCount++;
                    }
                    else if (result is not null)
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
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            InvalidateLocked();
            thread = _thread;
            Monitor.PulseAll(_gate);
        }

        if (thread is not null && Thread.CurrentThread != thread)
        {
            thread.Join(JoinTimeout);
        }
    }
}
