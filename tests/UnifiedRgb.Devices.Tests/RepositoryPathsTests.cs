using System.Runtime.CompilerServices;

namespace UnifiedRgb.Devices.Tests;

public sealed class RepositoryPathsTests
{
    [Fact]
    public void Fixtures_belong_to_the_checkout_that_compiled_the_tests_even_when_outputs_are_elsewhere()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(OwnSource())!, "..", ".."));
        RepositoryPaths.Root().Should().Be(expected);
        Fixture.RepoRoot().Should().Be(expected);
        File.Exists(Path.Combine(expected, "tests", "RepositoryPaths.cs")).Should().BeTrue();
    }

    [Fact]
    public void Missing_or_incomplete_source_checkout_never_falls_back_to_an_ancestor_repository()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "urgb-repository-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(temporary, "src"));
            Directory.CreateDirectory(Path.Combine(temporary, "tests"));
            File.WriteAllText(Path.Combine(temporary, "src", "UnifiedRgb.sln"), "ancestor fixture");
            File.WriteAllText(Path.Combine(temporary, "tests", "Directory.Build.props"), "ancestor fixture");
            var source = Path.Combine(temporary, "bringup", "incomplete-checkout", "tests", "Example.Tests", "Example.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "// compiled child checkout fixture");
            ((Action)(() => RepositoryPaths.Root(source))).Should().Throw<DirectoryNotFoundException>().WithMessage("*refusing an ancestor checkout*");
            File.Delete(source);
            ((Action)(() => RepositoryPaths.Root(source))).Should().Throw<DirectoryNotFoundException>().WithMessage("*source is unavailable*");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static string OwnSource([CallerFilePath] string source = "") => source;
}
