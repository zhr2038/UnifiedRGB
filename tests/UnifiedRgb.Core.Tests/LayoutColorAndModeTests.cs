using UnifiedRgb.Core.Color;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

public class LayoutColorAndModeTests
{
    [Fact]
    public void Linear_and_matrix_u_span_0_to_1_and_point_is_centred()
    {
        var lin = Projector.Project(ZoneLayout.Linear(20));
        lin[0].U.Should().Be(0);
        lin[^1].U.Should().Be(1);
        lin.Select(s => s.U).Should().BeInAscendingOrder();
        Projector.Project(ZoneLayout.Point).Should().ContainSingle().Which.U.Should().Be(0.5f);
        Projector.Project(ZoneLayout.Matrix(4, 4)).Should().HaveCount(16);
    }

    [Fact]
    public void Ring_is_periodic_and_respects_direction()
    {
        var cw = Projector.Project(ZoneLayout.Ring(16));
        cw[0].U.Should().Be(0);
        cw[^1].U.Should().BeLessThan(1);
        var ccw = Projector.Project(ZoneLayout.Ring(16, 0, clockwise: false));
        ccw[4].Theta.Should().BeApproximately(-cw[4].Theta, 1e-5f);
    }

    [Fact]
    public void Group_concat_is_strictly_monotone_in_u()
    {
        var g = GroupLayout.Concat([ZoneLayout.Ring(16), ZoneLayout.Point, ZoneLayout.Linear(8)]);
        g.Should().HaveCount(25);
        g.Select(s => s.U).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        g.Should().OnlyContain(s => s.U > 0 && s.U < 1);
    }

    [Fact]
    public void Hsv_primaries_and_round_trip()
    {
        Hsv.ToRgb(0, 1, 1).Should().Be(new Rgb(255, 0, 0));
        Hsv.ToRgb(120, 1, 1).Should().Be(new Rgb(0, 255, 0));
        Hsv.ToRgb(240, 1, 1).Should().Be(new Rgb(0, 0, 255));
        var (h, s, v) = Hsv.FromRgb(new Rgb(255, 128, 0));
        Hsv.ToRgb(h, s, v).Should().Be(new Rgb(255, 128, 0));
        ColorTables.GenerateRainbowColorTable(6)[2].Should().Be(new Rgb(0, 255, 0));
    }

    [Fact]
    public void MapUserColor_wraps_and_falls_back_to_rainbow()
    {
        Rgb[] pal = [new(1, 1, 1), new(2, 2, 2)];
        ColorTables.MapUserColor(pal, 3).Should().Be(pal[1]);
        ColorTables.MapUserColor(pal, -1).Should().Be(pal[1]);
        ColorTables.MapUserColor([], 0).Should().Be(new Rgb(255, 0, 0));
    }

    [Fact]
    public void Output_stage_identity_and_gamma()
    {
        Rgb[] px = [new(10, 128, 255)];
        OutputStage.Identity.Apply(px, 1f);
        px[0].Should().Be(new Rgb(10, 128, 255));
        var g = new OutputStage(2.2f, new Rgb(255, 200, 255));
        Rgb[] w = [Rgb.White];
        g.Apply(w, 1f);
        w[0].Should().Be(new Rgb(255, 200, 255));
    }

    [Theory]
    [InlineData("P28", 34)]
    [InlineData("HS2", 19)]
    [InlineData("INFV3", 34)]
    [InlineData("SLv4", 25)]
    [InlineData("CL", 34)]
    [InlineData("H2", 11)]
    [InlineData("TL", 9)]
    [InlineData("INF", 34)]
    [InlineData("L88", 19)]
    [InlineData("SLv3", 25)]
    [InlineData("V150", 29)]
    public void Family_mode_counts_match_analysis(string family, int count)
    {
        FamilyModeTable.Default.Modes(family).Should().HaveCount(count);
        FamilyModeTable.Default.Resolve(family, count).Index.Should().Be(count);
    }

    [Fact]
    public void Strimer_has_at_least_five_modes_and_every_entry_resolves_to_a_registered_effect()
    {
        FamilyModeTable.Default.Modes("Strimer").Count.Should().BeGreaterOrEqualTo(5);
        foreach (var fam in FamilyModeTable.Default.Families)
        {
            foreach (var m in FamilyModeTable.Default.Modes(fam))
            {
                EffectRegistry.Default.Contains(m.EffectId).Should().BeTrue($"{fam}Mode{m.Index} → {m.EffectId}");
            }
        }
    }

    [Fact]
    public void Tl_and_h2_modes_carry_the_vendor_frames_and_interval_base_from_the_il()
    {
        var tl = FamilyModeTable.Default.Modes("TL");
        tl.Select(m => m.VendorMode).Should().Equal(1, 2, 3, 4, 5, 6, 7, 19, 29);
        tl.Should().OnlyContain(m => m.VendorIntervalBaseMs == 11);
        tl.Select(m => m.VendorFramesFor(3)).Should().Equal(39, 127, 30, 170, 88, 176, 39, 312, 200);
        tl[0].VendorFramesFor(5).Should().BeNull("TLMode* clamps to 4 fans");

        var h2 = FamilyModeTable.Default.Modes("H2");
        h2.Select(m => m.VendorMode).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12).And.NotContain(9, "GetH2Effect never reaches H2Mode9");
        h2.Select(m => m.VendorIntervalBaseMs).Should().Equal(20, 11, 20, 11, 20, 20, 20, 20, 20, 20, 20);
        h2.Select(m => m.VendorFramesFor(0)).Should().Equal(24, 127, 30, 170, 54, 60, 24, 200, null, 20, 80);
    }

    [Fact]
    public void Resolve_rejects_out_of_range_mode()
    {
        var act = () => FamilyModeTable.Default.Resolve("H2", 12);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
