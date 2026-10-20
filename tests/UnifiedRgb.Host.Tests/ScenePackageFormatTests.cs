using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Tests;

/// <summary>OPUS-GENERIC-SCENE-PACKAGES: ZIP safety, bounds, manifest contract, checksums (no Host, no I/O beyond temp files).</summary>
public sealed class ScenePackageFormatTests
{
    private static Action Reading(byte[] zip) => () => ScenePackageReader.Read(zip);

    private static byte[] Repack(byte[] zip, Func<List<(string Name, byte[] Data)>, IEnumerable<(string, byte[])>> change, Action<ZipArchiveEntry>? tweak = null) =>
        Pkg.Zip(change(Pkg.Entries(zip)), tweak);

    [Fact]
    public void A_valid_package_reads_with_its_content_id_the_hash_of_the_exact_manifest_bytes()
    {
        var zip = Pkg.Lighting();
        var c = ScenePackageReader.Read(zip);
        c.ContentId.Should().Be(Pkg.Sha(Pkg.Entries(zip).Single(e => e.Name == "scene.json").Data));
        c.Manifest.Lighting.Should().ContainSingle().Which.Should().Match<LightingTrack>(l => l.LedCount == 8 && l.Frames == 200 && l.FrameDurationMs == 60);
        c.Manifest.DurationMs.Should().Be(12000);
        c.Files["data/strip.rgb24"].Should().Equal(Pkg.Rgb(200, 8));
    }

    [Fact]
    public void Both_fixtures_read_and_the_generic_one_has_no_theme_dependency()
    {
        var generic = ScenePackageReader.Read(Pkg.GenericFixture());
        generic.ContentId.Should().Be("95d4603bf1dc513d727bcf603eea27f61836979668d845aac67a5cb4ef7d405b");
        generic.Manifest.Roles.Should().Equal("strip", "panel");

        var canonical = ScenePackageReader.Read(Pkg.FullCaseFixture());
        canonical.ContentId.Should().Be("638040f39a763d745ed76bed6e01fb00f31b327d0132fef4c40bcc067edb24c9");
        canonical.Manifest.Lighting.Should().HaveCount(9).And.OnlyContain(l => l.Frames == 200 && l.FrameDurationMs == 60);
        canonical.Manifest.Screens.Should().HaveCount(5).And.OnlyContain(s => s.DurationMs == 12000);
        canonical.Manifest.DurationMs.Should().Be(12000);
    }

    [Theory]
    [InlineData("../evil.rgb24")]
    [InlineData("data/../../evil.rgb24")]
    [InlineData("/abs.rgb24")]
    [InlineData("C:/abs.rgb24")]
    [InlineData("data\\strip.rgb24")]
    [InlineData("data//strip.rgb24")]
    [InlineData("data/./strip.rgb24")]
    [InlineData("data/CON.rgb24")]
    [InlineData("data/strip.rgb24 ")]
    public void Unsafe_entry_paths_are_rejected(string path)
    {
        var zip = Repack(Pkg.Lighting(), es => es.Select(e => e.Name == "data/strip.rgb24" ? (path, e.Data) : (e.Name, e.Data)));
        Reading(zip).Should().Throw<ArgumentException>().WithMessage("Scene package rejected*");
    }

    [Theory]
    [InlineData("data/strip.exe")]
    [InlineData("data/strip.dll")]
    [InlineData("data/strip.ps1")]
    [InlineData("data/strip.bin")]
    [InlineData("data/strip")]
    [InlineData("data/strip.rgb24.exe")]
    public void A_lighting_track_must_be_an_rgb24_file_even_when_the_byte_length_fits(string path)
    {
        // same exact RGB24 byte length, hashed correctly in files{}; only the declared suffix differs
        var files = new Dictionary<string, byte[]> { [path] = Pkg.Rgb(200, 8) };
        var m = Pkg.Manifest(files, [Pkg.LightingTrack("strip", path, 8, 200, 60)], durationMs: 12000);
        var zip = Pkg.Zip([("scene.json", Pkg.Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
        Reading(zip).Should().Throw<ArgumentException>().WithMessage("Scene package rejected*rgb24*");
    }

    [Fact]
    public void An_rgb24_suffix_is_accepted_case_insensitively()
    {
        var files = new Dictionary<string, byte[]> { ["data/strip.RGB24"] = Pkg.Rgb(200, 8) };
        var m = Pkg.Manifest(files, [Pkg.LightingTrack("strip", "data/strip.RGB24", 8, 200, 60)], durationMs: 12000);
        var c = ScenePackageReader.Read(Pkg.Zip([("scene.json", Pkg.Utf8(m)), .. files.Select(f => (f.Key, f.Value))]));
        c.Manifest.Lighting.Should().ContainSingle().Which.File.Should().Be("data/strip.RGB24");
    }

    [Fact]
    public void Duplicate_and_case_colliding_entries_are_rejected()
    {
        var dup = Repack(Pkg.Lighting(), es => [.. es, es.Single(e => e.Name == "data/strip.rgb24")]);
        Reading(dup).Should().Throw<ArgumentException>();
        var folded = Repack(Pkg.Lighting(), es => [.. es, ("DATA/Strip.rgb24", es.Single(e => e.Name == "data/strip.rgb24").Data)]);
        Reading(folded).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Symlink_entries_are_rejected()
    {
        var zip = Repack(Pkg.Lighting(), es => es, e =>
        {
            if (e.FullName == "data/strip.rgb24")
            {
                e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            }
        });
        Reading(zip).Should().Throw<ArgumentException>().WithMessage("*link*");
    }

    [Fact]
    public void Extra_missing_and_tampered_files_are_rejected()
    {
        Reading(Repack(Pkg.Lighting(), es => [.. es, ("data/extra.bin", new byte[] { 1 })])).Should().Throw<ArgumentException>();
        Reading(Repack(Pkg.Lighting(), es => es.Where(e => e.Name != "data/strip.rgb24"))).Should().Throw<ArgumentException>();
        Reading(Repack(Pkg.Lighting(), es => es.Where(e => e.Name != "scene.json"))).Should().Throw<ArgumentException>();
        Reading(Repack(Pkg.Lighting(), es => es.Select(e => e.Name == "data/strip.rgb24" ? (e.Name, Flip(e.Data)) : (e.Name, e.Data))))
            .Should().Throw<ArgumentException>().WithMessage("*SHA-256*");
        Reading(new byte[] { 0x50, 0x4B, 1, 2, 3 }).Should().Throw<ArgumentException>();
        Reading([]).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_RGB_file_whose_length_is_not_frames_by_leds_by_3_is_rejected_even_with_a_matching_hash()
    {
        var files = new Dictionary<string, byte[]> { ["data/strip.rgb24"] = Pkg.Rgb(200, 8)[..^3] };
        var m = Pkg.Manifest(files, [Pkg.LightingTrack("strip", "data/strip.rgb24", 8, 200, 60)]);
        Reading(Pkg.Zip([("scene.json", Pkg.Utf8(m)), ("data/strip.rgb24", files["data/strip.rgb24"])])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_compression_bomb_is_refused_by_the_per_kind_cap_before_it_is_expanded()
    {
        var huge = new byte[(int)ScenePackageFormat.MaxRgbBytes + 3];
        var files = new Dictionary<string, byte[]> { ["data/strip.rgb24"] = huge };
        var m = Pkg.Manifest(files, [Pkg.LightingTrack("strip", "data/strip.rgb24", 8, 200, 60)]);
        var zip = Pkg.Zip([("scene.json", Pkg.Utf8(m)), ("data/strip.rgb24", huge)]);
        zip.Length.Should().BeLessThan(1 << 20, "highly compressible: a real bomb");
        Reading(zip).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("format", "UnifiedRgb.ScenePackage.Draft", "*draft*")]
    [InlineData("format", "Something.Else", "*format*")]
    [InlineData("playbackContract", "rgb24-hold-physical-ms-v1-draft", "*contract*")]
    [InlineData("playbackContract", "rgb24-hold-physical-ms-v2", "*contract*")]
    public void Draft_and_unknown_formats_and_contracts_are_rejected_explicitly(string key, string value, string message)
    {
        Reading(Pkg.Lighting(edit: m => m[key] = value)).Should().Throw<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public void Unknown_schema_versions_properties_and_duplicate_keys_are_rejected()
    {
        Reading(Pkg.Lighting(edit: m => m["schemaVersion"] = 3)).Should().Throw<ArgumentException>().WithMessage("*schemaVersion 3*reads 1 and 2 only*");
        Reading(Pkg.Lighting(edit: m => m["schemaVersion"] = 0)).Should().Throw<ArgumentException>().WithMessage("*schemaVersion*");
        Reading(Pkg.Lighting(edit: m => m["brightness"] = 0.7)).Should().Throw<ArgumentException>().WithMessage("*brightness*");
        Reading(Pkg.Lighting(edit: m => m["lighting"]![0]!["coordinates"] = "x")).Should().Throw<ArgumentException>();
        Reading(Pkg.Lighting(edit: m => m["loop"] = false)).Should().Throw<ArgumentException>();

        var good = Pkg.Entries(Pkg.Lighting());
        var text = Encoding.UTF8.GetString(good.Single(e => e.Name == "scene.json").Data);
        var dupKey = text.Replace("\"revision\":1", "\"revision\":1,\"revision\":2", StringComparison.Ordinal);
        dupKey.Should().NotBe(text);
        Reading(Pkg.Zip(good.Select(e => e.Name == "scene.json" ? (e.Name, Encoding.UTF8.GetBytes(dupKey)) : (e.Name, e.Data))))
            .Should().Throw<ArgumentException>().WithMessage("*duplicate*");
    }

    [Fact]
    public void Inconsistent_timing_and_bounds_are_rejected()
    {
        Reading(Pkg.Lighting(edit: m => m["durationMs"] = 11999)).Should().Throw<ArgumentException>();
        Reading(Pkg.Lighting(edit: m => m["lighting"]![0]!["frameDurationMs"] = 0)).Should().Throw<ArgumentException>();
        Reading(Pkg.Lighting(edit: m => m["lighting"]![0]!["pixelFormat"] = "RGBW32")).Should().Throw<ArgumentException>();
        Reading(Pkg.Lighting(edit: m => m["lighting"]![0]!["ledCount"] = ScenePackageFormat.MaxLedsPerTrack + 1)).Should().Throw<ArgumentException>();
        Reading(Pkg.Lighting(edit: m => m["id"] = "Bad Id!")).Should().Throw<ArgumentException>();
        Reading(Pkg.Lighting(role: "bad role")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_physical_ms_contract_maps_60ms_to_exactly_96_raw_ticks_and_never_quantizes()
    {
        FrameSet.TryRawTicks(60, out var ticks).Should().BeTrue();
        ticks.Should().Be(96);
        (200 * 60 / 1000.0).Should().Be(12, "200 frames × 60 physical ms = 12 s");
        FrameSet.TryRawTicks(12.5, out ticks).Should().BeTrue();
        ticks.Should().Be(20);
        FrameSet.TryRawTicks(61.3, out ticks).Should().BeTrue("98.08 raw ticks fit the header's hundredths");
        ticks.Should().BeApproximately(98.08, 1e-9);
        FrameSet.TryRawTicks(60.001, out _).Should().BeFalse("96.0016 ticks would have to be quantized");
        FrameSet.TryRawTicks(0, out _).Should().BeFalse();
        FrameSet.TryRawTicks(double.NaN, out _).Should().BeFalse();
    }

    private static byte[] Flip(byte[] data)
    {
        var copy = (byte[])data.Clone();
        copy[^1] ^= 0xFF;
        return copy;
    }
}

/// <summary>Content-addressed store and immutable binding snapshots (temp directory, no Host).</summary>
public sealed class ScenePackageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-pkg-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private (ScenePackageStore Store, ScenePackageContent Content, byte[] Zip) Installed()
    {
        var zip = Pkg.Lighting();
        var store = new ScenePackageStore(_dir);
        var content = ScenePackageReader.Read(zip);
        store.Install(content, zip, []).Created.Should().BeTrue();
        return (store, content, zip);
    }

    [Fact]
    public void Install_is_idempotent_and_reloads_in_a_new_store_instance()
    {
        var (store, content, zip) = Installed();
        var again = store.Install(content, zip, []);
        again.Created.Should().BeFalse();
        again.Package.ContentId.Should().Be(content.ContentId);

        var reloaded = new ScenePackageStore(_dir);
        reloaded.TryGet(content.ContentId, out var p, out var problem).Should().BeTrue(problem);
        p.Lighting["data/strip.rgb24"].Should().Equal(content.Files["data/strip.rgb24"]);
        p.ZipSha256.Should().Be(Pkg.Sha(zip));
        reloaded.List().Packages.Should().ContainSingle();
        Directory.GetDirectories(reloaded.Root).Should().ContainSingle();
    }

    [Fact]
    public void A_corrupted_entry_is_reported_never_played_and_a_reinstall_moves_it_aside()
    {
        var (_, content, zip) = Installed();
        var file = Directory.GetFiles(Path.Combine(_dir, ScenePackageStore.DirectoryName), "strip.rgb24", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(file);
        bytes[0] ^= 1;
        File.WriteAllBytes(file, bytes);

        var fresh = new ScenePackageStore(_dir);
        fresh.TryGet(content.ContentId, out _, out var problem).Should().BeFalse();
        problem.Should().Contain("corrupt");
        fresh.List().Problems.Should().ContainKey(content.ContentId);

        fresh.Install(content, zip, []).Created.Should().BeTrue();
        fresh.TryGet(content.ContentId, out var repaired, out _).Should().BeTrue();
        repaired.Lighting["data/strip.rgb24"].Should().Equal(content.Files["data/strip.rgb24"]);
        Directory.GetDirectories(fresh.Root, "~corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public void A_missing_entry_and_a_bad_id_are_reported()
    {
        var (_, content, _) = Installed();
        Directory.Delete(Path.Combine(_dir, ScenePackageStore.DirectoryName, content.ContentId), recursive: true);
        var fresh = new ScenePackageStore(_dir);
        fresh.TryGet(content.ContentId, out _, out var problem).Should().BeFalse();
        problem.Should().Contain("not installed");
        fresh.TryGet("../x", out _, out problem).Should().BeFalse();
        problem.Should().Contain("not a package content id");
    }

    [Fact]
    public void A_failed_install_leaves_no_entry_and_staging_debris_is_swept()
    {
        var zip = Pkg.Lighting();
        var content = ScenePackageReader.Read(zip);
        var store = new ScenePackageStore(_dir) { BeforeCommit = _ => throw new IOException("disk full (test)") };
        FluentActions.Invoking(() => store.Install(content, zip, [])).Should().Throw<IOException>();
        Directory.Exists(Path.Combine(store.Root, content.ContentId)).Should().BeFalse();
        Directory.GetDirectories(store.Root, "~stage-*").Should().BeEmpty();

        var stage = Path.Combine(store.Root, "~stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "package.zip"), "partial upload");
        var unknown = Path.Combine(store.Root, "~stage-debris");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "owner-note.txt"), "keep me");
        var fresh = new ScenePackageStore(_dir);
        fresh.TryGet(content.ContentId, out _, out _).Should().BeFalse();
        Directory.Exists(stage).Should().BeFalse("only an owned staging name and known files may be swept");
        File.ReadAllText(Path.Combine(unknown, "owner-note.txt")).Should().Be("keep me");
    }

    [Fact]
    public void Binding_snapshots_are_immutable_content_addressed_and_tamper_evident()
    {
        var (store, content, _) = Installed();
        var bindings = new ScenePackageBindingStore(store);
        BindingEntry[] a = [new("strip", BindingEntry.Lighting, "pkg:1", 0, null)];
        BindingEntry[] b = [new("strip", BindingEntry.Lighting, "pkg:2", 0, null)];

        var first = bindings.Save(content.ContentId, a);
        bindings.Save(content.ContentId, a).BindingId.Should().Be(first.BindingId, "same bindings → same snapshot");
        var second = bindings.Save(content.ContentId, b);
        second.BindingId.Should().NotBe(first.BindingId);

        var fresh = new ScenePackageBindingStore(new ScenePackageStore(_dir));
        fresh.Current(content.ContentId, out _)!.BindingId.Should().Be(second.BindingId);
        fresh.TryLoad(content.ContentId, first.BindingId, out var old, out _).Should().BeTrue("a profile pinned to the old snapshot still resolves it");
        old.Bindings.Should().Equal(a);

        var path = Path.Combine(fresh.Root, content.ContentId, first.BindingId + ".json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("pkg:1", "pkg:9", StringComparison.Ordinal));
        new ScenePackageBindingStore(new ScenePackageStore(_dir)).TryLoad(content.ContentId, first.BindingId, out _, out var problem).Should().BeFalse();
        problem.Should().Contain("corrupt");
    }
}
