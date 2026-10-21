using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// NATIVE-STACKING-MIXED-ADDENDUM §6: shared Stacking with streamed (native) members. Every expected coordinate comes from
/// literal extents and pass arithmetic in this file (pass p ends at F_p = (p+1)·dots − 6p(p+1) − 1; band p is
/// [dots − 12(p+1), dots − 12p) for vendor direction 0 = Forward, [12p, 12p + 12) for Reverse); nothing calls the production
/// extent or streamed-slice helpers. Real-codec budgets (case 6) are in the Devices tests.
/// </summary>
public class LConnectStackingMixedTests
{
    private static readonly Rgb Red = new(255, 0, 0);

    /// <summary>The literal per-fan expansion of 12 source dots onto 13 TL columns (WP-15).</summary>
    private static readonly int[] Expand = [0, 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10, 11];

    /// <summary>Literal deposit-aligned extents 12·max(1, ⌊N/12 + 1/2⌋) for the logical counts used in this file.</summary>
    private static readonly Dictionary<int, int> StreamedExtent = new()
    {
        [1] = 12, [8] = 12, [12] = 12, [13] = 12, [17] = 12, [24] = 24, [25] = 24, [29] = 24, [34] = 36, [170] = 168, [180] = 180,
    };

    private const string RingSuffix = "; H2 vertical-fill (bottom=High, interpolated)";

    private static LConnectZone Tl(int fans, bool reverseChain = false) => new("TL", fans, fans * 26, ReverseChain: reverseChain);

    private static LConnectZone H2() => new("H2", 1, 24, FaceOnFill: true);

    private static LConnectZone Point() => new("Streamed", 0, 1);

    private static LConnectZone Mapped(int n) => new("Streamed", 0, n);

    /// <summary>The live group in target order: bottom TL140 LCD, vertical TL140, top TL120 (ReverseChain), H2 (FaceOnFill).</summary>
    private static LConnectZone[] Live() => [Tl(3), Tl(3), Tl(3, reverseChain: true), H2()];

    private static EffectParams P(Direction dir, float speed = 1f) => new([Red], speed, 1f, dir);

    private static int Extent(LConnectZone z) => z.Family switch { "TL" => z.Fans * 12, "H2" => 24, _ => StreamedExtent[z.LedCount] };

    private static int[] Starts(int[] extents)
    {
        var s = new int[extents.Length];
        for (var i = 1; i < s.Length; i++)
        {
            s[i] = s[i - 1] + extents[i - 1];
        }

        return s;
    }

    private static int PassEnd(int dots, int p) => ((p + 1) * dots) - (6 * p * (p + 1)) - 1;

    private static (int Start, int End) Band(int dots, int p, Direction dir) =>
        dir == Direction.Forward ? (dots - (12 * (p + 1)), dots - (12 * p)) : (12 * p, (12 * p) + 12);

    private static int StackFrames(int dots) => Enumerable.Range(0, (dots + 11) / 12).Sum(p => dots - (12 * p));

    private static byte[,] UiRed()
    {
        var ui = new byte[1, 3];
        var c = LConnectLoops.CapColour(Red, LConnectLoops.RgbMax);
        (ui[0, 0], ui[0, 1], ui[0, 2]) = (c.R, c.G, c.B);
        return ui;
    }

    /// <summary>The unchanged vendor body (brightness 1 → 255), the oracle's source rows.</summary>
    private static IReadOnlyList<byte[]> Raw(int mode, int dots, Direction dir) =>
        LConnectLoops.UiModeFrames(mode, dots, 255, LConnectLoops.VendorDirection(dir), UiRed(), out _)!;

    private static bool On(ReadOnlySpan<byte> rgb, int led) => rgb[led * 3] != 0 || rgb[(led * 3) + 1] != 0 || rgb[(led * 3) + 2] != 0;

    private static int LitIn(byte[] rgb, int leds, int frame, int from, int count)
    {
        var f = rgb.AsSpan(frame * leds * 3, leds * 3);
        var n = 0;
        for (var i = from; i < from + count; i++)
        {
            n += On(f, i) ? 1 : 0;
        }

        return n;
    }

    /// <summary>Lit LEDs (both rows) of physical TL fan <paramref name="fan"/>: columns 13·fan .. 13·fan + 12 of each row.</summary>
    private static int FanLit(byte[] rgb, LConnectZone z, int frame, int fan) =>
        LitIn(rgb, z.LedCount, frame, fan * 13, 13) + LitIn(rgb, z.LedCount, frame, (z.Fans * 13) + (fan * 13), 13);

    /// <summary>
    /// Case 1's invariant over sliced zones: at every F_p each TL fan is 0 or 26 lit; exactly the TL fan whose source
    /// sub-extent is band p becomes lit (none for an H2/streamed band); lit fans never un-light; the final frame is fully lit.
    /// Returns the violations (empty = holds) so the negative control can show the check detects the defect.
    /// </summary>
    private static List<string> Violations(LConnectZone[] zones, int[] extents, byte[][] rgb, Direction dir)
    {
        var v = new List<string>();
        var dots = extents.Sum();
        var starts = Starts(extents);
        var frames = rgb[0].Length / (zones[0].LedCount * 3);
        var lit = new HashSet<(int Zone, int Fan)>();
        for (var p = 0; p < (dots + 11) / 12; p++)
        {
            var f = PassEnd(dots, p);
            var (b0, b1) = Band(dots, p, dir);
            (int Zone, int Fan)? expected = null;
            for (var z = 0; z < zones.Length; z++)
            {
                if (zones[z].Family == "TL" && b0 >= starts[z] && b1 <= starts[z] + extents[z] && (b0 - starts[z]) % 12 == 0)
                {
                    var sub = (b0 - starts[z]) / 12;
                    expected = (z, zones[z].ReverseChain ? zones[z].Fans - 1 - sub : sub);
                }
            }

            var now = new HashSet<(int Zone, int Fan)>();
            for (var z = 0; z < zones.Length; z++)
            {
                for (var fan = 0; zones[z].Family == "TL" && fan < zones[z].Fans; fan++)
                {
                    var n = FanLit(rgb[z], zones[z], f, fan);
                    if (n is not 0 and not 26)
                    {
                        v.Add($"pass {p} frame {f}: zone {z} fan {fan} shows {n} of 26");
                    }

                    if (n == 26)
                    {
                        now.Add((z, fan));
                    }
                }
            }

            if (!lit.IsSubsetOf(now))
            {
                v.Add($"pass {p} frame {f}: a lit fan went dark");
            }

            var added = now.Except(lit).ToArray();
            (int Zone, int Fan)[] want = expected is { } e ? [e] : [];
            if (!added.SequenceEqual(want))
            {
                v.Add($"pass {p} frame {f}: band [{b0},{b1}) lit [{string.Join(",", added)}], expected [{string.Join(",", want)}]");
            }

            lit = now;
        }

        for (var z = 0; z < zones.Length; z++)
        {
            var n = LitIn(rgb[z], zones[z].LedCount, frames - 1, 0, zones[z].LedCount);
            if (n != zones[z].LedCount)
            {
                v.Add($"final frame: zone {z} shows {n} of {zones[z].LedCount}");
            }
        }

        return v;
    }

    private static IEnumerable<(string Name, LConnectZone[] Zones)> Orders(LConnectZone[] ll, LConnectZone[] st)
    {
        yield return ("streamed first", [.. st, .. ll]);
        yield return ("streamed last", [.. ll, .. st]);
        for (var i = 1; i < ll.Length; i++)
        {
            yield return ($"streamed between {i - 1} and {i}", [.. ll[..i], .. st, .. ll[i..]]);
        }

        var mix = new List<LConnectZone>();
        for (var i = 0; i < Math.Max(ll.Length, st.Length); i++)
        {
            if (i < st.Length)
            {
                mix.Add(st[i]);
            }

            if (i < ll.Length)
            {
                mix.Add(ll[i]);
            }
        }

        yield return ("interleaved", [.. mix]);
    }

    public static TheoryData<string, Direction> Sets()
    {
        var data = new TheoryData<string, Direction>();
        foreach (var name in new[] { "a", "b", "c1", "c2", "c3", "c4", "c1234", "d13", "d17", "d25", "d29" })
        {
            data.Add(name, Direction.Forward);
            data.Add(name, Direction.Reverse);
        }

        return data;
    }

    private static (LConnectZone[] Ll, LConnectZone[] St) Set(string name) => name switch
    {
        "a" => (Live(), [Point(), Point(), Point(), Point()]),
        "b" => (Live(), [Mapped(34), Mapped(12), Mapped(8), Mapped(8)]),
        "c1" => ([Tl(1)], [Point(), Mapped(8)]),
        "c2" => ([Tl(2)], [Point(), Mapped(8)]),
        "c3" => ([Tl(3)], [Point(), Mapped(8)]),
        "c4" => ([Tl(4)], [Point(), Mapped(8)]),
        "c1234" => ([Tl(1), Tl(2), Tl(3), Tl(4)], [Point(), Mapped(8)]),
        _ => (Live(), [Mapped(int.Parse(name[1..], System.Globalization.CultureInfo.InvariantCulture))]),
    };

    // ---------------------------------------------------------------- 1. whole-fan invariant, mixed groups

    [Theory]
    [MemberData(nameof(Sets))]
    public void Every_completed_pass_keeps_whole_tl_fans_in_mixed_groups(string set, Direction dir)
    {
        var (ll, st) = Set(set);
        var lastTl = Array.FindLastIndex(ll, z => z.Family == "TL");
        var variants = new[]
        {
            ("no ReverseChain", ll.Select(z => z with { ReverseChain = false }).ToArray()),
            ("ReverseChain on one TL", ll.Select((z, i) => z with { ReverseChain = i == lastTl }).ToArray()),
        };
        var checkedPlans = 0;
        foreach (var (rc, chains) in variants)
        {
            foreach (var (order, zones) in Orders(chains, st))
            {
                var extents = zones.Select(Extent).ToArray();
                var dots = extents.Sum();
                (dots % 12).Should().Be(0);
                var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones, out var why);
                plan.Should().NotBeNull($"{set} {rc} {order}: {why}");
                plan!.Zones.Should().OnlyContain(z => z.Frames == StackFrames(dots) && z.Frames == 6 * (dots / 12) * ((dots / 12) + 1));
                plan.Variant.Should().Contain($"over {dots} source dots, {st.Length} streamed zone(s) padded to 12-dot deposits");
                Violations(zones, extents, plan.Zones.Select(z => z.Rgb).ToArray(), dir).Should().BeEmpty($"{set} {dir} {rc} {order}");
                checkedPlans++;
            }
        }

        checkedPlans.Should().Be(2 * (2 + ll.Length), "first, last, every adjacent pair and interleaved, with and without ReverseChain");
    }

    // ---------------------------------------------------------------- 2. negative control: the naive 1-dot extents

    /// <summary>Test-local slicing of the defective rev-3 rule: TL fan-aligned, H2 contiguous, a Point = its single dot.</summary>
    private static byte[][] NaiveSlice(IReadOnlyList<byte[]> raw, LConnectZone[] zones, int[] extents)
    {
        var starts = Starts(extents);
        var outp = zones.Select(z => new byte[raw.Count * z.LedCount * 3]).ToArray();
        for (var f = 0; f < raw.Count; f++)
        {
            for (var z = 0; z < zones.Length; z++)
            {
                var leds = zones[z].LedCount;
                for (var led = 0; led < leds; led++)
                {
                    int src;
                    if (zones[z].Family == "TL")
                    {
                        var width = zones[z].Fans * 13;
                        var p = led % width;
                        var q = zones[z].ReverseChain ? width - 1 - p : p;
                        src = starts[z] + ((q / 13) * 12) + Expand[q % 13];
                    }
                    else
                    {
                        src = starts[z] + led;
                    }

                    raw[f].AsSpan(src * 3, 3).CopyTo(outp[z].AsSpan(((f * leds) + led) * 3));
                }
            }
        }

        return outp;
    }

    [Fact]
    public void Naive_one_dot_streamed_extents_split_a_tl_fan_and_the_invariant_detects_it()
    {
        // Codex's report: TL/TL/H2/TL (132) + GPU, onboard and two DRAM Points as 1-dot extents = 136 dots
        LConnectZone[] last = [Tl(3), Tl(3), new("H2", 1, 24), Tl(3), Point(), Point(), Point(), Point()];
        int[] lastExtents = [36, 36, 24, 36, 1, 1, 1, 1];
        var forward = Raw(7, 136, Direction.Forward);
        forward.Should().HaveCount(840, "136 + 124 + … + 4");
        PassEnd(136, 0).Should().Be(135);
        var sliced = NaiveSlice(forward, last, lastExtents);
        FanLit(sliced[3], last[3], 135, 2).Should().Be(18, "band 0 = [124, 136) covers 8 of the last fan's 12 dots → 9 of 13 columns, both rows");
        Violations(last, lastExtents, sliced, Direction.Forward).Should().Contain("pass 0 frame 135: zone 3 fan 2 shows 18 of 26");

        // streamed first breaks direction 1 the same way: band 0 = [0, 12) covers 8 dots of the first fan
        LConnectZone[] first = [Point(), Point(), Point(), Point(), Tl(3), Tl(3), new("H2", 1, 24), Tl(3)];
        int[] firstExtents = [1, 1, 1, 1, 36, 36, 24, 36];
        var reverse = NaiveSlice(Raw(7, 136, Direction.Reverse), first, firstExtents);
        FanLit(reverse[4], first[4], 135, 0).Should().Be(18);
        Violations(first, firstExtents, reverse, Direction.Reverse).Should().Contain("pass 0 frame 135: zone 4 fan 0 shows 18 of 26");

        // the production plan for both arrangements pads the Points to whole deposits and keeps every fan whole
        foreach (var (zones, dir) in new[] { (last, Direction.Forward), (first, Direction.Reverse) })
        {
            var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones)!;
            plan.Variant.Should().Contain("over 180 source dots, 4 streamed zone(s) padded to 12-dot deposits");
            Violations(zones, zones.Select(Extent).ToArray(), plan.Zones.Select(z => z.Rgb).ToArray(), dir).Should().BeEmpty();
        }
    }

    // ---------------------------------------------------------------- 3. all-Lian-Li plan unchanged

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Live_group_without_streamed_zones_matches_the_fan_aligned_oracle_byte_for_byte(Direction dir)
    {
        var zones = Live();
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones)!;
        plan.Variant.Should().Be("UnifiedRgb uploaded UiMode7, fan-aligned Stacking over 132 source dots" + RingSuffix);
        plan.Summary.Should().Be($"792 frames × 36 ms = {28.512:0.###} s");
        plan.Zones.Should().OnlyContain(z => z.Frames == 792 && z.IntervalMs == 36.0);

        var raw = Raw(7, 132, dir);
        var expected = NaiveSlice(raw, zones, [36, 36, 36, 24]); // no streamed zone: the rev-3 slicing is exact
        expected[3] = RingFillMap.ApplyFrames(expected[3], EffectGeometry.Of("Stacking").Bottom!.Value, false, RingFillMap.SamplingFor("Stacking"));
        for (var z = 0; z < 4; z++)
        {
            plan.Zones[z].Rgb.Should().Equal(expected[z], $"{dir} zone {z}");
        }
    }

    // ---------------------------------------------------------------- 4. streamed sampling pins

    [Theory]
    [InlineData(Direction.Forward, 0)]
    [InlineData(Direction.Forward, 2)]
    [InlineData(Direction.Forward, 4)]
    [InlineData(Direction.Reverse, 0)]
    [InlineData(Direction.Reverse, 2)]
    [InlineData(Direction.Reverse, 4)]
    public void Point_ramps_with_the_bar_and_holds_from_its_bands_landing(Direction dir, int at)
    {
        var zones = Live().ToList();
        zones.Insert(at, Point());
        var extents = zones.Select(Extent).ToArray();
        var dots = extents.Sum();
        dots.Should().Be(144);
        var s = Starts(extents)[at];
        var raw = Raw(7, dots, dir);
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], zones)!;
        var point = plan.Zones[at];
        point.Frames.Should().Be(raw.Count).And.Be(936);

        // UiMode7 lights every dot with the one Stacking colour, so the coverage mean is exact: c' = ⌊(c·n + 6) / 12⌋
        var colour = raw[^1].AsSpan(0, 3).ToArray();
        raw.SelectMany(f => f.Chunk(3)).Where(d => d.Any(b => b != 0)).Should().OnlyContain(d => d.SequenceEqual(colour));
        var n = new int[raw.Count];
        for (var f = 0; f < raw.Count; f++)
        {
            n[f] = Enumerable.Range(s, 12).Count(k => raw[f][k * 3] != 0 || raw[f][(k * 3) + 1] != 0 || raw[f][(k * 3) + 2] != 0);
            var want = colour.Select(c => (byte)(((c * n[f]) + 6) / 12)).ToArray();
            point.Rgb.AsSpan(f * 3, 3).ToArray().Should().Equal(want, $"frame {f}: {n[f]} of 12 dots lit");
        }

        // hold from F_p of the band [s, s + 12) to the end, entered by a 1..11 ramp; every earlier transit is 1..12..1
        var p = dir == Direction.Forward ? ((dots - s) / 12) - 1 : s / 12;
        Band(dots, p, dir).Should().Be((s, s + 12));
        var hold = raw.Count;
        while (hold > 0 && n[hold - 1] == 12)
        {
            hold--;
        }

        hold.Should().Be(PassEnd(dots, p), "the Point fills in the same frame its deposit lands, like a fan");
        n[(hold - 11)..hold].Should().Equal(Enumerable.Range(1, 11));
        int[] transit = [.. Enumerable.Range(1, 12), .. Enumerable.Range(1, 11).Reverse()];
        var runs = 0;
        for (var f = 0; f < hold - 11;)
        {
            if (n[f] == 0)
            {
                f++;
                continue;
            }

            n[f..(f + 23)].Should().Equal(transit, $"transit starting at frame {f}");
            f += 23;
            runs++;
        }

        runs.Should().Be(p, "every earlier pass carries its bar across the Point to a band beyond it");
    }

    public static TheoryData<int, int, int[]> MappedTables() => new()
    {
        { 13, 12, [0, 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10, 11] }, // = the WP-15 TL expansion table
        { 8, 12, [0, 2, 3, 5, 6, 8, 9, 11] },
        { 17, 12, [0, 1, 1, 2, 3, 3, 4, 5, 6, 6, 7, 8, 8, 9, 10, 10, 11] },
        { 25, 24, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23] },
        { 29, 24, [0, 1, 2, 2, 3, 4, 5, 6, 7, 7, 8, 9, 10, 11, 12, 12, 13, 14, 15, 16, 16, 17, 18, 19, 20, 21, 21, 22, 23] },
        { 34, 36, [0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 27, 28, 29, 30, 31, 32, 33, 34, 35] },
        { 12, 12, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] },
        { 24, 24, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23] },
    };

    [Theory]
    [MemberData(nameof(MappedTables))]
    public void Mapped_zone_reads_the_midpoint_nearest_dot_of_its_extent(int leds, int extent, int[] table)
    {
        table.Should().HaveCount(leds).And.OnlyContain(k => k >= 0 && k < extent, "every index inside [s, s + E)");
        table.Should().Equal(Enumerable.Range(0, leds).Select(j => (((2 * j) + 1) * extent) / (2 * leds)), "src(j) = ⌊(2j + 1)·E / (2N)⌋");
        StreamedExtent[leds].Should().Be(extent);
        if (leds is 13 or 17 or 25)
        {
            extent.Should().NotBe(12 * ((leds + 11) / 12), "nearest rounding, not the ceiling of the first draft");
        }

        var zones = Live().Append(Mapped(leds)).ToArray();
        var dots = 132 + extent;
        var plan = LConnectLoops.TryPlan("Stacking", P(Direction.Forward), [Red], zones)!;
        plan.Variant.Should().Be($"UnifiedRgb uploaded UiMode7, fan-aligned Stacking over {dots} source dots, 1 streamed zone(s) padded to 12-dot deposits{RingSuffix}");
        var raw = Raw(7, dots, Direction.Forward);
        var mapped = plan.Zones[4];
        mapped.Frames.Should().Be(raw.Count);

        // a dot's on/off history over the whole loop is unique (the bar reaches each dot first in a different frame), so an
        // LED's history names its source dot without consulting the production mapping
        string History(Func<int, bool> on) => string.Concat(Enumerable.Range(0, raw.Count).Select(f => on(f) ? '1' : '0'));
        var dotHistory = Enumerable.Range(0, dots).Select(k => History(f => On(raw[f], k))).ToArray();
        dotHistory.Distinct().Should().HaveCount(dots);
        for (var j = 0; j < leds; j++)
        {
            var src = Array.IndexOf(dotHistory, History(f => On(mapped.Rgb.AsSpan(f * leds * 3, leds * 3), j)));
            src.Should().Be(132 + table[j], $"LED {j}");
            for (var f = 0; f < raw.Count; f += 97)
            {
                mapped.Rgb.AsSpan(((f * leds) + j) * 3, 3).ToArray().Should().Equal(raw[f].AsSpan(src * 3, 3).ToArray());
            }
        }
    }

    // ---------------------------------------------------------------- 5. timing and labels

    [Theory]
    [InlineData(1f, 36.0)]
    [InlineData(0.25f, 72.0)]
    public void Padded_groups_share_one_longer_period_and_say_so(float speed, double interval)
    {
        foreach (var (st, dots, frames) in new[]
        {
            (new[] { Point(), Point(), Point(), Point() }, 180, 1440),
            (new[] { Mapped(34), Mapped(12), Mapped(8), Mapped(8) }, 204, 1836),
        })
        {
            var plan = LConnectLoops.TryPlan("Stacking", P(Direction.Reverse, speed), [Red], [.. Live(), .. st])!;
            plan.Variant.Should().Be($"UnifiedRgb uploaded UiMode7, fan-aligned Stacking over {dots} source dots, 4 streamed zone(s) padded to 12-dot deposits{RingSuffix}");
            plan.Zones.Should().HaveCount(8).And.OnlyContain(z => z.Frames == frames && z.IntervalMs == interval);
            plan.Synchronous.Should().BeTrue();
            plan.Summary.Should().Be($"{frames} frames × {interval:0.##} ms = {frames * interval / 1000:0.###} s");
        }

        LConnectLoops.TryPlan("Stacking", P(Direction.Reverse, 1f), [Red], [.. Live(), Point(), Point(), Point(), Point()])!.PeriodSeconds.Should().BeApproximately(51.84, 1e-9);
        LConnectLoops.TryPlan("Stacking", P(Direction.Reverse, 1f), [Red], [.. Live(), Mapped(34), Mapped(12), Mapped(8), Mapped(8)])!.PeriodSeconds.Should().BeApproximately(66.096, 1e-9);
    }

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Other_shared_routes_keep_unpadded_contiguous_streamed_extents(Direction dir)
    {
        var zones = Live().Concat([Point(), Point(), Point(), Point()]).ToArray();
        var meteor = LConnectLoops.TryPlan("Meteor", P(dir), [Red], zones)!;
        meteor.Variant.Should().StartWith("UnifiedRgb uploaded UiMode6 over 145 dots").And.NotContain("padded").And.NotContain("fan-aligned");
        var raw = Raw(6, 145, dir);
        for (var i = 0; i < 4; i++)
        {
            var point = meteor.Zones[4 + i];
            point.Frames.Should().Be(raw.Count);
            for (var f = 0; f < raw.Count; f++)
            {
                point.Rgb.AsSpan(f * 3, 3).ToArray().Should().Equal(raw[f].AsSpan((141 + i) * 3, 3).ToArray(), $"Point {i} = dot {141 + i}, frame {f}");
            }
        }

        var rainbow = LConnectLoops.TryPlan("Rainbow", P(dir), [Red], zones)!;
        rainbow.Variant.Should().Contain("over 145 dots").And.NotContain("padded");
    }

    // ---------------------------------------------------------------- 7. refusal path

    private static ControllerDescriptor Chain(string id, string family, int units, int leds, ZoneLayout layout) =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));

    private static ControllerDescriptor[] LiveChains() =>
    [
        Chain("lianli:rf:02-00-00-00-00-04", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("lianli:rf:02-00-00-00-00-01", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("lianli:rf:02-00-00-00-00-02", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("lianli:rf:02-00-00-00-00-03", "H2", 1, 24, ZoneLayout.Ring(24)),
    ];

    private static RenderLoop LiveLoop(FrameBus bus) => new(EffectRegistry.Default, bus)
    {
        ChainReversed = id => id.Value == "lianli:rf:02-00-00-00-00-02",
        Ring = id => id.Value == "lianli:rf:02-00-00-00-00-03" ? new RingGeometry(true, false, 0) : default,
    };

    [Fact]
    public void Beyond_the_vendor_scratch_the_planner_refuses_and_the_assignment_runs_as_program_with_the_reason()
    {
        LConnectLoops.TryPlan("Stacking", P(Direction.Forward), [Red], [.. Live(), Mapped(180)], out var reason).Should().BeNull();
        reason.Should().Be("UiMode7 over 312 dots exceeds the 4096-frame compatibility budget.");
        var accepted = LConnectLoops.TryPlan("Stacking", P(Direction.Forward), [Red], [.. Live(), Mapped(170)])!;
        accepted.Variant.Should().Contain("over 300 source dots, 1 streamed zone(s) padded to 12-dot deposits");
        accepted.Zones.Should().OnlyContain(z => z.Frames == 3900);

        var chains = LiveChains();
        var strip = TestSupport.Controller("native:strip", ZoneLayout.Linear(180));
        using var bus = new FrameBus();
        var loop = LiveLoop(bus);
        loop.SetControllers([.. chains, strip]);
        ZoneRef[] targets = [.. chains.Select(c => new ZoneRef(c.Id, new ZoneId(0))), new ZoneRef(strip.Id, new ZoneId(0))];
        loop.SetScene(new Scene([Assignment.ForGroup("g", targets, "Stacking", P(Direction.Forward))]));
        var variant = loop.LastLoopCompile!.Variants!.Single();
        variant.Should().StartWith($"g: Program:Stacking generic (LConnect: {reason})");
        loop.HasPlayedSlices.Should().BeFalse("the Program strip renders live");
        loop.LastLoopCompile.Variants!.Concat(loop.LastLoopCompile.Truncated).Should().NotContain(v => v.Contains("streamed members", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- 8. engine parity

    [Theory]
    [InlineData(Direction.Forward)]
    [InlineData(Direction.Reverse)]
    public void Render_loop_plays_the_point_slice_in_phase_with_the_uploaded_loops(Direction dir)
    {
        var chains = LiveChains();
        var gpu = TestSupport.Controller("native:gpu", ZoneLayout.Point);
        using var bus = new FrameBus();
        var loop = LiveLoop(bus);
        loop.SetControllers([.. chains, gpu]);
        ZoneRef[] targets = [.. chains.Select(c => new ZoneRef(c.Id, new ZoneId(0))), new ZoneRef(gpu.Id, new ZoneId(0))];
        var p = P(dir);
        loop.SetScene(new Scene([Assignment.ForGroup("g", targets, "Stacking", p)]));
        loop.LastLoopCompile!.Variants!.Single().Should()
            .Contain("Vendor:Stacking UnifiedRgb uploaded UiMode7, fan-aligned Stacking over 144 source dots, 1 streamed zone(s) padded to 12-dot deposits");
        loop.PlayedSliceCount.Should().Be(1);

        var expected = LConnectLoops.TryPlan("Stacking", p, [Red], [.. Live(), Point()])!;
        var published = chains.Select(c =>
        {
            bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            return pending.Loop;
        }).ToArray();
        published.Select(l => l.EffectIndex).Distinct().Should().ContainSingle("one common effect index");
        for (var z = 0; z < 4; z++)
        {
            (published[z].Frames, published[z].IntervalMs).Should().Be((936, 36.0));
            published[z].Rgb.ToArray().Should().Equal(expected.Zones[z].Rgb, $"zone {z}");
        }

        const double t0 = 7.25;
        loop.AnchorLoop(published[0].EffectIndex, t0);
        var slice = expected.Zones[4].Rgb;
        var full = new List<bool>();
        for (var k = 0; k < 936; k++)
        {
            loop.RenderFrame(t0 + ((k + 0.5) * 36.0 / 1000), 0);
            bus[gpu.Id]!.TryConsume(out var frame).Should().BeTrue();
            var px = frame.Zones[0][0];
            new[] { px.R, px.G, px.B }.Should().Equal(slice.AsSpan(k * 3, 3).ToArray(), $"tick in frame {k} plays frame {k}");
            full.Add(px.R == slice[^3]);
        }

        // the Point sits last: band [132, 144) is band 0 for Forward, band 11 for Reverse
        var band = dir == Direction.Forward ? 0 : 11;
        full.FindIndex(f => f).Should().BeLessThanOrEqualTo(PassEnd(144, band));
        full.LastIndexOf(false).Should().Be(PassEnd(144, band) - 1, "the first frame of the final full-brightness hold is the band's F_p");
    }
}
