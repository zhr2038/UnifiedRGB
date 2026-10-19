using UnifiedRgb.App.Preview;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// WP-15 (WORKORDER-STACKING-FIX, test 6): the preview builds the same fan-aligned Stacking plan as the Host for the reported
/// group order and saved mounting (no duplicated preview placement).
/// </summary>
public sealed class StackingAlignmentHostTests
{
    /// <summary>Bottom TL140 LCD, vertical TL140, top TL120 (chain reversed), H2 (saved offset 0, FaceOnFill, whole-step).</summary>
    private static ZoneDto[] ReportedZones() =>
    [
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3, ChainReversed: true),
        new ZoneDto(0, "H2", "Ring", 24, 0, 0, "H2", 1, RingOffsetLeds: 0, HalfStep: false, FaceOnFill: true),
    ];

    [Theory]
    [InlineData(Direction.Reverse)]
    [InlineData(Direction.Forward)]
    public void Preview_and_host_plans_match_for_the_reported_group(Direction dir)
    {
        var gen = EffectRegistry.Default["Stacking"];
        var p = new EffectParams([new Rgb(255, 0, 0)], 0.25f, 1f, dir);
        var preview = PreviewRenderer.VendorPlanFor(gen, p, ReportedZones(), Core.Engine.LoopPolicy.LConnect)!;
        var host = LConnectLoops.TryPlan("Stacking", p, p.Colors,
            [new("TL", 3, 78), new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true)])!;

        preview.Variant.Should().Be(host.Variant).And.Be("L-Connect Quick/Sync UiMode7, fan-aligned Stacking over 132 source dots; H2 vertical-fill (bottom=High, interpolated)");
        preview.Summary.Should().Be(host.Summary);
        preview.PeriodSeconds.Should().BeApproximately(57.024, 1e-9);
        preview.Zones.Should().OnlyContain(z => z.Frames == 792 && z.IntervalMs == 72.0);
        preview.Zones.Select(z => z.Rgb).Should().BeEquivalentTo(host.Zones.Select(z => z.Rgb), o => o.WithStrictOrdering());
    }

    /// <summary>WP-16: the reported zones with the H2 half-step setting.</summary>
    private static ZoneDto[] Zones(bool half) =>
    [
        .. ReportedZones().Take(3),
        new ZoneDto(0, "H2", "Ring", 24, 0, 0, "H2", 1, RingOffsetLeds: 0, HalfStep: half, FaceOnFill: true),
    ];

    [Theory]
    [InlineData(Direction.Reverse, false)]
    [InlineData(Direction.Forward, false)]
    [InlineData(Direction.Reverse, true)]
    [InlineData(Direction.Forward, true)]
    public void Vendor_preview_matches_the_host_for_both_half_settings(Direction dir, bool half)
    {
        var gen = EffectRegistry.Default["Stacking"];
        var p = new EffectParams([new Rgb(255, 0, 0)], 0.25f, 1f, dir);
        var preview = PreviewRenderer.VendorPlanFor(gen, p, Zones(half), Core.Engine.LoopPolicy.LConnect)!;
        var host = LConnectLoops.TryPlan("Stacking", p, p.Colors,
            [new("TL", 3, 78), new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true, HalfStep: half)])!;
        preview.Variant.Should().Be(host.Variant).And.EndWith($"{(half ? ", half-step" : string.Empty)}, interpolated)");
        preview.Zones.Select(z => z.Rgb).Should().BeEquivalentTo(host.Zones.Select(z => z.Rgb), o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(Direction.Reverse, false)]
    [InlineData(Direction.Forward, false)]
    [InlineData(Direction.Reverse, true)]
    [InlineData(Direction.Forward, true)]
    public void Generic_preview_interpolates_stacking_through_the_core_fill(Direction dir, bool half)
    {
        var gen = EffectRegistry.Default["Stacking"];
        var p = new EffectParams([new Rgb(255, 0, 0), new Rgb(0, 0, 255)], 0.5f, 1f, dir);
        var zones = Zones(half);
        var layouts = zones.Select(PreviewRenderer.ToLayout).ToArray();
        var placed = new PreviewRenderer(gen, layouts, p, zones: zones);
        placed.VerticalFill.Should().BeTrue();
        var flatLayouts = layouts.ToArray();
        flatLayouts[3] = ZoneLayout.Linear(24);
        var reference = new PreviewRenderer(gen, flatLayouts, p);
        var bottom = BottomEnd.High; // Stacking's pinned bottom end
        foreach (var t in new[] { 0.0, 0.4, 1.3, 7.9 })
        {
            var flat = reference.Render(t).AsSpan(234, 24).ToArray();
            var expected = new Rgb[24];
            RingFillMap.Apply(flat, expected, bottom, half, RingSampling.Linear);
            placed.Render(t).AsSpan(234, 24).ToArray().Should().Equal(expected, $"t {t}: the preview uses the core rounding");

            // and it equals the byte path the Host uploads
            var bytes = flat.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
            expected.SelectMany(c => new[] { c.R, c.G, c.B }).Should().Equal(RingFillMap.ApplyFrames(bytes, bottom, half, RingSampling.Linear));
        }
    }
}
