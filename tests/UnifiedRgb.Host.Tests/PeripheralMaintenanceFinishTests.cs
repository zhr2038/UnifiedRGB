using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using FluentAssertions.Execution;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Ordinary maintenance finish/retry. The App view model talks to the REAL Host PeripheralConfigurationService (its persisted
/// marker, recovery-journal and fresh-resume guards) over a temp data directory; only the device release/resume is a fake, and
/// LeaseHost stands in for the proposed Host mapping (PM-HOST-1) of the owner's lease (PM-LEASE-1) into the status Intent.
/// No Host process, pipe, PnP/HID device, browser or window.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned tasks script lost answers.
[Collection("Host")]
public sealed class PeripheralMaintenanceFinishTests : IDisposable
{
    private const string Lease = "lease-1";
    private static readonly DateTimeOffset At = new(2026, 10, 27, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "urgb-maintenance-finish-" + Guid.NewGuid().ToString("N"));
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Normal_startup_without_a_requested_handoff_is_active_and_sends_nothing()
    {
        var rig = Create();
        rig.Host.Intent = Owned(PeripheralMaintenanceLeaseStates.Completed); // never attached while nothing is requested
        await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.Status!.State.Should().Be("Active");
            rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.NoHandoff);
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeTrue();
            rig.Vm.HasOriginNote.Should().BeFalse();
            rig.Vm.EnterLabel.Should().Be("配置键鼠（暂时交给网页）"); rig.Vm.ExitLabel.Should().Be("完成配置，恢复联动");
            rig.Host.Writes.Should().BeEmpty();
            rig.Resources.ReleaseCalls.Should().Be(0); rig.Resources.ResumeCalls.Should().Be(0);
            File.Exists(MarkerPath()).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_handoff_error_needs_an_explicit_retry_then_finish_and_resume_alone_leaves_it_unchanged(bool owned)
    {
        // Inherited handoff: Requested=true and a recovery journal, as left while the RF keyboard was asleep.
        Marker().Save();
        var journal = WritePeripheralFile("keyboard-display-session.json", "recovery"u8.ToArray());
        var rig = Create();
        rig.Host.Intent = owned ? Owned(PeripheralMaintenanceLeaseStates.Completed, PeripheralRoles.Rf, PeripheralRoles.Rf, ended: -1) : null;
        await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.Status!.State.Should().Be("Error"); rig.Vm.Status.InterfacesReleased.Should().BeFalse();
            rig.Vm.Finish!.Kind.Should().Be(owned ? PeripheralFinishKind.RetryRequired : PeripheralFinishKind.OriginUnknown);
            rig.Vm.CanEnter.Should().BeTrue("the explicit retry is offered");
            rig.Vm.CanExit.Should().BeFalse("resume cannot clear this Error");
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.EnterLabel.Should().Be(owned ? "重试释放键鼠接口" : "配置键鼠（暂时交给网页）");
            rig.Vm.OriginNote.Should().StartWith(owned ? "交接来源：固件维护（租约 lease-1，已完成" : "交接来源：后台未报告");
            rig.Vm.Guidance.Should().Contain(owned ? "只点恢复联动不会解除这个错误" : "重试释放");
            if (owned) rig.Vm.StateTitle.Should().Be("固件维护遗留的交接出错：需要明确重试释放");
            rig.Host.Writes.Should().BeEmpty("neither a stale Error nor a completed lease sends anything by itself");
        }

        // A supported mode-exit alone leaves the Error, the journal and Requested=true unchanged.
        var exitAlone = await rig.Real.ExitPeripheralConfigurationAsync();
        exitAlone.State.Should().Be("Error"); exitAlone.InterfacesReleased.Should().BeFalse();
        File.Exists(journal).Should().BeTrue(); File.Exists(MarkerPath()).Should().BeTrue();

        // Explicit retry while the keyboard is still asleep: the Host's failure is shown as read, never resent.
        rig.Resources.Release = _ => throw new IOException("fake: keyboard asleep");
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Host.Writes.Should().Equal("enter");
        rig.Vm.Status!.State.Should().Be("Error"); rig.Vm.Status.Detail.Should().Contain("asleep");
        rig.Vm.CanEnter.Should().BeTrue(); rig.Vm.CanExit.Should().BeFalse(); rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse();
        await rig.Vm.RefreshAsync();
        rig.Host.Writes.Should().Equal("enter");

        // Woken: the explicit retry restores from the journal and the Host reports Ready with released interfaces.
        rig.Resources.Release = _ => { File.Delete(journal); return Task.CompletedTask; };
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Vm.Status!.State.Should().Be("Ready"); rig.Vm.Status.InterfacesReleased.Should().BeTrue();
        rig.Host.Writes.Should().Equal("enter", "enter");

        if (owned)
        {
            // Revision 4: a completed lease no longer resumes by itself; the explicit finish below is the only Exit.
            rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse("a maintenance handoff is not a web configuration");
        }
        await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync();
        rig.Host.Writes.Should().Equal(new[] { "enter", "enter" }, "no read sends a resume, whatever the origin");
        rig.Vm.CanExit.Should().BeTrue();
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Host.Writes.Should().Equal("enter", "enter", "exit");
            rig.Vm.Status!.State.Should().Be("Active");
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeTrue();
            File.Exists(MarkerPath()).Should().BeFalse("Requested=false is persisted");
            rig.Resources.ResumeCalls.Should().Be(1);
            rig.Opened.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
        }
        await rig.Vm.RefreshAsync();
        rig.Host.Writes.Should().HaveCount(3);
        // Normal startup with Requested=false: a restarted Host is Active and blocks nothing.
        (await Create().Real.GetPeripheralConfigurationStatusAsync()).Should().Match<PeripheralConfigurationStatusDto>(s => s.State == "Active" && !s.ConfigurationMode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PeripheralHandoffOrigins.Manual)]
    [InlineData("manual-with-completed-lease")]
    public async Task Manual_or_unknown_intent_is_never_cleared_automatically(string? origin)
    {
        var rig = Create();
        rig.Host.Intent = origin switch
        {
            null => null,
            PeripheralHandoffOrigins.Manual => new(PeripheralHandoffOrigins.Manual),
            // A manual request made after an owned maintenance completed is reported as Manual and wins.
            _ => Owned(PeripheralMaintenanceLeaseStates.Completed) with { Origin = PeripheralHandoffOrigins.Manual },
        };
        await rig.Vm.RefreshAsync();
        await rig.Vm.EnterCommand.ExecuteAsync().WaitAsync(Guard); // the user's own Gear Link request
        for (var i = 0; i < 3; i++) { rig.Clock.Advance(TimeSpan.FromSeconds(2)); await rig.Vm.RefreshAsync(); }

        using (new AssertionScope())
        {
            rig.Host.Writes.Should().Equal("enter");
            rig.Vm.Status!.State.Should().Be("Ready");
            rig.Vm.Finish!.Kind.Should().Be(origin is null ? PeripheralFinishKind.OriginUnknown : PeripheralFinishKind.ManualIntent);
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeTrue("the user's web configuration stays available");
            rig.Vm.CanExit.Should().BeTrue();
            rig.Vm.ExitLabel.Should().Be("完成配置，恢复联动");
            rig.Vm.OriginNote.Should().Contain("不会自动清除");
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse();
            File.Exists(MarkerPath()).Should().BeTrue("Requested=true is kept");
        }

        // A restarted Host keeps the persisted request, and a fresh App view model leaves it alone too.
        var restarted = Create();
        restarted.Host.Intent = rig.Host.Intent;
        await restarted.Vm.RefreshAsync(); await restarted.Vm.RefreshAsync();
        restarted.Vm.Status!.State.Should().Be("Ready");
        restarted.Host.Writes.Should().BeEmpty();
        File.Exists(MarkerPath()).Should().BeTrue();
    }

    [Fact]
    public async Task Owned_lease_holds_the_peripherals_until_its_owner_completes_it_then_only_an_explicit_finish_resumes()
    {
        Marker().Save(); // the maintenance owner's begin persisted Requested=true
        var rig = Create();
        rig.Host.Intent = Owned(PeripheralMaintenanceLeaseStates.Active, ended: null);
        await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Vm.Status!.State.Should().Be("Ready"); rig.Vm.IsReady.Should().BeTrue();
            rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.MaintenanceRunning);
            rig.Vm.StateTitle.Should().Be("固件维护进行中：键鼠联动暂停");
            rig.Vm.CanEnter.Should().BeFalse(); rig.Vm.CanExit.Should().BeFalse();
            rig.Vm.EnterCommand.CanExecute(null).Should().BeFalse(); rig.Vm.ExitCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        }
        // Not even a programmatic Execute, which skips CanExecute, reaches the Host while the owner holds the lease.
        await rig.Vm.ExitAsync().WaitAsync(Guard); await rig.Vm.EnterAsync().WaitAsync(Guard);
        rig.Host.Writes.Should().BeEmpty();

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Host.Intent = Owned(PeripheralMaintenanceLeaseStates.Completed, ended: 0);
        for (var i = 0; i < 3; i++) { await rig.Vm.RefreshAsync(); rig.Clock.Advance(TimeSpan.FromSeconds(2)); }
        await rig.Vm.RefreshAsync();

        // Revision 4: the completed lease is shown, never acted on; the Host keeps Requested=true until the user's finish.
        using (new AssertionScope())
        {
            rig.Host.Writes.Should().BeEmpty();
            rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
            rig.Vm.StateTitle.Should().Be("维护程序报告已完成：键鼠接口已释放，需手动恢复联动");
            rig.Vm.Guidance.Should().Contain("界面不会自动恢复联动").And.Contain("没有按维护租约限定的完成接口");
            rig.Vm.NextStep.Should().Be("下一步：确认无误后手动点“完成维护，恢复联动”（不会自动恢复）。");
            rig.Vm.CanExit.Should().BeTrue(); rig.Vm.ExitLabel.Should().Be("完成维护，恢复联动");
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse();
            File.Exists(MarkerPath()).Should().BeTrue();
        }
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Host.Writes.Should().Equal("exit");
            rig.Vm.Status!.State.Should().Be("Active");
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeTrue();
            rig.Vm.Message.Should().Be("后台已答复恢复联动请求，以上为后台返回的实际状态。");
            File.Exists(MarkerPath()).Should().BeFalse();
            rig.Resources.ResumeCalls.Should().Be(1);
            rig.Errors.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
        }
        for (var i = 0; i < 3; i++) { rig.Clock.Advance(TimeSpan.FromSeconds(2)); await rig.Vm.RefreshAsync(); }
        rig.Host.Writes.Should().Equal("exit");
    }

    [Theory]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Failed, -1, 30, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.MaintenanceFailed)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Unknown, -1, 30, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData(Lease, "completed", -1, 30, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Completed, null, 30, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Completed, -5, -10, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData(null, PeripheralMaintenanceLeaseStates.Completed, -1, 30, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Expired, null, -1, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.LeaseExpired)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Active, null, -1, PeripheralRoles.Wired, PeripheralRoles.Wired, PeripheralFinishKind.LeaseExpired)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Completed, -1, 30, PeripheralRoles.Wired, PeripheralRoles.Rf, PeripheralFinishKind.RoleUnmatched)]
    [InlineData(Lease, PeripheralMaintenanceLeaseStates.Completed, -1, 30, PeripheralRoles.Rf, null, PeripheralFinishKind.RoleUnmatched)]
    public async Task Failed_unknown_expired_and_role_changed_leases_never_resume_automatically_and_are_never_ready(
        string? leaseId, string leaseState, int? endedMinutes, int expiresMinutes, string? leaseRole, string? currentRole, PeripheralFinishKind expected)
    {
        Marker().Save();
        var rig = Create();
        rig.Host.Intent = new(PeripheralHandoffOrigins.FirmwareMaintenance, leaseId, "updater", leaseRole, currentRole, leaseState,
            At.AddMinutes(expiresMinutes), endedMinutes is { } e ? At.AddMinutes(e) : null, "fake owner reason");
        for (var i = 0; i < 3; i++) { await rig.Vm.RefreshAsync(); rig.Clock.Advance(TimeSpan.FromSeconds(2)); }
        await rig.Vm.RefreshAsync();

        using (new AssertionScope())
        {
            rig.Host.Writes.Should().BeEmpty();
            rig.Vm.Finish!.Kind.Should().Be(expected);
            rig.Vm.FirmwareMaintenance.Should().BeTrue();
            rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse("nothing here is reported as resumed");
            rig.Vm.StateTitle.Should().NotStartWith("已就绪").And.NotContain("等待恢复联动");
            rig.Vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.CanExit.Should().BeTrue("the user may still resume explicitly through the Host's own guards");
            rig.Vm.ExitLabel.Should().Be("完成维护，恢复联动");
            rig.Vm.NextStep.Should().Contain("手动点“完成维护，恢复联动”");
            File.Exists(MarkerPath()).Should().BeTrue();
        }
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        rig.Host.Writes.Should().Equal("exit");
        rig.Vm.Status!.State.Should().Be("Active");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_finish_with_an_unknown_outcome_is_read_back_once_never_resent_and_never_forced_ready(bool hostApplied)
    {
        Marker().Save();
        var rig = Create(Short);
        rig.Host.Intent = Owned(PeripheralMaintenanceLeaseStates.Completed);
        // The answer is lost and the call ignores cancellation; the Host either applied the resume or never got it.
        rig.Host.NextWrites.Enqueue(async ct =>
        {
            if (hostApplied) await rig.Real.ExitPeripheralConfigurationAsync(ct);
            return await new TaskCompletionSource<PeripheralConfigurationStatusDto>().Task;
        });
        await rig.Vm.RefreshAsync();
        // Revision 4: the completed lease alone sends nothing; the user's explicit finish is the one request.
        rig.Host.Writes.Should().BeEmpty();
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Host.Writes.Should().Equal("exit");
            rig.Vm.Message.Should().Contain("恢复联动结果未确认").And.Contain("未自动重发").And.NotContain("自动恢复联动");
            rig.Vm.Status!.State.Should().Be(hostApplied ? "Active" : "Ready", "the readback, not the lost answer, supplies the state");
            rig.Vm.KeyboardIntegrationConfirmed.Should().Be(hostApplied);
            rig.Errors.Should().BeEmpty();
        }
        for (var i = 0; i < 2; i++) { rig.Clock.Advance(TimeSpan.FromSeconds(2)); await rig.Vm.RefreshAsync(); }
        rig.Vm.SetConnected(false); rig.Vm.SetConnected(true); // a new connection does not resend the unanswered request
        await rig.Vm.RefreshAsync();
        rig.Host.Writes.Should().Equal("exit");
        if (hostApplied) return;

        rig.Vm.CanExit.Should().BeTrue();
        rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
        rig.Vm.NextStep.Should().Be("下一步：确认无误后手动点“完成维护，恢复联动”（不会自动恢复）。");
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard); // a second, new explicit click
        rig.Host.Writes.Should().Equal("exit", "exit");
        rig.Vm.Status!.State.Should().Be("Active");
    }

    [Fact]
    public async Task Explicit_finish_refused_by_the_unknown_journal_guard_is_shown_as_the_hosts_error_and_not_retried()
    {
        Marker().Save();
        var rig = Create();
        rig.Host.Intent = Owned(PeripheralMaintenanceLeaseStates.Completed);
        var journal = WritePeripheralFile("keyboard-display-session.json", "unknown-recovery"u8.ToArray()); // appeared after Host start
        await rig.Vm.RefreshAsync();
        // Revision 4: nothing is sent by the read; the guard is reached only through the user's explicit finish.
        rig.Host.Writes.Should().BeEmpty();
        rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
        await rig.Vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Host.Writes.Should().Equal("exit");
            rig.Vm.Status!.State.Should().Be("Error"); rig.Vm.Status.InterfacesReleased.Should().BeFalse();
            rig.Vm.Status.Detail.Should().Contain("recovery journal");
            rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.RetryRequired);
            rig.Vm.IsReady.Should().BeFalse(); rig.Vm.KeyboardIntegrationConfirmed.Should().BeFalse();
            rig.Vm.CanExit.Should().BeFalse(); rig.Vm.CanEnter.Should().BeTrue();
            File.Exists(journal).Should().BeTrue("the journal is never deleted or replayed by the App");
            File.Exists(MarkerPath()).Should().BeTrue();
            rig.Resources.ResumeCalls.Should().Be(0);
            rig.Errors.Should().BeEmpty();
        }
        await rig.Vm.RefreshAsync();
        rig.Host.Writes.Should().Equal("exit");
    }

    [Fact]
    public void Intent_is_optional_on_the_pipe_and_an_older_host_reads_as_unknown_origin()
    {
        var plain = new PeripheralConfigurationStatusDto(true, "Ready", "released", true, At, null);
        var json = JsonSerializer.Serialize(plain, ContractsJsonContext.Default.PeripheralConfigurationStatusDto);
        json.Should().NotContain("intent", "a status without an intent keeps the frozen shape");
        const string older = """{"configurationMode":true,"state":"Ready","detail":"released","interfacesReleased":true,"updatedAt":null,"lastError":null}""";
        var parsed = JsonSerializer.Deserialize(older, ContractsJsonContext.Default.PeripheralConfigurationStatusDto)!;
        parsed.Intent.Should().BeNull();
        PeripheralMaintenanceFinish.Evaluate(parsed, At).Kind.Should().Be(PeripheralFinishKind.OriginUnknown);

        var withIntent = plain with { Intent = new(PeripheralHandoffOrigins.FirmwareMaintenance, Lease, "updater", PeripheralRoles.Rf, PeripheralRoles.Rf,
            PeripheralMaintenanceLeaseStates.Completed, At.AddMinutes(30), At, null) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(withIntent, ContractsJsonContext.Default.PeripheralConfigurationStatusDto);
        JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.PeripheralConfigurationStatusDto).Should().Be(withIntent);
        PeripheralMaintenanceFinish.Evaluate(withIntent, At).Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
        PeripheralMaintenanceFinish.Evaluate(withIntent with { Intent = withIntent.Intent! with { Origin = "firmwaremaintenance" } }, At)
            .Kind.Should().Be(PeripheralFinishKind.OriginUnknown, "origins are exact words");
        typeof(IPeripheralConfigurationService).GetMethods().Should().HaveCount(3, "no RPC method is added");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_panel_xaml_names_the_maintenance_retry_and_finish_and_keeps_the_web_handoff_labels_otherwise(bool owned)
    {
        Marker().Save();
        WritePeripheralFile("keyboard-display-session.json", "recovery"u8.ToArray());
        var rig = Create();
        rig.Host.Intent = owned ? Owned(PeripheralMaintenanceLeaseStates.Completed, PeripheralRoles.Rf, PeripheralRoles.Rf) : null;
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var panel = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, rig.Errors.Add);
        panel.AttachPeripheralConfiguration(rig.Vm); // attaching starts a new connection epoch, so read after it
        await rig.Vm.RefreshAsync();

        var (buttons, texts) = Render(panel);
        using (new AssertionScope())
        {
            buttons.Should().ContainKey(owned ? "重试释放键鼠接口" : "配置键鼠（暂时交给网页）").WhoseValue.Should().BeTrue();
            buttons.Should().ContainKey(owned ? "完成维护，恢复联动" : "完成配置，恢复联动").WhoseValue.Should().BeFalse();
            buttons.Should().ContainKey("打开 Gear Link 网页").WhoseValue.Should().BeFalse();
            texts.Should().Contain(owned
                ? "交接来源：固件维护（租约 lease-1，已完成；维护时连接方式 2.4G 无线，当前 2.4G 无线）。"
                : "交接来源：后台未报告（可能是手动 Gear Link，也可能是固件维护遗留）；不会自动清除，只按你的明确操作重试或恢复。");
            if (owned) texts.Should().Contain(t => t.StartsWith("只点恢复联动不会解除这个错误", StringComparison.Ordinal));
        }
        rig.Host.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_panel_xaml_shows_a_completed_maintenance_as_an_explicit_finish_without_claiming_automatic_recovery()
    {
        Marker().Save();
        var rig = Create();
        rig.Host.Intent = Owned(PeripheralMaintenanceLeaseStates.Completed, PeripheralRoles.Rf, PeripheralRoles.Rf);
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var panel = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, rig.Errors.Add);
        panel.AttachPeripheralConfiguration(rig.Vm);
        await rig.Vm.RefreshAsync();

        var (buttons, texts) = Render(panel);
        using (new AssertionScope())
        {
            rig.Vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
            buttons.Should().ContainKey("完成维护，恢复联动").WhoseValue.Should().BeTrue("the user's explicit finish stays available");
            buttons.Should().ContainKey("重试释放键鼠接口").WhoseValue.Should().BeFalse();
            buttons.Should().ContainKey("打开 Gear Link 网页").WhoseValue.Should().BeFalse();
            texts.Should().Contain("维护程序报告已完成：键鼠接口已释放，需手动恢复联动");
            texts.Should().Contain(t => t.Contains("界面不会自动恢复联动", StringComparison.Ordinal));
            texts.Should().NotContain(t => t.Contains("正在自动", StringComparison.Ordinal) || t.Contains("等待自动", StringComparison.Ordinal)
                || t.Contains("自动请求", StringComparison.Ordinal));
        }
        rig.Host.Writes.Should().BeEmpty();
        File.Exists(MarkerPath()).Should().BeTrue();
    }

    // Loads the actual panel XAML on an STA thread without a window; collects the shown peripheral buttons and texts.
    private static (Dictionary<string, bool> Buttons, List<string> Texts) Render(CaseControlPanelViewModel vm)
    {
        var buttons = new Dictionary<string, bool>();
        var texts = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = vm };
                view.Measure(new(300, 2400)); view.Arrange(new(0, 0, 300, 2400)); view.UpdateLayout();
                foreach (var (button, shown) in Logical<System.Windows.Controls.Button>(view, true))
                    if (shown && button.DataContext is PeripheralConfigurationViewModel) buttons[button.Content?.ToString() ?? string.Empty] = button.IsEnabled;
                foreach (var (text, shown) in Logical<System.Windows.Controls.TextBlock>(view, true))
                    if (shown && text.DataContext is PeripheralConfigurationViewModel) texts.Add(text.Text);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
        return (buttons, texts);
    }

    private static IEnumerable<(T Element, bool Shown)> Logical<T>(System.Windows.DependencyObject root, bool shown)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            var visible = shown && (child is not System.Windows.UIElement element || element.Visibility == System.Windows.Visibility.Visible);
            if (child is T match) yield return (match, visible);
            foreach (var nested in Logical<T>(child, visible)) yield return nested;
        }
    }

    private static PeripheralHandoffIntentDto Owned(string state, string role = PeripheralRoles.Wired, string? current = PeripheralRoles.Wired, int? ended = -1) =>
        new(PeripheralHandoffOrigins.FirmwareMaintenance, Lease, "updater", role, current, state, At.AddMinutes(30),
            ended is { } e ? At.AddMinutes(e) : null);

    private Rig Create(TimeSpan? timeout = null)
    {
        var options = new UnifiedRgbOptions { DataDirectory = _directory };
        var gate = new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load());
        var resources = new FakeResources();
        var real = new PeripheralConfigurationService(options, gate, resources, TimeSpan.FromSeconds(5));
        var host = new LeaseHost(real);
        var clock = new ManualClock();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var vm = new PeripheralConfigurationViewModel(() => host, InlineDispatcher.Instance, errors.Add, opened.Add, clock.Now, timeout ?? Long);
        return new(vm, host, real, resources, clock, errors, opened);
    }

    private PeripheralConfigurationMarker Marker() => PeripheralConfigurationMarker.For(new UnifiedRgbOptions { DataDirectory = _directory });

    private string MarkerPath() => Path.Combine(_directory, "peripherals", "peripheral-configuration.json");

    private string WritePeripheralFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, "peripherals", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        foreach (var actions in _owned) actions.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed record Rig(PeripheralConfigurationViewModel Vm, LeaseHost Host, PeripheralConfigurationService Real, FakeResources Resources,
        ManualClock Clock, List<Exception> Errors, List<Uri> Opened);

    private sealed class ManualClock
    {
        private DateTimeOffset _value = At;
        public DateTimeOffset Now() => _value;
        public void Advance(TimeSpan by) => _value += by;
    }

    /// <summary>
    /// The real Host service plus the proposed lease mapping: the owner's intent is attached only while a handoff is requested.
    /// NextWrites replaces one write with a scripted one (for example a lost answer).
    /// </summary>
    private sealed class LeaseHost(IPeripheralConfigurationService inner) : IPeripheralConfigurationService
    {
        public PeripheralHandoffIntentDto? Intent { get; set; }
        public List<string> Writes { get; } = [];
        public ConcurrentQueue<Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>> NextWrites { get; } = new();

        public async Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default) =>
            Attach(await inner.GetPeripheralConfigurationStatusAsync(ct).ConfigureAwait(false));

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) =>
            WriteAsync("enter", inner.EnterPeripheralConfigurationAsync, ct);

        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) =>
            WriteAsync("exit", inner.ExitPeripheralConfigurationAsync, ct);

        private async Task<PeripheralConfigurationStatusDto> WriteAsync(string name, Func<CancellationToken, Task<PeripheralConfigurationStatusDto>> call, CancellationToken ct)
        {
            lock (Writes) Writes.Add(name);
            if (NextWrites.TryDequeue(out var scripted)) call = scripted;
            return Attach(await call(ct).ConfigureAwait(false));
        }

        private PeripheralConfigurationStatusDto Attach(PeripheralConfigurationStatusDto status) =>
            status.ConfigurationMode && Intent is { } intent ? status with { Intent = intent } : status;
    }

    private sealed class FakeResources : IPeripheralConfigurationResources
    {
        public int ReleaseCalls;
        public int ResumeCalls;
        public Func<CancellationToken, Task>? Release;

        public async Task ReleaseAsync(CancellationToken ct)
        {
            ReleaseCalls++;
            if (Release is not null) await Release(ct).ConfigureAwait(false);
        }

        public Task ResumeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ResumeCalls++;
            return Task.CompletedTask;
        }
    }
}
