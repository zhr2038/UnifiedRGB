namespace UnifiedRgb.Contracts.Dto;

public sealed record SceneLibraryUsageDto(int Packages, int MaxPackages, long Bytes, long MaxBytes, long PendingCleanupBytes);
public sealed record ScenePackageRemovalPlanDto(string ContentId, string Name, long PackageBytes, IReadOnlyList<string> References, IReadOnlyList<string> RelatedAssetIds);
public sealed record ScenePackageRemovalRequestDto(string ContentId, bool CleanUnreferencedMedia = false);
public sealed record ScenePackageRemovalDto(bool Removed, string ContentId, long ReclaimedBytes, bool CleanupPending,
    IReadOnlyList<string> References, IReadOnlyList<string> RemovedAssetIds, IReadOnlyList<string> KeptAssetIds, string Detail);
public sealed record SceneProfileRemovalDto(bool Removed, string Name, string Detail);
