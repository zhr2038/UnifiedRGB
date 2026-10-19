using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>WP-11 package A (DEBUG-SESSION-1 §2.5/§4/§6): L-Connect-faithful loop bodies.</summary>
public class LConnectLoopTests
{
    private static readonly Rgb[] Four = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(200, 100, 0)];

    [Theory]
    [InlineData(0f, 7)]
    [InlineData(0.25f, 6)]
    [InlineData(0.5f, 5)]
    [InlineData(0.75f, 4)]
    [InlineData(1f, 3)]
    public void Speed_maps_to_the_vendor_speed_type(float speed, int type) => LConnectLoops.SpeedType(speed).Should().Be(type);

    [Fact]
    public void Tl_per_chain_rainbow_is_13_frames_per_fan_at_speed_type_times_11()
    {
        var plan = LConnectLoops.TryPlan("Rainbow", new EffectParams([], Speed: 0.5f), [], [new LConnectZone("TL", 3, 78)])!;
        plan.Should().NotBeNull();
        plan.Variant.Should().Contain("TLMode1");
        var z = plan.Zones.Should().ContainSingle().Subject;
        (z.Frames, z.IntervalMs, z.Rgb.Length).Should().Be((39, 55.0, 39 * 78 * 3));
        plan.PeriodSeconds.Should().BeApproximately(2.145, 1e-9);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Tl_frame_counts_match_the_il_table_for_every_ported_mode(int fans)
    {
        foreach (var m in FamilyModeTable.Default.Modes("TL"))
        {
            var frames = LConnectLoops.TlFrames(m.VendorMode!.Value, fans, 255, 0, Four);
            frames.Should().NotBeNull($"TLMode{m.VendorMode}");
            frames!.Length.Should().Be(m.VendorFramesFor(fans), $"TLMode{m.VendorMode} {m.EffectId} × {fans} fans");
            frames.Should().OnlyContain(f => f.Length == fans * 26 * 3);
        }
    }

    [Fact]
    public void Stacking_keeps_only_the_last_colours_pass()
    {
        var frames = LConnectLoops.TlFrames(19, 2, 255, 0, Four)!;
        frames.Should().HaveCount(208, "104 × fans: num4 restarts per colour, so colour 4 overwrites the earlier passes");
        var lit = frames.SelectMany(f => f.Chunk(3)).Where(c => c.Any(b => b != 0)).ToArray();
        lit.Should().NotBeEmpty().And.OnlyContain(c => c[0] == (200 * 255 >> 8) && c[1] == (100 * 255 >> 8) && c[2] == 0, "colour 4 at bright 255 (c·b ≫ 8)");
    }

    [Fact]
    public void H2_per_device_rainbow_uses_the_h2_interval_base()
    {
        var plan = LConnectLoops.TryPlan("Rainbow", new EffectParams([], Speed: 0.5f), [], [new LConnectZone("H2", 1, 24)])!;
        var z = plan.Zones.Single();
        (z.Frames, z.IntervalMs).Should().Be((24, 100.0));
        LConnectLoops.H2IntervalMs(2, 3).Should().Be(33);
    }

    [Fact]
    public void Quick_sync_rainbow_is_24_frames_at_speed_type_times_12_with_one_period_for_the_group()
    {
        var zones = new[] { new LConnectZone("TL", 2, 52), new LConnectZone("H2", 1, 24) };
        var plan = LConnectLoops.TryPlan("Rainbow", new EffectParams([], Speed: 0.5f), [], zones)!;
        plan.Variant.Should().Contain("UiMode1");
        plan.Zones.Should().HaveCount(2).And.OnlyContain(z => z.Frames == 24 && z.IntervalMs == 60.0);
        plan.Zones[0].Rgb.Length.Should().Be(24 * 52 * 3);
        plan.Zones[1].Rgb.Length.Should().Be(24 * 24 * 3);

        // both TL edges show the same dot
        var f0 = plan.Zones[0].Rgb.AsSpan(0, 52 * 3);
        f0[..(26 * 3)].ToArray().Should().Equal(f0[(26 * 3)..].ToArray());
    }

    [Fact]
    public void Quick_sync_static_and_breathing_and_per_device_morph()
    {
        var zones = new[] { new LConnectZone("TL", 1, 26), new LConnectZone("TL", 1, 26) };
        LConnectLoops.TryPlan("StaticColor", new EffectParams([new Rgb(10, 20, 30)]), [new Rgb(10, 20, 30)], zones)!.Zones
            .Should().OnlyContain(z => z.Frames == 1);
        LConnectLoops.TryPlan("Breathing", new EffectParams([]), [new Rgb(255, 0, 0)], zones)!.Zones.Should().OnlyContain(z => z.Frames == 170);
        LConnectLoops.TryPlan("RainbowMorph", new EffectParams([], Speed: 1f), [], zones)!.Zones
            .Should().OnlyContain(z => z.Frames == 127 && z.IntervalMs == 33.0);
        var meteor = LConnectLoops.TryPlan("Meteor", new EffectParams([]), Four, zones)!;
        meteor.Should().NotBeNull("WP-12: UIEffect 6 is the shared UiMode6 body");
        meteor.Variant.Should().Contain("UiMode6 over 26 dots");
        meteor.Zones.Should().OnlyContain(z => z.Frames == 26 + 12 && z.IntervalMs == 60.0);
    }

    [Fact]
    public void Unsupported_zones_and_effects_stay_generic()
    {
        var p = new EffectParams([]);
        LConnectLoops.TryPlan("Rainbow", p, [], [new LConnectZone("SLv3", 4, 160)]).Should().BeNull();
        LConnectLoops.TryPlan("Rainbow", p, [], [new LConnectZone(string.Empty, 0, 40)]).Should().BeNull();
        LConnectLoops.TryPlan("Rainbow", p, [], [new LConnectZone("TL", 5, 130)]).Should().BeNull();
        LConnectLoops.TryPlan("Rainbow", p, [], [new LConnectZone("H2", 2, 48)]).Should().BeNull("only the bare block");
        LConnectLoops.TryPlan("Fire", p, [], [new LConnectZone("TL", 1, 26)]).Should().BeNull();
        LConnectLoops.TryPlan("Rainbow", p, [], []).Should().BeNull();
    }

    [Fact]
    public void Brightness_scales_with_the_vendor_shift_and_direction_reverses_space()
    {
        LConnectLoops.TlFrames(1, 1, 0, 0, [])!.Should().OnlyContain(f => f.All(b => b == 0));
        LConnectLoops.BrightnessType(1f).Should().Be(255);
        LConnectLoops.BrightnessType(0f).Should().Be(0);

        var fwd = LConnectLoops.TlFrames(1, 2, 255, 0, [])!;
        var rev = LConnectLoops.TlFrames(1, 2, 255, 1, [])!;
        for (var k = 0; k < fwd.Length; k++)
        {
            for (var row = 0; row < 2; row++)
            {
                var a = fwd[k].AsSpan(row * 26 * 3, 26 * 3).ToArray().Chunk(3).Select(c => (c[0], c[1], c[2])).ToArray();
                var b = rev[k].AsSpan(row * 26 * 3, 26 * 3).ToArray().Chunk(3).Select(c => (c[0], c[1], c[2])).Reverse().ToArray();
                a.Should().Equal(b, $"frame {k} row {row}: Reverse is the spatial mirror");
            }
        }
    }

    [Fact]
    public void Colours_are_capped_by_the_vendor_rgb_max()
    {
        LConnectLoops.CapColour(new Rgb(100, 100, 100), 600).Should().Be(new Rgb(100, 100, 100));
        var c = LConnectLoops.CapColour(Rgb.White, 600);
        (c.R + c.G + c.B).Should().BeLessThanOrEqualTo(600);
        c.R.Should().Be(c.G).And.Be(c.B);
        var uc = LConnectLoops.Slv3UserColour(0, [], 600);
        (uc[0, 0], uc[0, 1], uc[0, 2]).Should().Be(((byte)255, (byte)0, (byte)0), "defaults when no user colour is given");
    }

    [Fact]
    public void Relabelled_tl_modes_map_runway_to_5_and_colour_cycle_to_7_and_keep_the_inventory()
    {
        LConnectLoops.TlModeFor("Runway").Should().Be(5);
        LConnectLoops.TlModeFor("ColorCycle").Should().Be(7);
        LConnectLoops.TlModeFor("Stacking").Should().Be(19);
        LConnectLoops.H2ModeFor("Rainbow").Should().Be(1);
        foreach (var id in new[] { "Runway", "ColorCycle", "Stacking", "Meteor", "Twinkle", "RainbowMorph", "Breathing", "StaticColor", "Rainbow" })
        {
            EffectRegistry.Default.Contains(id).Should().BeTrue($"{id} is not removed or renamed");
        }
    }
}

public class LConnectRenderLoopTests
{
    private static readonly Capabilities Caps = Capabilities.UploadedLoopOnly(200, 20, 12288);

    private static ControllerDescriptor Tl(string id, int fans, string? family = "TL") =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", ZoneLayout.Matrix(fans * 13, 2)) { Family = family, Units = family is null ? 0 : fans }], Caps);

    private static ZoneRef Z(ControllerDescriptor c) => new(c.Id, new ZoneId(0));

    private static LoopFrames Take(FrameBus bus, ControllerDescriptor c)
    {
        bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue($"{c.Id} has a pending loop");
        return pending.Loop;
    }

    [Fact]
    public void Tagged_tl_zone_gets_the_vendor_loop_by_default()
    {
        var tl = Tl("tl", 3);
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([tl]);
        loop.SetScene(new Scene([Assignment.ForZone(Z(tl), "Rainbow", new EffectParams([], Speed: 0.5f))]));
        var l = Take(bus, tl);
        (l.Frames, l.IntervalMs, l.LedCount).Should().Be((39, 55.0, 78));
        loop.LastLoopCompile!.Variants!.Should().ContainSingle().Which.Should().Contain("TLMode1");
    }

    [Fact]
    public void Generic_policy_and_untagged_zones_keep_the_generic_loop()
    {
        var p = new EffectParams([], Speed: 0.5f);
        var gen = EffectRegistry.Default["Rainbow"];
        var expected = LoopCompiler.TimingFor(LoopCompiler.GroupPeriod(LoopCompiler.NaturalPeriod(gen, p with { Colors = gen.Schema.DefaultColors.ToArray() }), [Caps], out _), Caps);

        foreach (var (c, policy) in new[] { (Tl("a", 3), LoopPolicy.Generic), (Tl("b", 3, null), LoopPolicy.LConnect) })
        {
            using var bus = new FrameBus();
            var loop = new RenderLoop(EffectRegistry.Default, bus);
            loop.SetControllers([c]);
            loop.SetScene(new Scene([Assignment.ForZone(Z(c), "Rainbow", p) with { Policy = policy }]));
            var l = Take(bus, c);
            (l.Frames, l.IntervalMs).Should().Be((expected.Frames, expected.IntervalMs), $"{c.Id} {policy}");
            loop.LastLoopCompile!.Variants!.Single().Should().Contain("generic");
        }
    }

    [Fact]
    public void Mixed_streamed_groups_keep_the_vendor_body()
    {
        // NATIVE design §7.3 supersedes WP-11's "mixed group → generic": Vendor keeps the L-Connect body and the streamed member
        // plays its slice of the same plan; Program is the explicit generic choice.
        var tl = Tl("tl", 1);
        var fan = TestSupport.Controller("fan", ZoneLayout.Ring(16));
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([tl, fan]);
        loop.SetScene(new Scene([Assignment.ForGroup("g", [Z(fan), Z(tl)], "Rainbow", new EffectParams([], Speed: 0.5f))]));
        var vendor = loop.LastLoopCompile!.Variants!.Single();
        vendor.Should().Contain("Vendor:Rainbow").And.NotContain("generic");
        loop.HasPlayedSlices.Should().BeTrue("the streamed ring plays its slice of the vendor plan");

        loop.SetScene(new Scene([Assignment.ForGroup("g", [Z(fan), Z(tl)], "Rainbow", new EffectParams([], Speed: 0.5f)) with { Strategy = ContentStrategy.Program }]));
        loop.LastLoopCompile!.Variants!.Single().Should().Contain("Program:Rainbow generic");
    }

    [Fact]
    public void Republish_for_one_controller_forces_its_group_with_a_fresh_shared_index()
    {
        var a = Tl("a", 1);
        var b = Tl("b", 2);
        var other = Tl("other", 1);
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([a, b, other]);
        loop.SetScene(new Scene(
        [
            Assignment.ForGroup("g", [Z(a), Z(b)], "Rainbow", new EffectParams([], Speed: 0.5f)),
            Assignment.ForZone(Z(other), "Breathing", new EffectParams([new Rgb(255, 0, 0)])),
        ]));
        var firstA = Take(bus, a);
        Take(bus, b);
        Take(bus, other);

        loop.SetScene(new Scene(
        [
            Assignment.ForGroup("g", [Z(a), Z(b)], "Rainbow", new EffectParams([], Speed: 0.5f)),
            Assignment.ForZone(Z(other), "Breathing", new EffectParams([new Rgb(255, 0, 0)])),
        ]));
        bus[a.Id]!.HasPendingLoop.Should().BeFalse("unchanged content is not re-sent");

        var report = loop.RepublishLoops(a.Id)!;
        report.Published.Should().Be(2, "the mounted device and its group companion");
        var againA = Take(bus, a);
        var againB = Take(bus, b);
        againA.SameContent(firstA).Should().BeTrue("canonical RGB is unchanged; the adapter applies the mounting");
        againA.EffectIndex.Should().BeGreaterThan(firstA.EffectIndex);
        againB.EffectIndex.Should().Be(againA.EffectIndex, "one fresh shared EffectIndex");
        bus[other.Id]!.HasPendingLoop.Should().BeFalse("other assignments keep their loops");
    }
}
