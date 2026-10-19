namespace UnifiedRgb.Core.Engine;

/// <summary>
/// STACK-RETURN (design/OPUS-STACKING-WAVE-RETURN-20261018.md): an opt-in tail after a filled loop's final full frame. The
/// full frame is held for <see cref="HoldMs"/>, then a soft travelling feather with two ripples drains it over
/// <see cref="DurationMs"/>, from the source end where the forward moving bar finished back to where it started, ending on
/// exact black (forward frame 0 follows on wrap). Durations are authored physical milliseconds on the existing RF master model
/// (<see cref="RenderLoop.DeviceClockTicksPerSecond"/> ticks/s, no calibration or I/O); each rounds UP to whole raw-tick
/// intervals. Currently admitted only on Vendor Stacking with an explicit <see cref="SpatialPath"/>
/// (<see cref="Assignment.ValidateLoopReturn"/>); null on an assignment = exactly the historical loop.
/// </summary>
public sealed record LoopReturn(double DurationMs = 4000, double HoldMs = 600, int WaveWidthDots = 24, double WaveStrength = 0.25)
{
    public const double MinDurationMs = 1000;
    public const double MaxDurationMs = 20000;
    public const double MaxHoldMs = 2000;
    public const int MinWaveWidthDots = 2;
    public const int MaxWaveWidthDots = 96;
    public const double MaxWaveStrength = 0.5;

    /// <summary>Allocation bound on forward + hold + return frames of one extended loop (the RF header itself allows 65535).</summary>
    public const int MaxExtendedFrames = 8192;

    /// <summary>Throws <see cref="ArgumentException"/> for a non-finite or out-of-range option.</summary>
    public void Validate()
    {
        if (!(DurationMs >= MinDurationMs && DurationMs <= MaxDurationMs))
        {
            throw new ArgumentException($"Loop return duration {DurationMs} ms is outside {MinDurationMs}..{MaxDurationMs} ms.", nameof(DurationMs));
        }

        if (!(HoldMs >= 0 && HoldMs <= MaxHoldMs))
        {
            throw new ArgumentException($"Loop return hold {HoldMs} ms is outside 0..{MaxHoldMs} ms.", nameof(HoldMs));
        }

        if (WaveWidthDots is < MinWaveWidthDots or > MaxWaveWidthDots)
        {
            throw new ArgumentException($"Loop return wave width {WaveWidthDots} dots is outside {MinWaveWidthDots}..{MaxWaveWidthDots}.", nameof(WaveWidthDots));
        }

        if (!(WaveStrength >= 0 && WaveStrength <= MaxWaveStrength))
        {
            throw new ArgumentException($"Loop return wave strength {WaveStrength} is outside 0..{MaxWaveStrength}.", nameof(WaveStrength));
        }
    }

    /// <summary>Hold frames at <paramref name="intervalTicks"/> raw ticks per frame: ⌈HoldMs · 1600 / (1000 · interval)⌉.</summary>
    public int HoldFrames(int intervalTicks) => FramesFor(HoldMs, intervalTicks);

    /// <summary>Drain frames at <paramref name="intervalTicks"/> raw ticks per frame: ⌈DurationMs · 1600 / (1000 · interval)⌉.</summary>
    public int ReturnFrames(int intervalTicks) => FramesFor(DurationMs, intervalTicks);

    /// <summary>
    /// ⌈<paramref name="ms"/> · <see cref="RenderLoop.DeviceClockTicksPerSecond"/> / (1000 · <paramref name="intervalTicks"/>)⌉:
    /// header intervals are raw master ticks, not milliseconds (600 ms at 48 ticks = 20 frames; 4000 ms = 134).
    /// </summary>
    public static int FramesFor(double ms, int intervalTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);
        return checked((int)Math.Ceiling(ms * RenderLoop.DeviceClockTicksPerSecond / (1000.0 * intervalTicks)));
    }

    /// <summary>"hold 600 ms, then 4000 ms reverse-flow drain (wave 24 dots, ripple 0.25)".</summary>
    public string Summary() =>
        $"hold {HoldMs:0.###} ms, then {DurationMs:0.###} ms reverse-flow drain (wave {WaveWidthDots} dots, ripple {WaveStrength:0.###})";
}
