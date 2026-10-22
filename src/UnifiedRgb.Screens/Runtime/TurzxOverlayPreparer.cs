using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// Everything that decides the overlay PNG bytes: the caller's structural generation, the immutable rendered layer (by reference:
/// the renderer returns a new raster whenever its text changes and never touches a returned one), the assignment's rotation and
/// brightness, the internal landscape rotation and the PNG mode (WIC or filter-NONE repack). Nothing else of the assignment
/// changes the bytes of a given layer.
/// </summary>
internal readonly record struct OverlayPreparationKey(long Generation, PixelFrame Layer, int Rotation, double Brightness,
    int InternalRotation, bool SimplePng);

internal sealed record OverlayPreparationRequest(long Generation, PixelFrame Layer, ScreenAssignmentDto Assignment, int InternalRotation,
    bool SimplePng)
{
    public OverlayPreparationKey Key => new(Generation, Layer, Assignment.Rotation, Assignment.Brightness, InternalRotation, SimplePng);
}

/// <summary>
/// One completed preparation: its generation, content stamp (monotonic per accepted request), the encoder's own fresh bytes (null
/// when <see cref="Error"/> is set), host timestamps (<see cref="TimeProvider"/> ticks) and the measured encode cost on the
/// preparer thread. After a take nothing here is touched again by the preparer; the taker owns <see cref="Bytes"/>.
/// </summary>
internal sealed record OverlayPreparation(long Generation, long Stamp, byte[]? Bytes, string? Note, string? Error, long RequestedAt,
    long CompletedAt, double EncodeMs, long AllocatedBytes);

internal enum OverlayRequestOutcome : byte
{
    Queued,

    /// <summary>Equal to the last REQUESTED key (whether pending, in flight, held or already handed over): nothing new is prepared.</summary>
    Deduplicated,

    /// <summary>Older than the current generation.</summary>
    StaleGeneration,

    /// <summary>No running preparer thread (not started, stopped, abandoned or disposed): the caller keeps today's inline path.</summary>
    NotRunning,
}

internal enum OverlayTakeOutcome : byte
{
    None,
    Ready,

    /// <summary>The encoder threw: <see cref="OverlayPreparation.Error"/>. The same key is not prepared again (no hot retry loop).</summary>
    Failed,

    /// <summary>Requested longer than the maximum age ago: dropped; the same key may be requested again.</summary>
    Stale,

    /// <summary>
    /// The caller's generation is older than the preparer's: nothing is handed over and NOTHING changes (the newer generation's
    /// held result, pending and in-flight requests and dedupe key all stay for its current caller).
    /// </summary>
    StaleCaller,

    /// <summary>Not newer than the last handed-over stamp of its generation: dropped.</summary>
    NotNewer,
}

internal enum OverlayStartOutcome : byte { Started, AlreadyRunning, Blocked, Disposed }

internal enum OverlayStopOutcome : byte
{
    NotRunning,
    Joined,

    /// <summary>The thread did not exit within <see cref="TurzxOverlayPreparer.JoinTimeout"/>: still alive, and no new one may start.</summary>
    Abandoned,
}

internal enum OverlayPreparerThreadState : byte { None, Running, Abandoned, Disposed }

/// <summary>
/// Actual state and limits.
/// <para>
/// RetainedLayers / RetainedLayerBytes: every DISTINCT rendered layer the preparer's fields reference (the in-flight request, the
/// pending request and the dedupe key of the last requested layer), each counted once by reference (layers are referenced, never
/// copied). At most 2: the dedupe key is the pending request's key whenever one is pending. The preparer thread's own frame holds
/// no request or result while it waits. This is NOT total memory: it excludes the held result (<see cref="ResultBytes"/>), the
/// small request/assignment records, everything the encoder touches while it runs (its managed copies and buffers are measured as
/// Allocated; native WIC/WPF memory and objects awaiting collection or finalization are not visible here), a still-running
/// abandoned encode's own references beyond its request, the thread's stack and dispatcher, and everything the caller holds.
/// </para>
/// <para>
/// ResultBytes: the held result. Allocated: managed bytes allocated by the preparer thread during one encode (rotation copies,
/// WIC and repack buffers; native WIC memory is not included). ThreadAlive is the thread's real state
/// (<see cref="System.Threading.Thread.IsAlive"/>: its encode and final dispatcher shutdown have not both returned), not a wait
/// outcome; Start is blocked exactly while it is true. StaleCallers: takes by a caller behind the preparer's generation (no effect).
/// </para>
/// </summary>
internal readonly record struct OverlayPreparerSnapshot(OverlayPreparerThreadState Thread, bool ThreadAlive, bool InFlight, bool Pending,
    bool ResultHeld, int RetainedLayers, long RetainedLayerBytes, int ResultBytes, long Generation, long Requests, long Queued,
    long Deduplicated, long StaleRequests, long NotRunning, long ReplacedPending, long Encodes, long Completed, long Failed, long Poisoned,
    long ReplacedResults, long Handed, long StaleResults, long StaleCallers, long NotNewer, long ThreadsCreated, long JoinTimeouts,
    long BlockedStarts, long LastAllocatedBytes, long MaxAllocatedBytes, double LastEncodeMs, double MaxEncodeMs);

/// <summary>
/// A bounded TURZX overlay preparer for explicitly opted-in lab trials: one dedicated STA thread per owner runs the worker's exact inline
/// overlay operations (<see cref="ScreenWorker.PrepareOverlayPng"/>, or an injected encoder for lifecycle tests) on an immutable
/// layer. Storage is fixed: at most one request in flight, one latest pending request (a newer one replaces it) and one held result
/// (a newer one replaces it); nothing grows. Requests are deduplicated against the last REQUESTED key, which is tracked apart from
/// the last HANDED-OVER (generation, stamp); that key references its layer (counted in the snapshot) until a newer request, a
/// generation change, a stale take, stop or dispose. An unchanged layer is never re-prepared, and a ready result is handed over
/// whenever it is of the current generation, newer than the last handed one and younger than the maximum age; it need not be the
/// newest request, so fast-changing telemetry cannot starve the sender. A generation change, stop or dispose poisons pending,
/// in-flight and held results; a caller still on an older generation changes nothing. Stop waits at most
/// <see cref="JoinTimeout"/>: a thread stuck in WIC is then abandoned (a background thread, never aborted), and this owner refuses
/// to start another until that thread has really exited, so abandoned threads cannot accumulate. A wait timeout is reported as
/// such, not as a released resource (nor as proof that WIC resources were terminated).
/// </summary>
internal sealed class TurzxOverlayPreparer : IDisposable
{
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>The design's freshness bound: a result requested longer ago is dropped at take.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxAge;
    private readonly Func<OverlayPreparationRequest, (byte[] Bytes, string Note)> _encode;
    private Worker? _worker;
    private bool _disposed;
    private long _generation, _stamp, _lastRequestedStamp, _lastHandedGeneration = long.MinValue, _lastHandedStamp;
    private OverlayPreparationKey? _lastRequested;
    private Job? _pending, _current;
    private OverlayPreparation? _result;
    private long _requests, _queued, _deduplicated, _staleRequests, _notRunning, _replacedPending, _encodes, _completed, _failed,
        _poisoned, _replacedResults, _handed, _staleResults, _staleCallers, _notNewer, _threadsCreated, _joinTimeouts,
        _blockedStarts, _lastAllocated, _maxAllocated;
    private double _lastEncodeMs, _maxEncodeMs;

    /// <param name="encode">Test seam; default: the worker's exact inline operations. Must return a fresh array each call.</param>
    public TurzxOverlayPreparer(TimeProvider time, TimeSpan maxAge, Func<OverlayPreparationRequest, (byte[] Bytes, string Note)>? encode = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);
        _time = time;
        _maxAge = maxAge;
        _encode = encode ?? (static r => ScreenWorker.PrepareOverlayPng(r.Layer, r.Assignment, r.InternalRotation, r.SimplePng));
    }

    public OverlayStartOutcome Start()
    {
        lock (_gate)
        {
            if (_disposed) return OverlayStartOutcome.Disposed;
            if (_worker is { } previous)
            {
                if (!previous.Stopping) return OverlayStartOutcome.AlreadyRunning;
                if (previous.Thread.IsAlive) // the same predicate as Snapshot.ThreadAlive; no pumping Join under the lock
                {
                    _blockedStarts++; // an abandoned encode has not returned yet: never a second thread for this owner
                    return OverlayStartOutcome.Blocked;
                }

                _worker = null;
            }

            var worker = new Worker();
            worker.Thread = new Thread(() => Run(worker)) { IsBackground = true, Name = "TURZX overlay preparer" };
            worker.Thread.SetApartmentState(ApartmentState.STA);
            _worker = worker;
            _threadsCreated++;
            worker.Thread.Start();
            return OverlayStartOutcome.Started;
        }
    }

    /// <summary>Clears both slots, poisons the in-flight encode and joins the thread for at most <see cref="JoinTimeout"/>.</summary>
    public OverlayStopOutcome Stop()
    {
        Worker worker;
        lock (_gate)
        {
            if (_worker is null) return OverlayStopOutcome.NotRunning;
            worker = _worker;
            if (!worker.Stopping)
            {
                worker.Stopping = true;
                if (_pending is not null) { _pending = null; _poisoned++; }
                if (_result is not null) { _result = null; _poisoned++; }
                _lastRequested = null;
                Monitor.PulseAll(_gate);
            }
        }

        if (worker.Thread.Join(JoinTimeout))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_worker, worker)) _worker = null;
            }

            return OverlayStopOutcome.Joined;
        }

        lock (_gate) _joinTimeouts++;
        return OverlayStopOutcome.Abandoned;
    }

    public OverlayRequestOutcome Request(OverlayPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Layer);
        ArgumentNullException.ThrowIfNull(request.Assignment);
        var key = request.Key;
        lock (_gate)
        {
            _requests++;
            if (_disposed || _worker is not { Stopping: false })
            {
                _notRunning++;
                return OverlayRequestOutcome.NotRunning;
            }

            if (_lastRequested == key)
            {
                _deduplicated++;
                return OverlayRequestOutcome.Deduplicated;
            }

            if (request.Generation < _generation)
            {
                _staleRequests++;
                return OverlayRequestOutcome.StaleGeneration;
            }

            if (request.Generation > _generation) InvalidateLocked(request.Generation);
            if (_pending is not null) _replacedPending++;
            _pending = new(request, ++_stamp, _time.GetTimestamp());
            _lastRequested = key;
            _lastRequestedStamp = _stamp;
            _queued++;
            Monitor.PulseAll(_gate);
            return OverlayRequestOutcome.Queued;
        }
    }

    /// <summary>Raises the generation: pending, in-flight and held results of older generations can no longer be handed over.</summary>
    public void Invalidate(long generation)
    {
        lock (_gate)
        {
            if (generation > _generation) InvalidateLocked(generation);
        }
    }

    /// <summary>
    /// Hands over the held result if it is of <paramref name="currentGeneration"/>, newer than the last handed-over stamp and not
    /// older than the maximum age. A caller behind the preparer's generation gets <see cref="OverlayTakeOutcome.StaleCaller"/> and
    /// changes nothing; a caller ahead of it first raises the generation (poisoning older work). Otherwise the held slot is emptied
    /// whatever the outcome; on Ready or Failed the caller owns the result.
    /// </summary>
    public OverlayTakeOutcome TryTake(long currentGeneration, out OverlayPreparation? taken)
    {
        taken = null;
        lock (_gate)
        {
            if (currentGeneration < _generation)
            {
                _staleCallers++;
                return OverlayTakeOutcome.StaleCaller;
            }

            if (currentGeneration > _generation) InvalidateLocked(currentGeneration);
            if (_result is not { } result) return OverlayTakeOutcome.None;
            _result = null; // a held result is always of _generation: older ones are poisoned when the generation rises

            if (_time.GetElapsedTime(result.RequestedAt) > _maxAge)
            {
                _staleResults++;
                ForgetRequested(result.Stamp); // the same layer may be requested (and prepared) again
                return OverlayTakeOutcome.Stale;
            }

            if (result.Generation == _lastHandedGeneration && result.Stamp <= _lastHandedStamp)
            {
                _notNewer++;
                return OverlayTakeOutcome.NotNewer;
            }

            _lastHandedGeneration = result.Generation;
            _lastHandedStamp = result.Stamp;
            taken = result;
            if (result.Error is not null) return OverlayTakeOutcome.Failed;
            _handed++;
            return OverlayTakeOutcome.Ready;
        }
    }

    public OverlayPreparerSnapshot Snapshot()
    {
        lock (_gate)
        {
            var alive = _worker?.Thread.IsAlive == true;
            var state = _disposed ? OverlayPreparerThreadState.Disposed
                : _worker is null ? OverlayPreparerThreadState.None
                : !_worker.Stopping ? OverlayPreparerThreadState.Running
                : alive ? OverlayPreparerThreadState.Abandoned : OverlayPreparerThreadState.None;
            PixelFrame? current = _current?.Request.Layer, pending = _pending?.Request.Layer, keyed = _lastRequested?.Layer;
            var layers = 0;
            long layerBytes = 0;
            Count(current, ref layers, ref layerBytes);
            if (!ReferenceEquals(pending, current)) Count(pending, ref layers, ref layerBytes);
            if (!ReferenceEquals(keyed, current) && !ReferenceEquals(keyed, pending)) Count(keyed, ref layers, ref layerBytes);
            return new(state, alive, _current is not null, _pending is not null, _result is not null, layers, layerBytes,
                _result?.Bytes?.Length ?? 0, _generation, _requests, _queued, _deduplicated, _staleRequests, _notRunning, _replacedPending,
                _encodes, _completed, _failed, _poisoned, _replacedResults, _handed, _staleResults, _staleCallers, _notNewer,
                _threadsCreated, _joinTimeouts, _blockedStarts, _lastAllocated, _maxAllocated, _lastEncodeMs, _maxEncodeMs);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    private void Run(Worker worker)
    {
        try
        {
            Job? job;
            Encoded encoded;
            while (true)
            {
                lock (_gate)
                {
                    while (!worker.Stopping && _pending is null) Monitor.Wait(_gate);
                    if (worker.Stopping) return;
                    job = _pending!;
                    _pending = null;
                    _current = job;
                    _encodes++;
                }

                encoded = Encode(job);
                lock (_gate)
                {
                    Complete(worker, job, encoded);

                    // Released under the same lock that clears _current, so the snapshot's layer count never misses a reference held
                    // here, and nothing of this job or result stays reachable from this frame while the thread waits for the next.
                    job = null;
                    encoded = default;
                }
            }
        }
        finally
        {
            StaThread.ShutdownDispatcher(); // releases the dispatcher WIC/WPF attached to this thread, as the existing STA threads do
        }
    }

    /// <summary>Runs the encoder; its temporaries (and any reference to the request) live only in this frame, gone on return.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Encoded Encode(Job job)
    {
        byte[]? bytes = null;
        string? note = null, error = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        try
        {
            (bytes, note) = _encode(job.Request);
        }
#pragma warning disable CA1031 // any encoder failure is reported through the result; an escaping exception would end the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        var encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (error is null && bytes is not { Length: > 0 }) error = "The overlay encoder returned no bytes.";
        return error is null ? new(bytes, note, null, encodeMs, allocated) : new(null, null, error, encodeMs, allocated);
    }

    /// <summary>Publishes or poisons one finished encode. Caller holds the gate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Complete(Worker worker, Job job, in Encoded encoded)
    {
        _current = null;
        _lastAllocated = encoded.AllocatedBytes;
        _maxAllocated = Math.Max(_maxAllocated, encoded.AllocatedBytes);
        _lastEncodeMs = encoded.EncodeMs;
        _maxEncodeMs = Math.Max(_maxEncodeMs, encoded.EncodeMs);
        var generation = job.Request.Generation;
        if (worker.Stopping || generation != _generation)
        {
            _poisoned++;
            return;
        }

        if (_result is not null) _replacedResults++;
        _result = new(generation, job.Stamp, encoded.Bytes, encoded.Note, encoded.Error, job.RequestedAt, _time.GetTimestamp(),
            encoded.EncodeMs, encoded.AllocatedBytes);
        if (encoded.Error is null) _completed++;
        else _failed++;
    }

    private static void Count(PixelFrame? layer, ref int layers, ref long bytes)
    {
        if (layer is null) return;
        layers++;
        bytes += layer.ByteCount;
    }

    private void InvalidateLocked(long generation)
    {
        _generation = generation;
        if (_pending is { } pending && pending.Request.Generation < generation) { _pending = null; _poisoned++; }
        if (_result is { } result && result.Generation < generation) { _result = null; _poisoned++; }
        _lastRequested = null; // the in-flight encode, if any, is discarded when it returns
    }

    private void ForgetRequested(long stamp)
    {
        if (_lastRequestedStamp == stamp) _lastRequested = null;
    }

    private sealed class Worker
    {
        public Thread Thread { get; set; } = null!;

        public bool Stopping { get; set; }
    }

    private sealed record Job(OverlayPreparationRequest Request, long Stamp, long RequestedAt);

    private readonly record struct Encoded(byte[]? Bytes, string? Note, string? Error, double EncodeMs, long AllocatedBytes);
}
