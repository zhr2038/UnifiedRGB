namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// Layout is one of "Linear", "Ring", "Matrix", "Point". Family/Units are the adapter's vendor tags (Lian Li "TL"/"H2" and fan
/// count), so the App preview selects the same loop bodies as the Host (WP-11). ChainReversed (WP-13) is the device's
/// whole-chain fan-order reversal mounting, which changes how group bodies are sliced, so the preview needs it too.
/// RingOffsetLeds/HalfStep/FaceOnFill (WP-14) are the H2 ring calibration, so the preview places the vertical fill truthfully.
/// Mapped (NATIVE design §2) is false for a native aggregate zone: one colour replicated to every record, no spatial claim;
/// the GUI tags it "unmapped". PerKey is true per-key readiness for a zone that claims independently addressable keys (null
/// otherwise, and omitted from JSON so legacy zones keep their shape); see PerKeyAdmission before any per-key output request.
/// </summary>
public sealed record ZoneDto(
    int Id,
    string Name,
    string Layout,
    int LedCount,
    int Width,
    int Height,
    string? Family = null,
    int Units = 0,
    bool ChainReversed = false,
    int RingOffsetLeds = 0,
    bool HalfStep = false,
    bool FaceOnFill = false,
    bool Mapped = true,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PerKeyStatusDto? PerKey = null);

public sealed record ZoneRefDto(string ControllerId, int ZoneId);
