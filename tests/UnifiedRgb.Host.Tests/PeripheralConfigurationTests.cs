using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using StreamJsonRpc;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Ipc;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fake-only App/MVVM/RPC checks for the optional keyboard/mouse handoff section. No Host process, pipe, device or browser:
/// every status comes from a scripted fake, and nothing here simulates an interface release.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned completion sources control old-connection answers.
[Collection("Host")]
public sealed class PeripheralConfigurationTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly List<CasePowerActions> _owned = [];

    [Fact]
    public async Task Supported_status_shows_the_actual_service_state_detail_and_last_error_without_writing()
    {
        var (vm, service, errors, opened) = Create(Long);
        service.Current = Status("Active", false, false, "fake last failure");
        await vm.RefreshAsync();

        vm.Status.Should().Be(service.Current);
        vm.StateTitle.Should().Be("键鼠联动运行中");
        vm.StatusLine.Should().Contain("后台状态 Active").And.Contain("已请求交接：否").And.Contain("接口已释放：否");
        vm.Detail.Should().Be("fake Active detail");
        vm.LastErrorText.Should().Be("最近错误：fake last failure");
        vm.CanEnter.Should().BeTrue(); vm.CanExit.Should().BeFalse(); vm.IsReady.Should().BeFalse();
        vm.EnterCommand.CanExecute(null).Should().BeTrue(); vm.ExitCommand.CanExecute(null).Should().BeFalse();
        vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        vm.KeyboardIntegrationConfirmed.Should().BeTrue();
        service.Writes.Should().BeEmpty(); errors.Should().BeEmpty(); opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Old_host_without_the_endpoint_gets_a_read_only_message_and_is_never_probed_again_or_mutated()
    {
        var (vm, service, errors, _) = Create(Long);
        service.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(MethodNotFound());
        await vm.RefreshAsync();

        vm.IsUnsupported.Should().BeTrue();
        vm.Message.Should().Be(PeripheralConfigurationViewModel.UnsupportedMessage);
        vm.StateTitle.Should().Be("当前后台不支持外设配置");
        vm.CanEnter.Should().BeFalse(); vm.CanExit.Should().BeFalse(); vm.IsReady.Should().BeFalse();
        vm.KeyboardIntegrationConfirmed.Should().BeTrue("an old Host has no handoff that could hold the keyboard");

        await vm.RefreshAsync(); await vm.EnterAsync(); await vm.ExitAsync();
        await vm.EnterCommand.ExecuteAsync(); await vm.ExitCommand.ExecuteAsync();
        service.Reads.Should().Be(1, "the old Host is not probed again on the same connection");
        service.Writes.Should().BeEmpty(); errors.Should().BeEmpty();

        // A new connection may reach a newer Host: one read-only status read, still no request.
        vm.SetConnected(false); vm.SetConnected(true);
        service.ReadOverride = null;
        await vm.RefreshAsync();
        vm.IsUnsupported.Should().BeFalse(); vm.StateTitle.Should().Be("键鼠联动运行中");
        service.Reads.Should().Be(2); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Old_host_without_the_request_endpoint_gets_one_attempt_and_no_fabricated_state()
    {
        var (vm, service, errors, _) = Create(Long);
        await vm.RefreshAsync();
        service.WriteOverride = (_, _) => Task.FromException<PeripheralConfigurationStatusDto>(MethodNotFound());
        await Run(vm.EnterCommand);

        service.Writes.Should().Equal("enter");
        service.Reads.Should().Be(1, "method-not-found means nothing applied, so there is nothing to read back");
        vm.IsUnsupported.Should().BeTrue(); vm.HasStatus.Should().BeFalse(); vm.IsReady.Should().BeFalse();
        vm.Message.Should().Be(PeripheralConfigurationViewModel.UnsupportedMessage);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnected_client_sends_nothing_and_shows_unknown()
    {
        var fake = new FakePeripherals();
        IPeripheralConfigurationService? live = null;
        var (vm, _, errors, _) = Create(Long, () => live ?? throw new HostNotConnectedException());
        await vm.RefreshAsync();
        vm.HasStatus.Should().BeFalse(); vm.Message.Should().Contain("后台未连接");
        vm.CanEnter.Should().BeFalse(); vm.KeyboardIntegrationConfirmed.Should().BeFalse();

        // Connected and read, but the proxy is gone by the time of the click: nothing is sent and the old status is dropped.
        live = fake;
        await vm.RefreshAsync(); vm.CanEnter.Should().BeTrue();
        live = null;
        await Run(vm.EnterCommand);
        fake.Writes.Should().BeEmpty();
        vm.HasStatus.Should().BeFalse();
        vm.Message.Should().Contain("后台未连接").And.Contain("未发送交接键鼠请求");

        vm.SetConnected(false);
        vm.StateTitle.Should().Be("后台未连接");
        live = fake;
        await vm.RefreshAsync(); await vm.EnterAsync(); await vm.ExitAsync();
        vm.EnterCommand.CanExecute(null).Should().BeFalse(); vm.ExitCommand.CanExecute(null).Should().BeFalse();
        fake.Reads.Should().Be(1); fake.Writes.Should().BeEmpty(); errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Entering", true, false)]
    [InlineData("Entering", true, true)]
    [InlineData("Ready", true, false)]
    [InlineData("Ready", false, true)]
    [InlineData("Active", true, false)]
    [InlineData("Resuming", false, true)]
    [InlineData("Error", true, true)]
    [InlineData("Unexpected", true, true)]
    public async Task Requested_mode_or_a_lone_release_bit_is_never_presented_as_ready(string state, bool mode, bool released)
    {
        var (vm, service, _, opened) = Create(Long);
        service.Current = Status(state, mode, released);
        await vm.RefreshAsync();

        vm.IsReady.Should().BeFalse();
        vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        vm.StateTitle.Should().NotContain("已就绪");
        vm.KeyboardIntegrationConfirmed.Should().BeFalse("normal keyboard/mouse integration is not confirmed");
        vm.OpenGearLinkCommand.Execute(null);
        opened.Should().BeEmpty();

        service.Current = ReadyStatus();
        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue(); vm.StateTitle.Should().StartWith("已就绪");
        opened.Should().BeEmpty("Ready never opens the web page by itself");
        service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Enter_sends_one_request_shows_entering_and_offers_the_web_page_only_after_actual_release()
    {
        var (vm, service, _, opened) = Create(Long);
        await vm.RefreshAsync();
        var notes = Notes(vm);
        await Run(vm.EnterCommand);

        service.Writes.Should().Equal("enter");
        lock (notes) notes.Should().Contain(n => n.Contains("正在请求交接键鼠"));
        vm.Status!.State.Should().Be("Entering");
        vm.StateTitle.Should().Be("正在交接：等待后台实际释放键鼠接口");
        vm.Guidance.Should().Contain("不代表接口已释放");
        vm.IsReady.Should().BeFalse(); vm.CanEnter.Should().BeFalse(); vm.CanExit.Should().BeTrue();

        service.Current = ReadyStatus();
        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue();
        vm.OpenGearLinkCommand.CanExecute(null).Should().BeTrue();
        opened.Should().BeEmpty("the web page opens only on an explicit click");

        vm.OpenGearLinkCommand.Execute(null);
        opened.Should().Equal(new Uri("https://gearlink.asus.com"));
        service.Writes.Should().Equal("enter"); service.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Repeated_clicks_and_polls_during_one_request_send_exactly_one_write_and_keep_its_feedback()
    {
        var (vm, service, _, _) = Create(Long);
        await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.WriteOverride = (_, _) => { entered.TrySetResult(); return release.Task; };
        var first = vm.EnterCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);

        vm.CanEnter.Should().BeFalse(); vm.CanExit.Should().BeFalse();
        await vm.EnterCommand.ExecuteAsync(); await vm.EnterAsync(); await vm.ExitAsync(); await vm.ExitCommand.ExecuteAsync();
        await vm.RefreshAsync().WaitAsync(Guard);
        service.Writes.Should().Equal("enter");
        service.Reads.Should().Be(1, "the in-flight request owns the feedback until its own answer");
        vm.Message.Should().Contain("正在请求交接键鼠");

        release.SetResult(EnteringStatus());
        await first.WaitAsync(Guard);
        service.Writes.Should().Equal("enter");
        vm.Status!.State.Should().Be("Entering");
    }

    [Fact]
    public async Task Exit_retires_ready_at_once_then_shows_resuming_and_returns_to_active_from_reads()
    {
        var (vm, service, _, opened) = Create(Long);
        service.Current = ReadyStatus();
        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.WriteOverride = (_, _) => { entered.TrySetResult(); return release.Task; };
        var exit = vm.ExitCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);

        vm.IsReady.Should().BeFalse("no earlier Ready may stay on screen once resuming is requested");
        vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        vm.HasStatus.Should().BeFalse(); vm.StateTitle.Should().Be("正在等待后台答复");
        vm.Message.Should().Contain("正在请求恢复联动");
        vm.KeyboardIntegrationConfirmed.Should().BeFalse();
        vm.OpenGearLinkCommand.Execute(null); opened.Should().BeEmpty();

        release.SetResult(ResumingStatus());
        await exit.WaitAsync(Guard);
        service.Writes.Should().Equal("exit");
        vm.StateTitle.Should().Be("正在恢复键鼠联动"); vm.IsReady.Should().BeFalse();
        vm.CanEnter.Should().BeFalse(); vm.CanExit.Should().BeFalse();
        vm.KeyboardIntegrationConfirmed.Should().BeFalse("resuming is not a confirmed resume");

        service.Current = ActiveStatus();
        await vm.RefreshAsync();
        vm.StateTitle.Should().Be("键鼠联动运行中"); vm.CanEnter.Should().BeTrue();
        vm.KeyboardIntegrationConfirmed.Should().BeTrue();
        service.Writes.Should().Equal("exit");
    }

    // Revision 3: a fresh Error always offers the explicit Enter retry; resume is withheld while a requested handoff is
    // unreleased (the previous expectation, Exit only for requested mode, was the defect).
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Error_status_shows_the_service_error_and_offers_recovery(bool mode, bool canExit)
    {
        var (vm, service, _, _) = Create(Long);
        service.Current = Status("Error", mode, false, "fake release failed");
        await vm.RefreshAsync();

        vm.StateTitle.Should().Be("外设配置出错");
        vm.StatusLine.Should().Contain("后台状态 Error");
        vm.LastErrorText.Should().Be("最近错误：fake release failed");
        vm.CanEnter.Should().BeTrue(); vm.CanExit.Should().Be(canExit);
        vm.IsReady.Should().BeFalse(); vm.KeyboardIntegrationConfirmed.Should().BeFalse();
        service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_ignoring_cancellation_is_bounded_read_back_once_and_never_resent()
    {
        var (vm, service, errors, _) = Create(Short);
        await vm.RefreshAsync();
        var notes = Notes(vm);
        var forwarded = CancellationToken.None;
        // The fake applies the request, then loses the answer and ignores cancellation.
        service.WriteOverride = (name, ct) => { forwarded = ct; service.Apply(name); return Never<PeripheralConfigurationStatusDto>(); };
        await Run(vm.EnterCommand);

        service.Writes.Should().Equal("enter");
        forwarded.IsCancellationRequested.Should().BeTrue("the deadline is still forwarded to cooperative services");
        service.Reads.Should().Be(2, "the initial read and one bounded readback on the captured connection");
        lock (notes) notes.Should().Contain(n => n.Contains("结果未确认") && n.Contains("未自动重发") && n.Contains("正在回读"));
        vm.Status!.State.Should().Be("Entering", "the readback, not the lost answer, supplies the state");
        vm.Message.Should().Contain("未自动重发");
        vm.IsReady.Should().BeFalse(); errors.Should().BeEmpty();
        await Task.Delay(Short * 3);
        service.Writes.Should().Equal(["enter"], "an unanswered request is never resent");
    }

    [Fact]
    public async Task Unknown_request_with_failed_readback_shows_unknown_and_keeps_every_affordance_off()
    {
        var (vm, service, _, _) = Create(Short);
        service.Current = ReadyStatus();
        await vm.RefreshAsync();
        service.WriteOverride = (_, _) => Never<PeripheralConfigurationStatusDto>();
        service.NextReads.Enqueue(_ => Never<PeripheralConfigurationStatusDto>());
        await Run(vm.ExitCommand);

        service.Writes.Should().Equal("exit"); service.Reads.Should().Be(2);
        vm.HasStatus.Should().BeFalse(); vm.IsReady.Should().BeFalse();
        vm.Message.Should().Contain("结果未知").And.Contain("未自动重发");
        vm.CanEnter.Should().BeFalse(); vm.CanExit.Should().BeFalse();
        vm.KeyboardIntegrationConfirmed.Should().BeFalse();
        await Task.Delay(Short * 3);
        service.Writes.Should().HaveCount(1);

        // The next poll is a plain read: it recovers the actual state without replaying the request.
        service.Current = ActiveStatus();
        await vm.RefreshAsync();
        vm.StateTitle.Should().Be("键鼠联动运行中"); service.Writes.Should().Equal("exit");
    }

    [Fact]
    public async Task Host_rejected_or_busy_request_is_reported_and_read_back_without_resend()
    {
        var (vm, service, errors, _) = Create(Long);
        await vm.RefreshAsync();
        service.WriteOverride = (_, _) => Task.FromException<PeripheralConfigurationStatusDto>(
            new RemoteInvocationException("fake peripheral handoff busy", 0, (object?)null));
        await Run(vm.EnterCommand);

        service.Writes.Should().Equal("enter");
        service.Reads.Should().Be(2, "the observed state is reread instead of assumed");
        errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>();
        vm.StateTitle.Should().Be("键鼠联动运行中");
        vm.Message.Should().Contain("返回错误");
        vm.CanEnter.Should().BeTrue("only a later explicit click may try again");
    }

    [Fact]
    public async Task Request_and_its_readback_never_move_to_a_newer_connection_proxy()
    {
        var oldPipe = new FakePeripherals();
        var newPipe = new FakePeripherals();
        IPeripheralConfigurationService current = oldPipe;
        var (vm, _, _, _) = Create(Long, () => current);
        await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldPipe.WriteOverride = (_, _) => { entered.TrySetResult(); return lost.Task; };
        var enter = vm.EnterCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);

        // The pipe reconnects before the view model observes the connection change.
        current = newPipe;
        oldPipe.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(new ConnectionLostException("fake old pipe closed"));
        lost.SetException(new ConnectionLostException("fake old pipe closed"));
        await enter.WaitAsync(Guard);

        oldPipe.Writes.Should().Equal("enter");
        newPipe.Writes.Should().BeEmpty();
        newPipe.Reads.Should().Be(0, "the readback stays on the connection captured for this intent");
        vm.Message.Should().Contain("结果未知"); vm.HasStatus.Should().BeFalse();

        // Once the reconnect is observed, a plain read of the new connection shows what actually applied.
        vm.SetConnected(false); vm.SetConnected(true);
        newPipe.Current = EnteringStatus();
        await vm.RefreshAsync();
        newPipe.Reads.Should().Be(1); newPipe.Writes.Should().BeEmpty();
        vm.StateTitle.Should().Be("正在交接：等待后台实际释放键鼠接口");
        oldPipe.Writes.Should().Equal("enter");
    }

    [Fact]
    public async Task Reconnect_during_request_discards_the_late_answer_and_reads_the_current_connection_once()
    {
        var oldPipe = new FakePeripherals();
        var newPipe = new FakePeripherals { Current = EnteringStatus() };
        IPeripheralConfigurationService current = oldPipe;
        var (vm, _, _, opened) = Create(Long, () => current);
        await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldPipe.WriteOverride = (_, _) => { entered.TrySetResult(); return answer.Task; };
        var enter = vm.EnterCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);

        vm.SetConnected(false); current = newPipe; vm.SetConnected(true);
        answer.SetResult(ReadyStatus()); // A late Ready from the old connection.
        await enter.WaitAsync(Guard);

        vm.IsReady.Should().BeFalse("a late answer from the old connection never becomes the current state");
        oldPipe.Reads.Should().Be(1, "nothing is read back through the old connection");
        newPipe.Reads.Should().Be(1, "the current connection is read, read-only");
        oldPipe.Writes.Should().Equal("enter"); newPipe.Writes.Should().BeEmpty();
        vm.StateTitle.Should().Be("正在交接：等待后台实际释放键鼠接口");
        opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnect_during_request_skips_every_readback_and_never_resends()
    {
        var (vm, service, _, _) = Create(Long);
        await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.WriteOverride = (name, _) => { service.Apply(name); entered.TrySetResult(); return lost.Task; };
        var enter = vm.EnterCommand.ExecuteAsync();
        await entered.Task.WaitAsync(Guard);
        vm.SetConnected(false);
        lost.SetException(new ConnectionLostException("fake pipe closed"));
        await enter.WaitAsync(Guard);

        service.Writes.Should().Equal("enter");
        service.Reads.Should().Be(1, "no readback through the closed connection");
        vm.StateTitle.Should().Be("后台未连接"); vm.HasStatus.Should().BeFalse();
        vm.KeyboardIntegrationConfirmed.Should().BeFalse();

        vm.SetConnected(true);
        await vm.RefreshAsync();
        vm.StateTitle.Should().Be("正在交接：等待后台实际释放键鼠接口", "a fresh read on the new connection shows what actually applied");
        service.Writes.Should().Equal("enter");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_status_from_an_old_connection_never_shows_ready(bool reconnect)
    {
        var (vm, service, _, _) = Create(Long);
        service.Current = ReadyStatus();
        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue();
        var late = Hold(service);
        var refresh = vm.RefreshAsync();
        vm.SetConnected(false);
        vm.IsReady.Should().BeFalse(); vm.HasStatus.Should().BeFalse(); vm.StateTitle.Should().Be("后台未连接");
        if (reconnect) vm.SetConnected(true);

        late.SetResult(ReadyStatus());
        await refresh.WaitAsync(Guard);
        vm.IsReady.Should().BeFalse(); vm.HasStatus.Should().BeFalse();
        vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        if (!reconnect) return;

        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue("a read started on the new connection is current");
    }

    [Fact]
    public async Task Late_refresh_cannot_overwrite_a_newer_request_answer()
    {
        var (vm, service, _, _) = Create(Long);
        await vm.RefreshAsync();
        var late = Hold(service);
        var refresh = vm.RefreshAsync();
        await Run(vm.EnterCommand);
        vm.Status!.State.Should().Be("Entering");

        late.SetResult(ActiveStatus());
        await refresh.WaitAsync(Guard);
        vm.Status!.State.Should().Be("Entering", "the read started before the request and is older");
        vm.KeyboardIntegrationConfirmed.Should().BeFalse();
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("hang")]
    [InlineData("expire")]
    public async Task Failed_hung_or_expired_reads_clear_ready_and_a_later_read_recovers(string mode)
    {
        var time = DateTimeOffset.UtcNow;
        var (vm, service, errors, _) = Create(Short, now: () => time);
        service.Current = ReadyStatus();
        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue();

        if (mode == "expire")
        {
            time += TimeSpan.FromSeconds(11);
            vm.ExpireObservation();
        }
        else
        {
            // A hanging fake ignores cancellation; the deadline must still end the read.
            service.NextReads.Enqueue(_ => mode == "hang" ? Never<PeripheralConfigurationStatusDto>()
                : Task.FromException<PeripheralConfigurationStatusDto>(new IOException("fake status lost")));
            await vm.RefreshAsync().WaitAsync(Guard);
        }
        vm.IsReady.Should().BeFalse(); vm.HasStatus.Should().BeFalse();
        vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
        vm.Message.Should().Contain(mode == "expire" ? "已过期" : "未读到");
        errors.Should().BeEmpty(); service.Writes.Should().BeEmpty();

        await vm.RefreshAsync();
        vm.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task Keyboard_toggle_is_held_while_a_handoff_may_be_active_and_returns_after_a_confirmed_resume()
    {
        var rig = CreatePanel();
        await rig.Panel.RefreshAsync();
        rig.Panel.HasKeyboardDisplayService.Should().BeTrue();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse("this connection has not confirmed normal keyboard/mouse integration yet");
        rig.Panel.ToggleKeyboardDisplayCommand.CanExecute(null).Should().BeFalse();
        rig.Panel.KeyboardTransportNote.Should().Contain("外设配置状态未确认");

        await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        rig.Panel.ToggleKeyboardDisplayCommand.CanExecute(null).Should().BeTrue();
        rig.Panel.KeyboardTransportNote.Should().Contain("已确认 3 幅画面");

        await Run(rig.Peripherals.EnterCommand);
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
        rig.Panel.ToggleKeyboardDisplayCommand.CanExecute(null).Should().BeFalse();
        rig.Panel.KeyboardTransportNote.Should().Contain("外设配置中");
        await Run(rig.Panel.ToggleKeyboardDisplayCommand);
        rig.Keyboard.Writes.Should().BeEmpty("no keyboard activity is offered or sent during the handoff");

        rig.Service.Current = ReadyStatus();
        await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
        await Run(rig.Peripherals.ExitCommand);
        rig.Peripherals.StateTitle.Should().Be("正在恢复键鼠联动");
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse("resuming is not yet a confirmed resume");

        rig.Service.Current = ActiveStatus();
        await rig.Peripherals.RefreshAsync();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        await Run(rig.Panel.ToggleKeyboardDisplayCommand);
        rig.Keyboard.Writes.Should().Equal(true);
        rig.Service.Writes.Should().Equal("enter", "exit");
        rig.Rpc.Writes.Should().BeEmpty("the handoff never touches the case scene or saved settings");
        rig.Panel.Display.State.Should().Be(CaseControlState.Active);
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Handoff_that_begins_during_the_keyboard_preflight_prevents_the_keyboard_write()
    {
        var rig = CreatePanel();
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();
        var preflight = new TaskCompletionSource<KeyboardDisplayStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.NextReads.Enqueue(_ => preflight.Task);
        var toggle = rig.Panel.ToggleKeyboardDisplayCommand.ExecuteAsync();
        await Run(rig.Peripherals.EnterCommand);

        preflight.SetResult(KeyboardStatus(false, 4));
        await toggle.WaitAsync(Guard);
        rig.Keyboard.Writes.Should().BeEmpty();
        rig.Panel.HasKeyboardDisplayService.Should().BeTrue("the preflight read itself is still published");
        rig.Panel.CanToggleKeyboardDisplay.Should().BeFalse();
        rig.Panel.KeyboardTransportNote.Should().Contain("外设配置中");
    }

    [Fact]
    public async Task Old_host_keeps_the_existing_keyboard_toggle_and_case_readback()
    {
        var rig = CreatePanel();
        rig.Service.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(MethodNotFound());
        await rig.Panel.RefreshAsync(); await rig.Peripherals.RefreshAsync();

        rig.Peripherals.IsUnsupported.Should().BeTrue();
        rig.Panel.CanToggleKeyboardDisplay.Should().BeTrue();
        rig.Panel.KeyboardTransportNote.Should().Contain("已确认 3 幅画面");
        await Run(rig.Panel.ToggleKeyboardDisplayCommand);
        rig.Keyboard.Writes.Should().Equal(true);
        rig.Panel.KeyboardToggleLabel.Should().Be("恢复键盘原动画");
        rig.Service.Writes.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty();
        rig.Panel.Display.State.Should().Be(CaseControlState.Active);
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Panel_connection_changes_reach_the_section_and_a_hung_section_read_never_delays_the_case_readback()
    {
        var rig = CreatePanel(Short);
        rig.Service.Current = ReadyStatus();
        await rig.Peripherals.RefreshAsync();
        rig.Peripherals.IsReady.Should().BeTrue();
        rig.Panel.SetConnected(false);
        rig.Peripherals.IsReady.Should().BeFalse(); rig.Peripherals.StateTitle.Should().Be("后台未连接");
        rig.Panel.SetConnected(true);
        rig.Peripherals.HasStatus.Should().BeFalse("a reconnect never reuses the old observation");

        rig.Service.NextReads.Enqueue(_ => Never<PeripheralConfigurationStatusDto>());
        var hung = rig.Peripherals.RefreshAsync();
        await rig.Panel.RefreshAsync().WaitAsync(Guard);
        rig.Panel.Display.State.Should().Be(CaseControlState.Active, "the case readback is independent of the optional section");
        await hung.WaitAsync(Guard);
        rig.Peripherals.HasStatus.Should().BeFalse();
        rig.Rpc.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Json_rpc_old_host_answers_method_not_found_and_the_section_stays_read_only()
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        server.StartListening(); // An older Host: no peripheral configuration target.
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<IPeripheralConfigurationService>();
        client.StartListening();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var vm = new PeripheralConfigurationViewModel(() => proxy, InlineDispatcher.Instance, errors.Add, opened.Add, timeout: Guard);

        await vm.RefreshAsync();
        vm.IsUnsupported.Should().BeTrue();
        vm.Message.Should().Be(PeripheralConfigurationViewModel.UnsupportedMessage);
        vm.CanEnter.Should().BeFalse(); vm.CanExit.Should().BeFalse();
        errors.Should().BeEmpty(); opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Json_rpc_round_trips_the_frozen_status_and_sends_one_request_per_intent()
    {
        var fake = new FakePeripherals { Current = Status("Active", false, false, "fake earlier failure") };
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        server.AddLocalRpcTarget<IPeripheralConfigurationService>(fake, null);
        server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<IPeripheralConfigurationService>();
        client.StartListening();
        var vm = new PeripheralConfigurationViewModel(() => proxy, InlineDispatcher.Instance, _ => { }, _ => { }, timeout: Guard);

        await vm.RefreshAsync();
        vm.Status.Should().Be(fake.Current, "every frozen field survives the shared contract JSON settings");
        await Run(vm.EnterCommand);
        fake.Writes.Should().Equal("enter");
        vm.Status!.State.Should().Be("Entering");
        vm.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task Pipe_client_gates_the_optional_proxy_like_every_other_accessor()
    {
        await using var client = new PipeClient("UnifiedRgb-audit-unused-" + Guid.NewGuid().ToString("N"), PipeTrust.ElevatedOwnerOrCurrentUser);
        client.Invoking(c => c.RequirePeripheralConfiguration()).Should().Throw<HostNotConnectedException>()
            .WithMessage(HostNotConnectedException.DefaultMessage);
        client.BlockForUiFault("fake ui fault");
        client.Invoking(c => c.RequirePeripheralConfiguration()).Should().Throw<HostAccessBlockedException>().WithMessage("fake ui fault");
        client.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Main_view_model_attaches_the_section_without_contacting_a_host_or_opening_a_browser()
    {
        await using var client = new PipeClient("UnifiedRgb-audit-unused-" + Guid.NewGuid().ToString("N"), PipeTrust.ElevatedOwnerOrCurrentUser);
        var opened = new List<Uri>();
        using var main = new MainViewModel(client, InlineDispatcher.Instance, new AppLogicTests.MemoryAutostart(), "app.exe",
            Path.GetTempPath(), Path.GetTempPath(), openLink: opened.Add);
        var panel = main.Studio.ControlPanel;
        var section = panel.Peripherals!;
        panel.HasPeripheralConfiguration.Should().BeTrue();
        section.CanEnter.Should().BeFalse(); section.CanExit.Should().BeFalse(); section.IsReady.Should().BeFalse();

        await section.ReadCommand.ExecuteAsync();
        await section.EnterCommand.ExecuteAsync(); await section.ExitCommand.ExecuteAsync();
        section.OpenGearLinkCommand.Execute(null);
        section.Message.Should().Contain("后台未连接");
        section.HasStatus.Should().BeFalse();
        opened.Should().BeEmpty();
        panel.CanToggleKeyboardDisplay.Should().BeFalse();
        client.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Actual_panel_xaml_shows_the_exact_labels_and_binds_ready_without_opening_the_page()
    {
        var rig = CreatePanel();
        await rig.Panel.RefreshAsync();
        rig.Service.Current = ReadyStatus();
        await rig.Peripherals.RefreshAsync();
        var reads = rig.Service.Reads;
        var buttons = new Dictionary<string, bool>();
        var texts = new List<(string Text, bool Shown)>();
        var section = System.Windows.Visibility.Collapsed;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = rig.Panel };
                view.Measure(new(300, 900)); view.Arrange(new(0, 0, 300, 900)); view.UpdateLayout();
                foreach (var button in Logical<System.Windows.Controls.Button>(view)) buttons[button.Content?.ToString() ?? string.Empty] = button.IsEnabled;
                foreach (var text in Logical<System.Windows.Controls.TextBlock>(view)) texts.Add((text.Text, text.Visibility == System.Windows.Visibility.Visible));
                section = Logical<System.Windows.Controls.Border>(view).Single(b => b.Child is System.Windows.Controls.StackPanel { DataContext: PeripheralConfigurationViewModel }).Visibility;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();

        section.Should().Be(System.Windows.Visibility.Visible);
        buttons.Should().ContainKey("配置键鼠（暂时交给网页）").WhoseValue.Should().BeFalse("configuration is already requested");
        buttons.Should().ContainKey("完成配置，恢复联动").WhoseValue.Should().BeTrue();
        buttons.Should().ContainKey("打开 Gear Link 网页").WhoseValue.Should().BeTrue();
        buttons.Should().ContainKey("开启光域状态屏").WhoseValue.Should().BeFalse("the keyboard toggle is held during the handoff");
        texts.Should().Contain(("只暂停键盘、鼠标的外设联动；机箱灯光和屏幕照常运行。", true));
        texts.Should().Contain(("已就绪：键鼠接口已释放，可在网页中配置", true));
        texts.Should().Contain(t => t.Shown && t.Text.Contains("后台状态 Ready") && t.Text.Contains("接口已释放：是"));
        texts.Should().Contain((string.Empty, false), "the empty last-error line is collapsed");
        rig.Opened.Should().BeEmpty("binding Ready never opens the web page");
        rig.Service.Reads.Should().Be(reads); rig.Service.Writes.Should().BeEmpty();
    }

    private static Task Run(AsyncCommand command) => command.ExecuteAsync().WaitAsync(Guard);
    private static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;

    private static PeripheralConfigurationStatusDto Status(string state, bool mode, bool released, string? error = null) =>
        new(mode, state, $"fake {state} detail", released, DateTimeOffset.UtcNow, error);
    private static PeripheralConfigurationStatusDto ActiveStatus() => Status("Active", false, false);
    private static PeripheralConfigurationStatusDto EnteringStatus() => Status("Entering", true, false);
    private static PeripheralConfigurationStatusDto ReadyStatus() => Status("Ready", true, true);
    private static PeripheralConfigurationStatusDto ResumingStatus() => Status("Resuming", false, true);

    private static KeyboardDisplayStatusDto KeyboardStatus(bool enabled, long frames) =>
        new(enabled, enabled ? "Connecting" : "Stopped", enabled ? "等待接入键盘状态屏" : "键盘状态屏已关闭", frames, 0, DateTimeOffset.UtcNow, null);

    // Same construction as the other old-Host compatibility fakes; StreamJsonRpc does not expose a public constructor.
    private static RemoteMethodNotFoundException MethodNotFound() =>
        (RemoteMethodNotFoundException)RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException));

    private static TaskCompletionSource<PeripheralConfigurationStatusDto> Hold(FakePeripherals service)
    {
        var hold = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NextReads.Enqueue(_ => hold.Task);
        return hold;
    }

    private static List<string> Notes(PeripheralConfigurationViewModel vm)
    {
        var notes = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PeripheralConfigurationViewModel.Message)) lock (notes) notes.Add(vm.Message);
        };
        return notes;
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

    private static (PeripheralConfigurationViewModel Vm, FakePeripherals Service, List<Exception> Errors, List<Uri> Opened) Create(TimeSpan timeout,
        Func<IPeripheralConfigurationService>? service = null, Func<DateTimeOffset>? now = null)
    {
        var fake = new FakePeripherals();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var vm = new PeripheralConfigurationViewModel(service ?? (() => fake), InlineDispatcher.Instance, errors.Add, opened.Add, now, timeout);
        return (vm, fake, errors, opened);
    }

    private PanelRig CreatePanel(TimeSpan? timeout = null)
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var keyboard = new PanelKeyboard();
        var panelErrors = new List<Exception>();
        var panel = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, panelErrors.Add,
            keyboardDisplay: () => keyboard, keyboardTimeout: timeout ?? Long);
        var (peripherals, service, errors, opened) = Create(timeout ?? Long);
        panel.AttachPeripheralConfiguration(peripherals);
        return new(panel, peripherals, service, keyboard, rpc, [.. panelErrors, .. errors], opened);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed record PanelRig(CaseControlPanelViewModel Panel, PeripheralConfigurationViewModel Peripherals, FakePeripherals Service,
        PanelKeyboard Keyboard, CasePowerRpc Rpc, List<Exception> Errors, List<Uri> Opened);

    private sealed class FakePeripherals : IPeripheralConfigurationService
    {
        private int _reads;
        public PeripheralConfigurationStatusDto Current { get; set; } = ActiveStatus();
        public ConcurrentQueue<Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>> NextReads { get; } = new();
        public Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>? ReadOverride { get; set; }
        public Func<string, CancellationToken, Task<PeripheralConfigurationStatusDto>>? WriteOverride { get; set; }
        public List<string> Writes { get; } = [];
        public int Reads => Volatile.Read(ref _reads);

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return NextReads.TryDequeue(out var next) ? next(ct) : ReadOverride?.Invoke(ct) ?? Task.FromResult(Current);
        }

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default) => Write("enter", ct);
        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default) => Write("exit", ct);

        // Scripted answers only: the fake reports a state, it never models a release.
        public void Apply(string name) => Current = name == "enter" ? EnteringStatus() : ResumingStatus();

        private Task<PeripheralConfigurationStatusDto> Write(string name, CancellationToken ct)
        {
            lock (Writes) Writes.Add(name);
            if (WriteOverride is { } write) return write(name, ct);
            Apply(name);
            return Task.FromResult(Current);
        }
    }

    private sealed class PanelKeyboard : IKeyboardDisplayService
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
