using System.Security.Cryptography;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

public sealed partial class ScenePackageStore
{
    public string DataRoot => Path.GetDirectoryName(Path.GetFullPath(Root))!;
    internal Action? BeforeRemoveCommit { get; set; }

    internal (int Count, long Bytes, long Pending) LibraryUsage()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root)) return (0, 0, 0);
            OwnedLibraryPaths.CheckAncestors(Root);
            long total = 0, pending = 0;
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var size = DirectoryBytes(dir);
                total += size;
                if (Path.GetFileName(dir).StartsWith("~remove-", StringComparison.Ordinal)) pending += size;
            }
            return (EntryIds().Length, total, pending);
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
