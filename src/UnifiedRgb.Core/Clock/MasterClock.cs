using System.Diagnostics;

namespace UnifiedRgb.Core.Clock;

/// <summary>
/// One Stopwatch-driven ticker (30–120 Hz) on a dedicated above-normal-priority thread. Ticks are scheduled on an
/// absolute grid (start + k·period), so sleep error never accumulates (drift-corrected). If a tick is more than a
/// whole period late the missed grid points are skipped and counted as overruns; t always reflects real time.
/// </summary>
public sealed class MasterClock : IClock, IDisposable
{
    private const int JitterWindow = 4096;

    private readonly IPreciseSleeper _sleeper;
    private readonly double[] _jitterMs = new double[JitterWindow];
    private readonly object _statsGate = new();
    private Thread? _thread;
    private volatile bool _running;
    private long _ticks;
    private long _overruns;
    private int _jitterCount;
    private int _fps;
    private double _now;
    private long _frame;

    public MasterClock(IPreciseSleeper? sleeper = null) => _sleeper = sleeper ?? new HybridSleeper();

    public event Action<double, double>? Tick;

    public double Now => Volatile.Read(ref _now);

    public long Frame => Interlocked.Read(ref _frame);

    public bool IsRunning => _running;

    public void Start(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 30);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 120);
        if (_running)
        {
            throw new InvalidOperationException("Clock already running.");
        }

        _fps = fps;
        lock (_statsGate)
        {
            _ticks = 0;
            _overruns = 0;
            _jitterCount = 0;
        }

        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "UnifiedRgb MasterClock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        var t = _thread;
        if (t is not null && t != Thread.CurrentThread)
        {
            t.Join();
        }

        _thread = null;
    }

    public void Dispose() => Stop();

    public ClockStats GetStats()
    {
        lock (_statsGate)
        {
            var n = Math.Min(_jitterCount, JitterWindow);
            if (n == 0)
            {
                return new ClockStats(_ticks, _overruns, 0, 0, 0, _fps);
            }

            var copy = new double[n];
            Array.Copy(_jitterMs, copy, n);
            Array.Sort(copy);
            return new ClockStats(_ticks, _overruns, Percentile(copy, 0.50), Percentile(copy, 0.99), copy[^1], _fps);
        }
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private void Run()
    {
        var freq = (double)Stopwatch.Frequency;
        var periodTicks = Stopwatch.Frequency / (double)_fps;
        var start = Stopwatch.GetTimestamp();
        long k = 0;
        var lastT = 0.0;
        while (_running)
        {
            var deadline = start + (long)Math.Round(k * periodTicks);
            _sleeper.SleepUntil(deadline);
            var now = Stopwatch.GetTimestamp();
            var lateTicks = now - deadline;
            if (lateTicks > periodTicks)
            {
                var skip = (long)(lateTicks / periodTicks);
                k += skip;
                lock (_statsGate)
                {
                    _overruns += skip;
                }
            }

            var t = (now - start) / freq;
            var dt = k == 0 ? 0 : t - lastT;
            lastT = t;
            Volatile.Write(ref _now, t);
            Interlocked.Exchange(ref _frame, k);
            lock (_statsGate)
            {
                _jitterMs[_jitterCount % JitterWindow] = (now - (start + (long)Math.Round(k * periodTicks))) * 1000.0 / freq;
                _jitterCount++;
                _ticks++;
            }

            Tick?.Invoke(t, dt);
            k++;
        }
    }
}
