using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

public sealed partial class ScenePackageService
{
    public async Task<SceneLibraryUsageDto> GetSceneLibraryUsageAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var usage = await Task.Run(Store.LibraryUsage, ct).ConfigureAwait(false);
            return new(usage.Count, ScenePackageStore.MaxPackages, usage.Bytes, ScenePackageStore.MaxStoreBytes, usage.Pending);
        }
        finally { _gate.Release(); }
    }

    public async Task<ScenePackageRemovalPlanDto> InspectScenePackageRemovalAsync(string contentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var package = await RequireAsync(contentId, ct).ConfigureAwait(false);
            return await Task.Run(() => SceneStateFiles.Coordinate(Store.DataRoot,
                () => new ScenePackageRemovalPlanDto(package.ContentId, package.Manifest.Name, Store.InstalledBytes(package), PackageReferences(contentId), RelatedAssets(package))), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ScenePackageRemovalDto> RemoveScenePackageAsync(ScenePackageRemovalRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var package = await RequireAsync(request.ContentId, ct).ConfigureAwait(false);
            string[] blockers = [];
            var detached = await Task.Run(() => SceneStateFiles.Coordinate(Store.DataRoot, () =>
            {
                ct.ThrowIfCancellationRequested();
                blockers = PackageReferences(package.ContentId);
                if (blockers.Length > 0) return ((string Trash, long Bytes, IReadOnlySet<string> Owned)?)null;
                var removed = Store.Detach(package);
                _source.Forget(package.ContentId); _timeline.Forget(package.ContentId);
                return removed;
            }), ct).ConfigureAwait(false);
            if (detached is null) return new(false, package.ContentId, 0, false, blockers, [], [], "Package is referenced; nothing was removed.");

            // Logical removal already committed. Cancellation must not pretend it did not happen or skip the honest cleanup result.
            var purge = await Task.Run(() => OwnedLibraryPaths.Purge(Store.Root, detached.Value.Trash, detached.Value.Owned, ScenePackageFormat.ManifestName)).ConfigureAwait(false);
            var pending = !purge; long reclaimed = purge ? detached.Value.Bytes : 0;
            var removedAssets = new List<string>(); var keptAssets = new List<string>();
            foreach (var asset in RelatedAssets(package))
            {
                if (!request.CleanUnreferencedMedia || _media is null) { keptAssets.Add(asset); continue; }
                try
                {
                    var result = await _media.DeleteUnusedPackageAssetAsync(asset, ProfileReferencesAsset, CancellationToken.None).ConfigureAwait(false);
                    if (result.Removed) removedAssets.Add(asset); else keptAssets.Add(asset);
                    reclaimed += result.ReclaimedBytes; pending |= result.CleanupPending;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { keptAssets.Add(asset); pending = true; } // original is removed; a partial cleanup is visible and never reported as an atomic failure
            }
            return new(true, package.ContentId, reclaimed, pending, [], removedAssets, keptAssets,
                pending ? "Package removed. Some owned files need cleanup retry; saved bindings were kept." : "Package removed. Local binding history was kept; original upload files outside this library were untouched.");
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneProfileRemovalDto> RemoveSavedSceneAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_profiles is null) throw new NotSupportedException("This Host has no managed profile library.");
        if (string.Equals(name, "Before Lights Off", StringComparison.OrdinalIgnoreCase))
            return new(false, name, "The lights-off recovery point is protected. It was not removed.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var deleted = await Task.Run(() => _profiles.Delete(name), ct).ConfigureAwait(false);
            return new(deleted, name, deleted ? "Saved scene removed; current playback was not changed." : "Saved scene does not exist.");
        }
        finally { _gate.Release(); }
    }

    private static string[] RelatedAssets(InstalledScenePackage package) => package.Manifest.Screens
        .Select(s => package.Manifest.Files[s.File][..16]).Distinct(StringComparer.Ordinal).ToArray();
}
