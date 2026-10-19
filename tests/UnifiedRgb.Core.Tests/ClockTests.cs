using UnifiedRgb.Core.Clock;

namespace UnifiedRgb.Core.Tests;

[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingGroup
{
}

[Collection("Timing")]
public class ClockTests
{
    [Fact]
    public void Jitter_p99_below_2ms_over_10_seconds()
    {
        using var clock = new MasterClock();
        long ticks = 0;
        clock.Tick += (_, _) => Interlocked.Increment(ref ticks);
        clock.Start(60);
        Thread.Sleep(TimeSpan.FromSeconds(10));
        clock.Stop();

        var stats = clock.GetStats();
        stats.JitterP99Ms.Should().BeLessThan(2.0);
        Interlocked.Read(ref ticks).Should().BeInRange(590, 610);
        stats.Overruns.Should().Be(0);
    }

    [Fact]
    public void Time_is_monotone_and_dt_positive()
    {
        using var clock = new MasterClock();
        var times = new List<(double T, double Dt)>();
        clock.Tick += (t, dt) =>
        {
            lock (times)
            {
                times.Add((t, dt));
            }
        };
        clock.Start(120);
        Thread.Sleep(500);
        clock.Stop();

        times.Should().HaveCountGreaterThan(40);
        times.Select(x => x.T).Should().BeInAscendingOrder();
        times.Skip(1).Should().OnlyContain(x => x.Dt > 0);
        clock.Frame.Should().BeGreaterThan(40);
    }

    [Fact]
    public void Rejects_out_of_range_fps()
    {
        using var clock = new MasterClock();
        var act = () => clock.Start(240);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
