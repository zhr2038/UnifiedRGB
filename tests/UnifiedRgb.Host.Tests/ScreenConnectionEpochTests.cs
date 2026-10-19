using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Test-owned completion sources control old-session responses.
public sealed class ScreenConnectionEpochTests
{
    private static ScreensViewModel New(RecordingScreenWorkbench rpc, IScreenDeviceSettingsService? settings = null) =>
        new(() => rpc, InlineDispatcher.Instance, _ => { }, () => null, _ => false, deviceSettings: settings is null ? null : () => settings);

    [Fact]
    public async Task Late_full_refresh_cannot_undo_disconnect_and_a_new_refresh_can_reconnect()
    {
        var rpc = new RecordingScreenWorkbench(); var vm = New(rpc); await vm.RefreshAsync();
        var gate = new TaskCompletionSource<ScreenSceneDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.ReadScene = () => gate.Task;
        var refresh = vm.RefreshAsync(); vm.MarkDisconnected();
        gate.SetResult(rpc.Scene); await refresh;
        vm.Available.Should().BeFalse(); vm.CanApplyContent.Should().BeFalse(); vm.ScreenDetail.Should().Contain("上次读取");
        rpc.ReadScene = null; await vm.RefreshOptionalAsync();
        vm.Available.Should().BeTrue(); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Late_dirty_refresh_keeps_both_drafts_and_the_disconnect_message()
    {
        var rpc = new RecordingScreenWorkbench(); var settings = new DelayedSettings();
        var vm = New(rpc, settings); await vm.RefreshAsync(); await vm.ReadDeviceSettingsAsync();
        vm.Background = "#123456"; vm.Rotation = 270;
        var gate = new TaskCompletionSource<IReadOnlyList<ScreenDeviceDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.ReadScreens = () => gate.Task;
        var refresh = vm.RefreshOptionalAsync(); vm.MarkDisconnected();
        gate.SetResult(rpc.DeviceSnapshots); await refresh;
        vm.Available.Should().BeFalse(); vm.Background.Should().Be("#123456"); vm.Rotation.Should().Be(270);
        vm.Dirty.Should().BeTrue(); vm.DeviceSettingsDirty.Should().BeTrue(); vm.Message.Should().Contain("后台未连接");
        rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Old_poll_cannot_overwrite_new_session_telemetry_or_commands()
    {
        var rpc = new RecordingScreenWorkbench(); var vm = New(rpc); await vm.RefreshAsync();
        var gate = new TaskCompletionSource<PcTelemetryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.ReadTelemetry = () => gate.Task;
        var poll = vm.PollAsync(); vm.MarkDisconnected();
        rpc.ReadTelemetry = null; rpc.Time = DateTimeOffset.UtcNow;
        await vm.RefreshOptionalAsync(); var fresh = vm.Telemetry;
        gate.SetResult(new(DateTimeOffset.UtcNow, 99, 99, 99, 99, 99, 99, 99, "old", null)); await poll;
        vm.Telemetry.Should().Be(fresh); vm.Available.Should().BeTrue(); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Late_settings_read_cannot_change_rotation_after_disconnect()
    {
        var rpc = new RecordingScreenWorkbench(); var settings = new DelayedSettings();
        var vm = New(rpc, settings); await vm.RefreshAsync(); await vm.ReadDeviceSettingsAsync();
        var gate = new TaskCompletionSource<ScreenDeviceSettingsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.Pending = gate.Task;
        var read = vm.ReadDeviceSettingsAsync(); vm.MarkDisconnected();
        gate.SetResult(new("screen:h2:test", 270, "legacy", false)); await read;
        vm.Rotation.Should().Be(180); vm.Available.Should().BeFalse(); vm.CanSaveDeviceSettings.Should().BeFalse();
    }

    private sealed class DelayedSettings : IScreenDeviceSettingsService
    {
        public Task<ScreenDeviceSettingsDto>? Pending { get; set; }
        public Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string id, CancellationToken ct = default) =>
            Pending ?? Task.FromResult(new ScreenDeviceSettingsDto(id, 180, "legacy", false));
        public Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto value, CancellationToken ct = default) => throw new InvalidOperationException("No write expected.");
    }
}
