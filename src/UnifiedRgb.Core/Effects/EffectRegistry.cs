using System.Collections.Frozen;
using UnifiedRgb.Core.Effects.Generators;

namespace UnifiedRgb.Core.Effects;

/// <summary>All built-in generators (30 named effects of analysis 05 §2 + 4 wireless-only). Immutable, thread-safe.</summary>
public sealed class EffectRegistry
{
    private readonly FrozenDictionary<string, IEffectGenerator> _byId;

    public EffectRegistry(IEnumerable<IEffectGenerator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        All = generators.ToArray();
        _byId = All.ToFrozenDictionary(g => g.Id, StringComparer.Ordinal);
    }

    public static EffectRegistry Default { get; } = new(CreateBuiltIns());

    public IReadOnlyList<IEffectGenerator> All { get; }

    public IEffectGenerator this[string id] =>
        _byId.TryGetValue(id, out var g) ? g : throw new KeyNotFoundException($"Unknown effect '{id}'.");

    public bool Contains(string id) => _byId.ContainsKey(id);

    public bool TryGet(string id, out IEffectGenerator generator) => _byId.TryGetValue(id, out generator!);

    public static IEffectGenerator[] CreateBuiltIns() =>
    [
        new StaticColorEffect(), new RainbowEffect(), new RainbowMorphEffect(), new BreathingEffect(), new RunwayEffect(),
        new MeteorEffect(), new ColorCycleEffect(), new CoverCycleEffect(), new WaveEffect(), new MeteorShowerEffect(),
        new TwinkleEffect(), new TaichiEffect(), new MopUpEffect(), new LotteryEffect(), new WarningEffect(),
        new MixingEffect(), new TideEffect(), new ScanEffect(), new DoubleMeteorEffect(), new MeteorContestEffect(),
        new MeteorMixEffect(), new ReturnArcEffect(), new DoubleArcEffect(), new HeartBeatEffect(), new HeartBeatRunwayEffect(),
        new DiscoEffect(), new ElectricCurrentEffect(), new GradientRibbonEffect(), new DrummingEffect(), new CandyBoxEffect(),
        new BurstEffect(), new RippleEffect(), new StackingEffect(), new EchoEffect(),
    ];
}
