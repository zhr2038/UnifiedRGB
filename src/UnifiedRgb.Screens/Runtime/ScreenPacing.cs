namespace UnifiedRgb.Screens.Runtime;

/// <summary>Absolute monotonic slots. Late work skips slots; never catches up with a burst of old frames.</summary>
internal sealed class ScreenPacing
{
    private double _next;
    private double _period;
    private bool _started;

    public void Reset() => _started = false;

    public int Remaining(long now, long frequency, double periodMs)
    {
        if (!_started || _period != periodMs)
        {
            _period = periodMs;
            _next = now;
            _started = true;
        }
        return (int)Math.Clamp(Math.Ceiling((_next - now) * 1000.0 / frequency), 0, 1000);
    }

    public long Completed(long now, long frequency)
    {
        var ticks = _period * frequency / 1000.0;
        _next += ticks;
        if (_next > now) return 0;
        // Leave the newest due slot available immediately when transport itself is slower than the target.
        // Waiting for a future slot here would halve throughput around the hardware's sustainable rate.
        var skipped = (long)Math.Floor((now - _next) / ticks);
        _next += skipped * ticks;
        return skipped;
    }
}
