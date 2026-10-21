namespace UnifiedRgb.Setup;

/// <summary>Resolution of packaged resource paths: plain relative paths that stay inside the installation directory.</summary>
public static class SetupPaths
{
    /// <summary>
    /// Resolves <paramref name="relative"/> against <paramref name="installDirectory"/>. Refuses rooted, drive-relative, UNC or
    /// device paths, <c>..</c> segments, alternate data streams, wildcards and anything that escapes the directory.
    /// </summary>
    public static bool TryResolveInstallRelative(string installDirectory, string relative, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        if (!IsUsableInstallDirectory(installDirectory, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 200)
        {
            error = "The resource path is empty or too long.";
            return false;
        }

        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal) || relative.StartsWith('\\') || relative.StartsWith('/')
            || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || relative.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
        {
            error = $"'{relative}' is not a plain relative path.";
            return false;
        }

        var segments = relative.Split(['\\', '/']);
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.EndsWith('.') || s.EndsWith(' ')))
        {
            error = $"'{relative}' contains an empty, dot or parent segment.";
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{relative}' escapes the installation directory.";
            return false;
        }

        fullPath = full;
        error = string.Empty;
        return true;
    }

    public static bool IsUsableInstallDirectory(string installDirectory, out string error)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Path.IsPathFullyQualified(installDirectory)
            || installDirectory.StartsWith(@"\\", StringComparison.Ordinal) || installDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = $"Installation directory '{installDirectory}' must be a local absolute path.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
