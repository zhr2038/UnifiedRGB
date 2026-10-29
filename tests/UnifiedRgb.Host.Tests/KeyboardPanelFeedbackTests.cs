using System.Collections.Concurrent;
using System.Reflection;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>Fake-RPC ordering, deadline and compatibility checks for the control panel's keyboard-display feedback.</summary>
public sealed class KeyboardPanelFeedbackTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const string Disconnected = "键盘状态未知，等待新的后台回读。";
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto Active = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Late_refresh_status_cannot_overwrite_newer_toggle_readback()
    {
        var (vm, keyboard, _, _) = Create(Long);
        var late = Hold(keyboard);
        var refresh = vm.RefreshAsync();
        await Toggle(vm);
        keyboard.Writes.Should().Equal(true);
        vm.KeyboardToggleLabel.Should().Be("恢复键盘原动画");
        vm.KeyboardTransportNote.Should().Contain("已确认 9 幅画面");

        late.SetResult(KeyboardStatus(false, 3));
        await refresh.WaitAsync(Guard);
        vm.KeyboardToggleLabel.Should().Be("恢复键盘原动画", "the refresh read started before the toggle and is older");
        vm.KeyboardTransportNote.Should().Contain("已确认 9 幅画面").And.NotContain("已确认 3");
        keyboard.Writes.Should().Equal(true);
    }

    [Fact]
    public async Task Toggle_preflight_retires_an_earlier_refresh_read()
    {
        var (vm, keyboard, _, _) = Create(Long);
        await vm.RefreshAsync();
        var late = Hold(keyboard);
        var preflight = Hold(keyboard);
        var refresh = vm.RefreshAsync();
        var toggle = vm.ToggleKeyboardDisplayCommand.ExecuteAsync();

        late.SetResult(KeyboardStatus(false, 5));
        await refresh.WaitAsync(Guard);
        vm.KeyboardTransportNote.Should().Contain("已确认 3 幅画面").And.NotContain("已确认 5");

        preflight.SetResult(KeyboardStatus(false, 6));
        await toggle.WaitAsync(Guard);
        keyboard.Writes.Should().Equal(true);
        vm.KeyboardTransportNote.Should().Contain("已确认 9 幅画面");
    }

    [Fact]
    public async Task Refresh_during_toggle_neither_reads_keyboard_nor_replaces_pending_feedback()
    {
        var (vm, keyboard, _, _) = Create(Long);
        await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        keyboard.WriteOverride = async (enabled, _) => { entered.SetResult(); await release.Task; keyboard.Apply(enabled); };
        var toggle = vm.ToggleKeyboardDisplayCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);

        var reads = keyboard.Reads;
        await vm.RefreshAsync().WaitAsync(Guard);
        keyboard.Reads.Should().Be(reads, "the in-flight toggle owns the keyboard feedback until its own readback");
        vm.KeyboardTransportNote.Should().Contain("正在请求开启").And.NotContain("已确认");
        vm.Display.State.Should().Be(CaseControlState.Active, "the case readback still refreshes");

        release.SetResult();
        await toggle.WaitAsync(Guard);
        keyboard.Writes.Should().Equal(true);
        vm.KeyboardTransportNote.Should().Contain("已确认 9 幅画面");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_and_reconnect_reject_late_keyboard_status(bool reconnect)
    {
        var (vm, keyboard, _, _) = Create(Long);
        var late = Hold(keyboard);
        var refresh = vm.RefreshAsync();
        vm.SetConnected(false);
        if (reconnect) vm.SetConnected(true);

        late.SetResult(KeyboardStatus(true, 4));
        await refresh.WaitAsync(Guard);
        vm.HasKeyboardDisplayService.Should().BeFalse();
        vm.KeyboardTransportNote.Should().Be(Disconnected);
        if (!reconnect) return;

        await vm.RefreshAsync();
        vm.KeyboardTransportNote.Should().Contain("已确认 3 幅画面", "a read started on the new connection is current");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_returning_after_disconnect_never_writes_through_a_stale_connection(bool reconnect)
    {
        var (vm, keyboard, _, _) = Create(Long);
        await vm.RefreshAsync();
        var preflight = Hold(keyboard);
        var toggle = vm.ToggleKeyboardDisplayCommand.ExecuteAsync();
        vm.SetConnected(false);
        if (reconnect) vm.SetConnected(true);

        preflight.SetResult(KeyboardStatus(false, 3));
        await toggle.WaitAsync(Guard);
        keyboard.Writes.Should().BeEmpty();
        vm.KeyboardTransportNote.Should().Be(Disconnected);
        vm.HasKeyboardDisplayService.Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_write_never_moves_to_a_newer_connection_proxy()
    {
        IKeyboardDisplayService current = null!;
        var (vm, oldPipe, _, _) = Create(Long, () => current);
        current = oldPipe;
        await vm.RefreshAsync();
        var preflight = Hold(oldPipe);
        var toggle = vm.ToggleKeyboardDisplayCommand.ExecuteAsync();

        // The pipe reconnects before the view model observes the connection change.
        var newPipe = new FakeKeyboard();
        current = newPipe;
        oldPipe.WriteOverride = (_, _) => Task.FromException(new ConnectionLostException("fake old pipe closed"));
        oldPipe.ReadOverride = _ => Task.FromException<KeyboardDisplayStatusDto>(new ConnectionLostException("fake old pipe closed"));
        preflight.SetResult(KeyboardStatus(false, 3));
        await toggle.WaitAsync(Guard);

        newPipe.Writes.Should().BeEmpty("the decision was read on the old connection");
        newPipe.Reads.Should().Be(0);
        oldPipe.Writes.Should().Equal(true);
        vm.KeyboardTransportNote.Should().Contain("结果未知").And.NotContain("已确认");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_deadline_or_failure_clears_confirmed_frames_and_a_later_read_recovers(bool hang)
    {
        var (vm, keyboard, errors, _) = Create(Short);
        await vm.RefreshAsync();
        vm.KeyboardTransportNote.Should().Contain("已确认 3 幅画面");

        // A hanging fake ignores cancellation; the deadline must still end the read.
        keyboard.NextReads.Enqueue(_ => hang ? Never<KeyboardDisplayStatusDto>() : Task.FromException<KeyboardDisplayStatusDto>(new IOException("fake status lost")));
        await vm.RefreshAsync().WaitAsync(Guard);
        vm.KeyboardTransportNote.Should().NotContain("已确认").And.Contain("未知").And.Contain("重试");
        vm.HasKeyboardDisplayService.Should().BeFalse();
        vm.Display.State.Should().Be(CaseControlState.Active);
        errors.Should().BeEmpty(); keyboard.Writes.Should().BeEmpty();

        await vm.RefreshAsync();
        vm.KeyboardTransportNote.Should().Contain("已确认 3 幅画面");
        vm.HasKeyboardDisplayService.Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_preflight_deadline_sends_nothing_and_clears_stale_frames()
    {
        var (vm, keyboard, _, _) = Create(Short);
        await vm.RefreshAsync();
        keyboard.NextReads.Enqueue(_ => Never<KeyboardDisplayStatusDto>());
        await Toggle(vm);
        keyboard.Writes.Should().BeEmpty();
        vm.KeyboardTransportNote.Should().Contain("未发送切换指令").And.NotContain("已确认");
        vm.HasKeyboardDisplayService.Should().BeFalse();
    }

    [Fact]
    public async Task Old_host_without_keyboard_endpoint_stays_a_local_only_preview()
    {
        var (vm, keyboard, errors, _) = Create(Long);
        keyboard.ReadOverride = _ => Task.FromException<KeyboardDisplayStatusDto>(MethodNotFound());
        await vm.RefreshAsync();
        vm.KeyboardTransportNote.Should().Be(CaseControlPanelViewModel.TransportNote);
        vm.HasKeyboardDisplayService.Should().BeFalse();
        vm.Display.State.Should().Be(CaseControlState.Active);

        await Toggle(vm);
        keyboard.Writes.Should().BeEmpty();
        vm.KeyboardTransportNote.Should().Be(CaseControlPanelViewModel.TransportNote);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Old_host_without_write_endpoint_gets_one_attempt_and_no_fabricated_confirmation()
    {
        var (vm, keyboard, errors, _) = Create(Long);
        await vm.RefreshAsync();
        keyboard.WriteOverride = (_, _) => Task.FromException(MethodNotFound());
        await Toggle(vm);
        keyboard.Writes.Should().Equal(true);
        vm.KeyboardTransportNote.Should().Be(CaseControlPanelViewModel.TransportNote);
        vm.HasKeyboardDisplayService.Should().BeFalse();
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_ignoring_cancellation_times_out_once_and_readback_supplies_the_observed_state()
    {
        var (vm, keyboard, _, _) = Create(Short);
        await vm.RefreshAsync();
        var notes = Notes(vm);
        var forwarded = CancellationToken.None;
        // The fake applies the change, then loses the answer and ignores cancellation.
        keyboard.WriteOverride = (enabled, ct) => { forwarded = ct; keyboard.Apply(enabled); return Never(); };
        await Toggle(vm);

        keyboard.Writes.Should().Equal(true);
        forwarded.IsCancellationRequested.Should().BeTrue("the deadline is still forwarded to cooperative services");
        keyboard.Reads.Should().Be(3, "initial refresh, the toggle preflight and one bounded readback");
        lock (notes) notes.Should().Contain(n => n.Contains("结果未确认") && n.Contains("未自动重发"));
        vm.KeyboardToggleLabel.Should().Be("恢复键盘原动画");
        vm.KeyboardTransportNote.Should().Contain("已确认 9 幅画面");
        await Task.Delay(Short * 3);
        keyboard.Writes.Should().Equal([true], "an unanswered write is never resent automatically");

        // A later explicit action rereads the current state and keeps toggle semantics.
        keyboard.WriteOverride = null;
        await Toggle(vm);
        keyboard.Writes.Should().Equal(true, false);
        vm.KeyboardToggleLabel.Should().Be("开启光域状态屏");
    }

    [Fact]
    public async Task Unknown_write_with_failed_readback_shows_unknown_instead_of_stale_frame_counts()
    {
        var (vm, keyboard, _, _) = Create(Short);
        await vm.RefreshAsync();
        keyboard.WriteOverride = (_, _) => Never();
        keyboard.NextReads.Enqueue(_ => Task.FromResult(keyboard.Status));
        keyboard.NextReads.Enqueue(_ => Never<KeyboardDisplayStatusDto>());
        await Toggle(vm);

        keyboard.Writes.Should().Equal(true);
        keyboard.Reads.Should().Be(3);
        vm.HasKeyboardDisplayService.Should().BeFalse();
        vm.KeyboardTransportNote.Should().Contain("结果未知").And.Contain("未自动重发").And.NotContain("已确认");
        await Task.Delay(Short * 3);
        keyboard.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task Disconnect_during_write_skips_readback_and_never_resends()
    {
        var (vm, keyboard, _, _) = Create(Long);
        await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        keyboard.WriteOverride = (enabled, _) => { keyboard.Apply(enabled); entered.SetResult(); return lost.Task; };
        var toggle = vm.ToggleKeyboardDisplayCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);
        vm.SetConnected(false);
        lost.SetException(new ConnectionLostException("fake pipe closed"));
        await toggle.WaitAsync(Guard);

        keyboard.Writes.Should().Equal(true);
        keyboard.Reads.Should().Be(2, "no readback through the closed connection");
        vm.KeyboardTransportNote.Should().Be(Disconnected);

        vm.SetConnected(true);
        await vm.RefreshAsync();
        vm.KeyboardToggleLabel.Should().Be("恢复键盘原动画", "a fresh read on the new connection shows what actually applied");
        keyboard.Writes.Should().Equal(true);
    }

    [Fact]
    public async Task Host_rejected_write_is_reported_and_read_back_without_resend()
    {
        var (vm, keyboard, errors, _) = Create(Long);
        await vm.RefreshAsync();
        keyboard.WriteOverride = (_, _) => Task.FromException(new RemoteInvocationException("fake keyboard identity is not verified", 0, (object?)null));
        await Toggle(vm);

        keyboard.Writes.Should().Equal(true);
        keyboard.Reads.Should().Be(3, "the observed state is reread instead of assumed");
        errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>();
        vm.KeyboardToggleLabel.Should().Be("开启光域状态屏");
        vm.KeyboardTransportNote.Should().Contain("已确认 3 幅画面");
    }

    [Fact]
    public async Task Slow_or_failing_keyboard_status_never_delays_or_replaces_the_case_readback()
    {
        var (vm, keyboard, errors, _) = Create(Long);
        var held = Hold(keyboard);
        var refresh = vm.RefreshAsync();
        vm.Display.State.Should().Be(CaseControlState.Active, "the case readback is published before the optional keyboard read");
        vm.ObservationNote.Should().Contain("后台回读");

        held.SetException(new IOException("fake keyboard status failed"));
        await refresh.WaitAsync(Guard);
        vm.Display.State.Should().Be(CaseControlState.Active);
        vm.KeyboardTransportNote.Should().Contain("未读到");
        errors.Should().BeEmpty();

        keyboard.NextReads.Enqueue(_ => Task.FromException<KeyboardDisplayStatusDto>(new IOException("fake preflight failed")));
        await Toggle(vm);
        keyboard.Writes.Should().BeEmpty();
        vm.Display.State.Should().Be(CaseControlState.Active);
        vm.ObservationNote.Should().Contain("后台回读");
    }

    [Fact]
    public async Task Toggle_keeps_on_off_semantics_and_reports_only_communication_evidence()
    {
        var (vm, keyboard, _, rpc) = Create(Long);
        await vm.RefreshAsync();
        var notes = Notes(vm);
        await Toggle(vm);

        keyboard.Writes.Should().Equal(true);
        lock (notes) notes.Should().Contain(n => n.Contains("正在请求开启")).And.Contain(n => n.Contains("后台已接受开启指令"));
        vm.KeyboardToggleLabel.Should().Be("恢复键盘原动画");
        vm.KeyboardTransportNote.Should().Contain("已确认 9 幅画面").And.Contain("非光学确认");

        await Toggle(vm);
        keyboard.Writes.Should().Equal(true, false);
        vm.KeyboardToggleLabel.Should().Be("开启光域状态屏");
        keyboard.Reads.Should().Be(5, "one refresh plus a preflight and a readback per toggle");
        rpc.Writes.Should().BeEmpty("the keyboard toggle never touches the case scene or saved settings");
    }

    private static Task Toggle(CaseControlPanelViewModel vm) => vm.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
    private static Task Never() => new TaskCompletionSource().Task;
    private static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;

    private static KeyboardDisplayStatusDto KeyboardStatus(bool enabled, long frames) =>
        new(enabled, enabled ? "Connecting" : "Stopped", enabled ? "等待接入键盘状态屏" : "键盘状态屏已关闭", frames, 0, DateTimeOffset.UtcNow, null);

    // Same construction as the other old-Host compatibility fakes; StreamJsonRpc does not expose a public constructor.
    private static RemoteMethodNotFoundException MethodNotFound() =>
        (RemoteMethodNotFoundException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException));

    private static TaskCompletionSource<KeyboardDisplayStatusDto> Hold(FakeKeyboard keyboard)
    {
        var hold = new TaskCompletionSource<KeyboardDisplayStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        keyboard.NextReads.Enqueue(_ => hold.Task);
        return hold;
    }

    private static List<string> Notes(CaseControlPanelViewModel vm)
    {
        var notes = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CaseControlPanelViewModel.KeyboardTransportNote)) lock (notes) notes.Add(vm.KeyboardTransportNote);
        };
        return notes;
    }

    private (CaseControlPanelViewModel Vm, FakeKeyboard Keyboard, List<Exception> Errors, CasePowerRpc Rpc) Create(TimeSpan keyboardTimeout,
        Func<IKeyboardDisplayService>? keyboardDisplay = null)
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = Active; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var keyboard = new FakeKeyboard();
        var errors = new List<Exception>();
        var vm = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, errors.Add,
            keyboardDisplay: keyboardDisplay ?? (() => keyboard), keyboardTimeout: keyboardTimeout);
        return (vm, keyboard, errors, rpc);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed class FakeKeyboard : IKeyboardDisplayService
    {
        private int _reads;
        public KeyboardDisplayStatusDto Status { get; set; } = KeyboardStatus(false, 3);
        public ConcurrentQueue<Func<CancellationToken, Task<KeyboardDisplayStatusDto>>> NextReads { get; } = new();
        public Func<CancellationToken, Task<KeyboardDisplayStatusDto>>? ReadOverride { get; set; }
        public Func<bool, CancellationToken, Task>? WriteOverride { get; set; }
        public List<bool> Writes { get; } = [];
        public int Reads => Volatile.Read(ref _reads);

        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return NextReads.TryDequeue(out var next) ? next(ct) : ReadOverride?.Invoke(ct) ?? Task.FromResult(Status);
        }

        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            lock (Writes) Writes.Add(enabled);
            if (WriteOverride is { } write) return write(enabled, ct);
            Apply(enabled);
            return Task.CompletedTask;
        }

        public void Apply(bool enabled) => Status = KeyboardStatus(enabled, 9);
    }
}
