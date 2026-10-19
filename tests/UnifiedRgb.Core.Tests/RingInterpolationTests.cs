using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// WP-16 test oracle: continuous-height sampling of a 24-dot vertical strip, written from the work order's formula without
/// the production tap table. θ = (k + s)·15° for the canonical index k of each mirror pair (s = ½ for half-step),
/// x = 23·(1 − cos θ)/2, a = ⌊x⌋, b = min(a + 1, 23), t = x − a, out = round_half_up((1 − t)·A + t·B). Bottom Low samples
/// slice[a]/slice[b]; bottom High samples slice[23 − a]/slice[23 − b].
/// </summary>
internal static class LinearFillOracle
{
    /// <summary>One 24-LED frame (72 bytes) from one 24-dot strip (72 bytes).</summary>
    public static byte[] Frame(ReadOnlySpan<byte> slice, bool high, bool halfStep)
    {
        var dst = new byte[72];
        for (var i = 0; i < 24; i++)
        {
            // the canonical member of LED i's mirror pair (whole-step: i ↔ 24 − i about LED 0; half-step: i ↔ 23 − i)
            var k = halfStep ? (i <= 11 ? i : 23 - i) : (i <= 12 ? i : 24 - i);
            var theta = (k + (halfStep ? 0.5 : 0.0)) * Math.PI / 12.0;
            var x = 23.0 * (1.0 - Math.Cos(theta)) / 2.0;
            if (Math.Abs(x - Math.Round(x)) < 1e-9)
            {
                x = Math.Round(x);
            }

            var a = (int)Math.Floor(x);
            var b = Math.Min(a + 1, 23);
            var t = x - a;
            var (sa, sb) = high ? (23 - a, 23 - b) : (a, b);
            for (var ch = 0; ch < 3; ch++)
            {
                var v = ((1.0 - t) * slice[(sa * 3) + ch]) + (t * slice[(sb * 3) + ch]);
                dst[(i * 3) + ch] = (byte)Math.Clamp((int)Math.Floor(v + 0.5 + 1e-7), 0, 255);
            }
        }

        return dst;
    }

    /// <summary>Frame-major 24-dot frames → frame-major 24-LED frames.</summary>
    public static byte[] Frames(byte[] rgb, bool high, bool halfStep)
    {
        var dst = new byte[rgb.Length];
        for (var f = 0; f < rgb.Length / 72; f++)
        {
            Frame(rgb.AsSpan(f * 72, 72), high, halfStep).CopyTo(dst, f * 72);
        }

        return dst;
    }
}

/// <summary>
/// WP-16 (wp16-smoothing-order): linear RGB interpolation of the H2 vertical fill, for shared Stacking only. Pinned integers
/// were computed outside the production code; nearest stays the default everywhere else.
/// </summary>
public class RingInterpolationTests
{
    private static readonly Rgb Red = new(255, 0, 0);

    private static byte[] Slice(Func<int, (byte R, byte G, byte B)> dot)
    {
        var s = new byte[72];
        for (var d = 0; d < 24; d++)
        {
            (s[d * 3], s[(d * 3) + 1], s[(d * 3) + 2]) = dot(d);
        }

        return s;
    }

    private static byte[] Linear(byte[] slice, BottomEnd bottom, bool half)
    {
        var dst = new byte[72];
        RingFillMap.Apply(slice, dst, bottom, half, RingSampling.Linear);
        return dst;
    }

    private static int[] Channel(byte[] frame, int ch) => Enumerable.Range(0, 24).Select(i => (int)frame[(i * 3) + ch]).ToArray();

    // ---------------------------------------------------------------- 1. numeric taps and rounding

    [Fact]
    public void Bottom_only_sample_spreads_by_the_pinned_weights()
    {
        // whole-step i = 1: x = 23·(1 − cos 15°)/2 ≈ .391853 → (1 − .391853)·191 = 116.15 → 116
        var low = Slice(d => d == 0 ? ((byte)191, (byte)0, (byte)0) : default);
        int[] whole = [191, 116, .. new int[21], 116];
        int[] half = [172, 24, .. new int[20], 24, 172];
        Channel(Linear(low, BottomEnd.Low, false), 0).Should().Equal(whole);
        Channel(Linear(low, BottomEnd.Low, true), 0).Should().Equal(half);

        // bottom High reads the strip from its far end
        var high = Slice(d => d == 23 ? ((byte)191, (byte)0, (byte)0) : default);
        Channel(Linear(high, BottomEnd.High, false), 0).Should().Equal(whole);
        Channel(Linear(high, BottomEnd.High, true), 0).Should().Equal(half);
        Linear(low, BottomEnd.Low, false).Where((_, j) => j % 3 != 0).Should().OnlyContain(v => v == 0, "black channels stay black");
    }

    private static readonly int[][] RampWhole =
    [
        [0, 255, 77], [4, 251, 77], [15, 240, 77], [34, 221, 77], [58, 198, 77], [85, 170, 77], [115, 140, 77],
        [145, 110, 77], [173, 83, 77], [196, 59, 77], [215, 40, 77], [226, 29, 77], [230, 25, 77],
    ];

    private static readonly int[][] RampHalf =
    [
        [1, 254, 77], [9, 246, 77], [24, 231, 77], [45, 210, 77], [71, 184, 77], [100, 155, 77],
        [130, 125, 77], [159, 96, 77], [185, 70, 77], [206, 49, 77], [221, 34, 77], [229, 26, 77],
    ];

    [Theory]
    [InlineData(false, BottomEnd.Low)]
    [InlineData(false, BottomEnd.High)]
    [InlineData(true, BottomEnd.Low)]
    [InlineData(true, BottomEnd.High)]
    public void Unequal_ramp_pins_every_led_and_mirror_pairs_are_byte_equal(bool half, BottomEnd bottom)
    {
        // strip dot s (counted from the bottom): R = 10s, G = 255 − 10s, B = 77
        var slice = Slice(d =>
        {
            var s = bottom == BottomEnd.Low ? d : 23 - d;
            return ((byte)(10 * s), (byte)(255 - (10 * s)), (byte)77);
        });
        var dst = Linear(slice, bottom, half);
        var table = half ? RampHalf : RampWhole;
        for (var i = 0; i < 24; i++)
        {
            var k = half ? (i <= 11 ? i : 23 - i) : (i <= 12 ? i : 24 - i);
            new[] { (int)dst[i * 3], dst[(i * 3) + 1], dst[(i * 3) + 2] }.Should().Equal(table[k], $"LED {i} half {half} {bottom}");
            dst.AsSpan(i * 3, 3).ToArray().Should().Equal(dst.AsSpan(RingFillMap.Mirror(i, half) * 3, 3).ToArray(), "mirror pair");
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(17, 99, 200)]
    [InlineData(254, 0, 0)]
    public void Constant_strips_stay_constant(byte r, byte g, byte b)
    {
        var slice = Slice(_ => (r, g, b));
        foreach (var half in new[] { false, true })
        {
            foreach (var bottom in new[] { BottomEnd.Low, BottomEnd.High })
            {
                Linear(slice, bottom, half).Should().Equal(slice, $"half {half} {bottom}: no blend invents a colour");
            }
        }
    }

    [Fact]
    public void Production_equals_the_independent_oracle_on_random_strips()
    {
        var rng = new Random(16);
        for (var n = 0; n < 200; n++)
        {
            var slice = new byte[72];
            rng.NextBytes(slice);
            foreach (var half in new[] { false, true })
            {
                foreach (var bottom in new[] { BottomEnd.Low, BottomEnd.High })
                {
                    var dst = Linear(slice, bottom, half);
                    dst.Should().Equal(LinearFillOracle.Frame(slice, bottom == BottomEnd.High, half), $"strip {n} half {half} {bottom}");
                    for (var i = 0; i < 24; i++)
                    {
                        dst.AsSpan(i * 3, 3).SequenceEqual(dst.AsSpan(RingFillMap.Mirror(i, half) * 3, 3)).Should().BeTrue("paired bytes are exact");
                    }
                }
            }
        }
    }

    [Fact]
    public void Rgb_overload_and_frame_overload_share_the_byte_path()
    {
        var rng = new Random(7);
        var slice = new byte[72 * 3];
        rng.NextBytes(slice);
        foreach (var half in new[] { false, true })
        {
            foreach (var bottom in new[] { BottomEnd.Low, BottomEnd.High })
            {
                var frames = RingFillMap.ApplyFrames(slice, bottom, half, RingSampling.Linear);
                for (var f = 0; f < 3; f++)
                {
                    var src = Enumerable.Range(0, 24).Select(d => new Rgb(slice[(f * 72) + (d * 3)], slice[(f * 72) + (d * 3) + 1], slice[(f * 72) + (d * 3) + 2])).ToArray();
                    var rgb = new Rgb[24];
                    RingFillMap.Apply(src, rgb, bottom, half, RingSampling.Linear);
                    rgb.SelectMany(c => new[] { c.R, c.G, c.B }).Should().Equal(frames.AsSpan(f * 72, 72).ToArray(), $"frame {f} half {half} {bottom}");
                    frames.AsSpan(f * 72, 72).ToArray().Should().Equal(LinearFillOracle.Frame(slice.AsSpan(f * 72, 72), bottom == BottomEnd.High, half));
                }
            }
        }
    }

    [Fact]
    public void Nearest_stays_the_default_and_the_calibration_fixtures_stay_binary()
    {
        var rng = new Random(3);
        var slice = new byte[72 * 2];
        rng.NextBytes(slice);
        foreach (var half in new[] { false, true })
        {
            foreach (var bottom in new[] { BottomEnd.Low, BottomEnd.High })
            {
                // the WP-14 nearest lookup, literally: LED i shows strip dot Dots[i]
                var expected = new byte[slice.Length];
                for (var f = 0; f < 2; f++)
                {
                    for (var i = 0; i < 24; i++)
                    {
                        var d = RingFillMap.Dots(half)[i];
                        var s = bottom == BottomEnd.Low ? d : 23 - d;
                        slice.AsSpan((f * 72) + (s * 3), 3).CopyTo(expected.AsSpan((f * 72) + (i * 3)));
                    }
                }

                RingFillMap.ApplyFrames(slice, bottom, half).Should().Equal(expected, "the 3-argument overload is nearest");
                RingFillMap.ApplyFrames(slice, bottom, half, RingSampling.Nearest).Should().Equal(expected);
                var one = new byte[72];
                RingFillMap.Apply(slice.AsSpan(0, 72), one, bottom, half);
                one.Should().Equal(expected.AsSpan(0, 72).ToArray());
            }

            // marker and wedge are on/off fixtures, never blended (their exact LEDs are pinned in RingFillTests)
            RingFillMap.MarkerFrame(half).Should().OnlyContain(v => v == 0 || v == 32 || v == 255);
            var wedge = RingFillMap.WedgeFrame(half);
            wedge.Should().OnlyContain(v => v == 0 || v == 200);
            Enumerable.Range(0, 24).Where(i => wedge[i * 3] != 0).Should().Equal(RingFillMap.LitAt(0.25, half));
        }

        RingFillMap.Dots(false).Take(13).Should().Equal(0, 0, 2, 3, 6, 9, 12, 14, 17, 20, 21, 23, 23);
        RingFillMap.Dots(true).Take(12).Should().Equal(0, 1, 2, 4, 7, 10, 13, 16, 19, 21, 22, 23);
    }

    // ---------------------------------------------------------------- 2. the reported aligned four-chain group

    private static LConnectZone[] Reported(bool fill = true, bool half = false) =>
        [new("TL", 3, 78), new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: fill, HalfStep: half)];

    private static EffectParams P(Direction dir) => new([Red], 0.25f, 1f, dir);

    private static byte[] RawH2Slice(Direction dir)
    {
        var ui = new byte[6, 3];
        var c = LConnectLoops.CapColour(Red, LConnectLoops.RgbMax);
        (ui[0, 0], ui[0, 1], ui[0, 2]) = (c.R, c.G, c.B);
        var raw = LConnectLoops.UiModeFrames(7, 132, 255, LConnectLoops.VendorDirection(dir), ui, out _)!;
        raw.Should().HaveCount(792);
        var dst = new byte[792 * 72];
        for (var f = 0; f < 792; f++)
        {
            raw[f].AsSpan(108 * 3, 72).CopyTo(dst.AsSpan(f * 72)); // the H2 owns source dots 108..131 (after 3 × 36 TL dots)
        }

        return dst;
    }

    [Theory]
    [InlineData(Direction.Reverse, false)]
    [InlineData(Direction.Forward, false)]
    [InlineData(Direction.Reverse, true)]
    [InlineData(Direction.Forward, true)]
    public void Reported_group_interpolates_only_the_h2_and_keeps_tl_bytes_and_timing(Direction dir, bool half)
    {
        var plan = LConnectLoops.TryPlan("Stacking", P(dir), [Red], Reported(half: half))!;
        var unfilled = LConnectLoops.TryPlan("Stacking", P(dir), [Red], Reported(fill: false))!;
        for (var z = 0; z < 3; z++)
        {
            // TL bytes do not depend on the H2 flag; LConnectStackingAlignmentTests pins them to the WP-15 expansion oracle
            plan.Zones[z].Rgb.Should().Equal(unfilled.Zones[z].Rgb, $"{dir} TL zone {z} is the WP-15 placement");
        }

        plan.Zones.Should().OnlyContain(x => x.Frames == 792 && x.IntervalMs == 72.0);
        plan.PeriodSeconds.Should().BeApproximately(57.024, 1e-9);
        plan.Summary.Should().Be(unfilled.Summary);

        var raw = RawH2Slice(dir);
        unfilled.Zones[3].Rgb.Should().Equal(raw, "an unflagged H2 shows its contiguous slice of the unchanged UiMode7(132) body");
        plan.Zones[3].Rgb.Should().Equal(LinearFillOracle.Frames(raw, high: true, halfStep: half), $"{dir} half {half}");
        plan.Zones[3].Rgb.Should().NotEqual(RingFillMap.ApplyFrames(raw, BottomEnd.High, half), "the WP-15 nearest fill is replaced");
        plan.Variant.Should().EndWith($"; H2 vertical-fill (bottom=High{(half ? ", half-step" : string.Empty)}, interpolated)");
        unfilled.Variant.Should().NotContain("vertical-fill").And.NotContain("interpolated");
    }

    // ---------------------------------------------------------------- 3. gating

    [Fact]
    public void Only_stacking_selects_linear_sampling()
    {
        EffectRegistry.Default.All.Select(g => g.Id).Where(id => RingFillMap.SamplingFor(id) == RingSampling.Linear).Should().Equal("Stacking");
        RingFillMap.SamplingFor("stacking").Should().Be(RingSampling.Nearest, "the effect id match is exact");
    }

    public static TheoryData<string> OtherTranslationalRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var id in LConnectLoops.GroupRoutes.Keys.Where(k => EffectGeometry.IsTranslational(k) && k != "Stacking").Order(StringComparer.Ordinal))
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OtherTranslationalRoutes))]
    public void Other_translational_routes_keep_nearest_bytes(string effect)
    {
        foreach (var half in new[] { false, true })
        {
            var p = new EffectParams([Red, new Rgb(0, 255, 0)], 0.5f, 1f);
            var plain = LConnectLoops.TryPlan(effect, p, p.Colors, Reported(fill: false))!;
            var filled = LConnectLoops.TryPlan(effect, p, p.Colors, Reported(half: half))!;
            filled.Zones[3].Rgb.Should().Equal(RingFillMap.ApplyFrames(plain.Zones[3].Rgb, EffectGeometry.Of(effect).Bottom!.Value, half), $"{effect} half {half}");
            filled.Variant.Should().NotContain("interpolated");
        }
    }

    [Fact]
    public void Rotational_per_device_single_target_and_uieffect_bodies_are_unchanged()
    {
        var p = new EffectParams([Red, new Rgb(0, 255, 0)], 0.5f, 1f);
        // Taichi has no vendor group body (generic); RingFillTests.Rotational_and_uniform_effects_are_byte_identical_with_fill_on
        // covers it on both RenderLoop policies
        foreach (var effect in new[] { "Rainbow", "RainbowMorph", "Twinkle" })
        {
            var plain = LConnectLoops.TryPlan(effect, p, p.Colors, Reported(fill: false))!;
            var flagged = LConnectLoops.TryPlan(effect, p, p.Colors, Reported())!;
            flagged.Zones[3].Rgb.Should().Equal(plain.Zones[3].Rgb, effect);
            flagged.Variant.Should().NotContain("interpolated");
        }

        // single-target Stacking on the H2 (no vendor H2 body: the generic path) keeps its whole-ring body, flag or not
        var plainSolo = LConnectLoops.TryPlan("Stacking", P(Direction.Forward), [Red], [new LConnectZone("H2", 1, 24)]);
        var flaggedSolo = LConnectLoops.TryPlan("Stacking", P(Direction.Forward), [Red], [new LConnectZone("H2", 1, 24, FaceOnFill: true, HalfStep: true)]);
        (flaggedSolo?.Zones[0].Rgb).Should().Equal(plainSolo?.Zones[0].Rgb);
        var solo = Chain("h2", "H2", 1, 24, ZoneLayout.Ring(24));
        byte[] Solo(RingGeometry ring, out string? geometry)
        {
            using var bus = new FrameBus();
            var loop = new RenderLoop(EffectRegistry.Default, bus) { Ring = _ => ring };
            loop.SetControllers([solo]);
            loop.SetScene(new Scene([Assignment.ForGroup("s", [new ZoneRef(solo.Id, new ZoneId(0))], "Stacking", P(Direction.Forward))]));
            bus[solo.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            geometry = pending.Loop.Geometry;
            return pending.Loop.Rgb.ToArray();
        }

        Solo(new RingGeometry(true, false, 0), out var g).Should().Equal(Solo(default, out _));
        g.Should().Be("ring (per-device body)");
    }

    // ---------------------------------------------------------------- 4. RenderLoop parity

    private static ControllerDescriptor Chain(string id, string family, int units, int leds, ZoneLayout layout) =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));

    private static ControllerDescriptor[] Chains(ZoneLayout? h2 = null) =>
    [
        Chain("lcd", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("side", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("top", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("h2", "H2", 1, 24, h2 ?? ZoneLayout.Ring(24)),
    ];

    private static (LoopFrames H2, string Variant) Publish(ControllerDescriptor[] c, LoopPolicy policy, EffectParams p, RingGeometry ring)
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus) { ChainReversed = id => id.Value == "top", Ring = id => id.Value == "h2" ? ring : default };
        loop.SetControllers(c);
        loop.SetScene(new Scene([Assignment.ForGroup("g", c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray(), "Stacking", p) with { Policy = policy }]));
        bus[c[3].Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
        return (pending.Loop, loop.LastLoopCompile!.Variants!.Single());
    }

    [Theory]
    [InlineData(Direction.Reverse, false)]
    [InlineData(Direction.Forward, false)]
    [InlineData(Direction.Reverse, true)]
    [InlineData(Direction.Forward, true)]
    public void Render_loop_vendor_and_generic_paths_publish_the_interpolated_h2(Direction dir, bool half)
    {
        var p = P(dir);
        var vendor = Publish(Chains(), LoopPolicy.LConnect, p, new RingGeometry(true, half, 0));
        vendor.H2.Rgb.ToArray().Should().Equal(LConnectLoops.TryPlan("Stacking", p, [Red], Reported(half: half))!.Zones[3].Rgb);
        vendor.H2.Geometry.Should().Be($"vertical-fill offset=+0 half={(half ? "true" : "false")} bottom=High sampling=linear");

        var flat = Publish(Chains(ZoneLayout.Linear(24)), LoopPolicy.Generic, p, default);
        var generic = Publish(Chains(), LoopPolicy.Generic, p, new RingGeometry(true, half, 0));
        generic.H2.Rgb.ToArray().Should().Equal(LinearFillOracle.Frames(flat.H2.Rgb.ToArray(), high: true, halfStep: half), "generic Stacking shares the rounding");
        (generic.H2.Frames, generic.H2.IntervalMs).Should().Be((flat.H2.Frames, flat.H2.IntervalMs));
        generic.H2.Geometry.Should().EndWith("bottom=High sampling=linear");
        generic.Variant.Should().EndWith($"; H2 vertical-fill (bottom=High{(half ? ", half-step" : string.Empty)}, interpolated)");
    }
}
