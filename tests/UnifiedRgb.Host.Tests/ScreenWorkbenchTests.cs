using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Test-owned latches intentionally defer RPC completion until after another UI action.

public sealed class ScreenWorkbenchTests
{
    [Fact]
    public async Task Editing_and_saving_a_dashboard_keeps_its_authored_region()
    {
        var service = new RecordingScreenWorkbench();
        var region = new ScreenOverlayLayoutDto(.50, .20, .47, .66, 2, 0);
        service.Scene = new(service.Scene.Assignments.Select(a => a.ScreenId == "screen:turzx:test"
            ? a with { Overlay = "dashboard", OverlayLayout = region } : a).ToArray());
        var vm = New(service); await vm.RefreshAsync();
        vm.Selected = vm.Screens.Single(s => s.Id == "screen:turzx:test");
        vm.Brightness = 72; vm.Fps = 12;
        vm.Build().OverlayLayout.Should().Be(region);
        await vm.ApplyCommand.ExecuteAsync();
        var saved = service.Scene.Assignments.Single(a => a.ScreenId == "screen:turzx:test");
        saved.OverlayLayout.Should().Be(region);
        saved.Brightness.Should().Be(.72); saved.Fps.Should().Be(12);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("compact")]
    public async Task Explicitly_switching_away_from_dashboard_clears_the_incompatible_region(string mode)
    {
        var service = new RecordingScreenWorkbench();
        service.Scene = new(service.Scene.Assignments.Select(a => a.ScreenId == "screen:turzx:test"
            ? a with { Overlay = "dashboard", OverlayLayout = new(.50, .20, .47, .66, 2, 0) } : a).ToArray());
        var vm = New(service); await vm.RefreshAsync();
        vm.Selected = vm.Screens.Single(s => s.Id == "screen:turzx:test");
        vm.Overlay = vm.Overlays.Single(o => o.Id == mode);
        vm.Build().OverlayLayout.Should().BeNull();
        await vm.ApplyCommand.ExecuteAsync();
        service.Scene.Assignments.Single(a => a.ScreenId == "screen:turzx:test").OverlayLayout.Should().BeNull();
    }

    [Fact]
    public async Task Constructing_reading_selecting_and_editing_screen_drafts_never_writes()
    {
        var service = new RecordingScreenWorkbench();
        var vm = New(service); service.Calls.Should().BeEmpty();
        await vm.RefreshAsync();
        vm.Background = "#FF0000"; vm.Overlay = vm.Overlays[2]; vm.Rotation = 90;
        vm.Dirty.Should().BeTrue(); vm.Available.Should().BeTrue();
        service.Writes.Should().BeEmpty();
        vm.Build().Overlay.Should().Be("dashboard");
        vm.Build().Rotation.Should().Be(90);
    }

    [Fact]
    public async Task Applying_selected_screen_keeps_other_screen_content()
    {
        var service = new RecordingScreenWorkbench(); var vm = New(service);
        await vm.RefreshAsync(); vm.Background = "#112233";
        await vm.ApplyCommand.ExecuteAsync();
        service.Scene.Assignments.Should().HaveCount(2);
        service.Scene.Assignments.Single(a => a.ScreenId == "screen:h2:test").BackgroundColor.Should().Be("#112233");
        service.Scene.Assignments.Single(a => a.ScreenId == "screen:turzx:test").BackgroundColor.Should().Be("#ABCDEF");
        service.Writes.Should().Equal("SetScreenScene"); vm.Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task Newer_screen_edit_is_not_overwritten_and_errors_are_visible()
    {
        var service = new RecordingScreenWorkbench(); Exception? error = null;
        var vm = new ScreensViewModel(() => service, InlineDispatcher.Instance, ex => error = ex, () => null);
        await vm.RefreshAsync(); vm.Background = "#112233";
        service.Scene = new(service.Scene.Assignments.Select(a => a.ScreenId == "screen:h2:test" ? a with { Overlay = "dashboard" } : a).ToArray());
        await vm.ApplyCommand.ExecuteAsync();
        error.Should().BeOfType<InvalidOperationException>(); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnect_and_refresh_preserve_unapplied_screen_draft_and_unavailable_values()
    {
        var service = new RecordingScreenWorkbench(); var vm = New(service);
        await vm.RefreshAsync(); vm.Telemetry.Should().Contain("不可用").And.NotContain("GPU 0");
        vm.Background = "#001122";
        await vm.RefreshOptionalAsync(); await vm.RefreshAsync();
        vm.Background.Should().Be("#001122"); vm.Dirty.Should().BeTrue(); service.Writes.Should().BeEmpty();
        vm.MarkDisconnected(); vm.Available.Should().BeFalse(); vm.Message.Should().Contain("未连接");
    }

    [Fact]
    public async Task Stale_PC_metrics_are_labelled_stale_instead_of_current()
    {
        var service = new RecordingScreenWorkbench { Time = DateTimeOffset.UtcNow.AddMinutes(-1) }; var vm = New(service);
        await vm.RefreshAsync(); vm.Telemetry.Should().Contain("已过期");
    }

    [Theory]
    [InlineData("Disabled", "输出未开启")]
    [InlineData("Running", "正在发送")]
    [InlineData("Blocked", "被其他程序占用")]
    public void Screen_status_does_not_equate_sends_with_optical_acceptance(string state, string label)
    {
        ScreenChoice.StateName(state).Should().Be(label);
        var screen = new ScreenChoice(new("screen:tl-lcd:test", "test", "TlLcd", 400, 400, state, null, false, 0, 0, null, 0));
        screen.Name.Should().NotContain("左").And.NotContain("右");
    }

    [Fact]
    public async Task Lcd_restart_preserves_draft_and_other_content_and_has_a_cooldown()
    {
        var service = WithFan(); var now = DateTimeOffset.UtcNow;
        var vm = new ScreensViewModel(() => service, InlineDispatcher.Instance, ex => throw ex, () => null, _ => false, () => now);
        await vm.RefreshAsync(); vm.Background = "#001122"; var original = service.Scene;
        await vm.RestartLcdCommand.ExecuteAsync();
        service.Writes.Should().Equal("RestartFanLcd"); service.Scene.Should().BeSameAs(original);
        vm.Background.Should().Be("#001122"); vm.Dirty.Should().BeTrue();
        vm.Message.Should().Contain("请确认实际画面"); vm.CanRestartLcd.Should().BeFalse();
        await vm.RestartLcdCommand.ExecuteAsync(); service.Writes.Should().HaveCount(1);
        now = now.AddSeconds(61); await vm.PollAsync(); vm.CanRestartLcd.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_click_during_restart_does_not_issue_another_command()
    {
        var service = WithFan(); var completion = new TaskCompletionSource<ScreenRestartDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Restart = () => completion.Task; var vm = New(service); await vm.RefreshAsync();
        var first = vm.RestartLcdCommand.ExecuteAsync();
        vm.CanChangeOutput.Should().BeFalse(); vm.RestartLcdText.Should().Contain("正在重启");
        await vm.RestartLcdCommand.ExecuteAsync(); service.Writes.Should().Equal("RestartFanLcd");
        completion.SetResult(new(true, true, 1, ["screen:tl-lcd:test"], "accepted")); await first;
        vm.CanChangeOutput.Should().BeTrue();
    }

    [Fact]
    public async Task Unconfirmed_restart_is_reported_without_claiming_recovery()
    {
        var service = WithFan(); service.Restart = () => Task.FromResult(new ScreenRestartDto(false, false, 3, ["screen:tl-lcd:test"], "no ack"));
        var vm = New(service); await vm.RefreshAsync(); await vm.RestartLcdCommand.ExecuteAsync();
        vm.Message.Should().Contain("未获接收端确认").And.NotContain("已重新连接");
        vm.CanChangeOutput.Should().BeTrue(); vm.CanRestartLcd.Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_outputs_and_disconnected_Host_cannot_restart_screens()
    {
        var service = WithFan(); service.DeviceSnapshots = service.DeviceSnapshots.Select(s => s with { State = "Disabled" }).ToArray();
        var vm = New(service); await vm.RefreshAsync(); await vm.RestartLcdCommand.ExecuteAsync(); service.Writes.Should().BeEmpty();
        service.DeviceSnapshots = service.DeviceSnapshots.Select(s => s with { State = "Running" }).ToArray();
        await vm.PollAsync(); vm.CanRestartLcd.Should().BeTrue(); vm.MarkDisconnected();
        await vm.RestartLcdCommand.ExecuteAsync(); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_updates_unselected_device_errors_without_discarding_draft()
    {
        var service = WithFan(); var vm = New(service); await vm.RefreshAsync(); vm.Background = "#112233";
        service.DeviceSnapshots = service.DeviceSnapshots.Select(s => s.Family == "TlLcd" ? s with { State = "Error", LastError = "timeout" } : s).ToArray();
        await vm.PollAsync();
        vm.Screens.Single(s => s.Dto.Family == "TlLcd").Detail.Should().Contain("通信异常");
        vm.Selected!.Id.Should().Be("screen:h2:test"); vm.Dirty.Should().BeTrue(); vm.Background.Should().Be("#112233");
        service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_preserves_a_selected_unplugged_screen_draft_and_labels_it_disconnected()
    {
        var service = WithFan(); var vm = New(service); await vm.RefreshAsync(); vm.Background = "#112233";
        service.DeviceSnapshots = service.DeviceSnapshots.Where(s => s.Id != "screen:h2:test").ToArray();
        await vm.PollAsync();
        vm.Selected!.Dto.State.Should().Be("Disconnected"); vm.ScreenDetail.Should().Contain("未连接");
        vm.Background.Should().Be("#112233"); vm.Dirty.Should().BeTrue(); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnect_restores_commands_and_polling_while_keeping_a_dirty_screen_draft()
    {
        var service = WithFan(); var vm = New(service); await vm.RefreshAsync(); vm.Background = "#112233";
        vm.MarkDisconnected(); vm.Available.Should().BeFalse();
        await vm.RefreshOptionalAsync();
        vm.Available.Should().BeTrue(); vm.CanChangeOutput.Should().BeTrue(); vm.CanRestartLcd.Should().BeTrue();
        vm.Background.Should().Be("#112233"); vm.Dirty.Should().BeTrue(); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_slow_screen_refresh_does_not_overwrite_a_new_background_edit()
    {
        var service = WithFan(); var vm = New(service); await vm.RefreshAsync();
        var read = new TaskCompletionSource<ScreenSceneDto>(TaskCreationOptions.RunContinuationsAsynchronously); service.ReadScene = () => read.Task;
        var pending = vm.RefreshAsync(); vm.Background = "#112233"; read.SetResult(service.Scene); await pending;
        vm.Background.Should().Be("#112233"); vm.Dirty.Should().BeTrue(); vm.Available.Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_asset_A_does_not_remove_B_selected_while_the_RPC_waits()
    {
        var service = WithFan();
        service.AssetRows = [new("aaaaaaaaaaaaaaaa", "A", 8, 8, 1, 0, "image"), new("bbbbbbbbbbbbbbbb", "B", 8, 8, 1, 0, "image")];
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Delete = _ => deleted.Task;
        var vm = new ScreensViewModel(() => service, InlineDispatcher.Instance, ex => throw ex, () => null, _ => true);
        await vm.RefreshAsync(); vm.Asset = vm.Assets[1];
        var pending = vm.RemoveAssetCommand.ExecuteAsync(); vm.Asset = vm.Assets[2]; deleted.SetResult(); await pending;
        vm.Asset!.Dto!.Name.Should().Be("B"); vm.Assets.Should().Contain(a => a.Dto != null && a.Dto.Name == "B");
        vm.Assets.Should().NotContain(a => a.Dto != null && a.Dto.Name == "A"); vm.Dirty.Should().BeTrue();
    }

    private static RecordingScreenWorkbench WithFan()
    {
        var service = new RecordingScreenWorkbench();
        service.DeviceSnapshots = service.DeviceSnapshots.Append(new ScreenDeviceDto("screen:tl-lcd:test", "TL", "TlLcd", 400, 400, "Running", null, true, 12, 0, null, 5)).ToArray();
        return service;
    }

    private static ScreensViewModel New(RecordingScreenWorkbench service) => new(() => service, InlineDispatcher.Instance, ex => throw ex, () => null, _ => false);
}

internal sealed class RecordingScreenWorkbench : IScreenService
{
    public List<string> Calls { get; } = [];
    public List<string> Writes { get; } = [];
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public ScreenSceneDto Scene { get; set; } = new([new("screen:h2:test"), new("screen:turzx:test", BackgroundColor: "#ABCDEF")]);
    public IReadOnlyList<ScreenDeviceDto> DeviceSnapshots { get; set; } = [new("screen:h2:test", "H2", "H2", 480, 480, "Disabled", null, false, 0, 0, null, 0), new("screen:turzx:test", "TURZX", "Turzx", 1920, 480, "Disabled", null, false, 0, 0, null, 0)];
    public Func<Task<ScreenRestartDto>> Restart { get; set; } = () => Task.FromResult(new ScreenRestartDto(true, true, 1, ["screen:tl-lcd:test"], "accepted"));
    public IReadOnlyList<ScreenAssetDto> AssetRows { get; set; } = [];
    public Func<Task<ScreenSceneDto>>? ReadScene { get; set; }
    public Func<string, Task> Delete { get; set; } = _ => Task.CompletedTask;
    public Func<ScreenAssignmentDto, Task<ScreenPreviewDto>> Preview { get; set; } = _ => throw new NotSupportedException();
    public Task<IReadOnlyList<ScreenDeviceDto>> ListScreensAsync(CancellationToken ct = default)
    {
        Calls.Add("ListScreens");
        return Task.FromResult(DeviceSnapshots);
    }
    public Task<PcTelemetryDto> GetPcTelemetryAsync(CancellationToken ct = default) => Task.FromResult(new PcTelemetryDto(Time, 20, 1024, 4096, null, null, null, null, "test", null));
    public Task<IReadOnlyList<ScreenAssetDto>> ListScreenAssetsAsync(CancellationToken ct = default) => Task.FromResult(AssetRows);
    public Task<ScreenAssetDto> UploadScreenAssetAsync(ScreenAssetUploadDto asset, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteScreenAssetAsync(string assetId, CancellationToken ct = default) { Writes.Add("DeleteScreenAsset:" + assetId); return Delete(assetId); }
    public Task<ScreenPreviewDto> PreviewScreenAsync(ScreenAssignmentDto assignment, CancellationToken ct = default) => Preview(assignment);
    public Task<ScreenSceneDto> GetScreenSceneAsync(CancellationToken ct = default) { Calls.Add("GetScreenScene"); return ReadScene?.Invoke() ?? Task.FromResult(Scene); }
    public Task SetScreenSceneAsync(ScreenSceneDto scene, CancellationToken ct = default) { Writes.Add("SetScreenScene"); Scene = scene; return Task.CompletedTask; }
    public Task StopScreensAsync(CancellationToken ct = default) { Writes.Add("StopScreens"); return Task.CompletedTask; }
    public Task<ScreenRestartDto> RestartFanLcdAsync(CancellationToken ct = default) { Writes.Add("RestartFanLcd"); return Restart(); }
}
