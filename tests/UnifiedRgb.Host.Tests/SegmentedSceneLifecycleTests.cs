using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

public sealed partial class ScenePackageHostTests
{
    [NativeVideoHostFact]
    public async Task Segmented_video_longer_than_thirty_seconds_keeps_identity_protection_overlay_and_profile_after_restart()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        var platform = new FakeScreens { AcceptOpens = true, CasePresent = true };
        var media = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-30.mp4"));
        var zip = SegmentedScenePackageTests.Package(media: media, clipCount: 16, edit: track =>
        {
            track["overlay"] = "dashboard"; track["overlayLayout"] = Pkg.Region();
        });
        try
        {
            var first = await StartAsync(true, data, platform);
            await first.Host.Get<IScreenDeviceSettingsService>().SetScreenDeviceSettingsAsync(new(FakeScreens.Case.Id, 90));
            var cid = (await ImportAsync(first.Packages, zip)).Info.ContentId;
            var bound = await first.Packages.SaveScenePackageBindingsAsync(Bind(cid, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Case.Id)));
            bound.Ready.Should().BeTrue(string.Join("; ", bound.Problems));
            (await first.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
            var current = CaseOf(first);
            current.Should().Match<ScreenAssignmentDto>(a => a.Fps == 30 && a.Rotation == 90 && a.TimelineId != null && a.OverlayLayout != null);
            var screens = first.Host.Get<IScreenService>();
            var asset = (await screens.ListScreenAssetsAsync()).Single();
            (asset.Kind, asset.FrameCount, asset.DurationMs).Should().Be(("video", 960, 32000));
            (await first.Host.Get<ISceneLibraryService>().InspectScenePackageRemovalAsync(cid)).RelatedAssetIds.Should().Equal(asset.Id);
            await FluentActions.Awaiting(() => screens.DeleteScreenAssetAsync(asset.Id)).Should().ThrowAsync<InvalidOperationException>();
            await first.Rgb.SaveProfileAsync(new("segmented forest", await first.Rgb.GetSceneAsync()));
            await first.Rgb.SetSceneAsync(new([], Screens: new([new(FakeScreens.Case.Id, BackgroundColor: "#102030")])));
            await first.Rgb.ApplyProfileAsync("segmented forest");
            CaseOf(first).Should().Be(current);
            await first.StopKeepingDataAsync();
            await using var second = await StartAsync(true, data, platform);
            CaseOf(second).Should().Be(current);
            (await second.Host.Get<IScreenService>().ListScreenAssetsAsync()).Single().Should().BeEquivalentTo(asset);
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
    }
}
