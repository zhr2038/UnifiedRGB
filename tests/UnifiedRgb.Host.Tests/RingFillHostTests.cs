using System.Text.Json;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Preview;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// WP-14 (H2-VERTICAL-FILL §3/§5/§6): mounting.json schema 3 and the schema-2 migration of the owner's rev-15 file, the
/// preview's truthful vertical fill, and the mounting editor's ring fields.
/// </summary>
public sealed class RingFillHostTests : IDisposable
{
    /// <summary>The owner's validated rev-15 mounting.json (schema 2), verbatim.</summary>
    private const string OwnerRev15 = """
        {
          "schemaVersion": 2,
          "devices": [
            { "controllerId": "lianli:rf:1B-7B-F1-72-AB-3C", "rotationDegrees": 0, "reverse": false, "fans": [], "family": null, "units": 0,
              "revision": 0, "description": null, "overridden": false, "note": null, "reverseChain": true },
            { "controllerId": "lianli:rf:35-F7-EF-72-AB-3C", "rotationDegrees": 0, "reverse": false, "fans": [], "family": null, "units": 0,
              "revision": 0, "description": null, "overridden": false, "note": null, "reverseChain": false },
            { "controllerId": "lianli:rf:EF-7E-63-62-32-E1", "rotationDegrees": 0, "reverse": true, "fans": [], "family": null, "units": 0,
              "revision": 0, "description": null, "overridden": false, "note": null, "reverseChain": false }
          ]
        }
        """;

    private const string Tl120 = "lianli:rf:1B-7B-F1-72-AB-3C";
    private const string Lcd = "lianli:rf:35-F7-EF-72-AB-3C";
    private const string Block = "lianli:rf:EF-7E-63-62-32-E1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-wp14", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, MountingStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Owner_rev15_schema2_file_loads_unchanged_and_is_not_rewritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, OwnerRev15);
        var before = File.ReadAllBytes(FilePath);
        var loaded = new MountingStore(FilePath).Load();
        File.ReadAllBytes(FilePath).Should().Equal(before, "loading never migrates the file on disk");
        loaded.Keys.Should().BeEquivalentTo(Tl120, Lcd, Block);

        // every entry is semantically what WP-13 read: same flags, ring fields at their identity defaults
        (loaded[Tl120].Reverse, loaded[Tl120].ReverseChain, loaded[Tl120].RotationDegrees).Should().Be((false, true, 0));
        (loaded[Lcd].Reverse, loaded[Lcd].ReverseChain).Should().Be((false, false));
        (loaded[Block].Reverse, loaded[Block].ReverseChain).Should().Be((true, false));
        loaded.Values.Should().OnlyContain(d => d.RingOffsetLeds == 0 && !d.HalfStep && !d.FaceOnFill && d.Fans.Count == 0);

        // and they build byte-identical wire maps and the same descriptions as before WP-14
        var tl = FamilyDetector.ByFamily("TL")!;
        var h2 = FamilyDetector.ByFamily("H2")!;
        MountingMap.TryBuild(tl, 78, LightingEngine.FromDto(loaded[Tl120]), out var tlMap, out _).Should().BeTrue();
        tlMap.Should().BeNull();
        MountingMap.TryBuild(h2, 24, LightingEngine.FromDto(loaded[Block]), out var h2Map, out _).Should().BeTrue();
        h2Map.Should().Equal(Enumerable.Range(0, 24).Select(i => (24 - i) % 24));
        MountingMap.Describe(LightingEngine.FromDto(loaded[Tl120])).Should().Be("fan order reversed");
        MountingMap.Describe(LightingEngine.FromDto(loaded[Block])).Should().Be("0° mirrored");
        MountingMap.Describe(LightingEngine.FromDto(loaded[Lcd])).Should().Be("identity");
    }

    [Fact]
    public void Saving_one_device_upgrades_to_schema_3_and_keeps_the_other_entries_semantically_identical()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, OwnerRev15);
        var store = new MountingStore(FilePath);
        var old = store.Load();
        store.Save(Block, new MountingDto(Block, 90, true, [], RingOffsetLeds: -2, HalfStep: true, FaceOnFill: true));

        var text = File.ReadAllText(FilePath);
        text.Should().MatchRegex("\"schemaVersion\":\\s*3").And.MatchRegex("\"ringOffsetLeds\":\\s*-2").And.MatchRegex("\"faceOnFill\":\\s*true");
        var again = new MountingStore(FilePath).Load();
        again[Tl120].Should().BeEquivalentTo(old[Tl120]);
        again[Lcd].Should().BeEquivalentTo(old[Lcd]);
        (again[Block].RotationDegrees, again[Block].Reverse, again[Block].RingOffsetLeds, again[Block].HalfStep, again[Block].FaceOnFill)
            .Should().Be((90, true, -2, true, true));

        var m = LightingEngine.FromDto(again[Block]);
        (m.RingOffsetLeds, m.HalfStep, m.FaceOnFill).Should().Be((-2, true, true), "the engine sees the calibration");
    }

    [Fact]
    public void Ring_calibration_dto_and_zone_fields_round_trip_over_json()
    {
        var o = ContractsJson.Configure(new JsonSerializerOptions());
        var c = new RingCalibrationDto(Block, RingCalibrationDto.Wedge, 90, -1, true);
        JsonSerializer.Deserialize<RingCalibrationDto>(JsonSerializer.Serialize(c, o), o).Should().Be(c);
        var z = new ZoneDto(0, "H2", "Ring", 24, 0, 0, "H2", 1, false, 2, true, true);
        JsonSerializer.Deserialize<ZoneDto>(JsonSerializer.Serialize(z, o), o).Should().Be(z);
        var u = new LoopUploadDto(DateTimeOffset.UnixEpoch, 1, 1, 20, 72, 10, 1, 1, 0, "x", Geometry: "vertical-fill offset=+2 half=false bottom=High");
        JsonSerializer.Deserialize<LoopUploadDto>(JsonSerializer.Serialize(u, o), o)!.Geometry.Should().Be(u.Geometry);
        LightingEngine.ToDto(new LoopUploadStats(DateTimeOffset.UnixEpoch, 0, 1, 1, 20, 72, 10, 1, TimeSpan.Zero, 0, "x") { Geometry = "ring" })!
            .Geometry.Should().Be("ring");
    }

    private static ZoneDto[] FourZones(bool fill, bool half = false) =>
    [
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3, true),
        new ZoneDto(0, "H2", "Ring", 24, 0, 0, "H2", 1, false, 0, half, fill),
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
    ];

    [Fact]
    public void Preview_vendor_plan_carries_the_fill_like_the_host()
    {
        var gen = EffectRegistry.Default["Stacking"];
        var p = new EffectParams([new Rgb(255, 0, 0)], 0.5f);
        var preview = PreviewRenderer.VendorPlanFor(gen, p, FourZones(fill: true, half: true), Core.Engine.LoopPolicy.LConnect)!;
        var host = LConnectLoops.TryPlan("Stacking", p, p.Colors,
            [new("TL", 3, 78), new("TL", 3, 78, true), new("H2", 1, 24, false, true, true), new("TL", 3, 78)])!;
        preview.Zones.Select(z => z.Rgb).Should().BeEquivalentTo(host.Zones.Select(z => z.Rgb), o => o.WithStrictOrdering());
        preview.Variant.Should().Contain("; H2 vertical-fill (bottom=High, half-step, interpolated)");

        var renderer = new PreviewRenderer(gen, FourZones(true).Select(PreviewRenderer.ToLayout).ToArray(), p, vendor: preview, zones: FourZones(true, true));
        renderer.Faces.Should().Equal(null, null, new RingFace(true), null);
        new PreviewRenderer(gen, FourZones(false).Select(PreviewRenderer.ToLayout).ToArray(), p, zones: FourZones(false)).Faces.Should().OnlyContain(f => f == null);
    }

    [Fact]
    public void Generic_preview_places_the_h2_by_height_only_for_translational_effects()
    {
        var p = new EffectParams([new Rgb(255, 0, 0), new Rgb(0, 255, 0)], 0.5f);
        var layouts = FourZones(true).Select(PreviewRenderer.ToLayout).ToArray();
        var runway = new PreviewRenderer(EffectRegistry.Default["Runway"], layouts, p, zones: FourZones(true));
        runway.VerticalFill.Should().BeTrue();
        var linear = layouts.ToArray();
        linear[2] = ZoneLayout.Linear(24);
        var reference = new PreviewRenderer(EffectRegistry.Default["Runway"], linear, p);
        foreach (var t in new[] { 0.0, 0.4, 1.3 })
        {
            var flat = reference.Render(t).AsSpan(156, 24).ToArray();
            var placed = runway.Render(t).AsSpan(156, 24).ToArray();
            for (var i = 0; i < 24; i++)
            {
                placed[i].Should().Be(flat[RingFillMap.Dots(false)[i]], $"t {t} LED {i}");
            }
        }

        new PreviewRenderer(EffectRegistry.Default["Rainbow"], layouts, p, zones: FourZones(true)).VerticalFill.Should().BeFalse("rotational stays a ring");
        new PreviewRenderer(EffectRegistry.Default["Runway"], [layouts[2]], p, zones: [FourZones(true)[2]]).VerticalFill.Should().BeFalse("a single block keeps its ring");
    }

    [Fact]
    public async Task Mounting_editor_round_trips_the_ring_fields_and_offers_calibration_only_for_h2()
    {
        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var dto = new MountingDto(Block, 90, true, [], "H2", 1, RingOffsetLeds: 2, HalfStep: true, FaceOnFill: true);
        var item = new MountingItem(dto, client, InlineDispatcher.Instance, _ => { });
        item.IsRing.Should().BeTrue();
        item.IsCalibrating.Should().BeFalse();
        (item.RingOffsetLeds, item.HalfStep, item.FaceOnFill).Should().Be((2, true, true));
        var built = item.Build();
        (built.RotationDegrees, built.Reverse, built.RingOffsetLeds, built.HalfStep, built.FaceOnFill).Should().Be((90, true, 2, true, true),
            "a plain Apply keeps the calibration");
        item.FaceOnFill = false;
        item.Build().FaceOnFill.Should().BeFalse("vertical fill can be turned off");
        item.CalibrationContext.Should().Contain("Mirror: on");

        var tl = new MountingItem(new MountingDto(Tl120, 0, false, [], "TL", 3, ReverseChain: true), client, InlineDispatcher.Instance, _ => { });
        tl.IsRing.Should().BeFalse();
        tl.RingOffsetLeds = 3;
        (tl.Build().RingOffsetLeds, tl.Build().FaceOnFill).Should().Be((0, false), "TL chains never send ring fields");
    }
}
