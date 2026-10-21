using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Media;

/// <summary>A stored, normalized asset. Frames are PNG files f000.png.. in the asset directory.</summary>
internal sealed record StoredAsset(string Id, string Name, string Format, int Width, int Height, IReadOnlyList<int> DelaysMs, long Bytes)
{
    public int FrameCount => DelaysMs.Count;

    public bool Animated => FrameCount > 1;

    public int DurationMs => Animated ? DelaysMs.Sum() : 0;

    public string Kind => Format == "mp4" ? "video" : Animated ? "animation" : "image";

    public ScreenAssetDto ToDto() => new(Id, Name, Width, Height, FrameCount, DurationMs, Kind);

    /// <summary>The frame shown <paramref name="elapsedMs"/> after the animation started (looping); 0 for a still image.</summary>
    public int FrameAt(long elapsedMs)
    {
        if (!Animated || DurationMs <= 0)
        {
            return 0;
        }

        var t = (int)(Math.Max(0, elapsedMs) % DurationMs);
        for (var i = 0; i < DelaysMs.Count; i++)
        {
            t -= DelaysMs[i];
            if (t < 0)
            {
                return i;
            }
        }

        return DelaysMs.Count - 1;
    }
}

internal sealed record AssetMeta(int Version, string Id, string Name, string Format, int Width, int Height, int[] DelaysMs);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AssetMeta))]
internal sealed partial class ScreensJsonContext : JsonSerializerContext;

/// <summary>
/// Bounded on-disk store under &lt;DataDirectory&gt;\screens\assets. Each asset is written into a temporary directory and renamed
/// into place, so a crash leaves only "~" debris that the next start removes. Ids are content hashes, so an identical upload
/// maps to the existing asset. All members are thread-safe.
/// </summary>
internal sealed partial class AssetStore
{
    public const int MaxFrameFileBytes = 32 * 1024 * 1024;
    private const int MaxMetaBytes = 64 * 1024;

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly int _maxCount;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredAsset> _assets = new(StringComparer.Ordinal);
    private bool _loaded;
    // Deterministic concurrency seam for the disk-write/atomic-commit boundary; never set in production.
    internal Action? BeforeCommit { get; set; }

    public AssetStore(string root, long maxBytes, int maxCount, ILogger log)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(log);
        _root = root;
        _maxBytes = maxBytes;
        _maxCount = maxCount;
        _log = log;
    }

    public string Root => _root;

    /// <summary>"0123456789abcdef": the first 16 hex characters of the upload's SHA-256.</summary>
    public static string IdOf(ReadOnlySpan<byte> upload) => Convert.ToHexString(SHA256.HashData(upload))[..16].ToLowerInvariant();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public IReadOnlyList<StoredAsset> List()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return [.. _assets.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal)];
        }
    }

    public StoredAsset? Find(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _assets.GetValueOrDefault(id);
        }
    }

    /// <summary>Fails early (before decoding) when the store cannot take one more asset.</summary>
    public void RequireRoomForOne()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_assets.Count >= _maxCount)
            {
                throw new InvalidOperationException($"The screen asset store already holds {_maxCount} assets; delete one first.");
            }
        }
    }

    /// <summary>Stores an imported asset under <paramref name="id"/>, or returns the existing asset with that id.</summary>
    public StoredAsset Commit(string id, string name, ImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (!IsValidId(id))
        {
            throw new ArgumentException("invalid asset id", nameof(id));
        }

        var bytes = import.FramePngs.Sum(f => (long)f.Length);
        lock (_gate)
        {
            EnsureLoaded();
            if (_assets.TryGetValue(id, out var existing))
            {
                return existing;
            }

            if (_assets.Count >= _maxCount)
            {
                throw new InvalidOperationException($"The screen asset store already holds {_maxCount} assets; delete one first.");
            }

            var used = _assets.Values.Sum(a => a.Bytes);
            if (used + bytes > _maxBytes)
            {
                throw new InvalidOperationException($"The screen asset store would exceed {_maxBytes} bytes ({used} used, {bytes} needed); delete an asset first.");
            }
        }

        // write outside the lock (I/O), then rename into place under it
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, "~tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string? stale = null;
        try
        {
            // The private staging metadata also bounds crash cleanup; publication still happens only after every frame exists.
            var meta = new AssetMeta(import.Format == "mp4" ? 2 : 1, id, name, import.Format, import.Width, import.Height, [.. import.DelaysMs]);
            File.WriteAllBytes(Path.Combine(temp, "meta.json"), JsonSerializer.SerializeToUtf8Bytes(meta, ScreensJsonContext.Default.AssetMeta));
            for (var i = 0; i < import.FramePngs.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(temp, FrameFile(i)), import.FramePngs[i]);
            }

            var asset = new StoredAsset(id, name, import.Format, import.Width, import.Height, [.. import.DelaysMs], bytes);
            BeforeCommit?.Invoke();
            lock (_gate)
            {
                if (_assets.TryGetValue(id, out var raced))
                {
                    return raced;
                }

                // Another upload can commit while this upload writes its temporary files.
                // Recheck both budgets at the actual publication boundary, under the same lock.
                if (_assets.Count >= _maxCount)
                    throw new InvalidOperationException($"The screen asset store already holds {_maxCount} assets; delete one first.");
                var used = _assets.Values.Sum(a => a.Bytes);
                if (used + bytes > _maxBytes)
                    throw new InvalidOperationException($"The screen asset store would exceed {_maxBytes} bytes ({used} used, {bytes} needed); delete an asset first.");

                var target = Path.Combine(_root, id);
                if (Directory.Exists(target))
                {
                    // an unindexed directory under this id was skipped at load (corrupt or incomplete normalized cache): it is
                    // replaced by this fresh normalization, never reused
                    stale = Path.Combine(_root, "~del-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(target, stale);
                }

                Directory.Move(temp, target);
                _assets[id] = asset;
                return asset;
            }
        }
        finally
        {
            TryDeleteDirectory(temp);
            if (stale is not null)
            {
                TryDeleteDirectory(stale);
            }
        }
    }

    /// <summary>
    /// Removes an asset; false when it does not exist. The directory is renamed away before the index forgets it, so a rename
    /// that fails (a reader still has a frame open) throws and changes nothing.
    /// </summary>
    public bool Delete(string id) => DeleteDetailed(id).Removed;

    public ScreenAssetCleanupResult DeleteDetailed(string id)
    {
        string trash;
        HashSet<string> owned;
        long bytes;
        lock (_gate)
        {
            EnsureLoaded();
            if (!IsValidId(id) || !_assets.TryGetValue(id, out var asset))
            {
                return new(false, false, 0);
            }

            owned = Enumerable.Range(0, asset.FrameCount).Select(FrameFile).Append("meta.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
            _ = OwnedLibraryPaths.Inventory(_root, Path.Combine(_root, id), owned);
            bytes = owned.Sum(f => new FileInfo(Path.Combine(_root, id, f)).Length);

            trash = Path.Combine(_root, "~del-" + Guid.NewGuid().ToString("N"));
            Directory.Move(Path.Combine(_root, id), trash);
            _assets.Remove(id);
        }

        var purged = OwnedLibraryPaths.Purge(_root, trash, owned);
        return new(true, !purged, purged ? bytes : 0);
    }

    /// <summary>The stored PNG of one frame (bounded read).</summary>
    public byte[] ReadFrame(StoredAsset asset, int index)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, asset.FrameCount);
        return ReadBounded(Path.Combine(_root, asset.Id, FrameFile(index)), MaxFrameFileBytes);
    }

    private static string FrameFile(int index) => "f" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".png";

    private static byte[] ReadBounded(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length > max)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is larger than {max} bytes");
        }

        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('~'))
            {
                TryDeleteDirectory(dir); // interrupted upload or delete
                continue;
            }

            if (!IsValidId(name))
            {
                continue;
            }

            try
            {
                var meta = JsonSerializer.Deserialize(ReadBounded(Path.Combine(dir, "meta.json"), MaxMetaBytes), ScreensJsonContext.Default.AssetMeta);
                if (meta is null || meta.Id != name || meta.DelaysMs is not { Length: >= 1 } || meta.Width < 1 || meta.Height < 1
                    || string.IsNullOrEmpty(meta.Name)
                    || (meta.Format == "mp4"
                        ? meta.Version != 2 || meta.DelaysMs.Length is < 2 or > Mp4Importer.MaxFrames || meta.DelaysMs.Any(d => d <= 0 || d > 30000) || meta.DelaysMs.Sum(d => (long)d) > 30000
                        : meta.Version != 1 || meta.DelaysMs.Length > 300 || meta.Format is not ("jpeg" or "png" or "gif")))
                {
                    LogSkipped(_log, name, "invalid meta.json");
                    continue;
                }

                long bytes = 0;
                for (var i = 0; i < meta.DelaysMs.Length; i++)
                {
                    bytes += new FileInfo(Path.Combine(dir, FrameFile(i))).Length; // throws if a frame is missing
                }

                _assets[name] = new StoredAsset(name, meta.Name, meta.Format, meta.Width, meta.Height, meta.DelaysMs, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LogSkipped(_log, name, ex.Message);
            }
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            if (Directory.Exists(path) && (name.StartsWith("~tmp-", StringComparison.Ordinal) || name.StartsWith("~del-", StringComparison.Ordinal))
                && Guid.TryParseExact(name[5..], "N", out _))
            {
                var limit = 300;
                var metadata = Path.Combine(path, "meta.json");
                if (File.Exists(metadata))
                {
                    var meta = JsonSerializer.Deserialize(ReadBounded(metadata, MaxMetaBytes), ScreensJsonContext.Default.AssetMeta);
                    if (meta is { Version: 2, Format: "mp4", DelaysMs.Length: >= 2 and <= Mp4Importer.MaxFrames }) limit = meta.DelaysMs.Length;
                }
                var owned = Enumerable.Range(0, limit).Select(FrameFile).Append("meta.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!OwnedLibraryPaths.Purge(_root, path, owned)) LogCleanupFailed(_log, path, "Unknown or locked files kept for inspection.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogCleanupFailed(_log, path, ex.Message);
        }
    }

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen asset {Id} skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string id, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen asset cleanup of {Path} failed: {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string path, string reason);
}
