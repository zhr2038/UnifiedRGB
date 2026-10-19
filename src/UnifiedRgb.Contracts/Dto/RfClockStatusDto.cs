namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// NATIVE-RF-CLOCK diagnostics: whether the experimental device-clock reference is enabled (LianLi:UseDeviceClock) and which
/// reference clock each assignment's streamed members used on the last frame. A device-clock sample is a successful USB 17
/// query, not a receiver playback ACK; extrapolated and derived values are host model values.
/// </summary>
public sealed record RfClockStatusDto(bool Enabled, IReadOnlyList<StreamedClockDto> Assignments);

/// <summary>
/// One assignment: <c>Mode</c> is "MasterTime", "LocalEpoch", "UploadAnchor" (legacy, or a device-clock fallback with
/// <c>FallbackReason</c>), "DeviceClock" or "LoopsOnly". Sample fields describe the first uploaded loop's clock domain (raw
/// last reading, its age and query latency, extrapolated counter). <c>NominalPeriodMs</c> is the old header model (frames ×
/// interval as ms); <c>DerivedPeriodMs</c> is the device-clock model's period (frames × interval ticks ÷ <c>TicksPerSecond</c>),
/// not a measurement. <c>ReferenceFrame</c> is the frame the model predicts, only in DeviceClock mode. <c>UploadedFrames</c> /
/// <c>UploadedInterval</c> are what the completed upload of the reference loop's current index transmitted (null until reported).
/// </summary>
public sealed record StreamedClockDto(
    string Assignment,
    string Mode,
    string? FallbackReason = null,
    string? Domain = null,
    uint? RawTicks = null,
    double? SampleAgeMs = null,
    double? QueryLatencyMs = null,
    double TicksPerSecond = 1600,
    double? ExtrapolatedTicks = null,
    int ReferenceFrames = 0,
    double ReferenceInterval = 0,
    double NominalPeriodMs = 0,
    double? DerivedPeriodMs = null,
    bool WirePeriodsDiffer = false,
    int? ReferenceFrame = null,
    int? UploadedFrames = null,
    double? UploadedInterval = null);
