using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Local screen settings in the real Host (full DI, fake screens): a saved local rotation holds across two different generic
/// packages, a profile carrying a stale rotation, raw RGB+screen scenes and a restart; RGB-only scenes, refused package applies and
/// the settings save itself change nothing else. Package files and stored bindings are never touched by a local setting.
/// </summary>
public sealed partial class ScenePackageHostTests
{
    private static readonly SceneDto RedLoop = new([new AssignmentDto([new ZoneRefDto(Loop, 0)], "StaticColor", ["#FF0000"])]);

    private static string SettingsFile(Rig rig) => Path.Combine(rig.Host.Options.ResolvedDataDirectory, "screens", "device-settings.json");

    private static ScreenAssignmentDto H2Of(Rig rig) => rig.Host.Get<ScreenManager>().CurrentForScene()!.Assignments.Single(a => a.ScreenId == FakeScreens.H2.Id);

    /// <summary>Path → SHA-256 of every file in the package store (contents, bindings and records).</summary>
    private static Dictionary<string, string> PackageStoreHashes(Rig rig)
    {
        var root = Path.Combine(rig.Host.Options.ResolvedDataDirectory, ScenePackageStore.DirectoryName);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Pkg.Sha(File.ReadAllBytes(f)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_local_rotation_holds_across_two_packages_a_stale_profile_raw_scenes_and_a_restart()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        var screens = new FakeScreens { AcceptOpens = true };
        try
        {
            var first = await StartAsync(true, data, screens);
            var settings = first.Host.Get<IScreenDeviceSettingsService>();
            settings.Should().BeSameAs(first.Host.Get<ScreenManager>());
            (await settings.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(FakeScreens.H2.Id, 90))).Rotation.Should().Be(90);

            // package A: screen only
            var a = (await ImportAsync(first.Packages, Pkg.ScreenOnly(Pkg.Png()))).Info.ContentId;
            var aState = await first.Packages.SaveScenePackageBindingsAsync(Bind(a, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.H2.Id)));
            aState.Ready.Should().BeTrue(string.Join("; ", aState.Problems));
            var applied = await first.Packages.ApplyScenePackageAsync(a);
            applied.Applied.Should().BeTrue(applied.Detail);
            H2Of(first).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.TimelineId == ScenePackageIds.TimelineIdOf(a, aState.BindingId!));

            // package B: different content, lighting + screen
            var b = (await ImportAsync(first.Packages, Pkg.Mixed(Pkg.Png()))).Info.ContentId;
            b.Should().NotBe(a);
            var bState = await first.Packages.SaveScenePackageBindingsAsync(Bind(b, Zone("strip"), new ScenePackageBindingDto("panel", ScreenId: FakeScreens.H2.Id)));
            bState.Ready.Should().BeTrue(string.Join("; ", bState.Problems));
            applied = await first.Packages.ApplyScenePackageAsync(b);
            applied.Applied.Should().BeTrue(applied.Detail);
            H2Of(first).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.TimelineId == ScenePackageIds.TimelineIdOf(b, bState.BindingId!));
            var store = PackageStoreHashes(first);

            // a profile of the current package scene carrying a stale rotation (as an older profile would)
            var current = await first.Rgb.GetSceneAsync();
            current.Screens!.Assignments.Single().Rotation.Should().Be(90);
            var stale = current with { Screens = new ScreenSceneDto([.. current.Screens.Assignments.Select(s => s with { Rotation = 0 })]) };
            await first.Rgb.SaveProfileAsync(new ProfileDto("stale package profile", stale));
            await first.Rgb.SaveProfileAsync(new ProfileDto("stale plain profile",
                RedLoop with { Screens = new ScreenSceneDto([new ScreenAssignmentDto(FakeScreens.H2.Id, BackgroundColor: "#102030", Rotation: 270)]) }));

            await first.Rgb.ApplyProfileAsync("stale plain profile");
            H2Of(first).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.BackgroundColor == "#102030");
            await first.Rgb.ApplyProfileAsync("stale package profile");
            H2Of(first).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.TimelineId == ScenePackageIds.TimelineIdOf(b, bState.BindingId!));
            (await first.Rgb.LoadProfileAsync("stale package profile")).Scene.Screens!.Assignments.Single().Rotation.Should().Be(0, "a stored profile is not rewritten");

            // raw RGB + screen scene with another rotation: shown at the local value, never saved as one
            await first.Rgb.SetSceneAsync(RedLoop with { Screens = new ScreenSceneDto([new ScreenAssignmentDto(FakeScreens.H2.Id, BackgroundColor: "#203040", Rotation: 180)]) });
            H2Of(first).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.BackgroundColor == "#203040");
            (await settings.GetScreenDeviceSettingsAsync(FakeScreens.H2.Id)).Rotation.Should().Be(90);
            PackageStoreHashes(first).Should().BeEquivalentTo(store, "local settings never touch package content, records or bindings");
            await first.StopKeepingDataAsync();

            await using var second = await StartAsync(true, data, screens);
            H2Of(second).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.BackgroundColor == "#203040", "the startup restore shows the local rotation");
            (await second.Host.Get<IScreenDeviceSettingsService>().GetScreenDeviceSettingsAsync(FakeScreens.H2.Id)).Rotation.Should().Be(90);
            applied = await second.Packages.ApplyScenePackageAsync(a);
            applied.Applied.Should().BeTrue(applied.Detail);
            H2Of(second).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 90 && s.TimelineId == ScenePackageIds.TimelineIdOf(a, aState.BindingId!));
            PackageStoreHashes(second).Should().BeEquivalentTo(store);

            // an unconfigured screen still shows a package at 0 (no preference)
            (await second.Host.Get<IScreenDeviceSettingsService>().GetScreenDeviceSettingsAsync(FakeScreens.Tl.Id)).Rotation.Should().Be(0);
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
    public async Task Saving_a_local_setting_keeps_RGB_package_timeline_and_content_and_RGB_only_scenes_keep_the_settings()
    {
        await using var rig = await StartAsync(screenOutput: true);
        var settings = rig.Host.Get<IScreenDeviceSettingsService>();
        var cid = (await ImportAsync(rig.Packages, Pkg.Mixed(Pkg.Png()))).Info.ContentId;
        var state = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip"), new ScenePackageBindingDto("panel", ScreenId: FakeScreens.H2.Id)));
        var applied = await rig.Packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeTrue(applied.Detail);
        var timeline = ScenePackageIds.TimelineIdOf(cid, state.BindingId!);
        await SettledAsync(rig.Adapter);
        var rgbScene = rig.Engine.Loop.Scene;
        var uploads = rig.Adapter.Uploaded.Count;
        var before = H2Of(rig);
        var source = rig.Host.Get<ScenePackageTimeline>().Position(timeline)!.Source;
        source.Should().NotBe(TimelineSources.Blocked);

        (await settings.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(FakeScreens.H2.Id, 270))).Rotation.Should().Be(270);

        H2Of(rig).Should().Be(before with { Rotation = 270 }, "only the orientation changes; asset, timeline, overlay, fps and brightness stay");
        rig.Engine.Loop.Scene.Should().BeSameAs(rgbScene, "a local screen setting never touches RGB");
        rig.Host.Get<ScenePackageTimeline>().Position(timeline)!.Source.Should().Be(source, "the package timeline stays published");
        (await rig.Rgb.GetSceneAsync()).Package.Should().Be(new ScenePackageRefDto(cid, state.BindingId!, Pkg.Contract));
        await Task.Delay(300);
        rig.Adapter.Uploaded.Count.Should().Be(uploads, "no RGB upload follows a screen setting");

        // RGB-only scenes keep the screens and the settings file
        var file = File.ReadAllBytes(SettingsFile(rig));
        var screens = rig.Host.Get<ScreenManager>().CurrentForScene();
        await rig.Rgb.SetSceneAsync(RedLoop);
        await rig.Rgb.SaveProfileAsync(new ProfileDto("rgb only", RedLoop));
        await rig.Rgb.ApplyProfileAsync("rgb only");
        rig.Host.Get<ScreenManager>().CurrentForScene().Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        File.ReadAllBytes(SettingsFile(rig)).Should().Equal(file);
        File.ReadAllText(Path.Combine(rig.Host.Options.ResolvedDataDirectory, "profiles", "rgb only.json")).Should().NotContain("rotation");
    }

    [Fact]
    public async Task A_refused_package_apply_keeps_the_settings_screens_and_the_previous_timeline()
    {
        await using var rig = await StartAsync(screenOutput: true);
        var settings = rig.Host.Get<IScreenDeviceSettingsService>();
        await settings.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(FakeScreens.H2.Id, 180));
        var a = (await ImportAsync(rig.Packages, Pkg.ScreenOnly(Pkg.Png()))).Info.ContentId;
        var aState = await rig.Packages.SaveScenePackageBindingsAsync(Bind(a, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.H2.Id)));
        (await rig.Packages.ApplyScenePackageAsync(a)).Applied.Should().BeTrue();
        var aTimeline = ScenePackageIds.TimelineIdOf(a, aState.BindingId!);
        var b = (await ImportAsync(rig.Packages, Pkg.Mixed(Pkg.Png()))).Info.ContentId;
        var bState = await rig.Packages.SaveScenePackageBindingsAsync(Bind(b, Zone("strip"), new ScenePackageBindingDto("panel", ScreenId: FakeScreens.H2.Id)));
        bState.Ready.Should().BeTrue(string.Join("; ", bState.Problems));
        var screens = rig.Host.Get<ScreenManager>().CurrentForScene();
        var file = File.ReadAllBytes(SettingsFile(rig));

        rig.Adapter.Fits = false; // the strict wire budget refuses package B at engine admission
        var refused = await rig.Packages.ApplyScenePackageAsync(b);

        refused.Applied.Should().BeFalse();
        rig.Host.Get<ScreenManager>().CurrentForScene().Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        H2Of(rig).Should().Match<ScreenAssignmentDto>(s => s.Rotation == 180 && s.TimelineId == aTimeline);
        File.ReadAllBytes(SettingsFile(rig)).Should().Equal(file);
        rig.Host.Get<ScenePackageTimeline>().Position(aTimeline)!.Source.Should().NotBe(TimelineSources.Blocked, "the previous package timeline stays published");
        rig.Engine.Loop.Scene.Package!.ContentId.Should().Be(a, "the refused candidate never replaced the RGB scene");
    }
}
