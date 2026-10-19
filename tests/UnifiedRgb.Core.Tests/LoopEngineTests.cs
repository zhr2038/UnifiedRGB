using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>Records loop uploads; <see cref="Hold"/> blocks the first upload so publishes can pile up behind it.</summary>
internal sealed class RecordingLoopAdapter : ILoopUploadAdapter, ILoopRecoveryControl
{
    private readonly object _gate = new();

    public List<(ControllerId Id, ZoneId Zone, LoopFrames Loop)> Uploads { get; } = [];

    public ManualResetEventSlim? Hold { get; set; }

    public bool RecoveryAllowed { get; private set; }

    public LoopUploadStats? ReportOverride { get; set; }

    public void SetRecoveryAllowed(ControllerId id, bool allowed) => RecoveryAllowed = allowed;

    public uint[] Indices()
    {
        lock (_gate)
        {
            return [.. Uploads.Select(u => u.Loop.EffectIndex)];
        }
    }

    public ManualResetEventSlim Entered { get; } = new();

    /// <summary>True: the hold is cancellable, like a queued upload waiting for the sender's gate; false: already on the wire.</summary>
    public bool Queued { get; set; }

    public string Vendor => "Test";

    public DeviceClass Class => DeviceClass.Virtual;

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);

    public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new NotSupportedException("uploaded loops only");

    public Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct)
    {
        Entered.Set();
        if (Hold is { } h)
        {
            if (Queued)
            {
                ct.ThrowIfCancellationRequested(); // as SemaphoreSlim.WaitAsync: a cancelled token wins over a free gate
                h.Wait(TimeSpan.FromSeconds(10), ct);
                ct.ThrowIfCancellationRequested(); // a release racing cancellation must not start a stale queued upload
            }
            else
            {
                h.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            }

            Hold = null;
        }

        lock (_gate)
        {
            Uploads.Add((id, zone, loop));
        }

        return Task.CompletedTask;
    }

    public LoopUploadStats? LastUpload(ControllerId id)
    {
        lock (_gate)
        {
            return ReportOverride ?? (Uploads.Count == 0 ? null : new LoopUploadStats(DateTimeOffset.UnixEpoch, 1, Uploads[^1].Loop.EffectIndex, Uploads[^1].Loop.Frames,
                Uploads[^1].Loop.IntervalMs, Uploads[^1].Loop.Rgb.Length, Uploads[^1].Loop.Rgb.Length, 1, TimeSpan.Zero, 0, "test"));
        }
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    public Task CloseAsync(ControllerId id) => Task.CompletedTask;
}

public class LoopCompilerTests
{
    private static readonly Capabilities Chain = Capabilities.UploadedLoopOnly(200, 20, 12288);
    private static readonly Capabilities Slow = Capabilities.UploadedLoopOnly(100, 30, 12288);

    [Fact]
    public void Timing_takes_as_many_frames_as_the_minimum_interval_allows()
    {
        LoopCompiler.TimingFor(1.0, Chain).Should().Be(new LoopTiming(50, 20));
        LoopCompiler.TimingFor(7.0, Chain).Should().Be(new LoopTiming(200, 35));
        LoopCompiler.TimingFor(0.0, Chain).Should().Be(new LoopTiming(1, 20));
        LoopCompiler.TimingFor(0.005, Chain).Should().Be(new LoopTiming(1, 5));
        LoopCompiler.TimingFor(2.5, Slow).Should().Be(new LoopTiming(83, 30.12));
        LoopCompiler.TimingFor(1.0, Capabilities.UploadedLoopOnly(200, 20.83, 12288)).Frames.Should().Be(48);
    }

    [Fact]
    public void Group_period_falls_back_and_truncates_to_the_tightest_zone()
    {
        LoopCompiler.GroupPeriod(3.0, [Chain], out var cut).Should().Be(3.0);
        cut.Should().BeFalse();
        LoopCompiler.GroupPeriod(null, [Chain], out cut).Should().Be(4.0, "fallback: 200 frames at the minimum interval");
        cut.Should().BeFalse();
        LoopCompiler.GroupPeriod(30.0, [Chain], out cut).Should().Be(10.0, "200 × 50 ms");
        cut.Should().BeTrue();
        LoopCompiler.GroupPeriod(30.0, [Chain, Slow], out cut).Should().Be(5.0, "the slow zone holds 100 × 50 ms");
        cut.Should().BeTrue();
        LoopCompiler.GroupPeriod(0, [Chain], out cut).Should().Be(0);
        LoopCompiler.NaturalPeriod(new TwinkleEffect(), EffectParams.Default).Should().BeNull("random effects have no period");
        LoopCompiler.NaturalPeriod(new StaticColorEffect(), EffectParams.Default).Should().Be(0);
    }

    [Fact]
    public void Effect_index_is_ms_since_midnight_and_strictly_increasing()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Local);
        var c = new LoopCompiler(() => now);
        c.NextEffectIndex().Should().Be(43_200_000u);
        c.NextEffectIndex().Should().Be(43_200_001u);
        now = now.AddMilliseconds(500);
        c.NextEffectIndex().Should().Be(43_200_500u);
        now = now.Date.AddDays(1);
        c.NextEffectIndex().Should().Be(0u, "a new day restarts the index");
    }

    [Fact]
    public void Every_loopable_generator_repeats_after_its_period()
    {
        var samples = Projector.Project(ZoneLayout.Linear(40));
        var checkedCount = 0;
        foreach (var gen in EffectRegistry.Default.All)
        {
            var colors = gen.Schema.DefaultColors.ToArray();
            var p = LoopCompiler.NaturalPeriod(gen, new EffectParams(colors));
            if (p is not > 0)
            {
                continue;
            }

            foreach (var t in new[] { 0.0, 0.37 * p.Value })
            {
                var a = TestSupport.Render(gen, samples, t, speed: 0.5f);
                var b = TestSupport.Render(gen, samples, t + p.Value, speed: 0.5f);
                for (var i = 0; i < a.Length; i++)
                {
                    Math.Abs(a[i].R - b[i].R).Should().BeLessThanOrEqualTo(2, $"{gen.Id} LED {i} at t={t:0.###} vs t+P (P={p:0.###} s)");
                    Math.Abs(a[i].G - b[i].G).Should().BeLessThanOrEqualTo(2, $"{gen.Id} LED {i}");
                    Math.Abs(a[i].B - b[i].B).Should().BeLessThanOrEqualTo(2, $"{gen.Id} LED {i}");
                }
            }

            checkedCount++;
        }

        checkedCount.Should().BeGreaterThanOrEqualTo(20, "23 generators declare a period, StaticColor's is 0");
    }
}

public class LoopEngineTests
{
    private static readonly ControllerDescriptor Fan = TestSupport.Controller("fan", ZoneLayout.Ring(16));
    private static readonly ControllerDescriptor Dram = TestSupport.Controller("dram", ZoneLayout.Linear(8));
    private static readonly ControllerDescriptor Chain = LoopController("chain", 40, Capabilities.UploadedLoopOnly(200, 20, 12288));
    private static readonly ControllerDescriptor Slow = LoopController("slow", 24, Capabilities.UploadedLoopOnly(100, 30, 12288));

    private static ControllerDescriptor LoopController(string id, int leds, Capabilities caps) =>
        new(new ControllerId(id), "Test", id, DeviceType.Cooler, DeviceClass.Virtual, id, [new ZoneDescriptor(new ZoneId(0), "chain", ZoneLayout.Linear(leds))], caps);

    private static ZoneRef Z(ControllerDescriptor c) => new(c.Id, new ZoneId(0));

    private static readonly EffectParams Runway = new([new Rgb(255, 255, 255)], Speed: 0.5f);

    private static LoopFrames Take(FrameBus bus, ControllerDescriptor c)
    {
        bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue($"{c.Id} has a pending loop");
        return pending.Loop;
    }

    private static Rgb[] FrameOf(LoopFrames loop, int k)
    {
        var span = loop.Rgb.Slice(k * loop.LedCount * 3, loop.LedCount * 3).ToArray();
        return Enumerable.Range(0, loop.LedCount).Select(i => new Rgb(span[3 * i], span[(3 * i) + 1], span[(3 * i) + 2])).ToArray();
    }

    [Fact]
    public void Group_slice_of_a_loop_equals_the_streamed_render_at_t_k()
    {
        using var bus = new FrameBus();
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Local);
        var loop = new RenderLoop(EffectRegistry.Default, bus, new LoopCompiler(() => now));
        loop.SetControllers([Fan, Chain, Dram]);
        loop.SetScene(new Scene([Assignment.ForGroup("all", [Z(Fan), Z(Chain), Z(Dram)], RunwayEffect.EffectId, Runway)]));

        var l = Take(bus, Chain);
        l.EffectIndex.Should().Be(43_200_001u, "43_200_000 was SetControllers' black loop for the then-unassigned chain (superseded)");
        l.LedCount.Should().Be(40);
        var gen = EffectRegistry.Default[RunwayEffect.EffectId];
        var period = LoopCompiler.GroupPeriod(LoopCompiler.NaturalPeriod(gen, Runway), [Chain.Capabilities], out _);
        l.Frames.Should().Be(LoopCompiler.TimingFor(period, Chain.Capabilities).Frames);
        l.PeriodSeconds.Should().BeApproximately(period, l.Frames * 0.005 / 1000 + 1e-9);

        var samples = GroupLayout.Concat([Fan.Zones[0].Layout, Chain.Zones[0].Layout, Dram.Zones[0].Layout]);
        var lit = 0;
        foreach (var k in new[] { 0, 1, l.Frames / 3, l.Frames - 1 })
        {
            var t = LoopCompiler.FrameTime(k, l.Frames, period);
            var whole = new Rgb[samples.Length];
            gen.Render(new RenderContext(t, period / l.Frames, [.. Runway.Colors], Runway.Speed, 1f, Direction.Forward, samples, Runway.Seed), whole);
            FrameOf(l, k).Should().Equal(whole[16..56], $"loop frame {k} is the group render at t_k");

            loop.RenderFrame(t, period / l.Frames);
            bus[Fan.Id]!.TryConsume(out var fan).Should().BeTrue();
            bus[Dram.Id]!.TryConsume(out var dram).Should().BeTrue();
            fan.Zones[0].Should().Equal(whole[..16], "the streamed fan renders the same t (anchor T0 = 0)");
            dram.Zones[0].Should().Equal(whole[56..]);
            bus[Chain.Id]!.TryConsume(out _).Should().BeFalse("RenderFrame never publishes frames to a loop controller");
            lit += whole.Count(c => c != Rgb.Black);
        }

        lit.Should().BePositive();
    }

    [Fact]
    public void Mixed_loop_zones_share_one_period_and_effect_index()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Chain, Slow, Fan]);
        loop.SetScene(new Scene([Assignment.ForGroup("all", [Z(Chain), Z(Slow), Z(Fan)], RainbowEffect.EffectId, new EffectParams([], Speed: 0.3f))]));
        var a = Take(bus, Chain);
        var b = Take(bus, Slow);
        a.EffectIndex.Should().Be(b.EffectIndex, "one compile, one EffectIndex for every chain");
        a.Frames.Should().NotBe(b.Frames);
        a.PeriodSeconds.Should().BeApproximately(b.PeriodSeconds, 0.01 * Math.Max(a.Frames, b.Frames) / 1000);
        b.IntervalMs.Should().BeGreaterThanOrEqualTo(30);
        a.IntervalMs.Should().BeGreaterThanOrEqualTo(20);
        loop.LastLoopCompile!.Published.Should().Be(2);
    }

    [Fact]
    public void Long_periods_are_truncated_and_reported()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Slow]);
        loop.SetScene(new Scene([Assignment.ForZone(Z(Slow), RainbowEffect.EffectId, new EffectParams([], Speed: 0f))]));
        var l = Take(bus, Slow);
        l.PeriodSeconds.Should().BeLessThanOrEqualTo(5.0 + 1e-6);
        loop.LastLoopCompile!.Truncated.Should().ContainSingle().Which.Should().Contain("truncated");
    }

    [Fact]
    public void Unchanged_loops_are_not_republished_until_a_resync()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Chain, Fan]);
        var scene = new Scene([Assignment.ForGroup("g", [Z(Fan), Z(Chain)], RunwayEffect.EffectId, Runway)]);
        loop.SetScene(scene);
        var first = Take(bus, Chain);

        loop.SetScene(new Scene([Assignment.ForGroup("g", [Z(Fan), Z(Chain)], RunwayEffect.EffectId, Runway)]));
        bus[Chain.Id]!.HasPendingLoop.Should().BeFalse("same content: the device already plays it");
        loop.LastLoopCompile!.Unchanged.Should().Be(1);
        loop.SetControllers([Chain, Fan]);
        bus[Chain.Id]!.HasPendingLoop.Should().BeFalse();

        var report = loop.RepublishLoops()!;
        report.Published.Should().Be(1);
        var again = Take(bus, Chain);
        again.SameContent(first).Should().BeTrue();
        again.EffectIndex.Should().BeGreaterThan(first.EffectIndex, "a re-sync carries a fresh EffectIndex");

        loop.SetScene(new Scene([Assignment.ForGroup("g", [Z(Fan), Z(Chain)], RunwayEffect.EffectId, Runway with { Speed = 0.9f })]));
        Take(bus, Chain).SameContent(first).Should().BeFalse();
    }

    [Fact]
    public void Untargeted_loop_zones_get_a_one_frame_black_loop()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Chain, Fan]);
        loop.SetScene(new Scene([Assignment.ForZone(Z(Fan), RainbowEffect.EffectId)]));
        var l = Take(bus, Chain);
        l.Frames.Should().Be(1);
        l.Rgb.ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Anchoring_shifts_the_streamed_phase_to_the_upload_start()
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([Fan, Chain]);
        loop.SetScene(new Scene([Assignment.ForGroup("g", [Z(Fan), Z(Chain)], RunwayEffect.EffectId, Runway)]));
        var l = Take(bus, Chain);
        var gen = EffectRegistry.Default[RunwayEffect.EffectId];
        var period = l.Frames * l.IntervalMs / 1000;
        const double t0 = 3.21;
        loop.AnchorLoop(l.EffectIndex + 1, 99); // another compile's index: ignored
        loop.AnchorLoop(l.EffectIndex, t0 + 0.5);
        loop.AnchorLoop(l.EffectIndex, t0); // earliest report wins
        loop.AnchorLoop(l.EffectIndex, t0 + 0.2);

        var samples = GroupLayout.Concat([Fan.Zones[0].Layout, Chain.Zones[0].Layout]);
        var k = l.Frames / 4;
        var tk = LoopCompiler.FrameTime(k, l.Frames, period);
        var whole = new Rgb[samples.Length];
        var p = LoopCompiler.GroupPeriod(LoopCompiler.NaturalPeriod(gen, Runway), [Chain.Capabilities], out _);
        gen.Render(new RenderContext(LoopCompiler.FrameTime(k, l.Frames, p), 0, [.. Runway.Colors], Runway.Speed, 1f, Direction.Forward, samples, Runway.Seed), whole);
        loop.RenderFrame(t0 + (5 * p) + LoopCompiler.FrameTime(k, l.Frames, p), 0);
        bus[Fan.Id]!.TryConsume(out var fan).Should().BeTrue();
        for (var i = 0; i < 16; i++)
        {
            Math.Abs(fan.Zones[0][i].R - whole[i].R).Should().BeLessThanOrEqualTo(2, $"LED {i} at t0 + 5P + t_k");
        }

        FrameOf(l, k).Should().Equal(whole[16..]);
        tk.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Fifty_publishes_coalesce_into_one_upload()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Chain);
        var adapter = new RecordingLoopAdapter();
        using var writer = new DeviceWriter(box, adapter);
        writer.UploadsLoops.Should().BeTrue();
        for (var i = 0; i < 50; i++)
        {
            bus.PublishLoop(Chain.Id, new ZoneId(0), new LoopFrames(1, 40, 20, (uint)i, new byte[120]));
        }

        writer.DrainOnce().Should().BeTrue();
        writer.DrainOnce().Should().BeFalse();
        adapter.Uploads.Should().ContainSingle().Which.Loop.EffectIndex.Should().Be(49u);
        var stats = writer.GetStats();
        (stats.Uploads, stats.Written, stats.Superseded, stats.LastUpload!.EffectIndex).Should().Be((1L, 1L, 49L, 49u));
    }

    [Fact]
    public void Writer_ownership_pause_quiesce_and_stop_also_control_autonomous_recovery()
    {
        using var bus = new FrameBus(); var adapter = new RecordingLoopAdapter();
        using var writer = new DeviceWriter(bus.Register(Chain), adapter);
        adapter.RecoveryAllowed.Should().BeFalse();
        writer.Start(); adapter.RecoveryAllowed.Should().BeTrue();
        writer.Paused = true; adapter.RecoveryAllowed.Should().BeFalse();
        writer.Paused = false; adapter.RecoveryAllowed.Should().BeTrue();
        writer.Quiesce(); adapter.RecoveryAllowed.Should().BeFalse();
        writer.Paused = false; adapter.RecoveryAllowed.Should().BeTrue();
        writer.Stop(); adapter.RecoveryAllowed.Should().BeFalse();
        writer.Paused = false; adapter.RecoveryAllowed.Should().BeFalse("a stopped writer cannot grant recovery permission");
        adapter.Uploads.Should().BeEmpty();
    }

    [Fact]
    public void Receiver_diagnostics_refresh_without_another_writer_upload_or_timing_anchor()
    {
        using var bus = new FrameBus(); var adapter = new RecordingLoopAdapter();
        using var writer = new DeviceWriter(bus.Register(Chain), adapter);
        bus.PublishLoop(Chain.Id, new ZoneId(0), new LoopFrames(1, 40, 20, 100, new byte[120]));
        writer.DrainOnce().Should().BeTrue();
        var original = writer.GetStats();
        adapter.ReportOverride = original.LastUpload! with { ReceiverDelivery = new("ReportedMatch", 100, 100, 2600, DateTimeOffset.UtcNow, 1, "generation only") };
        var current = writer.GetStats();
        current.LastUpload!.ReceiverDelivery!.State.Should().Be("ReportedMatch");
        (current.Written, current.Uploads, current.LastUpload.StartedTimestamp).Should().Be((original.Written, original.Uploads, original.LastUpload!.StartedTimestamp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publishes_during_an_upload_coalesce_into_one_follow_up(bool queued)
    {
        using var bus = new FrameBus();
        var box = bus.Register(Chain);
        using var hold = new ManualResetEventSlim();
        var adapter = new RecordingLoopAdapter { Hold = hold, Queued = queued };
        var uploaded = new List<uint>();
        using var writer = new DeviceWriter(box, adapter, onLoopUploaded: (_, p, _) => { lock (uploaded) { uploaded.Add(p.Loop.EffectIndex); } });
        writer.Start();
        bus.PublishLoop(Chain.Id, new ZoneId(0), new LoopFrames(1, 40, 20, 0, new byte[120]));
        adapter.Entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        for (var i = 1; i <= 50; i++)
        {
            bus.PublishLoop(Chain.Id, new ZoneId(0), new LoopFrames(1, 40, 20, (uint)i, new byte[120]));
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (queued && DateTime.UtcNow < deadline && writer.GetStats().UploadsAborted == 0)
        {
            Thread.Sleep(5); // the queued upload observes the cancellation before the gate opens
        }

        hold.Set();
        while (DateTime.UtcNow < deadline && !adapter.Indices().Contains(50u))
        {
            Thread.Sleep(10);
        }

        Thread.Sleep(50);
        writer.Stop();
        // in flight: it completes, then only the newest of the 50 follows; queued: it is aborted and only the newest goes out
        var expected = queued ? new[] { 50u } : [0u, 50u];
        adapter.Indices().Should().Equal(expected);
        uploaded.Should().Equal(expected);
        if (queued)
        {
            writer.GetStats().UploadsAborted.Should().BeGreaterThanOrEqualTo(1,
                "several queued versions may observe cancellation while the publisher is still coalescing; none reaches the wire");
        }
        else
        {
            writer.GetStats().UploadsAborted.Should().Be(0);
        }
    }

    [Fact]
    public void Loop_writers_need_a_loop_adapter_and_never_stream()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Chain);
        var act = () => new DeviceWriter(box, new RecordingAdapter());
        act.Should().Throw<ArgumentException>();
        var sink = (IFrameSink)new NoLoopSink();
        var publish = () => sink.PublishLoop(Chain.Id, new ZoneId(0), new LoopFrames(1, 1, 20, 0, new byte[3]));
        publish.Should().Throw<NotSupportedException>();
    }

    private sealed class NoLoopSink : IFrameSink
    {
        public void Publish(ControllerId id, Frame frame)
        {
        }
    }
}
