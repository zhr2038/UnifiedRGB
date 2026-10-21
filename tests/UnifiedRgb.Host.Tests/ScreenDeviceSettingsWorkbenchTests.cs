using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Test-controlled RPC completion races exercise draft ownership.
public sealed class ScreenDeviceSettingsWorkbenchTests
{
    [Fact]
    public async Task Local_settings_read_and_edit_do_not_write_or_enter_the_content_draft()
    {
        var screens = new RecordingScreenWorkbench(); var settings = new LocalSettings(screens);
        var vm = New(screens, settings); settings.Writes.Should().BeEmpty();
        await vm.RefreshAsync(); vm.Rotation.Should().Be(180); vm.Build().Rotation.Should().Be(180);
        vm.Rotation = 90; vm.EncodingQuality = vm.EncodingQualities[1];
        vm.DeviceSettingsDirty.Should().BeTrue(); vm.Dirty.Should().BeFalse();
        vm.Build().Rotation.Should().Be(180, "content apply cannot silently save a local direction draft");
        settings.Writes.Should().BeEmpty(); screens.Writes.Should().BeEmpty();
        vm.EncodingQualities.Should().NotContain(q => q.Id.Contains("palette", StringComparison.Ordinal));
        vm.EncodingQualities.Should().NotContain(q => q.Id == "jpeg-detail-v1", "older Hosts must not advertise an unsupported policy");
    }

    [Fact]
    public async Task Saving_local_settings_keeps_the_content_draft_and_other_device_content()
    {
        var screens = new RecordingScreenWorkbench(); var settings = new LocalSettings(screens); var vm = New(screens, settings);
        await vm.RefreshAsync(); var other = screens.Scene.Assignments[1];
        vm.Background = "#123456"; vm.Rotation = 270; vm.EncodingQuality = vm.EncodingQualities[1];
        await vm.SaveDeviceSettingsCommand.ExecuteAsync();
        vm.Dirty.Should().BeTrue(); vm.Background.Should().Be("#123456"); vm.DeviceSettingsDirty.Should().BeFalse();
        vm.Build().Rotation.Should().Be(270); settings.Writes.Should().ContainSingle(); screens.Writes.Should().BeEmpty();
        screens.Scene.Assignments[1].Should().Be(other); screens.Scene.Assignments[0].BackgroundColor.Should().NotBe("#123456");
        await vm.ApplyCommand.ExecuteAsync(); vm.Dirty.Should().BeFalse(); screens.Writes.Should().Equal("SetScreenScene");
    }

    [Fact]
    public async Task JPEG_detail_is_offered_only_after_Host_capability_and_saved_without_applying_content()
    {
        var screens=new RecordingScreenWorkbench();var settings=new LocalSettings(screens){JpegSupport=true};var vm=New(screens,settings);
        await vm.RefreshAsync();
        vm.EncodingQuality=vm.EncodingQualities.Single(q=>q.Id=="jpeg-detail-v1");
        await vm.SaveDeviceSettingsCommand.ExecuteAsync();
        settings.Writes.Should().ContainSingle().Which.EncodingQuality.Should().Be("jpeg-detail-v1");
        screens.Writes.Should().BeEmpty("saving local quality must not apply the content draft");
    }

    [Fact]
    public async Task Reconnect_keeps_a_local_only_draft_and_does_not_re_read_over_it()
    {
        var screens = new RecordingScreenWorkbench(); var settings = new LocalSettings(screens); var vm = New(screens, settings);
        await vm.RefreshAsync(); vm.Rotation = 90; vm.MarkDisconnected(); await vm.RefreshOptionalAsync();
        vm.Rotation.Should().Be(90); vm.DeviceSettingsDirty.Should().BeTrue(); vm.CanSaveDeviceSettings.Should().BeTrue();
        settings.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_started_before_a_local_edit_does_not_discard_it()
    {
        var screens = new RecordingScreenWorkbench(); var settings = new LocalSettings(screens); var vm = New(screens, settings);
        await vm.RefreshAsync(); var pending = new TaskCompletionSource<ScreenSceneDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        screens.ReadScene = () => pending.Task; var read = vm.RefreshAsync(); vm.Rotation = 90;
        pending.SetResult(screens.Scene); await read;
        vm.Rotation.Should().Be(90); vm.DeviceSettingsDirty.Should().BeTrue(); settings.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_to_read_local_settings_disables_save_and_never_writes()
    {
        var screens = new RecordingScreenWorkbench(); var settings = new LocalSettings(screens) { FailRead = true }; var vm = New(screens, settings);
        await vm.RefreshAsync(); vm.Rotation = 90; await vm.SaveDeviceSettingsCommand.ExecuteAsync();
        vm.CanSaveDeviceSettings.Should().BeFalse(); vm.DeviceSettingsNote.Should().Contain("读取失败"); settings.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Encoding_failure_removes_the_previous_preview_instead_of_showing_a_stale_success()
    {
        // A tiny valid PNG represents a returned encoded preview; no hardware calls occur.
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==";
        var screens = new RecordingScreenWorkbench { Preview = _ => Task.FromResult(new ScreenPreviewDto(png, 1, 1, "encoded")) };
        Exception? error = null; var vm = new ScreensViewModel(() => screens, InlineDispatcher.Instance, ex => error = ex, () => null);
        await vm.RefreshAsync(); await vm.PreviewCommand.ExecuteAsync(); vm.Preview.Should().NotBeNull();
        screens.Preview = _ => throw new InvalidOperationException("NOT SENDABLE: over budget");
        await vm.PreviewCommand.ExecuteAsync(); vm.Preview.Should().BeNull(); vm.PreviewNote.Should().Contain("编码失败");
        error.Should().BeOfType<InvalidOperationException>();
    }

    private static ScreensViewModel New(RecordingScreenWorkbench screens, LocalSettings settings) =>
        new(() => screens, InlineDispatcher.Instance, ex => throw ex, () => null, _ => false, deviceSettings: () => settings);

    private sealed class LocalSettings(RecordingScreenWorkbench screens) : IScreenDeviceSettingsService
    {
        public List<ScreenDeviceSettingsUpdateDto> Writes { get; } = [];
        public bool FailRead { get; init; }
        public bool JpegSupport { get; init; }
        public Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string screenId, CancellationToken ct = default) =>
            FailRead ? throw new InvalidOperationException("unavailable") : Task.FromResult(new ScreenDeviceSettingsDto(screenId, 180, "legacy", false, JpegDetailSupported:JpegSupport));
        public Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto s, CancellationToken ct = default)
        {
            Writes.Add(s); screens.Scene = new(screens.Scene.Assignments.Select(a => a.ScreenId == s.ScreenId ? a with { Rotation = s.Rotation } : a).ToArray());
            return Task.FromResult(new ScreenDeviceSettingsDto(s.ScreenId, s.Rotation, s.EncodingQuality, false, JpegDetailSupported:JpegSupport));
        }
    }
}
