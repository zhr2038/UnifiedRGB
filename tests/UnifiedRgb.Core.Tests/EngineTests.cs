using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

public class EngineTests
{
    private static readonly ControllerDescriptor Fan = TestSupport.Controller("fan", ZoneLayout.Ring(16));
    private static readonly ControllerDescriptor Gpu = TestSupport.Controller("gpu", ZoneLayout.Point);
    private static readonly ControllerDescriptor Dram = TestSupport.Controller("dram", ZoneLayout.Linear(8));

    private static ZoneRef Z(ControllerDescriptor c) => new(c.Id, new ZoneId(0));

    [Fact]
    public void Group_runway_head_moves_monotonically_across_concatenated_u()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Fan, Gpu, Dram]);
        loop.SetScene(new Scene([Assignment.ForGroup("all", [Z(Fan), Z(Gpu), Z(Dram)], RunwayEffect.EffectId,
            new EffectParams([new Rgb(255, 255, 255)], Speed: 0.5f))]));

        const int n = 25;
        var prev = -1;
        var advance = 0;
        for (var f = 0; f < 240; f++)
        {
            loop.RenderFrame(f / 60.0, 1 / 60.0);
            var leds = new List<int>(n);
            foreach (var c in new[] { Fan, Gpu, Dram })
            {
                bus[c.Id]!.TryConsume(out var frame).Should().BeTrue();
                leds.AddRange(frame.Zones[0].Select(p => p.R + p.G + p.B));
            }

            var head = leds.IndexOf(leds.Max());
            if (prev >= 0)
            {
                var step = ((head - prev) % n + n) % n;
                step.Should().BeInRange(0, 2, $"frame {f}: head went {prev}→{head}");
                advance += step;
            }

            prev = head;
        }

        advance.Should().BeGreaterThan(n); // more than one full lap through fan → GPU → DRAM
    }

    [Fact]
    public void RenderLoop_frame_path_allocates_nothing()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        var mb = TestSupport.Controller("mb", ZoneLayout.Linear(2), ZoneLayout.Linear(20), ZoneLayout.Linear(20), ZoneLayout.Linear(20));
        loop.SetControllers([Fan, Gpu, Dram, mb]);
        var assignments = EffectRegistry.Default.All
            .Select((g, i) => Assignment.ForGroup("g" + i, [Z(Fan), Z(Gpu), Z(Dram), new ZoneRef(mb.Id, new ZoneId(i % 4))], g.Id))
            .ToArray();
        loop.SetScene(new Scene(assignments));

        for (var f = 0; f < 10; f++)
        {
            loop.RenderFrame(f / 60.0, 1 / 60.0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 10; f < 310; f++)
        {
            loop.RenderFrame(f / 60.0, 1 / 60.0);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void Mailbox_is_latest_frame_wins()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Gpu);
        for (var i = 1; i <= 3; i++)
        {
            var back = box.Back;
            back.Zones[0][0] = new Rgb((byte)i, 0, 0);
            bus.Publish(Gpu.Id, back);
        }

        box.TryConsume(out var frame).Should().BeTrue();
        frame.Zones[0][0].R.Should().Be(3);
        box.TryConsume(out _).Should().BeFalse();
        box.Superseded.Should().Be(2);
    }

    [Fact]
    public void Publishing_a_foreign_buffer_is_rejected()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Gpu);
        box.TryConsume(out var front);
        var act = () => bus.Publish(Gpu.Id, front);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetScene_rejects_unknown_effect_and_zone()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Gpu]);
        var badEffect = () => loop.SetScene(new Scene([Assignment.ForZone(Z(Gpu), "Nope")]));
        badEffect.Should().Throw<ArgumentException>();
        var badZone = () => loop.SetScene(new Scene([Assignment.ForZone(new ZoneRef(Gpu.Id, new ZoneId(5)), RainbowEffect.EffectId)]));
        badZone.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Brightness_is_applied_at_the_output_stage()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Gpu]);
        loop.SetScene(new Scene([Assignment.ForZone(Z(Gpu), StaticColorEffect.EffectId, new EffectParams([new Rgb(200, 100, 50)], Brightness: 0.5f))]));
        loop.RenderFrame(0, 0);
        bus[Gpu.Id]!.TryConsume(out var frame);
        frame.Zones[0][0].Should().Be(new Rgb(100, 50, 25));
    }

    [Fact]
    public void DeviceWriter_drains_latest_frame_into_adapter()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Dram]);
        loop.SetScene(new Scene([Assignment.ForZone(Z(Dram), StaticColorEffect.EffectId, new EffectParams([new Rgb(0, 9, 0)]))]));
        var adapter = new RecordingAdapter();
        using var writer = new DeviceWriter(bus[Dram.Id]!, adapter);
        loop.RenderFrame(0, 0);
        loop.RenderFrame(1 / 60.0, 1 / 60.0);
        writer.DrainOnce().Should().BeTrue();
        writer.DrainOnce().Should().BeFalse();
        adapter.Writes.Should().ContainSingle().Which.Colors.Should().OnlyContain(c => c == new Rgb(0, 9, 0));
        writer.GetStats().Written.Should().Be(1);
    }

    [Fact]
    public void Paused_writer_never_touches_the_device()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Gpu);
        var adapter = new RecordingAdapter();
        using var writer = new DeviceWriter(box, adapter) { Paused = true };
        bus.Publish(Gpu.Id, box.Back);
        writer.DrainOnce().Should().BeTrue();
        adapter.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Quiesce_waits_out_the_write_in_flight_and_nothing_follows_it()
    {
        // Codex review (writer quiescence): a bring-up pause must drain the frame already inside the adapter before a marker
        using var bus = new FrameBus();
        var box = bus.Register(Gpu);
        using var adapter = new BlockingAdapter();
        using var writer = new DeviceWriter(box, adapter);
        bus.Publish(Gpu.Id, box.Back);
        var draining = Task.Factory.StartNew(writer.DrainOnce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        adapter.Entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the first write is in flight");

        var quiesce = Task.Factory.StartNew(writer.Quiesce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        (await Task.WhenAny(quiesce, Task.Delay(200))).Should().NotBeSameAs(quiesce, "Quiesce returns only after the in-flight write");
        writer.Paused.Should().BeTrue("the pause is visible at once");
        adapter.Release.Set();
        await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
        (await draining.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        adapter.Writes.Should().Be(1);

        bus.Publish(Gpu.Id, box.Back);
        writer.DrainOnce().Should().BeTrue("the frame is consumed");
        adapter.Writes.Should().Be(1, "nothing reaches the device after Quiesce");
        writer.GetStats().Written.Should().Be(1);
    }

    [Fact]
    public async Task Blocked_writer_stop_is_bounded_refuses_overlap_and_can_finish_after_io_returns()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Gpu);
        using var adapter = new BlockingAdapter();
        var writer = new DeviceWriter(box, adapter, stopTimeout: TimeSpan.FromMilliseconds(150));
        writer.Start();
        bus.Publish(Gpu.Id, box.Back);
        try
        {
            adapter.Entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var stop = () => writer.StopAsync();
            await stop.Should().ThrowAsync<TimeoutException>().WithMessage("*I/O may still be in flight*");
            writer.GetStats().Errors.Should().Be(1);
            writer.GetStats().LastError.Should().Contain("did not stop");
            var start = writer.Start;
            start.Should().Throw<InvalidOperationException>().WithMessage("*second writer is refused*");
            await stop.Should().ThrowAsync<TimeoutException>();
            writer.GetStats().Errors.Should().Be(1, "the same failed shutdown is reported only once");
        }
        finally
        {
            adapter.Release.Set();
            // The same stop task completes; it never starts a second hardware writer.
            await Task.Delay(50);
            await writer.StopAsync();
        }
        adapter.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Quiesce_timeout_keeps_pause_until_the_inflight_write_finishes()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Gpu);
        using var adapter = new BlockingAdapter();
        var writer = new DeviceWriter(box, adapter, stopTimeout: TimeSpan.FromMilliseconds(100));
        bus.Publish(Gpu.Id, box.Back);
        var draining = Task.Factory.StartNew(writer.DrainOnce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            adapter.Entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var quiesce = writer.Quiesce;
            quiesce.Should().Throw<TimeoutException>();
            writer.Paused.Should().BeTrue();
        }
        finally { adapter.Release.Set(); await draining; writer.Dispose(); }
    }

    private sealed class BlockingAdapter : IDeviceAdapter, IDisposable
    {
        private int _writes;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int Writes => Volatile.Read(ref _writes);

        public string Vendor => "Test";

        public DeviceClass Class => DeviceClass.Virtual;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);

        public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref _writes);
        }

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
