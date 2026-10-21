using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

public sealed class ScenePackageVideoTests
{
    internal static byte[] VideoPackage(int schema, int fps=30, byte[]? content=null)
    {
        var bytes=content??[0,0,0,16,(byte)'f',(byte)'t',(byte)'y',(byte)'p',105,115,111,109,0,0,0,0];
        var file="media/panel.mp4";var files=new Dictionary<string,byte[]>{{file,bytes}};
        var track=Pkg.ScreenTrack("panel",file,2000);track["fps"]=fps;track["overlay"]="dashboard";track["overlayLayout"]=Pkg.Region();
        var manifest=Pkg.Manifest(files,[],[track],2000);manifest["schemaVersion"]=schema;
        return Pkg.Zip([("scene.json",Pkg.Utf8(manifest)),(file,bytes)]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Old_package_versions_do_not_silently_accept_MP4(int schema)
    {
        FluentActions.Invoking(()=>ScenePackageReader.Read(VideoPackage(schema))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void V3_explicitly_pins_MP4_bytes_and_keeps_the_shared_loop_and_region_contract()
    {
        var source=VideoPackage(3);var package=ScenePackageReader.Read(source);
        package.Manifest.SchemaVersion.Should().Be(3);package.Manifest.DurationMs.Should().Be(2000);
        var screen=package.Manifest.Screens.Single();screen.Fps.Should().Be(30);screen.Overlay.Should().Be("dashboard");screen.OverlayLayout.Should().NotBeNull();
        package.Manifest.Files[screen.File].Should().Be(Pkg.Sha(package.Files[screen.File]));
        ScenePackageReader.Read(source).ContentId.Should().Be(package.ContentId,"the original ZIP/manifest bytes are never rewritten");
    }

    [Fact]
    public void V3_rejects_excessive_frame_rate_and_disguised_non_video_payloads()
    {
        FluentActions.Invoking(()=>ScenePackageReader.Read(VideoPackage(3,61))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(()=>ScenePackageReader.Read(VideoPackage(3,30,Pkg.Png()))).Should().Throw<ArgumentException>().WithMessage("*MP4*");
        var old=Pkg.RegionScreenOnly(Pkg.Png(),s=>s["fps"]=30);
        FluentActions.Invoking(()=>ScenePackageReader.Read(old)).Should().Throw<ArgumentException>().WithMessage("*fps*");
    }
}

internal sealed class NativeVideoHostFactAttribute : FactAttribute
{
    public NativeVideoHostFactAttribute()
    {
        using var decoder = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{62ce7e72-4c71-4d20-b15d-452831a87d9d}\InprocServer32");
        if (decoder is null) Skip = "Windows Media Foundation H.264 decoder is not installed.";
    }
}

public sealed partial class ScenePackageHostTests
{
    [NativeVideoHostFact]
    public async Task A_real_MP4_package_keeps_source_hash_local_direction_shared_clock_and_overlay_across_restore_and_restart()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        var platform = new FakeScreens { AcceptOpens = true, CasePresent = true };
        var media = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-30.mp4"));
        var zip = ScenePackageVideoTests.VideoPackage(3, 30, media);
        try
        {
            var first = await StartAsync(true, data, platform);
            await first.Host.Get<IScreenDeviceSettingsService>().SetScreenDeviceSettingsAsync(new(FakeScreens.Case.Id, 90));
            var cid = (await ImportAsync(first.Packages, zip)).Info.ContentId;
            var bound = await first.Packages.SaveScenePackageBindingsAsync(Bind(cid, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Case.Id)));
            bound.Ready.Should().BeTrue(string.Join("; ", bound.Problems));
            (await first.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
            var current = CaseOf(first);
            current.Should().Match<ScreenAssignmentDto>(a => a.Fps == 30 && a.Rotation == 90 && a.Overlay == "dashboard" && a.OverlayLayout != null && a.TimelineId != null);
            var asset = (await first.Host.Get<IScreenService>().ListScreenAssetsAsync()).Single();
            (asset.Kind, asset.FrameCount, asset.DurationMs).Should().Be(("video", 60, 2000));
            await first.Rgb.SaveProfileAsync(new("video scene", await first.Rgb.GetSceneAsync()));
            await first.Rgb.SetSceneAsync(new([], Screens: new([new(FakeScreens.Case.Id, BackgroundColor: "#102030")])));
            await first.Rgb.ApplyProfileAsync("video scene");
            CaseOf(first).Should().Be(current);
            await first.StopKeepingDataAsync();
            await using var second = await StartAsync(true, data, platform);
            CaseOf(second).Should().Be(current);
            (await second.Host.Get<IScreenService>().ListScreenAssetsAsync()).Single().Should().BeEquivalentTo(asset);
            var installed = ScenePackageReader.Read(zip);
            installed.Files[installed.Manifest.Screens.Single().File].Should().Equal(media);
            installed.ContentId.Should().Be(cid);
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
    }
}
