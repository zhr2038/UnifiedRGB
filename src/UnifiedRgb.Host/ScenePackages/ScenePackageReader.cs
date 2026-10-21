using System.IO.Compression;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>A package read entirely in memory and verified: manifest bytes, the validated manifest and every declared file.</summary>
/// <param name="ContentId">Lowercase SHA-256 of the exact manifest bytes; the manifest pins every file hash, so this addresses the content.</param>
public sealed record ScenePackageContent(string ContentId, byte[] ManifestBytes, ScenePackageManifest Manifest, IReadOnlyDictionary<string, byte[]> Files);

/// <summary>
/// Reads a v1 package ZIP from memory; nothing is extracted to disk and nothing outside the archive is touched. Rejected
/// (ArgumentException, nothing kept): oversized archive, too many entries, unsafe or colliding names (absolute, drive, UNC,
/// backslash, '..', case collisions, duplicates), symlinks, encrypted or unreadable entries, entries that are not exactly the
/// manifest plus its declared files, any byte count beyond the per-kind or cumulative limits (counted while reading, never
/// trusting the central directory), hash or RGB-length mismatches, and media that is not PNG/JPEG/GIF by signature.
/// Screen media decoding/duration is checked separately by the service through the existing importer.
/// </summary>
public static class ScenePackageReader
{
    private const int SymlinkType = 0xA000;
    private const int FileTypeMask = 0xF000;

    public static ScenePackageContent Read(byte[] zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (zip.Length == 0 || zip.Length > ScenePackageFormat.MaxZipBytes)
        {
            throw ScenePackageFormat.Bad($"a package ZIP is 1..{ScenePackageFormat.MaxZipBytes} bytes (got {zip.Length})");
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read, leaveOpen: false);
            return Read(archive);
        }
        catch (InvalidDataException ex)
        {
            throw ScenePackageFormat.Bad($"not a readable ZIP archive ({ex.Message})");
        }
        catch (NotSupportedException ex)
        {
            throw ScenePackageFormat.Bad($"unsupported ZIP feature ({ex.Message})");
        }
    }

    private static ScenePackageContent Read(ZipArchive archive)
    {
        if (archive.Entries.Count > ScenePackageFormat.MaxEntries)
        {
            throw ScenePackageFormat.Bad($"{archive.Entries.Count} ZIP entries (at most {ScenePackageFormat.MaxEntries})");
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var directories = new List<string>();
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!exact.Add(name))
            {
                throw ScenePackageFormat.Bad($"duplicate ZIP entry '{ScenePackageFormat.Printable(name)}'");
            }

            if (!folded.Add(name))
            {
                throw ScenePackageFormat.Bad($"ZIP entry '{ScenePackageFormat.Printable(name)}' collides with another entry ignoring case");
            }

            if (((entry.ExternalAttributes >> 16) & FileTypeMask) == SymlinkType)
            {
                throw ScenePackageFormat.Bad($"ZIP entry '{ScenePackageFormat.Printable(name)}' is a symbolic link");
            }

            if (name.EndsWith('/'))
            {
                var dir = name[..^1];
                if (ScenePackageFormat.PathProblem(dir) is not null || entry.Length != 0)
                {
                    throw ScenePackageFormat.Bad($"unsafe directory entry '{ScenePackageFormat.Printable(name)}'");
                }

                directories.Add(dir);
                continue;
            }

            if (ScenePackageFormat.PathProblem(name) is { } why)
            {
                throw ScenePackageFormat.Bad($"unsafe ZIP entry: {why}");
            }

            files.Add(name, entry);
        }

        if (!files.TryGetValue(ScenePackageFormat.ManifestName, out var manifestEntry))
        {
            throw ScenePackageFormat.Bad($"no {ScenePackageFormat.ManifestName} at the ZIP root");
        }

        var budget = new Budget();
        var manifestBytes = ReadEntry(manifestEntry, ScenePackageFormat.MaxManifestBytes, budget);
        var manifest = ScenePackageFormat.ParseManifest(manifestBytes);

        foreach (var name in files.Keys)
        {
            if (name != ScenePackageFormat.ManifestName && !manifest.Files.ContainsKey(name))
            {
                throw ScenePackageFormat.Bad($"ZIP entry '{name}' is not declared in files (only the manifest and declared files are allowed)");
            }
        }

        foreach (var dir in directories)
        {
            if (!manifest.Files.Keys.Any(f => f.StartsWith(dir + "/", StringComparison.Ordinal)))
            {
                throw ScenePackageFormat.Bad($"directory entry '{dir}/' contains no declared file");
            }
        }

        var lighting = manifest.Lighting.GroupBy(l => l.File, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var screens = manifest.Screens.Select(s => s.File).ToHashSet(StringComparer.Ordinal);
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, hash) in manifest.Files)
        {
            if (!files.TryGetValue(path, out var entry))
            {
                throw ScenePackageFormat.Bad($"declared file '{path}' is missing from the ZIP");
            }

            long cap = lighting.TryGetValue(path, out var track) ? track.ByteLength
                : screens.Contains(path) ? ScenePackageFormat.MaxScreenFileBytes
                : ScenePackageFormat.MaxInertFileBytes;
            var bytes = ReadEntry(entry, cap, budget);
            if (ScenePackageFormat.Sha256Hex(bytes) != hash)
            {
                throw ScenePackageFormat.Bad($"'{path}' does not match its SHA-256 in files");
            }

            if (track is not null && bytes.LongLength != track.ByteLength)
            {
                throw ScenePackageFormat.Bad($"'{path}' is {bytes.Length} bytes, not {track.Frames} frames × {track.LedCount} LEDs × 3");
            }

            if (screens.Contains(path) && SignatureProblem(bytes, manifest.SchemaVersion == ScenePackageFormat.SchemaVersionVideo
                && Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) is { } bad)
            {
                throw ScenePackageFormat.Bad($"'{path}': {bad}");
            }

            data.Add(path, bytes);
        }

        // lighting tracks sharing one file must agree on its size (checked against each track)
        foreach (var l in manifest.Lighting)
        {
            if (data[l.File].LongLength != l.ByteLength)
            {
                throw ScenePackageFormat.Bad($"lighting '{l.Role}': '{l.File}' is {data[l.File].Length} bytes, not {l.ByteLength}");
            }
        }

        return new ScenePackageContent(ScenePackageFormat.Sha256Hex(manifestBytes), manifestBytes, manifest, data);
    }

    /// <summary>Null when <paramref name="data"/> starts like PNG, JPEG or GIF; anything else (video, executables, scripts) is refused.</summary>
    public static string? SignatureProblem(ReadOnlySpan<byte> data, bool allowMp4 = false)
    {
        if (allowMp4) return data.Length >= 16 && data.Slice(4, 4).SequenceEqual("ftyp"u8) ? null : "MP4 track does not contain an MP4 file";
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.StartsWith(png) || (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            || data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return null;
        }

        return "screen media must be a PNG, JPEG or GIF file";
    }

    /// <summary>Reads at most <paramref name="cap"/> bytes, counting every byte actually produced against the cumulative limit.</summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, long cap, Budget budget)
    {
        if (entry.Length > cap)
        {
            throw ScenePackageFormat.Bad($"'{entry.FullName}' declares {entry.Length} bytes (at most {cap})");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(Math.Max(entry.Length, 0), cap));
        var chunk = new byte[81920];
        long total = 0;
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += n;
            budget.Add(n);
            if (total > cap)
            {
                throw ScenePackageFormat.Bad($"'{entry.FullName}' expands beyond {cap} bytes");
            }

            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }

    private sealed class Budget
    {
        private long _total;

        public void Add(int n)
        {
            _total += n;
            if (_total > ScenePackageFormat.MaxExpandedBytes)
            {
                throw ScenePackageFormat.Bad($"the package expands beyond {ScenePackageFormat.MaxExpandedBytes} bytes");
            }
        }
    }
}
