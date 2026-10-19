using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// STACK-RETURN (design/OPUS-STACKING-WAVE-RETURN-20261018.md): the opt-in hold + reverse-flow drain after shared Stacking's
/// final full frame, over the accepted profile11 path (top 0..36 | side 36..72 | bottom 72..108 | GPU 108..120 | H2 ‖ RAM ‖ RAM ‖
/// ROG 120..144; all three TL and the H2 bindings locally reversed, top ReverseChain). Expected bytes come from literal stage
/// ranges, literal TL/streamed index arithmetic and the analytic weight re-derived in this file; spot values, stage order and
/// frame counts were computed independently (.artifacts-stack-return-opus-20261018/oracle_check.py). The oracle never calls
/// LoopReturnTail; the unchanged vendor body is the forward source and the accepted RingFillMap places the H2 ring.
/// </summary>
public class StackingReturnTests
{
    private const string Side = "lianli:rf:06-A8-F0-72-AB-3C";
    private const string Top = "lianli:rf:1B-7B-F1-72-AB-3C";
    private const string H2Id = "lianli:rf:EF-7E-63-62-32-E1";
    private const string Bottom = "lianli:rf:35-F7-EF-72-AB-3C";
    private const string Gpu = "msigpu:0:53011462";
    private const string Rog = "asusaura:19AF:0";
    private const string Ram71 = "enedram:p0:0x71";
    private const string Ram73 = "enedram:p0:0x73";

    private const int Total = 144;
    private const int Forward = 936;
    private const int Hold = 20;
    private const int Drain = 134;
    private const int Extended = 1090;
    private const int Interval = 48;

    /// <summary>Red 255 at vendor brightness 255: 255 · 255 &gt;&gt; 8.</summary>
    private const byte Full = 254;

    private static readonly string[] Controllers = [Side, Top, H2Id, Bottom, Gpu, Rog, Ram71, Ram73];

    /// <summary>profile11's target order: side, top, H2, bottom, GPU vertical, ROG, RAM71, RAM73, GPU horizontal.</summary>
    private static readonly ZoneRef[] Targets = [Z(Side), Z(Top), Z(H2Id), Z(Bottom), Z(Gpu), Z(Rog), Z(Ram71), Z(Ram73), Z(Gpu, 1)];

    /// <summary>Literal source ranges (low end first) and artistic reversal of profile11.</summary>
    private static readonly Dictionary<ZoneRef, (int Start, int Length, bool Reverse)> Range = new()
    {
        [Z(Top)] = (0, 36, true), [Z(Side)] = (36, 36, true), [Z(Bottom)] = (72, 36, true),
        [Z(Gpu)] = (108, 12, false), [Z(Gpu, 1)] = (108, 12, false),
        [Z(H2Id)] = (120, 24, true), [Z(Ram71)] = (120, 24, false), [Z(Ram73)] = (120, 24, false), [Z(Rog)] = (120, 24, false),
    };

    private static readonly int[] Expand = [0, 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10, 11];

    private static readonly Rgb Red = new(255, 0, 0);

    private static readonly LoopReturn Accepted = new(4000, 600, 24, 0.25);

    private static readonly Lazy<IReadOnlyList<byte[]>> Raw = new(() => LConnectLoops.UiModeFrames(7, Total, 255, 0, UiRed(), out _)!);

    private static ZoneRef Z(string id, int zone = 0) => new(new ControllerId(id), new ZoneId(zone));

    private static byte[,] UiRed()
    {
        var ui = new byte[6, 3];
        var c = LConnectLoops.CapColour(Red, LConnectLoops.RgbMax);
        (ui[0, 0], ui[0, 1], ui[0, 2]) = (c.R, c.G, c.B);
        return ui;
    }

    private static SpatialPath Profile11Path() => new([
        new SpatialStage(36, [new SpatialBinding(Z(Top), true)]),
        new SpatialStage(36, [new SpatialBinding(Z(Side), true)]),
        new SpatialStage(36, [new SpatialBinding(Z(Bottom), true)]),
        new SpatialStage(12, [new SpatialBinding(Z(Gpu)), new SpatialBinding(Z(Gpu, 1))]),
        new SpatialStage(24, [new SpatialBinding(Z(H2Id), true), new SpatialBinding(Z(Ram71)), new SpatialBinding(Z(Ram73)), new SpatialBinding(Z(Rog))]),
    ]);

    private static Assignment Profile11(LoopReturn? r, float speed = 0.75f, Direction dir = Direction.Forward) =>
        new Assignment(Targets, "Stacking", new EffectParams([Red], speed, 1f, dir), "case") { Path = Profile11Path(), LoopReturn = r };

    private static int Leds(ZoneRef z) => z.Controller.Value switch
    {
        Side or Top or Bottom => 78,
        H2Id => 24,
        Gpu => z.Zone.Value == 0 ? 12 : 4,
        Rog => 2,
        _ => 8,
    };

    private static LConnectZone ZoneOf(ZoneRef z) => z.Controller.Value switch
    {
        Side or Bottom => new("TL", 3, 78),
        Top => new("TL", 3, 78, ReverseChain: true),
        H2Id => new("H2", 1, 24, FaceOnFill: true),
        _ => new("Streamed", 0, Leds(z)),
    };

    private static LConnectPlan Plan(LoopReturn? r, IReadOnlyList<ZoneRef>? zones = null, Direction dir = Direction.Forward)
    {
        var zs = zones ?? Targets;
        var spans = Profile11Path().Resolve();
        var plan = LConnectLoops.TryPlan("Stacking", new EffectParams([Red], 0.75f, 1f, dir), [Red], zs.Select(ZoneOf).ToArray(),
            zs.Select(z => spans[z]).ToArray(), r, out var why);
        plan.Should().NotBeNull(why);
        return plan!;
    }

    /// <summary>The analytic drain weight, re-derived here (not LoopReturnTail): dot j at return frame k of R.</summary>
    private static double Weight(int j, int k, int dots = Total, int frames = Drain, int width = 24, double s = 0.25)
    {
        var u = (((dots + width) * (1 - ((double)k / frames))) - (j + 0.5)) / width;
        return u >= 1 ? 1 : u <= 0 ? 0 : u * u * (3 - (2 * u)) * (1 - s + (s * Math.Cos(4 * Math.PI * u)));
    }

    /// <summary>The red channel of source dot j in extended frame f: the vendor body, then the held full frame, then the drain.</summary>
    private static byte Src(int j, int f) =>
        f < Forward ? Raw.Value[f][j * 3]
        : f < Forward + Hold ? Full
        : (byte)Math.Floor((Full * Weight(j, f - Forward - Hold + 1)) + 0.5);

    /// <summary>The source dot a zone LED shows (literal TL fan-column / streamed midpoint arithmetic; H2 handled by the ring map).</summary>
    private static int SourceOf(ZoneRef z, int led)
    {
        var (start, length, reverse) = Range[z];
        if (ZoneOf(z).Family == "TL")
        {
            var col = led % 39; // both banks (rows 0 and 1) show the same column
            var q = ZoneOf(z).ReverseChain ? 38 - col : col;
            var local = ((q / 13) * 12) + Expand[q % 13];
            return start + (reverse ? 35 - local : local);
        }

        var n = Leds(z);
        var k = (((2 * led) + 1) * length) / (2 * n);
        return start + (reverse ? length - 1 - k : k);
    }

    /// <summary>The literal expected RGB of a zone over all extended frames.</summary>
    private static byte[] Expected(ZoneRef z, int frames = Extended)
    {
        var leds = Leds(z);
        if (z.Controller.Value == H2Id)
        {
            var strip = new byte[frames * 24 * 3];
            for (var f = 0; f < frames; f++)
            {
                for (var j = 0; j < 24; j++)
                {
                    strip[((f * 24) + j) * 3] = Src(SourceOf(z, j), f);
                }
            }

            return RingFillMap.ApplyFrames(strip, EffectGeometry.Of("Stacking").Bottom!.Value, false, RingSampling.Linear);
        }

        var rgb = new byte[frames * leds * 3];
        for (var f = 0; f < frames; f++)
        {
            for (var p = 0; p < leds; p++)
            {
                rgb[((f * leds) + p) * 3] = Src(SourceOf(z, p), f);
            }
        }

        return rgb;
    }

    private static byte[] FrameOf(byte[] rgb, int leds, int f) => rgb.AsSpan(f * leds * 3, leds * 3).ToArray();

    // ------------------------------------------------------------------------------------------------- frame arithmetic

    [Theory]
    [InlineData(0.75f, 48, 20, 134)]
    [InlineData(1f, 36, 27, 178)]
    [InlineData(0f, 84, 12, 77)]
    public void Frame_counts_round_up_whole_raw_tick_intervals_on_the_1600_Hz_model(float speed, int interval, int hold, int drain)
    {
        (LConnectLoops.SpeedType(speed) * LConnectLoops.UiIntervalBase).Should().Be(interval);
        (Accepted.HoldFrames(interval), Accepted.ReturnFrames(interval)).Should().Be((hold, drain));
        LoopReturn.FramesFor(0, interval).Should().Be(0);
        LoopReturn.FramesFor(600, 48).Should().Be(20, "960000 / 48000 is exact: no extra frame from binary rounding");
        RenderLoop.DeviceClockTicksPerSecond.Should().Be(1600, "the existing RF master model constant, reused");
        ((Forward + Hold + Drain) * Interval / 1600.0).Should().BeApproximately(32.70, 1e-9, "profile11: 1090 × 48 ticks at 1600 ticks/s");
        (Forward * Interval / 1600.0, Hold * Interval / 1600.0, Drain * Interval / 1600.0).Should().Be((28.08, 0.6, 4.02));
        var act = () => LoopReturn.FramesFor(600, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------------------------------------------- pure tail

    [Fact]
    public void The_weight_is_the_analytic_two_ripple_feather_and_channels_round_half_up()
    {
        // D = 10, W = 4, R = 7: k = 1 puts the boundary at 12, so dot 9 sits at u = 5/8 and dot 8 at k = 2 at u = 3/8
        LoopReturnTail.Weight(9, 10, 1, 7, 4, 0.5).Should().BeApproximately(0.341796875, 1e-12, "S(5/8) = 0.68359375, cos 2.5π = 0");
        LoopReturnTail.Weight(8, 10, 2, 7, 4, 0.5).Should().BeApproximately(0.158203125, 1e-12, "S(3/8) = 0.31640625, cos 1.5π = 0");
        LoopReturnTail.Weight(9, 10, 1, 7, 4, 0).Should().BeApproximately(0.68359375, 1e-12, "strength 0 is the plain smoothstep");
        foreach (var s in new[] { 0, 0.25, 0.5 })
        {
            // u = 1/2 (cos 2π = 1): S = 1/2 whatever the strength; u = 1/4 (cos π = −1): S · (1 − 2s)
            LoopReturnTail.Weight(0, 1, 1, 2, 2, s).Should().BeApproximately(0.5 * (1 - s + s), 1e-12);
            LoopReturnTail.Weight(0, 1, 2, 3, 2, s).Should().BeApproximately(0.15625 * (1 - (2 * s)), 1e-12, $"strength {s}");
            LoopReturnTail.Weight(0, 1, 1, 3, 2, s).Should().BeApproximately(0.84375 * (1 - (2 * s)), 1e-12, $"u = 3/4, strength {s}");
        }

        LoopReturnTail.Weight(0, 144, 1, 134, 24, 0.25).Should().Be(1, "the full region stays exactly full");
        LoopReturnTail.Weight(143, 144, 134, 134, 24, 0.25).Should().Be(0, "the last frame is black");
        (LoopReturnTail.Scale(1, 0.5), LoopReturnTail.Scale(5, 0.5), LoopReturnTail.Scale(254, 1), LoopReturnTail.Scale(255, 0.999999), LoopReturnTail.Scale(200, 0))
            .Should().Be(((byte)1, (byte)3, (byte)254, (byte)255, (byte)0), "half-up (not banker's) rounding, never above the held byte");
        for (var j = 0; j < Total; j++)
        {
            for (var k = 1; k <= Drain; k++)
            {
                var w = LoopReturnTail.Weight(j, Total, k, Drain, 24, 0.25);
                w.Should().Be(Weight(j, k));
                (w is >= 0 and <= 1).Should().BeTrue($"dot {j} frame {k}: {w}");
            }
        }
    }

    [Fact]
    public void The_source_tail_holds_then_drains_from_the_high_end_to_exact_black()
    {
        var raw = Raw.Value;
        raw.Should().HaveCount(Forward);
        raw[^1].Where((_, i) => i % 3 == 0).Should().OnlyContain(v => v == Full, "the final forward frame is full");
        var ext = LoopReturnTail.Extend(raw, Total, Hold, Drain, 24, 0.25, drainHighFirst: true, LoopReturn.MaxExtendedFrames);
        ext.Should().HaveCount(Extended);
        for (var f = 0; f < Forward; f++)
        {
            ext[f].Should().BeSameAs(raw[f], "the forward frames are kept as they are");
        }

        for (var f = Forward; f < Extended; f++)
        {
            for (var j = 0; j < Total; j++)
            {
                (ext[f][j * 3], ext[f][(j * 3) + 1], ext[f][(j * 3) + 2]).Should().Be((Src(j, f), (byte)0, (byte)0), $"frame {f} dot {j}");
            }
        }

        ext[Forward].Should().NotBeSameAs(raw[^1], "hold frames are copies");
        ext[Forward + Hold - 1].Should().Equal(raw[^1]);
        byte At(int f, int j) => ext[f][j * 3];
        var first = Forward + Hold;
        Enumerable.Range(0, Total).Where(j => At(first, j) != Full).Should().Equal(143);
        At(first, 143).Should().Be(248, "the first drain frame touches only the highest source dot");
        ext[^1].Should().OnlyContain(b => b == 0, "the loop closes on exact black");
        Enumerable.Range(0, Total).Where(j => At(Extended - 2, j) != 0).Should().Equal(0);

        // independently computed spot values
        (At(first + 39, 100), At(first + 59, 72), At(first + 79, 50), At(first + 132, 0), At(first + 129, 5))
            .Should().Be(((byte)106, (byte)156, (byte)107, (byte)1, (byte)0));

        // the inverse of the moving bar: inner (H2/RAM/ROG) first, then GPU, bottom, side, top: by first dimming and by full black
        var stages = new (string Name, int Start, int End, int Dims, int Black)[]
        {
            ("inner", 120, 144, 1, 38), ("gpu", 108, 120, 20, 47), ("bottom", 72, 108, 30, 76), ("side", 36, 72, 59, 105), ("top", 0, 36, 87, 134),
        };
        foreach (var (name, start, end, dims, black) in stages)
        {
            var span = Enumerable.Range(start, end - start).ToArray();
            Enumerable.Range(1, Drain).First(k => span.Any(j => At(first + k - 1, j) != Full)).Should().Be(dims, $"{name} first dims");
            Enumerable.Range(1, Drain).First(k => span.All(j => At(first + k - 1, j) == 0)).Should().Be(black, $"{name} is black");
        }

        // NOT the reversed forward sequence: undoing the frames would first change the top (the last deposit's bar)
        Enumerable.Range(0, Total).Where(j => raw[^2][j * 3] != raw[^1][j * 3]).Should().OnlyContain(j => j < 24);
        Enumerable.Range(0, 36).Select(j => At(first, j)).Should().OnlyContain(v => v == Full, "the drain leaves the top full at its start");

        // a travelling feather, never a global breathing: bounded width, non-increasing total, nothing above the held frame
        var energy = new long[Drain];
        for (var k = 1; k <= Drain; k++)
        {
            var f = first + k - 1;
            Enumerable.Range(0, Total).Count(j => At(f, j) is > 0 and < Full).Should().BeLessThanOrEqualTo(24, $"frame {f}");
            energy[k - 1] = Enumerable.Range(0, Total).Sum(j => (long)At(f, j));
            ext[f].Should().OnlyContain(b => b <= Full);
        }

        energy.Should().BeInDescendingOrder("the case only drains; ripples stay inside the feather").And.HaveElementAt(0, 36570);
    }

    [Fact]
    public void The_builder_mirrors_for_the_reverse_direction_and_checks_its_inputs()
    {
        var raw = LConnectLoops.UiModeFrames(7, 24, 255, 1, UiRed(), out _)!;
        var high = LoopReturnTail.Extend(raw, 24, 2, 9, 4, 0.5, drainHighFirst: true, 1000);
        var low = LoopReturnTail.Extend(raw, 24, 2, 9, 4, 0.5, drainHighFirst: false, 1000);
        for (var f = raw.Count; f < high.Count; f++)
        {
            for (var j = 0; j < 24; j++)
            {
                low[f].AsSpan(j * 3, 3).ToArray().Should().Equal(high[f].AsSpan((23 - j) * 3, 3).ToArray(), $"frame {f} dot {j} mirrored");
            }
        }

        var bad = new List<Action>
        {
            () => LoopReturnTail.Extend(raw, 24, 0, 9, 4, 0.51, true, 1000),
            () => LoopReturnTail.Extend(raw, 24, 0, 9, 4, double.NaN, true, 1000),
            () => LoopReturnTail.Extend(raw, 24, -1, 9, 4, 0.25, true, 1000),
            () => LoopReturnTail.Extend(raw, 24, 0, 0, 4, 0.25, true, 1000),
            () => LoopReturnTail.Extend(raw, 24, 0, 9, 0, 0.25, true, 1000),
            () => LoopReturnTail.Extend(raw, 23, 0, 9, 4, 0.25, true, 1000),
            () => LoopReturnTail.Extend([], 24, 0, 9, 4, 0.25, true, 1000),
            () => LoopReturnTail.Extend(raw, 24, 0, 9, 4, 0.25, true, raw.Count + 8),
            () => LoopReturnTail.Extend(raw, 24, int.MaxValue, int.MaxValue, 4, 0.25, true, LoopReturn.MaxExtendedFrames),
        };
        foreach (var act in bad)
        {
            act.Should().Throw<ArgumentException>();
        }
    }

    // ------------------------------------------------------------------------------------------------- vendor plan

    [Fact]
    public void An_absent_return_is_the_legacy_plan_byte_for_byte_and_the_forward_prefix_never_changes()
    {
        var spans = Profile11Path().Resolve();
        var zones = Targets.Select(ZoneOf).ToArray();
        var legacy = LConnectLoops.TryPlan("Stacking", new EffectParams([Red], 0.75f, 1f, Direction.Forward), [Red], zones,
            Targets.Select(z => spans[z]).ToArray(), out var why)!;
        legacy.Should().NotBeNull(why);
        var none = Plan(null);
        var ext = Plan(Accepted);
        none.Variant.Should().Be(legacy.Variant).And.NotContain("loop return");
        ext.Variant.Should().Contain(", loop return: hold 20 + reverse-flow drain 134 frames from the high source end")
            .And.Contain("explicit spatial path of 144 source dots").And.Contain("fan-aligned Stacking");
        for (var i = 0; i < Targets.Length; i++)
        {
            var leds = Leds(Targets[i]);
            (legacy.Zones[i].Frames, legacy.Zones[i].IntervalMs).Should().Be((Forward, Interval));
            (none.Zones[i].Frames, none.Zones[i].IntervalMs).Should().Be((legacy.Zones[i].Frames, legacy.Zones[i].IntervalMs));
            none.Zones[i].Rgb.Should().Equal(legacy.Zones[i].Rgb, $"{Targets[i]}: absent = legacy byte for byte");
            (ext.Zones[i].Frames, ext.Zones[i].IntervalMs).Should().Be((Extended, Interval), $"{Targets[i]}: one shared extended timeline");
            ext.Zones[i].Rgb.AsSpan(0, Forward * leds * 3).ToArray().Should().Equal(legacy.Zones[i].Rgb, $"{Targets[i]}: forward bytes exactly");
        }

        ext.PeriodSeconds.Should().BeApproximately(52.32, 1e-9, "nominal header units (frames × raw interval), not physical seconds");
        ext.Synchronous.Should().BeTrue();
    }

    [Fact]
    public void Every_profile11_zone_carries_the_literal_extended_oracle()
    {
        var plan = Plan(Accepted);
        for (var i = 0; i < Targets.Length; i++)
        {
            var z = Targets[i];
            var leds = Leds(z);
            var rgb = plan.Zones[i].Rgb;
            rgb.Should().Equal(Expected(z), $"{z}: all {Extended} frames");
            FrameOf(rgb, leds, Extended - 1).Should().OnlyContain(b => b == 0, $"{z}: exact black last frame");
            for (var h = Forward; h < Forward + Hold; h++)
            {
                FrameOf(rgb, leds, h).Should().Equal(FrameOf(rgb, leds, Forward - 1), $"{z}: hold frame {h} is the full frame");
            }

            if (ZoneOf(z).Family == "TL")
            {
                for (var f = 0; f < Extended; f += 7)
                {
                    rgb.AsSpan(f * 78 * 3, 39 * 3).ToArray().Should().Equal(rgb.AsSpan(((f * 78) + 39) * 3, 39 * 3).ToArray(), $"{z} frame {f}: both banks");
                }
            }
        }
    }

    [Fact]
    public void The_physical_return_starts_at_the_inner_group_and_finishes_at_the_top()
    {
        var plan = Plan(Accepted);
        var first = Forward + Hold;
        int FirstDim(ZoneRef z) => Enumerable.Range(first, Drain).First(f => FrameOf(plan.Zones[Array.IndexOf(Targets, z)].Rgb, Leds(z), f)
            .Where((_, i) => i % 3 == 0).Any(v => v < Full)) - first + 1;
        int Black(ZoneRef z) => Enumerable.Range(first, Drain).First(f => FrameOf(plan.Zones[Array.IndexOf(Targets, z)].Rgb, Leds(z), f)
            .All(v => v == 0)) - first + 1;

        // H2/RAM/ROG start together (one parallel stage), then GPU, bottom, side; the top clears on the very last frame
        var inner = new[] { Z(H2Id), Z(Ram71), Z(Ram73), Z(Rog) };
        var order = new[] { Z(Gpu), Z(Bottom), Z(Side), Z(Top) };
        inner.Select(FirstDim).Max().Should().BeLessThan(order.Select(FirstDim).Min());
        order.Select(FirstDim).Should().BeInAscendingOrder();
        order.Select(Black).Should().BeInAscendingOrder();
        Black(Z(Top)).Should().Be(Drain);
        inner.Select(Black).Max().Should().BeLessThan(Black(Z(Bottom)));

        // within the inner stage the drain runs top-down: RAM/ROG lose their high (path-upper) LEDs first, the H2 its ring bottom-up
        // equivalent of the reversed binding; GPU vertical drains its upper LEDs first, the TLs their accepted directions
        var ram = plan.Zones[Array.IndexOf(Targets, Z(Ram71))].Rgb;
        var ramFirst = Enumerable.Range(first, Drain).First(f => FrameOf(ram, 8, f).Where((_, i) => i % 3 == 0).Any(v => v < Full));
        FrameOf(ram, 8, ramFirst)[7 * 3].Should().BeLessThan(Full, "RAM LED 7 (path-high) dims first");
        FrameOf(ram, 8, ramFirst)[0].Should().Be(Full);
        var gpu = plan.Zones[Array.IndexOf(Targets, Z(Gpu))].Rgb;
        var gpuFirst = Enumerable.Range(first, Drain).First(f => FrameOf(gpu, 12, f).Where((_, i) => i % 3 == 0).Any(v => v < Full));
        FrameOf(gpu, 12, gpuFirst)[11 * 3].Should().BeLessThan(Full, "GPU vertical LED 11 dims first");
    }

    [Fact]
    public void The_reverse_direction_drains_from_the_low_source_end()
    {
        var plan = Plan(Accepted, dir: Direction.Reverse);
        plan.Variant.Should().Contain("from the low source end");
        var first = Forward + Hold;
        var top = plan.Zones[Array.IndexOf(Targets, Z(Top))].Rgb;
        var ram = plan.Zones[Array.IndexOf(Targets, Z(Ram71))].Rgb;
        FrameOf(top, 78, first).Where((_, i) => i % 3 == 0).Should().Contain(v => v < Full, "the bar ended at the low end (top)");
        FrameOf(ram, 8, first).Where((_, i) => i % 3 == 0).Should().OnlyContain(v => v == Full);
        int Black(byte[] rgb, int leds) => Enumerable.Range(first, Drain).First(f => FrameOf(rgb, leds, f).All(v => v == 0)) - first + 1;
        (Black(top, 78), Black(ram, 8)).Should().Be((47, 133), "mirrored: the top clears early, the high end last (independently computed)");
        FrameOf(ram, 8, Extended - 1).Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void A_singleton_or_partial_member_set_keeps_the_path_total_and_the_tail_timing()
    {
        var full = Plan(Accepted);
        foreach (var only in new[] { Z(Top), Z(H2Id), Z(Bottom) })
        {
            var lone = Plan(Accepted, [only]);
            var at = Array.IndexOf(Targets, only);
            (lone.Zones[0].Frames, lone.Zones[0].IntervalMs).Should().Be((Extended, Interval), $"{only}");
            lone.Zones[0].Rgb.Should().Equal(full.Zones[at].Rgb, $"{only}: its part of the full plan, over the full 144-dot total");
        }

        var part = new[] { Z(Side), Z(Gpu), Z(Ram73) };
        var some = Plan(Accepted, part);
        for (var i = 0; i < part.Length; i++)
        {
            some.Zones[i].Rgb.Should().Equal(full.Zones[Array.IndexOf(Targets, part[i])].Rgb, $"{part[i]}");
        }
    }

    // ------------------------------------------------------------------------------------------------- validation

    public static TheoryData<string, LoopReturn, string> BadOptions => new()
    {
        { "duration NaN", new LoopReturn(double.NaN), "duration" },
        { "duration 999", new LoopReturn(999), "duration" },
        { "duration 20001", new LoopReturn(20001), "duration" },
        { "duration +inf", new LoopReturn(double.PositiveInfinity), "duration" },
        { "hold -1", new LoopReturn(HoldMs: -1), "hold" },
        { "hold 2001", new LoopReturn(HoldMs: 2001), "hold" },
        { "hold NaN", new LoopReturn(HoldMs: double.NaN), "hold" },
        { "width 1", new LoopReturn(WaveWidthDots: 1), "wave width" },
        { "width 97", new LoopReturn(WaveWidthDots: 97), "wave width" },
        { "strength -0.01", new LoopReturn(WaveStrength: -0.01), "wave strength" },
        { "strength 0.51", new LoopReturn(WaveStrength: 0.51), "wave strength" },
        { "strength NaN", new LoopReturn(WaveStrength: double.NaN), "wave strength" },
        { "strength -inf", new LoopReturn(WaveStrength: double.NegativeInfinity), "wave strength" },
    };

    [Theory]
    [MemberData(nameof(BadOptions))]
    public void Non_finite_or_out_of_range_options_are_rejected(string name, LoopReturn r, string message)
    {
        var act = Profile11(r).Validate;
        act.Should().Throw<ArgumentException>(name).WithMessage($"*{message}*");
    }

    [Fact]
    public void Boundary_options_are_accepted_and_unsupported_routes_are_refused_not_ignored()
    {
        Profile11(new LoopReturn(1000, 0, 2, 0)).Validate();
        Profile11(new LoopReturn(20000, 2000, 96, 0.5)).Validate();
        Profile11(null).Validate();
        new LoopReturn().Should().Be(Accepted, "the defaults are the accepted design values; value equality");
        var a = Profile11(null);
        (a with { LoopReturn = new LoopReturn() }).Should().Be(a with { LoopReturn = Accepted }, "the record equality includes the options");
        (a with { LoopReturn = Accepted }).Should().NotBe(a).And.NotBe(a with { LoopReturn = Accepted with { WaveWidthDots = 12 } });

        var program = Profile11(Accepted) with { Strategy = ContentStrategy.Program };
        program.Invoking(a => a.Validate()).Should().Throw<ArgumentException>().WithMessage("*only with Vendor Stacking*");
        var runway = Profile11(Accepted) with { EffectId = "Runway" };
        runway.Invoking(a => a.Validate()).Should().Throw<ArgumentException>().WithMessage("*only with Vendor Stacking*");
        var noPath = Profile11(Accepted) with { Path = null };
        noPath.Invoking(a => a.Validate()).Should().Throw<ArgumentException>().WithMessage("*explicit spatial path*");
        noPath.ValidatePath(); // the path rules alone still pass: the return is what is refused
        var frames = Profile11(Accepted) with { Strategy = ContentStrategy.Frames, Path = null, FrameSetId = "x" };
        frames.Invoking(a => a.Validate()).Should().Throw<ArgumentException>().WithMessage("*only with Vendor Stacking*");

        // the frame bound is defence in depth: the largest playable Stacking path (300 dots, 3900 frames) at the fastest interval
        // with every option at its maximum stays within it
        var max = new LoopReturn(20000, 2000, 96, 0.5);
        LConnectLoops.UiStackFrames(300).Should().Be(3900);
        (3900 + max.HoldFrames(36) + max.ReturnFrames(36)).Should().Be(4878).And.BeLessThanOrEqualTo(LoopReturn.MaxExtendedFrames);
        LConnectLoops.LoopReturnRefusal(1f, 300, max).Should().BeNull();
        LConnectLoops.LoopReturnRefusal(1f, 12, max).Should().BeNull("outside the vendor range the plan itself falls back, as without a return");
        LConnectLoops.UiModeRefusal(7, 12).Should().Be("UiMode7 clamps 12 dots to 24 (vendor range 24..720): not reproduced");
        LConnectLoops.UiModeRefusal(7, 312).Should().Be("UiMode7 over 312 dots needs 4212 frames, beyond the vendor's 4096-frame scratch");
        LConnectLoops.UiModeRefusal(7, 144).Should().BeNull();
    }

    [Fact]
    public void Try_plan_refuses_a_return_it_cannot_carry_instead_of_dropping_it()
    {
        var p = new EffectParams([Red], 0.75f, 1f, Direction.Forward);
        LConnectLoops.TryPlan("Stacking", p, [Red], [ZoneOf(Z(Side)), ZoneOf(Z(Top))], null, Accepted, out var why).Should().BeNull();
        why.Should().Contain("loop return needs shared Stacking over an explicit spatial path").And.Contain("without a path");
        var spans = new[] { new SpatialSpan(0, 39, false, 78), new SpatialSpan(39, 39, false, 78) };
        LConnectLoops.TryPlan("Runway", p, [Red], [ZoneOf(Z(Side)), ZoneOf(Z(Bottom))], spans, Accepted, out why).Should().BeNull();
        why.Should().Contain("not Runway");
    }

    // ------------------------------------------------------------------------------------------------- engine

    private sealed class Rig : IDisposable
    {
        private readonly FrameBus _bus = new();

        public Rig(IEnumerable<string>? present = null)
        {
            Loop = new RenderLoop(new EffectRegistry([.. EffectRegistry.CreateBuiltIns()]), _bus)
            {
                ChainReversed = id => id.Value == Top,
                Ring = id => id.Value == H2Id ? new RingGeometry(true, false, 0) : default,
            };
            Loop.SetControllers((present ?? Controllers).Select(Descriptor).ToArray());
        }

        public RenderLoop Loop { get; }

        public static ControllerDescriptor Descriptor(string id) => id switch
        {
            Side or Top or Bottom => Chain(id, "TL", 3, ZoneLayout.Matrix(39, 2)),
            H2Id => Chain(id, "H2", 1, ZoneLayout.Ring(24)),
            Gpu => TestSupport.Controller(id, ZoneLayout.Linear(12), ZoneLayout.Linear(4)),
            Rog => TestSupport.Controller(id, ZoneLayout.Linear(2)),
            _ => TestSupport.Controller(id, ZoneLayout.Linear(8)),
        };

        public static ControllerDescriptor Chain(string id, string family, int units, ZoneLayout layout) =>
            new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
                [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(4000, 20, 12288));

        public void Dispose() => _bus.Dispose();

        public Dictionary<string, LoopFrames> Drain() =>
            Controllers.Where(id => _bus[new ControllerId(id)]?.HasPendingLoop == true).ToDictionary(id => id, id =>
            {
                _bus[new ControllerId(id)]!.TryConsumeLoop(out var pending).Should().BeTrue();
                return pending.Loop;
            });

        /// <summary>Every streamed zone's pixels at the centre of frame <paramref name="frame"/> of the 48-tick timeline (T0 = 0).</summary>
        public Dictionary<ZoneRef, byte[]> Streamed(int frame)
        {
            Loop.RenderFrame((frame + 0.5) * Interval / 1000.0, 0);
            var result = new Dictionary<ZoneRef, byte[]>();
            foreach (var id in new[] { Gpu, Rog, Ram71, Ram73 })
            {
                var mailbox = _bus[new ControllerId(id)];
                if (mailbox is null || !mailbox.TryConsume(out var f))
                {
                    continue;
                }

                for (var zi = 0; zi < f.Zones.Length; zi++)
                {
                    result[Z(id, zi)] = f.Zones[zi].SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
                }
            }

            return result;
        }
    }

    [Fact]
    public void The_engine_publishes_one_extended_timeline_to_every_rf_loop_and_native_zone()
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Profile11(Accepted)]));
        var loops = rig.Drain();
        var plan = Plan(Accepted);
        loops.Keys.Should().BeEquivalentTo(new[] { Side, Top, H2Id, Bottom });
        foreach (var id in loops.Keys)
        {
            var at = Array.IndexOf(Targets, Z(id));
            (loops[id].Frames, loops[id].IntervalMs).Should().Be((Extended, (double)Interval), id);
            loops[id].Rgb.ToArray().Should().Equal(plan.Zones[at].Rgb, id);
        }

        loops.Values.Select(l => l.EffectIndex).Distinct().Should().ContainSingle("one shared effect index");
        loops[H2Id].Geometry.Should().StartWith("vertical-fill").And.EndWith("sampling=linear");
        rig.Loop.PlayedSliceCount.Should().Be(5, "GPU vertical + horizontal, ROG, both RAM play the vendor frames");
        var report = rig.Loop.ClockReport().Single();
        (report.ReferenceFrames, report.ReferenceInterval).Should().Be((Extended, (double)Interval));
        rig.Loop.LastLoopCompile!.Variants!.Should().ContainSingle().Which.Should()
            .Contain("Vendor:Stacking").And.Contain("loop return: hold 20 + reverse-flow drain 134 frames from the high source end")
            .And.Contain("spatial path 144 dots").And.NotContain("NOT applied");

        foreach (var frame in new[] { 0, 500, Forward - 1, Forward, Forward + Hold - 1, Forward + Hold, 1000, 1050, Extended - 1 })
        {
            var streamed = rig.Streamed(frame);
            streamed.Keys.Should().BeEquivalentTo(new[] { Z(Gpu), Z(Gpu, 1), Z(Rog), Z(Ram71), Z(Ram73) });
            foreach (var (z, px) in streamed)
            {
                px.Should().Equal(FrameOf(Expected(z), Leds(z), frame), $"{z} frame {frame}");
            }
        }
    }

    [Fact]
    public void Missing_members_keep_the_tail_timing_and_bytes()
    {
        var full = Plan(Accepted);
        foreach (var only in new[] { Top, H2Id })
        {
            using var lone = new Rig([only]);
            lone.Loop.SetScene(new Scene([Profile11(Accepted)]), allowMissingZones: true);
            var l = lone.Drain()[only];
            (l.Frames, l.IntervalMs).Should().Be((Extended, (double)Interval), only);
            l.Rgb.ToArray().Should().Equal(full.Zones[Array.IndexOf(Targets, Z(only))].Rgb, only);
        }

        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Profile11(Accepted)]));
        var first = rig.Drain();
        rig.Loop.SetControllers(Controllers.Where(id => id != Top).Select(Rig.Descriptor).ToArray());
        rig.Drain().Should().BeEmpty("the explicit path keeps every other range and byte: nothing is re-sent");
        rig.Loop.SetControllers(Controllers.Select(Rig.Descriptor).ToArray());
        var back = rig.Drain();
        foreach (var id in first.Keys)
        {
            back[id].Rgb.ToArray().Should().Equal(first[id].Rgb.ToArray(), id);
        }
    }

    private const string NotApplied = "loop return NOT applied (hold 600 ms, then 4000 ms reverse-flow drain (wave 24 dots, ripple 0.25))";

    private const string Applied = "loop return: hold 20 + reverse-flow drain 134 frames from the high source end";

    private static readonly string[] Natives = [Gpu, Rog, Ram71, Ram73];

    /// <summary>Review2: the compile report is this compilation's, never a leftover vendor success (no RF carrier injected).</summary>
    [Fact]
    public void A_true_native_only_rig_reports_the_unapplied_return_and_empty_or_legacy_scenes_leave_nothing_stale()
    {
        // started with the native members only: no RF descriptor at all
        using var native = new Rig(Natives);
        native.Loop.LastLoopCompile.Should().BeNull("nothing compiled yet");
        native.Loop.SetScene(new Scene([Profile11(Accepted)]), allowMissingZones: true);
        native.Drain().Should().BeEmpty();
        native.Loop.PlayedSliceCount.Should().Be(0, "no vendor plan without a Lian Li zone: the members render live");
        native.Streamed(0).Keys.Should().BeEquivalentTo(new[] { Z(Gpu), Z(Gpu, 1), Z(Rog), Z(Ram71), Z(Ram73) });
        var report = native.Loop.LastLoopCompile!;
        (report.EffectIndex, report.Published, report.Unchanged, report.Truncated.Count).Should().Be((0u, 0, 0, 0), "a zero-upload report");
        report.Variants!.Should().ContainSingle().Which.Should().Contain("Program:Stacking live").And.Contain(NotApplied).And.NotContain(Applied);

        // the accepted profile11 (no return) adds no variant there: no report rather than the previous one
        native.Loop.SetScene(new Scene([Profile11(null)]), allowMissingZones: true);
        native.Loop.LastLoopCompile.Should().BeNull();
        native.Loop.SetScene(new Scene([Profile11(Accepted)]), allowMissingZones: true);
        native.Loop.LastLoopCompile!.Variants!.Should().ContainSingle().Which.Should().Contain(NotApplied);
        native.Loop.SetScene(Scene.Empty);
        native.Loop.LastLoopCompile.Should().BeNull("an empty scene without uploaded-loop zones has nothing to report");

        // with RF chains an empty scene keeps the legacy report: their uncovered zones go black, no variant
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Profile11(Accepted)]));
        rig.Drain();
        rig.Loop.SetScene(Scene.Empty);
        var black = rig.Loop.LastLoopCompile!;
        (black.Published, black.Unchanged, black.Variants!.Count).Should().Be((4, 0, 0));
        rig.Drain().Values.Should().OnlyContain(l => l.Frames == 1);

        // no controller at all: the scene is kept but nothing compiles, so nothing is reported until the controllers appear
        using var none = new Rig([]);
        none.Loop.SetScene(new Scene([Profile11(Accepted)]), allowMissingZones: true);
        none.Loop.LastLoopCompile.Should().BeNull();
        none.Loop.SetControllers(Controllers.Select(Rig.Descriptor).ToArray());
        none.Drain().Values.Should().HaveCount(4).And.OnlyContain(l => l.Frames == Extended);
        (none.Loop.LastLoopCompile!.Published, none.Loop.PlayedSliceCount).Should().Be((4, 5));
    }

    /// <summary>Review2 (Root repro return-fallback-round1-repro.json): full profile12, then the RF chains go and come back.</summary>
    [Fact]
    public void Losing_every_rf_chain_replaces_the_vendor_report_and_reconnect_restores_the_extended_timeline()
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Profile11(Accepted)]));
        var first = rig.Drain();
        var applied = rig.Loop.LastLoopCompile!;
        (applied.Published, applied.Unchanged).Should().Be((4, 0));
        applied.Variants!.Should().ContainSingle().Which.Should().Contain("Vendor:Stacking").And.Contain(Applied).And.NotContain("NOT applied");

        // one RF chain lost: the explicit path keeps every other byte, nothing is re-sent, the fresh report still applies the tail
        rig.Loop.SetControllers(Controllers.Where(id => id != H2Id).Select(Rig.Descriptor).ToArray());
        rig.Drain().Should().BeEmpty();
        var partial = rig.Loop.LastLoopCompile!;
        partial.Should().NotBeSameAs(applied);
        (partial.Published, partial.Unchanged).Should().Be((0, 3));
        partial.Variants!.Should().ContainSingle().Which.Should().Contain(Applied).And.NotContain("NOT applied");

        // every RF chain lost (the native members only): live generic fallback without the tail, and the report says so
        rig.Loop.SetControllers(Natives.Select(Rig.Descriptor).ToArray());
        rig.Drain().Should().BeEmpty();
        rig.Loop.PlayedSliceCount.Should().Be(0);
        var lost = rig.Loop.LastLoopCompile!;
        lost.Should().NotBeSameAs(partial).And.NotBeSameAs(applied);
        (lost.EffectIndex, lost.Published, lost.Unchanged, lost.Truncated.Count).Should().Be((0u, 0, 0, 0), "nothing was transmitted");
        lost.Variants!.Should().ContainSingle().Which.Should().Contain("Program:Stacking live").And.Contain(NotApplied)
            .And.NotContain("Vendor:Stacking").And.NotContain(Applied);
        rig.Streamed(Forward + Hold).Keys.Should().BeEquivalentTo(new[] { Z(Gpu), Z(Gpu, 1), Z(Rog), Z(Ram71), Z(Ram73) }, "the native members keep rendering");

        // reconnect: the vendor plan, the 1090-frame timeline and the exact bytes return with a fresh effect index
        rig.Loop.SetControllers(Controllers.Select(Rig.Descriptor).ToArray());
        var back = rig.Drain();
        back.Keys.Should().BeEquivalentTo(first.Keys);
        foreach (var id in first.Keys)
        {
            (back[id].Frames, back[id].IntervalMs).Should().Be((Extended, (double)Interval), id);
            back[id].Rgb.ToArray().Should().Equal(first[id].Rgb.ToArray(), id);
        }

        back.Values.Select(l => l.EffectIndex).Distinct().Should().ContainSingle();
        rig.Loop.PlayedSliceCount.Should().Be(5);
        var restored = rig.Loop.LastLoopCompile!;
        (restored.Published, restored.Unchanged).Should().Be((4, 0));
        restored.EffectIndex.Should().Be(back[Top].EffectIndex).And.NotBe(applied.EffectIndex);
        restored.Variants.Should().Equal(applied.Variants!);
        var clock = rig.Loop.ClockReport().Single();
        (clock.ReferenceFrames, clock.ReferenceInterval).Should().Be((Extended, (double)Interval));

        // every controller absent: nothing compiles, the applied report does not linger; they come back with the tail
        rig.Loop.SetControllers(Array.Empty<ControllerDescriptor>());
        rig.Loop.LastLoopCompile.Should().BeNull();
        rig.Loop.PlayedSliceCount.Should().Be(0);
        rig.Loop.SetControllers(Controllers.Select(Rig.Descriptor).ToArray());
        rig.Drain().Values.Should().HaveCount(4).And.OnlyContain(l => l.Frames == Extended);
        rig.Loop.LastLoopCompile!.Variants.Should().Equal(applied.Variants!);
    }

    [Fact]
    public void A_late_tl_stage_that_cannot_play_falls_back_and_reports_the_unapplied_return()
    {
        using var rig = new Rig(Controllers.Where(id => id != Side));
        rig.Loop.SetScene(new Scene([Profile11(Accepted)]), allowMissingZones: true);
        rig.Drain();
        // the side controller reappears with two fans: its 36-dot stage is no longer 12 per fan, so the recompile falls back
        rig.Loop.SetControllers([.. Controllers.Where(id => id != Side).Select(Rig.Descriptor), Rig.Chain(Side, "TL", 2, ZoneLayout.Matrix(26, 2))]);
        rig.Loop.LastLoopCompile!.Variants!.Should().ContainSingle().Which.Should()
            .Contain("Program:Stacking generic").And.Contain("not 12 per fan").And.Contain("loop return NOT applied");
        rig.Drain().Values.Should().OnlyContain(l => l.Frames != Extended, "the generic fallback has no tail");
    }

    [Fact]
    public void Toggling_or_revising_the_return_recompiles_and_removing_it_restores_the_accepted_bytes()
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Profile11(null)]));
        var legacy = rig.Drain();
        legacy.Values.Should().OnlyContain(l => l.Frames == Forward);
        rig.Loop.SetScene(new Scene([Profile11(null)]));
        rig.Drain().Should().BeEmpty("unchanged content is not re-sent");

        var indices = new HashSet<uint>(legacy.Values.Select(l => l.EffectIndex));
        Dictionary<string, LoopFrames> Publish(LoopReturn? r, int frames)
        {
            rig.Loop.SetScene(new Scene([Profile11(r)]));
            var got = rig.Drain();
            got.Keys.Should().BeEquivalentTo(new[] { Side, Top, H2Id, Bottom }, $"{r}: every RF loop is re-sent");
            got.Values.Should().OnlyContain(l => l.Frames == frames, $"{r}");
            indices.Add(got.Values.Select(l => l.EffectIndex).Distinct().Single()).Should().BeTrue("a fresh shared effect index");
            return got;
        }

        var on = Publish(Accepted, Extended);
        foreach (var id in on.Keys)
        {
            var bytes = Forward * on[id].LedCount * 3;
            on[id].Rgb[..bytes].ToArray().Should().Equal(legacy[id].Rgb.ToArray(), $"{id}: forward prefix unchanged");
        }

        rig.Loop.SetScene(new Scene([Profile11(new LoopReturn(4000, 600, 24, 0.25))]));
        rig.Drain().Should().BeEmpty("an equal option set compiles to equal content");
        var ripple = Publish(Accepted with { WaveStrength = 0.3 }, Extended);
        ripple[Side].Rgb.ToArray().Should().NotEqual(on[Side].Rgb.ToArray(), "a revised ripple changes the uploaded bytes");
        Publish(Accepted with { HoldMs = 0 }, Forward + Drain);
        Publish(Accepted with { DurationMs = 1000 }, Forward + Hold + 34);
        var off = Publish(null, Forward);
        foreach (var id in off.Keys)
        {
            off[id].Rgb.ToArray().Should().Equal(legacy[id].Rgb.ToArray(), $"{id}: removing the return restores the accepted bytes exactly");
            off[id].Geometry.Should().Be(legacy[id].Geometry);
        }
    }

    [Fact]
    public void Set_scene_rejects_a_bad_return_before_publishing_and_keeps_the_running_scene()
    {
        using var rig = new Rig();
        var accepted = new Scene([Profile11(null)]);
        rig.Loop.SetScene(accepted);
        rig.Drain();
        foreach (var bad in new[] { Profile11(new LoopReturn(WaveStrength: double.NaN)), Profile11(Accepted) with { Strategy = ContentStrategy.Program } })
        {
            var act = () => rig.Loop.SetScene(new Scene([bad]), allowMissingZones: true);
            act.Should().Throw<ArgumentException>();
            rig.Loop.Scene.Should().BeSameAs(accepted);
            rig.Drain().Should().BeEmpty("nothing was published");
        }
    }
}
