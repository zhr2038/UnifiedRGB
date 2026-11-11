using System.Security.Cryptography;
using System.Text.Json;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The UnifiedRgb assemblies this test process actually loaded (path and SHA-256), for pinning a focused run: they must come from
/// the test output directory; the list is written only when URGB_LOADED_COHORT names an output file.
/// </summary>
public sealed class LoadedCohortProbe
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void Loaded_unifiedrgb_assemblies_come_from_the_test_output()
    {
        _ = typeof(UnifiedRgb.Host.Services.KeyboardDisplayService).Assembly;
        _ = typeof(UnifiedRgb.Screens.Keyboard.KeyboardOledFrames).Assembly;
        _ = typeof(UnifiedRgb.Contracts.Dto.KeyboardDisplayAnimationStatusDto).Assembly;
        var output = Path.GetFullPath(AppContext.BaseDirectory);
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.GetName().Name is { } name && name.StartsWith("UnifiedRgb", StringComparison.Ordinal))
            .Select(a => new { a.GetName().Name, a.Location, SHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant() })
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
        loaded.Select(a => a.Name).Should().Contain(["UnifiedRgb.Host", "UnifiedRgb.Screens", "UnifiedRgb.Contracts"]);
        loaded.Should().OnlyContain(a => string.Equals(Path.GetDirectoryName(a.Location) + Path.DirectorySeparatorChar, output, StringComparison.OrdinalIgnoreCase));
        if (Environment.GetEnvironmentVariable("URGB_LOADED_COHORT") is { Length: > 0 } path)
            File.WriteAllText(path, JsonSerializer.Serialize(new { Output = output, Loaded = loaded }, Indented));
    }
}
