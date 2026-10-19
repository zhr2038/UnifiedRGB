using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// SPATIAL-PATH (Codex design 2026-10-18): the optional explicit source path. Expected coordinates come from literal stage
/// ranges and literal pass arithmetic in this file (pass p of shared Stacking over D dots ends at F_p = (p+1)·D − 6p(p+1) − 1
/// and deposits band [D − 12(p+1), D − 12p) for vendor direction 0); the oracle never calls the production resolver,
/// strip resampler or streamed-slice helpers. The unchanged vendor body (UiModeFrames) is the source the oracle samples.
/// </summary>
public class SpatialPathTests
{
    private const string Side = "lianli:rf:06-A8-F0-72-AB-3C";
    private const string Top = "lianli:rf:1B-7B-F1-72-AB-3C";
    private const string H2Id = "lianli:rf:EF-7E-63-62-32-E1";
    private const string Bottom = "lianli:rf:35-F7-EF-72-AB-3C";
    private const string Gpu = "native:gpu";
    private const string Rog = "native:rog";
    private const string Ram71 = "native:ram71";
    private const string Ram73 = "native:ram73";

    /// <summary>The live target enumeration: side, top, H2, bottom, GPU, ROG, RAM71, RAM73.</summary>
    private static readonly string[] Enumeration = [Side, Top, H2Id, Bottom, Gpu, Rog, Ram71, Ram73];

    /// <summary>Literal candidate ranges (low source end first): GPU 0..12, inner 12..36, top 36..72, side 72..108, bottom 108..144.</summary>
    private static readonly Dictionary<string, (int Start, int Length)> Range = new()
    {
        [Gpu] = (0, 12), [H2Id] = (12, 24), [Ram71] = (12, 24), [Ram73] = (12, 24), [Rog] = (12, 24),
        [Top] = (36, 36), [Side] = (72, 36), [Bottom] = (108, 36),
    };

    private const int Total = 144;

    private static readonly int[] Expand = [0, 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10, 11];

    private static readonly Rgb Red = new(255, 0, 0);

    private static ZoneRef R(string id) => new(new ControllerId(id), new ZoneId(0));

    private static SpatialPath Candidate(bool bottomReverse = false, bool sideReverse = false, bool topReverse = false, bool h2Reverse = false) =>
        new([
            new SpatialStage(12, [new SpatialBinding(R(Gpu))]),
            new SpatialStage(24, [new SpatialBinding(R(H2Id), h2Reverse), new SpatialBinding(R(Ram71)), new SpatialBinding(R(Ram73)), new SpatialBinding(R(Rog))]),
            new SpatialStage(36, [new SpatialBinding(R(Top), topReverse)]),
            new SpatialStage(36, [new SpatialBinding(R(Side), sideReverse)]),
            new SpatialStage(36, [new SpatialBinding(R(Bottom), bottomReverse)]),
        ]);

    // ------------------------------------------------------------------------------------------------- validation

    [Fact]
    public void The_resolver_returns_the_literal_candidate_ranges()
    {
        var spans = Candidate(bottomReverse: true).Resolve();
        foreach (var id in Enumeration)
        {
            var s = spans[R(id)];
            (s.Start, s.Length, s.Total).Should().Be((Range[id].Start, Range[id].Length, Total), id);
            s.Reverse.Should().Be(id == Bottom);
        }

        Candidate().TotalLength.Should().Be(144);
    }

    public static TheoryData<string, Func<SpatialPath>, string> Malformed => new()
    {
        { "duplicate", () => new SpatialPath([new(12, [new(R(Gpu))]), new(24, [new(R(Gpu))])]), "more than once" },
        { "zero length", () => new SpatialPath([new(0, [new(R(Gpu))])]), "outside 1..720" },
        { "negative length", () => new SpatialPath([new(-12, [new(R(Gpu))])]), "outside 1..720" },
        { "721", () => new SpatialPath([new(721, [new(R(Gpu))])]), "outside 1..720" },
        { "total 721", () => new SpatialPath([new(720, [new(R(Gpu))]), new(1, [new(R(Rog))])]), "exceeds 720" },
        { "int overflow", () => new SpatialPath([new(int.MaxValue, [new(R(Gpu))]), new(int.MaxValue, [new(R(Rog))])]), "outside 1..720" },
        { "no stage", () => new SpatialPath([]), "at least one stage" },
        { "no binding", () => new SpatialPath([new(12, [])]), "no target binding" },
        { "null bindings", () => new SpatialPath([new(12, null!)]), "no target binding" },
        { "null binding", () => new SpatialPath([new(12, [null!])]), "null binding" },
        { "null stage", () => new SpatialPath([null!]), "is null" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_paths_are_rejected_with_a_useful_message(string name, Func<SpatialPath> path, string message)
    {
        var a = Assignment.ForGroup("g", [R(Gpu), R(Rog)], "Breathing") with { Path = path() };
        var act = a.ValidatePath;
        act.Should().Throw<ArgumentException>(name).WithMessage($"*{message}*");
    }

    [Fact]
    public void Every_target_is_bound_exactly_once_and_nothing_else_is_bound()
    {
        var omitted = Assignment.ForGroup("g", [R(Gpu), R(Rog)], "Breathing") with { Path = new SpatialPath([new(12, [new(R(Gpu))])]) };
        omitted.Invoking(a => a.ValidatePath()).Should().Throw<ArgumentException>().WithMessage("*does not bind target native:rog*");
        var unknown = Assignment.ForGroup("g", [R(Gpu)], "Breathing") with { Path = new SpatialPath([new(12, [new(R(Gpu)), new(R(Ram71))])]) };
        unknown.Invoking(a => a.ValidatePath()).Should().Throw<ArgumentException>().WithMessage("*binds native:ram71*not a target*");
        var listedTwice = Assignment.ForGroup("g", [R(Gpu), R(Gpu)], "Breathing") with { Path = new SpatialPath([new(12, [new(R(Gpu))])]) };
        listedTwice.Invoking(a => a.ValidatePath()).Should().Throw<ArgumentException>().WithMessage("*listed more than once*");
        (Assignment.ForGroup("g", [R(Gpu)], "Breathing") with { Path = null }).ValidatePath(); // absent path: always valid
    }

    [Fact]
    public void Unsupported_routes_reject_an_explicit_path_instead_of_ignoring_it()
    {
        var path = new SpatialPath([new(12, [new(R(Gpu)), new(R(Rog))])]);
        var frames = new Assignment([R(Gpu), R(Rog)], "StaticColor", EffectParams.Default, "g", ContentStrategy.Frames) { FrameSetId = "x", Path = path };
        frames.Invoking(a => a.ValidatePath()).Should().Throw<ArgumentException>().WithMessage("*Frames*");
        foreach (var perDevice in new[] { "RainbowMorph", "Twinkle" })
        {
            var a = Assignment.ForGroup("g", [R(Gpu), R(Rog)], perDevice) with { Path = path };
            a.Invoking(x => x.ValidatePath()).Should().Throw<ArgumentException>().WithMessage($"*per-device route of {perDevice}*");
            (a with { Strategy = ContentStrategy.Program }).ValidatePath(); // the Program equivalent has one shared layout
        }
    }

    [Fact]
    public void The_path_is_deep_copied_so_a_caller_cannot_change_a_published_scene()
    {
        var bindings = new[] { new SpatialBinding(R(Gpu)) };
        var stages = new[] { new SpatialStage(12, bindings) };
        var path = new SpatialPath(stages);
        bindings[0] = new SpatialBinding(R(Rog), true);
        stages[0] = new SpatialStage(99, [new SpatialBinding(R(Ram71))]);
        path.Stages.Should().ContainSingle().Which.Length.Should().Be(12);
        path.Stages[0].Bindings.Should().Equal(new SpatialBinding(R(Gpu)));
        path.Should().Be(new SpatialPath([new SpatialStage(12, [new SpatialBinding(R(Gpu))])]), "value equality over stages and bindings");
    }

    [Fact]
    public void Removing_zones_unbinds_them_and_drops_empty_stages()
    {
        var rest = Candidate().Without(new HashSet<ZoneRef> { R(Gpu), R(Rog) })!;
        rest.Stages.Select(s => s.Length).Should().Equal(24, 36, 36, 36);
        rest.Stages[0].Bindings.Select(b => b.Target).Should().Equal(R(H2Id), R(Ram71), R(Ram73));
        Candidate().Without(Enumeration.Select(R).ToHashSet()).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------- vendor projection

    private static LConnectZone ZoneOf(string id) => id switch
    {
        Side or Bottom => new("TL", 3, 78),
        Top => new("TL", 3, 78, ReverseChain: true),
        H2Id => new("H2", 1, 24, FaceOnFill: true),
        _ => new("Streamed", 0, 1),
    };

    private static SpatialSpan Literal(string id, bool reverse = false) => new(Range[id].Start, Range[id].Length, reverse, Total);

    private static byte[,] UiRed()
    {
        var ui = new byte[6, 3];
        var c = LConnectLoops.CapColour(Red, LConnectLoops.RgbMax);
        (ui[0, 0], ui[0, 1], ui[0, 2]) = (c.R, c.G, c.B);
        return ui;
    }

    private static IReadOnlyList<byte[]> Raw(int mode, int dots, int bright = 255) => LConnectLoops.UiModeFrames(mode, dots, bright, 0, UiRed(), out _)!;

    private static LConnectPlan PlanCandidate(string effect, Func<string, bool>? reverse = null, IReadOnlyList<string>? order = null,
        float speed = 1f, float brightness = 1f)
    {
        var ids = order ?? Enumeration;
        var plan = LConnectLoops.TryPlan(effect, new EffectParams([Red], speed, brightness, Direction.Forward), [Red], ids.Select(ZoneOf).ToArray(),
            ids.Select(id => Literal(id, reverse?.Invoke(id) == true)).ToArray(), out var why);
        plan.Should().NotBeNull(why);
        return plan!;
    }

    private static int PassEnd(int dots, int p) => ((p + 1) * dots) - (6 * p * (p + 1)) - 1;

    private static bool On(byte[] rgb, int frame, int leds, int led) =>
        rgb[(((frame * leds) + led) * 3)] != 0 || rgb[(((frame * leds) + led) * 3) + 1] != 0 || rgb[(((frame * leds) + led) * 3) + 2] != 0;

    private static int FanLit(byte[] rgb, int frame, int fan) =>
        Enumerable.Range(0, 13).Count(k => On(rgb, frame, 78, (fan * 13) + k)) + Enumerable.Range(0, 13).Count(k => On(rgb, frame, 78, 39 + (fan * 13) + k));

    /// <summary>The literal expected TL fan of band [b0, b0+12) inside a 36-dot stage: binding reversal, then ReverseChain.</summary>
    private static int ExpectedFan(string id, int b0, bool bindingReverse)
    {
        var sub = (b0 - Range[id].Start) / 12;
        sub = bindingReverse ? 2 - sub : sub;
        return ZoneOf(id).ReverseChain ? 2 - sub : sub;
    }

    /// <remarks>
    /// The last row is Root's offline candidate bringup/demo-profiles/07-spatial-red-stacking.json exactly: all three TL
    /// bindings locally reversed (bottom right-first, side top-first, top left-first with its existing ReverseChain), speed
    /// 0.25 (vendor multiplier 6), brightness 0.5 (vendor 128).
    /// </remarks>
    [Theory]
    [InlineData(false, false, false, 1f, 1f)]
    [InlineData(true, false, false, 1f, 1f)]
    [InlineData(false, false, true, 1f, 1f)]
    [InlineData(false, true, false, 1f, 1f)]
    [InlineData(true, true, true, 1f, 1f)]
    [InlineData(true, true, true, 0.25f, 0.5f)]
    public void Spatial_stacking_deposits_bottom_side_top_inner_then_gpu_one_whole_fan_at_a_time(bool bottomReverse, bool sideReverse, bool topReverse,
        float speed, float brightness)
    {
        bool Rev(string id) => (id == Bottom && bottomReverse) || (id == Side && sideReverse) || (id == Top && topReverse);
        var plan = PlanCandidate("Stacking", Rev, speed: speed, brightness: brightness);
        plan.Variant.Should().Contain("explicit spatial path of 144 source dots").And.Contain("fan-aligned Stacking");
        var rgb = Enumeration.Select((id, i) => (id, plan.Zones[i].Rgb)).ToDictionary(x => x.id, x => x.Rgb);
        var raw = Raw(7, Total, LConnectLoops.BrightnessType(brightness));
        plan.Zones.Should().OnlyContain(z => z.IntervalMs == LConnectLoops.SpeedType(speed) * 12, "the vendor interval of this speed, unchanged by the path");
        plan.Zones.Should().OnlyContain(z => z.Frames == raw.Count, "one body, one period");
        raw.Count.Should().Be(Enumerable.Range(0, 12).Sum(p => Total - (12 * p)));

        var sequence = new List<string>();
        var lit = new HashSet<(string, int)>();
        var inner = new[] { Ram71, Ram73, Rog };
        var lastInner = 0;
        var gpuLit = false;
        for (var p = 0; p < 12; p++)
        {
            var f = PassEnd(Total, p);
            var b0 = Total - (12 * (p + 1));
            var now = new HashSet<(string, int)>();
            foreach (var id in new[] { Bottom, Side, Top })
            {
                for (var fan = 0; fan < 3; fan++)
                {
                    var n = FanLit(rgb[id], f, fan);
                    n.Should().BeOneOf([0, 26], $"pass {p}: {id} fan {fan} is never partially filled (no neighbour straddling)");
                    if (n == 26)
                    {
                        now.Add((id, fan));
                    }
                }
            }

            now.IsSupersetOf(lit).Should().BeTrue($"pass {p}: a retained deposit never un-lights");
            var gained = now.Except(lit).ToArray();
            lit = now;

            // inner aggregate Points: mutually equal, equal to the literal coverage mean of source dots 12..36 of this frame
            var sum = Enumerable.Range(12, 24).Sum(d => raw[f][d * 3]);
            var mean = (byte)((sum + 12) / 24);
            foreach (var id in inner)
            {
                rgb[id][(f * 3)..((f * 3) + 3)].Should().Equal([mean, 0, 0], $"pass {p}: {id} shows the stage's coverage mean");
            }

            var gpu = rgb[Gpu][f * 3];
            gpu.Should().Be((byte)((Enumerable.Range(0, 12).Sum(d => raw[f][d * 3]) + 6) / 12), "GPU: its own 12-dot stage");
            if (gained.Length == 1 && b0 >= 36)
            {
                var (id, fan) = gained[0];
                var stageOf = b0 >= 108 ? Bottom : b0 >= 72 ? Side : Top;
                id.Should().Be(stageOf, $"pass {p}");
                fan.Should().Be(ExpectedFan(stageOf, b0, Rev(stageOf)), $"pass {p}: the literal fan of band {b0}");
                sequence.Add(id);
            }
            else
            {
                gained.Should().BeEmpty($"pass {p}: band {b0} is not a TL band");
                if (mean > lastInner)
                {
                    sequence.Add("inner");
                }
                else if (!gpuLit && gpu > 0)
                {
                    sequence.Add("gpu");
                }
            }

            lastInner = Math.Max(lastInner, mean);
            gpuLit |= gpu > 0;
        }

        sequence.Should().Equal(Bottom, Bottom, Bottom, Side, Side, Side, Top, Top, Top, "inner", "inner", "gpu");
        lastInner.Should().Be(raw[^1][12 * 3], "a complete inner stage is the full colour");
    }

    [Fact]
    public void Local_reversal_and_reverse_chain_compose_without_changing_the_chain_orientation()
    {
        // top (ReverseChain) with and without the path-local reversal: the reversal undoes exactly the chain's fan flip
        var plain = PlanCandidate("Stacking");
        var reversed = PlanCandidate("Stacking", id => id == Top);
        var top = Array.IndexOf(Enumeration, Top);
        var f = PassEnd(Total, 6); // the first top deposit, band 60..72
        Enumerable.Range(0, 3).Select(fan => FanLit(plain.Zones[top].Rgb, f, fan)).Should().Equal(new[] { 26, 0, 0 }, "ReverseChain: band 60..72 lands on fan 0");
        Enumerable.Range(0, 3).Select(fan => FanLit(reversed.Zones[top].Rgb, f, fan)).Should().Equal(new[] { 0, 0, 26 }, "plus local reversal: fan 2");

        // every other zone is byte-identical: a reversal is local to its binding
        foreach (var i in Enumerable.Range(0, Enumeration.Length).Where(i => i != top))
        {
            reversed.Zones[i].Rgb.Should().Equal(plain.Zones[i].Rgb, Enumeration[i]);
        }

        // within a deposited fan the 12 → 13 column expansion is unchanged (ReverseChain reads the physical row back to front)
        var lastFrame = plain.Zones[0].Frames - 1;
        for (var p = 0; p < 39; p++)
        {
            On(plain.Zones[top].Rgb, lastFrame, 78, p).Should().BeTrue();
        }
    }

    [Fact]
    public void Spatial_stacking_matches_the_literal_column_oracle_byte_for_byte()
    {
        var plan = PlanCandidate("Stacking", id => id == Side);
        var raw = Raw(7, Total);
        foreach (var id in new[] { Bottom, Side, Top })
        {
            var rgb = plan.Zones[Array.IndexOf(Enumeration, id)].Rgb;
            var (start, _) = Range[id];
            var rev = id == Side;
            for (var f = 0; f < raw.Count; f += 37)
            {
                for (var row = 0; row < 2; row++)
                {
                    for (var p = 0; p < 39; p++)
                    {
                        var q = ZoneOf(id).ReverseChain ? 38 - p : p;
                        var local = ((q / 13) * 12) + Expand[q % 13];
                        var src = start + (rev ? 35 - local : local);
                        rgb.AsSpan((((f * 78) + (row * 39) + p) * 3), 3).ToArray().Should().Equal(raw[f].AsSpan(src * 3, 3).ToArray(), $"{id} f{f} r{row} p{p}");
                    }
                }
            }
        }
    }

    [Fact]
    public void The_h2_shares_the_inner_stage_with_its_vertical_fill_and_symmetry()
    {
        var plan = PlanCandidate("Stacking");
        var h2 = plan.Zones[Array.IndexOf(Enumeration, H2Id)].Rgb;
        var raw = Raw(7, Total);
        var strip = new byte[raw.Count * 24 * 3];
        for (var f = 0; f < raw.Count; f++)
        {
            raw[f].AsSpan(12 * 3, 24 * 3).CopyTo(strip.AsSpan(f * 72));
        }

        h2.Should().Equal(RingFillMap.ApplyFrames(strip, BottomEnd.High, false, RingFillMap.SamplingFor("Stacking")),
            "the inner stage's 24 dots, through the owner-accepted whole-step fill with linear sampling");
        var half = PassEnd(Total, 9);
        var litHalf = Enumerable.Range(0, 24).Count(i => On(h2, half, 24, i));
        litHalf.Should().BeInRange(8, 16, "half the inner stage: about half the ring by height");
        for (var i = 0; i < 24; i++)
        {
            h2.AsSpan(((half * 24) + i) * 3, 3).ToArray().Should().Equal(h2.AsSpan(((half * 24) + RingFillMap.Mirror(i, false)) * 3, 3).ToArray(),
                $"LED {i} and its mirror are at the same height");
        }

        Enumerable.Range(0, 24).Should().OnlyContain(i => On(h2, PassEnd(Total, 10), 24, i), "full inner stage: the whole ring");
    }

    [Fact]
    public void Spatial_stacking_refuses_a_tl_stage_that_is_not_twelve_per_fan()
    {
        var zones = new[] { ZoneOf(Side), ZoneOf(Gpu) };
        var p = new EffectParams([Red], 1f, 1f, Direction.Forward);
        LConnectLoops.TryPlan("Stacking", p, [Red], zones, [new SpatialSpan(0, 39, false, 51), new SpatialSpan(39, 12, false, 51)], out var why).Should().BeNull();
        why.Should().Contain("not a multiple of 12");
        LConnectLoops.TryPlan("Stacking", p, [Red], zones, [new SpatialSpan(0, 24, false, 36), new SpatialSpan(24, 12, false, 36)], out why).Should().BeNull();
        why.Should().Contain("not 12 per fan (36)");
    }

    [Fact]
    public void A_shared_runway_uses_the_same_resolver_with_nonconstant_rgb_on_every_projection()
    {
        var plan = PlanCandidate("Runway", id => id == Bottom);
        var raw = Raw(5, Total);
        plan.Zones.Should().OnlyContain(z => z.Frames == raw.Count);
        var varied = Enumeration.ToDictionary(id => id, _ => new HashSet<string>());
        var h2 = plan.Zones[Array.IndexOf(Enumeration, H2Id)].Rgb;

        for (var f = 0; f < raw.Count; f++)
        {
            foreach (var id in new[] { Bottom, Side, Top })
            {
                var rgb = plan.Zones[Array.IndexOf(Enumeration, id)].Rgb;
                var (start, len) = Range[id];
                for (var p = 0; p < 39; p++)
                {
                    // 36-dot stage onto 39 natural TL dots, midpoint-nearest; ReverseChain reads the physical row back to front
                    var local = ((2 * (ZoneOf(id).ReverseChain ? 38 - p : p)) + 1) * len / 78;
                    var src = start + (id == Bottom ? len - 1 - local : local);
                    for (var row = 0; row < 2; row++)
                    {
                        rgb.AsSpan((((f * 78) + (row * 39) + p) * 3), 3).ToArray().Should().Equal(raw[f].AsSpan(src * 3, 3).ToArray(), $"{id} f{f} p{p}");
                    }
                }

                varied[id].Add(Convert.ToHexString(rgb, f * 78 * 3, 78 * 3));
            }

            foreach (var id in new[] { Gpu, Rog, Ram71, Ram73 })
            {
                var (start, len) = Range[id];
                var expected = new byte[3];
                for (var c = 0; c < 3; c++)
                {
                    expected[c] = (byte)((Enumerable.Range(start, len).Sum(d => raw[f][(d * 3) + c]) + (len / 2)) / len);
                }

                plan.Zones[Array.IndexOf(Enumeration, id)].Rgb.AsSpan(f * 3, 3).ToArray().Should().Equal(expected, $"{id} f{f}: coverage mean");
                varied[id].Add(Convert.ToHexString(expected));
            }

            varied[H2Id].Add(Convert.ToHexString(h2, f * 72, 72));
        }

        varied.Should().OnlyContain(kv => kv.Value.Count > 2, "every projection (TL, H2, aggregate Points) shows a moving runway, not a constant");
    }

    [Fact]
    public void Uniform_breathing_stays_one_whole_case_colour_with_a_path()
    {
        var plan = PlanCandidate("Breathing", id => id == Bottom || id == H2Id);
        var raw = Raw(4, Total);
        plan.Zones.Should().OnlyContain(z => z.Frames == raw.Count);
        var levels = new HashSet<byte>();
        for (var f = 0; f < raw.Count; f++)
        {
            var c = raw[f].AsSpan(0, 3).ToArray();
            for (var i = 0; i < Enumeration.Length; i++)
            {
                var z = plan.Zones[i];
                var leds = z.Rgb.Length / 3 / z.Frames;
                for (var led = 0; led < leds; led++)
                {
                    z.Rgb.AsSpan(((f * leds) + led) * 3, 3).ToArray().Should().Equal(c, $"{Enumeration[i]} f{f} led{led}");
                }
            }

            levels.Add(c[0]);
        }

        levels.Count.Should().BeGreaterThan(2, "it breathes");
    }

    [Fact]
    public void The_target_enumeration_cannot_change_an_explicit_path_projection()
    {
        var a = PlanCandidate("Stacking");
        var shuffled = new[] { Ram73, Bottom, Gpu, H2Id, Side, Rog, Top, Ram71 };
        var b = PlanCandidate("Stacking", order: shuffled);
        for (var i = 0; i < shuffled.Length; i++)
        {
            b.Zones[i].Rgb.Should().Equal(a.Zones[Array.IndexOf(Enumeration, shuffled[i])].Rgb, shuffled[i]);
        }
    }

    // ------------------------------------------------------------------------------------------------- engine integration

    private sealed class UProbe() : EffectGenerator("UProbe", new EffectParamSchema("probe", "probe", 1, 1, false, false, [Rgb.White]))
    {
        public override void Render(in RenderContext ctx, Span<Rgb> output)
        {
            for (var i = 0; i < output.Length; i++)
            {
                var s = ctx.Samples[i];
                output[i] = new Rgb((byte)Math.Round(s.U * 255), (byte)Math.Round(s.X * 255), (byte)Math.Round(s.Y * 255));
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly FrameBus _bus = new();

        public Rig(IReadOnlyList<string>? present = null)
        {
            Loop = new RenderLoop(new EffectRegistry([.. EffectRegistry.CreateBuiltIns(), new UProbe()]), _bus)
            {
                ChainReversed = id => id.Value == Top,
                Ring = id => id.Value == H2Id ? new RingGeometry(true, false, 0) : default,
            };
            Loop.SetControllers(Controllers(present ?? Enumeration));
        }

        public RenderLoop Loop { get; }

        public static ControllerDescriptor[] Controllers(IEnumerable<string> ids) => ids.Select(Descriptor).ToArray();

        public static ControllerDescriptor Descriptor(string id) => id switch
        {
            Side or Top or Bottom => Chain(id, "TL", 3, ZoneLayout.Matrix(39, 2)),
            H2Id => Chain(id, "H2", 1, ZoneLayout.Ring(24)),
            _ => TestSupport.Controller(id, ZoneLayout.Point),
        };

        public void Dispose() => _bus.Dispose();

        public Dictionary<string, LoopFrames> Drain() =>
            Enumeration.Where(id => _bus[new ControllerId(id)]?.HasPendingLoop == true).ToDictionary(id => id, id =>
            {
                _bus[new ControllerId(id)]!.TryConsumeLoop(out var pending).Should().BeTrue();
                return pending.Loop;
            });

        public Rgb Streamed(string id, double t = 0.5)
        {
            Loop.RenderFrame(t, 0);
            _bus[new ControllerId(id)]!.TryConsume(out var frame).Should().BeTrue();
            return frame.Zones[0][0];
        }

        private static ControllerDescriptor Chain(string id, string family, int units, ZoneLayout layout) =>
            new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
                [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(4000, 20, 12288));
    }

    private static Assignment Group(string effect, SpatialPath? path, IReadOnlyList<string>? order = null, ContentStrategy strategy = ContentStrategy.Vendor) =>
        Assignment.ForGroup("case", (order ?? Enumeration).Select(R).ToArray(), effect, new EffectParams([Red], 1f, 1f, Direction.Forward))
            with { Strategy = strategy, Path = path };

    [Fact]
    public void The_engine_publishes_the_projected_vendor_loops_and_labels_the_path()
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Group("Stacking", Candidate(bottomReverse: true))]));
        var loops = rig.Drain();
        var plan = PlanCandidate("Stacking", id => id == Bottom);
        foreach (var id in new[] { Side, Top, H2Id, Bottom })
        {
            loops[id].Rgb.ToArray().Should().Equal(plan.Zones[Array.IndexOf(Enumeration, id)].Rgb, id);
        }

        loops.Values.Select(l => l.EffectIndex).Distinct().Should().ContainSingle("one shared index");
        rig.Loop.LastLoopCompile!.Variants!.Should().ContainSingle().Which.Should().Contain("spatial path 144 dots: 12:[native:gpu/0]")
            .And.Contain("lianli:rf:35-F7-EF-72-AB-3C/0~");
    }

    [Fact]
    public void A_missing_member_never_slides_the_others_and_its_reconnect_restores_its_range()
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Group("Stacking", Candidate())]), allowMissingZones: true);
        var first = rig.Drain();
        rig.Loop.SetControllers(Rig.Controllers(Enumeration.Where(id => id != Top)));
        rig.Drain().Should().BeEmpty("with explicit ranges the remaining loops are byte-identical, nothing is re-sent");
        rig.Loop.SetControllers(Rig.Controllers(Enumeration));
        var back = rig.Drain();
        back.Keys.Should().BeEquivalentTo(first.Keys, "one shared group item is re-sent under one index when a member returns");
        foreach (var id in first.Keys)
        {
            back[id].Rgb.ToArray().Should().Equal(first[id].Rgb.ToArray(), $"{id}: the same range and bytes as before the loss");
        }

        back.Values.Select(l => l.EffectIndex).Distinct().Should().ContainSingle();

        // control: the legacy concatenation does slide when a member goes missing
        using var legacy = new Rig();
        legacy.Loop.SetScene(new Scene([Group("Stacking", null)]), allowMissingZones: true);
        legacy.Drain();
        legacy.Loop.SetControllers(Rig.Controllers(Enumeration.Where(id => id != Top)));
        legacy.Drain().Keys.Should().Contain(H2Id, "without a path the H2 moves to the top's old offset");
    }

    [Fact]
    public void A_rejected_path_leaves_the_previous_scene_untouched()
    {
        using var rig = new Rig();
        var good = new Scene([Group("Stacking", Candidate())]);
        rig.Loop.SetScene(good);
        rig.Drain();
        var bad = new SpatialPath([new(12, [new(R(Gpu))]), new(24, [new(R(H2Id)), new(R(Ram71)), new(R(Ram73)), new(R(Rog))]),
            new(24, [new(R(Top))]), new(36, [new(R(Side))]), new(36, [new(R(Bottom))])]); // top: 24 is not 12 per fan
        var act = () => rig.Loop.SetScene(new Scene([Group("Stacking", bad)]));
        act.Should().Throw<ArgumentException>().WithMessage("*not 12 per fan (36)*");
        var odd = new SpatialPath([new(18, [new(R(Gpu))]), new(18, [new(R(H2Id)), new(R(Ram71)), new(R(Ram73)), new(R(Rog))]),
            new(36, [new(R(Top))]), new(36, [new(R(Side))]), new(36, [new(R(Bottom))])]);
        act = () => rig.Loop.SetScene(new Scene([Group("Stacking", odd)]));
        act.Should().Throw<ArgumentException>().WithMessage("*not a multiple of 12*");
        act = () => rig.Loop.SetScene(new Scene([Group("Stacking", Candidate().Without(new HashSet<ZoneRef> { R(Rog) }))]));
        act.Should().Throw<ArgumentException>().WithMessage("*does not bind target native:rog*");
        rig.Loop.Scene.Should().BeSameAs(good);
        rig.Drain().Should().BeEmpty("nothing was published");

        // the Runway route has no 12-dot rule: an 18-dot stage is a valid path there
        rig.Loop.SetScene(new Scene([Group("Runway", odd)]));
    }

    /// <remarks>
    /// R1: the top chain is calibrated ChainReversed (physically wired right-to-left), which the vendor slicing applies to the
    /// whole strip; an explicit Program path reads it the same way, so its local U is reversed by ChainReversed XOR the
    /// binding's Reverse. Side/bottom (not chain-reversed) follow their binding alone; X stays local, never flipped.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Program_maps_local_u_onto_each_stage_and_points_sit_at_the_stage_centre(bool topReverse)
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Group("UProbe", Candidate(topReverse: topReverse, sideReverse: true), strategy: ContentStrategy.Program)]));
        var loops = rig.Drain();
        static byte Q(float u) => (byte)Math.Round(u * 255);
        foreach (var id in new[] { Side, Top, Bottom })
        {
            var (start, len) = Range[id];
            var rgb = loops[id].Rgb.ToArray();
            for (var row = 0; row < 2; row++)
            {
                for (var x = 0; x < 39; x++)
                {
                    var local = x / 38f;
                    var reversed = id switch { Top => !topReverse, Side => true, _ => false }; // top: ChainReversed XOR binding
                    var u = (start + ((reversed ? 1f - local : local) * len)) / Total;
                    rgb[((row * 39) + x) * 3].Should().Be(Q(u), $"{id} row {row} x {x}: both rows share the stage U");
                    rgb[(((row * 39) + x) * 3) + 1].Should().Be(Q(local), "X stays local");
                }
            }
        }

        var ring = loops[H2Id].Rgb.ToArray();
        for (var i = 0; i < 24; i++)
        {
            ring[i * 3].Should().Be(Q((12 + (i / 24f * 24)) / Total), $"H2 LED {i}: ring local u = i/24 on the inner stage");
        }

        foreach (var id in new[] { Rog, Ram71, Ram73 })
        {
            rig.Streamed(id).R.Should().Be(Q(24f / Total), $"{id}: the inner stage centre");
        }

        rig.Streamed(Gpu).R.Should().Be(Q(6f / Total), "GPU: its own stage centre");
        rig.Loop.LastLoopCompile!.Variants!.Should().ContainSingle().Which.Should().Contain("Program:UProbe").And.Contain("spatial path 144 dots");
    }

    [Fact]
    public void A_vendor_effect_without_a_body_falls_back_to_program_and_keeps_the_path()
    {
        using var program = new Rig();
        program.Loop.SetScene(new Scene([Group("UProbe", Candidate(), strategy: ContentStrategy.Program)]));
        var expected = program.Drain();
        using var vendor = new Rig();
        vendor.Loop.SetScene(new Scene([Group("UProbe", Candidate())]));
        var loops = vendor.Drain();
        foreach (var id in new[] { Side, Top, H2Id, Bottom })
        {
            loops[id].Rgb.ToArray().Should().Equal(expected[id].Rgb.ToArray(), id);
        }

        vendor.Loop.LastLoopCompile!.Variants![0].Should().Contain("LConnect: no Quick/Sync UIEffect").And.Contain("spatial path");
        vendor.Streamed(Rog).Should().Be(program.Streamed(Rog));
    }

    [Fact]
    public void A_program_translational_group_fills_the_h2_by_height_along_its_stage()
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Group("Runway", Candidate(h2Reverse: true), strategy: ContentStrategy.Program)]));
        var loops = rig.Drain();
        rig.Loop.LastLoopCompile!.Variants![0].Should().Contain("H2 vertical-fill").And.Contain("spatial path");
        var gen = EffectRegistry.Default["Runway"];
        var frames = loops[H2Id].Frames;
        var caps = new[] { Side, Top, H2Id, Bottom }.Select(id => Rig.Descriptor(id).Capabilities).ToArray();
        var period = LoopCompiler.GroupPeriod(LoopCompiler.NaturalPeriod(gen, new EffectParams([Red], 1f, 1f, Direction.Forward)), caps, out _);
        LoopCompiler.TimingFor(period, caps[2]).Frames.Should().Be(frames);

        // independent samples: the H2 as Linear(24) with u_local = i/23 reversed into 12..36, every other member as the engine does
        var samples = new List<Sample>();
        foreach (var id in Enumeration)
        {
            var (start, len) = Range[id];
            var reverse = id == H2Id;
            var n = id switch { Side or Top or Bottom => 78, H2Id => 24, _ => 1 };
            for (var i = 0; i < n; i++)
            {
                var (local, x, y) = id switch
                {
                    Side or Top or Bottom => ((i % 39) / 38f, (i % 39) / 38f, i / 39 == 0 ? 0f : 1f),
                    H2Id => (i / 23f, i / 23f, 0.5f),
                    _ => (0.5f, 0.5f, 0.5f),
                };
                samples.Add(new Sample((start + ((reverse ? 1f - local : local) * len)) / Total, 0f, x, y));
            }
        }

        var offset = 2 * 78; // LEDs of side, top before the H2
        var strip = new byte[frames * 24 * 3];
        var scratch = new Rgb[samples.Count];
        for (var k = 0; k < frames; k++)
        {
            var ctx = new RenderContext(LoopCompiler.FrameTime(k, frames, period), period / frames, [Red], 1f, 1f, Direction.Forward, samples.ToArray(), 1);
            gen.Render(in ctx, scratch);
            LoopCompiler.Pack(scratch.AsSpan(offset, 24), strip.AsSpan(k * 72));
        }

        loops[H2Id].Rgb.ToArray().Should().Equal(RingFillMap.ApplyFrames(strip, BottomEnd.Low, false, RingFillMap.SamplingFor("Runway")));
    }

    // ------------------------------------------------------------------------------------------------- review corrections R1..R3

    /// <summary>One zone's Program loop from independent samples (Runway is per-sample, so the zone's own samples suffice).</summary>
    private static byte[] ProgramOracle(string effect, Sample[] samples, int frames, double period)
    {
        var gen = EffectRegistry.Default[effect];
        var dst = new byte[frames * samples.Length * 3];
        var scratch = new Rgb[samples.Length];
        for (var k = 0; k < frames; k++)
        {
            var ctx = new RenderContext(LoopCompiler.FrameTime(k, frames, period), period / frames, [Red], 1f, 1f, Direction.Forward, samples, 1);
            gen.Render(in ctx, scratch);
            LoopCompiler.Pack(scratch, dst.AsSpan(k * samples.Length * 3));
        }

        return dst;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R1_a_program_runway_reads_the_reverse_chained_top_in_its_physical_order_on_both_rows(bool topReverse)
    {
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Group("Runway", Candidate(topReverse: topReverse), strategy: ContentStrategy.Program)]));
        var loops = rig.Drain();
        var top = loops[Top];
        var caps = new[] { Side, Top, H2Id, Bottom }.Select(id => Rig.Descriptor(id).Capabilities).ToArray();
        var period = LoopCompiler.GroupPeriod(LoopCompiler.NaturalPeriod(EffectRegistry.Default["Runway"], new EffectParams([Red], 1f, 1f, Direction.Forward)), caps, out _);

        // literal: physical column x of either row reads stage dot 36 + 36 · (ChainReversed XOR Reverse ? 1 − x/38 : x/38)
        var samples = new Sample[78];
        for (var i = 0; i < 78; i++)
        {
            var local = (i % 39) / 38f;
            var u = (36 + ((!topReverse ? 1f - local : local) * 36)) / Total;
            samples[i] = new Sample(u, 0f, local, i / 39);
        }

        var expected = ProgramOracle("Runway", samples, top.Frames, period);
        top.Rgb.ToArray().Should().Equal(expected, "the Program route reads the calibrated chain the way the vendor slicing does");
        var rgb = top.Rgb.ToArray();
        for (var f = 0; f < top.Frames; f++)
        {
            rgb.AsSpan(f * 78 * 3, 39 * 3).ToArray().Should().Equal(rgb.AsSpan(((f * 78) + 39) * 3, 39 * 3).ToArray(), $"f{f}: both rows share U");
        }

        Enumerable.Range(0, top.Frames).Select(f => Convert.ToHexString(rgb, f * 78 * 3, 78 * 3)).Distinct().Count().Should().BeGreaterThan(2, "it moves");
    }

    [Fact]
    public void R1_no_path_program_keeps_the_historical_chain_independent_u()
    {
        // no path: legacy GroupLayout.Concat, ChainReversed is not applied to Program samples (unchanged behaviour)
        using var rig = new Rig();
        rig.Loop.SetScene(new Scene([Group("UProbe", null, strategy: ContentStrategy.Program)]));
        var top = rig.Drain()[Top].Rgb.ToArray();
        var concat = GroupLayout.Concat(Enumeration.Select(id => Rig.Descriptor(id).Zones[0].Layout).ToArray(), Enumeration.Select(_ => (ZonePlacement?)null).ToArray());
        for (var i = 0; i < 78; i++)
        {
            top[i * 3].Should().Be((byte)Math.Round(concat[78 + i].U * 255), $"LED {i}");
        }
    }

    [Theory]
    [InlineData(Top)]
    [InlineData(H2Id)]
    public void R2_a_lone_present_member_plays_exactly_its_part_of_the_full_plan(string only)
    {
        var path = Candidate(bottomReverse: true, sideReverse: true, topReverse: true);
        using var full = new Rig();
        full.Loop.SetScene(new Scene([Group("Stacking", path)]), allowMissingZones: true);
        var all = full.Drain();

        // the same authored scene with every other controller absent from the start
        using var lone = new Rig(present: [only]);
        lone.Loop.SetScene(new Scene([Group("Stacking", path)]), allowMissingZones: true);
        var mine = lone.Drain();
        mine.Keys.Should().Equal(only);
        (mine[only].Frames, mine[only].IntervalMs).Should().Be((all[only].Frames, all[only].IntervalMs));
        mine[only].Rgb.ToArray().Should().Equal(all[only].Rgb.ToArray(), "its reserved stage of the 144-dot body, not a per-device body");
        mine[only].Geometry.Should().Be(all[only].Geometry, "the shared-body geometry tag (H2: vertical-fill), not a per-device ring tag");
        lone.Loop.LastLoopCompile!.Variants![0].Should().Contain("explicit spatial path of 144 source dots").And.NotContain("per-device");
        if (only == H2Id)
        {
            mine[only].Geometry.Should().StartWith("vertical-fill").And.EndWith("sampling=linear");
        }

        // the others go missing from the full rig: the survivor's content is unchanged. (It may be re-sent under a new index:
        // the existing group anchor is keyed on the item's first loop, here a member that went missing; not path behaviour.)
        full.Loop.SetControllers(Rig.Controllers([only]));
        foreach (var (id, l) in full.Drain())
        {
            id.Should().Be(only);
            (l.Frames, l.IntervalMs, l.Geometry).Should().Be((all[only].Frames, all[only].IntervalMs, all[only].Geometry));
            l.Rgb.ToArray().Should().Equal(all[only].Rgb.ToArray(), "the same reserved range while the others are missing");
        }

        full.Loop.SetControllers(Rig.Controllers(Enumeration));
        var back = full.Drain();
        foreach (var id in all.Keys)
        {
            back[id].Rgb.ToArray().Should().Equal(all[id].Rgb.ToArray(), id);
            back[id].Geometry.Should().Be(all[id].Geometry, id);
        }

        // and the lone rig, when the rest arrives, converges on the full plan
        lone.Loop.SetControllers(Rig.Controllers(Enumeration));
        var joined = lone.Drain();
        foreach (var id in all.Keys)
        {
            joined[id].Rgb.ToArray().Should().Equal(all[id].Rgb.ToArray(), id);
        }
    }

    [Fact]
    public void R2_an_explicit_singleton_authored_from_the_start_uses_the_shared_body_over_its_own_path()
    {
        var p = new EffectParams([Red], 1f, 1f, Direction.Forward);
        using var rig = new Rig(present: [Top]);
        var a = Assignment.ForGroup("one", [R(Top)], "Stacking", p) with { Path = new SpatialPath([new(36, [new SpatialBinding(R(Top), true)])]) };
        rig.Loop.SetScene(new Scene([a]));
        var loop = rig.Drain()[Top];
        var raw = Raw(7, 36);
        loop.Frames.Should().Be(raw.Count, "the shared UiMode7 body over the 36-dot path");
        var rgb = loop.Rgb.ToArray();

        // literal: pass 0 deposits band [24, 36); local reverse → sub 0; ReverseChain → physical fan 2
        Enumerable.Range(0, 3).Select(fan => FanLit(rgb, PassEnd(36, 0), fan)).Should().Equal(new[] { 0, 0, 26 });
        Enumerable.Range(0, 3).Select(fan => FanLit(rgb, PassEnd(36, 1), fan)).Should().Equal(new[] { 0, 26, 26 });
        Enumerable.Range(0, 3).Select(fan => FanLit(rgb, PassEnd(36, 2), fan)).Should().Equal(new[] { 26, 26, 26 });

        var perDevice = LConnectLoops.TryPlan("Stacking", p, [Red], [ZoneOf(Top)], out _)!;
        perDevice.Variant.Should().NotContain("spatial", "null spans keep the exact historical singleton route");
        LConnectLoops.TryPlan("Stacking", p, [Red], [ZoneOf(Top)], null, out _)!.Zones[0].Rgb.Should().Equal(perDevice.Zones[0].Rgb);
        rgb.Should().NotEqual(perDevice.Zones[0].Rgb, "an explicit singleton never falls into the per-device family body");

        // the H2 alone, authored: its 24-dot path through the accepted height fill with linear sampling
        using var ring = new Rig(present: [H2Id]);
        ring.Loop.SetScene(new Scene([Assignment.ForGroup("ring", [R(H2Id)], "Stacking", p) with { Path = new SpatialPath([new(24, [new SpatialBinding(R(H2Id))])]) }]));
        var h2 = ring.Drain()[H2Id];
        var strip = Raw(7, 24).SelectMany(f => f).ToArray();
        h2.Rgb.ToArray().Should().Equal(RingFillMap.ApplyFrames(strip, BottomEnd.High, false, RingFillMap.SamplingFor("Stacking")));
        h2.Geometry.Should().StartWith("vertical-fill").And.EndWith("sampling=linear");
    }

    [Fact]
    public void R2_per_device_routes_stay_rejected_for_an_explicit_singleton()
    {
        var path = new SpatialPath([new(36, [new SpatialBinding(R(Top))])]);
        foreach (var effect in new[] { "RainbowMorph", "Twinkle" })
        {
            var a = Assignment.ForGroup("one", [R(Top)], effect) with { Path = path };
            a.Invoking(x => x.ValidatePath()).Should().Throw<ArgumentException>().WithMessage($"*per-device route of {effect}*");
            LConnectLoops.TryPlan(effect, EffectParams.Default, [Red], [ZoneOf(Top)], [new SpatialSpan(0, 36, false, 36)], out var why).Should().BeNull();
            why.Should().Contain("explicit spatial path is not supported");
        }
    }

    [Fact]
    public void R3_the_stacking_multiple_of_twelve_rule_holds_without_any_hardware()
    {
        var odd = new SpatialPath([new(18, [new SpatialBinding(R(Gpu))]), new(18, [new SpatialBinding(R(Rog))])]);
        var vendor = Assignment.ForGroup("g", [R(Gpu), R(Rog)], "Stacking") with { Path = odd };
        vendor.Invoking(a => a.ValidatePath()).Should().Throw<ArgumentException>().WithMessage("*Stacking stage 0 is 18 source dots, not a multiple of 12*");
        (vendor with { Strategy = ContentStrategy.Program }).ValidatePath();
        (Assignment.ForGroup("g", [R(Gpu), R(Rog)], "Runway") with { Path = odd }).ValidatePath();

        // every controller absent: SetScene still rejects it before publishing, and keeps the previous scene
        using var empty = new Rig(present: []);
        var before = empty.Loop.Scene;
        var act = () => empty.Loop.SetScene(new Scene([vendor]), allowMissingZones: true);
        act.Should().Throw<ArgumentException>().WithMessage("*not a multiple of 12*");
        empty.Loop.Scene.Should().BeSameAs(before);
        var malformed = Assignment.ForGroup("g", [R(Gpu), R(Rog)], "Runway") with { Path = new SpatialPath([new(12, [new SpatialBinding(R(Gpu))])]) };
        act = () => empty.Loop.SetScene(new Scene([malformed]), allowMissingZones: true);
        act.Should().Throw<ArgumentException>().WithMessage("*does not bind target native:rog*");

        // a valid offline path is accepted with nothing present
        empty.Loop.SetScene(new Scene([Group("Stacking", Candidate())]), allowMissingZones: true);
        empty.Loop.Scene.Assignments[0].Path.Should().Be(Candidate());
    }

    [Fact]
    public void Absent_path_keeps_the_historical_loops_byte_for_byte()
    {
        using var a = new Rig();
        a.Loop.SetScene(new Scene([Group("Stacking", null)]));
        var legacy = a.Drain();
        var plan = LConnectLoops.TryPlan("Stacking", new EffectParams([Red], 1f, 1f, Direction.Forward), [Red], Enumeration.Select(ZoneOf).ToArray(), out _)!;
        foreach (var id in new[] { Side, Top, H2Id, Bottom })
        {
            legacy[id].Rgb.ToArray().Should().Equal(plan.Zones[Array.IndexOf(Enumeration, id)].Rgb, id);
        }

        plan.Variant.Should().NotContain("spatial");
        a.Loop.LastLoopCompile!.Variants![0].Should().NotContain("spatial path");
    }
}
