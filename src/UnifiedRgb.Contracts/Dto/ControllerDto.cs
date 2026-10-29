namespace UnifiedRgb.Contracts.Dto;

/// <summary>Engine state of one controller as seen by clients.</summary>
public static class ControllerStates
{
    public const string Active = "Active";
    public const string Blocked = "Blocked";
    public const string Contended = "Contended";
    public const string Offline = "Offline";

    /// <summary>A native bring-up session holds the controller (NATIVE design §6); BlockedBy names the lease.</summary>
    public const string BringUp = "BringUp";
}

/// <summary>
/// One discovered controller. <c>ControlVerified</c> (NATIVE design §2/§6) is false for a native controller whose class gate
/// has not been set after the morning E3c: discoverable and openable by a bring-up session only, never a scene writer.
/// Older Hosts may omit it; missing verification is displayed conservatively as unverified. The Host remains the write gate.
/// PerKeyFrame is the adapter's last software-generated per-key frame (review data, not optical proof); null and omitted otherwise.
/// </summary>
public sealed record ControllerDto(
    string Id,
    string Vendor,
    string Name,
    string DeviceClass,
    string DeviceType,
    string Serial,
    IReadOnlyList<ZoneDto> Zones,
    int MaxFps,
    string State,
    string? BlockedBy,
    bool ControlVerified = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PerKeyFrameDto? PerKeyFrame = null);
