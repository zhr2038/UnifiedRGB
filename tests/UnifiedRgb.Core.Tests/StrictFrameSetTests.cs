using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// OPUS-GENERIC-SCENE-PACKAGES in the render loop: claimed (strict) frame sets use the opt-in physical-ms contract (60 ms → 96 raw
/// ticks, exact or blocked), are never retargeted, never fall back to the assignment's nominal effect and are dark when blocked.
/// Unclaimed frame sets keep their historical nominal meaning byte for byte.
/// </summary>
public sealed class StrictFrameSetTests
{
    private const string Id = "package:c:b";
    private static readonly ControllerId Chain = new("chain:1");
    private static readonly ZoneRef Target = new(Chain, new ZoneId(0));

    private sealed class StrictSource(FrameSet? set, string? problem = null, IReadOnlyList<ZoneRef>? bound = null) : IFrameSetSource
    {
        public bool TryGet(string id, out FrameSet frameSet)
        {
            frameSet = set!;
            return id == set?.Id;
        }

        public bool Claims(string id) => id.StartsWith("package:", StringComparison.Ordinal);

        public string? Problem(string id) => problem;

        public IReadOnlyList<ZoneRef>? BoundTargets(string id) => bound ?? [Target];
    }

    private static byte[] Rgb(int frames) => Enumerable.Range(0, frames * 4 * 3).Select(i => (byte)(1 + (i % 250))).ToArray();

    private static FrameSet Set(string id, double intervalMs, FrameSetTiming timing, int frames = 200) =>
        new(id, frames, intervalMs, [4], [Rgb(frames)]) { Timing = timing };

    /// <summary>Red StaticColor is only the nominal effect: strict content must never show it.</summary>
    private static Scene SceneOf(string id, ZoneRef? target = null) =>
        new([new Assignment([target ?? Target], "StaticColor", new EffectParams([new Rgb(255, 0, 0)]), "g", ContentStrategy.Frames) { FrameSetId = id }]);

    private static (RenderLoop Loop, FrameBus Bus) Start(IFrameSetSource source)
    {
        var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus) { FrameSets = source };
        loop.SetControllers([new ControllerDescriptor(Chain, "T", "chain", DeviceType.Cooler, DeviceClass.Virtual, "1",
            [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Linear(4)), new ZoneDescriptor(new ZoneId(1), "y", ZoneLayout.Linear(4))],
            Capabilities.UploadedLoopOnly(4096, 1, 12288))]);
        return (loop, bus);
    }

    private static LoopFrames Published(FrameBus bus, int zone = 0)
    {
        LoopFrames? found = null;
        while (bus[Chain]!.TryConsumeLoop(out var pending))
        {
            if (pending.Zone.Value == zone)
            {
                found = pending.Loop;
            }
        }

        return found ?? throw new InvalidOperationException("no loop published");
    }

    [Fact]
    public void A_physical_60ms_strict_set_uploads_its_exact_bytes_at_96_raw_ticks_with_a_12s_timeline()
    {
        var set = Set(Id, 60, FrameSetTiming.PhysicalMs);
        var (loop, bus) = Start(new StrictSource(set));
        using (loop)
        using (bus)
        {
            loop.SetScene(SceneOf(Id));
            var l = Published(bus);
            (l.Frames, l.IntervalMs, l.Strict).Should().Be((200, 96.0, true));
            l.Rgb.ToArray().Should().Equal(set.ZoneRgb[0]);
            loop.BlockedContent.Should().BeEmpty();
            var t = loop.Timeline(Id)!;
            t.PeriodMs.Should().Be(12000);
            t.Blocked.Should().BeNull();
            t.IsDeviceClock.Should().BeFalse();
        }
    }

    [Fact]
    public void An_unclaimed_nominal_set_keeps_its_historical_meaning()
    {
        var legacy = new InMemoryFrameSetSource();
        legacy.Add(Set("legacy", 40, FrameSetTiming.Nominal, 20));
        var (loop, bus) = Start(legacy);
        using (loop)
        using (bus)
        {
            loop.SetScene(SceneOf("legacy"));
            var l = Published(bus);
            (l.Frames, l.IntervalMs, l.Strict).Should().Be((20, 40.0, false), "the header interval is the nominal value, not converted");
            loop.Timeline("legacy").Should().BeNull("only strict sets expose a shared package timeline");
        }
    }

    [Theory]
    [InlineData(60.001, null, false)]
    [InlineData(60, "package missing", false)]
    [InlineData(60, null, true)]
    public void Unrepresentable_missing_or_retargeted_strict_content_is_blocked_dark_never_the_nominal_effect(double interval, string? problem, bool retarget)
    {
        var set = problem is null ? Set(Id, interval, FrameSetTiming.PhysicalMs) : null;
        var (loop, bus) = Start(new StrictSource(set, problem));
        using (loop)
        using (bus)
        {
            loop.SetScene(SceneOf(Id, retarget ? new ZoneRef(Chain, new ZoneId(1)) : null));
            loop.BlockedContent.Should().ContainSingle().Which.Should().Contain("BLOCKED");
            var l = Published(bus, retarget ? 1 : 0);
            l.Rgb.ToArray().Should().OnlyContain(b => b == 0, "dark, never red StaticColor or another substitute");
            l.Strict.Should().BeFalse("the dark hold is the uncovered black loop");
            loop.Timeline(Id)!.Blocked.Should().NotBeNull();
        }
    }

    [Fact]
    public void Preflight_compiles_without_publishing_or_changing_the_scene()
    {
        var set = Set(Id, 60, FrameSetTiming.PhysicalMs);
        var (loop, bus) = Start(new StrictSource(set));
        using (loop)
        using (bus)
        {
            while (bus[Chain]!.TryConsumeLoop(out _))
            {
                // the controller set's initial (uncovered) loops
            }

            var before = loop.Scene;
            var pre = loop.PreflightStrict(SceneOf(Id));
            pre.Blocked.Should().BeEmpty();
            pre.Loops.Should().ContainSingle().Which.Loop.Should().Match<LoopFrames>(l => l.Strict && l.Frames == 200 && l.IntervalMs == 96);
            bus[Chain]!.HasPendingLoop.Should().BeFalse();
            loop.Scene.Should().BeSameAs(before);

            loop.PreflightStrict(SceneOf(Id, new ZoneRef(Chain, new ZoneId(1)))).Blocked.Should().ContainSingle();
        }
    }

    [Fact]
    public void Sleeping_keyboard_or_mouse_preserves_case_loops_unblocked_timeline_and_exact_zone_rgb_alignment()
    {
        var caseController = new ControllerId("case:1");
        var kbController = new ControllerId("rog:kb");
        var mouseController = new ControllerId("rog:mouse");
        ZoneRef[] targets =
        [
            new(Chain, new ZoneId(0)),
            new(caseController, new ZoneId(0)),
            new(kbController, new ZoneId(0)),
            new(mouseController, new ZoneId(0)),
        ];

        const int frames = 4;
        byte[] Fill(int leds, Func<int, Rgb> colorAtFrame)
        {
            var buf = new byte[frames * leds * 3];
            for (var f = 0; f < frames; f++)
            {
                var c = colorAtFrame(f);
                for (var i = 0; i < leds; i++)
                {
                    var o = ((f * leds) + i) * 3;
                    buf[o] = c.R;
                    buf[o + 1] = c.G;
                    buf[o + 2] = c.B;
                }
            }

            return buf;
        }

        var set = new FrameSet(Id, frames, 60, [4, 4, 81, 1], [
            Fill(4, f => new Rgb((byte)(5 + f), 0, 0)),
            Fill(4, f => new Rgb((byte)(10 + f), 0, 0)),
            Fill(81, f => new Rgb(0, (byte)(80 + f), 0)),
            Fill(1, f => new Rgb(0, 0, (byte)(200 + f))),
        ]) { Timing = FrameSetTiming.PhysicalMs };

        var chainDesc = new ControllerDescriptor(Chain, "LianLi", "chain", DeviceType.Cooler, DeviceClass.LianLi, "1",
            [new ZoneDescriptor(new ZoneId(0), "chain", ZoneLayout.Linear(4))], Capabilities.UploadedLoopOnly(4096, 1, 12288));
        var caseDesc = new ControllerDescriptor(caseController, "MSI", "case", DeviceType.Motherboard, DeviceClass.MsiGpu, "2",
            [new ZoneDescriptor(new ZoneId(0), "case", ZoneLayout.Linear(4))], Capabilities.DirectOnly(60));
        var kbDesc = new ControllerDescriptor(kbController, "ROG", "kb", DeviceType.Keyboard, DeviceClass.AsusRog, "3",
            [new ZoneDescriptor(new ZoneId(0), "keys", ZoneLayout.Linear(81))], Capabilities.DirectOnly(60));
        var mouseDesc = new ControllerDescriptor(mouseController, "ROG", "mouse", DeviceType.Mouse, DeviceClass.AsusRog, "4",
            [new ZoneDescriptor(new ZoneId(0), "logo", ZoneLayout.Linear(1))], Capabilities.DirectOnly(60));

        var scene = new Scene([new Assignment(targets, "StaticColor", new EffectParams([new Rgb(255, 0, 0)]), "g", ContentStrategy.Frames) { FrameSetId = Id }]);

        using var bus = new FrameBus();
        using var loop = new RenderLoop(EffectRegistry.Default, bus) { FrameSets = new StrictSource(set, bound: targets) };
        loop.SetControllers([chainDesc, caseDesc, kbDesc, mouseDesc]);
        loop.SetScene(scene);

        _ = Published(bus);
        loop.RenderFrame(0, 0.06);
        bus[caseController]!.TryConsume(out var cf).Should().BeTrue();
        cf.Zones[0][0].Should().Be(new Rgb(10, 0, 0));
        bus[kbController]!.TryConsume(out var kf).Should().BeTrue();
        kf.Zones[0][0].Should().Be(new Rgb(0, 80, 0));
        bus[mouseController]!.TryConsume(out var mf).Should().BeTrue();
        mf.Zones[0].Should().Equal([new Rgb(0, 0, 200)]);

        // Keyboard sleeps: case loop is unchanged (0 publishes), shared timeline stays unblocked, and Mouse keeps its own ZoneRgb[3].
        loop.SetSleepingPeripherals(new Dictionary<ZoneRef, int> { [targets[2]] = 81 });
        loop.SetControllers([chainDesc, caseDesc, mouseDesc]);

        loop.BlockedContent.Should().BeEmpty();
        loop.Timeline(Id)!.Blocked.Should().BeNull();
        bus[Chain]!.HasPendingLoop.Should().BeFalse("case loop must never re-upload or black out when a keyboard sleeps");
        loop.LastLoopCompile!.Published.Should().Be(0);
        loop.LastLoopCompile!.Unchanged.Should().Be(1);

        loop.RenderFrame(0.06, 0.06);
        bus[caseController]!.TryConsume(out cf).Should().BeTrue();
        cf.Zones[0][0].Should().Be(new Rgb(11, 0, 0));
        bus[mouseController]!.TryConsume(out mf).Should().BeTrue();
        mf.Zones[0].Should().Equal([new Rgb(0, 0, 201)], "sleeping Keyboard at index 2 must not shift Mouse to index 2");

        // Keyboard wakes back up: case loop still untouched and Keyboard resumes its exact ZoneRgb[2].
        loop.SetSleepingPeripherals(null);
        loop.SetControllers([chainDesc, caseDesc, kbDesc, mouseDesc]);

        loop.BlockedContent.Should().BeEmpty();
        bus[Chain]!.HasPendingLoop.Should().BeFalse("waking Keyboard must not force Lian Li RF re-uploads");
        loop.LastLoopCompile!.Published.Should().Be(0);
        loop.LastLoopCompile!.Unchanged.Should().Be(1);

        loop.RenderFrame(0.12, 0.06);
        bus[kbController]!.TryConsume(out kf).Should().BeTrue();
        kf.Zones[0][0].Should().Be(new Rgb(0, 82, 0));
    }

    [Fact]
    public void Peripheral_only_strict_package_keeps_local_timeline_when_all_peripherals_sleep_but_unregistered_missing_blocks()
    {
        var kbController = new ControllerId("rog:kb");
        var mouseController = new ControllerId("rog:mouse");
        ZoneRef[] targets = [new(kbController, new ZoneId(0)), new(mouseController, new ZoneId(0))];
        var set = new FrameSet(Id, 200, 60, [4, 4], [Rgb(200), Rgb(200)]) { Timing = FrameSetTiming.PhysicalMs };
        var kbDesc = new ControllerDescriptor(kbController, "ROG", "kb", DeviceType.Keyboard, DeviceClass.AsusRog, "3",
            [new ZoneDescriptor(new ZoneId(0), "keys", ZoneLayout.Linear(4))], Capabilities.DirectOnly(60));
        var mouseDesc = new ControllerDescriptor(mouseController, "ROG", "mouse", DeviceType.Mouse, DeviceClass.AsusRog, "4",
            [new ZoneDescriptor(new ZoneId(0), "logo", ZoneLayout.Linear(4))], Capabilities.DirectOnly(60));
        var scene = new Scene([new Assignment(targets, "StaticColor", new EffectParams([new Rgb(255, 0, 0)]), "g", ContentStrategy.Frames) { FrameSetId = Id }]);

        using var bus = new FrameBus();
        using var loop = new RenderLoop(EffectRegistry.Default, bus) { FrameSets = new StrictSource(set, bound: targets) };
        loop.SetSleepingPeripherals(new Dictionary<ZoneRef, int> { [targets[0]] = 4, [targets[1]] = 4 });
        loop.SetControllers([]);
        loop.SetScene(scene, allowMissingZones: true);

        loop.BlockedContent.Should().BeEmpty();
        var timeline = loop.Timeline(Id)!;
        timeline.Blocked.Should().BeNull();
        timeline.Source.Should().Be(TimelineSources.LocalEpoch);
        loop.TimelineAt(Id, 1.5)!.PositionMs.Should().BeApproximately(1500, 1);

        loop.SetControllers([kbDesc, mouseDesc]);
        loop.SetSleepingPeripherals(null);
        loop.BlockedContent.Should().BeEmpty();

        // Removing both without registering them as sleeping peripherals (e.g., non-sleepable absence) must still block dark.
        loop.SetControllers([]);
        loop.BlockedContent.Should().ContainSingle().Which.Should().Contain("BLOCKED");
        loop.Timeline(Id)!.Source.Should().Be(TimelineSources.Blocked);
    }
}
