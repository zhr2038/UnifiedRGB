using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>Explicit local library maintenance. No command changes the current playback or user configuration.</summary>
public interface ISceneLibraryService
{
    Task<SceneLibraryUsageDto> GetSceneLibraryUsageAsync(CancellationToken ct = default);
    Task<ScenePackageRemovalPlanDto> InspectScenePackageRemovalAsync(string contentId, CancellationToken ct = default);
    Task<ScenePackageRemovalDto> RemoveScenePackageAsync(ScenePackageRemovalRequestDto request, CancellationToken ct = default);
    Task<SceneProfileRemovalDto> RemoveSavedSceneAsync(string name, CancellationToken ct = default);
}
