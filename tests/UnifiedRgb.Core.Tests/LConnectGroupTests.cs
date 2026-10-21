using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>WP-12 (DEBUG-SESSION-2 §8/§9): Quick/Sync group routes, per-device exceptions, calibration and vendor tables.</summary>
public class LConnectGroupTests
{
    private static readonly Rgb[] Four = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(200, 100, 0)];

    /// <summary>The owner's four chains in group order: TL140 ×3, TL120 ×3, H2 block, TL140 LCD ×3 (39 + 39 + 24 + 39 = 141 dots).</summary>
    public static readonly LConnectZone[] Group = [new("TL", 3, 78), new("TL", 3, 78), new("H2", 1, 24), new("TL", 3, 78)];

    private static readonly int[] Starts = [0, 39, 78, 102];

    private static EffectParams P(Direction dir = Direction.Forward, float speed = 0.5f, float bright = 1f) => new(Four, speed, bright, dir);

    [Theory]
    [InlineData("Rainbow", 1, 200, 60.0)]
    [InlineData("StaticColor", 3, 1, 60.0)]
    [InlineData("Breathing", 4, 200, 60.0)]
    [InlineData("Runway", 5, 200, 60.0)]
    [InlineData("Meteor", 6, 200, 60.0)]
    [InlineData("ColorCycle", 9, 200, 60.0)]
    [InlineData("CoverCycle", 10, 200, 60.0)]
    [InlineData("Wave", 11, 100, 120.0)]
    [InlineData("MeteorShower", 12, 200, 60.0)]
    [InlineData("Tide", 13, 100, 120.0)]
    [InlineData("MopUp", 15, 200, 60.0)]
    [InlineData("Disco", 16, 200, 60.0)]
    [InlineData("Warning", 22, 200, 60.0)]
    [InlineData("Echo", 26, 200, 60.0)]
    [InlineData("HeartBeat", 27, 200, 90.0)]
    [InlineData("Ripple", 30, 100, 180.0)]
    public void Four_chain_group_parity_per_shared_route(string effect, int ui, int frames, double interval)
    {
        var plan = LConnectLoops.TryPlan(effect, P(), Four, Group, out var why);
        plan.Should().NotBeNull(why);
        LConnectLoops.GroupRoutes[effect].Should().Be(ui);
        plan!.Variant.Should().Be($"UnifiedRgb uploaded UiMode{ui} over 141 dots");
        plan.Zones.Should().HaveCount(4);
        plan.Synchronous.Should().BeTrue("a shared UiMode body keeps one period for the group");
        for (var z = 0; z < 4; z++)
        {
            var leds = Group[z].LedCount;
            (plan.Zones[z].Frames, plan.Zones[z].IntervalMs, plan.Zones[z].Rgb.Length).Should().Be((frames, interval, frames * leds * 3), $"{effect} zone {z}");
        }

        plan.PeriodSeconds.Should().BeApproximately(frames * interval / 1000.0, 1e-9);
    }

    [Fact]
    public void Four_chain_stacking_is_the_fan_aligned_exception_with_one_period()
    {
        // WP-15: shared Stacking runs the unchanged UiMode7 body over 12 source dots per TL fan (36 + 36 + 24 + 36 = 132)
        var plan = LConnectLoops.TryPlan("Stacking", P(), Four, Group, out var why);
        plan.Should().NotBeNull(why);
        LConnectLoops.GroupRoutes["Stacking"].Should().Be(7);
        plan!.Variant.Should().Be("UnifiedRgb uploaded UiMode7, fan-aligned Stacking over 132 source dots");
        plan.Synchronous.Should().BeTrue();
        for (var z = 0; z < 4; z++)
        {
            var leds = Group[z].LedCount;
            (plan.Zones[z].Frames, plan.Zones[z].IntervalMs, plan.Zones[z].Rgb.Length).Should().Be((792, 60.0, 792 * leds * 3), $"zone {z}");
        }

        plan.PeriodSeconds.Should().BeApproximately(792 * 60.0 / 1000.0, 1e-9);
    }

    [Theory]
    [InlineData("ElectricCurrent", 14)]
    [InlineData("Mixing", 17)]
    public void Loop_computed_routes_still_share_one_period(string effect, int ui)
    {
        var plan = LConnectLoops.TryPlan(effect, P(), Four, Group, out var why)!;
        plan.Should().NotBeNull(why);
        plan.Variant.Should().Contain($"UiMode{ui} over 141 dots");
        plan.Zones.Should().HaveCount(4).And.OnlyContain(z => z.Frames == plan.Zones[0].Frames && z.IntervalMs == 60.0);
        plan.Zones[0].Frames.Should().BeGreaterThan(1);
        var raw = LConnectLoops.UiModeFrames(ui, 141, 255, 0, Ui(Four), out _)!;
        raw.Count.Should().Be(plan.Zones[0].Frames);
    }

    [Fact]
    public void Acceptance_four_chain_counts_at_speed_half()
    {
        var expected = new Dictionary<string, int> { ["Runway"] = 200, ["Meteor"] = 200, ["ColorCycle"] = 200, ["Stacking"] = 792, ["CoverCycle"] = 200, ["Wave"] = 100 };
        foreach (var (effect, frames) in expected)
        {
            var interval = effect == "Wave" ? 120 : 60;
            LConnectLoops.TryPlan(effect, P(), Four, Group)!.Zones.Should().OnlyContain(z => z.Frames == frames && z.IntervalMs == interval, effect);
        }
    }

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Group_slices_take_contiguous_dots_at_0_39_78_102_and_both_tl_edges_match(Direction dir)
    {
        // Stacking is the WP-15 fan-aligned exception (LConnectStackingAlignmentTests); every other shared body slices contiguously
        foreach (var effect in new[] { "Runway", "Meteor", "CoverCycle", "Wave", "ColorCycle", "Ripple" })
        {
            var ui = LConnectLoops.GroupRoutes[effect];
            var plan = LConnectLoops.TryPlan(effect, P(dir), Four, Group)!;
            var raw = LConnectLoops.UiModeFrames(ui, 141, 255, LConnectLoops.VendorDirection(dir), Ui(Four), out _)!;
            for (var z = 0; z < 4; z++)
            {
                var zone = Group[z];
                var width = zone.Family == "TL" ? 39 : 24;
                for (var f = 0; f < raw.Count; f++)
                {
                    var expected = raw[f].AsSpan(Starts[z] * 3, width * 3).ToArray();
                    var o = f * zone.LedCount * 3;
                    plan.Zones[z].Rgb.AsSpan(o, width * 3).ToArray().Should().Equal(expected, $"{effect} {dir} zone {z} frame {f}");
                    if (zone.Family == "TL")
                    {
                        plan.Zones[z].Rgb.AsSpan(o + (width * 3), width * 3).ToArray().Should().Equal(expected, "both TL edges show the same dots");
                    }
                }
            }
        }
    }

    [Fact]
    public void Runway_head_traverses_the_whole_shared_strip()
    {
        var raw = LConnectLoops.UiModeFrames(5, 141, 255, 0, Ui(Four), out _)!;
        var peaks = raw.Select(frame => Enumerable.Range(0,141).MaxBy(i => frame[i*3]+frame[(i*3)+1]+frame[(i*3)+2])).ToArray();
        peaks.Min().Should().BeLessThan(10);
        peaks.Max().Should().BeGreaterThan(130);
        peaks.Distinct().Count().Should().BeGreaterThan(40, "the moving head must traverse the case, rather than pulse a fixed location");
    }

    [Fact]
    public void Procedural_travel_curves_are_bounded_and_direction_is_an_exact_spatial_mirror()
    {
        foreach (var mode in new[] { 5, 6 })
        {
            var forward = LConnectLoops.UiModeFrames(mode,141,255,0,Ui(Four),out _)!;
            var reverse = LConnectLoops.UiModeFrames(mode,141,255,1,Ui(Four),out _)!;
            forward.Should().HaveCount(200);
            forward.SelectMany(f=>f).Max().Should().BeGreaterThan(200);
            for(var frame=0;frame<forward.Count;frame++)
                forward[frame].Chunk(3).Reverse().SelectMany(p=>p).Should().Equal(reverse[frame]);
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(8, 1)]
    public void Procedural_travel_budget_does_not_grow_with_the_group_size(int tlFans, int h2)
    {
        var zones = new List<LConnectZone>();
        for(var left=tlFans;left>0;left-=4) {var fans=Math.Min(4,left);zones.Add(new("TL",fans,fans*26));}
        for(var i=0;i<h2;i++) zones.Add(new("H2",1,24));
        if(zones.Count<2) zones.Add(new("TL",1,26));
        foreach(var effect in new[]{"Runway","Meteor"})
        {
            var plan=LConnectLoops.TryPlan(effect,P(),Four,zones)!;
            plan.Synchronous.Should().BeTrue();
            plan.Zones.Should().OnlyContain(z=>z.Frames==200);
            for(var i=0;i<zones.Count;i++) plan.Zones[i].Rgb.Length.Should().Be(200*zones[i].LedCount*3);
        }
    }

    [Fact]
    public void Procedural_twinkle_is_deterministic_and_reaches_both_edges()
    {
        Rgb[] palette=[new(255,0,0),new(0,255,0),new(0,0,255)];
        var first=LConnectLoops.TlFrames(29,2,255,0,palette)!;
        var second=LConnectLoops.TlFrames(29,2,255,0,palette)!;
        first.Should().HaveCount(200);
        for(var frame=0;frame<first.Length;frame++)
        {
            first[frame].Should().Equal(second[frame],"random-looking content must be deterministic");
            first[frame][..78].Should().Equal(first[frame][78..],"both fan edges share their logical strip");
        }
        first.Select(f=>Convert.ToHexString(f)).Distinct().Count().Should().BeGreaterThan(20);
    }

    [Fact]
    public void Colour_cycle_is_uniform_across_devices_and_cross_fades()
    {
        var plan=LConnectLoops.TryPlan("ColorCycle",P(),Four,Group)!;
        plan.Zones.Should().OnlyContain(z=>z.Frames==200);
        for(var frame=0;frame<200;frame++)
        {
            var expected=plan.Zones[0].Rgb.AsSpan(frame*Group[0].LedCount*3,3).ToArray();
            for(var zone=0;zone<Group.Length;zone++)
                plan.Zones[zone].Rgb.AsSpan(frame*Group[zone].LedCount*3,Group[zone].LedCount*3).ToArray().Chunk(3)
                    .Should().OnlyContain(px=>px.SequenceEqual(expected));
        }
        plan.Zones[0].Rgb.Chunk(3).Should().Contain(px=>px[0]>0&&px[1]>0,"cross-fades are intentional in the new curve");
    }

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Stacking_lights_every_zone_of_the_group(Direction dir)
    {
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), Four, Group)!;
        plan.Zones.Should().HaveCount(4);
        for (var z = 0; z < 4; z++)
        {
            plan.Zones[z].Rgb.Should().Contain(b => b != 0, $"zone {z} ({Group[z].Family}) is lit during the cycle ({dir})");
        }

        // the stack: after the first pass the last 12 dots of the run stay lit
        var raw = LConnectLoops.UiModeFrames(7, 141, 255, LConnectLoops.VendorDirection(dir), Ui(Four), out _)!;
        raw.Should().HaveCount(900);
        var last = raw[^1].Chunk(3).Count(c => c[0] != 0);
        last.Should().BeGreaterThanOrEqualTo(141 - 12, "all completed passes remain stacked at the end");
    }

    [Fact]
    public void Twinkle_group_is_per_device_with_unequal_periods()
    {
        var plan = LConnectLoops.TryPlan("Twinkle", P(), Four, Group)!;
        plan.Variant.Should().Be("UnifiedRgb uploaded UIEffect 8 (per-device TLMode29/H2Mode8)");
        (plan.Zones[0].Frames, plan.Zones[0].IntervalMs).Should().Be((200, 55.0));
        (plan.Zones[2].Frames, plan.Zones[2].IntervalMs).Should().Be((200, 100.0));
        plan.Zones[2].Rgb.Length.Should().Be(200 * 24 * 3);
        plan.Synchronous.Should().BeFalse();
        plan.PeriodSeconds.Should().BeApproximately(220, 1e-9, "lcm(11 s, 20 s)");
        plan.Summary.Should().Contain("zone 0: 200 × 55 ms = 11 s").And.Contain("zone 2: 200 × 100 ms = 20 s").And.Contain("repeats after 220 s");

        var morph = LConnectLoops.TryPlan("RainbowMorph", P(), Four, Group)!;
        morph.Variant.Should().Contain("UIEffect 2 (per-device TLMode2/H2Mode2)");
        morph.Zones.Should().OnlyContain(z => z.Frames == 200 && z.IntervalMs == 55.0);
        morph.Synchronous.Should().BeTrue();
    }

    [Theory]
    [InlineData("tl-twinkle.bin")]
    [InlineData("h2-twinkle.bin")]
    [InlineData("ui-ripple.bin")]
    public void Manufacturer_sampling_tables_are_absent_from_the_product(string name)
    {
        typeof(LConnectLoops).Assembly.GetManifestResourceStream($"UnifiedRgb.Core.{name}").Should().BeNull();
        LConnectLoops.HasTwinkleTable.Should().BeFalse();
    }

    [Theory]
    [InlineData(600, 200)]
    [InlineData(570, 190)]
    public void Proportional_white_cap_preserves_neutrality(int cap, byte channel)
    {
        LConnectLoops.CapColour(Rgb.White, cap).Should().Be(new Rgb(channel, channel, channel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(255)]
    public void Procedural_twinkle_brightness_is_monotonic_and_zero_is_black(int bright)
    {
        var dark=LConnectLoops.TlFrames(29,1,0,0,[Rgb.White])!;
        dark.SelectMany(f=>f).Should().OnlyContain(b=>b==0);
        var full=LConnectLoops.TlFrames(29,1,255,0,[Rgb.White])!;
        var dim=LConnectLoops.TlFrames(29,1,bright,0,[Rgb.White])!;
        for(var frame=0;frame<full.Length;frame++)
            for(var i=0;i<full[frame].Length;i++)
                Math.Abs(dim[frame][i]-(full[frame][i]*bright/255.0)).Should().BeLessThanOrEqualTo(1);
        LConnectLoops.H2Twinkle(bright).Should().HaveCount(200).And.OnlyContain(f=>f.Length==72);
    }

    [Fact]
    public void Speed_and_brightness_have_the_five_vendor_steps()
    {
        float[] steps = [0f, 0.25f, 0.5f, 0.75f, 1f];
        steps.Select(LConnectLoops.SpeedType).Should().Equal(7, 6, 5, 4, 3);
        steps.Select(LConnectLoops.VendorSpeed).Should().Equal(steps);
        steps.Select(LConnectLoops.BrightnessType).Should().Equal(0, 64, 128, 192, 255);
        LConnectLoops.BrightnessType(0.75f).Should().Be(192, "75 % is 192, not 191");

        // bucket boundaries: nearest step, midpoints away from zero
        (LConnectLoops.SpeedType(0.12f), LConnectLoops.SpeedType(0.125f), LConnectLoops.SpeedType(0.62f), LConnectLoops.SpeedType(0.625f)).Should().Be((7, 6, 5, 4));
        (LConnectLoops.SpeedType(-1f), LConnectLoops.SpeedType(2f), LConnectLoops.SpeedType(float.NaN)).Should().Be((7, 3, 5));
        LConnectLoops.VendorSpeed(0.3f).Should().Be(0.25f);

        // canonical intervals per step (L1..L5)
        steps.Select(s => LConnectLoops.SpeedType(s) * LConnectLoops.TlIntervalBase).Should().Equal(77, 66, 55, 44, 33);
        steps.Select(s => LConnectLoops.SpeedType(s) * LConnectLoops.UiIntervalBaseFor(5)).Should().Equal(84, 72, 60, 48, 36);
        steps.Select(s => LConnectLoops.SpeedType(s) * LConnectLoops.UiIntervalBaseFor(30)).Should().Equal(126, 108, 90, 72, 54);
        steps.Select(s => LConnectLoops.H2IntervalMs(1, LConnectLoops.SpeedType(s))).Should().Equal(140, 120, 100, 80, 60);
        steps.Select(s => LConnectLoops.H2IntervalMs(2, LConnectLoops.SpeedType(s))).Should().Equal(77, 66, 55, 44, 33);
        foreach (var s in steps)
        {
            LConnectLoops.TryPlan("Meteor", P(speed: s), Four, Group)!.Zones[0].IntervalMs.Should().Be(LConnectLoops.SpeedType(s) * 12);
        }
    }

    [Fact]
    public void Brightness_is_applied_once_inside_the_vendor_body()
    {
        var full = LConnectLoops.TryPlan("StaticColor", P(), [new Rgb(200, 100, 50)], Group)!;
        var dim = LConnectLoops.TryPlan("StaticColor", P(bright: 0.75f), [new Rgb(200, 100, 50)], Group)!;
        full.Zones[0].Rgb[..3].Should().Equal((byte)(200 * 255 >> 8), (byte)(100 * 255 >> 8), (byte)(50 * 255 >> 8));
        dim.Zones[0].Rgb[..3].Should().Equal((byte)(200 * 192 >> 8), (byte)(100 * 192 >> 8), (byte)(50 * 192 >> 8));
    }

    [Fact]
    public void H2_metadata_follows_the_h2effects_enum_and_selects_only_ported_bodies()
    {
        var h2 = FamilyModeTable.Default.Modes("H2");
        h2.Select(m => m.VendorName).Should().Equal("Rainbow", "RainbowMorph", "StaticColor", "Breathing", "Runway", "Meteor", "Taichi", "Twinkle", "Voice", "Pump", "Bounce");
        h2.Select(m => m.VendorSelected).Should().Equal(true, true, true, true, true, true, true, true, false, false, false);
        h2.Take(8).Select(m => m.VendorMode).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        (LConnectLoops.H2ModeFor("Runway"), LConnectLoops.H2ModeFor("Meteor"), LConnectLoops.H2ModeFor("Taichi"), LConnectLoops.H2ModeFor("Twinkle"))
            .Should().Be((5, 6, 7, 8));
        LConnectLoops.H2ModeFor("Tide").Should().BeNull("Pump is not ported; its Tide stand-in is generic");
        LConnectLoops.H2ModeFor("Burst").Should().BeNull();
        h2[8].Label.Should().Contain("Voice").And.Contain("generic");

        foreach (var (family, allowed) in new[] { ("TL", LConnectLoops.TlModes), ("H2", LConnectLoops.H2Modes) })
        {
            foreach (var m in FamilyModeTable.Default.Modes(family))
            {
                var selected = family == "TL" ? LConnectLoops.TlModeFor(m.EffectId) : LConnectLoops.H2ModeFor(m.EffectId);
                if (m.VendorSelected)
                {
                    allowed.Should().Contain(m.VendorMode!.Value, $"{family} {m.VendorName} advertises a vendor body that must exist");
                    selected.Should().NotBeNull();
                }
            }
        }
    }

    [Fact]
    public void Every_effect_is_registered_and_every_unsupported_combo_has_a_reason()
    {
        EffectRegistry.Default.All.Should().HaveCount(34);
        foreach (var gen in EffectRegistry.Default.All)
        {
            foreach (var zones in new[] { Group, [new LConnectZone("TL", 3, 78)], [new LConnectZone("H2", 1, 24)] })
            {
                var plan = LConnectLoops.TryPlan(gen.Id, P(), gen.Schema.DefaultColors, zones, out var why);
                if (plan is null)
                {
                    why.Should().NotBeNullOrWhiteSpace($"{gen.Id} on {zones.Length} zones falls back with a reason");
                }
                else
                {
                    why.Should().BeEmpty();
                    plan.Zones.Should().HaveCount(zones.Length);
                }
            }
        }

        LConnectLoops.TryPlan("Fire", P(), Four, Group, out var fire).Should().BeNull();
        fire.Should().Contain("no Quick/Sync UIEffect").And.Contain("Fire");
        LConnectLoops.TryPlan("Wave", P(), Four, [new LConnectZone("TL", 3, 78)], out var wave).Should().BeNull();
        wave.Should().Contain("compatibility route");
        LConnectLoops.TryPlan("Wave", P(), Four, [new LConnectZone("H2", 1, 24)], out var h2).Should().BeNull();
        h2.Should().Contain("compatibility route");
    }

    [Fact]
    public void Ui_bodies_refuse_what_the_vendor_would_not_produce()
    {
        LConnectLoops.UiModeFrames(5, 23, 255, 0, Ui(Four), out var small).Should().BeNull();
        small.Should().Contain("24..720");
        LConnectLoops.UiModeFrames(27, 521, 255, 0, Ui(Four), out var beat).Should().BeNull();
        beat.Should().Contain("24..520");
        LConnectLoops.UiModeFrames(27, 520, 255, 0, Ui(Four), out _).Should().HaveCount(200);
        LConnectLoops.UiStackFrames(141).Should().Be(900);
        LConnectLoops.UiModeFrames(7, 400, 255, 0, Ui(Four), out var stack).Should().BeNull();
        stack.Should().Contain("4096");

        // two single-fan TLs = 26 dots: in range; one TL + nothing else never reaches the group path
        LConnectLoops.TryPlan("Meteor", P(), Four, [new LConnectZone("TL", 1, 26), new LConnectZone("TL", 1, 26)]).Should().NotBeNull();
    }

    private static byte[] Resource(string name)
    {
        using var s = typeof(LConnectLoops).Assembly.GetManifestResourceStream($"UnifiedRgb.Core.{name}")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>GetUIeEffect's colour array as TryPlan builds it (capped at 600).</summary>
    private static byte[,] Ui(Rgb[] colors)
    {
        var ui = new byte[colors.Length, 3];
        for (var i = 0; i < colors.Length && i < 6; i++)
        {
            var c = LConnectLoops.CapColour(colors[i], LConnectLoops.RgbMax);
            (ui[i, 0], ui[i, 1], ui[i, 2]) = (c.R, c.G, c.B);
        }

        return ui;
    }

    private static int[] Lit(byte[] frame, int dots, int r, int g, int b) =>
        Enumerable.Range(0, dots).Where(d => frame[d * 3] == (r * 255 >> 8) && frame[(d * 3) + 1] == (g * 255 >> 8) && frame[(d * 3) + 2] == (b * 255 >> 8)).ToArray();
}

/// <summary>WP-12: the engine publishes the vendor group plan once, reports fallback reasons, and never republishes on ticks.</summary>
public class LConnectGroupRenderLoopTests
{
    private static readonly Capabilities Caps = Capabilities.UploadedLoopOnly(200, 20, 12288);

    private static ControllerDescriptor Chain(string id, string family, int units, int leds, ZoneLayout layout) =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Caps);

    private static ControllerDescriptor[] Four() =>
    [
        Chain("a", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("b", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("h2", "H2", 1, 24, ZoneLayout.Ring(24)),
        Chain("lcd", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
    ];

    private static Scene GroupScene(ControllerDescriptor[] c, string effect) =>
        new([Assignment.ForGroup("g", c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray(), effect, new EffectParams([new Rgb(255, 0, 0), new Rgb(0, 0, 255)], Speed: 0.5f))]);

    [Theory]
    [InlineData("Meteor", 200, "UiMode6 over 141 dots")]
    [InlineData("Stacking", 792, "UiMode7, fan-aligned Stacking over 132 source dots")]
    [InlineData("Twinkle", 200, "per-device TLMode29/H2Mode8")]
    public void Four_chain_group_publishes_the_vendor_plan_once(string effect, int frames, string variant)
    {
        var c = Four();
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers(c);
        loop.SetScene(GroupScene(c, effect));
        loop.LastLoopCompile!.Variants!.Single().Should().Contain(variant);
        var loops = c.Select(x =>
        {
            bus[x.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            return pending.Loop;
        }).ToArray();
        loops.Should().OnlyContain(l => l.Frames == frames);
        loops.Select(l => l.EffectIndex).Distinct().Should().ContainSingle("one shared EffectIndex for the group");
        if (effect == "Twinkle")
        {
            (loops[0].IntervalMs, loops[2].IntervalMs).Should().Be((55.0, 100.0), "the per-device exception keeps each body's own interval");
        }

        // ticks across colour/loop boundaries and an identical scene publish nothing
        for (var t = 0.0; t < 60; t += 0.5)
        {
            loop.RenderFrame(t, 0.5);
        }

        loop.SetScene(GroupScene(c, effect));
        c.Should().OnlyContain(x => !bus[x.Id]!.HasPendingLoop, "no republish without a content change");
    }

    [Fact]
    public void Generic_stacking_prefix_misses_chains_and_the_vendor_route_lights_all_four()
    {
        // DEBUG-SESSION-2 §1 item 6 diagnostic regression: generic Stacking (Reverse, speed 0.5) uploads a four-second time
        // prefix in which only the last chain (LCD) is ever lit. The LConnect policy now selects UiMode7 instead.
        var c = Four();
        var refs = c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray();
        var p = new EffectParams([new Rgb(255, 0, 0), new Rgb(0, 0, 255)], Speed: 0.5f, Direction: Direction.Reverse);
        bool[] LitZones(LoopPolicy policy, out string variant)
        {
            using var bus = new FrameBus();
            var loop = new RenderLoop(EffectRegistry.Default, bus);
            loop.SetControllers(c);
            loop.SetScene(new Scene([Assignment.ForGroup("g", refs, "Stacking", p) with { Policy = policy }]));
            variant = loop.LastLoopCompile!.Variants!.Single();
            return c.Select(x =>
            {
                bus[x.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
                return pending.Loop.Rgb.ToArray().Any(b => b != 0);
            }).ToArray();
        }

        var generic = LitZones(LoopPolicy.Generic, out var gv);
        gv.Should().Contain("generic (Generic)");
        generic.Should().Equal([false, false, false, true], "the generic prefix only reaches the LCD chain (known generic defect, kept visible)");

        LitZones(LoopPolicy.LConnect, out var vv).Should().Equal(true, true, true, true);
        vv.Should().Contain("UiMode7, fan-aligned Stacking over 132 source dots");
    }

    [Fact]
    public void Generic_fallback_reports_the_reason()
    {
        var c = Four();
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers(c);
        loop.SetScene(GroupScene(c, "Lottery"));
        loop.LastLoopCompile!.Variants!.Single().Should().Contain("Lottery generic (LConnect: no Quick/Sync UIEffect is established for Lottery");
    }
}
