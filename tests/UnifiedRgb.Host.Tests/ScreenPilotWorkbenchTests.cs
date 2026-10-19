using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Views;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Test-owned completion gates select UI/RPC interleavings.

public sealed class ScreenPilotWorkbenchTests
{
    private static ScreensViewModel New(RecordingScreenWorkbench rpc, Settings? settings = null) =>
        new(() => rpc, InlineDispatcher.Instance, _ => { }, () => null, _ => false, deviceSettings: settings is null ? null : () => settings);

    [Theory]
    [InlineData(554, ScreenWorkspaceLayout.Compact)]
    [InlineData(799, ScreenWorkspaceLayout.Compact)]
    [InlineData(800, ScreenWorkspaceLayout.Split)]
    [InlineData(1034, ScreenWorkspaceLayout.Split)]
    [InlineData(1119, ScreenWorkspaceLayout.Split)]
    [InlineData(1120, ScreenWorkspaceLayout.Wide)]
    public void Page_breakpoints_choose_regions_instead_of_scaling_text(double width, ScreenWorkspaceLayout expected) =>
        ScreenWorkspaceView.LayoutFor(width).Should().Be(expected);

    [Fact]
    public async Task Busy_preview_is_observable_and_never_writes_or_repeats_the_RPC()
    {
        var rpc = new RecordingScreenWorkbench();
        var gate = new TaskCompletionSource<ScreenPreviewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        rpc.Preview = _ => { calls++; return gate.Task; };
        var vm = New(rpc); await vm.RefreshAsync();
        var first = vm.PreviewCommand.ExecuteAsync();
        vm.EditorBusy.Should().BeTrue(); vm.EditorIdle.Should().BeFalse(); vm.BusyText.Should().Contain("实际编码");
        vm.CanApplyContent.Should().BeFalse(); vm.CanGeneratePreview.Should().BeFalse(); vm.CanEditContent.Should().BeFalse();
        await vm.PreviewCommand.ExecuteAsync(); calls.Should().Be(1); rpc.Writes.Should().BeEmpty();
        gate.SetException(new InvalidOperationException("模拟编码错误")); await first;
        vm.EditorBusy.Should().BeFalse(); vm.HasError.Should().BeTrue(); vm.ErrorText.Should().Be("模拟编码错误");
        vm.Preview.Should().BeNull(); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_device_settings_preserves_the_content_draft_and_has_its_own_status()
    {
        var rpc = new RecordingScreenWorkbench(); var settings = new Settings(rpc);
        var vm = New(rpc, settings); await vm.RefreshAsync();
        vm.Background = "#345678"; vm.Rotation = 90;
        vm.ContentDraftState.Should().Contain("尚未应用"); vm.LocalDraftState.Should().Contain("尚未保存");
        await vm.SaveDeviceSettingsCommand.ExecuteAsync();
        settings.Writes.Should().ContainSingle(); rpc.Writes.Should().BeEmpty();
        vm.Dirty.Should().BeTrue(); vm.DeviceSettingsDirty.Should().BeFalse(); vm.Background.Should().Be("#345678");
        vm.ContentDraftState.Should().Contain("尚未应用"); vm.EditorBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Applying_content_does_not_save_an_unsaved_device_direction()
    {
        var rpc = new RecordingScreenWorkbench(); var settings = new Settings(rpc);
        var vm = New(rpc, settings); await vm.RefreshAsync();
        vm.Background = "#345678"; vm.Rotation = 90;
        await vm.ApplyCommand.ExecuteAsync();
        rpc.Writes.Should().Equal("SetScreenScene"); settings.Writes.Should().BeEmpty();
        rpc.Scene.Assignments.Single(x => x.ScreenId == vm.Selected!.Id).Rotation.Should().Be(180);
        vm.DeviceSettingsDirty.Should().BeTrue(); vm.Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task Error_dismissal_keeps_draft_and_does_not_retry_or_write()
    {
        var rpc = new RecordingScreenWorkbench { Preview = _ => throw new InvalidOperationException("超预算：请修改素材后重试") };
        var vm = New(rpc); await vm.RefreshAsync(); vm.Background = "#334455";
        var dismissed = 0; vm.ErrorDismissed += (_, _) => dismissed++;
        await vm.PreviewCommand.ExecuteAsync(); vm.HasError.Should().BeTrue();
        vm.DismissErrorCommand.Execute(null);
        vm.HasError.Should().BeFalse(); vm.Dirty.Should().BeTrue(); vm.Background.Should().Be("#334455");
        dismissed.Should().Be(1);
        rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnection_disables_write_affordances_without_discarding_local_work()
    {
        var rpc = new RecordingScreenWorkbench(); var vm = New(rpc); await vm.RefreshAsync();
        vm.Background = "#334455"; vm.MarkDisconnected();
        vm.CanApplyContent.Should().BeFalse(); vm.CanGeneratePreview.Should().BeFalse();
        vm.ConnectionHint.Should().Contain("草稿保留").And.Contain("暂不可用");
        vm.ScreenDetail.Should().StartWith("上次读取");
        vm.Dirty.Should().BeTrue(); vm.HasSelection.Should().BeTrue(); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task No_device_does_not_present_apply_or_preview_as_ready()
    {
        var rpc = new RecordingScreenWorkbench { DeviceSnapshots = [] }; var vm = New(rpc); await vm.RefreshAsync();
        vm.HasSelection.Should().BeFalse(); vm.HasScreens.Should().BeFalse();
        vm.CanApplyContent.Should().BeFalse(); vm.CanGeneratePreview.Should().BeFalse();
        vm.ConnectionHint.Should().Contain("尚未发现"); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Accessible_device_text_contains_a_name_and_state_instead_of_the_DTO_dump()
    {
        var item = new ScreenChoice(new("screen:turzx:test", "test", "Turzx", 1920, 480, "Running", null, false, 5, 0, null, 8.8));
        item.ToString().Should().Contain("机箱长条屏").And.Contain("正在发送").And.NotContain("ScreenDeviceDto").And.NotContain("FramesWritten");
    }

    [Theory]
    [InlineData(2, 800, true)]
    [InlineData(2, 1055, true)]
    [InlineData(2, 1056, false)]
    [InlineData(2, 1920, false)]
    [InlineData(0, 800, false)]
    [InlineData(1, 800, false)]
    [InlineData(4, 800, false)]
    public void Compact_shell_is_limited_to_the_screen_pilot(int page, double width, bool expected) =>
        new ScreenShellCompactConverter().Convert([page, width], typeof(bool), null!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);

    private sealed class Settings(RecordingScreenWorkbench rpc) : IScreenDeviceSettingsService
    {
        public List<ScreenDeviceSettingsUpdateDto> Writes { get; } = [];
        public Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(new ScreenDeviceSettingsDto(id, 180, "legacy", false));
        public Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto value, CancellationToken ct = default)
        {
            Writes.Add(value);
            rpc.Scene = new(rpc.Scene.Assignments.Select(x => x.ScreenId == value.ScreenId ? x with { Rotation = value.Rotation } : x).ToArray());
            return Task.FromResult(new ScreenDeviceSettingsDto(value.ScreenId, value.Rotation, value.EncodingQuality, false));
        }
    }
}
