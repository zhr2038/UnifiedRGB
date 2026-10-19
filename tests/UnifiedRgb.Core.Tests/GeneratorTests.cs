using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

public class GeneratorTests
{
    public static TheoryData<string> EffectIds()
    {
        var d = new TheoryData<string>();
        foreach (var g in EffectRegistry.Default.All)
        {
            d.Add(g.Id);
        }

        return d;
    }

    [Fact]
    public void Registry_has_34_unique_generators_and_19_wireless()
    {
        var all = EffectRegistry.Default.All;
        all.Should().HaveCount(34);
        all.Select(g => g.Id).Should().OnlyHaveUniqueItems();
        all.Count(g => g.Schema.Wireless19).Should().Be(19);
        all.Select(g => g.GetType().Name).Should().OnlyContain(n => n.EndsWith("Effect", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Same_t_and_seed_give_identical_bytes(string id)
    {
        var samples = GroupLayout.Concat([ZoneLayout.Ring(16), ZoneLayout.Point, ZoneLayout.Linear(8)]);
        foreach (var t in new[] { 0.0, 0.37, 12.5, 86400.123 })
        {
            var a = TestSupport.Render(EffectRegistry.Default[id], samples, t);
            var fresh = (IEffectGenerator)Activator.CreateInstance(EffectRegistry.Default[id].GetType())!;
            var b = TestSupport.Render(fresh, samples, t);
            a.Should().Equal(b);
        }
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Render_path_allocates_nothing(string id)
    {
        var gen = EffectRegistry.Default[id];
        var samples = Projector.Project(ZoneLayout.Linear(64));
        var colors = gen.Schema.DefaultColors.ToArray();
        var output = new Rgb[samples.Length];
        void Run(int frames)
        {
            for (var f = 0; f < frames; f++)
            {
                var ctx = new RenderContext(f / 60.0, 1 / 60.0, colors, 0.5f, 1f, Direction.Reverse, samples, 7);
                gen.Render(in ctx, output);
            }
        }

        Run(10);

        // steady state: a real per-frame allocation shows in every pass; a one-off JIT tier-up/OSR transition does not
        var allocated = long.MaxValue;
        for (var pass = 0; pass < 3 && allocated != 0; pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Run(600);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        allocated.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Every_generator_produces_light_over_time(string id)
    {
        var gen = EffectRegistry.Default[id];
        var samples = Projector.Project(ZoneLayout.Linear(20));
        var lit = Enumerable.Range(0, 240).Any(f => TestSupport.Render(gen, samples, f / 60.0).Any(c => c != Rgb.Black));
        lit.Should().BeTrue();
    }

    [Fact]
    public void Random_effects_depend_on_seed()
    {
        var samples = Projector.Project(ZoneLayout.Linear(30));
        foreach (var id in new[] { TwinkleEffect.EffectId, DiscoEffect.EffectId, CandyBoxEffect.EffectId, LotteryEffect.EffectId })
        {
            var a = TestSupport.Render(EffectRegistry.Default[id], samples, 3.3, seed: 1);
            var b = TestSupport.Render(EffectRegistry.Default[id], samples, 3.3, seed: 2);
            a.Should().NotEqual(b, id);
        }
    }

    [Fact]
    public void Static_color_uses_palette_color_0()
    {
        var samples = Projector.Project(ZoneLayout.Linear(5));
        var ctx = new RenderContext(1, 0, [new Rgb(1, 2, 3)], 0.5f, 1f, Direction.Forward, samples, 0);
        var output = new Rgb[5];
        new StaticColorEffect().Render(in ctx, output);
        output.Should().OnlyContain(c => c == new Rgb(1, 2, 3));
    }

    [Fact]
    public void Faster_speed_advances_phase_faster()
    {
        var samples = Projector.Project(ZoneLayout.Point);
        var colors = new[] { new Rgb(255, 0, 0) };
        var slow = new RenderContext(1, 0, colors, 0f, 1f, Direction.Forward, samples, 0);
        var fast = new RenderContext(1, 0, colors, 1f, 1f, Direction.Forward, samples, 0);
        fast.Phase(0.1, 1.0).Should().BeGreaterThan(slow.Phase(0.1, 1.0));
    }
}
