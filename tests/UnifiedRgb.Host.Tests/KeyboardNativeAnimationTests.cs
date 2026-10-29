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
/// Revision 4: SetKeyboardDisplayEnabled(false) requests the keyboard's own visible animation; it is never a panel-off, and
/// no receipt, ACK or Stopped readback is shown as proof that the animation plays. The UI shows no brightness value and
/// implements no brightness policy; its guide only explains the backend's. Fake services and a headless view only: no
/// browser, window, Host, pipe or device.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned tasks script lost answers.
[Collection("Host")]
public sealed class KeyboardNativeAnimationTests : IDisposable
{
    private const string RestoreNative = "恢复键盘原动画";
    private const string EnablePage = "开启光域状态屏";
    private static readonly DateTimeOffset At = new(2026, 10, 25, 13, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    // UI-owned text must never claim the native animation plays, or describe the request as switching a screen off.
    private static readonly string[] FalseClaims = ["正在播放", "已在播放", "原动画已恢复", "已恢复原动画", "已恢复键盘原动画", "原动画已可见",
        "关闭屏幕", "屏幕已关闭", "关闭键盘状态屏", "关闭光域状态屏", "键盘状态屏已关闭"];
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly List<CasePowerActions> _owned = [];

    [Theory]
    [InlineData(true, RestoreNative)]
    [InlineData(false, EnablePage)]
    public async Task Labels_name_the_native_animation_request_and_our_status_page(bool pageEnabled, string label)
    {
        var rig = Create(Long);
        rig.Keyboard.Status = pageEnabled ? Page() : Native();
        await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.KeyboardToggleLabel.Should().Be(label);
            rig.Vm.KeyboardToggleLabel.Should().NotContain("关闭");
            rig.Vm.CanToggleKeyboardDisplay.Should().BeTrue();
            Claims(rig.Vm.KeyboardTransportNote);
        }
        rig.Keyboard.Writes.Should().BeEmpty("showing a label sends nothing");
    }

    [Fact]
    public async Task Explicit_restore_sends_exactly_one_disable_and_never_claims_the_native_animation_plays()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Page();
        await rig.Vm.RefreshAsync();
        var notes = Notes(rig.Vm);
        await Toggle(rig.Vm);

        using (new AssertionScope())
        {
            rig.Keyboard.Writes.Should().Equal(false);
            rig.Keyboard.Reads.Should().Be(3, "one refresh, the toggle preflight and one bounded readback");
            lock (notes)
            {
                notes.Should().Contain(n => n.Contains("正在请求" + RestoreNative));
                notes.Should().Contain(n => n.Contains("后台已接受恢复原动画指令"));
                foreach (var note in notes) Claims(note);
            }
            rig.Vm.KeyboardToggleLabel.Should().Be(EnablePage);
            rig.Vm.KeyboardTransportNote.Should().Contain("目标为键盘原动画").And.Contain("以键盘实际显示为准").And.Contain("非光学确认");
            Claims(rig.Vm.KeyboardTransportNote);
        }

        // Root leaves our page disabled: polls never re-enable it or repeat the request.
        await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync();
        rig.Keyboard.Writes.Should().Equal([false], "no automatic enable, retry or restore");
        rig.Rpc.Writes.Should().BeEmpty("the case scene is untouched"); rig.Rpc.Submitted.Should().BeEmpty(); rig.Rpc.Loads.Should().Be(0);
        rig.Vm.Display.State.Should().Be(CaseControlState.Active);
        rig.Errors.Should().BeEmpty("the screen fake answers reads only; any screen write would have been reported");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_with_unknown_outcome_reads_back_once_and_never_reports_success(bool readbackSucceeds)
    {
        var rig = Create(Short);
        rig.Keyboard.Status = Page();
        await rig.Vm.RefreshAsync();
        var notes = Notes(rig.Vm);
        // The Host applies the request, then the answer is lost and the call ignores cancellation.
        rig.Keyboard.WriteOverride = (enabled, _) => { rig.Keyboard.Apply(enabled); return Never(); };
        if (!readbackSucceeds)
        {
            rig.Keyboard.NextReads.Enqueue(_ => Task.FromResult(rig.Keyboard.Status));
            rig.Keyboard.NextReads.Enqueue(_ => Never<KeyboardDisplayStatusDto>());
        }
        await Toggle(rig.Vm);

        using (new AssertionScope())
        {
            rig.Keyboard.Writes.Should().Equal(false);
            lock (notes)
            {
                notes.Should().Contain(n => n.Contains("恢复原动画结果未确认") && n.Contains("未自动重发"));
                foreach (var note in notes) Claims(note);
            }
            if (readbackSucceeds)
            {
                rig.Vm.KeyboardToggleLabel.Should().Be(EnablePage, "the readback, not the lost answer, supplies the state");
                rig.Vm.KeyboardTransportNote.Should().Contain("目标为键盘原动画").And.Contain("以键盘实际显示为准");
            }
            else
            {
                rig.Vm.HasKeyboardDisplayService.Should().BeFalse();
                rig.Vm.CanToggleKeyboardDisplay.Should().BeFalse();
                rig.Vm.KeyboardTransportNote.Should().Contain("恢复原动画结果未知").And.Contain("未自动重发").And.NotContain("已确认");
            }
            Claims(rig.Vm.KeyboardTransportNote);
            rig.Errors.Should().BeEmpty();
        }
        await Task.Delay(Short * 3);
        rig.Keyboard.Writes.Should().HaveCount(1, "an unanswered request is never resent");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejected_restore_is_reported_read_back_and_never_shown_as_success(bool readbackSucceeds)
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Page();
        await rig.Vm.RefreshAsync();
        var notes = Notes(rig.Vm);
        rig.Keyboard.WriteOverride = (_, _) => Task.FromException(new RemoteInvocationException("fake native restore refused", 0, (object?)null));
        if (!readbackSucceeds)
        {
            rig.Keyboard.NextReads.Enqueue(_ => Task.FromResult(rig.Keyboard.Status));
            rig.Keyboard.NextReads.Enqueue(_ => Task.FromException<KeyboardDisplayStatusDto>(new IOException("fake status lost")));
        }
        await Toggle(rig.Vm);

        using (new AssertionScope())
        {
            rig.Keyboard.Writes.Should().Equal(false);
            rig.Keyboard.Reads.Should().Be(3, "the actual state is read back instead of assumed");
            rig.Errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>();
            lock (notes)
            {
                notes.Should().Contain(n => n.Contains("后台对恢复原动画指令返回错误"));
                foreach (var note in notes) Claims(note);
            }
            if (readbackSucceeds) rig.Vm.KeyboardToggleLabel.Should().Be(RestoreNative, "our page is still enabled, as read back");
            else rig.Vm.KeyboardTransportNote.Should().Contain("不能确认原动画是否已恢复");
            Claims(rig.Vm.KeyboardTransportNote);
        }
        rig.Keyboard.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task Native_restore_error_is_shown_as_reported_and_never_retried_or_reenabled()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Native("Error");
        await rig.Vm.RefreshAsync();
        await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.KeyboardTransportNote.Should().Contain("后台状态 Error").And.Contain("fake Error detail").And.Contain("以键盘实际显示为准");
            Claims(rig.Vm.KeyboardTransportNote);
            rig.Vm.KeyboardToggleLabel.Should().Be(EnablePage);
        }
        rig.Keyboard.Writes.Should().BeEmpty("an Error is never answered with an automatic retry or enable");
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_stays_held_during_a_configuration_handoff_and_stays_explicit_afterwards()
    {
        var rig = Create(Long);
        var peripherals = new FakePeripherals { Current = new(true, "Entering", "fake Entering detail", false, At, null) };
        var opened = new List<Uri>();
        var config = new PeripheralConfigurationViewModel(() => peripherals, InlineDispatcher.Instance, rig.Errors.Add, opened.Add, timeout: Long);
        rig.Vm.AttachPeripheralConfiguration(config);
        rig.Keyboard.Status = Page();
        await rig.Vm.RefreshAsync(); await config.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.KeyboardToggleLabel.Should().Be(RestoreNative);
            rig.Vm.CanToggleKeyboardDisplay.Should().BeFalse();
            rig.Vm.ToggleKeyboardDisplayCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.KeyboardTransportNote.Should().Contain("外设配置中");
        }
        await Toggle(rig.Vm);
        rig.Keyboard.Writes.Should().BeEmpty("the handoff may own the keyboard");

        peripherals.Current = new(false, "Active", "fake Active detail", false, At, null);
        await config.RefreshAsync();
        rig.Keyboard.Writes.Should().BeEmpty("confirming integration never restores by itself");
        await Toggle(rig.Vm);
        rig.Keyboard.Writes.Should().Equal(false);
        peripherals.Writes.Should().BeEmpty(); opened.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_panel_xaml_shows_both_labels_intent_and_brightness_guide_without_mislabelling_100()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Page();
        await rig.Vm.RefreshAsync();
        var reads = rig.Keyboard.Reads;
        var page = Render(rig.Vm);
        rig.Keyboard.Status = Native();
        await rig.Vm.RefreshAsync();
        var native = Render(rig.Vm);

        using (new AssertionScope())
        {
            page.Buttons.Should().ContainKey(RestoreNative).WhoseValue.Enabled.Should().BeTrue();
            native.Buttons.Should().ContainKey(EnablePage).WhoseValue.Enabled.Should().BeTrue();
            page.Buttons.GetValueOrDefault(RestoreNative).ToolTip.Should().Contain("RGB 灯光跟随").And.Contain("机箱").And.NotContain("关闭");
            foreach (var view in new[] { page, native })
            {
                view.Buttons.Keys.Should().NotContain(k => k.Contains("关闭"));
                foreach (var (text, shown) in view.Texts) if (shown) Claims(text);
            }
            var guide = page.Texts.Should().ContainSingle(t => t.Text.Contains("兜底")).Which;
            guide.Shown.Should().BeTrue();
            guide.Text.Should().Contain(RestoreNative).And.Contain("RGB 灯光跟随").And.Contain("机箱状态")
                .And.Contain("正值").And.Contain("100 不代表原来的亮度").And.Contain("以键盘实际显示为准");
            // The policy never presents the 100 fallback (or a status-page 100) as the owner's earlier native brightness.
            guide.Text.Should().NotContain("原亮度 100").And.NotContain("原来的亮度为 100").And.NotContain("恢复为 100")
                .And.NotContain("恢复到 100").And.NotContain("原亮度为 0");
            native.Texts.Should().Contain(t => t.Shown && t.Text.Contains("目标为键盘原动画"));
        }
        rig.Keyboard.Reads.Should().Be(reads + 1, "binding reads nothing; only the explicit second refresh did");
        rig.Keyboard.Writes.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Old_host_without_keyboard_endpoint_keeps_a_disabled_local_preview_without_the_guide()
    {
        var rig = Create(Long);
        rig.Keyboard.ReadOverride = _ => Task.FromException<KeyboardDisplayStatusDto>(MethodNotFound());
        await rig.Vm.RefreshAsync();
        await Toggle(rig.Vm); // Before rendering: the headless view's thread ends, so later command events must not reach it.
        var view = Render(rig.Vm);

        using (new AssertionScope())
        {
            view.Buttons.Should().ContainKey(EnablePage).WhoseValue.Enabled.Should().BeFalse();
            view.Texts.Should().Contain((CaseControlPanelViewModel.TransportNote, true));
            view.Texts.Should().ContainSingle(t => t.Text.Contains("兜底")).Which.Shown.Should().BeFalse("an old Host offers no native-animation request");
            rig.Vm.KeyboardTransportNote.Should().Be(CaseControlPanelViewModel.TransportNote);
        }
        rig.Keyboard.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    private static void Claims(string text)
    {
        foreach (var claim in FalseClaims) text.Should().NotContain(claim);
    }

    private static Task Toggle(CaseControlPanelViewModel vm) => vm.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
    private static Task Never() => new TaskCompletionSource().Task;
    private static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;
    private static KeyboardDisplayStatusDto Page() => new(true, "Running", "fake page detail", 9, 0, At, null);
    private static KeyboardDisplayStatusDto Native(string state = "Stopped") => new(false, state, $"fake {state} detail", 9, 0, At, null);

    // Same construction as the other old-Host compatibility fakes; StreamJsonRpc does not expose a public constructor.
    private static RemoteMethodNotFoundException MethodNotFound() =>
        (RemoteMethodNotFoundException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException));

    private static List<string> Notes(CaseControlPanelViewModel vm)
    {
        var notes = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CaseControlPanelViewModel.KeyboardTransportNote)) lock (notes) notes.Add(vm.KeyboardTransportNote);
        };
        return notes;
    }

    // Loads the actual panel XAML on an STA thread without a window and collects what its bindings show.
    private static Rendered Render(CaseControlPanelViewModel vm)
    {
        var buttons = new Dictionary<string, (bool Enabled, string ToolTip)>();
        var texts = new List<(string Text, bool Shown)>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = vm };
                view.Measure(new(300, 900)); view.Arrange(new(0, 0, 300, 900)); view.UpdateLayout();
                foreach (var button in Logical<System.Windows.Controls.Button>(view))
                    buttons[button.Content?.ToString() ?? string.Empty] = (button.IsEnabled, button.ToolTip?.ToString() ?? string.Empty);
                foreach (var text in Logical<System.Windows.Controls.TextBlock>(view)) texts.Add((text.Text, text.Visibility == System.Windows.Visibility.Visible));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
        return new(buttons, texts);
    }

    private static IEnumerable<T> Logical<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    private Rig Create(TimeSpan keyboardTimeout)
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var keyboard = new FakeKeyboard();
        var errors = new List<Exception>();
        var vm = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, errors.Add,
            keyboardDisplay: () => keyboard, keyboardTimeout: keyboardTimeout);
        return new(vm, keyboard, rpc, errors);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed record Rig(CaseControlPanelViewModel Vm, FakeKeyboard Keyboard, CasePowerRpc Rpc, List<Exception> Errors);

    private sealed record Rendered(Dictionary<string, (bool Enabled, string ToolTip)> Buttons, List<(string Text, bool Shown)> Texts);

    private sealed class FakeKeyboard : IKeyboardDisplayService
    {
        private int _reads;
        public KeyboardDisplayStatusDto Status { get; set; } = Native();
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

        // Scripted states only: the fake reports what a Host would, it never models brightness or a native animation.
        public void Apply(bool enabled) => Status = enabled ? Page() : Native();
    }

    private sealed class FakePeripherals : IPeripheralConfigurationService
    {
        public PeripheralConfigurationStatusDto Current { get; set; } = new(false, "Active", "fake Active detail", false, null, null);
        public List<string> Writes { get; } = [];

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default)
        {
            lock (Writes) Writes.Add("enter");
            return Task.FromResult(Current);
        }

        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default)
        {
            lock (Writes) Writes.Add("exit");
            return Task.FromResult(Current);
        }
    }
}
