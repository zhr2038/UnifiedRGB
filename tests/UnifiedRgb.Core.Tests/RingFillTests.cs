using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// WP-14 (H2-VERTICAL-FILL §6): the normative d_i tables, fill fixtures, body-untouched byte equality, sweep sense of every
/// translational route, composition rules, and rotational / per-device no-regression.
/// </summary>
public class RingFillTests
{
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb[] RedGreen = [Red, Green];

    /// <summary>The translational vendor group routes (§4 ∩ GroupRoutes) with their pinned bottom end.</summary>
    public static TheoryData<string> TranslationalRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var id in LConnectLoops.GroupRoutes.Keys.Where(EffectGeometry.IsTranslational).Order(StringComparer.Ordinal))
        {
            data.Add(id);
        }

        return data;
    }

    public static TheoryData<string> NonTranslationalRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var id in LConnectLoops.GroupRoutes.Keys.Where(k => !EffectGeometry.IsTranslational(k)).Order(StringComparer.Ordinal))
        {
            data.Add(id);
        }

        return data;
    }

    private static EffectParams P(Direction dir = Direction.Forward) => new(RedGreen, 0.5f, 1f, dir);

    private static LConnectZone[] Group(bool fill = false, bool half = false, bool reverseH2 = false) =>
        [new("TL", 3, 78), new("TL", 3, 78, true), new("H2", 1, 24, reverseH2, fill, half), new("TL", 3, 78)];

    // ---------------------------------------------------------------- §2 tables

    [Fact]
    public void Tables_are_the_normative_values_and_match_the_cosine_derivation()
    {
        RingFillMap.Dots(false).Take(13).Should().Equal(0, 0, 2, 3, 6, 9, 12, 14, 17, 20, 21, 23, 23);
        RingFillMap.Dots(true).Take(12).Should().Equal(0, 1, 2, 4, 7, 10, 13, 16, 19, 21, 22, 23);
        foreach (var half in new[] { false, true })
        {
            for (var i = 0; i < 24; i++)
            {
                RingFillMap.Dots(half)[i].Should().Be(RingFillMap.Derive(i, half), $"i {i} half {half}");
                RingFillMap.Dots(half)[RingFillMap.Mirror(i, half)].Should().Be(RingFillMap.Dots(half)[i], "mirror pairs take the same dot");
            }
        }
    }

    [Theory]
    [InlineData(false, 0.0, new[] { 0, 1, 23 })]
    [InlineData(false, 1.0 / 23, new[] { 0, 1, 23 })]
    [InlineData(false, 0.25, new[] { 0, 1, 2, 3, 4, 20, 21, 22, 23 })]
    [InlineData(false, 0.5, new[] { 0, 1, 2, 3, 4, 5, 6, 18, 19, 20, 21, 22, 23 })]
    [InlineData(false, 0.75, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 16, 17, 18, 19, 20, 21, 22, 23 })]
    [InlineData(true, 0.0, new[] { 0, 23 })]
    [InlineData(true, 1.0 / 23, new[] { 0, 1, 22, 23 })]
    [InlineData(true, 0.25, new[] { 0, 1, 2, 3, 20, 21, 22, 23 })]
    [InlineData(true, 0.5, new[] { 0, 1, 2, 3, 4, 5, 18, 19, 20, 21, 22, 23 })]
    [InlineData(true, 0.75, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23 })]
    public void Fill_fixtures_light_the_expected_leds(bool half, double p, int[] lit)
    {
        RingFillMap.LitAt(p, half).Should().Equal(lit);
        lit.Should().OnlyContain(i => lit.Contains(RingFillMap.Mirror(i, half)), "the fill is symmetric about the bottom");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_top_is_lit_only_at_full_fill(bool half)
    {
        var top = half ? 11 : 12;
        foreach (var p in new[] { 0, 1.0 / 23, 0.25, 0.5, 0.75 })
        {
            RingFillMap.LitAt(p, half).Should().NotContain(top, $"p {p}");
        }

        RingFillMap.LitAt(1, half).Should().HaveCount(24);
    }

    [Fact]
    public void Apply_takes_dot_d_from_the_bottom_end()
    {
        var ramp = Enumerable.Range(0, 24).SelectMany(i => new[] { (byte)i, (byte)(100 + i), (byte)200 }).ToArray();
        var low = RingFillMap.ApplyFrames(ramp, BottomEnd.Low, false);
        var high = RingFillMap.ApplyFrames(ramp, BottomEnd.High, false);
        for (var i = 0; i < 24; i++)
        {
            low[i * 3].Should().Be((byte)RingFillMap.Dots(false)[i]);
            high[i * 3].Should().Be((byte)(23 - RingFillMap.Dots(false)[i]));
            low[(i * 3) + 1].Should().Be((byte)(100 + low[i * 3]), "triplets stay intact");
        }

        var bad = () => RingFillMap.ApplyFrames(new byte[70], BottomEnd.Low, false);
        bad.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Calibration_frames_mark_led_0_and_the_quarter_wedge()
    {
        var m = RingFillMap.MarkerFrame(false);
        (m[0], m[1], m[2]).Should().Be(((byte)255, (byte)0, (byte)0));
        Enumerable.Range(1, 3).Should().OnlyContain(i => m[i * 3] == 0 && m[(i * 3) + 1] > 0 && m[(i * 3) + 1] < 64);
        Enumerable.Range(4, 20).Should().OnlyContain(i => m[i * 3] == 0 && m[(i * 3) + 1] == 0 && m[(i * 3) + 2] == 0);
        var h = RingFillMap.MarkerFrame(true);
        (h[0], h[23 * 3]).Should().Be(((byte)255, (byte)255), "half-step: the two LEDs straddling the bottom are red");

        var w = RingFillMap.WedgeFrame(false);
        Enumerable.Range(0, 24).Where(i => w[i * 3] > 0).Should().Equal(RingFillMap.LitAt(0.25, false));
    }

    [Fact]
    public void Shift_normalisation_preserves_the_ring_shift()
    {
        for (var q = 0; q < 4; q++)
        {
            for (var off = -23; off <= 23; off++)
            {
                var (rot, o) = RingFillMap.NormalizeShift(q * 90, off);
                o.Should().BeInRange(-3, 2);
                rot.Should().BeOneOf(0, 90, 180, 270);
                (((rot / 90 * 6) + o - (q * 6) - off) % 24).Should().Be(0, $"rot {q * 90} off {off}");
            }
        }

        RingFillMap.NormalizeShift(0, 8).Should().Be((90, 2));
        RingFillMap.NormalizeShift(90, 3).Should().Be((180, -3));
        RingFillMap.NormalizeShift(0, -1).Should().Be((0, -1));
        RingFillMap.NormalizeShift(0, -4).Should().Be((270, 2));
    }

    // ---------------------------------------------------------------- §4 classification

    [Fact]
    public void Every_registered_effect_is_classified_and_only_stack_effects_are_high()
    {
        EffectRegistry.Default.All.Select(g => g.Id).Should().OnlyContain(id => EffectGeometry.Table.ContainsKey(id));
        EffectGeometry.Table.Where(kv => kv.Value.Bottom == BottomEnd.High).Select(kv => kv.Key).Should().BeEquivalentTo("MopUp", "Stacking");
        EffectGeometry.Of("Rainbow").Class.Should().Be(GeometryClass.Rotational);
        EffectGeometry.Of("Taichi").Class.Should().Be(GeometryClass.Rotational);
        EffectGeometry.Of("nope").Class.Should().Be(GeometryClass.Uniform);
        EffectGeometry.Table.Values.Where(s => s.IsTranslational).Should().OnlyContain(s => s.Bottom != null);
        EffectGeometry.Table.Values.Where(s => !s.IsTranslational).Should().OnlyContain(s => s.Bottom == null);
    }

    // ---------------------------------------------------------------- body untouched

    [Theory]
    [MemberData(nameof(TranslationalRoutes))]
    public void Fill_touches_only_the_h2_slice_and_keeps_frames_and_interval(string effect)
    {
        foreach (var dir in new[] { Direction.Forward, Direction.Reverse })
        {
            var plain = LConnectLoops.TryPlan(effect, P(dir), RedGreen, Group())!;
            foreach (var half in new[] { false, true })
            {
                var filled = LConnectLoops.TryPlan(effect, P(dir), RedGreen, Group(fill: true, half: half))!;
                foreach (var z in new[] { 0, 1, 3 })
                {
                    filled.Zones[z].Rgb.Should().Equal(plain.Zones[z].Rgb, $"{effect} TL zone {z} is byte-equal");
                }

                (filled.Zones[2].Frames, filled.Zones[2].IntervalMs).Should().Be((plain.Zones[2].Frames, plain.Zones[2].IntervalMs));
                filled.PeriodSeconds.Should().Be(plain.PeriodSeconds);
                // WP-16: Stacking alone is interpolated (pinned independently in RingInterpolationTests); the rest stay nearest
                var linear = effect == "Stacking";
                var sampling = linear ? RingSampling.Linear : RingSampling.Nearest;
                filled.Zones[2].Rgb.Should().Equal(RingFillMap.ApplyFrames(plain.Zones[2].Rgb, EffectGeometry.Of(effect).Bottom!.Value, half, sampling));
                filled.Variant.Should().EndWith($"; H2 vertical-fill (bottom={EffectGeometry.Of(effect).Bottom}{(half ? ", half-step" : string.Empty)}{(linear ? ", interpolated" : string.Empty)})");
            }
        }
    }

    [Theory]
    [MemberData(nameof(NonTranslationalRoutes))]
    public void Non_translational_routes_are_byte_identical_with_the_flag(string effect)
    {
        var plain = LConnectLoops.TryPlan(effect, P(), RedGreen, Group())!;
        var flagged = LConnectLoops.TryPlan(effect, P(), RedGreen, Group(fill: true))!;
        for (var z = 0; z < 4; z++)
        {
            flagged.Zones[z].Rgb.Should().Equal(plain.Zones[z].Rgb, $"{effect} zone {z}");
        }

        flagged.Variant.Should().EndWith("; H2 ring").And.Be(plain.Variant + "; H2 ring");
    }

    [Fact]
    public void Per_device_h2_bodies_are_never_remapped()
    {
        var checkedModes = new HashSet<int>();
        foreach (var g in EffectRegistry.Default.All)
        {
            if (LConnectLoops.H2ModeFor(g.Id) is not { } mode)
            {
                continue;
            }

            var colors = g.Schema.DefaultColors.Count > 0 ? g.Schema.DefaultColors.ToArray() : RedGreen;
            var p = new EffectParams(colors, 0.5f, 1f);
            var plain = LConnectLoops.TryPlan(g.Id, p, colors, [new LConnectZone("H2", 1, 24)]);
            var flagged = LConnectLoops.TryPlan(g.Id, p, colors, [new LConnectZone("H2", 1, 24, false, true, true)]);
            if (plain is null)
            {
                flagged.Should().BeNull();
                continue;
            }

            flagged!.Zones[0].Rgb.Should().Equal(plain.Zones[0].Rgb, $"{g.Id} → H2Mode{mode} is a per-device body");
            flagged.Variant.Should().Be(plain.Variant + "; H2 ring");
            checkedModes.Add(mode);
        }

        checkedModes.Should().BeEquivalentTo(LConnectLoops.H2Modes, "every per-device H2 mode was exercised");

        // UIEffect 2/8 groups hand each device its own body too
        foreach (var effect in new[] { "RainbowMorph", "Twinkle" })
        {
            var plain = LConnectLoops.TryPlan(effect, P(), RedGreen, Group())!;
            LConnectLoops.TryPlan(effect, P(), RedGreen, Group(fill: true))!.Zones[2].Rgb.Should().Equal(plain.Zones[2].Rgb, effect);
        }
    }

    // ---------------------------------------------------------------- sweep sense

    /// <summary>A 48-dot run (two H2 slices) of a group route, frame-major, 48 × RGB per frame.</summary>
    internal static byte[][] Run48(string effect, Direction dir)
    {
        var plan = LConnectLoops.TryPlan(effect, P(dir), RedGreen, [new LConnectZone("H2", 1, 24), new LConnectZone("H2", 1, 24)])!;
        return Enumerable.Range(0, plan.Zones[0].Frames)
            .Select(f => plan.Zones[0].Rgb.AsSpan(f * 72, 72).ToArray().Concat(plan.Zones[1].Rgb.AsSpan(f * 72, 72).ToArray()).ToArray())
            .ToArray();
    }

    /// <summary>A dot's coarse state: 0 = dark, else 1 + its dominant channel (fades and gradients keep their key).</summary>
    private static int Dot(byte[] frame, int j)
    {
        var (r, g, b) = (frame[j * 3], frame[(j * 3) + 1], frame[(j * 3) + 2]);
        return r + g + b == 0 ? 0 : r >= g && r >= b ? 1 : g >= b ? 2 : 3;
    }

    /// <summary>
    /// Net motion of the content inside the first 24-dot slice: + toward its high index, − toward its low index (one-dot shift
    /// matches of changed dots). Some bodies are symmetric over the whole run (Tide, Echo, ElectricCurrent run in from both
    /// ends), so the sense is measured where the H2 slice sits, not over the run.
    /// </summary>
    internal static int Motion(byte[][] frames, int upTo = int.MaxValue)
    {
        var score = 0;
        for (var f = 0; f + 1 < Math.Min(frames.Length, upTo); f++)
        {
            var (a, b) = (frames[f], frames[f + 1]);
            for (var j = 1; j < 23; j++)
            {
                if (Dot(a, j) == Dot(b, j))
                {
                    continue;
                }

                score += Dot(b, j) == Dot(a, j - 1) ? 1 : 0;
                score -= Dot(b, j) == Dot(a, j + 1) ? 1 : 0;
            }
        }

        return score;
    }

    [Theory]
    [MemberData(nameof(TranslationalRoutes))]
    public void Translational_routes_travel_toward_the_high_end_so_the_pinned_bottom_end_rises(string effect)
    {
        // travelling bodies enter at the slice's low index (bottom = Low: the head rises); accumulating bodies (MopUp, Stacking)
        // travel the same way and pile up at the far end first (bottom = High: the stack rises)
        // measured over the first H2 slice of a 48-dot run; Runway and MopUp sweep there and back, so the opening quarter pins it
        var forward = Run48(effect, Direction.Forward);
        Motion(forward, forward.Length / 4).Should().BePositive($"{effect} forward opens moving toward the slice's high index");
        Motion(forward).Should().BeGreaterThanOrEqualTo(0, $"{effect} never travels net downward");

        // Reverse either runs the body the other way, or the UiMode body ignores direction (Runway, MopUp and the
        // run-symmetric Tide, ElectricCurrent, Echo): then it is byte-identical and keeps the same bottom end
        var reverse = Run48(effect, Direction.Reverse);
        var unchanged = reverse.Length == forward.Length && reverse.Zip(forward).All(x => x.First.SequenceEqual(x.Second));
        (unchanged || Motion(reverse) < 0).Should().BeTrue($"{effect} reverse: motion {Motion(reverse)}");
    }

    [Fact]
    public void Stacking_fills_from_the_bottom_and_reverse_drains()
    {
        int[] Lit(Direction dir, bool nearest = false)
        {
            var plan = LConnectLoops.TryPlan("Stacking", new EffectParams([Red], 0.5f, 1f, dir), [Red], Group(fill: !nearest))!;
            var h2 = plan.Zones[2];
            var rgb = nearest ? RingFillMap.ApplyFrames(h2.Rgb, BottomEnd.High, false) : h2.Rgb;
            return Enumerable.Range(0, h2.Frames).Select(f => Enumerable.Range(0, 24).Count(i => rgb[((f * 24) + i) * 3] > 0)).ToArray();
        }

        var forward = Lit(Direction.Forward);
        var fullAt = Array.IndexOf(forward, 24);
        fullAt.Should().BePositive("the block fills completely");
        var firstLit = Array.FindIndex(forward, n => n > 0);
        var plan0 = LConnectLoops.TryPlan("Stacking", new EffectParams([Red], 0.5f, 1f), [Red], Group(fill: true))!.Zones[2];
        bool On(int f, int i) => plan0.Rgb[((f * 24) + i) * 3] > 0;
        var bottomFrames = Enumerable.Range(firstLit, fullAt - firstLit).Count(f => On(f, 0));
        var topFrames = Enumerable.Range(firstLit, fullAt - firstLit).Count(f => On(f, 12));
        bottomFrames.Should().BeGreaterThan(topFrames * 2, "the stack sits at the bottom; the top only flashes as blocks fall past");
        // WP-15: the block holds exactly two fan-aligned 12-dot deposits (strip dots 0..11, then 12..23); the upper one falls
        // from the top and lands on the lower. WP-16 (interpolated): an LED is dark only when both of its taps are dark, so the
        // last dark LEDs are 7/17 (taps strip 14/15, x ≈ 14.48) while strip dots 12..15 are still empty; LED 6/18 (x = 11.5)
        // already shows half of the lit strip dot 11
        Enumerable.Range(0, 24).Where(i => plan0.Rgb[((fullAt - 1) * 24 + i) * 3] == 0).Should().Equal([7, 17],
            "the falling upper deposit's last gap sits just above the lower deposit");
        (plan0.Rgb[((fullAt - 1) * 24 + 6) * 3], plan0.Rgb[((fullAt - 1) * 24 + 18) * 3]).Should().Be(((byte)127, (byte)127),
            "round_half_up(0.5 × 254) at the half-height LEDs");

        // Reverse plays the drain: the lit count falls where the forward count rises. WP-16: the drain is a property of the
        // unchanged body and the fill is a per-frame map, so it is counted on the WP-15 nearest placement of the same strip; the
        // rise/drop count is not robust to interpolated partial edge LEDs (measured: −11 vs −11 on the interpolated output)
        forward = Lit(Direction.Forward, nearest: true);
        var reverse = Lit(Direction.Reverse, nearest: true);
        reverse.Should().Contain(24);
        var drop = Enumerable.Range(1, reverse.Length - 1).Count(f => reverse[f] < reverse[f - 1]);
        var rise = Enumerable.Range(1, reverse.Length - 1).Count(f => reverse[f] > reverse[f - 1]);
        var fdrop = Enumerable.Range(1, forward.Length - 1).Count(f => forward[f] < forward[f - 1]);
        var frise = Enumerable.Range(1, forward.Length - 1).Count(f => forward[f] > forward[f - 1]);
        (drop - rise).Should().BeGreaterThan(fdrop - frise, "reverse drains rather than fills");
    }

    // ---------------------------------------------------------------- composition

    [Theory]
    [MemberData(nameof(TranslationalRoutes))]
    public void Fill_frames_are_mirror_symmetric_and_chain_reversal_swaps_the_bottom_end(string effect)
    {
        foreach (var half in new[] { false, true })
        {
            var h2 = LConnectLoops.TryPlan(effect, P(), RedGreen, Group(fill: true, half: half))!.Zones[2];
            for (var f = 0; f < h2.Frames; f++)
            {
                for (var i = 0; i < 24; i++)
                {
                    var m = RingFillMap.Mirror(i, half);
                    h2.Rgb.AsSpan(((f * 24) + i) * 3, 3).SequenceEqual(h2.Rgb.AsSpan(((f * 24) + m) * 3, 3)).Should().BeTrue(
                        $"{effect} frame {f} LED {i}: a mounting mirror leaves the fill unchanged");
                }
            }
        }

        var plain = LConnectLoops.TryPlan(effect, P(), RedGreen, Group())!.Zones[2].Rgb;
        var bottom = EffectGeometry.Of(effect).Bottom!.Value;
        var swapped = bottom == BottomEnd.Low ? BottomEnd.High : BottomEnd.Low;
        var sampling = effect == "Stacking" ? RingSampling.Linear : RingSampling.Nearest; // WP-16 policy
        LConnectLoops.TryPlan(effect, P(), RedGreen, Group(fill: true, reverseH2: true))!.Zones[2].Rgb
            .Should().Equal(RingFillMap.ApplyFrames(plain, swapped, false, sampling), $"{effect}: ReverseChain on the H2 swaps Low/High");
    }

    // ---------------------------------------------------------------- render loop

    private static ControllerDescriptor Chain(string id, string family, int units, int leds, ZoneLayout layout) =>
        new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));

    private static ControllerDescriptor[] Chains(ZoneLayout? h2Layout = null) =>
    [
        Chain("a", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("b", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
        Chain("h2", "H2", 1, 24, h2Layout ?? ZoneLayout.Ring(24)),
        Chain("lcd", "TL", 3, 78, ZoneLayout.Matrix(39, 2)),
    ];

    private static Scene GroupScene(ControllerDescriptor[] c, string effect, LoopPolicy policy = LoopPolicy.LConnect) =>
        new([Assignment.ForGroup("g", c.Select(x => new ZoneRef(x.Id, new ZoneId(0))).ToArray(), effect, P()) with { Policy = policy }]);

    private static LoopFrames Take(FrameBus bus, ControllerDescriptor c)
    {
        bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue($"{c.Id} got a loop");
        return pending.Loop;
    }

    [Fact]
    public void Render_loop_fills_the_h2_from_its_ring_provider_and_tags_the_geometry()
    {
        var c = Chains();
        using var bus = new FrameBus();
        var ring = new RingGeometry(true, false, 2);
        var loop = new RenderLoop(EffectRegistry.Default, bus) { ChainReversed = id => id.Value == "b", Ring = id => id.Value == "h2" ? ring : default };
        loop.SetControllers(c);
        loop.SetScene(GroupScene(c, "Stacking"));
        var expected = LConnectLoops.TryPlan("Stacking", P(), RedGreen, Group(fill: true))!;
        var h2 = Take(bus, c[2]);
        h2.Rgb.ToArray().Should().Equal(expected.Zones[2].Rgb);
        h2.Geometry.Should().Be("vertical-fill offset=+2 half=false bottom=High sampling=linear");
        Take(bus, c[0]).Geometry.Should().BeNull("TL zones carry no ring geometry");
        loop.LastLoopCompile!.Variants!.Single().Should().Contain("; H2 vertical-fill (bottom=High, interpolated):");

        loop.SetScene(GroupScene(c, "Rainbow"));
        Take(bus, c[2]).Geometry.Should().Be("ring (effect is rotational)");
        loop.LastLoopCompile!.Variants!.Single().Should().Contain("; H2 ring:");

        ring = default;
        loop.RepublishLoops(c[2].Id);
        Take(bus, c[2]).Geometry.Should().Be("ring");
    }

    [Theory]
    [InlineData("Rainbow")]
    [InlineData("Taichi")]
    [InlineData("Breathing")]
    public void Rotational_and_uniform_effects_are_byte_identical_with_fill_on(string effect)
    {
        byte[] Run(bool fill, LoopPolicy policy)
        {
            var c = Chains();
            using var bus = new FrameBus();
            var loop = new RenderLoop(EffectRegistry.Default, bus) { Ring = _ => new RingGeometry(fill, fill, 0) };
            loop.SetControllers(c);
            loop.SetScene(GroupScene(c, effect, policy));
            return Take(bus, c[2]).Rgb.ToArray();
        }

        foreach (var policy in new[] { LoopPolicy.LConnect, LoopPolicy.Generic })
        {
            Run(true, policy).Should().Equal(Run(false, policy), $"{effect} {policy}");
        }
    }

    [Fact]
    public void Generic_group_path_renders_the_h2_as_linear_24_and_places_it_by_height()
    {
        byte[] Run(bool fill, ZoneLayout layout, out string variant, out string? geometry)
        {
            var c = Chains(layout);
            using var bus = new FrameBus();
            var loop = new RenderLoop(EffectRegistry.Default, bus) { Ring = id => id.Value == "h2" ? new RingGeometry(fill, false, 0) : default };
            loop.SetControllers(c);
            loop.SetScene(GroupScene(c, "Runway", LoopPolicy.Generic));
            variant = loop.LastLoopCompile!.Variants!.Single();
            var l = Take(bus, c[2]);
            geometry = l.Geometry;
            return l.Rgb.ToArray();
        }

        var linear = Run(false, ZoneLayout.Linear(24), out _, out _);
        var filled = Run(true, ZoneLayout.Ring(24), out var v, out var g);
        filled.Should().Equal(RingFillMap.ApplyFrames(linear, BottomEnd.Low, false));
        v.Should().EndWith("; H2 vertical-fill (bottom=Low)");
        g.Should().Be("vertical-fill offset=+0 half=false bottom=Low");

        // the lit LEDs of every frame are whole mirror pairs, as in the vendor fill
        for (var f = 0; f < filled.Length / 72; f++)
        {
            var lit = Enumerable.Range(0, 24).Where(i => filled[(f * 72) + (i * 3)] + filled[(f * 72) + (i * 3) + 1] + filled[(f * 72) + (i * 3) + 2] > 0).ToArray();
            lit.All(i => lit.Contains(RingFillMap.Mirror(i, false))).Should().BeTrue($"frame {f}");
        }

        // a single-target generic H2 is not remapped (its body is the whole ring)
        var single = Chain("h2", "H2", 1, 24, ZoneLayout.Ring(24));
        using var bus = new FrameBus();
        var solo = new RenderLoop(EffectRegistry.Default, bus) { Ring = _ => new RingGeometry(true, false, 0) };
        solo.SetControllers([single]);
        solo.SetScene(new Scene([Assignment.ForGroup("s", [new ZoneRef(single.Id, new ZoneId(0))], "Runway", P()) with { Policy = LoopPolicy.Generic }]));
        Take(bus, single).Geometry.Should().Be("ring (per-device body)");
    }

    [Fact]
    public void Generic_group_of_the_h2_and_one_streamed_zone_still_fills_the_h2()
    {
        // Codex review: "multi-zone" counts logical members; an H2 grouped with a streamed GPU zone is a group, not a lone ring
        var gpu = TestSupport.Controller("gpu", ZoneLayout.Point);
        byte[] Run(bool fill, ZoneLayout layout, out string variant, out string? geometry)
        {
            ControllerDescriptor[] c = [Chain("h2", "H2", 1, 24, layout), gpu];
            using var bus = new FrameBus();
            var loop = new RenderLoop(EffectRegistry.Default, bus) { Ring = id => id.Value == "h2" ? new RingGeometry(fill, false, 0) : default };
            loop.SetControllers(c);
            loop.SetScene(GroupScene(c, "Runway", LoopPolicy.Generic));
            variant = loop.LastLoopCompile!.Variants!.Single();
            var l = Take(bus, c[0]);
            geometry = l.Geometry;
            return l.Rgb.ToArray();
        }

        var linear = Run(false, ZoneLayout.Linear(24), out _, out _);
        var filled = Run(true, ZoneLayout.Ring(24), out var v, out var g);
        filled.Should().Equal(RingFillMap.ApplyFrames(linear, BottomEnd.Low, false));
        v.Should().EndWith("; H2 vertical-fill (bottom=Low)");
        g.Should().Be("vertical-fill offset=+0 half=false bottom=Low");
    }

    [Fact]
    public void Calibration_holds_the_controller_and_release_restores_the_scene_loop()
    {
        var c = Chains();
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus) { Ring = _ => new RingGeometry(true, false, 0) };
        loop.SetControllers(c);
        loop.SetScene(GroupScene(c, "Meteor"));
        var scene = Take(bus, c[2]);

        loop.PublishCalibration(c[2].Id, RingFillMap.MarkerFrame(false));
        loop.IsHeld(c[2].Id).Should().BeTrue();
        var marker = Take(bus, c[2]);
        (marker.Frames, marker.Geometry).Should().Be((1, "calibration"));
        marker.Rgb.ToArray().Should().Equal(RingFillMap.MarkerFrame(false));
        marker.EffectIndex.Should().NotBe(scene.EffectIndex);

        loop.SetScene(GroupScene(c, "Wave"));
        bus[c[2].Id]!.TryConsumeLoop(out _).Should().BeFalse("a held controller gets no scene loop");
        Take(bus, c[0]);

        loop.EndCalibration(c[2].Id, republish: true);
        loop.IsHeld(c[2].Id).Should().BeFalse();
        Take(bus, c[2]).Rgb.ToArray().Should().Equal(LConnectLoops.TryPlan("Wave", P(), RedGreen, [new("TL", 3, 78), new("TL", 3, 78), new("H2", 1, 24, false, true), new("TL", 3, 78)])!.Zones[2].Rgb);

        var wrong = () => loop.PublishCalibration(c[2].Id, new byte[10]);
        wrong.Should().Throw<ArgumentException>();
    }
}
