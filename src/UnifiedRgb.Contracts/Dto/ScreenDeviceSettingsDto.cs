namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// The local, per-device settings of one screen (never part of scene or scene-package content). Rotation 0/90/180/270 clockwise is
/// the authoritative orientation of the content shown on this physical screen; EncodingQuality is a versioned local encoder policy
/// ("legacy" or "lossless-first-v1"). Indexed8Verified is read-only and computed from the Host configuration
/// (ScreensOptions.Indexed8VerifiedScreenIds). The research-only palette token is refused even when this flag is true.
/// </summary>
public sealed record ScreenDeviceSettingsDto(string ScreenId, int Rotation, string EncodingQuality, bool Indexed8Verified);

/// <summary>An explicit save of one screen's local settings (see <see cref="ScreenDeviceSettingsDto"/>); no capability fields.</summary>
public sealed record ScreenDeviceSettingsUpdateDto(string ScreenId, int Rotation = 0, string EncodingQuality = "legacy");
