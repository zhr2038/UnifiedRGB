using System.Reflection;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Whole-case OFF/ON lifecycle against in-memory fakes: queue cancellation and failure boundaries, answers lost after a
/// write, late completions across reconnects, conflicting entry points and what the local preview claims meanwhile.
/// Nothing here reaches a Host, a screen or a keyboard; "applied" means the fake's scene changed.
/// </summary>
#pragma warning disable VSTHRD003 // Controlled latches deliberately cross the queue, read and disconnect boundaries.
public sealed class CasePowerLifecycleTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly ScenePackageRefDto Package = new("content", "binding", "v1");
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto Running = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])], Package: Package);
    private static readonly SceneDto Later = new([new([new("case:one", 0)], "Rainbow", [])]);
    private static readonly SceneDto Black = new([new([new("case:one", 0)], "StaticColor", ["#000000"], Brightness: 0, Strategy: "Vendor")]);
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Cancelled_queued_request_sends_nothing_and_the_queue_keeps_working()
    {
        var (actions, rpc, _) = CreateActions();
        var (entered, release) = (Latch(), Latch());
        rpc.Before = async (call, _) => { if (call == "save") { entered.TrySetResult(); await release.Task; } };
        var off = actions.OffAsync();
        await entered.Task.WaitAsync(Wait);
        using var stop = new CancellationTokenSource();
        var queued = actions.EnsureOnAsync(stop.Token);
        await stop.CancelAsync();
        await FluentActions.Awaiting(() => queued.WaitAsync(Wait)).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        (await off.WaitAsync(Wait)).Changed.Should().BeTrue();
        rpc.Log.Should().Equal("get", "list", "save", "set");
        actions.Status.State.Should().Be(CasePowerRequestState.OffRequested, "the cancelled waiter never owned the queue");
        (await actions.EnsureOnAsync().WaitAsync(Wait)).Changed.Should().BeTrue();
        rpc.Scene.Should().BeEquivalentTo(Running);
    }

    [Fact]
    public async Task Unavailable_host_fails_before_any_write_and_releases_the_queue()
    {
        var connected = false;
        var (actions, rpc, _) = CreateActions(() => connected);
        await FluentActions.Awaiting(() => actions.OffAsync()).Should().ThrowAsync<InvalidOperationException>();
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);
        rpc.Log.Should().BeEmpty();
        connected = true;
        (await actions.OffAsync().WaitAsync(Wait)).Changed.Should().BeTrue();
        rpc.Backup!.Scene.Should().BeEquivalentTo(Running);
    }

    [Fact]
    public async Task Off_saves_the_scene_running_when_it_leaves_the_queue_not_an_earlier_observation()
    {
        var (vm, rpc, _, _) = CreatePanel();
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Active);
        rpc.Scene = Later; // another editor applied newer content after the panel's last read
        (await vm.OffAsync()).Changed.Should().BeTrue();
        rpc.Backup!.Scene.Should().BeEquivalentTo(new SceneDto(Later.Assignments));
        rpc.Submitted.Should().OnlyContain(s => s.Screens == null);
    }

    [Fact]
    public async Task Blackout_applied_without_an_answer_keeps_the_first_recovery_point_on_retry()
    {
        var (actions, rpc, _) = CreateActions();
        rpc.LoseSetAnswer = () => new StreamJsonRpc.ConnectionLostException();
        await FluentActions.Awaiting(() => actions.OffAsync()).Should().ThrowAsync<StreamJsonRpc.ConnectionLostException>();
        CasePowerActions.IsBlackout(rpc.Scene).Should().BeTrue("the fake applied the write before the answer was lost");
        actions.Status.State.Should().Be(CasePowerRequestState.Unconfirmed);
        rpc.LoseSetAnswer = null;
        (await actions.OffAsync()).Changed.Should().BeFalse();
        rpc.Log.Count(x => x == "save").Should().Be(1, "the retry must not replace the recovery point with the blackout");
        (await actions.EnsureOnAsync()).Changed.Should().BeTrue();
        rpc.Scene.Assignments.Should().BeEquivalentTo(Running.Assignments, o => o.WithStrictOrdering());
        rpc.Scene.Package.Should().Be(Package);
    }

    [Fact]
    public async Task Write_cancelled_after_it_was_sent_is_not_reported_as_not_done()
    {
        var (actions, rpc, _) = CreateActions();
        using var stop = new CancellationTokenSource();
        rpc.LoseSetAnswer = () => { stop.Cancel(); return new OperationCanceledException(stop.Token); };
        await FluentActions.Awaiting(() => actions.OffAsync(stop.Token)).Should().ThrowAsync<OperationCanceledException>();
        CasePowerActions.IsBlackout(rpc.Scene).Should().BeTrue("the host applied the blackout before the caller stopped waiting");
        actions.Status.State.Should().Be(CasePowerRequestState.Unconfirmed, "the outcome is unknown, not a confirmed failure");
        actions.Status.Message.Should().NotContain("未完成");
    }

    [Fact]
    public async Task Failure_before_the_scene_write_is_a_genuine_failure_and_keeps_the_scene()
    {
        var (actions, rpc, _) = CreateActions();
        using var stop = new CancellationTokenSource();
        rpc.Before = async (call, ct) => { if (call == "save") { await stop.CancelAsync(); ct.ThrowIfCancellationRequested(); } };
        await FluentActions.Awaiting(() => actions.OffAsync(stop.Token)).Should().ThrowAsync<OperationCanceledException>();
        rpc.Scene.Should().BeSameAs(Running);
        rpc.Log.Should().NotContain("set");
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Late_success_after_a_reconnect_ends_on_a_fresh_readback()
    {
        var (vm, rpc, _, errors) = CreatePanel();
        var (entered, release) = (Latch(), Latch());
        rpc.Before = async (call, _) => { if (call == "save") { entered.TrySetResult(); await release.Task; } };
        var off = vm.OffAsync();
        await entered.Task.WaitAsync(Wait);
        vm.SetConnected(false); vm.SetConnected(true);
        vm.CanControl.Should().BeFalse("the earlier request still owns the queue");
        vm.Display.Title.Should().Be("等待读取");
        release.SetResult();
        await off.WaitAsync(Wait);
        vm.Display.Should().Be(new CaseControlDisplayState(CaseControlState.Pending, "等待屏幕休眠", "屏幕 0/1 上报休眠"));
        vm.ObservationNote.Should().Contain("非光学确认");
        vm.CanControl.Should().BeTrue(); errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Late_failure_after_a_reconnect_is_not_shown_as_the_current_error()
    {
        var (vm, rpc, _, _) = CreatePanel();
        var (entered, release) = (Latch(), Latch());
        rpc.Before = async (call, _) =>
        {
            if (call != "set") return;
            entered.TrySetResult(); await release.Task;
            throw new IOException("rejected before applying");
        };
        var off = vm.OffAsync();
        await entered.Task.WaitAsync(Wait);
        vm.SetConnected(false); vm.SetConnected(true);
        release.SetResult();
        await FluentActions.Awaiting(() => off.WaitAsync(Wait)).Should().ThrowAsync<IOException>();
        vm.Display.State.Should().NotBe(CaseControlState.Error, "the failure belongs to the previous connection");
        vm.CanControl.Should().BeTrue();
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Active);
        rpc.Scene.Should().BeSameAs(Running);
    }

    [Fact]
    public async Task Top_bar_restore_during_panel_off_is_queued_and_the_panel_stays_locked()
    {
        var (vm, rpc, _, errors) = CreatePanel();
        var (entered, release) = (Latch(), Latch());
        rpc.Before = async (call, _) => { if (call == "save") { entered.TrySetResult(); await release.Task; } };
        var off = vm.OffCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Wait);
        vm.OffCommand.CanExecute(null).Should().BeFalse(); vm.OnCommand.CanExecute(null).Should().BeFalse();
        var restore = vm.RestoreBackupAsync();
        vm.Display.Title.Should().Be("正在恢复…");
        release.SetResult();
        await Task.WhenAll(off, restore).WaitAsync(Wait);
        rpc.Log.Where(x => x is "save" or "set" or "load").Should().Equal("save", "set", "load", "set");
        rpc.Scene.Should().BeEquivalentTo(Running);
        vm.Display.State.Should().Be(CaseControlState.Active);
        vm.CanControl.Should().BeTrue(); errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_that_needed_no_write_never_claims_a_submitted_command(bool off)
    {
        var (vm, rpc, _, _) = CreatePanel();
        if (off) rpc.Scene = Black;
        var shown = Record(vm);
        var result = off ? await vm.OffAsync() : await vm.EnsureOnAsync();
        result.Changed.Should().BeFalse();
        rpc.Log.Should().NotContain(["save", "set", "load"]);
        shown.Select(d => d.Title).Should().NotContain("指令已提交", "nothing was sent to the Host");
    }

    [Fact]
    public async Task Observation_started_before_a_request_is_never_displayed_after_it()
    {
        var (vm, _, screens, _) = CreatePanel();
        var (entered, release) = (Latch(), Latch());
        var first = true;
        screens.Before = async call =>
        {
            if (call != "screens" || !first) return;
            first = false; entered.TrySetResult(); await release.Task;
        };
        var read = vm.RefreshAsync();
        await entered.Task.WaitAsync(Wait);
        var shown = Record(vm);
        await vm.OffAsync().WaitAsync(Wait);
        release.SetResult();
        await read.WaitAsync(Wait);
        shown.Should().NotContain(d => d.State == CaseControlState.Active, "that read began before the blackout was sent");
        await vm.RefreshAsync();
        vm.Display.Title.Should().Be("等待屏幕休眠");
    }

    [Fact]
    public async Task Answer_lost_after_the_write_is_read_back_instead_of_reported_as_not_done()
    {
        var (vm, rpc, _, errors) = CreatePanel();
        rpc.LoseSetAnswer = () => new TimeoutException("no answer after the write");
        var shown = Record(vm);
        await vm.OffCommand.ExecuteAsync();
        errors.Should().ContainSingle().Which.Should().BeOfType<TimeoutException>("the banner still reports the missing answer");
        shown.Select(d => d.Title).Should().NotContain("操作未完成", "the blackout may have been applied");
        vm.Display.Should().Be(new CaseControlDisplayState(CaseControlState.Pending, "等待屏幕休眠", "屏幕 0/1 上报休眠"));
        vm.ObservationNote.Should().Contain("非光学确认");
    }

    [Fact]
    public async Task Rejected_off_reports_an_error_until_the_next_read_shows_the_unchanged_scene()
    {
        var (vm, rpc, _, errors) = CreatePanel();
        rpc.Before = (call, _) => call == "set" ? Task.FromException(new IOException("rejected before applying")) : Task.CompletedTask;
        await vm.OffCommand.ExecuteAsync();
        errors.Should().ContainSingle().Which.Should().BeOfType<IOException>();
        vm.Display.State.Should().Be(CaseControlState.Error);
        vm.ObservationNote.Should().Be("尚无有效回读");
        rpc.Before = null;
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Active);
        rpc.Scene.Should().BeSameAs(Running);
    }

    [Fact]
    public async Task Missing_recovery_point_leaves_the_blackout_observable_and_writes_nothing()
    {
        var (vm, rpc, screens, errors) = CreatePanel();
        rpc.Scene = Black; rpc.Backup = null;
        screens.Screens = [Screen("Sleeping")];
        await vm.OnCommand.ExecuteAsync();
        errors.Should().ContainSingle().Which.Should().BeOfType<FileNotFoundException>();
        vm.Display.State.Should().Be(CaseControlState.Error);
        rpc.Log.Should().NotContain(["save", "set"]);
        await vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Blackout);
    }

    [Fact]
    public async Task Disposed_queue_refuses_panel_requests_without_writing()
    {
        var (vm, rpc, _, errors) = CreatePanel();
        _owned[^1].Dispose();
        await vm.OffCommand.ExecuteAsync();
        errors.Should().ContainSingle().Which.Should().BeOfType<ObjectDisposedException>();
        rpc.Log.Should().NotContain(["save", "set", "load"]);
        vm.CanControl.Should().BeTrue("a refused request must not leave the panel locked");
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private static TaskCompletionSource Latch() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ScreenDeviceDto Screen(string state) => new("screen:h2:test", "H2", "H2", 480, 480, state, null, true, 10, 0, null, 30);

    private static List<CaseControlDisplayState> Record(CaseControlPanelViewModel vm)
    {
        var shown = new List<CaseControlDisplayState>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Display)) lock (shown) shown.Add(vm.Display); };
        return shown;
    }

    private (CasePowerActions Actions, CaseLifecycleRgb Rpc, IRgbService Proxy) CreateActions(Func<bool>? connected = null)
    {
        var proxy = DispatchProxy.Create<IRgbService, CaseLifecycleRgb>();
        var rpc = (CaseLifecycleRgb)(object)proxy;
        rpc.Scene = Running; rpc.Controllers = [Controller];
        var actions = new CasePowerActions(() => connected?.Invoke() == false ? throw new InvalidOperationException("Host not connected.") : proxy);
        _owned.Add(actions);
        return (actions, rpc, proxy);
    }

    private (CaseControlPanelViewModel Vm, CaseLifecycleRgb Rpc, CaseLifecycleScreens Screens, List<Exception> Errors) CreatePanel()
    {
        var (actions, rpc, proxy) = CreateActions();
        var screenProxy = DispatchProxy.Create<IScreenService, CaseLifecycleScreens>();
        var screens = (CaseLifecycleScreens)(object)screenProxy;
        screens.Scene = new([new("screen:h2:test")]); screens.Screens = [Screen("Running")];
        var errors = new List<Exception>();
        return (new(actions, () => proxy, () => screenProxy, InlineDispatcher.Instance, e => { lock (errors) errors.Add(e); }), rpc, screens, errors);
    }
}

/// <summary>In-memory RGB RPC: every call is logged on arrival; Before may hold, cancel or reject it before it has any effect.</summary>
public class CaseLifecycleRgb : DispatchProxy
{
    public SceneDto Scene { get; set; } = new([]);
    public IReadOnlyList<ControllerDto> Controllers { get; set; } = [];
    public ProfileDto? Backup { get; set; }
    public List<string> Log { get; } = [];
    public List<SceneDto> Submitted { get; } = [];
    public Func<string, CancellationToken, Task>? Before { get; set; }

    /// <summary>SetScene applies, then its answer is replaced by this failure (lost, timed out or cancelled response).</summary>
    public Func<Exception>? LoseSetAnswer { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var ct = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
        return targetMethod?.Name switch
        {
            nameof(IRgbService.GetSceneAsync) => AnswerAsync("get", () => Scene, ct),
            nameof(IRgbService.ListControllersAsync) => AnswerAsync("list", () => Controllers, ct),
            nameof(IRgbService.LoadProfileAsync) => AnswerAsync("load", () => Backup ?? throw new FileNotFoundException("no recovery point"), ct),
            nameof(IRgbService.SaveProfileAsync) => RunAsync("save", () => Backup = (ProfileDto)args![0]!, ct),
            nameof(IRgbService.SetSceneAsync) => RunAsync("set", () =>
            {
                var next = (SceneDto)args![0]!;
                Submitted.Add(next);
                Scene = next.Screens is null ? next with { Screens = Scene.Screens } : next;
                if (LoseSetAnswer?.Invoke() is { } lost) throw lost;
            }, ct),
            _ => throw new InvalidOperationException("Unexpected fake RPC: " + targetMethod?.Name),
        };
    }

    private async Task<T> AnswerAsync<T>(string call, Func<T> answer, CancellationToken ct)
    {
        lock (Log) Log.Add(call);
        if (Before is { } before) await before(call, ct);
        return answer();
    }

    private async Task RunAsync(string call, Action apply, CancellationToken ct)
    {
        lock (Log) Log.Add(call);
        if (Before is { } before) await before(call, ct);
        apply();
    }
}

/// <summary>In-memory screen RPC for the panel's read-only observation.</summary>
public class CaseLifecycleScreens : DispatchProxy
{
    public ScreenSceneDto Scene { get; set; } = new([]);
    public IReadOnlyList<ScreenDeviceDto> Screens { get; set; } = [];
    public Func<string, Task>? Before { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IScreenService.GetScreenSceneAsync) => AnswerAsync("screen-scene", () => Scene),
        nameof(IScreenService.ListScreensAsync) => AnswerAsync("screens", () => Screens),
        _ => throw new InvalidOperationException("Unexpected screen RPC: " + targetMethod?.Name),
    };

    private async Task<T> AnswerAsync<T>(string call, Func<T> answer)
    {
        if (Before is { } before) await before(call);
        return answer();
    }
}
