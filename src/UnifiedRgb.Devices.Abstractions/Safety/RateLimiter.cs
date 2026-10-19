using System.Diagnostics;

namespace UnifiedRgb.Devices.Safety;

/// <summary>Default per-transport rate limits (ARCHITECTURE §3; RF relay per ARCHITECTURE-D2 §4).</summary>
public static class RateLimits
{
    public const double WinUsbFps = 60;

    /// <summary>RF relay: a packet token bucket, refilled at 100 packets/s, burst 256 (one 60-packet upload fits 4×).</summary>
    public const double RfRelayPacketsPerSecond = 100;

    public const double RfRelayPacketBurst = 256;

    public const double NvapiI2cFps = 20;
    public const double OpenRgbSdkFps = 60;
}

/// <summary>
/// Token bucket with fractional carry. Windows timers oversleep by up to ~16 ms, so the bucket keeps
/// <see cref="TimerSlackSeconds"/> worth of extra tokens: the long-run rate stays exact while the worst-case
/// burst is bounded to 1 + rate × slack frames (≤ 2.8 frames at 60 fps).
/// </summary>
public sealed class TokenBucket
{
    public const double TimerSlackSeconds = 0.03;

    private static readonly double TicksPerSecond = Stopwatch.Frequency;
    private readonly object _gate = new();
    private readonly double _ratePerTick;
    private readonly double _capacity;
    private double _tokens;
    private long _lastTicks;

    public TokenBucket(double ratePerSecond)
        : this(ratePerSecond, 1.0 + (ratePerSecond * TimerSlackSeconds))
    {
    }

    public TokenBucket(double ratePerSecond, double capacity)
        : this(ratePerSecond, capacity, startFull: false)
    {
    }

    /// <param name="startFull">Start with <paramref name="capacity"/> tokens (a packet budget's burst) instead of one.</param>
    public TokenBucket(double ratePerSecond, double capacity, bool startFull)
    {
        if (ratePerSecond <= 0 || double.IsNaN(ratePerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1.0);

        RatePerSecond = ratePerSecond;
        _ratePerTick = ratePerSecond / TicksPerSecond;
        _capacity = capacity;
        _tokens = startFull ? capacity : 1.0;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public double RatePerSecond { get; }

    /// <summary>Takes a token if available; otherwise returns the seconds until one is.</summary>
    public bool TryTake(out double waitSeconds)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(_capacity, _tokens + ((now - _lastTicks) * _ratePerTick));
            _lastTicks = now;
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                waitSeconds = 0;
                return true;
            }

            waitSeconds = (1.0 - _tokens) / RatePerSecond;
            return false;
        }
    }

    public bool TryTake() => TryTake(out _);

    public async ValueTask WaitAsync(CancellationToken ct)
    {
        while (!TryTake(out var wait))
        {
            ct.ThrowIfCancellationRequested();
            var ms = (wait * 1000.0) - 2.0;
            if (ms >= 1.0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }
}

/// <summary>ITransport decorator applying a token bucket to every send. Reads are not limited.</summary>
public sealed class RateLimiter : ITransport
{
    private readonly ITransport _inner;

    public RateLimiter(ITransport inner, double framesPerSecond)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Bucket = new TokenBucket(framesPerSecond);
    }

    public TokenBucket Bucket { get; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await Bucket.WaitAsync(ct).ConfigureAwait(false);
        await _inner.SendAsync(frame, ct).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
}
