namespace UnifiedRgb.Core.Clock;

/// <summary>The single master clock every device renders from (IMPLEMENTATION_PLAN §1).</summary>
public interface IClock
{
    /// <summary>Seconds since Start of the most recent tick.</summary>
    double Now { get; }

    /// <summary>Index of the most recent tick.</summary>
    long Frame { get; }

    /// <summary>Raised on the clock thread with (t, dt) in seconds.</summary>
    event Action<double, double>? Tick;

    void Start(int fps);

    void Stop();
}

/// <summary>Precise wait primitive. Core ships a portable hybrid; the Host injects a high-resolution timer.</summary>
public interface IPreciseSleeper
{
    /// <summary>Blocks until Stopwatch.GetTimestamp() ≥ deadlineTicks (or returns early never).</summary>
    void SleepUntil(long deadlineTicks);
}

/// <summary>Jitter/overrun statistics for status.json and the Diagnostics page.</summary>
public readonly record struct ClockStats(long Ticks, long Overruns, double JitterP50Ms, double JitterP99Ms, double JitterMaxMs, int Fps);
