using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>Decoded facts of one screen track, recorded at import by the existing importer (thumbnail kept beside it).</summary>
public sealed record ScenePackageMediaRecord(string Role, string Format, int Width, int Height, int Frames, int DurationMs);

/// <summary>
/// One installed, verified package. <see cref="Lighting"/> holds the authored RGB24 bytes by file path (immutable; never
/// rescaled, resampled or regenerated). Screen media is re-read and re-verified on demand through <see cref="ReadFile"/>.
/// </summary>
public sealed class InstalledScenePackage
{
    internal InstalledScenePackage(string contentId, string directory, ScenePackageManifest manifest, IReadOnlyDictionary<string, byte[]> lighting,
        IReadOnlyList<ScenePackageMediaRecord> media, string zipSha256)
    {
        ContentId = contentId;
        Directory = directory;
        Manifest = manifest;
        Lighting = lighting;
        Media = media;
        ZipSha256 = zipSha256;
    }

    public string ContentId { get; }

    public string Directory { get; }

    public ScenePackageManifest Manifest { get; }

    public IReadOnlyDictionary<string, byte[]> Lighting { get; }

    public IReadOnlyList<ScenePackageMediaRecord> Media { get; }

    /// <summary>SHA-256 of the original package ZIP kept as package.zip (the first import of this content).</summary>
    public string ZipSha256 { get; }

    /// <summary>A declared file, read from the store and verified against its manifest hash (InvalidDataException otherwise).</summary>
    public byte[] ReadFile(string path)
    {
        if (!Manifest.Files.TryGetValue(path, out var hash))
        {
            throw new ArgumentException($"'{path}' is not a file of package {ContentId}.", nameof(path));
        }

        var bytes = File.ReadAllBytes(ScenePackageStore.FilePath(Directory, path));
        return ScenePackageFormat.Sha256Hex(bytes) == hash
            ? bytes
            : throw new InvalidDataException($"package {ContentId}: '{path}' no longer matches its SHA-256");
    }

    /// <summary>The stored first-frame thumbnail of a screen role, or null (cache data, not authored content).</summary>
    public byte[]? Thumbnail(string role)
    {
        var path = Path.Combine(Directory, ScenePackageStore.ThumbsDir, role + ".png");
        try
        {
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            return bytes is { Length: > 0 and <= 1024 * 1024 } ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The ProgramData "scene-packages" store: <c>&lt;contentId&gt;/{package.zip, scene.json, files/…, thumbs/…, record.json}</c>.
/// Content-addressed and idempotent (same manifest bytes → same entry, never overwritten); a new entry is fully written in a
/// <c>~stage-*</c> directory, verified and then renamed into place. Each process verifies every entry it serves (manifest
/// hash = directory name, strict parse, every file hash/length, original ZIP hash) before first use: persisted metadata never
/// skips verification. A corrupt entry is reported (and renamed aside on reinstall), never served. There is no delete API.
/// </summary>
public sealed partial class ScenePackageStore
{
    public const string DirectoryName = "scene-packages";
    public const string ZipName = "package.zip";
    public const string FilesDir = "files";
    public const string ThumbsDir = "thumbs";
    public const string RecordName = "record.json";
    public const int MaxPackages = 64;
    public const long MaxStoreBytes = 1024L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, InstalledScenePackage> _verified = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _corrupt = new(StringComparer.Ordinal);
    private readonly ILogger? _log;
    private bool _swept;

    public ScenePackageStore(string dataDirectory, ILogger<ScenePackageStore>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        Root = Path.Combine(dataDirectory, DirectoryName);
        _log = log;
    }

    public ScenePackageStore(UnifiedRgbOptions options, ILogger<ScenePackageStore>? log = null)
        : this((options ?? throw new ArgumentNullException(nameof(options))).ResolvedDataDirectory, log)
    {
    }

    public string Root { get; }

    /// <summary>Test hook: runs after the staged entry is complete and before it is renamed into place.</summary>
    internal Action<string>? BeforeCommit { get; set; }

    internal static string FilePath(string packageDirectory, string relative)
    {
        var files = Path.GetFullPath(Path.Combine(packageDirectory, FilesDir)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(files, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(files, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new InvalidDataException($"package path '{relative}' escapes the package directory");
    }

    /// <summary>Every entry that verifies (sorted by name/revision), plus the reasons others do not.</summary>
    public (IReadOnlyList<InstalledScenePackage> Packages, IReadOnlyDictionary<string, string> Problems) List()
    {
        var list = new List<InstalledScenePackage>();
        foreach (var id in EntryIds())
        {
            if (TryGet(id, out var p, out _))
            {
                list.Add(p);
            }
        }

        return (list.OrderBy(p => p.Manifest.Name, StringComparer.Ordinal).ThenBy(p => p.Manifest.Revision).ThenBy(p => p.ContentId, StringComparer.Ordinal).ToArray(),
            new Dictionary<string, string>(_corrupt, StringComparer.Ordinal));
    }

    /// <summary>The verified entry, or false and why (unknown id, missing or corrupt entry).</summary>
    public bool TryGet(string contentId, out InstalledScenePackage package, out string? problem)
    {
        package = null!;
        if (!ScenePackageFormat.IsSha256Hex(contentId))
        {
            problem = $"'{ScenePackageFormat.Printable(contentId)}' is not a package content id";
            return false;
        }

        if (_verified.TryGetValue(contentId, out var cached))
        {
            package = cached;
            problem = null;
            return true;
        }

        lock (_gate)
        {
            Sweep();
            if (_verified.TryGetValue(contentId, out cached))
            {
                package = cached;
                problem = null;
                return true;
            }

            var dir = Path.Combine(Root, contentId);
            if (!Directory.Exists(dir))
            {
                problem = $"scene package {contentId} is not installed";
                return false;
            }

            try
            {
                var verified = Verify(contentId, dir);
                _verified[contentId] = verified;
                _corrupt.TryRemove(contentId, out _);
                package = verified;
                problem = null;
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                problem = $"scene package {contentId} is corrupt ({ex.Message})";
                _corrupt[contentId] = problem;
                LogCorrupt(contentId, ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Installs <paramref name="content"/> (already fully validated, media included) with the original <paramref name="zip"/>.
    /// Idempotent: an entry that verifies is returned unchanged (Created false). A corrupt entry of the same id is renamed aside,
    /// never overwritten. Store bounds are checked first; IOException/InvalidOperationException leave no partial entry.
    /// </summary>
    public (InstalledScenePackage Package, bool Created) Install(ScenePackageContent content, byte[] zip, IReadOnlyList<(ScenePackageMediaRecord Media, byte[] Thumbnail)> media)
    {
        ArgumentNullException.ThrowIfNull(zip);
        using var stream = new MemoryStream(zip, writable: false);
        return Install(content, stream, null, media);
    }

    /// <summary>
    /// As the byte-array overload, copying the original ZIP from a seekable stream (read from position 0, never written) into the
    /// staged entry while hashing it, so no second in-memory copy of the archive is made. When <paramref name="expectedZipSha256"/>
    /// is given, the copied bytes must hash to it (InvalidDataException otherwise, nothing installed): the stored package.zip is
    /// exactly the bytes that were validated.
    /// </summary>
    public (InstalledScenePackage Package, bool Created) Install(ScenePackageContent content, Stream zip, string? expectedZipSha256,
        IReadOnlyList<(ScenePackageMediaRecord Media, byte[] Thumbnail)> media)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(media);
        if (!zip.CanRead || !zip.CanSeek)
        {
            throw new ArgumentException("A package ZIP stream must be readable and seekable.", nameof(zip));
        }

        if (expectedZipSha256 is not null && !ScenePackageFormat.IsSha256Hex(expectedZipSha256))
        {
            throw new ArgumentException("An expected ZIP hash is a lowercase SHA-256 hex string.", nameof(expectedZipSha256));
        }

        var zipLength = zip.Length;
        if (zipLength <= 0 || zipLength > ScenePackageFormat.MaxZipBytes)
        {
            throw ScenePackageFormat.Bad($"a package ZIP is 1..{ScenePackageFormat.MaxZipBytes} bytes (got {zipLength})");
        }

        lock (_gate)
        {
            if (TryGet(content.ContentId, out var existing, out _))
            {
                return (existing, false);
            }

            var final = Path.Combine(Root, content.ContentId);
            if (Directory.Exists(final))
            {
                var aside = Path.Combine(Root, $"~corrupt-{content.ContentId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
                Directory.Move(final, aside);
                Invalidate(content.ContentId, Path.GetFileName(aside));
                LogMovedAside(content.ContentId, aside);
            }

            var adding = zipLength + content.ManifestBytes.LongLength + content.Files.Values.Sum(f => f.LongLength) + media.Sum(m => (long)m.Thumbnail.Length);
            var (count, bytes) = Usage();
            if (count >= MaxPackages || bytes + adding > MaxStoreBytes)
            {
                throw new InvalidOperationException(
                    $"The scene-package store is full ({count} of {MaxPackages} packages, {bytes + adding} of {MaxStoreBytes} bytes); nothing was installed.");
            }

            var stage = Path.Combine(Root, $"~stage-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stage);
                var zipSha256 = CopyZip(zip, Path.Combine(stage, ZipName), zipLength);
                if (expectedZipSha256 is not null && zipSha256 != expectedZipSha256)
                {
                    throw new InvalidDataException("The package ZIP changed before it was stored; nothing was installed.");
                }

                Write(Path.Combine(stage, ScenePackageFormat.ManifestName), content.ManifestBytes);
                foreach (var (path, data) in content.Files)
                {
                    var target = FilePath(stage, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Write(target, data);
                }

                Directory.CreateDirectory(Path.Combine(stage, ThumbsDir));
                foreach (var (m, thumb) in media)
                {
                    Write(Path.Combine(stage, ThumbsDir, m.Role + ".png"), thumb);
                }

                var record = new StoreRecord(1, content.ContentId, zipSha256, zipLength, DateTimeOffset.UtcNow,
                    media.Select(m => m.Media).ToArray());
                Write(Path.Combine(stage, RecordName), JsonSerializer.SerializeToUtf8Bytes(record, StoreJsonContext.Default.StoreRecord));
                var staged = Verify(content.ContentId, stage); // what is renamed into place has been read back and verified
                BeforeCommit?.Invoke(stage);
                Directory.Move(stage, final);
                Invalidate(content.ContentId);
                var installed = new InstalledScenePackage(content.ContentId, final, staged.Manifest, staged.Lighting, staged.Media, staged.ZipSha256);
                _verified[content.ContentId] = installed;
                _corrupt.TryRemove(content.ContentId, out _);
                return (installed, true);
            }
            finally
            {
                TryDelete(stage);
            }
        }
    }

    /// <summary>
    /// True when screen asset <paramref name="assetId"/> (AssetStore.IdOf = first 16 hex of the upload SHA-256) is media of any
    /// installed entry. Conservative: read from each entry's manifest alone, so even an entry failing verification keeps its
    /// assets protected from generic deletion.
    /// </summary>
    public bool PinsAsset(string assetId)
    {
        if (assetId is not { Length: 16 })
        {
            return false;
        }

        foreach (var id in EntryIds())
        {
            try
            {
                var path = Path.Combine(Root, id, ScenePackageFormat.ManifestName);
                UnifiedRgb.Screens.Runtime.OwnedLibraryPaths.CheckAncestors(path);
                if (new FileInfo(path).Length > ScenePackageFormat.MaxManifestBytes) return true;
                var manifest = _verified.TryGetValue(id, out var p)
                    ? p.Manifest
                    : ScenePackageFormat.ParseManifest(File.ReadAllBytes(path));
                if (manifest.Screens.Any(s => manifest.Files[s.File].StartsWith(assetId, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return true; // unreadable references refuse cache reclamation instead of guessing that no package needs it
            }
        }

        return false;
    }

    private static InstalledScenePackage Verify(string contentId, string dir)
    {
        var manifestBytes = File.ReadAllBytes(Path.Combine(dir, ScenePackageFormat.ManifestName));
        if (ScenePackageFormat.Sha256Hex(manifestBytes) != contentId)
        {
            throw new InvalidDataException("scene.json does not hash to the content id");
        }

        var manifest = ScenePackageFormat.ParseManifest(manifestBytes);
        var record = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, RecordName)), StoreJsonContext.Default.StoreRecord)
            ?? throw new InvalidDataException("record.json is empty");
        if (record.Schema != 1 || record.ContentId != contentId || !ScenePackageFormat.IsSha256Hex(record.ZipSha256))
        {
            throw new InvalidDataException("record.json does not describe this entry");
        }

        // streamed: the original archive (up to 64 MiB) and non-lighting files are hashed without being held in memory
        var zipPath = Path.Combine(dir, ZipName);
        if (record.ZipBytes is <= 0 or > ScenePackageFormat.MaxZipBytes || new FileInfo(zipPath).Length != record.ZipBytes
            || HashFile(zipPath, ScenePackageFormat.MaxZipBytes) != record.ZipSha256)
        {
            throw new InvalidDataException("package.zip does not match its recorded SHA-256");
        }

        var lighting = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var lightingFiles = manifest.Lighting.Select(l => l.File).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, hash) in manifest.Files)
        {
            if (!lightingFiles.Contains(path))
            {
                if (HashFile(FilePath(dir, path), ScenePackageFormat.MaxExpandedBytes) != hash)
                {
                    throw new InvalidDataException($"'{path}' does not match its SHA-256");
                }

                continue;
            }

            var bytes = File.ReadAllBytes(FilePath(dir, path));
            if (ScenePackageFormat.Sha256Hex(bytes) != hash)
            {
                throw new InvalidDataException($"'{path}' does not match its SHA-256");
            }

            lighting[path] = bytes;
        }

        foreach (var l in manifest.Lighting)
        {
            if (lighting[l.File].LongLength != l.ByteLength)
            {
                throw new InvalidDataException($"'{l.File}' is not {l.ByteLength} bytes");
            }
        }

        var media = record.Media ?? [];
        if (media.Count != manifest.Screens.Count || manifest.Screens.Where((s, i) => media[i].Role != s.Role).Any())
        {
            throw new InvalidDataException("record.json media does not match the screen tracks");
        }

        return new InstalledScenePackage(contentId, dir, manifest, lighting, media, record.ZipSha256);
    }

    private string[] EntryIds()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Where(ScenePackageFormat.IsSha256Hex).Select(n => n!).ToArray();
    }

    /// <summary>
    /// Admission always rechecks the bounded on-disk tree. The UI usage cache is advisory: out-of-band changes must never
    /// allow an install past the hard quota or hide a newly introduced link. This work never runs on a render thread.
    /// </summary>
    private (int Count, long Bytes) Usage()
    {
        var usage = MeasureUsage(full: true);
        return (usage.Count, usage.Bytes);
    }

    /// <summary>Copies the whole stream into a new file and returns its SHA-256; the stream must yield exactly <paramref name="length"/> bytes.</summary>
    private static string CopyZip(Stream source, string path, long length)
    {
        source.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long total = 0;
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > length)
                {
                    throw new InvalidDataException("The package ZIP grew while it was stored; nothing was installed.");
                }

                hash.AppendData(buffer, 0, n);
                fs.Write(buffer, 0, n);
            }

            if (total != length)
            {
                throw new InvalidDataException("The package ZIP ended early while it was stored; nothing was installed.");
            }

            fs.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Once per process: removes staging debris of an interrupted install (never a committed or corrupt entry).</summary>
    private void Sweep()
    {
        if (_swept)
        {
            return;
        }

        _swept = true;
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var debris in Directory.EnumerateDirectories(Root).Where(p => Path.GetFileName(p).StartsWith("~stage-", StringComparison.Ordinal)
                     || Path.GetFileName(p).StartsWith("~remove-", StringComparison.Ordinal)))
        {
            TryDelete(debris);
        }
    }

    private static void Write(string path, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                var name = Path.GetFileName(dir);
                if (!(name.StartsWith("~stage-", StringComparison.Ordinal) && Guid.TryParseExact(name[7..], "N", out _))
                    && !(name.Length == 105 && name.StartsWith("~remove-", StringComparison.Ordinal)
                        && ScenePackageFormat.IsSha256Hex(name.Substring(8, 64)) && name[72] == '-'
                        && Guid.TryParseExact(name[73..], "N", out _))) return;
                UnifiedRgb.Screens.Runtime.OwnedLibraryPaths.CheckAncestors(dir);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ZipName, RecordName, ScenePackageFormat.ManifestName, FilesDir + "/", ThumbsDir + "/" };
                var manifestPath = Path.Combine(dir, ScenePackageFormat.ManifestName);
                if (File.Exists(manifestPath))
                {
                    if (new FileInfo(manifestPath).Length > ScenePackageFormat.MaxManifestBytes) return;
                    var manifest = ScenePackageFormat.ParseManifest(File.ReadAllBytes(manifestPath));
                    foreach (var file in manifest.Files.Keys) names.Add(FilesDir + "/" + file);
                    foreach (var screen in manifest.Screens) names.Add(ThumbsDir + "/" + screen.Role + ".png");
                }
                Invalidate(name);
                if (!UnifiedRgb.Screens.Runtime.OwnedLibraryPaths.Purge(Root, dir, names, ScenePackageFormat.ManifestName)) LogDebris(dir, "Unknown or locked files kept for inspection.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            LogDebris(dir, ex.Message);
        }
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Error, Message = "Scene package {ContentId} failed verification: {Reason}")]
    private static partial void LogCorrupt(ILogger logger, string contentId, string reason);

    private void LogCorrupt(string contentId, string reason)
    {
        if (_log is not null)
        {
            LogCorrupt(_log, contentId, reason);
        }
    }

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "Corrupt scene package {ContentId} moved aside to {Path} before reinstalling")]
    private static partial void LogMovedAside(ILogger logger, string contentId, string path);

    private void LogMovedAside(string contentId, string path)
    {
        if (_log is not null)
        {
            LogMovedAside(_log, contentId, path);
        }
    }

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "Scene package staging debris {Path} could not be removed: {Reason}")]
    private static partial void LogDebris(ILogger logger, string path, string reason);

    private void LogDebris(string path, string reason)
    {
        if (_log is not null)
        {
            LogDebris(_log, path, reason);
        }
    }
}

internal sealed record StoreRecord(int Schema, string ContentId, string ZipSha256, long ZipBytes, DateTimeOffset InstalledAt, IReadOnlyList<ScenePackageMediaRecord>? Media);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StoreRecord))]
[JsonSerializable(typeof(BindingSnapshotFile))]
[JsonSerializable(typeof(BindingPointerFile))]
internal sealed partial class StoreJsonContext : JsonSerializerContext
{
}
