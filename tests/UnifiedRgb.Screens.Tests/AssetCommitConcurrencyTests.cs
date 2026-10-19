using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class AssetCommitConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_disk_writes_cannot_both_publish_past_the_count_or_byte_budget(bool countLimit)
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-assets-race-" + Guid.NewGuid().ToString("N"));
        var a = await Images.PngAsync(Images.Solid(8, 8, 255, 0, 0));
        var b = await Images.PngAsync(Images.Solid(8, 8, 0, 255, 0));
        var first = await Sta.RunAsync(() => ImageImporter.Import(a, ImportLimits.Default));
        var second = await Sta.RunAsync(() => ImageImporter.Import(b, ImportLimits.Default));
        using var barrier = new Barrier(2);
        var bytes = first.FramePngs.Sum(f => (long)f.Length) + second.FramePngs.Sum(f => (long)f.Length);
        var store = new AssetStore(root, countLimit ? 1 << 20 : bytes - 1, countLimit ? 1 : 5, NullLogger.Instance)
        {
            BeforeCommit = () => barrier.SignalAndWait(TimeSpan.FromSeconds(5)).Should().BeTrue(),
        };
        try
        {
            var outcomes = await Task.WhenAll(
                Task.Run(() => Record.Exception(() => store.Commit(AssetStore.IdOf(a), "A", first))),
                Task.Run(() => Record.Exception(() => store.Commit(AssetStore.IdOf(b), "B", second))));
            outcomes.Count(e => e is null).Should().Be(1);
            outcomes.Count(e => e is InvalidOperationException).Should().Be(1);
            store.List().Should().ContainSingle();
            Directory.GetDirectories(root).Should().ContainSingle();
            Directory.EnumerateDirectories(root, "~*").Should().BeEmpty("the rejected upload's temporary files are removed");
            var reloaded = new AssetStore(root, 1 << 20, 5, NullLogger.Instance);
            reloaded.List().Should().ContainSingle();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
