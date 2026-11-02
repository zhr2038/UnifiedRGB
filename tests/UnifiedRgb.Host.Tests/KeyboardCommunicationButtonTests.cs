using System.Reflection;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class KeyboardCommunicationButtonTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Recovery_reasserts_the_fresh_display_preference_once_without_case_or_maintenance_writes(bool enabled)
    {
        var (vm, keyboard, maintenance, host, actions) = Create(enabled);
        using (actions)
        {
            await vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
            keyboard.Writes.Should().Equal(enabled); keyboard.Reads.Should().Be(2);
            maintenance.Reads.Should().Be(2); maintenance.Mutations.Should().Be(0); host.Calls.Should().BeEmpty();
            vm.KeyboardToggleLabel.Should().Be(enabled ? "恢复键盘原动画" : "开启光域状态屏");
            vm.KeyboardCommunicationRecoveryNote.Should().NotContain("恢复成功").And.NotContain("已就绪");
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Maintenance_or_unread_status_blocks_recovery_without_clearing_handoff(bool unknown)
    {
        var (vm, keyboard, maintenance, host, actions) = Create(false);
        using (actions)
        {
            maintenance.Unknown = unknown;
            maintenance.Current = new(true, "Ready", "firmware maintenance", true, DateTimeOffset.UtcNow, null);
            await vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
            keyboard.Reads.Should().Be(0); keyboard.Writes.Should().BeEmpty();
            maintenance.Mutations.Should().Be(0); host.Calls.Should().BeEmpty();
            vm.KeyboardCommunicationRecoveryNote.Should().Contain("未发送恢复请求");
        }
    }

    [Fact]
    public async Task A_lost_write_answer_reads_back_without_resend_or_claiming_recovery_complete()
    {
        var (vm, keyboard, maintenance, host, actions) = Create(false);
        using (actions)
        {
            keyboard.LostWriteAnswer = true;
            await vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
            keyboard.Writes.Should().Equal(false); keyboard.Reads.Should().Be(2);
            vm.KeyboardCommunicationRecoveryNote.Should().Contain("结果未确认").And.Contain("未自动重发");
            maintenance.Mutations.Should().Be(0); host.Calls.Should().BeEmpty();
        }
    }

    private static (CaseControlPanelViewModel Vm, KeyboardProxy Keyboard, Maintenance Maintenance, MenuHostFake Host, CasePowerActions Actions) Create(bool enabled)
    {
        var proxy = DispatchProxy.Create<IKeyboardDisplayService, KeyboardProxy>();
        var keyboard = (KeyboardProxy)(object)proxy;
        keyboard.Status = new(enabled, "Stopped", "native control", 0, 0, DateTimeOffset.UtcNow, null);
        var maintenance = new Maintenance(); var host = new MenuHostFake(new SceneDto([])); var ui = new InlineUi();
        var actions = new CasePowerActions(() => host.Connection.Rgb);
        var vm = new CaseControlPanelViewModel(actions, () => host.Connection.Rgb, () => host.Connection.Screens!, ui,
            ex => throw new InvalidOperationException("Unexpected error", ex), keyboardDisplay: () => proxy);
        vm.AttachPeripheralConfiguration(new(() => maintenance, ui, ex => throw new InvalidOperationException("Unexpected error", ex),
            _ => throw new InvalidOperationException("No browser")));
        return (vm, keyboard, maintenance, host, actions);
    }

    private sealed class InlineUi : IUiDispatcher { public void Post(Action action) => action(); }
    public class KeyboardProxy : DispatchProxy
    {
        public KeyboardDisplayStatusDto Status { get; set; } = null!;
        public List<bool> Writes { get; } = [];
        public int Reads { get; private set; }
        public bool LostWriteAnswer { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IKeyboardDisplayService.GetKeyboardDisplayStatusAsync)) { Reads++; return Task.FromResult(Status); }
            if (targetMethod?.Name == nameof(IKeyboardDisplayService.SetKeyboardDisplayEnabledAsync))
            {
                Writes.Add((bool)args![0]!);
                return LostWriteAnswer ? Task.FromException<KeyboardDisplayStatusDto>(new IOException("lost answer")) : Task.FromResult(Status);
            }
            throw new InvalidOperationException("Unexpected keyboard operation: " + targetMethod?.Name);
        }
    }
    private sealed class Maintenance : IPeripheralConfigurationService
    {
        public PeripheralConfigurationStatusDto Current { get; set; } = new(false, "Active", "normal", false, DateTimeOffset.UtcNow, null);
        public bool Unknown { get; set; }
        public int Reads { get; private set; }
        public int Mutations { get; private set; }
        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Reads++; return Unknown ? Task.FromException<PeripheralConfigurationStatusDto>(new IOException("lost status")) : Task.FromResult(Current);
        }
        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) { Mutations++; throw new InvalidOperationException("No handoff"); }
        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) { Mutations++; throw new InvalidOperationException("No exit"); }
    }
}
