using UnifiedRgb.Screens.Telemetry;

namespace UnifiedRgb.Screens.Tests;

/// <summary>PC metrics: unavailable values are null (never 0), CPU from deltas, optional GPU reader with bounded retry.</summary>
public sealed class TelemetryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_readable_yields_nulls_and_an_error()
    {
        using var s = new PcTelemetrySampler(new FakeOs { Fail = true }, null, new ManualTime(T0));

        var t = s.Sample();

        (t.CpuUsagePercent, t.MemoryUsedBytes, t.MemoryTotalBytes).Should().Be((null, null, null));
        (t.GpuUsagePercent, t.GpuTemperatureC, t.GpuMemoryUsedBytes, t.GpuMemoryTotalBytes).Should().Be((null, null, null, null));
        t.Source.Should().Be(PcTelemetrySampler.BaseSource);
        t.Error.Should().Be("GetSystemTimes failed; GlobalMemoryStatusEx failed");
        t.Timestamp.Should().Be(T0);
        s.Latest.Should().BeSameAs(t);
    }

    [Fact]
    public void Cpu_is_the_busy_share_between_two_samples()
    {
        using var s = new PcTelemetrySampler(new FakeOs(), null, new ManualTime(T0));

        s.Latest.Should().BeNull();
        var first = s.Sample();
        var second = s.Sample();

        first.CpuUsagePercent.Should().BeNull("one sample has no interval");
        second.CpuUsagePercent.Should().Be(25.0);
        (second.MemoryUsedBytes, second.MemoryTotalBytes).Should().Be((12L << 30, 32L << 30));
        second.Error.Should().BeNull();
        second.GpuUsagePercent.Should().BeNull("GPU metrics are disabled");
    }

    [Fact]
    public void A_gpu_reader_adds_its_values_and_source()
    {
        var gpu = new FakeGpu();
        using var s = new PcTelemetrySampler(new FakeOs(), () => (gpu, null), new ManualTime(T0));

        var t = s.Sample();

        (t.GpuUsagePercent, t.GpuTemperatureC, t.GpuMemoryUsedBytes, t.GpuMemoryTotalBytes).Should().Be((42.0, 61.0, 8L << 30, 32L << 30));
        t.Source.Should().Be(PcTelemetrySampler.BaseSource + "+FakeGPU");
        s.Dispose();
        gpu.Disposed.Should().BeTrue();
    }

    [Fact]
    public void A_failing_gpu_factory_is_retried_at_most_once_a_minute()
    {
        var time = new ManualTime(T0);
        var calls = 0;
        using var s = new PcTelemetrySampler(new FakeOs(), () =>
        {
            calls++;
            throw new DllNotFoundException("boom");
        }, time);

        s.Sample().Error.Should().Be("GPU metrics unavailable: boom");
        s.Sample().GpuUsagePercent.Should().BeNull();
        time.Advance(TimeSpan.FromSeconds(59));
        s.Sample().Error.Should().Be("GPU metrics unavailable: boom");
        calls.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(2));
        s.Sample();
        calls.Should().Be(2);
        s.Sample().CpuUsagePercent.Should().Be(25.0, "OS metrics are unaffected");
    }

    [Fact]
    public void A_factory_reporting_no_gpu_keeps_its_reason()
    {
        using var s = new PcTelemetrySampler(new FakeOs(), () => (null, "nvml.dll is not in System32"), new ManualTime(T0));

        var t = s.Sample();

        t.Error.Should().Be("nvml.dll is not in System32");
        t.Source.Should().Be(PcTelemetrySampler.BaseSource);
    }

    [Fact]
    public void A_gpu_reader_that_stops_answering_is_dropped_and_recreated_later()
    {
        var time = new ManualTime(T0);
        var readers = new List<FakeGpu>();
        using var s = new PcTelemetrySampler(new FakeOs(), () =>
        {
            var g = new FakeGpu();
            readers.Add(g);
            return (g, null);
        }, time);

        s.Sample().GpuUsagePercent.Should().Be(42);
        readers[0].Next = new GpuSample(null, null, null, null, "GPU lost");
        var lost = s.Sample();
        lost.Error.Should().Be("GPU lost");
        readers[0].Disposed.Should().BeTrue();

        var after = s.Sample();
        after.Source.Should().Be(PcTelemetrySampler.BaseSource);
        after.GpuUsagePercent.Should().BeNull();
        readers.Should().HaveCount(1);
        time.Advance(TimeSpan.FromMinutes(1.1));
        s.Sample().GpuUsagePercent.Should().Be(42);
        readers.Should().HaveCount(2);
    }

    [Fact]
    public void A_throwing_gpu_read_is_contained()
    {
        using var s = new PcTelemetrySampler(new FakeOs(), () => (new ThrowingGpu(), null), new ManualTime(T0));

        var t = s.Sample();

        t.Error.Should().Be("GPU read failed: driver reset");
        t.GpuTemperatureC.Should().BeNull();
        t.MemoryTotalBytes.Should().Be(32L << 30);
    }

    [Fact]
    public async Task The_service_warms_up_so_the_first_answer_has_cpu_even_while_disabled()
    {
        await using var rig = new Rig();

        var t = await rig.Manager.GetPcTelemetryAsync();

        rig.Options.Enabled.Should().BeFalse();
        t.CpuUsagePercent.Should().Be(25.0);
        t.MemoryTotalBytes.Should().Be(32L << 30);
        t.Source.Should().Be(PcTelemetrySampler.BaseSource, "NVML is off in the rig");
        t.GpuUsagePercent.Should().BeNull();
        (await rig.Manager.GetPcTelemetryAsync()).CpuUsagePercent.Should().Be(25.0);
    }

    [Fact]
    public async Task The_service_reports_null_metrics_when_the_os_reads_fail()
    {
        await using var rig = new Rig();
        rig.Os.Fail = true;

        var t = await rig.Manager.GetPcTelemetryAsync();

        (t.CpuUsagePercent, t.MemoryUsedBytes, t.GpuUsagePercent).Should().Be((null, null, null));
        t.Error.Should().Contain("GetSystemTimes failed");
    }

    private sealed class ThrowingGpu : IGpuReader
    {
        public string Name => "Throwing";

        public GpuSample Read() => throw new InvalidOperationException("driver reset");

        public void Dispose()
        {
        }
    }
}
