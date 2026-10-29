using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions.Execution;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Revision 3: a fresh requested-mode Error with unreleased interfaces (for example after a paused restart with unresolved
/// recovery) offers the explicit Enter retry and withholds resume, so resume cannot bypass an incomplete release. The UI maps
/// only the frozen status fields; it never parses Detail/LastError text. Injected clock and launcher only: no browser,
/// window, Host, pipe or device.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned tasks script lost answers.
[Collection("Host")]
public sealed class PeripheralConfigurationRetryReleaseTests : IDisposable
{
    private const string RecoveryDetail = "A keyboard recovery journal remains after restart; original wired restoration requires an explicit Enter reattempt.";
    private static readonly DateTimeOffset At = new(2026, 10, 25, 13, 1, 18, TimeSpan.Zero);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Fresh_requested_mode_error_offers_one_explicit_enter_retry_and_never_the_resume_bypass()
    {
        var rig = Create();
        rig.Service.Current = RequestedError();
        await rig.Vm.RefreshAsync();
        await rig.Vm.RefreshAsync(); // Repeated polls stay reads.
        using (new AssertionScope())
        {
            rig.Vm.Status.Should().Be(RequestedError());
            rig.Vm.CanEnter.Should().BeTrue("a fresh Error permits an explicit retry-release even in requested mode");
            rig.Vm.EnterCommand.CanExecute(null).Should().BeTrue();
            rig.Vm.CanExit.Should().BeFalse("resume must not bypass an incomplete release");
            rig.Vm.ExitCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.IsReady.Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.StateTitle.Should().Be("外设配置出错");
            rig.Vm.Detail.Should().Be(RecoveryDetail);
            rig.Vm.LastErrorText.Should().Be("最近错误：" + RecoveryDetail);
            rig.Vm.Guidance.Should().Contain("“配置键鼠（暂时交给网页）”").And.Contain("重试释放").And.NotContain("可点“完成配置，恢复联动”");
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse();
            // Not even a programmatic Execute, which skips CanExecute, can use resume as a bypass.
            await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
            await rig.Vm.ExitAsync().WaitAsync(Guard);
            rig.Service.Writes.Should().BeEmpty("nothing is sent before an explicit Enter, and resume is withheld");
            rig.Opened.Should().BeEmpty();
        }
        rig.Service.Reads.Should().Be(2);

        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Service.Writes.Should().Equal("enter");
        rig.Vm.Status!.State.Should().Be("Entering");
        rig.Vm.IsReady.Should().BeFalse("Entering after a retry is not a release");
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        rig.Vm.CanEnter.Should().BeFalse();

        // Ready with released interfaces comes only from the backend; only then is the explicit resume a normal Exit.
        rig.Service.Current = Ready();
        await rig.Vm.RefreshAsync();
        rig.Vm.IsReady.Should().BeTrue(); rig.Vm.CanExit.Should().BeTrue(); rig.Vm.CanEnter.Should().BeFalse();
        rig.Opened.Should().BeEmpty("Ready never opens the page by itself");
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Service.Writes.Should().Equal("enter", "exit");
        rig.Vm.StateTitle.Should().Be("正在恢复键鼠联动"); rig.Vm.IsReady.Should().BeFalse();

        rig.Service.Current = Active();
        await rig.Vm.RefreshAsync();
        rig.Vm.StateTitle.Should().Be("键鼠联动运行中");
        rig.Service.Writes.Should().Equal("enter", "exit");
        rig.Opened.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task Fresh_error_always_offers_the_enter_retry_and_withholds_resume_only_while_a_requested_release_is_incomplete(
        bool mode, bool released, bool canExit)
    {
        var rig = Create();
        rig.Service.Current = new(mode, "Error", RecoveryDetail, released, At, RecoveryDetail);
        await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.CanEnter.Should().BeTrue();
            rig.Vm.CanExit.Should().Be(canExit);
            rig.Vm.IsReady.Should().BeFalse("Error is never Ready, whatever its release bit says");
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse();
            rig.Vm.Detail.Should().Be(RecoveryDetail);
        }
        rig.Service.Writes.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Enter_retry_rejected_by_the_backend_is_reported_read_back_and_never_resent()
    {
        var rig = Create();
        rig.Service.Current = RequestedError();
        await rig.Vm.RefreshAsync();
        rig.Service.NextWrites.Enqueue((_, _) => Task.FromException<PeripheralConfigurationStatusDto>(
            new RemoteInvocationException("fake recovery refused", 0, (object?)null)));
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Service.Writes.Should().Equal("enter");
            rig.Service.Reads.Should().Be(2, "the actual state is read back instead of assumed");
            rig.Errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>();
            rig.Vm.Status.Should().Be(RequestedError());
            rig.Vm.Message.Should().Contain("返回错误");
            rig.Vm.CanEnter.Should().BeTrue("only a later explicit click may retry");
            rig.Vm.CanExit.Should().BeFalse();
            rig.Vm.IsReady.Should().BeFalse();
        }
        await Task.Delay(Short);
        rig.Service.Writes.Should().HaveCount(1); rig.Opened.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Enter_retry_with_unknown_outcome_reads_back_once_and_never_resends(bool readbackSucceeds)
    {
        var rig = Create(Short);
        rig.Service.Current = RequestedError();
        await rig.Vm.RefreshAsync();
        // The backend applies the retry, then the answer is lost and the call ignores cancellation.
        rig.Service.NextWrites.Enqueue((_, _) => { rig.Service.Current = Entering(); return Never<PeripheralConfigurationStatusDto>(); });
        if (!readbackSucceeds) rig.Service.NextReads.Enqueue(_ => Never<PeripheralConfigurationStatusDto>());
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Service.Writes.Should().Equal("enter");
            rig.Service.Reads.Should().Be(2, "one bounded readback on the captured connection");
            rig.Vm.Message.Should().Contain("未自动重发");
            rig.Vm.IsReady.Should().BeFalse();
            if (readbackSucceeds)
            {
                rig.Vm.Status!.State.Should().Be("Entering", "the readback, not the lost answer, supplies the state");
                rig.Vm.Message.Should().Contain("结果未确认");
            }
            else
            {
                rig.Vm.HasStatus.Should().BeFalse();
                rig.Vm.CanEnter.Should().BeFalse(); rig.Vm.CanExit.Should().BeFalse();
                rig.Vm.Message.Should().Contain("结果未知");
            }
            rig.Errors.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
        }
        await Task.Delay(Short * 3);
        rig.Service.Writes.Should().HaveCount(1, "an unanswered retry is never resent");
    }

    [Fact]
    public async Task Enter_retry_answered_with_error_again_is_shown_as_is_and_never_retried_automatically()
    {
        var rig = Create();
        rig.Service.Current = RequestedError();
        await rig.Vm.RefreshAsync();
        var again = new PeripheralConfigurationStatusDto(true, "Error", "fake release still incomplete", false, At, "fake release still incomplete");
        rig.Service.NextWrites.Enqueue((_, _) => { rig.Service.Current = again; return Task.FromResult(again); });
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync();

        rig.Service.Writes.Should().Equal("enter");
        rig.Vm.Status.Should().Be(again);
        rig.Vm.LastErrorText.Should().Be("最近错误：fake release still incomplete");
        rig.Vm.CanEnter.Should().BeTrue(); rig.Vm.CanExit.Should().BeFalse(); rig.Vm.IsReady.Should().BeFalse();
        rig.Errors.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_requested_mode_error_offers_no_retry_until_a_new_read()
    {
        var rig = Create();
        rig.Service.Current = RequestedError();
        await rig.Vm.RefreshAsync();
        rig.Vm.CanEnter.Should().BeTrue();
        rig.Clock.Advance(TimeSpan.FromSeconds(11));

        rig.Vm.CanEnter.Should().BeFalse(); rig.Vm.EnterCommand.CanExecute(null).Should().BeFalse();
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Service.Writes.Should().BeEmpty(); rig.Service.Reads.Should().Be(1);
        await rig.Vm.RefreshAsync();
        rig.Vm.CanEnter.Should().BeTrue(); rig.Service.Writes.Should().BeEmpty("a refresh never implies a retry");
    }

    [Fact]
    public async Task Keyboard_toggle_stays_held_from_the_requested_mode_error_until_a_confirmed_resume()
    {
        var rig = CreatePanel();
        rig.Service.Current = RequestedError();
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
        rig.Panel.ToggleKeyboardDisplayCommand.CanExecute(null).Should().BeFalse();
        rig.Panel.KeyboardTransportNote.Should().Contain("外设配置中");
        await rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);

        await rig.Peripherals.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse("Entering after a retry is not a resume");
        rig.Service.Current = Ready();
        await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
        await rig.Peripherals.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse("Resuming is not a confirmed resume");
        rig.Service.Current = Active();
        await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        rig.Keyboard.Writes.Should().BeEmpty("no keyboard write happens without an explicit toggle");

        await rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Keyboard.Writes.Should().Equal(true);
        rig.Service.Writes.Should().Equal("enter", "exit");
        rig.Rpc.Writes.Should().BeEmpty("the handoff never touches the case scene or saved settings");
        rig.Panel.Display.State.Should().Be(CaseControlState.Active);
        rig.PanelErrors.Should().BeEmpty(); rig.Errors.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_panel_xaml_offers_the_enter_retry_and_withholds_resume_for_requested_mode_error()
    {
        var rig = CreatePanel();
        rig.Service.Current = RequestedError();
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();
        var reads = rig.Service.Reads;
        var buttons = new Dictionary<string, bool>();
        var texts = new List<(string Text, bool Shown)>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = rig.Panel };
                view.Measure(new(300, 900)); view.Arrange(new(0, 0, 300, 900)); view.UpdateLayout();
                foreach (var button in Logical<System.Windows.Controls.Button>(view)) buttons[button.Content?.ToString() ?? string.Empty] = button.IsEnabled;
                foreach (var text in Logical<System.Windows.Controls.TextBlock>(view)) texts.Add((text.Text, text.Visibility == System.Windows.Visibility.Visible));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();

        using (new AssertionScope())
        {
            buttons.Should().ContainKey("配置键鼠（暂时交给网页）").WhoseValue.Should().BeTrue("the explicit retry-release is offered");
            buttons.Should().ContainKey("完成配置，恢复联动").WhoseValue.Should().BeFalse("resume cannot bypass the incomplete release");
            buttons.Should().ContainKey("打开 Gear Link 网页").WhoseValue.Should().BeFalse();
            buttons.Should().ContainKey("开启光域状态屏").WhoseValue.Should().BeFalse("the keyboard toggle stays held");
            texts.Should().Contain(("外设配置出错", true));
            texts.Should().Contain((RecoveryDetail, true), "the backend Detail is shown as is");
            texts.Should().Contain(("最近错误：" + RecoveryDetail, true));
            texts.Should().Contain(t => t.Shown && t.Text.Contains("重试释放"));
        }
        rig.Service.Reads.Should().Be(reads); rig.Service.Writes.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
    }

    private static PeripheralConfigurationStatusDto RequestedError() => new(true, "Error", RecoveryDetail, false, At, RecoveryDetail);
    private static PeripheralConfigurationStatusDto Entering() => new(true, "Entering", "fake Entering detail", false, At, null);
    private static PeripheralConfigurationStatusDto Ready() => new(true, "Ready", "fake Ready detail", true, At, null);
    private static PeripheralConfigurationStatusDto Resuming() => new(false, "Resuming", "fake Resuming detail", true, At, null);
    private static PeripheralConfigurationStatusDto Active() => new(false, "Active", "fake Active detail", false, At, null);

    private static KeyboardDisplayStatusDto KeyboardStatus(bool enabled, long frames) =>
        new(enabled, enabled ? "Connecting" : "Stopped", enabled ? "等待接入键盘状态屏" : "键盘状态屏已关闭", frames, 0, DateTimeOffset.UtcNow, null);

    private static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;

    private static IEnumerable<T> Logical<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    private static Rig Create(TimeSpan? timeout = null)
    {
        var clock = new ManualClock();
        var fake = new FakePeripherals();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var vm = new PeripheralConfigurationViewModel(() => fake, InlineDispatcher.Instance, errors.Add, opened.Add, clock.Now, timeout ?? Long);
        return new(vm, fake, clock, errors, opened);
    }

    private PanelRig CreatePanel()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var keyboard = new FakeKeyboard();
        var panelErrors = new List<Exception>();
        var panel = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, panelErrors.Add,
            keyboardDisplay: () => keyboard, keyboardTimeout: Long);
        var rig = Create();
        panel.AttachPeripheralConfiguration(rig.Vm);
        return new(panel, rig.Vm, rig.Service, keyboard, rpc, panelErrors, rig.Errors, rig.Opened);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed record Rig(PeripheralConfigurationViewModel Vm, FakePeripherals Service, ManualClock Clock, List<Exception> Errors, List<Uri> Opened);

    private sealed record PanelRig(CaseControlPanelViewModel Panel, PeripheralConfigurationViewModel Peripherals, FakePeripherals Service,
        FakeKeyboard Keyboard, CasePowerRpc Rpc, List<Exception> PanelErrors, List<Exception> Errors, List<Uri> Opened);

    private sealed class ManualClock
    {
        private DateTimeOffset _value = new(2026, 10, 25, 13, 1, 20, TimeSpan.Zero);
        public DateTimeOffset Now() => _value;
        public void Advance(TimeSpan by) => _value += by;
    }

    private sealed class FakePeripherals : IPeripheralConfigurationService
    {
        private int _reads;
        public PeripheralConfigurationStatusDto Current { get; set; } = Active();
        public ConcurrentQueue<Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>> NextReads { get; } = new();
        public ConcurrentQueue<Func<string, CancellationToken, Task<PeripheralConfigurationStatusDto>>> NextWrites { get; } = new();
        public List<string> Writes { get; } = [];
        public int Reads => Volatile.Read(ref _reads);

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return NextReads.TryDequeue(out var next) ? next(ct) : Task.FromResult(Current);
        }

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) => Write("enter", Entering(), ct);
        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) => Write("exit", Resuming(), ct);

        // Scripted answers only: the fake reports a state, it never models a release or a recovery.
        private Task<PeripheralConfigurationStatusDto> Write(string name, PeripheralConfigurationStatusDto next, CancellationToken ct)
        {
            lock (Writes) Writes.Add(name);
            if (NextWrites.TryDequeue(out var write)) return write(name, ct);
            Current = next;
            return Task.FromResult(next);
        }
    }

    private sealed class FakeKeyboard : IKeyboardDisplayService
    {
        public KeyboardDisplayStatusDto Status { get; set; } = KeyboardStatus(false, 3);
        public List<bool> Writes { get; } = [];

        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            lock (Writes) Writes.Add(enabled);
            Status = KeyboardStatus(enabled, 9);
            return Task.CompletedTask;
        }
    }
}
