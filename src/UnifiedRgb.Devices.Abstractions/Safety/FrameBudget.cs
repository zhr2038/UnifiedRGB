using System.Diagnostics;

namespace UnifiedRgb.Devices.Safety;

/// <summary>Complete-frame timing of one <see cref="FrameBudget"/> (NATIVE design §9).</summary>
/// <param name="P50Ms">Median complete-frame wall time over the last <see cref="FrameBudget.Window"/> samples (0 = none).</param>
/// <param name="P99Ms">99th percentile (nearest rank) over the same window.</param>
/// <param name="EffectiveFps">min(ceiling, ⌊1000 / (p99 × 1.5)⌋) once measured, else the configured ceiling.</param>
/// <param name="FpsSource"><see cref="FrameBudget.Measured"/> after a full window, else <see cref="FrameBudget.ConfiguredCeiling"/>.</param>
/// <param name="Skips">Frames refused because the member's previous frame was in flight or too recent (superseded, not queued).</param>
/// <param name="Frames">Complete frames measured since start.</param>
/// <param name="Failed">Frames begun but not completed (an operation threw); never sampled.</param>
/// <param name="Controls">Control sequences run (exempt from gating and from the samples).</param>
/// <param name="OutOfScope">Sends refused because they were neither inside a frame nor inside a control sequence.</param>
public sealed record FrameBudgetStats(
    double P50Ms,
    double P99Ms,
    int EffectiveFps,
    string FpsSource,
    long Skips,
    long Frames,
    int Samples,
    int CeilingFps,
    long Failed = 0,
    long Controls = 0,
    long OutOfScope = 0,
    int OperationsLastFrame = 0);

/// <summary>
/// Complete-frame limiter for the native classes (NATIVE design §9), replacing the per-call RateLimiter there. A frame is a
/// scope that holds every operation of one controller frame (GPU: one NVAPI call; Aura: ⌈N/20⌉ reports; ENE: a compound
/// per LED). A member's frame starts only when its previous frame has completed and the measured interval allows; a refused
/// frame is counted as a skip and simply not sent (the mailbox's latest-wins semantics supply the next one). Control
/// sequences (enter/release, direct on/off, mode select, remap, probes) run in a control scope: exempt from gating and never
/// cut in half. Sends through <see cref="Gate"/> outside both kinds of scope are refused, so no operation bypasses the
/// accounting. Members share one budget when they share a bus (ENE sticks on one SMBus port): a sample is the sum of the
/// members' latest complete-frame times, i.e. the port's cost of one frame of every stick.
/// </summary>
public sealed class FrameBudget
{
    public const int Window = 100;
    public const string Measured = "measured";
    public const string ConfiguredCeiling = "configured ceiling";

    private readonly Func<long> _clock;
    private readonly double _ticksPerMs;
    private readonly object _gate = new();
    private readonly double[] _ring = new double[Window];
    private readonly Dictionary<string, MemberState> _members = new(StringComparer.Ordinal);
    private readonly AsyncLocal<BudgetScope?> _current = new();
    private long _sampled;
    private long _frames;
    private long _skips;
    private long _failed;
    private long _controls;
    private long _outOfScope;
    private int _opsLastFrame;
    private int _effectiveFps;
    private bool _measured;

    /// <param name="ceilingFps">Configured ceiling until measured (GPU 20, Aura 30, ENE 10 per design §9).</param>
    /// <param name="clock">Monotonic tick source (tests inject one); default <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="ticksPerSecond">Ticks per second of <paramref name="clock"/>; default <see cref="Stopwatch.Frequency"/>.</param>
    public FrameBudget(int ceilingFps, Func<long>? clock = null, long ticksPerSecond = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ceilingFps, 1);
        CeilingFps = ceilingFps;
        _clock = clock ?? Stopwatch.GetTimestamp;
        var tps = ticksPerSecond > 0 ? ticksPerSecond : clock is null ? Stopwatch.Frequency : throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _ticksPerMs = tps / 1000.0;
        _effectiveFps = ceilingFps;
    }

    public int CeilingFps { get; }

    /// <summary>Ceiling from a configured per-frame budget in milliseconds: ⌊1000 / ms⌋, at least 1.</summary>
    public static int CeilingFromBudgetMs(int frameBudgetMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameBudgetMs, 1);
        return Math.Max(1, 1000 / frameBudgetMs);
    }

    /// <summary>EffectiveFps = min(ceiling, ⌊1000 / (p99 × 1.5)⌋), at least 1; the ceiling when p99 is not positive.</summary>
    public static int DeriveEffectiveFps(int ceilingFps, double p99Ms)
    {
        if (!(p99Ms > 0) || double.IsInfinity(p99Ms))
        {
            return ceilingFps;
        }

        var fps = (int)Math.Floor(1000.0 / (p99Ms * 1.5));
        return Math.Clamp(fps, 1, ceilingFps);
    }

    /// <summary>
    /// Begins a complete frame for <paramref name="member"/>, or returns null (counted as a skip) when that member's previous
    /// frame is still in flight or started less than one full effective interval (1000 / EffectiveFps ms, the measured value
    /// once it is below the ceiling) ago, so the admitted rate never exceeds EffectiveFps. Call <see cref="BudgetScope.Complete"/>
    /// after the frame's last operation; disposing without it counts the frame as failed and records no sample.
    /// </summary>
    public BudgetScope? TryBeginFrame(string member = "")
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            var now = _clock();
            if (!_members.TryGetValue(member, out var m))
            {
                m = new MemberState();
                _members[member] = m;
            }

            if (m.InFlight)
            {
                _skips++;
                return null;
            }

            if (m.HasStarted)
            {
                // strict complete-frame cap: a full effective interval (measured EffectiveFps once below the ceiling)
                if (now - m.LastStart < IntervalTicks())
                {
                    _skips++;
                    return null;
                }
            }

            m.InFlight = true;
            m.HasStarted = true;
            m.LastStart = now;
            var scope = new BudgetScope(true, member, now, _current.Value, End);
            _current.Value = scope;
            return scope;
        }
    }

    /// <summary>
    /// Side-effect-free pacing hint for <paramref name="member"/> (NATIVE-DYNAMIC-SYNC-FIX §B): the time left until the deadline
    /// <see cref="TryBeginFrame"/> enforces, i.e. the member's last admitted start plus one full effective interval, read from
    /// this budget's own clock and returned as a duration (no timestamp of another time domain is involved). Zero before the
    /// member's first start or once the deadline has passed; a positive remainder is rounded up, never to zero. It creates no
    /// member, counts no skip and changes no statistic. It is a hint, not admission: TryBeginFrame still decides, and still
    /// refuses a member whose previous frame is in flight or whose effective interval grew in the meantime.
    /// </summary>
    public TimeSpan DelayUntilNextFrame(string member = "")
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            if (!_members.TryGetValue(member, out var m) || !m.HasStarted)
            {
                return TimeSpan.Zero;
            }

            // the same comparison as TryBeginFrame: refused while (now - LastStart) < interval, i.e. while remaining > 0
            var remaining = IntervalTicks() - (_clock() - m.LastStart);
            if (!(remaining > 0))
            {
                return TimeSpan.Zero;
            }

            var ticks = Math.Ceiling(remaining / _ticksPerMs * TimeSpan.TicksPerMillisecond);
            return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks(Math.Max(1L, (long)ticks));
        }
    }

    /// <summary>Begins a control sequence: exempt from gating and samples, never refused.</summary>
    public BudgetScope BeginControl()
    {
        lock (_gate)
        {
            _controls++;
        }

        var scope = new BudgetScope(false, string.Empty, 0, _current.Value, End);
        _current.Value = scope;
        return scope;
    }

    /// <summary>Forgets a member (a stick that closed) so it no longer contributes to the shared sample.</summary>
    public void RemoveMember(string member)
    {
        lock (_gate)
        {
            _members.Remove(member);
        }
    }

    /// <summary>A transport that refuses sends outside a frame or control scope of this budget and counts frame operations.</summary>
    public ITransport Gate(ITransport inner) => new GateTransport(inner, this);

    public FrameBudgetStats Stats()
    {
        lock (_gate)
        {
            var n = (int)Math.Min(_sampled, Window);
            double p50 = 0, p99 = 0;
            if (n > 0)
            {
                var sorted = new double[n];
                Array.Copy(_ring, sorted, n);
                Array.Sort(sorted);
                p50 = sorted[Rank(0.50, n)];
                p99 = sorted[Rank(0.99, n)];
            }

            return new FrameBudgetStats(p50, p99, _effectiveFps, _measured ? Measured : ConfiguredCeiling, _skips, _frames, n, CeilingFps,
                _failed, _controls, _outOfScope, _opsLastFrame);
        }
    }

    private static int Rank(double p, int n) => Math.Clamp((int)Math.Ceiling(p * n) - 1, 0, n - 1);

    /// <summary>One full effective interval in clock ticks (callers hold <c>_gate</c>).</summary>
    private double IntervalTicks() => 1000.0 / _effectiveFps * _ticksPerMs;

    private void End(BudgetScope scope, bool completed)
    {
        if (ReferenceEquals(_current.Value, scope))
        {
            _current.Value = scope.Parent;
        }

        if (!scope.IsFrame)
        {
            return;
        }

        lock (_gate)
        {
            if (_members.TryGetValue(scope.Member, out var m))
            {
                m.InFlight = false;
            }

            if (!completed)
            {
                _failed++;
                return;
            }

            var ms = (_clock() - scope.Started) / _ticksPerMs;
            if (m is not null)
            {
                m.LastDurationMs = ms;
            }

            // shared bus: one sample is the cost of one frame of every member on it
            var sample = 0.0;
            foreach (var other in _members.Values)
            {
                sample += other.LastDurationMs;
            }

            _ring[_sampled % Window] = sample;
            _sampled++;
            _frames++;
            _opsLastFrame = scope.Operations;
            if (_sampled >= Window)
            {
                var sorted = (double[])_ring.Clone();
                Array.Sort(sorted);
                _effectiveFps = DeriveEffectiveFps(CeilingFps, sorted[Rank(0.99, Window)]);
                _measured = true;
            }
        }
    }

    /// <summary>
    /// Accounts one native operation to the current scope; throws when there is none. The gate transport calls it for
    /// every send; transports that are not an <see cref="ITransport"/> (the ENE compound bus) call it directly.
    /// </summary>
    public void CountOperation()
    {
        var scope = _current.Value;
        if (scope is null)
        {
            lock (_gate)
            {
                _outOfScope++;
            }

            throw new InvalidOperationException("Native send outside a frame or control sequence refused (FrameBudget).");
        }

        scope.Operations++;
    }

    private sealed class MemberState
    {
        public bool InFlight { get; set; }

        public bool HasStarted { get; set; }

        public long LastStart { get; set; }

        public double LastDurationMs { get; set; }
    }

    private sealed class GateTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly FrameBudget _budget;

        public GateTransport(ITransport inner, FrameBudget budget)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _budget = budget;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            _budget.CountOperation();
            return _inner.SendAsync(frame, ct);
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
    }
}

/// <summary>A frame or control scope of a <see cref="FrameBudget"/>; dispose to end it.</summary>
public sealed class BudgetScope : IDisposable
{
    private readonly Action<BudgetScope, bool> _end;
    private bool _completed;
    private bool _ended;

    internal BudgetScope(bool isFrame, string member, long started, BudgetScope? parent, Action<BudgetScope, bool> end)
    {
        IsFrame = isFrame;
        Member = member;
        Started = started;
        Parent = parent;
        _end = end;
    }

    /// <summary>True for a frame scope, false for a control sequence.</summary>
    public bool IsFrame { get; }

    public string Member { get; }

    /// <summary>Sends made through the budget's gate while this scope was current.</summary>
    public int Operations { get; internal set; }

    internal long Started { get; }

    internal BudgetScope? Parent { get; }

    /// <summary>Frame scopes: marks the frame's operations as all completed; its wall time becomes a sample.</summary>
    public void Complete() => _completed = true;

    public void Dispose()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _end(this, _completed);
    }
}
