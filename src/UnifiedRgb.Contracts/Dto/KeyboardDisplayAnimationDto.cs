namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// The local keyboard OLED animation preference (a Host sidecar file; never part of a scene package). Enabled is off by default.
/// AssetId + TimelineId bind one screen asset of the current screen scene and the shared timeline that scene plays it on.
/// Fit is "contain" or "cover"; Encoding is one of <see cref="KeyboardDisplayAnimationEncodings"/>; IntervalMs is the minimum
/// time between two animation uploads (a cadence ceiling, not a frame rate).
/// </summary>
public sealed record KeyboardDisplayAnimationSettingsDto(bool Enabled, string? AssetId, string? TimelineId, string Fit,
    string Encoding, int IntervalMs);

/// <summary>A bindable asset: an assignment of the current screen scene with a shared TimelineId and a stored asset.</summary>
public sealed record KeyboardDisplayAnimationChoiceDto(string AssetId, string TimelineId, string ScreenId, string Name,
    int FrameCount, int DurationMs, string Kind);

/// <summary>
/// What the Host reports. Settings.Enabled is the request; Admitted is the Host gate; State is the effective outcome (see
/// <see cref="KeyboardDisplayAnimationStates"/>). Selected/Acknowledged/Skipped count animation frames chosen for upload,
/// frames whose full upload the keyboard acknowledged (communication evidence, not optical confirmation) and newer source
/// frames that appeared while the cadence gate held. No visible frame rate is measured or implied.
/// </summary>
public sealed record KeyboardDisplayAnimationStatusDto(
    bool Admitted, KeyboardDisplayAnimationSettingsDto Settings, string State, string Detail,
    IReadOnlyList<KeyboardDisplayAnimationChoiceDto> Choices, int MinimumIntervalMs, int MaximumIntervalMs,
    int SourceFrames, int CachedFrames, long CacheBytes, long Selected, long Acknowledged, long Skipped,
    DateTimeOffset? UpdatedAt, string? LastError);

/// <summary>Asks the Host to encode a few positions of one bound choice with the given fit and encoding.</summary>
public sealed record KeyboardDisplayAnimationPreviewRequestDto(string AssetId, string TimelineId, string Fit, string Encoding);

/// <summary>Packed 4-bit frames exactly as they would be uploaded (see <see cref="KeyboardOledFormat"/>), base64.</summary>
public sealed record KeyboardDisplayAnimationPreviewDto(int Width, int Height, string Encoding,
    IReadOnlyList<KeyboardDisplayAnimationPreviewFrameDto> Frames, string Note);

public sealed record KeyboardDisplayAnimationPreviewFrameDto(int PositionMs, string PackedBase64);

/// <summary>The explicit, persisted gray encodings. Neither changes the status page encoding.</summary>
public static class KeyboardDisplayAnimationEncodings
{
    /// <summary>Luma (54R+183G+19B)/256, identity tone, nearest level (y*15+127)/255.</summary>
    public const string LumaRound = "luma-round-v1";

    /// <summary>The same luma and tone with a fixed 4x4 ordered (Bayer) threshold; no error diffusion.</summary>
    public const string LumaBayer4 = "luma-bayer4-v1";

    public static bool IsKnown(string? encoding) => encoding is LumaRound or LumaBayer4;
}

public static class KeyboardDisplayAnimationStates
{
    /// <summary>Not requested.</summary>
    public const string Off = "Off";
    /// <summary>The Host admission gate is off; any saved request is kept but never acted on.</summary>
    public const string NotAdmitted = "NotAdmitted";
    /// <summary>The local settings file could not be read; animation stays off and the file is not overwritten.</summary>
    public const string SettingsError = "SettingsError";
    /// <summary>Installed native-menu ownership: no legacy image is sent; preference and binding are kept.</summary>
    public const string SuppressedByNativeMenu = "SuppressedByNativeMenu";
    /// <summary>The keyboard status page is off, so the original ROG display is shown.</summary>
    public const string PageOff = "PageOff";
    /// <summary>The binding is not in the current screen scene (or not a stored asset).</summary>
    public const string Unavailable = "Unavailable";
    public const string Preparing = "Preparing";
    /// <summary>Preparation refused (budget, decode, cancellation by timeout); no partial cache exists.</summary>
    public const string Error = "Error";
    /// <summary>Prepared; waiting for the keyboard owner to run.</summary>
    public const string Ready = "Ready";
    /// <summary>The shared timeline is unknown or blocked: the status page is shown, never a held or black frame.</summary>
    public const string Fallback = "Fallback";
    /// <summary>A completed upload exceeded the budget: no new animation frame is selected until an explicit re-save.</summary>
    public const string BreakerTripped = "BreakerTripped";
    public const string Playing = "Playing";
}
