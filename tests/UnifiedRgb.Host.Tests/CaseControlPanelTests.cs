using System.Reflection;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Controlled fake RPC tasks deliberately cross the disconnect/request boundary.
public sealed class CaseControlPanelTests : IDisposable
{
    private readonly List<CasePowerActions> _owned = [];
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto Active = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly SceneDto Black = new([new([new("case:one", 0)], "StaticColor", ["#000000"], Brightness: 0)]);
    private static readonly ScreenSceneDto ScreenScene = new([new("screen:h2:test"), new("screen:turzx:test")]);
    private static ScreenDeviceDto Screen(string family, string state) => new($"screen:{family.ToLowerInvariant()}:test", family, family, 208, 64, state, null, true, 10, 0, null, 30);
    private static CaseControlDisplayState Describe(SceneDto scene, params ScreenDeviceDto[] screens) =>
        CaseControlPanelViewModel.Describe(scene, [Controller], ScreenScene, screens);

    [Fact]
    public void Off_request_does_not_claim_sleep_before_screen_feedback()
    {
        var state = Describe(Black, Screen("H2", "Sleeping"), Screen("Turzx", "Running"));
        state.State.Should().Be(CaseControlState.Pending);
        state.Detail.Should().Contain("1/2"); state.Title.Should().Be("等待屏幕休眠");
    }

    [Fact]
    public void All_configured_case_screens_must_report_sleeping()
    {
        var state = Describe(Black, Screen("H2", "Sleeping"), Screen("Turzx", "Sleeping"));
        state.State.Should().Be(CaseControlState.Blackout); state.Detail.Should().Contain("2/2");
    }

    [Fact]
    public void Restored_scene_with_sleeping_screens_is_still_pending()
    {
        Describe(Active, Screen("H2", "Sleeping"), Screen("Turzx", "Running")).State.Should().Be(CaseControlState.Pending);
        Describe(Active, Screen("H2", "Running"), Screen("Turzx", "Running")).State.Should().Be(CaseControlState.Active);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Blocked")]
    [InlineData("Disconnected")]
    [InlineData("Unsupported")]
    public void Screen_failures_are_not_reported_as_complete(string state) =>
        Describe(Active, Screen("H2", state), Screen("Turzx", "Running")).State.Should().Be(CaseControlState.Error);

    [Fact]
    public void Missing_configured_screen_does_not_disappear_from_the_denominator() =>
        Describe(Black, Screen("H2", "Sleeping")).State.Should().Be(CaseControlState.Unknown);

    [Fact]
    public void Control_display_and_disabled_screen_are_not_case_sleep_targets()
    {
        var scene = new ScreenSceneDto([new("screen:h2:test"), new("control:keyboard"), new("screen:turzx:disabled", Enabled: false)]);
        var state = CaseControlPanelViewModel.Describe(Black, [Controller], scene, [Screen("H2", "Sleeping")]);
        state.State.Should().Be(CaseControlState.Blackout); state.Detail.Should().Contain("1/1");
    }

    [Fact]
    public void Offline_lighting_controller_and_missing_screen_service_cannot_look_successful()
    {
        CaseControlPanelViewModel.Describe(Black, [], ScreenScene, []).State.Should().Be(CaseControlState.Error);
        CaseControlPanelViewModel.Describe(Black, [Controller], null, null).State.Should().Be(CaseControlState.Unknown);
        CaseControlPanelViewModel.Describe(new([]), [Controller], ScreenScene, []).State.Should().Be(CaseControlState.Unknown);
    }

    [Fact]
    public async Task Preview_refresh_is_read_only_and_never_sends_to_an_external_display()
    {
        var (vm, rpc, _) = Create();
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Active);
        rpc.Writes.Should().BeEmpty(); rpc.Loads.Should().Be(0);
        CaseControlPanelViewModel.TransportNote.Should().Contain("未发送");
    }

    [Fact]
    public async Task New_on_intent_preserves_running_scene_and_off_then_on_preserves_recovery_point()
    {
        var (vm, rpc, _) = Create();
        (await vm.EnsureOnAsync()).Changed.Should().BeFalse(); rpc.Writes.Should().BeEmpty();
        await vm.OffAsync();
        vm.Display.State.Should().Be(CaseControlState.Pending, "screens still report running after accepted off");
        await vm.OffAsync();
        rpc.Writes.Should().Equal("save", "set");
        await vm.EnsureOnAsync();
        rpc.Writes.Should().Equal("save", "set", "set"); rpc.Loads.Should().Be(1);
        vm.Display.State.Should().Be(CaseControlState.Active);
    }

    [Fact]
    public async Task Stale_and_disconnected_observations_are_not_displayed_as_current()
    {
        var time = DateTimeOffset.UtcNow;
        var (vm, _, _) = Create(() => time);
        await vm.RefreshAsync();
        time += TimeSpan.FromSeconds(11); vm.ExpireObservation();
        vm.Display.State.Should().Be(CaseControlState.Unknown); vm.Display.Title.Should().Be("状态已过期");
        await vm.RefreshAsync(); vm.SetConnected(false);
        vm.Display.State.Should().Be(CaseControlState.Unknown); vm.CanControl.Should().BeFalse();
        vm.OnCommand.CanExecute(null).Should().BeFalse(); vm.OffCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Late_read_after_disconnect_is_discarded()
    {
        var (vm, rpc, _) = Create();
        var hold = new TaskCompletionSource<SceneDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.ReadOverride = () => hold.Task;
        var read = vm.RefreshAsync(); vm.SetConnected(false);
        hold.SetResult(Active); await read;
        vm.Display.Title.Should().Be("后台未连接"); vm.ObservationNote.Should().Be("尚无有效回读");
    }

    [Fact]
    public async Task Scene_changed_during_multi_call_read_remains_pending()
    {
        var (vm, rpc, _) = Create();
        var count = 0;
        rpc.ReadOverride = () => Task.FromResult(++count == 1 ? Active : Black);
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Pending); vm.Display.Title.Should().Be("场景正在变化");
    }

    [Fact]
    public async Task Late_accepted_request_does_not_replace_disconnected_display()
    {
        var (vm, rpc, _) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeSave = async () => { entered.SetResult(); await release.Task; };
        var off = vm.OffAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SetConnected(false); release.SetResult(); await off;
        vm.Display.Title.Should().Be("后台未连接"); vm.Display.State.Should().Be(CaseControlState.Unknown);
    }

    [Fact]
    public async Task Read_failure_clears_previous_success_without_repeating_writes()
    {
        var (vm, rpc, errors) = Create(); await vm.RefreshAsync();
        rpc.ReadOverride = () => Task.FromException<SceneDto>(new IOException("fake read failed"));
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Unknown); errors.Should().HaveCount(1); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Actual_208_by_64_artwork_renders_chinese_states_in_wpf()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var states = new[] { new CaseControlDisplayState(CaseControlState.Unknown, "后台未连接", "不使用上次状态判断"),
                    Describe(Black, Screen("H2", "Sleeping"), Screen("Turzx", "Sleeping")),
                    Describe(Active, Screen("H2", "Running"), Screen("Turzx", "Running")) };
                foreach (var state in states)
                {
                    var display = new CaseControlDisplay { Width = 208, Height = 64, State = state };
                    display.Measure(new(208, 64)); display.Arrange(new(0, 0, 208, 64));
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(208, 64, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(display);
                    var bytes = new byte[208 * 64 * 4]; bitmap.CopyPixels(bytes, 208 * 4, 0);
                    bytes.Where((_, i) => i % 4 < 3).Count(b => b != 0).Should().BeGreaterThan(500);
                    if (Environment.GetEnvironmentVariable("UNIFIEDRGB_CONTROL_PANEL_QA_DIR") is { Length: > 0 } dir)
                    {
                        Directory.CreateDirectory(dir);
                        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(dir, state.State + ".png")); png.Save(file);
                    }
                }
                var view = new App.Views.CaseControlPanelView();
                view.Measure(new(300, 450)); view.Arrange(new(0, 0, 300, 450));
                view.ActualWidth.Should().Be(300, "the actual XAML must load its own scoped resources");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
    }

    private (CaseControlPanelViewModel Vm, CasePowerRpc Rpc, List<Exception> Errors) Create(Func<DateTimeOffset>? now = null)
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = Active; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = ScreenScene; screenRpc.Screens = [Screen("H2", "Running"), Screen("Turzx", "Running")];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var errors = new List<Exception>();
        return (new(actions, () => proxy, () => screens, InlineDispatcher.Instance, errors.Add, now), rpc, errors);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }
}

public class CasePanelScreenRpc : DispatchProxy
{
    public ScreenSceneDto Scene { get; set; } = new([]);
    public IReadOnlyList<ScreenDeviceDto> Screens { get; set; } = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IScreenService.GetScreenSceneAsync) => Task.FromResult(Scene),
        nameof(IScreenService.ListScreensAsync) => Task.FromResult(Screens),
        _ => throw new InvalidOperationException("Unexpected screen RPC: " + targetMethod?.Name),
    };
}
