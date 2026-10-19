using System.Diagnostics;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <param name="Uploads">UploadedLoop controllers: completed loop uploads (Written counts them too).</param>
/// <param name="UploadsAborted">Uploads dropped while queued because a newer loop arrived.</param>
public readonly record struct WriterStats(
    ControllerId Controller,
    long Written,
    long Errors,
    long Superseded,
    double Fps,
    int MaxFps,
    bool Paused,
    string? LastError,
    long Uploads = 0,
    long UploadsAborted = 0,
    LoopUploadStats? LastUpload = null,
    Devices.Safety.FrameBudgetStats? Budget = null,
    long BudgetSkips = 0);

/// <summary>
/// The only consumer of a controller's mailbox and the only caller of IDeviceAdapter.WriteFrame (Streamed) or
/// ILoopUploadAdapter.UploadLoopAsync (UploadedLoop) for it. Runs on its own thread. Streamed: drains the latest frame at most
/// MaxFps times per second; for an <see cref="IFramePacingSource"/> adapter (the native classes) its FrameBudget is the only
/// pacing authority instead (NATIVE-DYNAMIC-SYNC-FIX §B). UploadedLoop: drains the latest loop whenever one is published (on
/// change, unpaced: the adapter's packet budget paces the wire), ARCHITECTURE-D2 §1.
/// </summary>
public sealed class DeviceWriter : IDisposable
{
    /// <summary>Longest single sleep of a paced wait, so Stop and a pause are seen within it during a long measured interval.</summary>
    public static readonly TimeSpan PacingSlice = TimeSpan.FromMilliseconds(100);

    private readonly Mailbox _mailbox;
    private readonly IDeviceAdapter _adapter;
    private readonly IPreciseSleeper _sleeper;
    private readonly Action<ControllerId, Exception>? _onError;
    private readonly Action<ControllerId, PendingLoop, LoopUploadStats?>? _onLoopUploaded;
    private readonly ILoopUploadAdapter? _loopAdapter;
    private readonly IWholeFrameAdapter? _wholeAdapter;
    private readonly IFramePacingSource? _pacing;
    private readonly long _minIntervalTicks;
    private readonly long[] _writeTimes = new long[256];

    /// <summary>Held for every device write or upload; <see cref="Quiesce"/> takes it to wait out the one in flight.</summary>
    private readonly object _io = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private long _written;
    private long _errors;
    private long _uploads;
    private long _uploadsAborted;
    private string? _lastError;
    private LoopUploadStats? _lastUpload;
    // Single-consumer spill slot: a pause may win after the mailbox was consumed but before the I/O boundary.
    // New publishes stay in the mailbox; UploadLocked's same-zone supersession check always gives them priority.
    private PendingLoop? _deferredLoop;

    public DeviceWriter(
        Mailbox mailbox,
        IDeviceAdapter adapter,
        IPreciseSleeper? sleeper = null,
        Action<ControllerId, Exception>? onError = null,
        Action<ControllerId, PendingLoop, LoopUploadStats?>? onLoopUploaded = null)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _sleeper = sleeper ?? new HybridSleeper();
        _onError = onError;
        _onLoopUploaded = onLoopUploaded;
        if (mailbox.Controller.Capabilities.Delivery == Delivery.UploadedLoop)
        {
            _loopAdapter = adapter as ILoopUploadAdapter
                ?? throw new ArgumentException($"{mailbox.Controller.Id} takes uploaded loops but its adapter is not an ILoopUploadAdapter.", nameof(adapter));
        }

        _wholeAdapter = _loopAdapter is null ? adapter as IWholeFrameAdapter : null;
        _pacing = _loopAdapter is null ? adapter as IFramePacingSource : null;
        MaxFps = Math.Max(1, mailbox.Controller.Capabilities.MaxFps);
        _minIntervalTicks = Stopwatch.Frequency / MaxFps;
    }

    public ControllerId Controller => _mailbox.Controller.Id;

    public int MaxFps { get; }

    public bool UploadsLoops => _loopAdapter is not null;

    /// <summary>
    /// True when the adapter's FrameBudget paces this Streamed writer (<see cref="IFramePacingSource"/>): no MaxFps grid is
    /// kept, and <see cref="MaxFps"/> stays the configured ceiling, not a pacing interval. False for every other adapter.
    /// </summary>
    public bool PacedByAdapter => _pacing is not null;

    /// <summary>Paused writers discard streamed frames, but retain the newest pending uploaded loops without device I/O.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            (_loopAdapter as ILoopRecoveryControl)?.SetRecoveryAllowed(Controller, !value && _running);
        }
    }

    /// <summary>
    /// Pauses and waits for a write already in flight to finish. When it returns no frame or upload reaches the device until
    /// <see cref="Paused"/> is cleared: the pause is re-checked under the write lock, so a frame consumed just before the pause
    /// is dropped (streamed) or retained (uploaded loop) rather than written after it (NATIVE design §6: exclusive ownership).
    /// </summary>
    public void Quiesce()
    {
        Paused = true;
        lock (_io)
        {
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        (_loopAdapter as ILoopRecoveryControl)?.SetRecoveryAllowed(Controller, !_paused);
        _thread = new Thread(Run) { IsBackground = true, Name = $"DeviceWriter {Controller}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        (_loopAdapter as ILoopRecoveryControl)?.SetRecoveryAllowed(Controller, false);
        _thread?.Join();
        _thread = null;
    }

    public void Dispose() => Stop();

    public WriterStats GetStats()
    {
        var now = Stopwatch.GetTimestamp();
        var written = Interlocked.Read(ref _written);
        var n = (int)Math.Min(written, _writeTimes.Length);
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            if (now - Volatile.Read(ref _writeTimes[i]) <= Stopwatch.Frequency)
            {
                count++;
            }
        }

        var budget = (_adapter as IFrameBudgetSource)?.FrameBudget(Controller);
        return _loopAdapter is null
            ? new WriterStats(Controller, written, Interlocked.Read(ref _errors), _mailbox.Superseded, count, MaxFps, _paused, _lastError,
                Budget: budget, BudgetSkips: budget?.Skips ?? 0)
            : new WriterStats(Controller, written, Interlocked.Read(ref _errors), _mailbox.LoopsSuperseded, 0, 0, _paused, _lastError,
                Interlocked.Read(ref _uploads), Interlocked.Read(ref _uploadsAborted), _loopAdapter.LastUpload(Controller) ?? Volatile.Read(ref _lastUpload));
    }

    /// <summary>
    /// Uploads the newest pending loop, if any (UploadedLoop controllers; tests call it directly). Returns false when nothing
    /// was pending or the writer is paused. A pause retains pending content; an upload superseded by a newer loop for the same
    /// zone is counted as aborted, not an error.
    /// </summary>
    public bool DrainLoopOnce()
    {
        if (_loopAdapter is not { } loops || _paused)
        {
            return false;
        }

        var pending = Interlocked.Exchange(ref _deferredLoop, null);
        if (pending is null && !_mailbox.TryConsumeLoop(out pending))
        {
            return false;
        }

        lock (_io)
        {
            if (_paused)
            {
                Volatile.Write(ref _deferredLoop, pending);
                return false;
            }

            UploadLocked(loops, pending);
        }

        return true;
    }

    private void UploadLocked(ILoopUploadAdapter loops, PendingLoop pending)
    {
        try
        {
            while (true)
            {
                if (_paused)
                {
                    Volatile.Write(ref _deferredLoop, pending);
                    return;
                }

                var token = _mailbox.LoopSuperseded;
                if (_mailbox.IsLoopPending(pending.Zone))
                {
                    Interlocked.Increment(ref _uploadsAborted);
                    return;
                }

                try
                {
                    loops.UploadLoopAsync(Controller, pending.Zone, pending.Loop, token).GetAwaiter().GetResult();
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // superseded while queued: a newer loop for this zone wins (checked above); any other loop only retries
                }
            }

            var stats = loops.LastUpload(Controller);
            Volatile.Write(ref _lastUpload, stats);
            Interlocked.Increment(ref _uploads);
            var w = Interlocked.Increment(ref _written);
            Volatile.Write(ref _writeTimes[(int)((w - 1) % _writeTimes.Length)], Stopwatch.GetTimestamp());
            _onLoopUploaded?.Invoke(Controller, pending, stats);
        }
#pragma warning disable CA1031 // a device failure must never kill the writer thread; it is counted and reported
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _errors);
            _lastError = ex.Message;
            _onError?.Invoke(Controller, ex);
        }
    }

    /// <summary>Writes one frame synchronously (used by tests and by the shutdown "hold last frame" path).</summary>
    public bool DrainOnce()
    {
        if (_loopAdapter is not null)
        {
            return DrainLoopOnce(); // UploadedLoop: WriteFrame is never called
        }

        if (!_mailbox.TryConsume(out var frame))
        {
            return false;
        }

        lock (_io)
        {
            if (!_paused)
            {
                WriteLocked(frame);
            }
        }

        return true;
    }

    /// <summary>
    /// One paced step (NATIVE-DYNAMIC-SYNC-FIX §B; the writer thread runs it after each publish, tests call it directly):
    /// waits out the adapter's own FrameBudget delay for this controller, then consumes and writes the newest frame, so a
    /// frame published during the wait supersedes the older one and nothing is queued or replayed. The adapter still admits
    /// strictly; a frame it refuses sends nothing and is not retried. Paused: nothing is awaited or written (the frame is
    /// drained). Returns false when nothing was pending. Only for <see cref="PacedByAdapter"/> writers.
    /// </summary>
    public bool PacedDrainOnce()
    {
        if (_pacing is not { } pacing)
        {
            throw new InvalidOperationException($"{Controller}: the adapter does not pace its frames (IFramePacingSource); use DrainOnce.");
        }

        _ = WaitForPacing(pacing, whileRunning: false);
        return DrainOnce();
    }

    /// <summary>
    /// Sleeps until the adapter reports the controller due: each sleep is the reported remainder (at most
    /// <see cref="PacingSlice"/>), re-read afterwards, so an early wake waits again and the budget is never polled in a spin.
    /// Returns false when Stop was requested meanwhile (<paramref name="whileRunning"/>); a pause ends the wait at once.
    /// </summary>
    private bool WaitForPacing(IFramePacingSource pacing, bool whileRunning)
    {
        while (!_paused)
        {
            if (whileRunning && !_running)
            {
                return false;
            }

            TimeSpan delay;
            try
            {
                delay = pacing.DelayUntilNextFrame(Controller);
            }
#pragma warning disable CA1031 // a failing hint must never kill the writer thread: the adapter's own admission still decides
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Interlocked.Increment(ref _errors);
                _lastError = ex.Message;
                _onError?.Invoke(Controller, ex);
                return true;
            }

            if (delay <= TimeSpan.Zero)
            {
                return true;
            }

            var slice = delay < PacingSlice ? delay : PacingSlice;
            var ticks = Math.Max(1L, (long)Math.Ceiling(slice.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond));
            _sleeper.SleepUntil(Stopwatch.GetTimestamp() + ticks);
        }

        return true;
    }

    private void WriteLocked(Frame frame)
    {
        try
        {
            if (_wholeAdapter is not null)
            {
                // NATIVE §9: one complete frame per call; a frame the adapter's FrameBudget refused is superseded, not written
                if (!_wholeAdapter.WriteFrame(Controller, frame.Zones))
                {
                    return;
                }
            }
            else
            {
                var zones = _mailbox.Controller.Zones;
                for (var z = 0; z < zones.Count; z++)
                {
                    _adapter.WriteFrame(Controller, zones[z].Id, frame.Zones[z]);
                }
            }

            var w = Interlocked.Increment(ref _written);
            Volatile.Write(ref _writeTimes[(int)((w - 1) % _writeTimes.Length)], Stopwatch.GetTimestamp());
        }
#pragma warning disable CA1031 // a device failure must never kill the writer thread; it is counted and reported
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _errors);
            _lastError = ex.Message;
            _onError?.Invoke(Controller, ex);
        }
    }

    /// <remarks>
    /// Writes are paced on a grid of 1/MaxFps that advances by exactly one interval per write, so long-run rate never
    /// exceeds MaxFps, yet a tick arriving slightly early does not push the grid later (which would make an equal-rate
    /// clock slip and supersede frames). After an idle gap the grid re-anchors to the write time minus a quarter interval.
    /// An <see cref="IFramePacingSource"/> adapter keeps no grid: <see cref="RunPaced"/>.
    /// </remarks>
    private void Run()
    {
        if (_loopAdapter is not null)
        {
            RunLoops();
            return;
        }

        if (_pacing is not null)
        {
            RunPaced(_pacing);
            return;
        }

        var next = Stopwatch.GetTimestamp();
        var slack = _minIntervalTicks / 4;
        while (_running)
        {
            if (!_mailbox.Signal.WaitOne(100))
            {
                continue;
            }

            if (Stopwatch.GetTimestamp() < next)
            {
                _sleeper.SleepUntil(next);
            }

            var at = Stopwatch.GetTimestamp();
            if (DrainOnce())
            {
                next = Math.Max(next + _minIntervalTicks, at + _minIntervalTicks - slack);
            }
        }
    }

    /// <summary>
    /// NATIVE-DYNAMIC-SYNC-FIX §B: a second grid next to the adapter's strict FrameBudget woke just before the budget's own
    /// deadline, had the frame refused and still advanced, so alternate slots were lost. Here the budget alone paces: after a
    /// publish the writer waits out its delay, then writes the newest frame (<see cref="PacedDrainOnce"/>).
    /// </summary>
    private void RunPaced(IFramePacingSource pacing)
    {
        while (_running)
        {
            if (!_mailbox.Signal.WaitOne(100))
            {
                continue;
            }

            if (WaitForPacing(pacing, whileRunning: true))
            {
                DrainOnce();
            }
        }
    }

    private void RunLoops()
    {
        while (_running)
        {
            if (!_mailbox.Signal.WaitOne(100) && (_paused || (!_mailbox.HasPendingLoop && Volatile.Read(ref _deferredLoop) is null)))
            {
                continue;
            }

            while (_running && DrainLoopOnce())
            {
            }
        }
    }
}
