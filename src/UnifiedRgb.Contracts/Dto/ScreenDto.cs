namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// One USB screen (OPUS-SCREENS work order). Id is "screen:&lt;tl-lcd|h2|turzx&gt;:&lt;instance&gt;"; Family is "TlLcd", "H2" or
/// "Turzx". Width/Height are the authoring canvas at Rotation 0 (Turzx: landscape 1920x480). State is "Disabled", "Idle",
/// "Running", "Blocked", "Error", "Disconnected", "Unsupported", "Stopped" or "Sleeping" (native backlight off). FramesWritten counts completed USB writes, which
/// are not optical proof; ControlVerified is the owner's acceptance flag from configuration. A physical position (left/middle/
/// right) is never inferred.
/// </summary>
public sealed record ScreenDeviceDto(
    string Id,
    string Name,
    string Family,
    int Width,
    int Height,
    string State,
    string? BlockedBy,
    bool ControlVerified,
    long FramesWritten,
    long Dropped,
    string? LastError,
    double EffectiveFps);

/// <summary>An owned, normalized media asset. Kind is "image" (DurationMs 0) or "animation".</summary>
public sealed record ScreenAssetDto(string Id, string Name, int Width, int Height, int FrameCount, int DurationMs, string Kind);

/// <summary>An upload: a display name and the raw JPEG, PNG or GIF bytes as Base64 (at most 12 MiB decoded).</summary>
public sealed record ScreenAssetUploadDto(string Name, string Base64);

/// <summary>
/// What one screen shows. BackgroundAssetId null = BackgroundColor only. Overlay is "none", "compact" or "dashboard"; Rotation
/// 0/90/180/270 clockwise; Brightness 0..1; Fps 1..15; Fit "cover" (crop) or "contain" (letterbox on BackgroundColor).
/// TimelineId (optional, OPUS-GENERIC-SCENE-PACKAGES; not written when null) puts the screen on a shared playback timeline (a
/// package's "package:&lt;contentId&gt;:&lt;bindingId&gt;") instead of the worker's local start; null keeps the historical behavior.
/// OverlayLayout (optional, scene content; not written when null) confines the overlay to a region; null = historical overlay.
/// </summary>
public sealed record ScreenAssignmentDto(
    string ScreenId,
    string? BackgroundAssetId = null,
    string BackgroundColor = "#000000",
    string Overlay = "none",
    int Rotation = 0,
    double Brightness = 1,
    int Fps = 5,
    bool Enabled = true,
    string AccentColor = "#5DDAC8",
    string Fit = "cover",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? TimelineId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ScreenOverlayLayoutDto? OverlayLayout = null);

/// <summary>
/// Optional telemetry region of a dashboard overlay (scene package schemaVersion 2; not written when null). X/Y/Width/
/// Height are fractions of the CONTENT canvas before the screen's local rotation (finite, X,Y &gt;= 0, Width,Height &gt; 0, every
/// value and X+Width, Y+Height &lt;= 1). Columns 1..4 arrange the four readings (rows = ceil(4/Columns)); BackgroundOpacity 0..1 is
/// the region's backing (0 = none). Rectangular screens only. Null keeps the historical full-screen overlay exactly.
/// </summary>
public sealed record ScreenOverlayLayoutDto(double X, double Y, double Width, double Height, int Columns, double BackgroundOpacity);

/// <summary>Screen content only: never RGB zones. At most one assignment per screen id.</summary>
public sealed record ScreenSceneDto(IReadOnlyList<ScreenAssignmentDto> Assignments);

/// <summary>
/// Result of the manual fan-LCD group restart (IScreenService.RestartFanLcdAsync). CommandAccepted: a FRESH receiver report carried
/// the new RF command sequence (sends alone never count). ScreensReconnected: every configured screen id is enumerated again AND,
/// for each whose output was running (output gate open, not paused by StopScreens, enabled assignment), a new worker wrote new
/// frames; for screens not running before (paused/unassigned/gated) it means enumeration only, as Message states. Neither is
/// optical proof. Attempts counts RF sends (0 = nothing sent). ScreenIds are the configured screens. Message is English, starts
/// with an outcome word (Restarted, Partial, NotAccepted, Busy, Cooldown, Precheck, Disabled) and lists actual final states.
/// </summary>
public sealed record ScreenRestartDto(bool CommandAccepted, bool ScreensReconnected, int Attempts, IReadOnlyList<string> ScreenIds, string Message);

/// <summary>The composed device canvas as PNG (no USB involved) and a note on the encoder result.</summary>
public sealed record ScreenPreviewDto(string PngBase64, int Width, int Height, string Note);

/// <summary>Read-only PC metrics; an unavailable value is null, never a fabricated 0.</summary>
public sealed record PcTelemetryDto(
    DateTimeOffset Timestamp,
    double? CpuUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? GpuUsagePercent,
    double? GpuTemperatureC,
    long? GpuMemoryUsedBytes,
    long? GpuMemoryTotalBytes,
    string Source,
    string? Error);
