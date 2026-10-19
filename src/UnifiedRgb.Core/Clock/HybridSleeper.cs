using System.Diagnostics;

namespace UnifiedRgb.Core.Clock;

/// <summary>
/// Portable precise sleeper: Thread.Sleep(1) while far from the deadline, then a spin tail.
/// The far/near threshold is calibrated from the observed Sleep(1) granularity (≈1 ms when the process timer
/// resolution is raised, ≈15.6 ms otherwise), so the deadline is never overslept by a whole timer quantum.
/// </summary>
public sealed class HybridSleeper : IPreciseSleeper
{
    private readonly long _spinTailTicks;

    public HybridSleeper()
    {
        var worst = 0L;
        for (var i = 0; i < 5; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            worst = Math.Max(worst, Stopwatch.GetTimestamp() - t0);
        }

        // margin of 1 ms on top of the worst observed Sleep(1)
        _spinTailTicks = worst + (Stopwatch.Frequency / 1000);
    }

    public double SpinTailMs => _spinTailTicks * 1000.0 / Stopwatch.Frequency;

    public void SleepUntil(long deadlineTicks)
    {
        while (deadlineTicks - Stopwatch.GetTimestamp() > _spinTailTicks)
        {
            Thread.Sleep(1);
        }

        var spinner = default(SpinWait);
        while (Stopwatch.GetTimestamp() < deadlineTicks)
        {
            if (deadlineTicks - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 2000)
            {
                Thread.Yield();
            }
            else
            {
                spinner.SpinOnce(-1);
            }
        }
    }
}
