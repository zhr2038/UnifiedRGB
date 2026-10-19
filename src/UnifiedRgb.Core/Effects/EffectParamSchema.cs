using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects;

/// <summary>Parameter schema the GUI renders for an effect (colors, speed, direction).</summary>
public sealed record EffectParamSchema(
    string DisplayName,
    string LocalName,
    int MinColors,
    int MaxColors,
    bool UsesSpeed,
    bool UsesDirection,
    IReadOnlyList<Rgb> DefaultColors,
    bool Wireless19 = false);

/// <summary>User-chosen parameters of one assignment.</summary>
public sealed record EffectParams(IReadOnlyList<Rgb> Colors, float Speed = 0.5f, float Brightness = 1f, Direction Direction = Direction.Forward, uint Seed = 1)
{
    public static EffectParams Default { get; } = new(Array.Empty<Rgb>());
}
