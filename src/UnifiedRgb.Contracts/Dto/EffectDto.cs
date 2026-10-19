namespace UnifiedRgb.Contracts.Dto;

/// <summary>Effect with its parameter schema. Colors are "#RRGGBB".</summary>
public sealed record EffectDto(
    string Id,
    string DisplayName,
    int MinColors,
    int MaxColors,
    bool UsesSpeed,
    bool UsesDirection,
    bool Wireless,
    IReadOnlyList<string> DefaultColors);
