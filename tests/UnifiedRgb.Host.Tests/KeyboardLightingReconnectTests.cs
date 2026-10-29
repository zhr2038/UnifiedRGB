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
/// The explicit “重连键盘灯光” action reasserts exactly the page preference of a fresh read through the existing
/// SetKeyboardDisplayEnabledAsync: once per click, on the captured proxy, and only while the Host reports a keyboard Error
/// with a trustworthy preference. It never toggles the page and claims no recovery. Callable fakes record every proxy,
/// read and write; a headless view only: no browser, window, Host, pipe or device.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned tasks script held and lost answers.
[Collection("Host")]
public sealed class KeyboardLightingReconnectTests : IDisposable
{
    private const string Reconnect = "重连键盘灯光";
    private const string EnablePage = "开启光域状态屏";
    private const string RestoreNative = "恢复键盘原动画";
    private const string UnreadableSettings = "本机控制屏设置无法读取";
    private static readonly DateTimeOffset At = new(2026, 10, 26, 17, 47, 0, TimeSpan.Zero);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Expired = TimeSpan.FromSeconds(11);
    // UI-owned text must neither claim a recovery nor describe any keyboard request as switching a screen off.
    private static readonly string[] FalseClaims = ["正在播放", "已在播放", "原动画已恢复", "已恢复原动画", "已恢复键盘原动画", "原动画已可见",
        "关闭屏幕", "屏幕已关闭", "关闭键盘状态屏", "关闭光域状态屏", "键盘状态屏已关闭", "重连成功", "已重连", "灯光已恢复", "已恢复灯光", "已修复"];
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private static readonly ScreenDeviceDto Screen = new("screen:h2:test", "H2", "H2", 208, 64, "Running", null, true, 10, 0, null, 30);
    private readonly List<CasePowerActions> _owned = [];

    [Theory]
    [InlineData(false, EnablePage)]
    [InlineData(true, RestoreNative)]
    public async Task Explicit_reconnect_from_error_reasserts_exactly_the_read_preference_once_and_keeps_the_page_state(bool enabled, string label)
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(enabled);
        await Observe(rig);
        rig.Pipes.Snapshot().Should().Equal("proxy A", "A.read");
        using (new AssertionScope())
        {
            rig.Vm.CanReconnectKeyboardLighting.Should().BeTrue();
            rig.Vm.ReconnectKeyboardLightingCommand.CanExecute(null).Should().BeTrue();
            rig.Vm.KeyboardReconnectNote.Should().Contain("后台报告键盘错误").And.Contain("光域状态屏保持" + Page(enabled))
                .And.Contain("不绕过固件与身份校验").And.Contain("不保证恢复");
            rig.Vm.KeyboardToggleLabel.Should().Be(label);
            Claims(rig.Vm.KeyboardReconnectNote);
        }
        var notes = Notes(rig.Vm);
        await Click(rig.Vm);

        using (new AssertionScope())
        {
            rig.Pipes.Snapshot().Skip(2).Should().Equal(["proxy A", "A.read", Write("A", enabled), "A.read"],
                "one proxy per intent, a fresh preflight, exactly one write of the read preference and one readback");
            rig.Keyboard.Writes.Should().Equal(enabled);
            rig.Keyboard.WriteTokens.Should().ContainSingle().Which.CanBeCanceled.Should().BeTrue("the write is bounded");
            rig.Vm.KeyboardToggleLabel.Should().Be(label, "the page preference is reasserted, never inverted");
            rig.Vm.KeyboardTransportNote.Should().Contain(enabled ? "Connecting" : "Stopping");
            rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse("the readback no longer reports an Error");
            rig.Vm.KeyboardReconnectNote.Should().Contain("后台正在连接或恢复键盘");
            lock (notes)
            {
                notes.Should().Contain(n => n.Contains($"正在请求重连键盘灯光（光域状态屏保持{Page(enabled)}）"));
                notes.Should().Contain(n => n.Contains("后台已接受重连键盘灯光指令"));
                foreach (var note in notes) Claims(note);
            }
            Claims(rig.Vm.KeyboardTransportNote); Claims(rig.Vm.KeyboardReconnectNote);
        }

        // The Host's new session settles; polls never resend, toggle or turn the page on.
        rig.Keyboard.Status = State(enabled, enabled ? "Running" : "Stopped");
        await rig.Vm.RefreshAsync(); await rig.Vm.RefreshAsync();
        rig.Keyboard.Writes.Should().Equal([enabled], "no automatic resend, toggle or page-on");
        rig.Vm.KeyboardToggleLabel.Should().Be(label);
        rig.Vm.KeyboardReconnectNote.Should().Contain("后台未报告键盘错误");
        rig.Peripherals.Writes.Should().BeEmpty(); rig.Opened.Should().BeEmpty();
        rig.Rpc.Writes.Should().BeEmpty("the case scene is untouched"); rig.Rpc.Submitted.Should().BeEmpty(); rig.Rpc.Loads.Should().Be(0);
        rig.Errors.Should().BeEmpty("the screen fake answers reads only; any screen write would have been reported");
    }

    [Theory]
    [InlineData("unread", "键盘状态未知、未读到或后台不支持", true)]
    [InlineData("old-host", "键盘状态未知、未读到或后台不支持", true)]
    [InlineData("disconnected", "后台未连接", false)]
    [InlineData("Connecting", "后台正在连接或恢复键盘（状态 Connecting）", true)]
    [InlineData("Stopping", "后台正在连接或恢复键盘（状态 Stopping）", true)]
    [InlineData("Waiting", "键盘未接入（后台状态 Waiting）", true)]
    [InlineData("Stopped", "后台未报告键盘错误（状态 Stopped）", true)]
    [InlineData("Running", "后台未报告键盘错误（状态 Running）", true)]
    [InlineData("unreadable-settings", "开关值不可信", true)]
    [InlineData("config-entering", "键鼠外设配置中或未确认恢复联动", false)]
    [InlineData("config-unread", "键鼠外设配置中或未确认恢复联动", false)]
    [InlineData("config-expired", "键鼠外设配置中或未确认恢复联动", false)]
    public async Task Unavailable_reconnect_explains_why_and_a_click_calls_nothing(string condition, string reason, bool commandEnabled)
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(false);
        switch (condition)
        {
            case "unread": rig.Keyboard.ReadOverride = _ => Task.FromException<KeyboardDisplayStatusDto>(new IOException("fake status lost")); break;
            case "old-host": rig.Keyboard.ReadOverride = _ => Task.FromException<KeyboardDisplayStatusDto>(MethodNotFound()); break;
            case "unreadable-settings": rig.Keyboard.Status = Faulted(false, UnreadableSettings); break;
            case "config-entering": rig.Peripherals.Current = Peripheral("Entering", true, false); break;
            case "config-unread": rig.Peripherals.ReadOverride = _ => Task.FromException<PeripheralConfigurationStatusDto>(new IOException("fake handoff status lost")); break;
            case "Connecting" or "Stopping" or "Waiting" or "Stopped" or "Running": rig.Keyboard.Status = State(condition == "Running", condition); break;
        }
        await Observe(rig);
        if (condition == "disconnected") rig.Vm.SetConnected(false);
        if (condition == "config-expired") rig.Clock.Advance(Expired);
        var before = rig.Pipes.Snapshot();
        before.Should().Contain("A.read", "the keyboard fake is wired and was read by the poll");

        using (new AssertionScope())
        {
            rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse();
            rig.Vm.ReconnectKeyboardLightingCommand.CanExecute(null).Should().Be(commandEnabled, "CanExecute follows only connection and handoff");
            rig.Vm.KeyboardReconnectNote.Should().Contain(reason);
            Claims(rig.Vm.KeyboardReconnectNote);
        }
        await Click(rig.Vm);

        rig.Pipes.Snapshot().Should().Equal(before, "a refused click calls neither the proxy factory nor the service");
        rig.Keyboard.Writes.Should().BeEmpty();
        rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse();
        rig.Peripherals.Writes.Should().BeEmpty(); rig.Opened.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty();
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_keyboard_request_in_flight_holds_the_other_keyboard_action_without_any_call()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(true);
        await Observe(rig);

        // A reconnect whose write is unanswered holds the toggle.
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.WriteOverride = (_, _) => answer.Task;
        var reconnect = rig.Vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
        rig.Keyboard.Writes.Should().Equal(true);
        var held = rig.Pipes.Snapshot();
        using (new AssertionScope())
        {
            rig.Vm.CanToggleKeyboardDisplay.Should().BeFalse();
            rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse();
            rig.Vm.KeyboardReconnectNote.Should().Contain("正在等待键盘指令的后台答复");
        }
        await Toggle(rig.Vm);
        await Click(rig.Vm);
        await rig.Vm.RefreshAsync(); // The poll also leaves the keyboard to the intent that owns it.
        rig.Pipes.Snapshot().Should().Equal(held, "neither keyboard action nor the poll calls the keyboard while a request is in flight");
        answer.SetResult();
        await reconnect.WaitAsync(Guard);
        rig.Pipes.Snapshot().Skip(held.Length).Should().Equal("A.read");
        rig.Vm.CanToggleKeyboardDisplay.Should().BeTrue("the readback ended the request");

        // A toggle whose preflight is unanswered holds the reconnect.
        rig.Keyboard.WriteOverride = null;
        var preflight = new TaskCompletionSource<KeyboardDisplayStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.NextReads.Enqueue(_ => preflight.Task);
        var toggle = rig.Vm.ToggleKeyboardDisplayCommand.ExecuteAsync();
        held = rig.Pipes.Snapshot();
        rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse();
        rig.Vm.KeyboardReconnectNote.Should().Contain("正在等待键盘指令的后台答复");
        await Click(rig.Vm);
        rig.Pipes.Snapshot().Should().Equal(held);
        preflight.SetResult(Faulted(true));
        await toggle.WaitAsync(Guard);

        rig.Keyboard.Writes.Should().Equal([true, false], "the reconnect reasserted true once; the explicit toggle then requested the native animation");
        rig.Pipes.Snapshot().Count(c => c.Contains("write", StringComparison.Ordinal)).Should().Be(2);
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_click_sends_at_most_one_write_and_a_persisting_error_is_never_retried_automatically()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(false);
        rig.Keyboard.WriteOverride = (_, _) => Task.CompletedTask; // Accepted, but the reported Error persists.
        await Observe(rig);
        await Click(rig.Vm);
        rig.Keyboard.Writes.Should().Equal(false);
        rig.Vm.CanReconnectKeyboardLighting.Should().BeTrue("the readback still reports the Error, so a further explicit click is allowed");

        await rig.Vm.RefreshAsync(); await rig.Config.RefreshAsync(); await rig.Vm.RefreshAsync();
        await Task.Delay(Short * 2);
        rig.Keyboard.Writes.Should().Equal([false], "a persisting Error is never answered with an automatic resend");

        await Click(rig.Vm);
        rig.Keyboard.Writes.Should().Equal([false, false], "one write per explicit click");
        rig.Pipes.Snapshot().Count(c => c.Contains("write", StringComparison.Ordinal)).Should().Be(2);
        rig.Keyboard.Writes.Should().NotContain(true, "the disabled page is never turned on");
        rig.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("lost-answer-read-back")]
    [InlineData("lost-answer-unread")]
    [InlineData("rejected")]
    public async Task Unknown_or_rejected_reconnect_reads_back_once_through_the_same_proxy_and_never_resends(string outcome)
    {
        var rig = Create(Short);
        rig.Keyboard.Status = Faulted(false);
        await Observe(rig);
        var notes = Notes(rig.Vm);
        if (outcome == "rejected")
            rig.Keyboard.WriteOverride = (_, _) => Task.FromException(new RemoteInvocationException("fake reconnect refused", 0, (object?)null));
        else // The Host applies the request, then the answer is lost and the call ignores cancellation.
            rig.Keyboard.WriteOverride = (enabled, _) => { rig.Keyboard.Status = Reasserted(enabled); return Never(); };
        if (outcome == "lost-answer-unread")
        {
            rig.Keyboard.NextReads.Enqueue(_ => Task.FromResult(rig.Keyboard.Status));
            rig.Keyboard.NextReads.Enqueue(_ => Never<KeyboardDisplayStatusDto>());
        }
        await Click(rig.Vm);

        using (new AssertionScope())
        {
            rig.Pipes.Snapshot().Skip(2).Should().Equal(["proxy A", "A.read", "A.write(false)", "A.read"], "one bounded readback on the captured proxy");
            rig.Keyboard.Writes.Should().Equal(false);
            lock (notes) foreach (var note in notes) Claims(note);
            Claims(rig.Vm.KeyboardTransportNote); Claims(rig.Vm.KeyboardReconnectNote);
            switch (outcome)
            {
                case "lost-answer-read-back":
                    lock (notes) notes.Should().Contain(n => n.Contains("重连键盘灯光结果未确认") && n.Contains("未自动重发"));
                    rig.Vm.KeyboardTransportNote.Should().Contain("Stopping", "the readback, not the lost answer, supplies the state");
                    rig.Vm.KeyboardToggleLabel.Should().Be(EnablePage);
                    rig.Errors.Should().BeEmpty();
                    break;
                case "lost-answer-unread":
                    rig.Vm.HasKeyboardDisplayService.Should().BeFalse();
                    rig.Vm.KeyboardTransportNote.Should().Contain("重连键盘灯光结果未知").And.Contain("未自动重发").And.NotContain("已确认");
                    rig.Vm.KeyboardReconnectNote.Should().Contain("键盘状态未知");
                    rig.Errors.Should().BeEmpty();
                    break;
                default:
                    rig.Errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>();
                    lock (notes) notes.Should().Contain(n => n.Contains("后台对重连键盘灯光指令返回错误"));
                    rig.Vm.KeyboardTransportNote.Should().Contain("后台状态 Error", "the Error is shown as read back");
                    rig.Vm.CanReconnectKeyboardLighting.Should().BeTrue("only a further explicit click may ask again");
                    break;
            }
            rig.Vm.CanReconnectKeyboardLighting.Should().Be(outcome == "rejected");
        }
        await Task.Delay(Short * 3);
        await rig.Vm.RefreshAsync();
        rig.Keyboard.Writes.Should().HaveCount(1, "an unanswered or rejected request is never resent");
    }

    [Theory]
    [InlineData("recovered")]
    [InlineData("preference-changed")]
    [InlineData("unread")]
    [InlineData("unreadable-settings")]
    [InlineData("handoff-began")]
    public async Task The_fresh_preflight_decides_and_a_stale_offer_never_writes_its_old_preference(string preflight)
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(false);
        await Observe(rig);
        rig.Vm.CanReconnectKeyboardLighting.Should().BeTrue("the offer comes from the earlier poll");
        Func<CancellationToken, Task<KeyboardDisplayStatusDto>> read = preflight switch
        {
            "recovered" => _ => Task.FromResult(State(false, "Stopped")),
            "preference-changed" => _ => Task.FromResult(Faulted(true)), // Another client enabled the page; the Error remains.
            "unread" => _ => Task.FromException<KeyboardDisplayStatusDto>(new IOException("fake preflight lost")),
            "unreadable-settings" => _ => Task.FromResult(Faulted(false, UnreadableSettings)),
            _ => HandoffDuringRead,
        };
        rig.Keyboard.NextReads.Enqueue(read);
        var notes = Notes(rig.Vm);
        await Click(rig.Vm);

        using (new AssertionScope())
        {
            var calls = rig.Pipes.Snapshot().Skip(2).ToArray();
            if (preflight == "preference-changed")
            {
                calls.Should().Equal("proxy A", "A.read", "A.write(true)", "A.read");
                rig.Keyboard.Writes.Should().Equal([true], "the fresh preference is reasserted as read, not the stale one");
                lock (notes) notes.Should().Contain(n => n.Contains("光域状态屏保持开启"));
            }
            else
            {
                calls.Should().Equal(["proxy A", "A.read"], "the fresh read no longer calls for a write");
                rig.Keyboard.Writes.Should().BeEmpty();
            }
            switch (preflight)
            {
                case "recovered": rig.Vm.KeyboardReconnectNote.Should().Contain("后台未报告键盘错误（状态 Stopped）"); break;
                case "unread": rig.Vm.KeyboardTransportNote.Should().Be("键盘状态未读到，未发送重连请求；请稍后重试。"); break;
                case "unreadable-settings": rig.Vm.KeyboardReconnectNote.Should().Contain("开关值不可信"); break;
                case "handoff-began":
                    rig.Vm.KeyboardReconnectNote.Should().Contain("键鼠外设配置中");
                    rig.Config.Status!.State.Should().Be("Entering");
                    break;
            }
            rig.Peripherals.Writes.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty();
            rig.Errors.Should().BeEmpty();
        }

        async Task<KeyboardDisplayStatusDto> HandoffDuringRead(CancellationToken ct)
        {
            rig.Peripherals.Current = Peripheral("Entering", true, false); // Another window began the keyboard/mouse handoff.
            await rig.Config.RefreshAsync();
            return Faulted(false);
        }
    }

    [Fact]
    public async Task A_connection_change_during_the_preflight_ends_the_intent_without_any_write()
    {
        var rig = Create(Long, handoff: false); // Without the handoff section, only the connection epoch can stop the write.
        rig.Keyboard.Status = Faulted(false);
        await Observe(rig);
        var preflight = new TaskCompletionSource<KeyboardDisplayStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.NextReads.Enqueue(_ => preflight.Task);
        var click = rig.Vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
        rig.Vm.SetConnected(false); rig.Vm.SetConnected(true);
        preflight.SetResult(Faulted(false));
        await click.WaitAsync(Guard);

        using (new AssertionScope())
        {
            rig.Pipes.Snapshot().Skip(2).Should().Equal(["proxy A", "A.read"], "nothing is written through a connection that changed meanwhile");
            rig.Keyboard.Writes.Should().BeEmpty();
            rig.Vm.HasKeyboardDisplayService.Should().BeFalse("the old preflight is not shown on the new connection");
            rig.Vm.KeyboardTransportNote.Should().Be("键盘状态未知，等待新的后台回读。");
            rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse();
        }
        await rig.Vm.RefreshAsync();
        rig.Vm.CanReconnectKeyboardLighting.Should().BeTrue("the new connection's own read may offer it again");
        rig.Keyboard.Writes.Should().BeEmpty("offering again never sends by itself");
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_proxy_during_the_intent_never_receives_the_write_or_readback_chosen_on_the_old_one()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(false);
        await Observe(rig);
        var preflight = new TaskCompletionSource<KeyboardDisplayStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.NextReads.Enqueue(_ => preflight.Task);
        var click = rig.Vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
        var fresh = new FakeKeyboard("B", rig.Pipes) { Status = Faulted(true) };
        rig.Pipes.Current = fresh; // The pipe was replaced; this panel has not seen the reconnect yet.
        preflight.SetResult(Faulted(false));
        await click.WaitAsync(Guard);

        rig.Pipes.Snapshot().Skip(2).Should().Equal(["proxy A", "A.read", "A.write(false)", "A.read"], "the intent stays on the proxy it captured");
        rig.Keyboard.Writes.Should().Equal(false);
        fresh.Writes.Should().BeEmpty();
        rig.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_old_answer_after_a_reconnect_never_alters_the_new_connection(bool rejected)
    {
        var rig = Create(Long, handoff: false);
        rig.Keyboard.Status = Faulted(false);
        await Observe(rig);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Keyboard.WriteOverride = (_, _) => answer.Task;
        var click = rig.Vm.ReconnectKeyboardLightingCommand.ExecuteAsync();
        rig.Keyboard.Writes.Should().Equal(false);
        rig.Vm.SetConnected(false);
        var fresh = new FakeKeyboard("B", rig.Pipes) { Status = State(true, "Running") };
        rig.Pipes.Current = fresh;
        rig.Vm.SetConnected(true);
        await rig.Vm.RefreshAsync();
        var shown = (rig.Vm.KeyboardTransportNote, rig.Vm.KeyboardToggleLabel, rig.Vm.KeyboardReconnectNote, rig.Vm.CanToggleKeyboardDisplay);
        shown.KeyboardToggleLabel.Should().Be(RestoreNative, "the new connection reads its own state");
        shown.CanToggleKeyboardDisplay.Should().BeTrue("an old connection's request does not hold the new one");
        var trace = rig.Pipes.Snapshot();

        if (rejected) answer.SetException(new RemoteInvocationException("fake old refusal", 0, (object?)null));
        else answer.SetResult();
        await click.WaitAsync(Guard);

        using (new AssertionScope())
        {
            (rig.Vm.KeyboardTransportNote, rig.Vm.KeyboardToggleLabel, rig.Vm.KeyboardReconnectNote, rig.Vm.CanToggleKeyboardDisplay).Should().Be(shown);
            rig.Pipes.Snapshot().Should().Equal(trace, "the old intent reads nothing back once its connection ended");
            rig.Keyboard.Writes.Should().Equal(false);
            fresh.Writes.Should().BeEmpty();
            rig.Errors.Should().BeEmpty("an old connection's rejection never reaches the new connection's error banner");
        }
    }

    [Fact]
    public async Task Reconnect_leaves_the_unapplied_draft_and_the_case_scene_untouched()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var pipes = new Pipes();
        pipes.Current.Status = Faulted(false);
        var errors = new List<Exception>();
        using var studio = new StudioViewModel(new FakeConnection(proxy).Connect(), InlineDispatcher.Instance, errors.Add, keyboardDisplay: pipes.Connect);
        studio.Draft.Name = "未应用的草稿";
        studio.Draft.Dirty.Should().BeTrue();
        var draft = (studio.Draft.Name, studio.Draft.Dirty, studio.Draft.Revision, studio.Draft.DraftState, studio.Draft.Stages.Count);
        await studio.ControlPanel.RefreshAsync();
        studio.ControlPanel.CanReconnectKeyboardLighting.Should().BeTrue();
        var caseReads = rpc.Reads;

        await Click(studio.ControlPanel);

        using (new AssertionScope())
        {
            pipes.Current.Writes.Should().Equal(false);
            (studio.Draft.Name, studio.Draft.Dirty, studio.Draft.Revision, studio.Draft.DraftState, studio.Draft.Stages.Count).Should().Be(draft);
            rpc.Writes.Should().BeEmpty(); rpc.Submitted.Should().BeEmpty(); rpc.Loads.Should().Be(0);
            rpc.Reads.Should().Be(caseReads, "the reconnect reads only the keyboard status");
            errors.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Gear_link_ready_holds_the_reconnect_and_keeps_its_explicit_click_and_freshness()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = Faulted(false);
        rig.Peripherals.Current = Peripheral("Ready", true, true);
        await Observe(rig);
        var peripheralReads = rig.Peripherals.Reads;
        var trace = rig.Pipes.Snapshot();
        using (new AssertionScope())
        {
            rig.Config.IsReady.Should().BeTrue();
            rig.Vm.CanReconnectKeyboardLighting.Should().BeFalse();
            rig.Vm.ReconnectKeyboardLightingCommand.CanExecute(null).Should().BeFalse();
            rig.Vm.KeyboardReconnectNote.Should().Contain("键鼠外设配置中");
        }
        await Click(rig.Vm);

        using (new AssertionScope())
        {
            rig.Pipes.Snapshot().Should().Equal(trace);
            rig.Keyboard.Writes.Should().BeEmpty();
            rig.Peripherals.Reads.Should().Be(peripheralReads); rig.Peripherals.Writes.Should().BeEmpty();
            rig.Config.IsReady.Should().BeTrue("Ready remains the backend's actual release");
            rig.Config.Status!.State.Should().Be("Ready");
            rig.Opened.Should().BeEmpty("only the explicit Gear Link click opens the page");
        }
        rig.Config.OpenGearLinkCommand.Execute(null);
        rig.Opened.Should().Equal(PeripheralConfigurationViewModel.GearLinkUri);

        rig.Clock.Advance(Expired);
        rig.Config.OpenGearLinkCommand.CanExecute(null).Should().BeFalse("an expired Ready never opens the page");
        rig.Config.OpenGearLinkCommand.Execute(null);
        await Click(rig.Vm);
        rig.Opened.Should().HaveCount(1);
        rig.Keyboard.Writes.Should().BeEmpty("an expired handoff observation never authorises a keyboard write");
        rig.Peripherals.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_panel_xaml_shows_the_reconnect_and_its_reason_beside_the_unchanged_toggle()
    {
        var rig = Create(Long);
        rig.Keyboard.Status = State(false, "Stopped");
        await Observe(rig);
        var healthyNote = rig.Vm.KeyboardReconnectNote;
        var healthy = Render(rig.Vm);
        rig.Keyboard.Status = Faulted(false);
        await rig.Vm.RefreshAsync(); // Property changes only: no command event reaches the ended render thread.
        var trace = rig.Pipes.Snapshot();
        var faulted = Render(rig.Vm);

        using (new AssertionScope())
        {
            healthy.Buttons.Should().ContainKey(Reconnect).WhoseValue.Enabled.Should().BeFalse();
            healthy.Buttons[Reconnect].Help.Should().Be(healthyNote).And.Contain("后台未报告键盘错误（状态 Stopped）");
            healthy.Texts.Should().Contain((healthyNote, true));
            faulted.Buttons.Should().ContainKey(Reconnect).WhoseValue.Enabled.Should().BeTrue();
            faulted.Buttons[Reconnect].Help.Should().Be(rig.Vm.KeyboardReconnectNote).And.Contain("光域状态屏保持未启用");
            faulted.Buttons[Reconnect].ToolTip.Should().Contain("原样请求一次").And.Contain("不切换显示").And.Contain("未答复不自动重发");
            faulted.Texts.Should().Contain((rig.Vm.KeyboardReconnectNote, true));
            faulted.Buttons.Should().ContainKey(EnablePage).WhoseValue.Enabled.Should().BeTrue("the existing toggle is unchanged");
            faulted.Texts.Should().ContainSingle(t => t.Text.Contains("兜底")).Which.Shown.Should().BeTrue();
            foreach (var view in new[] { healthy, faulted })
            {
                view.Buttons.Keys.Should().NotContain(k => k.Contains("关闭"));
                view.Buttons[Reconnect].ToolTip.Should().NotContain("关闭");
                foreach (var (text, shown) in view.Texts) if (shown) Claims(text);
            }
        }
        rig.Pipes.Snapshot().Should().Equal(trace, "binding calls nothing");
        rig.Keyboard.Writes.Should().BeEmpty(); rig.Rpc.Writes.Should().BeEmpty(); rig.Errors.Should().BeEmpty();
    }

    private static void Claims(string text)
    {
        foreach (var claim in FalseClaims) text.Should().NotContain(claim);
    }

    private static Task Click(CaseControlPanelViewModel vm) => vm.ReconnectKeyboardLightingCommand.ExecuteAsync().WaitAsync(Guard);
    private static Task Toggle(CaseControlPanelViewModel vm) => vm.ToggleKeyboardDisplayCommand.ExecuteAsync().WaitAsync(Guard);
    private static Task Never() => new TaskCompletionSource().Task;
    private static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;
    private static string Page(bool enabled) => enabled ? "开启" : "未启用";
    private static string Write(string pipe, bool enabled) => $"{pipe}.write({(enabled ? "true" : "false")})";

    // The Host's sticky fault keeps the owner's page preference in Enabled; LastError carries the opaque layer's text as is.
    private static KeyboardDisplayStatusDto Faulted(bool enabled, string? lastError = "InvalidDataException: OLED acknowledgement changed; no retry.") =>
        new(enabled, "Error", "fake Error detail", 9, 0, At, lastError);

    private static KeyboardDisplayStatusDto State(bool enabled, string state) => new(enabled, state, $"fake {state} detail", 9, 0, At, null);

    // What a Host publishes right after accepting a reassert: a new session connecting, or the faulted one stopping.
    private static KeyboardDisplayStatusDto Reasserted(bool enabled) => State(enabled, enabled ? "Connecting" : "Stopping");

    private static PeripheralConfigurationStatusDto Peripheral(string state, bool mode, bool released) =>
        new(mode, state, $"fake {state} detail", released, At, null);

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

    // One handoff poll and one case/keyboard poll, as the workbench does.
    private static async Task Observe(Rig rig)
    {
        if (rig.Vm.HasPeripheralConfiguration) await rig.Config.RefreshAsync();
        await rig.Vm.RefreshAsync();
    }

    // Loads the actual panel XAML on an STA thread without a window and collects what its bindings show.
    private static Rendered Render(CaseControlPanelViewModel vm)
    {
        var buttons = new Dictionary<string, (bool Enabled, string ToolTip, string Help)>();
        var texts = new List<(string Text, bool Shown)>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = vm };
                view.Measure(new(300, 1200)); view.Arrange(new(0, 0, 300, 1200)); view.UpdateLayout();
                foreach (var button in Logical<System.Windows.Controls.Button>(view))
                    buttons[button.Content?.ToString() ?? string.Empty] = (button.IsEnabled, button.ToolTip?.ToString() ?? string.Empty,
                        System.Windows.Automation.AutomationProperties.GetHelpText(button));
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

    private Rig Create(TimeSpan keyboardTimeout, bool handoff = true)
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var screens = DispatchProxy.Create<IScreenService, CasePanelScreenRpc>();
        var screenRpc = (CasePanelScreenRpc)(object)screens;
        screenRpc.Scene = new([new("screen:h2:test")]); screenRpc.Screens = [Screen];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var pipes = new Pipes();
        var errors = new List<Exception>();
        var opened = new List<Uri>();
        var clock = new ManualClock();
        var peripherals = new FakePeripherals();
        var config = new PeripheralConfigurationViewModel(() => peripherals, InlineDispatcher.Instance, errors.Add, opened.Add, clock.Now, Long);
        var vm = new CaseControlPanelViewModel(actions, () => proxy, () => screens, InlineDispatcher.Instance, errors.Add,
            keyboardDisplay: pipes.Connect, keyboardTimeout: keyboardTimeout);
        if (handoff) vm.AttachPeripheralConfiguration(config);
        return new(vm, pipes, pipes.Current, rpc, config, peripherals, clock, errors, opened);
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }

    private sealed record Rig(CaseControlPanelViewModel Vm, Pipes Pipes, FakeKeyboard Keyboard, CasePowerRpc Rpc,
        PeripheralConfigurationViewModel Config, FakePeripherals Peripherals, ManualClock Clock, List<Exception> Errors, List<Uri> Opened);

    private sealed record Rendered(Dictionary<string, (bool Enabled, string ToolTip, string Help)> Buttons, List<(string Text, bool Shown)> Texts);

    /// <summary>The keyboard proxy factory: hands out the current pipe and records, in order, every proxy, read and write.</summary>
    private sealed class Pipes
    {
        private readonly List<string> _trace = [];
        public Pipes() => Current = new("A", this);
        public FakeKeyboard Current { get; set; }

        public FakeKeyboard Connect()
        {
            var current = Current;
            Record("proxy " + current.Name);
            return current;
        }

        public void Record(string call) { lock (_trace) _trace.Add(call); }
        public string[] Snapshot() { lock (_trace) return [.. _trace]; }
    }

    private sealed class FakeKeyboard(string name, Pipes pipes) : IKeyboardDisplayService
    {
        public string Name => name;
        public KeyboardDisplayStatusDto Status { get; set; } = Faulted(false);
        public ConcurrentQueue<Func<CancellationToken, Task<KeyboardDisplayStatusDto>>> NextReads { get; } = new();
        public Func<CancellationToken, Task<KeyboardDisplayStatusDto>>? ReadOverride { get; set; }
        public Func<bool, CancellationToken, Task>? WriteOverride { get; set; }
        public List<bool> Writes { get; } = [];
        public List<CancellationToken> WriteTokens { get; } = [];

        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default)
        {
            pipes.Record(name + ".read");
            return NextReads.TryDequeue(out var next) ? next(ct) : ReadOverride?.Invoke(ct) ?? Task.FromResult(Status);
        }

        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            pipes.Record(Write(name, enabled));
            lock (Writes) { Writes.Add(enabled); WriteTokens.Add(ct); }
            if (WriteOverride is { } write) return write(enabled, ct);
            Status = Reasserted(enabled); // Scripted states only: the fake never models brightness, radio or firmware.
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock
    {
        private DateTimeOffset _value = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now() => _value;
        public void Advance(TimeSpan by) => _value += by;
    }

    private sealed class FakePeripherals : IPeripheralConfigurationService
    {
        public PeripheralConfigurationStatusDto Current { get; set; } = Peripheral("Active", false, false);
        public Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>? ReadOverride { get; set; }
        public List<string> Writes { get; } = [];
        public int Reads { get; private set; }

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Reads++;
            return ReadOverride?.Invoke(ct) ?? Task.FromResult(Current);
        }

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
