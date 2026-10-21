using System.Security.Cryptography;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

public sealed partial class ScenePackageStore
{
    public string DataRoot => Path.GetDirectoryName(Path.GetFullPath(Root))!;
    internal Action? BeforeRemoveCommit { get; set; }

    // Usage cache. Only directories this store never changes after creating them are cached: committed content entries (named by
    // their content id) and quarantined ~corrupt-* entries. Every other top-level directory (bindings, ~remove-*, ~stage-*,
    // anything unknown) is measured on every call, the listing itself and the package count are always fresh, and each cached
    // directory is still refused when it has become a link. Every store path that moves or deletes a cached name invalidates it
    // first (install, move-aside, detach, debris removal). Changes made from outside the Host inside a cached entry are not
    // observed until the next full walk: forced every UsageRefreshInterval and before EVERY install quota admission.
    private Dictionary<string, long>? _usage;
    private readonly HashSet<string> _usageStale = new(StringComparer.OrdinalIgnoreCase);
    private long _usageWalkedAt;

    /// <summary>Longest age of the cached entry sizes before the next usage read walks the whole tree again.</summary>
    internal TimeSpan UsageRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Completed walks of the whole tree (tests).</summary>
    internal int FullUsageWalks { get; private set; }

    /// <summary>Directories measured file by file, cached or not (tests).</summary>
    internal int MeasuredDirectories { get; private set; }

    internal (int Count, long Bytes, long Pending) LibraryUsage()
    {
        var usage = MeasureUsage(full: false);
        return (usage.Count, usage.Bytes, usage.Pending);
    }

    private (int Count, long Bytes, long Pending, bool Full) MeasureUsage(bool full)
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root))
            {
                _usage = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                _usageStale.Clear();
                _usageWalkedAt = Environment.TickCount64;
                return (0, 0, 0, true);
            }

            OwnedLibraryPaths.CheckAncestors(Root);
            full |= _usage is null || Environment.TickCount64 - _usageWalkedAt >= (long)UsageRefreshInterval.TotalMilliseconds;
            var next = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long total = 0, pending = 0;
            var count = 0;
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                var cacheable = ScenePackageFormat.IsSha256Hex(name) || name.StartsWith("~corrupt-", StringComparison.Ordinal);
                long size;
                if (!full && cacheable && !_usageStale.Contains(name) && _usage!.TryGetValue(name, out var known))
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Library size inspection refuses a link: " + dir);
                    size = known;
                }
                else
                {
                    size = DirectoryBytes(dir);
                    MeasuredDirectories++;
                }

                if (cacheable) next[name] = size;
                total += size;
                if (name.StartsWith("~remove-", StringComparison.Ordinal)) pending += size;
                if (ScenePackageFormat.IsSha256Hex(name)) count++;
            }

            _usage = next; // only a complete measurement replaces the cache; a refused link or I/O error keeps the old one and its stale marks
            _usageStale.Clear();
            if (full)
            {
                _usageWalkedAt = Environment.TickCount64;
                FullUsageWalks++;
            }

            return (count, total, pending, full);
        }
    }

    /// <summary>Marks top-level names whose cached size may no longer be true (called before or right after the store moves them).</summary>
    private void Invalidate(params string[] names)
    {
        lock (_gate)
        {
            foreach (var name in names) _usageStale.Add(name);
        }
    }

    internal long InstalledBytes(InstalledScenePackage package)
    {
        var files = OwnedLibraryPaths.Inventory(Root, package.Directory, OwnedFiles(package));
        return files.Sum(f => new FileInfo(Path.Combine(package.Directory, f.Replace('/', Path.DirectorySeparatorChar))).Length);
    }

    internal (string Trash, long Bytes, IReadOnlySet<string> Owned) Detach(InstalledScenePackage package)
    {
        lock (_gate)
        {
            var owned = OwnedFiles(package);
            var files = OwnedLibraryPaths.Inventory(Root, package.Directory, owned);
            if (HashFile(Path.Combine(package.Directory, ScenePackageFormat.ManifestName), ScenePackageFormat.MaxManifestBytes) != package.ContentId
                || HashFile(Path.Combine(package.Directory, ZipName), ScenePackageFormat.MaxZipBytes) != package.ZipSha256)
                throw new InvalidDataException("The immutable package changed; no package files were removed.");
            foreach (var (path, hash) in package.Manifest.Files)
                if (HashFile(FilePath(package.Directory, path), ScenePackageFormat.MaxExpandedBytes) != hash)
                    throw new InvalidDataException("An authored package file changed; no package files were removed: " + path);
            var bytes = files.Sum(f => new FileInfo(Path.Combine(package.Directory, f.Replace('/', Path.DirectorySeparatorChar))).Length);
            var trash = Path.Combine(Root, "~remove-" + package.ContentId + "-" + Guid.NewGuid().ToString("N"));
            BeforeRemoveCommit?.Invoke();
            Invalidate(package.ContentId, Path.GetFileName(trash));
            Directory.Move(package.Directory, trash); // only after all reference/ownership checks; no authored bytes are rewritten
            _verified.TryRemove(package.ContentId, out _); _corrupt.TryRemove(package.ContentId, out _);
            return (trash, bytes, owned);
        }
    }

    internal static HashSet<string> OwnedFiles(InstalledScenePackage package)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ZipName, ScenePackageFormat.ManifestName, RecordName, FilesDir + "/", ThumbsDir + "/" };
        foreach (var file in package.Manifest.Files.Keys) names.Add(FilesDir + "/" + file);
        foreach (var screen in package.Manifest.Screens) names.Add(ThumbsDir + "/" + screen.Role + ".png");
        return names;
    }

    private static string HashFile(string path, long limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("Package file exceeds its verification budget: " + path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static long DirectoryBytes(string directory)
    {
        OwnedLibraryPaths.CheckAncestors(directory);
        long bytes = 0; var count = 0; var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > 262144) throw new IOException("Library size inspection exceeded its file budget.");
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("Library size inspection refuses a link: " + entry);
                if ((attr & FileAttributes.Directory) != 0) pending.Push(entry); else bytes += new FileInfo(entry).Length;
            }
        return bytes;
    }
}
