using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// WP-13 (DEBUG-SESSION-3 §7): regression fixtures whose expected output is derived independently of the production bodies —
/// the literal ColorCycle pattern, the §4.3 onset table, the §5.1 Stacking oracle — plus whole-chain reversal and the Stacking
/// default palette.
/// </summary>
public class LConnectFixtureTests
{
    /// <summary>Full-brightness level of a 255 channel: 255 × 255 ≫ 8.</summary>
    private const byte Full = 254;

    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);

    private static LConnectZone[] Group(bool reverseTl120 = false) =>
        [new("TL", 3, 78), new("TL", 3, 78, reverseTl120), new("H2", 1, 24), new("TL", 3, 78)];

    private static EffectParams P(Rgb[] colors, Direction dir) => new(colors, 0.5f, 1f, dir);

    /// <summary>Width of a zone's logical slice (13 per TL fan, 24 for the H2 block) and its row count.</summary>
    private static (int Width, int Rows) Shape(LConnectZone z) => z.Family == "TL" ? (z.Fans * 13, 2) : (24, 1);

    /// <summary>Group dot of zone-local position p, as the vendor GetTL/GetH2 index with directions[i] 0 or 1.</summary>
    private static int GlobalDot(LConnectZone z, int start, int p) => z.ReverseChain ? start + Shape(z).Width - 1 - p : start + p;

    private static (byte R, byte G, byte B) Px(LConnectZoneLoop loop, LConnectZone z, int frame, int row, int p)
    {
        var o = ((frame * z.LedCount) + (row * Shape(z).Width) + p) * 3;
        return (loop.Rgb[o], loop.Rgb[o + 1], loop.Rgb[o + 2]);
    }

    private static int[] StartsOf(IReadOnlyList<LConnectZone> zones)
    {
        var starts = new int[zones.Count];
        for (var i = 1; i < zones.Count; i++)
        {
            starts[i] = starts[i - 1] + Shape(zones[i - 1]).Width;
        }

        return starts;
    }

    // ---- ColorCycle (§3) --------------------------------------------------------------------------------------------

    /// <summary>The literal 48-position UiColourCycle pattern: colour 1 ×6, black ×10, colour 2 ×6, black ×10, colour 3 ×6, black ×10.</summary>
    private static int PatternSlot(int q) => q switch
    {
        < 6 => 0,
        >= 16 and < 22 => 1,
        >= 32 and < 38 => 2,
        _ => -1,
    };

    /// <summary>§3.1: Reverse ON reads P[(f + d % 48) % 48]; Reverse OFF reads P[(f + 47 − d % 48) % 48].</summary>
    private static (byte, byte, byte) ColourCycleExpected(int d, int f, Direction dir, Rgb[] colors)
    {
        var q = dir == Direction.Reverse ? (f + (d % 48)) % 48 : (f + 47 - (d % 48)) % 48;
        var slot = PatternSlot(q);
        if (slot < 0 || slot >= colors.Length)
        {
            return (0, 0, 0);
        }

        var c = colors[slot];
        return ((byte)(c.R * 255 >> 8), (byte)(c.G * 255 >> 8), (byte)(c.B * 255 >> 8));
    }

    public static TheoryData<Direction, bool> Directions() => new()
    {
        { Direction.Forward, false },
        { Direction.Reverse, false },
        { Direction.Forward, true },
        { Direction.Reverse, true },
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public void Colour_cycle_matches_the_literal_pattern_on_every_dot_and_row(Direction dir, bool reverseTl120)
    {
        Rgb[] rgb = [Red, Green, Blue];
        var zones = Group(reverseTl120);
        var starts = StartsOf(zones);
        starts.Should().Equal(0, 39, 78, 102);
        var plan = LConnectLoops.TryPlan("ColorCycle", P(rgb, dir), rgb, zones)!;
        plan.Zones.Should().OnlyContain(z => z.Frames == 48);
        var counts = new int[141, 4];
        for (var z = 0; z < zones.Length; z++)
        {
            var (width, rows) = Shape(zones[z]);
            for (var f = 0; f < 48; f++)
            {
                for (var row = 0; row < rows; row++)
                {
                    for (var p = 0; p < width; p++)
                    {
                        var d = GlobalDot(zones[z], starts[z], p);
                        var expected = ColourCycleExpected(d, f, dir, rgb);
                        Px(plan.Zones[z], zones[z], f, row, p).Should().Be(expected, $"{dir} zone {z} row {row} pos {p} (dot {d}) frame {f}");
                        if (row == 0)
                        {
                            counts[d, expected switch { (Full, 0, 0) => 0, (0, Full, 0) => 1, (0, 0, Full) => 2, _ => 3 }]++;
                        }
                    }
                }
            }
        }

        for (var d = 0; d < 141; d++)
        {
            (counts[d, 0], counts[d, 1], counts[d, 2], counts[d, 3]).Should().Be((6, 6, 6, 30), $"dot {d}: six frames per colour, 30 black");
        }

        // boundary identities: the last dot of one slice and the first of the next are adjacent pattern phases
        var step = dir == Direction.Reverse ? 1 : -1;
        foreach (var (a, za, zb) in new[] { (38, 0, 1), (77, 1, 2), (101, 2, 3) })
        {
            // position of dot a (last of za) and a + 1 (first of zb); a reversed TL120 holds dot 39 at 38 and dot 77 at 0
            var pa = a - starts[za];
            var pb = 0;
            (pa, pb) = (reverseTl120 && za == 1 ? 0 : pa, reverseTl120 && zb == 1 ? 38 : pb);

            for (var f = 0; f < 48; f++)
            {
                Px(plan.Zones[za], zones[za], f, 0, pa).Should().Be(ColourCycleExpected(a, f, dir, rgb), $"dot {a}");
                Px(plan.Zones[zb], zones[zb], f, 0, pb).Should().Be(ColourCycleExpected(a + 1, f, dir, rgb), $"dot {a + 1}");
                Px(plan.Zones[zb], zones[zb], f, 0, pb).Should().Be(ColourCycleExpected(a, (f + step + 48) % 48, dir, rgb), $"dot {a + 1} leads dot {a} by one frame");
            }
        }

        for (var f = 0; f < 48; f++)
        {
            Px(plan.Zones[3], zones[3], f, 1, 38).Should().Be(ColourCycleExpected(140, f, dir, rgb), "dot 140 is the LCD chain's last position");
        }
    }

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Colour_cycle_with_one_colour_lights_six_frames_per_dot_and_explicit_black_stays_black(Direction dir)
    {
        var zones = Group();
        var one = LConnectLoops.TryPlan("ColorCycle", P([Green], dir), [Green], zones)!;
        for (var z = 0; z < 4; z++)
        {
            var (width, rows) = Shape(zones[z]);
            for (var row = 0; row < rows; row++)
            {
                for (var p = 0; p < width; p++)
                {
                    var lit = Enumerable.Range(0, 48).Select(f => Px(one.Zones[z], zones[z], f, row, p)).ToArray();
                    lit.Count(px => px == (0, Full, 0)).Should().Be(6, $"zone {z} row {row} pos {p}");
                    lit.Count(px => px == (0, 0, 0)).Should().Be(42);
                }
            }
        }

        var black = LConnectLoops.TryPlan("ColorCycle", P([new Rgb(0, 0, 0)], dir), [new Rgb(0, 0, 0)], zones)!;
        black.Zones.Should().OnlyContain(z => z.Rgb.All(b => b == 0), "explicit black is honoured, not replaced by a default");
    }

    // ---- Slicing markers ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reordered_members_slice_by_their_own_running_offsets(bool reverseChain)
    {
        // H2 first, a 2-fan TL, a 4-fan TL: starts 0, 24, 50 (total 102 dots); positions derived from the literal pattern
        LConnectZone[] zones = [new("H2", 1, 24, reverseChain), new("TL", 2, 52), new("TL", 4, 104, reverseChain)];
        var starts = StartsOf(zones);
        starts.Should().Equal(0, 24, 50);
        Rgb[] rgb = [Red, Green, Blue];
        foreach (var dir in new[] { Direction.Forward, Direction.Reverse })
        {
            var plan = LConnectLoops.TryPlan("ColorCycle", P(rgb, dir), rgb, zones)!;
            plan.Variant.Should().Be("L-Connect Quick/Sync UiMode9 over 102 dots");
            for (var z = 0; z < zones.Length; z++)
            {
                var (width, rows) = Shape(zones[z]);
                for (var f = 0; f < 48; f++)
                {
                    for (var row = 0; row < rows; row++)
                    {
                        for (var p = 0; p < width; p++)
                        {
                            Px(plan.Zones[z], zones[z], f, row, p).Should().Be(ColourCycleExpected(GlobalDot(zones[z], starts[z], p), f, dir, rgb), $"{dir} zone {z} row {row} pos {p} frame {f}");
                        }
                    }
                }
            }
        }
    }

    // ---- Meteor / CoverCycle onsets (§4.3) ----------------------------------------------------------------------------

    /// <summary>First zero-based frame in which a TL120 fan (zone 1, fan 0..2) shows colour 1 (red) on the given edge.</summary>
    private static int[] Onsets(string effect, Direction dir, bool reverseChain, int row)
    {
        Rgb[] rgb = [Red, Green];
        var zones = Group(reverseChain);
        var plan = LConnectLoops.TryPlan(effect, P(rgb, dir), rgb, zones)!;
        var z = plan.Zones[1];
        return Enumerable.Range(0, 3).Select(fan => Enumerable.Range(0, z.Frames)
            .First(f => Enumerable.Range(fan * 13, 13).Any(p => Px(z, zones[1], f, row, p) is { R: > 0, G: 0, B: 0 }))).ToArray();
    }

    [Theory]
    [InlineData("Meteor")]
    [InlineData("CoverCycle")]
    public void Tl120_fan_onsets_match_the_debug_session_table(string effect)
    {
        foreach (var row in new[] { 0, 1 })
        {
            Onsets(effect, Direction.Forward, false, row).Should().Equal([40, 53, 66], $"{effect} forward, edge {row}");
            Onsets(effect, Direction.Reverse, false, row).Should().Equal([90, 77, 64], $"{effect} effect Reverse, edge {row}");

            // whole-chain reversal (vendor directions[i] = 1) reverses fan-entry order
            Onsets(effect, Direction.Reverse, true, row).Should().Equal([64, 77, 90], $"{effect} effect Reverse + chain reversed, edge {row}");
            Onsets(effect, Direction.Forward, true, row).Should().Equal([66, 53, 40], $"{effect} forward + chain reversed, edge {row}");
        }
    }

    [Fact]
    public void Chain_reversal_touches_only_the_reversed_member_and_maps_fan_row_k_to_2_minus_fan_row_12_minus_k()
    {
        Rgb[] rgb = [Red, Green];
        var plain = LConnectLoops.TryPlan("Meteor", P(rgb, Direction.Forward), rgb, Group())!;
        var flipped = LConnectLoops.TryPlan("Meteor", P(rgb, Direction.Forward), rgb, Group(reverseTl120: true))!;
        flipped.Variant.Should().Be(plain.Variant);
        foreach (var z in new[] { 0, 2, 3 })
        {
            flipped.Zones[z].Rgb.Should().Equal(plain.Zones[z].Rgb, $"zone {z} is not reversed");
        }

        var tl = Group()[1];
        for (var f = 0; f < plain.Zones[1].Frames; f++)
        {
            for (var row = 0; row < 2; row++)
            {
                for (var fan = 0; fan < 3; fan++)
                {
                    for (var k = 0; k < 13; k++)
                    {
                        Px(flipped.Zones[1], tl, f, row, (fan * 13) + k).Should().Be(Px(plain.Zones[1], tl, f, row, ((2 - fan) * 13) + (12 - k)));
                    }
                }
            }
        }
    }

    [Fact]
    public void Chain_reversal_does_not_apply_to_per_device_or_single_target_bodies()
    {
        Rgb[] rgb = [Red, Green];
        var p = P(rgb, Direction.Forward);
        LConnectLoops.TryPlan("Meteor", p, rgb, [new LConnectZone("TL", 3, 78, true)])!.Zones[0].Rgb
            .Should().Equal(LConnectLoops.TryPlan("Meteor", p, rgb, [new LConnectZone("TL", 3, 78)])!.Zones[0].Rgb, "a single chain uses its TLMode body, not group slicing");
        var twinkle = LConnectLoops.TryPlan("Twinkle", p, rgb, Group(true))!;
        twinkle.Zones[1].Rgb.Should().Equal(LConnectLoops.TryPlan("Twinkle", p, rgb, Group())!.Zones[1].Rgb, "per-device exception");
    }

    // ---- Stacking (§5) ------------------------------------------------------------------------------------------------

    /// <summary>
    /// §5.1 oracle: pass p runs a 12-dot block whose head goes 0 .. L_p − 1 with L_p = n − 12p; completed passes stay stacked
    /// at the far end. Returns the lit forward dots of frame f of an n-dot run (WP-13 raw body: n = 141; WP-15 group: 132).
    /// </summary>
    private static HashSet<int> StackingLit(int f, int n = 141)
    {
        var lit = new HashSet<int>();
        var len = n;
        while (f >= len)
        {
            f -= len;
            len -= 12;
        }

        for (var d = len; d < n; d++)
        {
            lit.Add(d);
        }

        for (var d = Math.Max(0, f - 11); d <= f; d++)
        {
            lit.Add(d);
        }

        return lit;
    }

    [Fact]
    public void Stacking_oracle_reproduces_the_debug_session_checkpoints()
    {
        Enumerable.Range(0, 12).Sum(p => 141 - (12 * p)).Should().Be(900);
        StackingLit(0).Should().BeEquivalentTo([0]);
        StackingLit(140).Should().BeEquivalentTo(Enumerable.Range(129, 12));
        StackingLit(141).Should().BeEquivalentTo(Enumerable.Range(129, 12).Append(0));
        StackingLit(269).Should().BeEquivalentTo(Enumerable.Range(117, 24));
        StackingLit(899).Should().HaveCount(141);
    }

    /// <summary>WP-13 raw-body fidelity, kept for WP-15: the unchanged UiMode7 over 141 dots against the 900-frame oracle.</summary>
    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Raw_uimode7_matches_the_independent_900_frame_oracle(Direction dir)
    {
        var raw = LConnectLoops.UiModeFrames(7, 141, 255, LConnectLoops.VendorDirection(dir), UiRed(), out _)!;
        raw.Should().HaveCount(900);
        var boundaries = new List<int>();
        var start = 0;
        for (var len = 141; len > 0; len -= 12)
        {
            boundaries.Add(start);
            start += len;
        }

        boundaries.Should().Equal(0, 141, 270, 387, 492, 585, 666, 735, 792, 837, 870, 891);
        for (var f = 0; f < 900; f++)
        {
            var lit = StackingLit(f);
            if (dir == Direction.Reverse)
            {
                lit = lit.Select(d => 140 - d).ToHashSet();
            }

            for (var d = 0; d < 141; d++)
            {
                (raw[f][d * 3], raw[f][(d * 3) + 1], raw[f][(d * 3) + 2]).Should().Be(lit.Contains(d) ? (Full, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0), $"{dir} frame {f} dot {d}");
            }
        }
    }

    /// <summary>
    /// WP-15 planner oracle: the group runs 132 source dots (12 per TL fan, 24 for the H2); TL column k of a fan shows source
    /// fan * 12 + the literal per-fan expansion below on both rows; the H2 takes its 24 dots contiguously.
    /// </summary>
    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Stacking_matches_the_independent_792_frame_fan_aligned_oracle_on_every_slice(Direction dir)
    {
        int[] expand = [0, 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10, 11];
        int[] sourceStarts = [0, 36, 72, 96];
        var zones = Group();
        var plan = LConnectLoops.TryPlan("Stacking", P([Red], dir), [Red], zones)!;
        plan.Zones.Should().OnlyContain(z => z.Frames == 792);
        var boundaries = new List<int>();
        var start = 0;
        for (var len = 132; len > 0; len -= 12)
        {
            boundaries.Add(start);
            start += len;
        }

        boundaries.Should().Equal(0, 132, 252, 360, 456, 540, 612, 672, 720, 756, 780);
        start.Should().Be(792);
        for (var f = 0; f < 792; f++)
        {
            var lit = StackingLit(f, 132);
            if (dir == Direction.Reverse)
            {
                lit = lit.Select(d => 131 - d).ToHashSet();
            }

            for (var z = 0; z < 4; z++)
            {
                var (width, rows) = Shape(zones[z]);
                for (var row = 0; row < rows; row++)
                {
                    for (var p = 0; p < width; p++)
                    {
                        var d = zones[z].Family == "TL" ? sourceStarts[z] + ((p / 13) * 12) + expand[p % 13] : sourceStarts[z] + p;
                        Px(plan.Zones[z], zones[z], f, row, p).Should().Be(lit.Contains(d) ? (Full, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0), $"{dir} frame {f} zone {z} row {row} source {d}");
                    }
                }
            }
        }

        // restart: the full stack of frame 791 (every physical position) is followed by the single travelling source dot of frame 0
        Enumerable.Range(0, 4).Sum(z => Enumerable.Range(0, Shape(zones[z]).Width).Count(p => Px(plan.Zones[z], zones[z], 791, 0, p) != (0, 0, 0))).Should().Be(141);
        Enumerable.Range(0, 4).Sum(z => Enumerable.Range(0, Shape(zones[z]).Width).Count(p => Px(plan.Zones[z], zones[z], 0, 0, p) != (0, 0, 0))).Should().Be(1);
    }

    private static byte[,] UiRed()
    {
        var ui = new byte[6, 3];
        var c = LConnectLoops.CapColour(Red, LConnectLoops.RgbMax);
        (ui[0, 0], ui[0, 1], ui[0, 2]) = (c.R, c.G, c.B);
        return ui;
    }

    [Fact]
    public void Stacking_unused_palette_rows_add_no_passes()
    {
        var zones = Group();
        var one = LConnectLoops.TryPlan("Stacking", P([Red], Direction.Forward), [Red], zones)!;
        var three = LConnectLoops.TryPlan("Stacking", P([Red, Green, Blue], Direction.Forward), [Red, Green, Blue], zones)!;
        for (var z = 0; z < 4; z++)
        {
            three.Zones[z].Frames.Should().Be(792);
            three.Zones[z].Rgb.Should().Equal(one.Zones[z].Rgb, "only palette row 0 is read");
        }
    }

    [Fact]
    public void Stacking_default_palette_is_one_editable_non_black_colour()
    {
        var schema = new StackingEffect().Schema;
        schema.MinColors.Should().Be(1);
        schema.DefaultColors.Should().NotBeEmpty();
        schema.DefaultColors[0].Should().NotBe(new Rgb(0, 0, 0));
        var plan = LConnectLoops.TryPlan("Stacking", new EffectParams([], 0.5f), schema.DefaultColors, Group())!;
        plan.Zones.Should().OnlyContain(z => z.Rgb.Any(b => b != 0), "the default selection lights every chain");

        var black = LConnectLoops.TryPlan("Stacking", P([new Rgb(0, 0, 0)], Direction.Forward), [new Rgb(0, 0, 0)], Group())!;
        black.Zones.Should().OnlyContain(z => z.Rgb.All(b => b == 0), "explicit black stays black");
    }

    [Fact]
    public void Restored_empty_stacking_assignment_resolves_to_the_non_black_default()
    {
        var c = new[]
        {
            Chain("a", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("b", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("h2", "H2", 1, 24, ZoneLayout.Ring(24)),
            Chain("lcd", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        };
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers(c);
        loop.SetScene(new Scene([Assignment.ForGroup("g", c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray(), "Stacking", new EffectParams([], 0.5f))]));
        loop.LastLoopCompile!.Variants!.Single().Should().Contain("UiMode7, fan-aligned Stacking over 132 source dots");
        foreach (var x in c)
        {
            bus[x.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            pending.Loop.Frames.Should().Be(792);
            pending.Loop.Rgb.ToArray().Should().Contain(b => b != 0, $"{x.Id} is lit by the default colour");
        }
    }

    [Fact]
    public void Render_loop_slices_a_reversed_chain_from_its_provider()
    {
        var c = new[]
        {
            Chain("a", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("b", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("h2", "H2", 1, 24, ZoneLayout.Ring(24)),
            Chain("lcd", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        };
        var refs = c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray();
        var reversed = false;
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus) { ChainReversed = id => reversed && id.Value == "b" };
        loop.SetControllers(c);
        Rgb[] rgb = [Red, Green];
        loop.SetScene(new Scene([Assignment.ForGroup("g", refs, "Meteor", P(rgb, Direction.Forward))]));
        bus[c[1].Id]!.TryConsumeLoop(out var before).Should().BeTrue();

        reversed = true;
        loop.RepublishLoops(c[1].Id).Should().NotBeNull();
        bus[c[1].Id]!.TryConsumeLoop(out var after).Should().BeTrue();
        var expected = LConnectLoops.TryPlan("Meteor", P(rgb, Direction.Forward), rgb, Group(reverseTl120: true))!.Zones[1].Rgb;
        after.Loop.Rgb.ToArray().Should().Equal(expected, "the provider's reversal reaches the slicing stage");
        after.Loop.Rgb.ToArray().Should().NotEqual(before.Loop.Rgb.ToArray());
    }

    private static ControllerDescriptor Chain(string id, string family, int units, int leds, ZoneLayout layout) =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));
}
