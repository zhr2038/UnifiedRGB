using System.Text;
using System.Text.Json.Nodes;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Scene package schemaVersion 2: the optional screen accentColor and overlayLayout (a telemetry region). schemaVersion 1 keeps
/// every byte, meaning and rejection (including the new fields as explicit null); v2 values are strict; a region binds only to a
/// rectangular screen and survives apply, profile save/restore and restart together with the local rotation. No theme content.
/// </summary>
public sealed class ScenePackageRegionFormatTests
{
    private static Action Reading(byte[] zip) => () => ScenePackageReader.Read(zip);

    private static byte[] V1Screen(Action<JsonObject> screen)
    {
        var files = new Dictionary<string, byte[]> { ["media/panel.png"] = Pkg.Png() };
        var track = Pkg.ScreenTrack("panel", "media/panel.png");
        screen(track);
        var m = Pkg.Manifest(files, [], [track]);
        return Pkg.Zip([("scene.json", Pkg.Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
    }

    [Theory]
    [InlineData("accentColor", "\"#FAD61D\"")]
    [InlineData("accentColor", "null")]
    [InlineData("overlayLayout", "{\"x\":0.5,\"y\":0.16,\"width\":0.48,\"height\":0.74,\"columns\":2,\"backgroundOpacity\":0}")]
    [InlineData("overlayLayout", "null")]
    public void Schema_1_still_rejects_either_new_screen_field_even_as_null(string key, string json)
    {
        var zip = V1Screen(t => t[key] = JsonNode.Parse(json));
        Reading(zip).Should().Throw<ArgumentException>().WithMessage($"*unknown property '{key}' (v1 defines no such field)*");
    }

    [Fact]
    public void Legacy_v1_fixtures_read_byte_exact_with_no_accent_or_region()
    {
        var generic = Pkg.GenericFixture();
        var canonical = Pkg.FullCaseFixture();
        foreach (var zip in new[] { generic, canonical })
        {
            var c = ScenePackageReader.Read(zip);
            c.Manifest.SchemaVersion.Should().Be(1);
            c.Manifest.Screens.Should().OnlyContain(s => s.AccentColor == null && s.OverlayLayout == null);
        }

        ScenePackageReader.Read(canonical).ContentId.Should().Be("638040f39a763d745ed76bed6e01fb00f31b327d0132fef4c40bcc067edb24c9", "v1 content ids never change");
    }

    [Fact]
    public void The_authoring_tool_v2_sample_imports_with_its_region()
    {
        // tools/scene-packages/pack_scene.py sample --schema 2 + build (generic, no theme content)
        using var fixture = typeof(ScenePackageRegionFormatTests).Assembly.GetManifestResourceStream("GenericRegionFixtureV2.zip")!;
        using var fixtureBytes = new MemoryStream();
        fixture.CopyTo(fixtureBytes);
        var zip = fixtureBytes.ToArray();
        var c = ScenePackageReader.Read(zip);
        c.ContentId.Should().Be("314530a8cfff110da58a9624606868d4575ba677ba6f3609ca5c1467796c3116");
        c.Manifest.SchemaVersion.Should().Be(2);
        var s = c.Manifest.Screens.Single();
        (s.Overlay, s.AccentColor, s.OverlayLayout).Should().Be(("dashboard", "#FAD61D", new ScreenOverlayLayoutDto(0.5, 0.16, 0.48, 0.74, 2, 0)));
    }

    [Fact]
    public void A_schema_2_region_reads_exactly_and_v2_without_a_region_keeps_the_old_meaning()
    {
        var c = ScenePackageReader.Read(Pkg.RegionScreenOnly(Pkg.Png()));
        c.Manifest.SchemaVersion.Should().Be(2);
        var s = c.Manifest.Screens.Single();
        s.Overlay.Should().Be("dashboard");
        s.AccentColor.Should().Be("#FAD61D");
        s.OverlayLayout.Should().Be(new ScreenOverlayLayoutDto(0.50, 0.16, 0.48, 0.74, 2, 0.0));

        var plain = ScenePackageReader.Read(Pkg.RegionScreenOnly(Pkg.Png(), t =>
        {
            t.Remove("overlayLayout");
            t.Remove("accentColor");
        })).Manifest.Screens.Single();
        (plain.AccentColor, plain.OverlayLayout).Should().Be((null, null));

        var accentOnly = ScenePackageReader.Read(Pkg.RegionScreenOnly(Pkg.Png(), t =>
        {
            t.Remove("overlayLayout");
            t["overlay"] = "compact";
        })).Manifest.Screens.Single();
        (accentOnly.Overlay, accentOnly.AccentColor, accentOnly.OverlayLayout).Should().Be(("compact", "#FAD61D", null));

        ScenePackageReader.Read(Pkg.RegionScreenOnly(Pkg.Png(), t => t["overlayLayout"] = Pkg.Region(0, 0, 1, 1, 4, 1))).Manifest.Screens.Single()
            .OverlayLayout.Should().Be(new ScreenOverlayLayoutDto(0, 0, 1, 1, 4, 1), "the full canvas and full opacity are inclusive bounds");
    }

    [Theory]
    [InlineData("x", "1e400", "*finite*")]
    [InlineData("y", "-1e400", "*finite*")]
    [InlineData("x", "-0.01", "*x and y*")]
    [InlineData("y", "1.5", "*x and y*")]
    [InlineData("width", "0", "*width and height*")]
    [InlineData("height", "-0.2", "*width and height*")]
    [InlineData("width", "0.6", "*inside the canvas*")]
    [InlineData("height", "0.9", "*inside the canvas*")]
    [InlineData("columns", "0", "*columns*")]
    [InlineData("columns", "5", "*columns*")]
    [InlineData("columns", "2.0", "*integer*")]
    [InlineData("columns", "2e0", "*integer*")]
    [InlineData("columns", "4294967298", "*integer*")]
    [InlineData("columns", "\"2\"", "*integer*")]
    [InlineData("backgroundOpacity", "1.0001", "*backgroundOpacity*")]
    [InlineData("backgroundOpacity", "-0.1", "*backgroundOpacity*")]
    [InlineData("backgroundOpacity", "null", "*finite*")]
    [InlineData("X", "0.5", "*unknown property 'X' (v2*")]
    [InlineData("padding", "0.1", "*unknown property 'padding' (v2*")]
    public void Bad_region_values_and_keys_are_rejected(string key, string json, string message)
    {
        var zip = Pkg.RegionScreenOnly(Pkg.Png(), t => t["overlayLayout"]![key] = JsonNode.Parse(json));
        Reading(zip).Should().Throw<ArgumentException>().WithMessage("Scene package rejected*" + message);
    }

    [Fact]
    public void Missing_region_keys_colours_and_overlay_conflicts_are_rejected()
    {
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["overlayLayout"]!.AsObject().Remove("columns"))).Should().Throw<ArgumentException>().WithMessage("*'columns' is required*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["overlayLayout"] = null)).Should().Throw<ArgumentException>().WithMessage("*JSON object*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["overlayLayout"] = new JsonArray())).Should().Throw<ArgumentException>().WithMessage("*JSON object*");
        foreach (var colour in new[] { "#FAD61", "FAD61D", "#GGGGGG", "#FAD61DFF", "#FAD 1D", "" })
        {
            Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["accentColor"] = colour)).Should().Throw<ArgumentException>().WithMessage("*accentColor must be #RRGGBB*");
        }

        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["accentColor"] = null)).Should().Throw<ArgumentException>().WithMessage("*'accentColor' must be a string*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["overlay"] = "none")).Should().Throw<ArgumentException>().WithMessage("*overlay is none*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t =>
        {
            t["overlay"] = "none";
            t.Remove("overlayLayout");
        })).Should().Throw<ArgumentException>().WithMessage("*accentColor needs a compact or dashboard overlay*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t =>
        {
            t["overlay"] = "compact";
            t.Remove("accentColor");
        })).Should().Throw<ArgumentException>().WithMessage("*overlayLayout needs overlay dashboard*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), t => t["rotation"] = 90)).Should().Throw<ArgumentException>().WithMessage("*unknown property 'rotation' (v2*");
        Reading(Pkg.RegionScreenOnly(Pkg.Png(), edit: m => m["brightness"] = 1)).Should().Throw<ArgumentException>().WithMessage("*unknown property 'brightness' (v2*");
    }

    [Fact]
    public void The_two_versions_are_distinct_in_details_and_messages()
    {
        var v2 = Encoding.UTF8.GetString(Pkg.Entries(Pkg.RegionScreenOnly(Pkg.Png())).Single(e => e.Name == "scene.json").Data);
        v2.Should().Contain("\"schemaVersion\":2");
        Reading(Pkg.Lighting(edit: m => m["schemaVersion"] = 4)).Should().Throw<ArgumentException>().WithMessage("*unsupported schemaVersion 4 (this program reads 1, 2 and 3 only)*");
        Reading(Pkg.Lighting(edit: m => m["format"] = "UnifiedRgb.ScenePackage.Draft")).Should().Throw<ArgumentException>().WithMessage("*v1 or v2 package*");
    }
}

/// <summary>Schema 2 regions through the real Host (full DI, fake screens incl. a rectangular case screen, fake loop adapter).</summary>
public sealed partial class ScenePackageHostTests
{
    private static readonly ScreenOverlayLayoutDto RegionRight = new(0.50, 0.16, 0.48, 0.74, 2, 0.0);

    private static ScreenAssignmentDto CaseOf(Rig rig) =>
        rig.Host.Get<ScreenManager>().CurrentForScene()!.Assignments.Single(a => a.ScreenId == FakeScreens.Case.Id);

    /// <summary>A v2 package with the RGB strip and the region panel (for the strict wire-budget refusal).</summary>
    private static byte[] RegionMixed()
    {
        var files = new Dictionary<string, byte[]> { ["data/strip.rgb24"] = Pkg.Rgb(200, 8), ["media/panel.png"] = Pkg.Png() };
        var track = Pkg.ScreenTrack("panel", "media/panel.png");
        track["overlay"] = "dashboard";
        track["accentColor"] = "#FAD61D";
        track["overlayLayout"] = Pkg.Region(0.02, 0.10, 0.40, 0.80, 1, 0.3);
        var m = Pkg.Manifest(files, [Pkg.LightingTrack("strip", "data/strip.rgb24", 8, 200, 60)], [track]);
        m["schemaVersion"] = 2;
        return Pkg.Zip([("scene.json", Pkg.Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
    }

    [Fact]
    public async Task A_region_bound_to_a_round_screen_is_never_ready_or_applied()
    {
        await using var rig = await StartAsync(true, null, new FakeScreens { AcceptOpens = true, CasePresent = true });
        var before = rig.Host.Get<ScreenManager>().CurrentForScene();
        var details = await ImportAsync(rig.Packages, Pkg.RegionScreenOnly(Pkg.Png()));
        details.Info.SchemaVersion.Should().Be(2);

        foreach (var round in new[] { FakeScreens.H2.Id, FakeScreens.Tl.Id })
        {
            var state = await rig.Packages.SaveScenePackageBindingsAsync(Bind(details.Info.ContentId, new ScenePackageBindingDto("panel", ScreenId: round)));
            state.Ready.Should().BeFalse();
            state.BindingId.Should().BeNull("a refused binding is never saved");
            state.Problems.Should().Contain(p => p.Contains("rectangular screens only", StringComparison.Ordinal));
        }

        var apply = await rig.Packages.ApplyScenePackageAsync(details.Info.ContentId);
        apply.Applied.Should().BeFalse();
        rig.Host.Get<ScreenManager>().CurrentForScene().Should().BeEquivalentTo(before);

        var v1 = (await ImportAsync(rig.Packages, Pkg.ScreenOnly(Pkg.Png()))).Info;
        v1.SchemaVersion.Should().Be(1);
        (await rig.Packages.ListScenePackagesAsync()).Select(p => p.SchemaVersion).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task A_region_survives_apply_profile_restore_and_restart_with_the_local_rotation()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        var screens = new FakeScreens { AcceptOpens = true, CasePresent = true };
        try
        {
            var first = await StartAsync(true, data, screens);
            await first.Host.Get<IScreenDeviceSettingsService>().SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(FakeScreens.Case.Id, 90));
            var cid = (await ImportAsync(first.Packages, Pkg.RegionScreenOnly(Pkg.Png()))).Info.ContentId;
            var state = await first.Packages.SaveScenePackageBindingsAsync(Bind(cid, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Case.Id)));
            state.Ready.Should().BeTrue(string.Join("; ", state.Problems));
            var applied = await first.Packages.ApplyScenePackageAsync(cid);
            applied.Applied.Should().BeTrue(applied.Detail);
            var timeline = ScenePackageIds.TimelineIdOf(cid, state.BindingId!);
            CaseOf(first).Should().Match<ScreenAssignmentDto>(a =>
                a.OverlayLayout == RegionRight && a.AccentColor == "#FAD61D" && a.Overlay == "dashboard" && a.Rotation == 90 && a.TimelineId == timeline);

            // profile: save the package scene, move away, restore
            await first.Rgb.SaveProfileAsync(new ProfileDto("region package", await first.Rgb.GetSceneAsync()));
            File.ReadAllText(Path.Combine(first.Host.Options.ResolvedDataDirectory, "profiles", "region package.json")).Should().Contain("overlayLayout");
            await first.Rgb.SetSceneAsync(new SceneDto([new AssignmentDto([new ZoneRefDto(Loop, 0)], "StaticColor", ["#FF0000"])], Screens: new ScreenSceneDto([new ScreenAssignmentDto(FakeScreens.Case.Id, BackgroundColor: "#102030")])));
            CaseOf(first).OverlayLayout.Should().BeNull();
            await first.Rgb.ApplyProfileAsync("region package");
            CaseOf(first).Should().Match<ScreenAssignmentDto>(a => a.OverlayLayout == RegionRight && a.AccentColor == "#FAD61D" && a.Rotation == 90 && a.TimelineId == timeline);

            // a plain profile with no region writes no region key (historical JSON)
            await first.Rgb.SaveProfileAsync(new ProfileDto("plain", new SceneDto([new AssignmentDto([new ZoneRefDto(Loop, 0)], "StaticColor", ["#FF0000"])], Screens: new ScreenSceneDto([new ScreenAssignmentDto(FakeScreens.Case.Id)]))));
            File.ReadAllText(Path.Combine(first.Host.Options.ResolvedDataDirectory, "profiles", "plain.json")).Should().NotContain("overlayLayout");
            await first.StopKeepingDataAsync();

            await using var second = await StartAsync(true, data, screens);
            CaseOf(second).Should().Match<ScreenAssignmentDto>(a =>
                a.OverlayLayout == RegionRight && a.AccentColor == "#FAD61D" && a.Rotation == 90 && a.TimelineId == timeline, "startup restores the region and the local rotation");
            (await second.Host.Get<IScreenService>().GetScreenSceneAsync()).Assignments.Single(a => a.ScreenId == FakeScreens.Case.Id).OverlayLayout.Should().Be(RegionRight);
        }
        finally
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task A_refused_region_apply_keeps_the_previous_RGB_screens_and_timeline()
    {
        await using var rig = await StartAsync(true, null, new FakeScreens { AcceptOpens = true, CasePresent = true });
        var a = (await ImportAsync(rig.Packages, Pkg.RegionScreenOnly(Pkg.Png()))).Info.ContentId;
        var aState = await rig.Packages.SaveScenePackageBindingsAsync(Bind(a, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Case.Id)));
        (await rig.Packages.ApplyScenePackageAsync(a)).Applied.Should().BeTrue();
        var aTimeline = ScenePackageIds.TimelineIdOf(a, aState.BindingId!);
        var b = (await ImportAsync(rig.Packages, RegionMixed())).Info.ContentId;
        var bState = await rig.Packages.SaveScenePackageBindingsAsync(Bind(b, Zone("strip"), new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Case.Id)));
        bState.Ready.Should().BeTrue(string.Join("; ", bState.Problems));
        var screens = rig.Host.Get<ScreenManager>().CurrentForScene();
        var rgb = rig.Engine.Loop.Scene;

        rig.Adapter.Fits = false; // the strict wire budget refuses package B at engine admission
        var refused = await rig.Packages.ApplyScenePackageAsync(b);

        refused.Applied.Should().BeFalse();
        rig.Host.Get<ScreenManager>().CurrentForScene().Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        CaseOf(rig).Should().Match<ScreenAssignmentDto>(s => s.OverlayLayout == RegionRight && s.TimelineId == aTimeline);
        rig.Engine.Loop.Scene.Should().BeSameAs(rgb, "the refused candidate never replaced the RGB scene");
        rig.Host.Get<ScenePackageTimeline>().Position(aTimeline)!.Source.Should().NotBe(TimelineSources.Blocked, "the previous package timeline stays published");

        rig.Adapter.Fits = true;
        (await rig.Packages.ApplyScenePackageAsync(b)).Applied.Should().BeTrue();
        CaseOf(rig).OverlayLayout.Should().Be(new ScreenOverlayLayoutDto(0.02, 0.10, 0.40, 0.80, 1, 0.3));
    }
}
