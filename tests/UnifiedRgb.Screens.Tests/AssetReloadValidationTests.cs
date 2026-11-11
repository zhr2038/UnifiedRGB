using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class AssetReloadValidationTests
{
    [Theory]
    [InlineData("gif", "[2147483647,2147483647]")]
    [InlineData("gif", "[-100,100]")]
    [InlineData("gif", "[0,100]")]
    [InlineData("gif", "[20000,20000]")]
    [InlineData("png", "[100,100]")]
    [InlineData("jpeg", "[100]")]
    public void Invalid_cached_timing_is_skipped_without_breaking_valid_assets(string format, string delaysJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-asset-reload-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteMetadata(root, "0123456789abcdef", format, JsonSerializer.Deserialize<int[]>(delaysJson)!);
            WriteMetadata(root, "fedcba9876543210", "png", [0]);

            var store = new AssetStore(root, 1 << 20, 10, NullLogger.Instance);
            store.List().Select(a => a.ToDto()).Should().ContainSingle().Which.Id.Should().Be("fedcba9876543210");
            store.Find("0123456789abcdef").Should().BeNull();
            Directory.Exists(Path.Combine(root, "0123456789abcdef")).Should().BeTrue("a damaged source remains available for inspection");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("png", "[0]", 0)]
    [InlineData("jpeg", "[0]", 0)]
    [InlineData("gif", "[0]", 0)]
    [InlineData("gif", "[20,100]", 120)]
    [InlineData("mp4", "[16,17]", 33)]
    public void Valid_normalized_timing_survives_reload(string format, string delaysJson, int duration)
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-asset-reload-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteMetadata(root, "0123456789abcdef", format, JsonSerializer.Deserialize<int[]>(delaysJson)!);

            var asset = new AssetStore(root, 1 << 20, 10, NullLogger.Instance).List().Should().ContainSingle().Subject;
            asset.DurationMs.Should().Be(duration);
            asset.FrameAt(duration).Should().Be(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void WriteMetadata(string root, string id, string format, int[] delays)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var meta = new AssetMeta(format == "mp4" ? 2 : 1, id, "cached media", format, 8, 8, delays);
        File.WriteAllBytes(Path.Combine(directory, "meta.json"), JsonSerializer.SerializeToUtf8Bytes(meta, ScreensJsonContext.Default.AssetMeta));
        for (var i = 0; i < delays.Length; i++) File.WriteAllBytes(Path.Combine(directory, $"f{i:D3}.png"), [1]);
    }
}
