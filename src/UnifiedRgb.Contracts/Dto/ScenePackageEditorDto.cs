namespace UnifiedRgb.Contracts.Dto;

/// <summary>Local, immutable playback variant. Original package bytes are never modified.</summary>
public sealed record ScenePackagePlaybackDto(double Speed = 1, bool Reverse = false, double LightingBrightness = 1,
    double HueDegrees = 0, bool LightingEnabled = true, IReadOnlyList<ScenePackageScreenEditDto>? Screens = null);

public sealed record ScenePackageScreenEditDto(string Role, double? Brightness = null, string? Overlay = null,
    string? AccentColor = null, string? Fit = null, int? Fps = null, bool? Enabled = null,
    ScreenOverlayLayoutDto? OverlayLayout = null, bool ClearOverlayLayout = false);

public sealed record ScenePackageEditVersionDto(string BindingId, DateTimeOffset SavedAt, ScenePackagePlaybackDto Playback);
public sealed record ScenePackageEditorStateDto(string ContentId, string? BindingId, ScenePackagePlaybackDto Playback,
    IReadOnlyList<ScenePackageBindingDto> Bindings, bool Ready, IReadOnlyList<string> Problems,
    IReadOnlyList<ScenePackageEditVersionDto> Versions);
public sealed record ScenePackageEditRequestDto(string ContentId, string? ExpectedBindingId,
    IReadOnlyList<ScenePackageBindingDto> Bindings, ScenePackagePlaybackDto Playback);

public sealed record ScenePackagePreviewTrackDto(string Role, string Label, string File, int LedCount, int Frames,
    double FrameDurationMs, long Bytes, string Sha256);
public sealed record ScenePackagePreviewClipDto(string File, double DurationMs, long Bytes, string Sha256);
public sealed record ScenePackagePreviewScreenDto(string Role, string Label, int Width, int Height, int Fps, string Overlay,
    string AccentColor, string Fit, ScreenOverlayLayoutDto? OverlayLayout, IReadOnlyList<ScenePackagePreviewClipDto> Clips);
public sealed record ScenePackagePreviewDto(string ContentId, string Name, double DurationMs,
    IReadOnlyList<ScenePackagePreviewTrackDto> Lighting, IReadOnlyList<ScenePackagePreviewScreenDto> Screens);
public sealed record ScenePackagePreviewChunkRequestDto(string ContentId, string File, long Offset, int Bytes = 49152);
public sealed record ScenePackagePreviewChunkDto(long Offset, long TotalBytes, string Sha256, string Base64);
public sealed record ScenePackageSaveVariantDto(string ContentId, string ExpectedBindingId, string Name, bool Overwrite = false);
