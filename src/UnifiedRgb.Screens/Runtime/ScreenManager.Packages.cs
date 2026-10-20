using UnifiedRgb.Screens.Media;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Decoded facts of one screen medium, from the existing bounded importer (no store write, no USB).</summary>
/// <param name="DurationMs">Normalized loop duration (sum of the played GIF delays); 0 for a still image.</param>
/// <param name="ThumbnailPng">The first frame fitted into a <see cref="ScreenManager.ThumbnailBox"/>-pixel box.</param>
public sealed record ScreenMediaInfo(string Format, int Width, int Height, int Frames, int DurationMs, byte[] ThumbnailPng);
public sealed record ScreenAssetCleanupResult(bool Removed, bool CleanupPending, long ReclaimedBytes);

/// <summary>
/// The screen side of external scene packages (OPUS-GENERIC-SCENE-PACKAGES): media validation with the existing importer and
/// limits (12 MiB / 300 frames / 30 s / normalization), installation of a package's media as ordinary content-addressed assets,
/// protection of those assets from generic deletion, and the shared timeline workers read. None of it opens a device.
/// </summary>
public interface IScreenPackageMedia
{
    /// <summary>Decodes <paramref name="data"/> exactly as an upload would; invalid media → ArgumentException. Stores nothing.</summary>
    Task<ScreenMediaInfo> ProbeMediaAsync(byte[] data, CancellationToken ct = default);

    /// <summary>Imports <paramref name="data"/> into the asset store (idempotent by content) and returns its asset id.</summary>
    Task<string> InstallPackageAssetAsync(string name, byte[] data, CancellationToken ct = default);

    /// <summary>True for an asset an installed package needs: DeleteScreenAsset refuses it (null: nothing is protected).</summary>
    Func<string, bool>? AssetPinned { get; set; }

    /// <summary>The shared timeline provider for assignments with a TimelineId (null: those screens show black, labelled BLOCKED).</summary>
    IScreenTimeline? Timeline { get; set; }

    /// <summary>
    /// True while a fan-LCD restart holds <paramref name="screenId"/> without a worker (it reports Stopped). Distinguishes that
    /// hold, which an apply must not override, from a StopScreens pause, which an explicit scene apply resumes.
    /// </summary>
    bool RestartHolds(string screenId);

    bool ContainsAsset(string assetId);

    /// <summary>Explicitly requested related-media cleanup. All live, installed-package and caller-supplied profile references protect it.</summary>
    Task<ScreenAssetCleanupResult> DeleteUnusedPackageAssetAsync(string assetId, Func<string, bool> profileReference, CancellationToken ct = default);
}

public sealed partial class ScreenManager : IScreenPackageMedia
{
    /// <summary>Largest side of a package role thumbnail.</summary>
    public const int ThumbnailBox = 160;

    public ScreenSceneDto? CurrentReferences() => Volatile.Read(ref _scene);

    public bool ContainsAsset(string assetId) => _assets.Find(assetId) is not null;

    public async Task<ScreenAssetCleanupResult> DeleteUnusedPackageAssetAsync(string assetId, Func<string, bool> profileReference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profileReference);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            return SceneStateFiles.Coordinate(_stateRoot, () =>
            {
                if ((_scene?.Assignments.Any(a => a.BackgroundAssetId == assetId) ?? false)
                    || AssetPinned?.Invoke(assetId) == true || profileReference(assetId)) return new ScreenAssetCleanupResult(false, false, 0);
                return _assets.DeleteDetailed(assetId);
            });
        }
        finally { _mutation.Release(); }
    }

    private IScreenTimeline? _timeline;

    public Func<string, bool>? AssetPinned { get; set; }

    public IScreenTimeline? Timeline
    {
        get => Volatile.Read(ref _timeline);
        set => Volatile.Write(ref _timeline, value);
    }

    public async Task<ScreenMediaInfo> ProbeMediaAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var limits = ImportLimits.Default;
        return await _importThread.Value.InvokeAsync(() =>
        {
            var import = ImageImporter.Import(data, limits);
            var thumb = ImageImporter.Import(import.FramePngs[0], limits with { NormalizeBox = ThumbnailBox });
            return new ScreenMediaInfo(import.Format, import.Width, import.Height, import.FramePngs.Count, import.DurationMs, thumb.FramePngs[0]);
        }, ct).ConfigureAwait(false);
    }

    public async Task<string> InstallPackageAssetAsync(string name, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var stored = await ImportAssetAsync(name, data, ct).ConfigureAwait(false);
        return stored.Id;
    }

    /// <summary>The upload path shared by UploadScreenAsset and package installation: idempotent by content id.</summary>
    private async Task<StoredAsset> ImportAssetAsync(string name, byte[] data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"An asset name must be 1..{MaxNameLength} printable characters.", nameof(name));
        }

        var limits = ImportLimits.Default;
        if (data.Length == 0 || data.Length > limits.MaxBytes)
        {
            throw new ArgumentException($"An asset must be 1..{limits.MaxBytes} bytes (got {data.Length}).", nameof(data));
        }

        var id = AssetStore.IdOf(data);
        if (_assets.Find(id) is { } existing)
        {
            return existing;
        }

        _assets.RequireRoomForOne();
        var import = await _importThread.Value.InvokeAsync(() => ImageImporter.Import(data, limits), ct).ConfigureAwait(false);
        var stored = await Task.Run(() => _assets.Commit(id, trimmed, import), ct).ConfigureAwait(false);
        LogUploaded(_log, stored.Id, stored.Kind, stored.Width, stored.Height, stored.FrameCount, stored.Bytes);
        return stored;
    }

    public bool RestartHolds(string screenId)
    {
        lock (_gate)
        {
            return _quiesced?.Contains(screenId) == true;
        }
    }

    /// <summary>The timeline label the screen's current worker last composed with (null: no worker or no TimelineId). Diagnostics/tests.</summary>
    internal string? TimelineSourceOf(string screenId)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(screenId, out var w) ? w.TimelineSource : null;
        }
    }
}
