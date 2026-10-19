using System.Text.Json.Serialization;

namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// A scene-package upload (OPUS-GENERIC-SCENE-PACKAGES): a display name for logs and the raw ZIP bytes as Base64 (at most 64 MiB
/// decoded). The package's own manifest id/name win; Name is only the uploader's label.
/// </summary>
public sealed record ScenePackageUploadDto(string Name, string Base64);

/// <summary>
/// One installed, validated package. ContentId is the immutable lowercase SHA-256 content address (independent of bindings);
/// Id/Name/Revision come from the manifest; PlaybackContract is the versioned playback semantics ("rgb24-hold-physical-ms-v1").
/// </summary>
public sealed record ScenePackageInfoDto(
    string ContentId,
    string Id,
    string Name,
    int Revision,
    string PlaybackContract,
    double DurationMs,
    int LightingRoles,
    int ScreenRoles);

/// <summary>
/// One authored role. Kind is "lighting" or "screen". Lighting: LedCount/Frames, Width/Height 0. Screen: Width/Height of the
/// decoded media, Frames its decoded frame count, LedCount 0. PreviewColors is the authored first RGB frame of a lighting role as
/// "#RRGGBB" in logical order (no physical mapping; empty for screens); PreviewPngBase64 a bounded thumbnail of a screen role's first
/// frame (null for lighting). Label is the manifest label or the role.
/// </summary>
public sealed record ScenePackageRoleDto(
    string Role,
    string Label,
    string Kind,
    int LedCount,
    int Width,
    int Height,
    int Frames,
    double DurationMs,
    IReadOnlyList<string> PreviewColors,
    string? PreviewPngBase64 = null);

public sealed record ScenePackageDetailsDto(ScenePackageInfoDto Info, IReadOnlyList<ScenePackageRoleDto> Roles);

/// <summary>A local binding of one role: a lighting role binds Zone, a screen role binds ScreenId; null = unbound.</summary>
public sealed record ScenePackageBindingDto(string Role, ZoneRefDto? Zone = null, string? ScreenId = null);

public sealed record ScenePackageBindingsDto(string ContentId, IReadOnlyList<ScenePackageBindingDto> Bindings);

/// <summary>
/// The binding state of one package. BindingId is the immutable hash of the current VALID snapshot (null = none saved). Bindings
/// lists every role (unbound roles have null targets). Ready = a valid snapshot exists and its targets are present right now;
/// Problems explains every reason it is not. A rejected save returns Ready false with Problems and the previous snapshot stays current.
/// </summary>
public sealed record ScenePackageBindingStateDto(
    string ContentId,
    string? BindingId,
    IReadOnlyList<ScenePackageBindingDto> Bindings,
    bool Ready,
    IReadOnlyList<string> Problems);

public sealed record ScenePackageApplyDto(bool Applied, string ContentId, string? BindingId, string Detail);

/// <summary>
/// Package provenance on a scene (optional on <see cref="SceneDto"/>; not written when null, so legacy scenes and profiles
/// serialize byte for byte as before). It pins the immutable content, the immutable binding snapshot and the playback contract;
/// a profile carrying it re-resolves exactly that content and binding, never the package's later current binding.
/// </summary>
public sealed record ScenePackageRefDto(string ContentId, string BindingId, string PlaybackContract);
