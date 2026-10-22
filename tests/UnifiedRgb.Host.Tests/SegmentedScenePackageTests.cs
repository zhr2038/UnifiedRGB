using System.Text.Json.Nodes;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Tests;

public sealed class SegmentedScenePackageTests
{
    internal static byte[] Package(int schema = 4, byte[]? media = null, Action<JsonObject>? edit = null, int clipCount = 2)
    {
        var bytes = media ?? [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 105, 115, 111, 109, 0, 0, 0, 0];
        var files = new Dictionary<string, byte[]> { ["media/a.mp4"] = bytes, ["media/b.mp4"] = bytes };
        var track = Pkg.ScreenTrack("panel", "media/a.mp4", clipCount * 2000); track.Remove("file"); track["fps"] = 30;
        track["segments"] = new JsonArray(Enumerable.Range(0, clipCount).Select(i => (JsonNode)new JsonObject { ["file"] = i % 2 == 0 ? "media/a.mp4" : "media/b.mp4", ["durationMs"] = 2000 }).ToArray());
        var manifest = Pkg.Manifest(files, [], [track], clipCount * 2000); manifest["schemaVersion"] = schema; edit?.Invoke(track);
        return Pkg.Zip([("scene.json", Pkg.Utf8(manifest)), .. files.Select(f => (f.Key, f.Value))]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Legacy_schemas_never_reinterpret_segment_lists(int schema)
        => FluentActions.Invoking(() => ScenePackageReader.Read(Package(schema))).Should().Throw<ArgumentException>();

    [Fact]
    public void V4_keeps_one_screen_track_and_pins_every_original_segment()
    {
        var zip = Package(); var content = ScenePackageReader.Read(zip); var track = content.Manifest.Screens.Single();
        content.Manifest.SchemaVersion.Should().Be(4); track.IsSequence.Should().BeTrue();
        track.MediaFiles.Should().Equal("media/a.mp4", "media/b.mp4"); track.DurationMs.Should().Be(4000);
        foreach (var file in track.MediaFiles) Pkg.Sha(content.Files[file]).Should().Be(content.Manifest.Files[file]);
        ScenePackageReader.Read(zip).ContentId.Should().Be(content.ContentId);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("short")]
    [InlineData("fractional")]
    [InlineData("too-long")]
    [InlineData("retime")]
    [InlineData("non-video")]
    public void Segment_contract_rejects_ambiguous_retimed_or_out_of_budget_media(string error)
    {
        var zip = Package(edit: track =>
        {
            var clips = (JsonArray)track["segments"]!;
            switch (error)
            {
                case "mixed": track["file"] = "media/a.mp4"; break;
                case "short": clips.RemoveAt(1); break;
                case "fractional": clips[0]!["durationMs"] = 2000.5; break;
                case "too-long": clips[0]!["durationMs"] = 30001; break;
                case "retime": clips[0]!["durationMs"] = 1999; break;
                case "non-video": clips[0]!["file"] = "media/a.png"; break;
            }
        });
        FluentActions.Invoking(() => ScenePackageReader.Read(zip)).Should().Throw<ArgumentException>();
    }
}
