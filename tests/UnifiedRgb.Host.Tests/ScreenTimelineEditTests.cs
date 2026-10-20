using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Editing a package-timed screen in the screen editor (fake RPC only): presentation edits keep its shared TimelineId through
/// preview and apply; different media never inherits it and detaches only after an explicit confirmation.
/// </summary>
public sealed class ScreenTimelineEditTests
{
    private const string Timeline = "package:1111111111111111111111111111111111111111111111111111111111111111:2222222222222222222222222222222222222222222222222222222222222222";
    private const string CaseMedia = "aaaaaaaaaaaaaaaa";
    private const string H2Media = "cccccccccccccccc";
    private const string UserMedia = "bbbbbbbbbbbbbbbb";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==";
    private static readonly ScreenOverlayLayoutDto Region = new(.50, .20, .47, .66, 2, 0);

    [Theory]
    [InlineData("brightness")]
    [InlineData("fps")]
    [InlineData("fit")]
    [InlineData("accent")]
    [InlineData("overlay")]
    public async Task Presentation_edit_keeps_the_shared_timeline_in_preview_and_apply(string edit)
    {
        var rig = new Rig(); var vm = await rig.OpenCaseAsync();
        var others = rig.Screens.Scene.Assignments.Where(a => a.ScreenId != Case).ToArray();
        switch (edit)
        {
            case "brightness": vm.Brightness = 60; break;
            case "fps": vm.Fps = 8; break;
            case "fit": vm.Fit = vm.Fits.Single(f => f.Id == "contain"); break;
            case "accent": vm.Accent = "#FAD61D"; break;
            default: vm.Overlay = vm.Overlays.Single(o => o.Id == "compact"); break;
        }

        await vm.PreviewCommand.ExecuteAsync();
        rig.Previewed.Should().ContainSingle().Which.TimelineId.Should().Be(Timeline);
        vm.PreviewNote.Should().NotContain("脱离");
        rig.Screens.Writes.Should().BeEmpty();

        await vm.ApplyCommand.ExecuteAsync();
        rig.Errors.Should().BeEmpty(); rig.Prompts.Should().BeEmpty();
        rig.Screens.Writes.Should().Equal("SetScreenScene");
        var saved = rig.Screens.Scene.Assignments.Single(a => a.ScreenId == Case);
        saved.TimelineId.Should().Be(Timeline);
        saved.BackgroundAssetId.Should().Be(CaseMedia);
        saved.Rotation.Should().Be(180, "the saved local rotation, not a scene value");
        saved.Brightness.Should().Be(edit == "brightness" ? .6 : 1);
        saved.Fps.Should().Be(edit == "fps" ? 8 : 15);
        saved.Fit.Should().Be(edit == "fit" ? "contain" : "cover");
        saved.AccentColor.Should().Be(edit == "accent" ? "#FAD61D" : "#5DDAC8");
        saved.Overlay.Should().Be(edit == "overlay" ? "compact" : "dashboard");
        saved.OverlayLayout.Should().Be(edit == "overlay" ? null : Region);
        rig.Screens.Scene.Assignments.Where(a => a.ScreenId != Case).Should().BeEquivalentTo(others);
        rig.Settings.Writes.Should().BeEmpty("content apply never saves local direction or quality");
        vm.Dirty.Should().BeFalse(); vm.Message.Should().NotContain("脱离");
    }

    [Fact]
    public async Task Later_edits_after_an_apply_and_a_reconnect_stay_on_the_timeline()
    {
        var rig = new Rig(); var vm = await rig.OpenCaseAsync();
        vm.Brightness = 70; await vm.ApplyCommand.ExecuteAsync();
        vm.Fps = 10; vm.MarkDisconnected(); await vm.RefreshOptionalAsync();
        vm.Dirty.Should().BeTrue(); vm.Fps.Should().Be(10); rig.Screens.Writes.Should().Equal("SetScreenScene");
        await vm.ApplyCommand.ExecuteAsync();
        var saved = rig.Screens.Scene.Assignments.Single(a => a.ScreenId == Case);
        saved.TimelineId.Should().Be(Timeline); saved.Brightness.Should().Be(.7); saved.Fps.Should().Be(10);
        rig.Prompts.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Selecting_and_previewing_package_screens_never_writes()
    {
        var rig = new Rig(); var vm = rig.New(); var original = rig.Screens.Scene;
        await vm.RefreshAsync();
        vm.Selected = vm.Screens.Single(s => s.Id == Case); vm.Selected = vm.Screens.Single(s => s.Id == H2);
        vm.Build().TimelineId.Should().Be(Timeline);
        await vm.PreviewCommand.ExecuteAsync();
        vm.Selected = vm.Screens.Single(s => s.Id == Case); vm.Brightness = 40;
        await vm.PreviewCommand.ExecuteAsync();
        rig.Previewed.Select(p => p.TimelineId).Should().Equal(Timeline, Timeline);
        rig.Screens.Writes.Should().BeEmpty(); rig.Settings.Writes.Should().BeEmpty();
        rig.Screens.Scene.Should().BeSameAs(original); rig.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UserMedia)]
    [InlineData(null)]
    public async Task Different_media_detaches_from_the_timeline_only_after_confirmation(string? media)
    {
        var rig = new Rig(); var vm = await rig.OpenCaseAsync();
        var others = rig.Screens.Scene.Assignments.Where(a => a.ScreenId != Case).ToArray();
        vm.Asset = vm.Assets.Single(a => a.Dto?.Id == media);
        vm.Build().TimelineId.Should().BeNull("new media never runs on a package clock authored for other media");
        await vm.PreviewCommand.ExecuteAsync();
        rig.Previewed.Single().TimelineId.Should().BeNull();
        vm.PreviewNote.Should().Contain("脱离场景包同步时间线");
        rig.Screens.Writes.Should().BeEmpty(); rig.Prompts.Should().BeEmpty();

        await vm.ApplyCommand.ExecuteAsync();
        rig.Errors.Should().BeEmpty();
        rig.Prompts.Should().ContainSingle().Which.Should().Contain("脱离场景包同步时间线");
        var saved = rig.Screens.Scene.Assignments.Single(a => a.ScreenId == Case);
        saved.TimelineId.Should().BeNull(); saved.BackgroundAssetId.Should().Be(media);
        saved.Rotation.Should().Be(180); saved.OverlayLayout.Should().Be(Region);
        rig.Screens.Scene.Assignments.Where(a => a.ScreenId != Case).Should().BeEquivalentTo(others);
        vm.Message.Should().Contain("脱离");

        vm.Brightness = 50; await vm.ApplyCommand.ExecuteAsync();
        rig.Prompts.Should().ContainSingle("an independent screen is not asked again");
        rig.Screens.Scene.Assignments.Single(a => a.ScreenId == Case).TimelineId.Should().BeNull();
    }

    [Fact]
    public async Task Declining_the_detach_writes_nothing_and_keeps_the_draft()
    {
        var rig = new Rig { Accept = false }; var vm = await rig.OpenCaseAsync(); var original = rig.Screens.Scene;
        vm.Asset = vm.Assets.Single(a => a.Dto?.Id == UserMedia);
        await vm.ApplyCommand.ExecuteAsync();
        rig.Prompts.Should().ContainSingle(); rig.Errors.Should().BeEmpty();
        rig.Screens.Writes.Should().BeEmpty(); rig.Screens.Scene.Should().BeSameAs(original);
        vm.Dirty.Should().BeTrue(); vm.Asset!.Dto!.Id.Should().Be(UserMedia);
    }

    [Fact]
    public async Task Returning_to_the_package_media_keeps_the_timeline_without_a_prompt()
    {
        var rig = new Rig(); var vm = await rig.OpenCaseAsync();
        vm.Asset = vm.Assets.Single(a => a.Dto?.Id == UserMedia);
        vm.Asset = vm.Assets.Single(a => a.Dto?.Id == CaseMedia);
        vm.Build().TimelineId.Should().Be(Timeline);
        await vm.ApplyCommand.ExecuteAsync();
        rig.Prompts.Should().BeEmpty();
        rig.Screens.Scene.Assignments.Single(a => a.ScreenId == Case).TimelineId.Should().Be(Timeline);
    }

    [Fact]
    public async Task A_screen_changed_elsewhere_is_not_overwritten_with_the_old_timeline()
    {
        var rig = new Rig(); var vm = await rig.OpenCaseAsync();
        vm.Brightness = 60;
        var other = "package:3333333333333333333333333333333333333333333333333333333333333333:4444444444444444444444444444444444444444444444444444444444444444";
        rig.Screens.Scene = new(rig.Screens.Scene.Assignments.Select(a => a.ScreenId == Case ? a with { TimelineId = other } : a).ToArray());
        await vm.ApplyCommand.ExecuteAsync();
        rig.Errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
        rig.Screens.Writes.Should().BeEmpty();
        rig.Screens.Scene.Assignments.Single(a => a.ScreenId == Case).TimelineId.Should().Be(other);
    }

    private const string Case = "screen:turzx:test";
    private const string H2 = "screen:h2:test";

    private sealed class Rig
    {
        public Rig()
        {
            Screens = new RecordingScreenWorkbench
            {
                Scene = new([
                    new(H2, H2Media, "#000000", "compact", 0, 1, 15, true, Fit: "cover", TimelineId: Timeline),
                    new(Case, CaseMedia, "#000000", "dashboard", 180, 1, 15, true, Fit: "cover", TimelineId: Timeline, OverlayLayout: Region),
                    new("screen:tl-lcd:test", BackgroundColor: "#ABCDEF"),
                ]),
                AssetRows = [
                    new(CaseMedia, "package case", 1920, 480, 240, 16000, "animation"),
                    new(H2Media, "package h2", 400, 400, 240, 16000, "animation"),
                    new(UserMedia, "user", 1920, 480, 1, 0, "image"),
                ],
            };
            Screens.DeviceSnapshots = Screens.DeviceSnapshots
                .Append(new ScreenDeviceDto("screen:tl-lcd:test", "TL", "TlLcd", 400, 400, "Running", null, true, 0, 0, null, 0)).ToArray();
            Screens.Preview = a => { Previewed.Add(a); return Task.FromResult(new ScreenPreviewDto(Png, 1, 1, "encoded")); };
            Settings = new SavedSettings();
        }

        public RecordingScreenWorkbench Screens { get; }
        public SavedSettings Settings { get; }
        public List<ScreenAssignmentDto> Previewed { get; } = [];
        public List<string> Prompts { get; } = [];
        public List<Exception> Errors { get; } = [];
        public bool Accept { get; init; } = true;

        public ScreensViewModel New() => new(() => Screens, InlineDispatcher.Instance, Errors.Add, () => null,
            prompt => { Prompts.Add(prompt); return Accept; }, deviceSettings: () => Settings);

        public async Task<ScreensViewModel> OpenCaseAsync()
        {
            var vm = New(); await vm.RefreshAsync();
            vm.Selected = vm.Screens.Single(s => s.Id == Case);
            vm.Rotation.Should().Be(180); vm.EncodingQuality!.Id.Should().Be("lossless-first-v1");
            Screens.Writes.Should().BeEmpty(); Prompts.Should().BeEmpty();
            return vm;
        }
    }

    private sealed class SavedSettings : IScreenDeviceSettingsService
    {
        public List<ScreenDeviceSettingsUpdateDto> Writes { get; } = [];

        public Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string screenId, CancellationToken ct = default) =>
            Task.FromResult(screenId == Case ? new ScreenDeviceSettingsDto(screenId, 180, "lossless-first-v1", false) : new ScreenDeviceSettingsDto(screenId, 0, "legacy", false));

        public Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto settings, CancellationToken ct = default)
        {
            Writes.Add(settings);
            return Task.FromResult(new ScreenDeviceSettingsDto(settings.ScreenId, settings.Rotation, settings.EncodingQuality, false));
        }
    }
}
