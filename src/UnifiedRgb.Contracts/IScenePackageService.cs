using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>
/// External scene packages (OPUS-GENERIC-SCENE-PACKAGES), an additional target on the same trusted pipe as
/// <see cref="IRgbService"/> (method names on the wire drop "Async"). Import, listing, details/preview and binding saves never write
/// hardware or change the running scene; only <see cref="ApplyScenePackageAsync"/> does, through the same gates as SetScene.
/// </summary>
public interface IScenePackageService
{
    Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default);

    /// <summary>Validates the whole ZIP and installs it; identical content returns the existing entry. Invalid input throws ArgumentException.</summary>
    Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default);

    Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default);

    Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default);

    Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default);

    /// <summary>Applies the package with its current valid binding snapshot as one transaction; on failure Applied is false and nothing changed.</summary>
    Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default);
}
