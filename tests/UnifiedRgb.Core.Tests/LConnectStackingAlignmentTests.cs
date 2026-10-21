using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// WP-15 (WORKORDER-STACKING-FIX): shared Stacking runs the unchanged UiMode7 body over 12 source dots per TL fan and expands
/// each fan's 12 dots onto its 13 columns, so every retained deposit fills one whole fan. Expected coordinates are derived
/// here independently (literal expansion table, explicit pass boundaries), never through the production placement helper.
/// </summary>
public class LConnectStackingAlignmentTests
{
    private const byte Full = 254;

    private static readonly Rgb Red = new(255, 0, 0);

    /// <summary>The literal per-fan expansion of 12 source dots onto 13 TL columns.</summary>
    private static readonly int[] Expand = [0, 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10, 11];

    /// <summary>
    /// The reported group order: bottom TL140 LCD (identity), vertical TL140 (identity), top TL120 (ReverseChain), H2 with
    /// FaceOnFill (its saved rotation 180 / mirror / offset 0 is a wire map, not part of the plan; HalfStep off).
    /// </summary>
    private static LConnectZone[] Reported() =>
        [new("TL", 3, 78), new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true)];

    private static EffectParams P(Direction dir, float speed = 0.5f) => new([Red], speed, 1f, dir);

    private static byte[,] UiRed()
    {
        var ui = new byte[6, 3];
        var c = LConnectLoops.CapColour(Red, LConnectLoops.RgbMax);
        (ui[0, 0], ui[0, 1], ui[0, 2]) = (c.R, c.G, c.B);
        return ui;
    }

    private static IReadOnlyList<byte[]> Raw(int dots, Direction dir) =>
        LConnectLoops.UiModeFrames(7, dots, 255, LConnectLoops.VendorDirection(dir), UiRed(), out _)!;

    private static int Width(LConnectZone z) => z.Family == "TL" ? z.Fans * 13 : 24;

    private static int Rows(LConnectZone z) => z.Family == "TL" ? 2 : 1;

    private static bool Lit(byte[] rgb, LConnectZone z, int frame, int row, int col)
    {
        var o = ((frame * z.LedCount) + (row * Width(z)) + col) * 3;
        return rgb[o] != 0 || rgb[o + 1] != 0 || rgb[o + 2] != 0;
    }

    /// <summary>Lit LEDs (both rows) of physical fan <paramref name="fan"/> (columns 13·fan .. 13·fan + 12) in a frame.</summary>
    private static int FanLit(byte[] rgb, LConnectZone z, int frame, int fan) =>
        Enumerable.Range(0, 2).Sum(row => Enumerable.Range(fan * 13, 13).Count(col => Lit(rgb, z, frame, row, col)));

    /// <summary>Last frame of every pass of an n-dot UiMode7 run: pass p is n − 12p frames long.</summary>
    private static int[] PassEnds(int n)
    {
        var ends = new List<int>();
        var at = 0;
        for (var len = n; len > 0; len -= 12)
        {
            at += len;
            ends.Add(at - 1);
        }

        return [.. ends];
    }

    // ---------------------------------------------------------------- 1. the reported defect

    [Fact]
    public void Legacy_141_dot_slicing_straddles_the_vertical_and_top_chains_at_frame_734()
    {
        // the old plan: raw body over 13 dots per fan, contiguous slices 0/39/78/117 (TL120 read reversed), Reverse effect
        var raw = Raw(141, Direction.Reverse);
        raw.Should().HaveCount(900);
        PassEnds(141)[6].Should().Be(734, "frame 734 ends the seventh pass");
        int[] starts = [0, 39, 78, 117];
        var zones = new[] { new LConnectZone("TL", 3, 78), new LConnectZone("TL", 3, 78), new LConnectZone("TL", 3, 78, true) };
        int LitLeds(int z)
        {
            var count = 0;
            for (var p = 0; p < 39; p++)
            {
                var d = zones[z].ReverseChain ? starts[z] + 38 - p : starts[z] + p;
                count += raw[734][d * 3] != 0 ? 2 : 0; // both TL rows show the dot
            }

            return count;
        }

        LitLeds(1).Should().Be(78, "the vertical TL140 is full");
        LitLeds(2).Should().Be(12, "six dots of the seventh deposit spill onto the top TL120");
    }

    // ---------------------------------------------------------------- 2. the corrected reported group

    [Theory]
    [InlineData(Direction.Reverse)]
    [InlineData(Direction.Forward)]
    public void Every_completed_pass_leaves_whole_tl_fans_and_adds_exactly_one(Direction dir)
    {
        var zones = Reported();
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones)!;
        var ends = PassEnds(132);
        ends.Should().Equal(131, 251, 359, 455, 539, 611, 671, 719, 755, 779, 791);
        var tls = Enumerable.Range(0, 3).SelectMany(z => Enumerable.Range(0, 3).Select(fan => (z, fan))).ToArray();
        var previous = new HashSet<(int, int)>();
        for (var pass = 0; pass < ends.Length; pass++)
        {
            var f = ends[pass];
            var full = new HashSet<(int, int)>();
            foreach (var (z, fan) in tls)
            {
                var lit = FanLit(plan.Zones[z].Rgb, zones[z], f, fan);
                lit.Should().BeOneOf([0, 26], $"{dir} pass {pass} frame {f}: zone {z} fan {fan} is fully dark or fully lit");
                if (lit == 26)
                {
                    full.Add((z, fan));
                }
            }

            full.IsSupersetOf(previous).Should().BeTrue($"{dir} pass {pass}: retained fans stay lit");
            var added = full.Except(previous).ToArray();
            // Reverse stacks from source 0 (bottom LCD first, H2 last); Forward from source 131 (H2's two passes first, then
            // TL120 source fans 2, 1, 0, the vertical TL140, the LCD)
            var k = dir == Direction.Reverse ? pass : pass - 2;
            if (k is < 0 or > 8)
            {
                added.Should().BeEmpty($"{dir} pass {pass} is an H2 deposit");
            }
            else
            {
                var zone = dir == Direction.Reverse ? k / 3 : 2 - (k / 3);
                var sourceFan = dir == Direction.Reverse ? k % 3 : 2 - (k % 3);
                var physical = zones[zone].ReverseChain ? 2 - sourceFan : sourceFan;
                added.Should().Equal([(zone, physical)], $"{dir} pass {pass}: exactly one new TL fan");
            }

            previous = full;
        }

        // the scene ends completely lit in either direction
        foreach (var (z, zone) in zones.Select((zone, z) => (z, zone)))
        {
            for (var row = 0; row < Rows(zone); row++)
            {
                Enumerable.Range(0, Width(zone)).Should().OnlyContain(c => Lit(plan.Zones[z].Rgb, zone, 791, row, c), $"{dir} zone {z} row {row} ends lit");
            }
        }
    }

    [Fact]
    public void Reverse_hands_off_from_the_full_vertical_tl140_to_the_rightmost_top_tl120_fan()
    {
        var zones = Reported();
        var plan = LConnectLoops.TryPlan("Stacking", P(Direction.Reverse), [Red], zones)!;

        // frame 611 ends pass 5: LCD and vertical TL140 full, top TL120 dark
        Enumerable.Range(0, 3).Select(f => FanLit(plan.Zones[1].Rgb, zones[1], 611, f)).Should().Equal(26, 26, 26);
        Enumerable.Range(0, 3).Select(f => FanLit(plan.Zones[2].Rgb, zones[2], 611, f)).Should().Equal(0, 0, 0);

        // frame 671 ends pass 6: exactly the TL120's rightmost physical fan (columns 26..38, both rows) is retained
        Enumerable.Range(0, 3).Select(f => FanLit(plan.Zones[2].Rgb, zones[2], 671, f)).Should().Equal(0, 0, 26);
        for (var row = 0; row < 2; row++)
        {
            Enumerable.Range(0, 39).Where(c => Lit(plan.Zones[2].Rgb, zones[2], 671, row, c)).Should().Equal(Enumerable.Range(26, 13), $"row {row}");
        }

        Enumerable.Range(0, 3).Select(f => FanLit(plan.Zones[1].Rgb, zones[1], 671, f)).Should().Equal([26, 26, 26], "the vertical TL140 stays full");
    }

    // ---------------------------------------------------------------- 3. directions, ReverseChain, 1..4 fans

    public static TheoryData<Direction, int> DirectionsAndZones()
    {
        var data = new TheoryData<Direction, int>();
        foreach (var dir in new[] { Direction.Forward, Direction.Reverse })
        {
            for (var z = 0; z < 4; z++)
            {
                data.Add(dir, z);
            }
        }

        return data;
    }

    private static LConnectZone[] Mixed(int reversed = -1) =>
        Enumerable.Range(1, 4).Select(fans => new LConnectZone("TL", fans, fans * 26, fans - 1 == reversed)).ToArray();

    [Theory]
    [MemberData(nameof(DirectionsAndZones))]
    public void Reverse_chain_is_the_physical_row_reversal_and_leaves_other_zones_equal(Direction dir, int reversed)
    {
        var normal = LConnectLoops.TryPlan("Stacking", P(dir), [Red], Mixed())!;
        var flipped = LConnectLoops.TryPlan("Stacking", P(dir), [Red], Mixed(reversed))!;
        normal.Variant.Should().Be("UnifiedRgb uploaded UiMode7, fan-aligned Stacking over 120 source dots");
        var zones = Mixed();
        for (var z = 0; z < 4; z++)
        {
            if (z != reversed)
            {
                flipped.Zones[z].Rgb.Should().Equal(normal.Zones[z].Rgb, $"zone {z} is untouched");
                continue;
            }

            var zone = zones[z];
            var w = Width(zone);
            for (var f = 0; f < normal.Zones[z].Frames; f++)
            {
                for (var row = 0; row < 2; row++)
                {
                    for (var c = 0; c < w; c++)
                    {
                        var a = (((f * zone.LedCount) + (row * w) + c) * 3);
                        var b = (((f * zone.LedCount) + (row * w) + (w - 1 - c)) * 3);
                        flipped.Zones[z].Rgb.AsSpan(a, 3).ToArray().Should().Equal(normal.Zones[z].Rgb.AsSpan(b, 3).ToArray(), $"{dir} zone {z} frame {f} row {row} col {c}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(Direction.Forward, -1)]
    [InlineData(Direction.Reverse, -1)]
    [InlineData(Direction.Forward, 2)]
    [InlineData(Direction.Reverse, 3)]
    public void Mixed_fan_counts_retain_only_whole_fans(Direction dir, int reversed)
    {
        var zones = Mixed(reversed);
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones)!;
        var ends = PassEnds(120);
        var total = ends[^1] + 1;
        plan.Zones.Should().OnlyContain(z => z.Frames == total);
        (ends.Length, ends[^1] + 1).Should().Be((10, 660), "10 fans, 10 deposits");
        var previous = 0;
        foreach (var (f, pass) in ends.Select((f, pass) => (f, pass)))
        {
            var full = 0;
            for (var z = 0; z < 4; z++)
            {
                for (var fan = 0; fan < zones[z].Fans; fan++)
                {
                    var lit = FanLit(plan.Zones[z].Rgb, zones[z], f, fan);
                    lit.Should().BeOneOf([0, 26], $"{dir} pass {pass} zone {z} fan {fan}");
                    full += lit == 26 ? 1 : 0;
                }
            }

            full.Should().Be(previous + 1, $"{dir} pass {pass} retains one more fan");
            previous = full;
        }
    }

    // ---------------------------------------------------------------- 4. full arrays vs an independent expansion

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Adapted_arrays_equal_an_independent_expansion_of_uimode7_132(Direction dir)
    {
        var zones = Reported();
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones)!;
        var raw = Raw(132, dir);
        raw.Should().HaveCount(792);
        int[] starts = [0, 36, 72, 108];
        for (var z = 0; z < 4; z++)
        {
            var zone = zones[z];
            var w = Width(zone);
            var expected = new byte[raw.Count * zone.LedCount * 3];
            for (var f = 0; f < raw.Count; f++)
            {
                for (var row = 0; row < Rows(zone); row++)
                {
                    for (var c = 0; c < w; c++)
                    {
                        int d;
                        if (zone.Family == "TL")
                        {
                            var q = zone.ReverseChain ? w - 1 - c : c;
                            d = starts[z] + ((q / 13) * 12) + Expand[q % 13];
                        }
                        else
                        {
                            d = starts[z] + c;
                        }

                        raw[f].AsSpan(d * 3, 3).CopyTo(expected.AsSpan(((f * zone.LedCount) + (row * w) + c) * 3));
                    }
                }
            }

            if (zone.Family == "H2")
            {
                // the ring fill on the contiguous 24-dot slice (Stacking's bottom end is High); WP-16: interpolated, by the
                // test's own continuous-height oracle
                expected = LinearFillOracle.Frames(expected, high: true, halfStep: false);
            }

            plan.Zones[z].Rgb.Length.Should().Be(792 * zone.LedCount * 3, $"zone {z}");
            plan.Zones[z].Rgb.Should().Equal(expected, $"{dir} zone {z}");
        }

        raw.Should().OnlyContain(fr => fr.Length >= 132 * 3, "every source index read is in bounds");
        plan.Zones[0].Rgb.Chunk(3).Should().OnlyContain(c => (c[0] == 0 || c[0] == Full) && c[1] == 0 && c[2] == 0);
    }

    // ---------------------------------------------------------------- 5. timing, variant, summary

    [Fact]
    public void All_zones_share_792_frames_at_the_unchanged_speed_interval()
    {
        var plan = LConnectLoops.TryPlan("Stacking", P(Direction.Reverse, speed: 0.25f), [Red], Reported())!;
        plan.Zones.Should().OnlyContain(z => z.Frames == 792 && z.IntervalMs == 72.0, "speed .25 = SpeedType 6 × 12 ms");
        plan.Synchronous.Should().BeTrue();
        plan.PeriodSeconds.Should().BeApproximately(57.024, 1e-9);
        plan.Variant.Should().Be("UnifiedRgb uploaded UiMode7, fan-aligned Stacking over 132 source dots; H2 vertical-fill (bottom=High, interpolated)");
        plan.Summary.Should().Be($"792 frames × 72 ms = {57.024:0.###} s");

        // other shared bodies keep the contiguous 13-dot slicing and its label
        LConnectLoops.TryPlan("Meteor", P(Direction.Reverse, speed: 0.25f), [Red], Reported())!.Variant.Should().StartWith("UnifiedRgb uploaded UiMode6 over 141 dots");
    }

    [Fact]
    public void Single_target_stacking_keeps_its_per_device_body()
    {
        var single = LConnectLoops.TryPlan("Stacking", P(Direction.Forward), [Red], [new LConnectZone("TL", 3, 78)])!;
        single.Variant.Should().NotContain("fan-aligned");
        single.Variant.Should().Contain("TLMode19");
    }

    // ---------------------------------------------------------------- 6. engine publication for the reported group

    private static ControllerDescriptor Chain(string id, string family, int units, int leds, ZoneLayout layout) =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));

    [Fact]
    public void Render_loop_publishes_the_reported_group_with_its_saved_mounting()
    {
        var c = new[]
        {
            Chain("lianli:rf:02-00-00-00-00-04", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:02-00-00-00-00-01", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:02-00-00-00-00-02", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:02-00-00-00-00-03", "H2", 1, 24, ZoneLayout.Ring(24)),
        };
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus)
        {
            ChainReversed = id => id.Value == "lianli:rf:02-00-00-00-00-02",
            Ring = id => id.Value == "lianli:rf:02-00-00-00-00-03" ? new RingGeometry(true, false, 0) : default,
        };
        loop.SetControllers(c);
        var p = P(Direction.Reverse, speed: 0.25f);
        loop.SetScene(new Scene([Assignment.ForGroup("g", c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray(), "Stacking", p)]));
        loop.LastLoopCompile!.Variants!.Single().Should().Contain("UiMode7, fan-aligned Stacking over 132 source dots; H2 vertical-fill (bottom=High, interpolated)")
            .And.Contain($"792 frames × 72 ms = {57.024:0.###} s");
        var expected = LConnectLoops.TryPlan("Stacking", p, [Red], Reported())!;
        var published = c.Select(x =>
        {
            bus[x.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            return pending.Loop;
        }).ToArray();
        published.Select(l => l.EffectIndex).Distinct().Should().ContainSingle("one common effect index");
        for (var z = 0; z < 4; z++)
        {
            (published[z].Frames, published[z].IntervalMs).Should().Be((792, 72.0));
            published[z].Rgb.ToArray().Should().Equal(expected.Zones[z].Rgb, $"zone {z}");
        }
    }
}
