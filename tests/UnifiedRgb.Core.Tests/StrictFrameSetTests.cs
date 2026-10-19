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
}
