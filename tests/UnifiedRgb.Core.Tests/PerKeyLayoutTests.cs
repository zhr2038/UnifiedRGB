using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// PER-KEY projection on the shared render timeline. The map is synthetic (two staggered rows; keys C and F share a column) and
/// stands for no device: it only proves the engine samples key geometry, not that any keyboard shows it.
/// </summary>
public sealed class PerKeyLayoutTests
{
    private static readonly PerKeyMap Map = new("synthetic", "synthetic-transport",
    [
        new(0x10, "A", 0, 0), new(0x11, "B", 0, 8), new(0x12, "C", 0, 16),
        new(0x20, "D", 1, 4), new(0x21, "E", 1, 12), new(0x22, "F", 1, 16),
    ], Complete: true, Verified: true, Evidence: "synthetic test map");

    private static ZoneDescriptor KeyZone => new(new ZoneId(0), "keys", ZoneLayout.Linear(Map.Keys.Count)) { PerKeyPoints = PerKeyProjection.Project(Map) };

    [Fact]
    public void Without_per_key_points_the_samples_are_exactly_the_ordinary_concatenation()
    {
        ZoneLayout[] layouts = [ZoneLayout.Ring(8), ZoneLayout.Linear(5)];
        ZonePlacement?[] placements = [null, new(0.1f, 0.2f, 1, 0, 0.5f)];
        IReadOnlyList<ZonePoint>?[] none = [null, null];
        SpatialSpan[] spans = [new(0, 8, false, 13), new(8, 5, true, 13)];

        PerKeyLayout.Any(none).Should().BeFalse();
        PerKeyLayout.Concat(layouts, placements, none, null).Should().Equal(GroupLayout.Concat(layouts, placements));
        PerKeyLayout.Concat(layouts, placements, none, spans).Should().Equal(GroupLayout.ConcatPath(layouts, placements, spans));
        PerKeyLayout.Concat([layouts[1]], [placements[1]], [null], null).Should().Equal(Projector.Project(layouts[1], placements[1]));
    }

    [Fact]
    public void A_single_per_key_zone_is_exactly_the_descriptor_projection()
    {
        var zone = KeyZone;
        PerKeyLayout.Any([zone.PerKeyPoints]).Should().BeTrue();
        PerKeyLayout.Concat([zone.Layout], [null], [zone.PerKeyPoints], null).Should().Equal(Projector.Project(zone));
    }

    [Fact]
    public void In_a_group_other_members_keep_their_samples_and_keys_travel_by_column()
    {
        var zone = KeyZone;
        ZoneLayout[] layouts = [ZoneLayout.Linear(4), zone.Layout];
        var plain = GroupLayout.Concat(layouts, [null, null]);
        var samples = PerKeyLayout.Concat(layouts, [null, null], [null, zone.PerKeyPoints], null);

        samples.Should().HaveCount(10);
        samples.Take(4).Should().Equal(plain.Take(4));
        var keys = samples.Skip(4).ToArray();
        keys.Select(s => (s.X, s.Y)).Should().Equal(zone.PerKeyPoints!.Select(p => (p.X, p.Y)));
        keys.Should().OnlyContain(s => s.U >= plain[4].U - 1e-6f && s.U <= plain[9].U + 1e-6f, "the keyboard keeps its own range of the group");
        keys[2].U.Should().Be(keys[5].U, "C and F share a column");
        keys[0].U.Should().BeLessThan(keys[3].U).And.BeLessThan(keys[1].U);
        keys[3].U.Should().BeLessThan(keys[1].U);
    }

    [Fact]
    public void A_path_maps_each_key_column_through_its_span()
    {
        var zone = KeyZone;
        SpatialSpan[] spans = [new(2, 6, true, 10)];
        var samples = PerKeyLayout.Concat([zone.Layout], [null], [zone.PerKeyPoints], spans);
        samples.Select(s => s.U).Should().Equal(zone.PerKeyPoints!.Select(p => spans[0].GlobalU(p.U)));
    }

    [Fact]
    public void Placement_on_a_per_key_member_and_a_wrong_point_count_are_refused()
    {
        var zone = KeyZone;
        var placed = () => PerKeyLayout.Concat([zone.Layout], [new ZonePlacement(0, 0, 1, 0, 1)], [zone.PerKeyPoints], null);
        placed.Should().Throw<NotSupportedException>();
        var short_ = () => PerKeyLayout.Concat([ZoneLayout.Linear(5)], [null], [zone.PerKeyPoints], null);
        short_.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_render_loop_samples_key_geometry_on_the_shared_clock()
    {
        var keyboard = new ControllerDescriptor(new ControllerId("keys"), "Test", "keys", DeviceType.Keyboard, DeviceClass.Virtual, "keys",
            [KeyZone], Capabilities.DirectOnly(60));
        var strip = TestSupport.Controller("strip", ZoneLayout.Linear(Map.Keys.Count));
        var keys = Render(keyboard);
        var dense = Render(strip);

        keys[2].Should().Be(keys[5], "keys in one column sample the same U");
        keys.Distinct().Count().Should().BeGreaterThan(3, "distinct columns keep distinct colours");
        dense[2].Should().NotBe(dense[5], "the same LED count without key points is the ordinary dense strip");
    }

    private static Rgb[] Render(ControllerDescriptor controller)
    {
        using var bus = new FrameBus();
        var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetControllers([controller]);
        loop.SetScene(new Scene([Assignment.ForZone(new(controller.Id, new ZoneId(0)), RainbowEffect.EffectId) with { Strategy = ContentStrategy.Program }]));
        loop.RenderFrame(0.25, 1 / 60.0);
        bus[controller.Id]!.TryConsume(out var frame).Should().BeTrue();
        return frame.Zones[0].ToArray();
    }
}
