namespace UnifiedRgb.Core.Effects;

/// <summary>How an effect moves across its strip (H2-VERTICAL-FILL §4). Only <see cref="Translational"/> is vertical-fill eligible.</summary>
public enum GeometryClass
{
    Uniform,
    Rotational,
    Translational,
    Radial,
    Random,
}

/// <summary>Which end of an effect's strip slice is the block's bottom: <see cref="Low"/> = slice start, <see cref="High"/> = slice end.</summary>
public enum BottomEnd
{
    Low,
    High,
}

/// <summary>An effect's geometry class and, for translational effects, the strip end that maps to the bottom.</summary>
public readonly record struct EffectShape(GeometryClass Class, BottomEnd? Bottom)
{
    public bool IsTranslational => Class == GeometryClass.Translational;
}

/// <summary>
/// H2-VERTICAL-FILL §4 classification table, one entry per registered effect. <see cref="BottomEnd"/> is a per-effect constant
/// (pinned by the sweep-sense tests), not a user setting: travelling bodies enter the slice at its low index (<c>Low</c> makes
/// the head rise); accumulating bodies land at the far end first (<c>High</c> makes the stack rise). A hardware mismatch is a
/// one-cell edit here.
/// </summary>
public static class EffectGeometry
{
    private static readonly EffectShape U = new(GeometryClass.Uniform, null);
    private static readonly EffectShape Rot = new(GeometryClass.Rotational, null);
    private static readonly EffectShape Rad = new(GeometryClass.Radial, null);
    private static readonly EffectShape Rnd = new(GeometryClass.Random, null);
    private static readonly EffectShape Low = new(GeometryClass.Translational, BottomEnd.Low);
    private static readonly EffectShape High = new(GeometryClass.Translational, BottomEnd.High);

    public static IReadOnlyDictionary<string, EffectShape> Table { get; } = new Dictionary<string, EffectShape>(StringComparer.Ordinal)
    {
        ["StaticColor"] = U,
        ["Rainbow"] = Rot,
        ["RainbowMorph"] = U,
        ["Breathing"] = U,
        ["Runway"] = Low,
        ["Meteor"] = Low,
        ["ColorCycle"] = U,
        ["CoverCycle"] = Low,
        ["Wave"] = Low,
        ["MeteorShower"] = Low,
        ["Twinkle"] = Rnd,
        ["Taichi"] = Rot,
        ["MopUp"] = High,
        ["Lottery"] = Rnd,
        ["Warning"] = U,
        ["Mixing"] = U,
        ["Tide"] = Low,
        ["Scan"] = Low,
        ["DoubleMeteor"] = Low,
        ["MeteorContest"] = Low,
        ["MeteorMix"] = Low,
        ["ReturnArc"] = Low,
        ["DoubleArc"] = Low,
        ["HeartBeat"] = U,
        ["HeartBeatRunway"] = Low,
        ["Disco"] = Rnd,
        ["ElectricCurrent"] = Low,
        ["GradientRibbon"] = Low,
        ["Drumming"] = U,
        ["CandyBox"] = Rnd,
        ["Burst"] = Rad,
        ["Ripple"] = Rad,
        ["Stacking"] = High,
        ["Echo"] = Low,
    };

    /// <summary>The effect's shape; an unknown effect keeps the ring (Uniform).</summary>
    public static EffectShape Of(string effectId) => Table.TryGetValue(effectId ?? string.Empty, out var s) ? s : U;

    public static bool IsTranslational(string effectId) => Of(effectId).IsTranslational;
}
