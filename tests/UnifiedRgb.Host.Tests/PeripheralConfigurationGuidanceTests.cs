using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions.Execution;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>Fake-only checks that disabled-stage guidance is visible and actionable without weakening the handoff guards.</summary>
[Collection("Host")]
public sealed class PeripheralConfigurationGuidanceTests : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Active_initial_state_explains_both_disabled_actions_without_allowing_either()
    {
        var rig = Create();
        await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.StateTitle.Should().Be("键鼠联动运行中");
            rig.Vm.NextStep.Should().Contain("尚未开始网页交接").And.Contain("配置键鼠（暂时交给网页）")
                .And.Contain("接口未确认释放前").And.Contain("恢复联动和打开网页不可用");
            rig.Vm.CanEnter.Should().BeTrue();
            rig.Vm.CanExit.Should().BeFalse();
            rig.Vm.ExitCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.Execute(null);
            rig.Opened.Should().BeEmpty();
            rig.Service.Writes.Should().BeEmpty();
            rig.Errors.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Entering_ready_and_resume_staging_keep_the_sequence_explicit_and_single_write_per_intent()
    {
        var rig = Create();
        await rig.Vm.RefreshAsync();
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Vm.Status!.State.Should().Be("Entering");
        rig.Vm.NextStep.Should().Contain("已请求交接").And.Contain("等待接口实际释放").And.Contain("Ready");
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        rig.Opened.Should().BeEmpty();

        rig.Service.Current = Status("Ready", true, true);
        await rig.Vm.RefreshAsync();
        rig.Vm.NextStep.Should().Contain("先点“打开 Gear Link 网页”").And.Contain("再点“完成配置，恢复联动”");
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeTrue();
        rig.Opened.Should().BeEmpty("a status transition never opens the page");
        rig.Vm.OpenGearLinkCommand.Execute(null);
        rig.Opened.Should().Equal(PeripheralConfigurationViewModel.GearLinkUri);

        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Vm.Status!.State.Should().Be("Resuming");
        rig.Vm.NextStep.Should().Contain("正在恢复键鼠联动").And.Contain("不要重复操作");
        rig.Vm.IsReady.Should().BeFalse();
        rig.Vm.CanExit.Should().BeFalse();

        rig.Service.Current = Status("Active", false, false);
        await rig.Vm.RefreshAsync();
        rig.Vm.CanEnter.Should().BeTrue();
        rig.Vm.KeyboardIntegrationConfirmed.Should().BeTrue();
        rig.Service.Writes.Should().Equal("enter", "exit");
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_ready_explains_that_fresh_readback_is_required_and_never_opens_the_page()
    {
        var rig = Create();
        rig.Service.Current = Status("Ready", true, true);
        await rig.Vm.RefreshAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(11));

        rig.Vm.NextStep.Should().Contain("已过期").And.Contain("新的后台回读");
        rig.Vm.CanExit.Should().BeFalse();
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        rig.Vm.OpenGearLinkCommand.Execute(null);
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Opened.Should().BeEmpty();
        rig.Service.Writes.Should().BeEmpty();
        rig.Service.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Requested_error_with_unreleased_interfaces_directs_retry_without_a_resume_bypass()
    {
        var rig = Create();
        rig.Service.Current = Status("Error", true, false);
        await rig.Vm.RefreshAsync();

        rig.Vm.NextStep.Should().Contain("交接未完成且接口未释放").And.Contain("重试").And.Contain("恢复联动和打开网页不可用");
        rig.Vm.CanEnter.Should().BeTrue();
        rig.Vm.CanExit.Should().BeFalse();
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Unsupported_disconnected_and_unread_states_explain_read_only_or_unknown_instead_of_reusing_ready()
    {
        var rig = Create();
        rig.Service.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(MethodNotFound());
        await rig.Vm.RefreshAsync();
        rig.Vm.NextStep.Should().Contain("仅支持只读提示").And.Contain("均不可用");
        rig.Vm.KeyboardIntegrationConfirmed.Should().BeTrue();

        rig.Vm.SetConnected(false); rig.Vm.SetConnected(true);
        rig.Service.ReadOverride = null;
        rig.Service.Current = Status("Ready", true, true);
        await rig.Vm.RefreshAsync();
        rig.Vm.IsReady.Should().BeTrue();
        rig.Vm.SetConnected(false);
        rig.Vm.NextStep.Should().Contain("后台未连接").And.Contain("暂不可用");
        rig.Vm.IsReady.Should().BeFalse();

        rig.Vm.SetConnected(true);
        rig.Service.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(new IOException("fake unread"));
        await rig.Vm.RefreshAsync();
        rig.Vm.NextStep.Should().Contain("状态未知、未读到或已过期").And.Contain("新的后台回读");
        rig.Vm.CanExit.Should().BeFalse();
        rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        rig.Service.Writes.Should().BeEmpty();
        rig.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_active_panel_shows_next_step_and_accessible_help_for_both_disabled_buttons()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var errors = new List<Exception>();
        var panel = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var rig = Create();
        panel.AttachPeripheralConfiguration(rig.Vm);
        await rig.Vm.RefreshAsync();
        var buttons = new Dictionary<string, (bool Enabled, string Help)>();
        string? nextStep = null;
        var visible = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = panel };
                view.Measure(new(360, 1000)); view.Arrange(new(0, 0, 360, 1000)); view.UpdateLayout();
                foreach (var button in Logical<System.Windows.Controls.Button>(view))
                    buttons[button.Content?.ToString() ?? string.Empty] = (button.IsEnabled, System.Windows.Automation.AutomationProperties.GetHelpText(button));
                var guidance = Logical<System.Windows.Controls.TextBlock>(view).Single(t => t.Text == rig.Vm.NextStep);
                nextStep = guidance.Text;
                visible = guidance.Visibility == System.Windows.Visibility.Visible;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();

        using (new AssertionScope())
        {
            visible.Should().BeTrue();
            nextStep.Should().Contain("接口未确认释放前");
            buttons.Should().ContainKey("完成配置，恢复联动").WhoseValue.Enabled.Should().BeFalse();
            buttons["完成配置，恢复联动"].Help.Should().Be(rig.Vm.NextStep);
            buttons.Should().ContainKey("打开 Gear Link 网页").WhoseValue.Enabled.Should().BeFalse();
            buttons["打开 Gear Link 网页"].Help.Should().Be(rig.Vm.NextStep);
        }
        rig.Service.Writes.Should().BeEmpty(); rig.Opened.Should().BeEmpty(); errors.Should().BeEmpty();
    }

    private static PeripheralConfigurationStatusDto Status(string state, bool mode, bool released) =>
        new(mode, state, $"fake {state} detail", released, DateTimeOffset.UtcNow, null);

    private static RemoteMethodNotFoundException MethodNotFound() =>
        (RemoteMethodNotFoundException)RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException));

    private static IEnumerable<T> Logical<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    private static Rig Create()
    {
        var clock = new ManualClock();
        var service = new FakePeripherals();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var vm = new PeripheralConfigurationViewModel(() => service, InlineDispatcher.Instance, errors.Add, opened.Add, clock.Now, Long);
        return new(vm, service, clock, errors, opened);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed record Rig(PeripheralConfigurationViewModel Vm, FakePeripherals Service, ManualClock Clock,
        List<Exception> Errors, List<Uri> Opened);

    private sealed class ManualClock
    {
        private DateTimeOffset _value = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now() => _value;
        public void Advance(TimeSpan by) => _value += by;
    }

    private sealed class FakePeripherals : IPeripheralConfigurationService
    {
        public PeripheralConfigurationStatusDto Current { get; set; } = Status("Active", false, false);
        public Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>? ReadOverride { get; set; }
        public List<string> Writes { get; } = [];
        public int Reads { get; private set; }

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Reads++;
            return ReadOverride?.Invoke(ct) ?? Task.FromResult(Current);
        }

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) =>
            Write("enter", Status("Entering", true, false));

        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) =>
            Write("exit", Status("Resuming", false, true));

        private Task<PeripheralConfigurationStatusDto> Write(string name, PeripheralConfigurationStatusDto next)
        {
            Writes.Add(name);
            Current = next;
            return Task.FromResult(next);
        }
    }
}
