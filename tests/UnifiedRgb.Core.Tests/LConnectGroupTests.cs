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
    [InlineData("Rainbow", 1, 24, 60.0)]
    [InlineData("StaticColor", 3, 1, 60.0)]
    [InlineData("Breathing", 4, 170, 60.0)]
    [InlineData("Runway", 5, 304, 60.0)]
    [InlineData("Meteor", 6, 153, 60.0)]
    [InlineData("ColorCycle", 9, 48, 60.0)]
    [InlineData("CoverCycle", 10, 282, 60.0)]
    [InlineData("Wave", 11, 24, 60.0)]
    [InlineData("MeteorShower", 12, 48, 60.0)]
    [InlineData("Tide", 13, 142, 60.0)]
    [InlineData("MopUp", 15, 596, 60.0)]
    [InlineData("Disco", 16, 12, 60.0)]
    [InlineData("Warning", 22, 148, 60.0)]
    [InlineData("Echo", 26, 179, 60.0)]
    [InlineData("HeartBeat", 27, 400, 90.0)]
    [InlineData("Ripple", 30, 294, 90.0)]
    public void Four_chain_group_parity_per_shared_route(string effect, int ui, int frames, double interval)
    {
        var plan = LConnectLoops.TryPlan(effect, P(), Four, Group, out var why);
        plan.Should().NotBeNull(why);
        LConnectLoops.GroupRoutes[effect].Should().Be(ui);
        plan!.Variant.Should().Be($"L-Connect Quick/Sync UiMode{ui} over 141 dots");
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
        plan!.Variant.Should().Be("L-Connect Quick/Sync UiMode7, fan-aligned Stacking over 132 source dots");
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
        var expected = new Dictionary<string, int> { ["Runway"] = 304, ["Meteor"] = 153, ["ColorCycle"] = 48, ["Stacking"] = 792, ["CoverCycle"] = 282, ["Wave"] = 24 };
        foreach (var (effect, frames) in expected)
        {
            LConnectLoops.TryPlan(effect, P(), Four, Group)!.Zones.Should().OnlyContain(z => z.Frames == frames && z.IntervalMs == 60.0, effect);
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
    public void Runway_marker_reaches_each_zone_start_at_its_slice_offset_and_follows_member_order()
    {
        // UiMode5: the colour-1 bar's head enters dot k at frame k, so each zone's first LED first turns red at its start dot
        static int[] FirstRed(LConnectPlan plan) => plan.Zones.Select(z =>
        {
            var leds = z.Rgb.Length / 3 / z.Frames;
            return Enumerable.Range(0, z.Frames).First(f => z.Rgb[f * leds * 3] == 254 && z.Rgb[(f * leds * 3) + 1] == 0);
        }).ToArray();

        FirstRed(LConnectLoops.TryPlan("Runway", P(), Four, Group)!).Should().Equal(0, 39, 78, 102);
        LConnectZone[] reordered = [Group[2], Group[0], Group[1], Group[3]];
        FirstRed(LConnectLoops.TryPlan("Runway", P(), Four, reordered)!).Should().Equal(0, 24, 63, 102);
    }

    [Fact]
    public void Runway_sweeps_forward_then_back_and_meteor_tail_and_direction_follow_the_il()
    {
        var run = LConnectLoops.UiModeFrames(5, 141, 255, 0, Ui(Four), out _)!;
        run.Should().HaveCount(2 * (141 + 11));
        Lit(run[11], 141, 255, 0, 0).Should().Equal(Enumerable.Range(0, 12), "bar = 12 dots ending at the head");
        Lit(run[152 + 11], 141, 255, 0, 0).Should().Equal(Enumerable.Range(129, 12), "second pass runs from the far end");
        Lit(run[20], 141, 0, 255, 0).Should().HaveCount(141 - 12, "the rest shows colour 2");

        var fwd = LConnectLoops.UiModeFrames(6, 141, 255, 0, Ui(Four), out _)!;
        var rev = LConnectLoops.UiModeFrames(6, 141, 255, 1, Ui(Four), out _)!;
        fwd.Should().HaveCount(153);
        var levels = new[] { 12, 16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 255 }.Select(t => (255 * t >> 8) * 255 >> 8).ToHashSet();
        fwd.Sum(f => f.Chunk(3).Count(c => c[0] != 0)).Should().BeGreaterThan(0);
        for (var f = 0; f < fwd.Count; f++)
        {
            fwd[f].Chunk(3).Select(c => (int)c[0]).Where(r => r != 0).All(levels.Contains).Should().BeTrue($"frame {f}: only tail levels (colour 1 × tail ≫ 8, × 255 ≫ 8)");
            fwd[f].Chunk(3).Should().OnlyContain(c => c[1] == 0 && c[2] == 0);
            fwd[f].Chunk(3).Reverse().SelectMany(c => c).ToArray().Should().Equal(rev[f], "iDir 1 is the spatial mirror");
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(8, 1)]
    public void Runway_and_meteor_frame_counts_follow_the_group_size(int tlFans, int h2)
    {
        // several TL chains (≤ 4 fans each) plus optional H2 blocks; D = 13 per fan + 24 per block
        var zones = new List<LConnectZone>();
        for (var left = tlFans; left > 0; left -= 4)
        {
            var fans = Math.Min(4, left);
            zones.Add(new LConnectZone("TL", fans, fans * 26));
        }

        for (var i = 0; i < h2; i++)
        {
            zones.Add(new LConnectZone("H2", 1, 24));
        }

        if (zones.Count < 2)
        {
            zones.Add(new LConnectZone("TL", 1, 26));
            tlFans++;
        }

        var dots = (tlFans * 13) + (h2 * 24);
        LConnectLoops.TryPlan("Runway", P(), Four, zones)!.Zones.Should().OnlyContain(z => z.Frames == 2 * (dots + 11));
        LConnectLoops.TryPlan("Meteor", P(), Four, zones)!.Zones.Should().OnlyContain(z => z.Frames == dots + 12);

        // Meteor wraps: the head returns to dot 0 after dots + 12 frames (the loop closes without a seam)
        var raw = LConnectLoops.UiModeFrames(6, dots, 255, 0, Ui(Four), out _)!;
        raw.Count.Should().Be(dots + 12);
        raw.Select(f => f.Chunk(3).Count(c => c[0] != 0)).Should().Contain(12, "the full 12-dot tail is on the strip at some frame");
    }

    [Fact]
    public void Tl_twinkle_pure_colours_on_both_edges_follow_the_table()
    {
        var table = Resource("tl-twinkle.bin");
        Rgb[] pure = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0)];
        foreach (var bright in new[] { 64, 192, 255 })
        {
            var frames = LConnectLoops.TlFrames(29, 2, bright, 0, pure)!;
            for (var f = 0; f < 200; f++)
            {
                for (var row = 0; row < 2; row++)
                {
                    for (var col = 0; col < 26; col++)
                    {
                        var wire = ((col / 13) * 26) + (row * 13) + (col % 13);
                        var level = (byte)(255 * table[(f * 174) + wire] >> 8) * bright >> 8;
                        var o = ((row * 26) + col) * 3;
                        var px = frames[f].AsSpan(o, 3).ToArray();
                        px.Should().OnlyContain(v => v == 0 || v == level, $"bright {bright} frame {f} row {row} col {col}: pure channel × table ≫ 8 × bright ≫ 8");
                        px.Count(v => v != 0).Should().BeLessThanOrEqualTo(2);
                    }
                }
            }
        }
    }

    [Fact]
    public void Colour_cycle_hard_cuts_between_palette_colours_and_black()
    {
        var plan = LConnectLoops.TryPlan("ColorCycle", P(), Four, Group)!;
        var allowed = new HashSet<(byte, byte, byte)> { (0, 0, 0), (254, 0, 0), (0, 254, 0), (0, 0, 254) };
        plan.Zones.SelectMany(z => z.Rgb.Chunk(3)).Select(c => (c[0], c[1], c[2])).Should().OnlyContain(c => allowed.Contains(c), "no blends: palette or black");

        var tl = LConnectLoops.TryPlan("ColorCycle", P(), Four, [new LConnectZone("TL", 3, 78)])!;
        tl.Variant.Should().Contain("TLMode7");
        tl.Zones[0].Frames.Should().Be(39);
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
        plan.Variant.Should().Be("L-Connect Quick/Sync UIEffect 8 (per-device TLMode29/H2Mode8)");
        (plan.Zones[0].Frames, plan.Zones[0].IntervalMs).Should().Be((200, 55.0));
        (plan.Zones[2].Frames, plan.Zones[2].IntervalMs).Should().Be((200, 100.0));
        plan.Zones[2].Rgb.Length.Should().Be(200 * 24 * 3);
        plan.Synchronous.Should().BeFalse();
        plan.PeriodSeconds.Should().BeApproximately(220, 1e-9, "lcm(11 s, 20 s)");
        plan.Summary.Should().Contain("zone 0: 200 × 55 ms = 11 s").And.Contain("zone 2: 200 × 100 ms = 20 s").And.Contain("repeats after 220 s");

        var morph = LConnectLoops.TryPlan("RainbowMorph", P(), Four, Group)!;
        morph.Variant.Should().Contain("UIEffect 2 (per-device TLMode2/H2Mode2)");
        morph.Zones.Should().OnlyContain(z => z.Frames == 127 && z.IntervalMs == 55.0);
        morph.Synchronous.Should().BeTrue();
    }

    [Theory]
    [InlineData("tl-twinkle.bin", "private static byte[,] TwinkleArr = new byte[200, 174]", null, 34800, "a4965538555254a7c98b13b876d64e60be3d715d1b5077a0ac01bb19ff714c57")]
    [InlineData("h2-twinkle.bin", "byte[,] array4 = new byte[200, 24]", "public int H2Mode8(int bright, int direction)", 4800, "e046a91cf21cbf926242b7b7d6bc76d6f9354bb6be841d49b0d768473e7b0d2c")]
    [InlineData("ui-ripple.bin", "byte[,] array5 = new byte[34, 48]", "public static Dictionary<int, byte[,,]> UiMode30(", 1632, "00d275c8df78e004633ae7b62ec671fe6febd72fd52ecab1e9fe5492c4875535")]
    public void Embedded_tables_match_the_pinned_reference_hashes(string name, string decl, string? method, int length, string sha)
    {
        using var s = typeof(LConnectLoops).Assembly.GetManifestResourceStream($"UnifiedRgb.Core.{name}")!;
        s.Should().NotBeNull();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        bytes.Should().HaveCount(length);
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(sha);

        var reference = Path.Combine(TestSupport.RepoRoot(), "analysis", "il", "il-RgbEffect.cs");
        if (!File.Exists(reference))
        {
            Console.WriteLine("Pinned embedded length/SHA verified. Additional private IL provenance comparison is unavailable.");
            return;
        }

        var il = File.ReadAllText(reference);
        var start = il.IndexOf(decl, method is null ? 0 : il.IndexOf(method, StringComparison.Ordinal), StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var open = il.IndexOf('{', start);
        var (depth, i) = (0, open);
        do
        {
            depth += il[i] == '{' ? 1 : il[i] == '}' ? -1 : 0;
            i++;
        }
        while (depth > 0);
        Regex.Matches(il[open..i], @"\d+").Select(m => byte.Parse(m.Value)).ToArray().Should().Equal(bytes, "the asset is the IL table, byte for byte");
    }

    [Theory]
    [InlineData(195, 0, 255, 0)]
    [InlineData(195, 5, 255, 2)]
    [InlineData(195, 255, 255, 193)]
    [InlineData(195, 255, 192, 145)]
    [InlineData(195, 255, 128, 97)]
    [InlineData(195, 255, 64, 48)]
    [InlineData(195, 255, 0, 0)]
    [InlineData(255, 255, 255, 253)]
    [InlineData(255, 5, 128, 2)]
    public void Twinkle_truncates_twice_like_the_il(int colour, int intensity, int bright, int expected) =>
        ((byte)(colour * intensity >> 8) * bright >> 8).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(255)]
    public void Twinkle_bodies_apply_the_two_shifts_to_the_table(int bright)
    {
        LConnectLoops.CapColour(Rgb.White, LConnectLoops.RgbMax).Should().Be(new Rgb(195, 195, 195), "TL caps white at 600");
        LConnectLoops.CapColour(Rgb.White, LConnectLoops.H2RgbMax).Should().Be(new Rgb(185, 185, 185), "H2 caps white at 570");

        var table = Resource("tl-twinkle.bin");
        var tl = LConnectLoops.TlFrames(29, 1, bright, 0, [Rgb.White, Rgb.White, Rgb.White, Rgb.White])!;
        tl.Should().HaveCount(200);
        for (var f = 0; f < 200; f++)
        {
            for (var col = 0; col < 13; col++)
            {
                // fan 0 Up edge: wire LEDs 0..12 (layout row 0)
                var t = table[(f * 174) + col];
                tl[f][col * 3].Should().Be((byte)((byte)(195 * t >> 8) * bright >> 8), $"frame {f} LED {col}");
            }
        }

        var h2Table = Resource("h2-twinkle.bin");
        var h2 = LConnectLoops.H2Twinkle(bright);
        h2.Should().HaveCount(200).And.OnlyContain(f => f.Length == 72);
        for (var f = 0; f < 200; f++)
        {
            var lit = Enumerable.Range(0, 24).Max(j => (int)h2Table[(f * 24) + j]);
            h2[f].Max().Should().Be((byte)((byte)(255 * lit >> 8) * bright >> 8), $"H2 frame {f}: fixed palette (255) × table ≫ 8 × bright ≫ 8");
        }
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
        wave.Should().Contain("WitmodLightings");
        LConnectLoops.TryPlan("Wave", P(), Four, [new LConnectZone("H2", 1, 24)], out var h2).Should().BeNull();
        h2.Should().Contain("H2 has no Wave body");
    }

    [Fact]
    public void Ui_bodies_refuse_what_the_vendor_would_not_produce()
    {
        LConnectLoops.UiModeFrames(5, 23, 255, 0, Ui(Four), out var small).Should().BeNull();
        small.Should().Contain("24..720");
        LConnectLoops.UiModeFrames(27, 521, 255, 0, Ui(Four), out var beat).Should().BeNull();
        beat.Should().Contain("24..520");
        LConnectLoops.UiModeFrames(27, 520, 255, 0, Ui(Four), out _).Should().HaveCount(400);
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
    private static byte[,] Ui(IReadOnlyList<Rgb> colors)
    {
        var ui = new byte[6, 3];
        for (var i = 0; i < colors.Count && i < 6; i++)
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
    [InlineData("Meteor", 153, "UiMode6 over 141 dots")]
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
