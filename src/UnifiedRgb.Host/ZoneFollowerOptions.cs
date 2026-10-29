using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host;

/// <summary>Explicit local fallback routes, separate from immutable scene packages. No guessed device IDs or defaults.</summary>
public sealed record ZoneFollowerOptions
{
    public string SourceControllerId { get; init; } = string.Empty;
    public int SourceZoneId { get; init; }
    public int SourceLed { get; init; }
    public string TargetControllerId { get; init; } = string.Empty;
    public int TargetZoneId { get; init; }

    public ZoneFollower ToBinding() => new(new(new ControllerId(SourceControllerId), new ZoneId(SourceZoneId)), SourceLed,
        new(new ControllerId(TargetControllerId), new ZoneId(TargetZoneId)));
}
