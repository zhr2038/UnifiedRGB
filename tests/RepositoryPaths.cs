using System.Runtime.CompilerServices;

namespace UnifiedRgb.Tests;

/// <summary>Fixture provenance follows the compiled test source, independently of the selected binary output directory.</summary>
internal static class RepositoryPaths
{
    internal static string Root([CallerFilePath] string sourceFile = "")
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
            throw new DirectoryNotFoundException("The compiled test source is unavailable; another checkout is not a fixture fallback.");

        var source = Path.GetFullPath(sourceFile);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "src", "UnifiedRgb.sln"))) continue;
            var tests = Path.Combine(directory.FullName, "tests") + Path.DirectorySeparatorChar;
            if (!source.StartsWith(tests, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(directory.FullName, "tests", "Directory.Build.props")))
                throw new DirectoryNotFoundException("The compiled source does not belong to this repository's test tree; refusing an ancestor checkout.");
            return directory.FullName;
        }

        throw new DirectoryNotFoundException("The compiled test source has no owning repository (src/UnifiedRgb.sln).");
    }
}
