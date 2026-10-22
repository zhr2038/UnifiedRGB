using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class SegmentedVideoTests
{
    [MediaFoundationFact]
    public async Task Null_clip_metadata_is_ignored_without_breaking_other_stored_assets()
    {
        var root = Root();
        try
        {
            await Sta.RunAsync(() =>
            {
                var bytes = Fixture(); var probe = SegmentedVideo.Probe(bytes, root, CancellationToken.None);
                var path = Path.Combine(root, "assets"); var store = new AssetStore(path, 1 << 24, 8, NullLogger.Instance);
                var asset = store.CommitSequence("corrupt clip metadata", [new(bytes, 2000), new(bytes, 2000)], [probe, probe]);
                var good = store.CommitSequence("valid neighbour", [new(bytes, 2000), new(bytes, 2000), new(bytes, 2000)], [probe, probe, probe]);
                var metadata = Path.Combine(path, asset.Id, "sequence.json");
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadata))!;
                node["clips"]![0] = null;
                File.WriteAllText(metadata, node.ToJsonString());
                var reloaded = new AssetStore(path, 1 << 24, 8, NullLogger.Instance);
                reloaded.Find(asset.Id).Should().BeNull();
                reloaded.Find(good.Id).Should().NotBeNull();
                return true;
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Five_STA_sequence_readers_cross_boundaries_and_wrap_without_sharing_decoders()
    {
        var root = Root();
        try
        {
            var bytes = Fixture("h264-wide-initial-format.mp4");
            var owners = await Task.WhenAll(Enumerable.Range(0, 5).Select(worker => Sta.RunAsync(() =>
            {
                var probe = SegmentedVideo.Probe(bytes, Path.Combine(root, "probe-" + worker), CancellationToken.None);
                var store = new AssetStore(Path.Combine(root, "assets-" + worker), 1 << 24, 4, NullLogger.Instance);
                var asset = store.CommitSequence("five readers", [new(bytes, 1000), new(bytes, 1000)], [probe, probe]);
                using var reader = new SequenceFrameReader(store);
                for (var i = 0; i < 120; i++)
                {
                    var pixels = reader.Read(asset, i % asset.FrameCount);
                    (pixels.Width, pixels.Height).Should().Be((1920, 480));
                }
                return Environment.CurrentManagedThreadId;
            })));
            owners.Distinct().Should().HaveCount(5);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Wide_bt709_source_preserves_declared_canvas_through_initial_decoder_format_notification()
    {
        await Sta.RunAsync(() =>
        {
            var facts = SegmentedVideo.Probe(Fixture("h264-wide-initial-format.mp4"), null, CancellationToken.None);
            (facts.Width, facts.Height, facts.DurationMs, facts.DelaysMs.Length).Should().Be((1920, 480, 1000, 24));
            return true;
        });
    }

    private static byte[] Fixture(string name = "h264-30.mp4") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static string Root() => Path.Combine(Path.GetTempPath(), "urgb-segment-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Sequence_identity_pins_order_and_duration_and_refuses_invalid_hashes()
    {
        var a = new string('a', 64); var b = new string('b', 64);
        var first = ScreenVideoSegment.SequenceId([(a, 2000), (b, 2000)]);
        first.Should().NotBe(ScreenVideoSegment.SequenceId([(b, 2000), (a, 2000)]));
        first.Should().NotBe(ScreenVideoSegment.SequenceId([(a, 2001), (b, 1999)]));
        FluentActions.Invoking(() => ScreenVideoSegment.SequenceId([("invalid", 2000), (b, 2000)])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScreenVideoSegment.SequenceId([(a, 30001), (b, 2000)])).Should().Throw<ArgumentException>();
    }

    [MediaFoundationFact]
    public async Task Streaming_sequence_matches_every_selected_legacy_picture_across_boundary_seek_and_wrap()
    {
        var root = Root();
        try
        {
            await Sta.RunAsync(() =>
            {
                var bytes = Fixture();
                var legacy = Mp4Importer.Import(bytes, ImportLimits.Default);
                var probe = SegmentedVideo.Probe(bytes, root, CancellationToken.None);
                probe.DelaysMs.Should().Equal(legacy.DelaysMs);
                var store = new AssetStore(Path.Combine(root, "assets"), 1 << 24, 10, NullLogger.Instance);
                var asset = store.CommitSequence("sequence", [new(bytes, 2000), new(bytes, 2000)], [probe, probe]);
                (asset.FrameCount, asset.DurationMs, asset.Kind).Should().Be((120, 4000, "video"));
                using var decoder = new SequenceFrameReader(store);
                foreach (var elapsed in new long[] { 0, 34, 667, 1999, 2000, 2034, 3999, 4000, 1900, 100 })
                {
                    var frame = asset.FrameAt(elapsed);
                    decoder.Read(asset, frame).Pixels.Should().Equal(WpfCodec.DecodePng(legacy.FramePngs[frame % 60], 4096).Pixels, "elapsed {0}ms", elapsed);
                }
                Directory.GetFiles(Path.Combine(store.Root, asset.Id)).Select(Path.GetFileName).Should().BeEquivalentTo("sequence.json", "c000.mp4", "c001.mp4");
                asset.Bytes.Should().BeLessThan(legacy.FramePngs.Sum(p => (long)p.Length) * 2, "original clips replace eager raster storage");
                return true;
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Sequence_originals_and_timing_survive_reload_and_can_be_removed_after_decoder_disposal()
    {
        var root = Root();
        try
        {
            await Sta.RunAsync(() =>
            {
                var bytes = Fixture(); var probe = SegmentedVideo.Probe(bytes, root, CancellationToken.None);
                var path = Path.Combine(root, "assets");
                var store = new AssetStore(path, 1 << 24, 10, NullLogger.Instance);
                var asset = store.CommitSequence("sequence", [new(bytes, 2000), new(bytes, 2000)], [probe, probe]);
                var reloaded = new AssetStore(path, 1 << 24, 10, NullLogger.Instance);
                var restored = reloaded.Find(asset.Id)!;
                restored.DurationMs.Should().Be(4000); restored.FrameAt(2000).Should().Be(60);
                using (var reader = new SequenceFrameReader(reloaded)) reader.Read(restored, 70).Width.Should().Be(64);
                File.ReadAllBytes(Path.Combine(path, asset.Id, "c001.mp4")).Should().Equal(bytes);
                reloaded.Delete(asset.Id).Should().BeTrue();
                Directory.Exists(Path.Combine(path, asset.Id)).Should().BeFalse();
                return true;
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Corrupt_original_blocks_sequence_output_instead_of_showing_cached_or_substitute_background()
    {
        var root = Root();
        try
        {
            await Sta.RunAsync(() =>
            {
                var bytes = Fixture(); var probe = SegmentedVideo.Probe(bytes, root, CancellationToken.None);
                var store = new AssetStore(Path.Combine(root, "assets"), 1 << 24, 10, NullLogger.Instance);
                var asset = store.CommitSequence("sequence", [new(bytes, 2000), new(bytes, 2000)], [probe, probe]);
                File.WriteAllBytes(Path.Combine(store.Root, asset.Id, "c000.mp4"), new byte[bytes.Length]);
                using var compositor = new ScreenCompositor(store, 1 << 20);
                var result = compositor.Compose(Devices.H2.Family, new(Devices.H2.Id, asset.Id, "#FF0000", "none", TimelineId: "strict"), 0, null, DateTimeOffset.UtcNow);
                result.Warning.Should().StartWith(ScreenCompositor.BlockedPrefix);
                result.Canvas.Pixels.Where((_, i) => i % 4 != 3).Should().OnlyContain(v => v == 0);
                return true;
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Sequence_probe_cancel_retiming_and_unsupported_geometry_publish_nothing()
    {
        await using var rig = new Rig(); var bytes = Fixture();
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await FluentActions.Awaiting(() => rig.Manager.ProbeSequenceAsync([new(bytes, 2000), new(bytes, 2000)], cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => rig.Manager.InstallPackageSequenceAsync("bad duration", [new(bytes, 1999), new(bytes, 2000)])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.InstallPackageSequenceAsync("bad orientation", [new(Fixture("h264-rotated.mp4"), 2000), new(bytes, 2000)])).Should().ThrowAsync<ArgumentException>();
        (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty();
    }

    [MediaFoundationFact]
    public async Task Sequence_store_enforces_bytes_at_atomic_publication_and_preserves_foreign_files()
    {
        var root = Root();
        try
        {
            await Sta.RunAsync(() =>
            {
                var bytes = Fixture(); var probe = SegmentedVideo.Probe(bytes, root, CancellationToken.None);
                var small = new AssetStore(Path.Combine(root, "small"), bytes.Length * 2L - 1, 10, NullLogger.Instance);
                FluentActions.Invoking(() => small.CommitSequence("sequence", [new(bytes, 2000), new(bytes, 2000)], [probe, probe])).Should().Throw<InvalidOperationException>();
                small.List().Should().BeEmpty();
                var store = new AssetStore(Path.Combine(root, "assets"), 1 << 24, 10, NullLogger.Instance);
                var asset = store.CommitSequence("sequence", [new(bytes, 2000), new(bytes, 2000)], [probe, probe]);
                var foreign = Path.Combine(store.Root, asset.Id, "owner-note.txt"); File.WriteAllText(foreign, "keep");
                FluentActions.Invoking(() => store.Delete(asset.Id)).Should().Throw<IOException>();
                File.ReadAllText(foreign).Should().Be("keep"); store.Find(asset.Id).Should().NotBeNull();
                return true;
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
