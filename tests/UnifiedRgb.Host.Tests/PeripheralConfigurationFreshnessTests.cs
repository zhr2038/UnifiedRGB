using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions.Execution;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Revision 2: an expired peripheral observation is refused at the moment it is used, before any poll or explicit
/// ExpireObservation retires it (for example a hidden window followed by an immediate click). Injected clock and launcher only:
/// no browser, window, Host, pipe or device.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned completion sources control the keyboard preflight.
public sealed class PeripheralConfigurationFreshnessTests : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Expired = TimeSpan.FromSeconds(11);
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Expired_ready_never_reaches_the_gear_link_launcher_without_a_poll_first()
    {
        var rig = Create();
        rig.Service.Current = Status("Ready", true, true);
        await rig.Vm.RefreshAsync();
        rig.Vm.IsReady.Should().BeTrue();
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.Vm.IsReady.Should().BeTrue("exactly 10 s is still inside the freshness window");
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeTrue();

        rig.Clock.Advance(TimeSpan.FromSeconds(1)); // 11 s; deliberately no ExpireObservation, RefreshAsync or poll.
        using (new AssertionScope())
        {
            rig.Vm.IsReady.Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.Execute(null); // A direct Execute skips CanExecute; the click itself must still refuse.
            rig.Opened.Should().BeEmpty("an expired Ready never reaches the launcher");
            rig.Vm.HasStatus.Should().BeFalse(); rig.Vm.Status.Should().BeNull();
            rig.Vm.StateTitle.Should().NotStartWith("已就绪");
            rig.Vm.Guidance.Should().BeEmpty();
            rig.Vm.Message.Should().Contain("已过期");
        }
        rig.Service.Reads.Should().Be(1); rig.Service.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();

        // Only a new read restores Ready, and even then only an explicit click opens the page.
        await rig.Vm.RefreshAsync();
        rig.Vm.IsReady.Should().BeTrue(); rig.Opened.Should().BeEmpty();
        rig.Vm.OpenGearLinkCommand.Execute(null);
        rig.Opened.Should().Equal(PeripheralConfigurationViewModel.GearLinkUri);
        rig.Service.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Error")]
    public async Task Expired_status_refuses_the_enter_intent_without_a_poll_first(string state)
    {
        var rig = Create();
        rig.Service.Current = Status(state, false, false);
        await rig.Vm.RefreshAsync();
        rig.Vm.CanEnter.Should().BeTrue();
        rig.Clock.Advance(Expired);
        var changes = Changes(rig.Vm);
        using (new AssertionScope())
        {
            rig.Vm.CanEnter.Should().BeFalse();
            rig.Vm.EnterCommand.CanExecute(null).Should().BeFalse();
            await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard); // ExecuteAsync does not consult CanExecute.
            await rig.Vm.EnterAsync().WaitAsync(Guard);
            rig.Service.Writes.Should().BeEmpty("an expired observation never authorises a request");
            rig.Service.Reads.Should().Be(1, "a refused intent is neither a hidden read nor a read-then-write");
            rig.Vm.Message.Should().Contain("已过期");
            changes.Should().Contain(nameof(PeripheralConfigurationViewModel.CanEnter), "the refused click retires the expired view for its bindings");
        }

        await rig.Vm.RefreshAsync();
        rig.Service.Writes.Should().BeEmpty("a refresh never implies a write");
        rig.Vm.CanEnter.Should().BeTrue();
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Service.Writes.Should().Equal("enter");
        rig.Errors.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Ready", true, true)]
    [InlineData("Entering", true, false)]
    [InlineData("Error", false, false)]
    public async Task Expired_status_refuses_the_exit_intent_without_a_poll_first(string state, bool mode, bool released)
    {
        var rig = Create();
        rig.Service.Current = Status(state, mode, released);
        await rig.Vm.RefreshAsync();
        rig.Vm.CanExit.Should().BeTrue();
        rig.Clock.Advance(Expired);
        var changes = Changes(rig.Vm);
        using (new AssertionScope())
        {
            rig.Vm.CanExit.Should().BeFalse();
            rig.Vm.ExitCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.IsReady.Should().BeFalse();
            await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
            await rig.Vm.ExitAsync().WaitAsync(Guard);
            rig.Service.Writes.Should().BeEmpty("an expired observation never authorises a request");
            rig.Service.Reads.Should().Be(1, "a refused intent is neither a hidden read nor a read-then-write");
            rig.Vm.Message.Should().Contain("已过期");
            changes.Should().Contain(nameof(PeripheralConfigurationViewModel.CanExit), "the refused click retires the expired view for its bindings");
        }

        await rig.Vm.RefreshAsync();
        rig.Service.Writes.Should().BeEmpty("a refresh never implies a write");
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Service.Writes.Should().Equal("exit");
        rig.Errors.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Clock_moving_backwards_never_extends_an_observation()
    {
        var rig = Create();
        rig.Service.Current = Status("Ready", true, true);
        await rig.Vm.RefreshAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(-5)); // The observation's real age is now unknown.
        using (new AssertionScope())
        {
            rig.Vm.IsReady.Should().BeFalse();
            rig.Vm.CanExit.Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.Execute(null);
            await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
            rig.Opened.Should().BeEmpty();
            rig.Service.Writes.Should().BeEmpty();
        }

        await rig.Vm.RefreshAsync();
        rig.Vm.IsReady.Should().BeTrue("a read at the adjusted time is fresh again");
    }

    [Fact]
    public async Task Expired_active_confirmation_holds_the_keyboard_toggle_without_a_poll_first()
    {
        var rig = CreatePanel();
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        rig.Clock.Advance(Expired);
        var changes = new List<string>();
        rig.Panel.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? string.Empty);
        using (new AssertionScope())
        {
            rig.Peripherals.KeyboardIntegrationConfirmed.Should().BeFalse();
            rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
            rig.Panel.ToggleKeyboardDisplayCommand.CanExecute(null).Should().BeFalse();
            await rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
            rig.Keyboard.Writes.Should().BeEmpty("an expired Active confirmation never authorises a keyboard write");
            rig.Panel.KeyboardTransportNote.Should().Contain("外设配置状态未确认");
            changes.Should().Contain(nameof(CaseControlPanelViewModel.CanToggleKeyboardDisplay), "the refused toggle retires the expired view for its bindings");
        }
        rig.Panel.Display.State.Should().Be(CaseControlState.Active, "the case readback has its own observation and is untouched");
        rig.Rpc.Writes.Should().BeEmpty(); rig.Service.Writes.Should().BeEmpty();

        await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        rig.Keyboard.Writes.Should().BeEmpty("re-confirming never toggles the keyboard by itself");
        await rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Keyboard.Writes.Should().Equal(true);
        rig.PanelErrors.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmation_that_expires_during_the_keyboard_preflight_prevents_the_keyboard_write()
    {
        var rig = CreatePanel();
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();
        var preflight = new TaskCompletionSource<KeyboardDisplayStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.NextReads.Enqueue(_ => preflight.Task);
        var toggle = rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync();
        rig.Clock.Advance(Expired);
        preflight.SetResult(KeyboardStatus(false, 4));
        await toggle.WaitAsync(Guard);

        rig.Keyboard.Writes.Should().BeEmpty();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
        rig.Panel.Display.State.Should().Be(CaseControlState.Active);
        rig.PanelErrors.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Old_host_compatibility_does_not_expire_and_keeps_the_existing_keyboard_toggle()
    {
        var rig = CreatePanel();
        rig.Service.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(MethodNotFound());
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        rig.Peripherals.IsUnsupported.Should().BeTrue();
        rig.Peripherals.Message.Should().Be(PeripheralConfigurationViewModel.UnsupportedMessage);
        rig.Peripherals.CanEnter.Should().BeFalse(); rig.Peripherals.CanExit.Should().BeFalse();
        rig.Peripherals.KeyboardIntegrationConfirmed.Should().BeTrue();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        await rig.Peripherals.RefreshAsync(); await rig.Peripherals.EnterAsync();
        rig.Service.Reads.Should().Be(1, "the old Host is still not probed again on this connection");
        await rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Keyboard.Writes.Should().Equal(true);
        rig.Service.Writes.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty();
        rig.PanelErrors.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    private static PeripheralConfigurationStatusDto Status(string state, bool mode, bool released) =>
        new(mode, state, $"fake {state} detail", released, null, null);

    private static KeyboardDisplayStatusDto KeyboardStatus(bool enabled, long frames) =>
        new(enabled, enabled ? "Connecting" : "Stopped", enabled ? "等待接入键盘状态屏" : "键盘状态屏已关闭", frames, 0, DateTimeOffset.UtcNow, null);

    // Same construction as the other old-Host compatibility fakes; StreamJsonRpc does not expose a public constructor.
    private static RemoteMethodNotFoundException MethodNotFound() =>
        (RemoteMethodNotFoundException)RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException));

    private static List<string> Changes(PeripheralConfigurationViewModel vm)
    {
        var changes = new List<string>();
        vm.PropertyChanged += (_, e) => { lock (changes) changes.Add(e.PropertyName ?? string.Empty); };
        return changes;
    }

    private static Rig Create()
    {
        var clock = new ManualClock();
        var fake = new FakePeripherals();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var vm = new PeripheralConfigurationViewModel(() => fake, InlineDispatcher.Instance, errors.Add, opened.Add, clock.Now, Long);
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
        // The panel keeps its own default clock: only the peripheral observation ages here.
        var panel = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, panelErrors.Add,
            keyboardDisplay: () => keyboard, keyboardTimeout: Long);
        var rig = Create();
        panel.AttachPeripheralConfiguration(rig.Vm);
        return new(panel, rig.Vm, rig.Service, rig.Clock, keyboard, rpc, panelErrors, rig.Errors);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed record Rig(PeripheralConfigurationViewModel Vm, FakePeripherals Service, ManualClock Clock, List<Exception> Errors, List<Uri> Opened);

    private sealed record PanelRig(CaseControlPanelViewModel Panel, PeripheralConfigurationViewModel Peripherals, FakePeripherals Service,
        ManualClock Clock, FakeKeyboard Keyboard, CasePowerRpc Rpc, List<Exception> PanelErrors, List<Exception> Errors);

    private sealed class ManualClock
    {
        private DateTimeOffset _value = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now() => _value;
        public void Advance(TimeSpan by) => _value += by;
    }

    private sealed class FakePeripherals : IPeripheralConfigurationService
    {
        private int _reads;
        public PeripheralConfigurationStatusDto Current { get; set; } = Status("Active", false, false);
        public Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>? ReadOverride { get; set; }
        public List<string> Writes { get; } = [];
        public int Reads => Volatile.Read(ref _reads);

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return ReadOverride?.Invoke(ct) ?? Task.FromResult(Current);
        }

        // Scripted answers only: the fake reports a state, it never models a release.
        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) =>
            Write("enter", Status("Entering", true, false));
        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) =>
            Write("exit", Status("Resuming", false, true));

        private Task<PeripheralConfigurationStatusDto> Write(string name, PeripheralConfigurationStatusDto next)
        {
            lock (Writes) Writes.Add(name);
            Current = next;
            return Task.FromResult(next);
        }
    }

    private sealed class FakeKeyboard : IKeyboardDisplayService
    {
        public KeyboardDisplayStatusDto Status { get; set; } = KeyboardStatus(false, 3);
        public ConcurrentQueue<Func<CancellationToken, Task<KeyboardDisplayStatusDto>>> NextReads { get; } = new();
        public List<bool> Writes { get; } = [];

        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) =>
            NextReads.TryDequeue(out var next) ? next(ct) : Task.FromResult(Status);

        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            lock (Writes) Writes.Add(enabled);
            Status = KeyboardStatus(enabled, 9);
            return Task.CompletedTask;
        }
    }
}
