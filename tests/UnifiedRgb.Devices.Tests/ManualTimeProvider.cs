namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> (no external package): time moves only through <see cref="Advance"/>, which fires
/// every timer that falls due, in due order, on the calling thread.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 18, 3, 30, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers that are scheduled to fire (created, not disposed, not stopped).</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(t);
        }

        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset end;
        lock (_gate)
        {
            end = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= end).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _ticks += (end - _now).Ticks;
                    _now = end;
                    return;
                }

                _ticks += (next.Due!.Value - _now).Ticks;
                _now = next.Due.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }

            next.Callback(next.State);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
