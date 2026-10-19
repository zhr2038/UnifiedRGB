namespace UnifiedRgb.Contracts.Dto;

/// <summary>Per-fan mounting override; <c>Fan</c> is the 0-based chain slot.</summary>
public sealed record FanMountingDto(int Fan, int RotationDegrees, bool Reverse);

/// <summary>
/// Physical mounting of one Lian Li wireless device (WP-11): clockwise rotation viewed from the lit face (0/90/180/270) and a
/// mounting mirror, applied per fan, with optional per-fan overrides. Separate from an effect's Reverse and from profiles.
/// <c>ReverseChain</c> (WP-13, schema 2) is the separate whole-chain fan-order reversal (vendor per-device GetTL/GetH2
/// direction 1), applied when a group body is sliced; it is not the fan-local mirror. Absent in schema-1 files = false.
/// <c>RingOffsetLeds</c>, <c>HalfStep</c> and <c>FaceOnFill</c> (WP-14, schema 3, H2-VERTICAL-FILL §3) are the H2 ring
/// calibration: fine clockwise shift in LEDs, bottom-between-two-LEDs, and vertical fill of translational group effects.
/// Absent in schema-1/2 files = 0/false. Ring (H2) devices only.
/// Read-only fields (Family, Units, Revision, Description, Overridden, Note) are filled by the Host and ignored on set.
/// </summary>
public sealed record MountingDto(
    string ControllerId,
    int RotationDegrees,
    bool Reverse,
    IReadOnlyList<FanMountingDto> Fans,
    string? Family = null,
    int Units = 0,
    int Revision = 0,
    string? Description = null,
    bool Overridden = false,
    string? Note = null,
    bool ReverseChain = false,
    int RingOffsetLeds = 0,
    bool HalfStep = false,
    bool FaceOnFill = false);

/// <summary>&lt;DataDirectory&gt;\mounting.json: GUI overrides keyed by controller ID.</summary>
public sealed record MountingFileDto(int SchemaVersion, IReadOnlyList<MountingDto> Devices)
{
    /// <summary>
    /// 2 (WP-13) adds <c>ReverseChain</c>; 3 (WP-14) adds <c>RingOffsetLeds</c>/<c>HalfStep</c>/<c>FaceOnFill</c>. Older files
    /// load with the new fields at their defaults and are not rewritten until the owner saves a mounting.
    /// </summary>
    public const int CurrentSchema = 3;
}

/// <summary>
/// WP-14 ring calibration step (H2-VERTICAL-FILL §3): <c>Step</c> is "marker" (LED 0 red, 1..3 dim green) or "wedge" (fill at
/// p = 0.25); the rotation/offset/half-step are the candidate values shown (not saved until EndRingCalibration with apply).
/// </summary>
public sealed record RingCalibrationDto(string ControllerId, string Step, int RotationDegrees, int RingOffsetLeds, bool HalfStep)
{
    public const string Marker = "marker";
    public const string Wedge = "wedge";
}
