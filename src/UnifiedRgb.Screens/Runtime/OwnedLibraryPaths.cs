namespace UnifiedRgb.Screens.Runtime;

/// <summary>Small owned-library file operations: no links, no unknown files and no recursive directory delete.</summary>
public static class OwnedLibraryPaths
{
    public static void CheckAncestors(string path)
    {
        path = Path.GetFullPath(path);
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Library operations refuse links or reparse paths: " + current);
    }

    public static IReadOnlyList<string> Inventory(string root, string directory, IReadOnlySet<string> allowed)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Owned directory escaped its library root.");
        CheckAncestors(directory);
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(directory); var entries = 0;
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if (++entries > 4096) throw new IOException("Owned library entry exceeds its cleanup budget.");
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A library entry is a link: " + entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var prefix = Path.GetRelativePath(directory, entry).Replace('\\', '/') + "/";
                    if (!allowed.Contains(prefix) && !allowed.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Unrecognized directory kept in the library: " + prefix);
                    pending.Push(entry);
                }
                else
                {
                    var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                    if (!allowed.Contains(relative)) throw new IOException("Unrecognized file kept in the library: " + relative);
                    files.Add(relative);
                }
            }
        }
        return files;
    }

    public static bool Purge(string root, string directory, IReadOnlySet<string> allowed, string? keepUntilLast = null)
    {
        try
        {
            var files = Inventory(root, directory, allowed);
            foreach (var relative in files.OrderBy(f => f == keepUntilLast)) File.Delete(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count > 0)
            {
                var current = pending.Pop(); CheckAncestors(current); directories.Add(current);
                foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
                if (directories.Count > 4096) throw new IOException("Cleanup directory budget exceeded.");
            }
            foreach (var child in directories.OrderByDescending(p => p.Length)) Directory.Delete(child, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
