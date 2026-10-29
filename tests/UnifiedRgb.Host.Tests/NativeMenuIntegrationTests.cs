using System.Reflection;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamJsonRpc;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// The Host's native-menu owner end to end on fakes: typed, already validated source events (an injected fake of Root's future
/// source) reach the existing NativeMenuCaseActionCoordinator, which runs the existing CasePowerCoordinator on the shared
/// MenuHostFake; the fresh CaseStatusObservation goes to the existing keyboard display owner's artwork. The coordinator's own
/// suite already covers its queue, clock and arm rules; here they are only exercised through the owner. No Host process, pipe,
/// device, keyboard transport or physical source: the OLED factories, writer guard and screen writes throw if reached.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned tasks script held and lost answers.
[Collection("Host")]
public sealed class NativeMenuServiceIntegrationTests : IAsyncLifetime
{
    private const string Session = "root-source:opaque-1";
    private const string NextSession = "root-source:opaque-2";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly ManualTimeProvider _time = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "urgb-native-menu-" + Guid.NewGuid().ToString("N"));
    private readonly List<NativeMenuService> _services = [];
    private readonly List<IDisposable> _owned = [];

    private string PreferencePath => Path.Combine(_data, "peripherals", "native-menu.json");

    [Fact]
    public async Task Default_composition_has_no_source_starts_off_and_a_request_alone_is_unavailable_and_never_acts()
    {
        var host = new MenuHostFake(Running());
        var services = new ServiceCollection().AddLogging().AddUnifiedRgb(Options(), enableIpc: false);
        services.AddSingleton(host.Connection.Rgb);
        services.AddSingleton(host.Connection.Screens!);
        services.AddSingleton<IRogWiredOledFactory>(new NoOled());
        services.AddSingleton<IRogWirelessOledFactory>(new NoWirelessOled());
        services.AddSingleton<IWriterGuard>(new NoGuard());

        services.Should().ContainSingle(d => d.ServiceType == typeof(INativeMenuSource))
            .Which.ImplementationFactory.Should().NotBeNull("the default-off physical source is registered once");
        services.Where(d => d.ServiceType == typeof(IHostedService)).Should()
            .ContainSingle(d => d.ImplementationFactory != null && d.ImplementationFactory.GetType() == typeof(Func<IServiceProvider, NativeMenuService>));
        await using var provider = services.BuildServiceProvider();
        var menu = provider.GetRequiredService<NativeMenuService>();
        provider.GetRequiredService<INativeMenuService>().Should().BeSameAs(menu, "one owner behind the RPC target and the hosted service");
        provider.GetRequiredService<INativeMenuSource>().Should().BeOfType<RogNativeMenuSource>().Which.IsPresent.Should().BeFalse();

        await menu.StartAsync(default);
        var off = await menu.GetNativeMenuStatusAsync();
        var requested = await menu.SetNativeMenuEnabledAsync(true);
        var after = await menu.GetNativeMenuStatusAsync();

        using (new AssertionScope())
        {
            off.Should().Match<NativeMenuStatusDto>(s => s.State == "Off" && !s.DesiredEnabled && s.PreferenceReadable && !s.SourcePresent && !s.Active);
            requested.Should().Be(after);
            requested.Should().Match<NativeMenuStatusDto>(s => s.State == "Unavailable" && s.DesiredEnabled && !s.SourcePresent && !s.Active
                && s.LastOutcome == null && s.LastError == null);
            requested.Detail.Should().Contain("尚未接入经验证的菜单来源").And.Contain("不执行机箱动作");
            Claims(off); Claims(requested);
            File.ReadAllText(PreferencePath).Should().Be("{\"Schema\":1,\"Enabled\":true}", "the request lives in its own local file, outside every scene and package");
            menu.Trace.Should().Equal("no trusted source installed", "preference saved: on");
            host.Calls.Should().BeEmpty("enabling without a verified source reaches neither the case nor its screens");
        }
        await menu.StopAsync(default);
        (await menu.GetNativeMenuStatusAsync()).Should().Match<NativeMenuStatusDto>(s => s.State == "Stopped" && !s.Active);
    }

    [Fact]
    public async Task A_validated_down_then_up_runs_the_existing_case_power_actions_and_shows_their_readback_on_the_keyboard_artwork()
    {
        var rig = await Start();
        await rig.Menu.SetNativeMenuEnabledAsync(true);
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        var armed = await rig.Menu.GetNativeMenuStatusAsync();
        armed.Should().Match<NativeMenuStatusDto>(s => s.State == "Active" && s.Active && s.SourcePresent && s.DesiredEnabled);
        Claims(armed);
        rig.Host.Calls.Should().BeEmpty("enabling and a verified context only arm");

        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        await Traced(rig.Menu, "artwork #2: Pending");
        var off = Readback(rig.Host);
        var offArtwork = rig.Display.ArtworkPixels.ToArray();
        var offStatus = await rig.Menu.GetNativeMenuStatusAsync();

        rig.Source.Press(Intent(4, DesiredAction.EnsureOn, 3));
        await Traced(rig.Menu, "artwork #4: Active");
        var on = Readback(rig.Host);
        var onStatus = await rig.Menu.GetNativeMenuStatusAsync();

        using (new AssertionScope())
        {
            off.State.Should().Be(CaseStatusState.Pending, "the fake case screen still reports Running: lights-off is pending, never claimed");
            offArtwork.Should().Equal(KeyboardStatusArtwork.Render(off), "the keyboard owner shows exactly the fresh readback");
            offStatus.LastOutcome.Should().Be("确保熄灯：后台已接受；回读：" + off.Title);
            on.State.Should().Be(CaseStatusState.Active);
            rig.Display.ArtworkPixels.ToArray().Should().Equal(KeyboardStatusArtwork.Render(on));
            onStatus.LastOutcome.Should().Be("确保亮灯：后台已接受；回读：" + on.Title);
            rig.Host.Applied.Should().HaveCount(2);
            CaseStatusSummary.IsBlackout(rig.Host.Applied[0]).Should().BeTrue("down ran the existing Blackout");
            rig.Host.Scene.Assignments.Should().BeEquivalentTo(Running().Assignments, o => o.WithStrictOrdering().ComparingRecordsByMembers(),
                "up ran the existing Restore of the recovery point");
            rig.Host.Calls.Should().Contain(["SaveProfile#1", "SetScene#1", "LoadProfile#1", "SetScene#2", "GetScreenScene#1", "ListScreens#1"]);
            rig.Host.Calls.Should().NotContain(c => c.StartsWith("UNEXPECTED", StringComparison.Ordinal));
            rig.Menu.Trace.Should().ContainInOrder("armed context 1 as 1 after #0 (context began)", "press #2 EnsureOff accepted",
                "outcome #2: Applied, readback Pending", "artwork #2: Pending", "press #4 EnsureOn accepted", "outcome #4: Applied, readback Active",
                "artwork #4: Active");
        }
    }

    [Fact]
    public async Task Only_a_fresh_in_scope_neutral_armed_press_acts_and_unmapped_foreign_stale_or_replayed_input_never_does()
    {
        var rig = await Start();
        await rig.Menu.SetNativeMenuEnabledAsync(true);
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        var t0 = _time.GetUtcNow();

        rig.Source.Press(Intent(2, 0, 1)); // An unmapped value (middle has no action).
        rig.Source.Press(Intent(4, (DesiredAction)72, 3)); // A raw volume-like value is no action either.
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5, session: "root-source:other"));
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5, context: 2));
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5, context: 0));
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5, profile: 4));
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 0));
        rig.Source.Press(Intent(4, DesiredAction.EnsureOff, 3));
        rig.Source.Press(Intent(8, DesiredAction.EnsureOff, 5)); // Held: no neutral after the previous press.
        rig.Source.Send(new NativeMenuPress(null!));
        rig.Source.Send(null!);
        _time.Advance(TimeSpan.FromSeconds(3));
        rig.Source.Press(Intent(10, DesiredAction.EnsureOff, 9, at: t0));
        var callsBeforeFresh = rig.Host.Calls;

        rig.Source.Press(Intent(12, DesiredAction.EnsureOff, 11));
        await Traced(rig.Menu, "outcome #12: Applied, readback Pending");
        rig.Source.Begin(context: 1, profile: 3, floor: 0); // A replayed Begin neither re-arms nor resets the sequence.
        rig.Source.Press(Intent(12, DesiredAction.EnsureOff, 11));
        rig.Source.Press(Intent(14, DesiredAction.EnsureOff, 13));
        await Traced(rig.Menu, "outcome #14: Unchanged, readback Pending");

        using (new AssertionScope())
        {
            callsBeforeFresh.Should().BeEmpty("no rejected input reached the case or its screens");
            rig.Menu.Trace.Should().ContainInOrder("press #2 0 rejected: Ambiguous", "press #4 72 rejected: Ambiguous",
                "press #6 EnsureOff rejected: other session", "press #6 EnsureOff rejected: unknown context",
                "press #6 EnsureOff rejected: stale context", "press #6 EnsureOff rejected: ProfileMismatch",
                "press #6 EnsureOff rejected: NoNeutralArm", "press #4 EnsureOff rejected: Replayed",
                "press #8 EnsureOff rejected: StaleNeutralArm", "rejected empty press", "ignored unknown event",
                "press #10 EnsureOff rejected: Expired", "press #12 EnsureOff accepted", "ignored stale context 1 (latest 1)",
                "press #12 EnsureOff rejected: Replayed", "press #14 EnsureOff accepted");
            rig.Host.Applied.Should().ContainSingle("down, down writes once: the second read the case already off");
            rig.Host.Calls.Count(c => c.StartsWith("SetScene", StringComparison.Ordinal)).Should().Be(1);
            (await rig.Menu.GetNativeMenuStatusAsync()).LastOutcome.Should().StartWith("确保熄灯：已是目标状态，未发送");
        }
    }

    [Fact]
    public async Task Preference_load_enable_context_begin_and_restart_never_act_and_a_press_held_across_enable_needs_a_new_neutral()
    {
        var rig = await Start();
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        (await rig.Menu.GetNativeMenuStatusAsync()).State.Should().Be("Off");

        await rig.Menu.SetNativeMenuEnabledAsync(true);
        rig.Source.Press(Intent(3, DesiredAction.EnsureOff, 1)); // Still the press from before the enable.
        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        rig.Host.Calls.Should().BeEmpty();
        rig.Source.Press(Intent(5, DesiredAction.EnsureOff, 4));
        await Traced(rig.Menu, "outcome #5: Applied, readback Pending");
        var calls = rig.Host.Calls;
        await rig.Menu.SetNativeMenuEnabledAsync(false);
        rig.Source.Press(Intent(7, DesiredAction.EnsureOn, 6));
        await rig.Menu.SetNativeMenuEnabledAsync(true);
        await rig.Menu.DisposeAsync();

        // A restart over the same data: the request is loaded, nothing cached or journaled is replayed.
        var restarted = await Restart(rig);
        var loaded = await restarted.Menu.GetNativeMenuStatusAsync();
        restarted.Source.Begin(context: 1, profile: 3, floor: 0, session: NextSession);
        var rearmed = await restarted.Menu.GetNativeMenuStatusAsync();

        using (new AssertionScope())
        {
            rig.Menu.Trace.Should().ContainInOrder("press #2 EnsureOff ignored: preference off", "preference saved: on",
                "armed context 1 as 1 after #2 (enabled)", "press #3 EnsureOff rejected: StaleNeutralArm", "press #2 EnsureOff rejected: Replayed",
                "press #5 EnsureOff accepted", "preference saved: off", "disarmed (disabled): 0 queued not sent",
                "press #7 EnsureOn ignored: preference off", "preference saved: on", "armed context 1 as 2 after #7 (enabled)");
            loaded.Should().Match<NativeMenuStatusDto>(s => s.State == "Unavailable" && s.DesiredEnabled && s.SourcePresent && !s.Active
                && s.LastOutcome == null);
            loaded.Detail.Should().Contain("没有经验证的连接");
            rearmed.State.Should().Be("Active");
            restarted.Menu.Trace.Should().Equal("preference loaded: on", "source connected", "context 1 profile 3 floor 0",
                "armed context 1 as 1 after #0 (context began)");
            rig.Host.Calls.Should().Equal(calls, "disable, enable, stop, preference load, start and a new context sent nothing");
            rig.Host.Applied.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task A_firmware_or_profile_change_disarms_without_resend_and_a_disconnected_session_never_returns()
    {
        var hold = new Hold();
        var rig = await Start(new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask });
        await rig.Menu.SetNativeMenuEnabledAsync(true);
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        rig.Source.Press(Intent(4, DesiredAction.EnsureOn, 3));
        var artwork = rig.Display.ArtworkPixels.ToArray();

        rig.Source.End(context: 1, disconnected: false, "firmware build changed");
        var inFlight = await Traced(rig.Menu, "outcome #2: ");
        var queued = await Traced(rig.Menu, "outcome #4: ");
        var ended = await rig.Menu.GetNativeMenuStatusAsync();
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5));
        var callsAfterEnd = rig.Host.Calls;
        var artworkAfterEnd = rig.Display.ArtworkPixels.ToArray();

        rig.Source.Begin(context: 2, profile: 4, floor: 6); // The new firmware profile is a new context with its own proof.
        rig.Source.Press(Intent(8, DesiredAction.EnsureOff, 7, context: 2, profile: 3));
        var callsBeforeFresh = rig.Host.Calls;
        rig.Source.Press(Intent(8, DesiredAction.EnsureOff, 7, context: 2, profile: 4));
        await Traced(rig.Menu, "artwork #8: Pending");

        rig.Source.End(context: 2, disconnected: true, "receiver unplugged");
        rig.Source.Begin(context: 3, profile: 4, floor: 8);
        var retired = await rig.Menu.GetNativeMenuStatusAsync();
        rig.Source.Press(Intent(10, DesiredAction.EnsureOn, 9, context: 3, profile: 4));
        var callsAfterRetire = rig.Host.Calls;
        rig.Source.Begin(context: 1, profile: 4, floor: 0, session: NextSession);
        rig.Source.Press(Intent(12, DesiredAction.EnsureOn, 11, context: 3, profile: 4));
        var callsBeforeNext = rig.Host.Calls;
        rig.Source.Press(Intent(2, DesiredAction.EnsureOn, 1, context: 1, profile: 4, session: NextSession));
        await Traced(rig.Menu, "artwork #2: Active");

        using (new AssertionScope())
        {
            rig.Menu.Trace.Should().Contain("disarmed (source: firmware build changed): 1 queued not sent, #2 in flight: outcome as answered, never resent");
            inFlight.Should().StartWith("outcome #2: Unknown").And.Contain("stale").And.EndWith("readback none");
            queued.Should().Be("outcome #4: NotSent/ContextRetired stale, readback none");
            ended.Should().Match<NativeMenuStatusDto>(s => s.State == "Unavailable" && !s.Active && s.DesiredEnabled);
            ended.LastOutcome.Should().Be("确保亮灯：未发送（连接已失效，不作为当前反馈）");
            rig.Menu.Trace.Should().NotContain(e => e.StartsWith("artwork #2:", StringComparison.Ordinal) && e != "artwork #2: Active",
                "a retired context's outcome is never shown as current feedback");
            artworkAfterEnd.Should().Equal(artwork, "nothing of the retired context was presented");
            callsAfterEnd.Should().NotContain(c => c.StartsWith("SetScene#2", StringComparison.Ordinal) || c.StartsWith("LoadProfile", StringComparison.Ordinal),
                "the queued intent never ran and the unanswered one was not resent");
            callsBeforeFresh.Should().Equal(callsAfterEnd, "old-context and old-profile presses sent nothing");
            rig.Menu.Trace.Should().ContainInOrder("press #6 EnsureOff rejected: stale context", "armed context 2 as 2 after #6 (context began)",
                "press #8 EnsureOff rejected: ProfileMismatch", "press #8 EnsureOff accepted",
                "context 2 ended, session disconnected: receiver unplugged", "press #10 EnsureOn ignored: not armed",
                "armed context 1 as 4 after #0 (context began)", "press #12 EnsureOn rejected: other session", "press #2 EnsureOn accepted");
            rig.Menu.Trace.Should().Contain(e => e.StartsWith("arm refused: ", StringComparison.Ordinal));
            retired.Should().Match<NativeMenuStatusDto>(s => s.State == "Unavailable" && !s.Active);
            retired.Detail.Should().Contain("已失效或被替代");
            callsBeforeNext.Should().Equal(callsAfterRetire, "the retired session and its replacement's stale input sent nothing");
            rig.Host.Applied.Should().HaveCount(2, "one blackout in the new profile, one restore in the next session");
            rig.Display.ArtworkPixels.ToArray().Should().Equal(KeyboardStatusArtwork.Render(Readback(rig.Host)));
        }
    }

    [Fact]
    public async Task A_configuration_handoff_pauses_suppresses_the_queue_and_artwork_and_resuming_needs_a_new_press()
    {
        var hold = new Hold();
        var rig = await Start(new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask });
        await rig.Menu.SetNativeMenuEnabledAsync(true);
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        rig.Source.Press(Intent(4, DesiredAction.EnsureOn, 3));

        rig.Gate.BlockRogAcquisition();
        var inFlight = await Traced(rig.Menu, "outcome #2: ");
        var queued = await Traced(rig.Menu, "outcome #4: ");
        var paused = await rig.Menu.GetNativeMenuStatusAsync();
        var presented = rig.Display.PresentCaseObservation(new(CaseStatusState.Active, "fake", "fake"), () => true);
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5));
        var callsInHandoff = rig.Host.Calls;

        rig.Gate.AllowRogAcquisition();
        var resumed = await rig.Menu.GetNativeMenuStatusAsync();
        rig.Source.Press(Intent(7, DesiredAction.EnsureOff, 5)); // Held through the whole handoff.
        var callsAfterResume = rig.Host.Calls;
        rig.Source.Press(Intent(9, DesiredAction.EnsureOff, 8));
        await Traced(rig.Menu, "artwork #9: Pending");

        using (new AssertionScope())
        {
            inFlight.Should().StartWith("outcome #2: Unknown").And.Contain("stale");
            queued.Should().Be("outcome #4: NotSent/ContextRetired stale, readback none");
            paused.Should().Match<NativeMenuStatusDto>(s => s.State == "Paused" && !s.Active && s.DesiredEnabled && s.SourcePresent);
            paused.Detail.Should().Contain("键鼠外设配置中");
            presented.Should().BeFalse("the keyboard owner refuses artwork while its interfaces are handed off");
            resumed.State.Should().Be("Active");
            callsAfterResume.Should().Equal(callsInHandoff, "the handoff's end and a held press send nothing");
            callsInHandoff.Should().NotContain(c => c.StartsWith("LoadProfile", StringComparison.Ordinal) || c.StartsWith("SetScene#2", StringComparison.Ordinal));
            rig.Menu.Trace.Should().ContainInOrder("disarmed (configuration handoff): 1 queued not sent, #2 in flight: outcome as answered, never resent",
                "press #6 EnsureOff ignored: configuration handoff", "armed context 1 as 2 after #6 (configuration handoff ended)",
                "press #7 EnsureOff rejected: StaleNeutralArm", "press #9 EnsureOff accepted", "artwork #9: Pending");
            rig.Host.Applied.Should().ContainSingle();
            rig.Display.ArtworkPixels.ToArray().Should().Equal(KeyboardStatusArtwork.Render(Readback(rig.Host)));
        }
    }

    [Fact]
    public async Task Stop_with_a_write_ignoring_cancellation_reports_it_unknown_drops_the_bounded_queue_and_never_resends()
    {
        var stuck = new Hold(ignoresCancellation: true);
        var rig = await Start(new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? stuck.WaitAsync(ct) : Task.CompletedTask });
        await rig.Menu.SetNativeMenuEnabledAsync(true);
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        await stuck.Entered;
        rig.Source.Press(Intent(4, DesiredAction.EnsureOn, 3));
        rig.Source.Press(Intent(6, DesiredAction.EnsureOff, 5));
        rig.Source.Press(Intent(8, DesiredAction.EnsureOn, 7));
        rig.Source.Press(Intent(10, DesiredAction.EnsureOff, 9));
        var calls = rig.Host.Calls;

        await rig.Menu.StopAsync(default).WaitAsync(Patience);
        var stopped = await rig.Menu.GetNativeMenuStatusAsync();
        rig.Source.Press(Intent(12, DesiredAction.EnsureOff, 11)); // A source that keeps delivering after Stop.
        var write = () => rig.Menu.SetNativeMenuEnabledAsync(false);
        await write.Should().ThrowAsync<InvalidOperationException>();
        stuck.Release();
        await Eventually(() => rig.Host.Applied.Length == 1, "the late write lands by itself");
        await rig.Menu.StopAsync(default).WaitAsync(Patience);

        using (new AssertionScope())
        {
            rig.Menu.Trace.Should().ContainInOrder("press #8 EnsureOn accepted", "press #10 EnsureOff rejected: QueueFull", "stopping");
            rig.Menu.Trace.Should().Contain(e => e.StartsWith("outcome #2: Unknown", StringComparison.Ordinal));
            foreach (var sequence in new[] { 4, 6, 8 })
                rig.Menu.Trace.Should().Contain(e => e.StartsWith($"outcome #{sequence}: NotSent/Stopped", StringComparison.Ordinal));
            rig.Menu.Trace.Should().Contain("ignored after stop: NativeMenuPress").And.NotContain(e => e.StartsWith("artwork", StringComparison.Ordinal));
            rig.Source.Disposed.Should().BeTrue("Stop ends the source registration");
            stopped.Should().Match<NativeMenuStatusDto>(s => s.State == "Stopped" && !s.Active);
            stopped.LastError.Should().Be("停止时仍有未答复的机箱请求：结果未知，未重发。");
            stopped.LastOutcome.Should().StartWith("确保亮灯：未发送");
            File.ReadAllText(PreferencePath).Should().Be("{\"Schema\":1,\"Enabled\":true}", "a request after Stop is refused, not half-applied");
            rig.Host.Calls.Should().Equal(calls, "no readback, resend or queued intent followed the late write");
        }
    }

    [Theory]
    [InlineData("{\"Schema\":1,\"Enabled\":tru")]
    [InlineData("null")]
    [InlineData("{\"Schema\":2,\"Enabled\":true}")]
    [InlineData("oversized")]
    public async Task A_corrupt_preference_fails_closed_for_the_menu_only_and_an_explicit_choice_repairs_it(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
        File.WriteAllText(PreferencePath, content == "oversized" ? "{\"Schema\":1,\"Enabled\":true}" + new string(' ', 4096) : content);

        var rig = await Start();
        rig.Source.Begin(context: 1, profile: 3, floor: 0);
        rig.Source.Press(Intent(2, DesiredAction.EnsureOff, 1));
        var failed = await rig.Menu.GetNativeMenuStatusAsync();
        var keyboard = await rig.Display.GetKeyboardDisplayStatusAsync();
        var repaired = await rig.Menu.SetNativeMenuEnabledAsync(false);
        await rig.Menu.DisposeAsync();
        var restarted = await Restart(rig);

        using (new AssertionScope())
        {
            failed.Should().Match<NativeMenuStatusDto>(s => s.State == "Error" && !s.PreferenceReadable && !s.DesiredEnabled && !s.Active && s.SourcePresent);
            failed.LastError.Should().StartWith("原生菜单联动偏好无法读取：");
            Claims(failed);
            keyboard.State.Should().NotBe("Error", "only the native menu fails closed; the keyboard owner on the same data is unaffected");
            rig.Menu.Trace.Should().ContainInOrder("source connected", "press #2 EnsureOff ignored: preference off", "preference saved: off");
            rig.Menu.Trace[0].Should().StartWith("preference unreadable, treated as off: ");
            repaired.Should().Match<NativeMenuStatusDto>(s => s.State == "Off" && s.PreferenceReadable && s.LastError == null);
            restarted.Menu.Trace[0].Should().Be("preference loaded: off");
            rig.Host.Calls.Should().BeEmpty();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services) await service.DisposeAsync();
        foreach (var owned in _owned) owned.Dispose();
        try { Directory.Delete(_data, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private UnifiedRgbOptions Options() => new() { UseVirtualAdapter = false, DataDirectory = _data };

    private async Task<Rig> Start(MenuHostFake? host = null)
    {
        host ??= new MenuHostFake(Running());
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(false, null));
        var display = new KeyboardDisplayService(Options(), host.Connection.Rgb, host.Connection.Screens!, new NoOled(), new NoGuard(),
            wirelessFactory: new NoWirelessOled(), configurationGate: gate);
        _owned.Add(display); _owned.Add(gate);
        var rig = new Rig(null!, null!, host, display, gate);
        return await Restart(rig);
    }

    // A new owner and a new source instance on the same data directory, Host connection, keyboard owner and gate.
    private async Task<Rig> Restart(Rig rig)
    {
        var source = new FakeSource();
        var menu = new NativeMenuService(Options(), rig.Host.Connection.Rgb, rig.Host.Connection.Screens, source, rig.Display, rig.Gate, _time);
        _services.Add(menu);
        await menu.StartAsync(default);
        return rig with { Menu = menu, Source = source };
    }

    private NativeMenuCaseIntent Intent(long sequence, DesiredAction action, long arm, long context = 1, long profile = 3,
        string session = Session, DateTimeOffset? at = null) =>
        new(session, context, profile, sequence, at ?? _time.GetUtcNow(), action, arm);

    // What the coordinator's fresh readback observes on the same fake right now.
    private static CaseStatusObservation Readback(MenuHostFake host) =>
        CaseStatusSummary.Describe(host.Scene, host.Controllers, host.ScreenScene, host.Screens);

    private static void Claims(NativeMenuStatusDto status)
    {
        foreach (var text in new[] { status.Detail, status.LastOutcome ?? string.Empty, status.LastError ?? string.Empty })
            text.Should().NotContain("已上线").And.NotContain("运行");
    }

    private static async Task<string> Traced(NativeMenuService menu, string prefix)
    {
        string? entry = null;
        await Eventually(() => (entry = Array.Find(menu.Trace, e => e.StartsWith(prefix, StringComparison.Ordinal))) is not null, prefix);
        return entry!;
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Not reached in time: " + what);
            await Task.Delay(10);
        }
    }

    private static SceneDto Running() => new([new([new("lianli:1", 0), new("lianli:1", 1)], "Rainbow", ["#FF0000", "#00FF00"], Speed: 0.25f)]);

    private sealed record Rig(NativeMenuService Menu, FakeSource Source, MenuHostFake Host, KeyboardDisplayService Display, PeripheralConfigurationGate Gate);

    /// <summary>Stands in for Root's future trusted source: delivers typed events it is told to, also after its registration ended.</summary>
    private sealed class FakeSource : INativeMenuSource
    {
        private Action<NativeMenuSourceEvent>? _deliver;
        public bool IsPresent => true;
        public bool Disposed { get; private set; }

        public IDisposable Connect(Action<NativeMenuSourceEvent> deliver)
        {
            _deliver.Should().BeNull("one owner connects once");
            _deliver = deliver;
            return new Registration(this);
        }

        public void Send(NativeMenuSourceEvent sourceEvent) => (_deliver ?? throw new InvalidOperationException("Not connected."))(sourceEvent);
        public void Begin(long context, long profile, long floor, string session = Session) => Send(new NativeMenuContextBegun(session, context, profile, floor));
        public void End(long context, bool disconnected, string reason) => Send(new NativeMenuContextEnded(Session, context, disconnected, reason));
        public void Press(NativeMenuCaseIntent intent) => Send(new NativeMenuPress(intent));

        private sealed class Registration(FakeSource owner) : IDisposable
        {
            public void Dispose() => owner.Disposed = true;
        }
    }

    private sealed class NoOled : IRogWiredOledFactory
    {
        public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("No keyboard transport may be opened.");
    }

    private sealed class NoWirelessOled : IRogWirelessOledFactory
    {
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader) =>
            throw new InvalidOperationException("No keyboard transport may be opened.");
    }

    private sealed class NoGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => throw new InvalidOperationException("No writer guard use.");
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => throw new InvalidOperationException("No writer guard use.");
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover.");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover.");
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException("No takeover.");
    }

    /// <summary>Holds one intercepted call until released; optionally ignores its token like a backend that never observes it.</summary>
    private sealed class Hold(bool ignoresCancellation = false)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task.WaitAsync(Patience);
        public void Release() => _released.TrySetResult();

        public Task WaitAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            return _released.Task.WaitAsync(Patience, ignoresCancellation ? CancellationToken.None : ct);
        }
    }
}

/// <summary>
/// The App side: the optional RPC through the shared contract JSON over an in-memory stream pair (no pipe, no Host process), an
/// old Host without the target, one request per click with an honest readback, the unapplied draft, and the actual panel XAML.
/// </summary>
[Collection("Host")]
public sealed class NativeMenuPanelIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly ControllerDto Controller = new("case:one", "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true);
    private static readonly SceneDto CaseScene = new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])]);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "urgb-native-menu-app-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    [Fact]
    public async Task An_old_host_without_the_target_is_read_only_and_never_probed_again_on_that_connection()
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        server.StartListening(); // An older Host: no native-menu target.
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<INativeMenuService>();
        client.StartListening();
        var errors = new List<Exception>();
        var proxies = 0;
        var vm = new NativeMenuViewModel(() => { proxies++; return proxy; }, InlineDispatcher.Instance, errors.Add, timeout: Guard);

        await vm.RefreshAsync();
        await vm.ToggleCommand.ExecuteAsync().WaitAsync(Guard);
        await vm.RefreshAsync();

        using (new AssertionScope())
        {
            vm.IsUnsupported.Should().BeTrue();
            vm.Message.Should().Be(NativeMenuViewModel.UnsupportedMessage);
            vm.StateTitle.Should().Be("当前后台不支持原生菜单联动");
            vm.CanToggle.Should().BeFalse(); vm.ToggleCommand.CanExecute(null).Should().BeFalse();
            proxies.Should().Be(1, "one probe; a click on an unsupported Host sends nothing");
            errors.Should().BeEmpty();
        }
        await using var pipe = new PipeClient("UnifiedRgb-audit-unused-" + Guid.NewGuid().ToString("N"), PipeTrust.ElevatedOwnerOrCurrentUser);
        pipe.Invoking(c => c.RequireNativeMenu()).Should().Throw<HostNotConnectedException>();
        pipe.Connections.Should().Be(0);
    }

    [Fact]
    public async Task The_new_target_round_trips_the_real_owner_and_a_click_only_changes_the_request()
    {
        var host = new MenuHostFake(CaseScene);
        await using var menu = new NativeMenuService(new UnifiedRgbOptions { UseVirtualAdapter = false, DataDirectory = _data },
            host.Connection.Rgb, host.Connection.Screens, new UnavailableNativeMenuSource(), time: new ManualTimeProvider());
        await menu.StartAsync(default);
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        server.AddLocalRpcTarget<INativeMenuService>(menu, null);
        server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<INativeMenuService>();
        client.StartListening();
        var errors = new List<Exception>();
        var vm = new NativeMenuViewModel(() => proxy, InlineDispatcher.Instance, errors.Add, timeout: Guard);

        await vm.RefreshAsync();
        var off = vm.Status;
        var direct = await menu.GetNativeMenuStatusAsync();
        await vm.ToggleCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            off.Should().Be(direct, "every field survives the shared contract JSON settings");
            off!.State.Should().Be("Off");
            vm.Status.Should().Be(await menu.GetNativeMenuStatusAsync(), "the click shows the Host's answer");
            vm.Status!.Should().Match<NativeMenuStatusDto>(s => s.State == "Unavailable" && s.DesiredEnabled && !s.SourcePresent && !s.Active);
            vm.StateTitle.Should().Be("已请求开启：等待经验证的菜单来源（当前不可用）");
            vm.StatusLine.Should().StartWith("已请求：开启 · 菜单来源：未接入 · 实际：未生效");
            vm.ToggleLabel.Should().Be("关闭原生菜单联动");
            Claims(vm);
            menu.Trace.Should().Equal("no trusted source installed", "preference saved: on");
            host.Calls.Should().BeEmpty("the request reaches neither the case nor its screens");
            errors.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("answered")]
    [InlineData("lost")]
    [InlineData("rejected")]
    public async Task One_click_sends_one_inverse_request_on_the_captured_proxy_and_an_unknown_answer_is_read_back_never_resent(string answer)
    {
        var fake = new FakeMenu();
        var lost = new TaskCompletionSource<NativeMenuStatusDto>();
        fake.WriteOverride = answer switch
        {
            "lost" => (enabled, _) => { fake.Current = Status(enabled, "Unavailable"); return lost.Task; }, // Applied; the answer is lost.
            "rejected" => (_, _) => Task.FromException<NativeMenuStatusDto>(new RemoteInvocationException("fake refusal", 0, (object?)null)),
            _ => null,
        };
        var errors = new List<Exception>();
        var proxies = 0;
        var vm = new NativeMenuViewModel(() => { proxies++; return fake; }, InlineDispatcher.Instance, errors.Add,
            timeout: answer == "lost" ? TimeSpan.FromMilliseconds(250) : Guard);

        await vm.RefreshAsync();
        vm.CanToggle.Should().BeTrue();
        vm.ToggleLabel.Should().Be("开启原生菜单联动");
        await vm.ToggleCommand.ExecuteAsync().WaitAsync(Guard);
        var afterClick = fake.Trace;
        await vm.RefreshAsync();

        string[] expected = answer == "answered" ? ["read", "read", "set(True)"] : ["read", "read", "set(True)", "read"];
        string[] polled = [.. afterClick, "read"];

        using (new AssertionScope())
        {
            afterClick.Should().Equal(expected, "a fresh preflight, exactly one request and, without an answer, one readback");
            fake.Trace.Should().Equal(polled, "a later poll only reads");
            proxies.Should().Be(3, "the click captured one proxy for its preflight, request and readback");
            vm.CanToggle.Should().BeTrue();
            switch (answer)
            {
                case "answered":
                    vm.Status!.State.Should().Be("Unavailable");
                    errors.Should().BeEmpty();
                    break;
                case "lost":
                    vm.Status!.DesiredEnabled.Should().BeTrue("the readback shows that the unanswered request applied");
                    errors.Should().BeEmpty("a lost answer is an unknown result, not a Host error");
                    break;
                default:
                    vm.Status!.State.Should().Be("Off", "the readback shows the refusal changed nothing");
                    errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>();
                    break;
            }
            Claims(vm);
        }
        if (answer == "lost")
        {
            var notes = new List<string>();
            var read = new NativeMenuViewModel(() => fake, InlineDispatcher.Instance, errors.Add, timeout: TimeSpan.FromMilliseconds(250));
            read.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NativeMenuViewModel.Message)) notes.Add(read.Message); };
            fake.Current = Status(false, "Off");
            await read.RefreshAsync();
            await read.ToggleCommand.ExecuteAsync().WaitAsync(Guard);
            notes.Should().Contain(n => n.Contains("未自动重发", StringComparison.Ordinal));
            read.Message.Should().Be("开启原生菜单联动结果未确认：未收到后台答复，未自动重发；以上为回读状态。");
            fake.Trace.Count(t => t.StartsWith("set", StringComparison.Ordinal)).Should().Be(2, "one request per click, across both clicks");
        }
    }

    [Fact]
    public async Task The_native_menu_click_leaves_the_unapplied_draft_and_the_case_scene_untouched()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var errors = new List<Exception>();
        using var studio = new StudioViewModel(new FakeConnection(proxy).Connect(), InlineDispatcher.Instance, errors.Add);
        var fake = new FakeMenu();
        var native = new NativeMenuViewModel(() => fake, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        studio.ControlPanel.AttachNativeMenu(native);
        studio.Draft.Name = "未应用的草稿";
        var draft = (studio.Draft.Name, studio.Draft.Dirty, studio.Draft.Revision, studio.Draft.DraftState, studio.Draft.Stages.Count);
        var caseReads = rpc.Reads;

        await native.RefreshAsync();
        await native.ToggleCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            studio.ControlPanel.NativeMenu.Should().BeSameAs(native);
            studio.ControlPanel.HasNativeMenu.Should().BeTrue();
            fake.Trace.Should().Equal("read", "read", "set(True)");
            draft.Dirty.Should().BeTrue();
            (studio.Draft.Name, studio.Draft.Dirty, studio.Draft.Revision, studio.Draft.DraftState, studio.Draft.Stages.Count).Should().Be(draft);
            rpc.Writes.Should().BeEmpty(); rpc.Submitted.Should().BeEmpty(); rpc.Loads.Should().Be(0);
            rpc.Reads.Should().Be(caseReads, "the native-menu request reads no case state");
            errors.Should().BeEmpty();
        }
        studio.ControlPanel.Invoking(p => p.AttachNativeMenu(new(() => fake, InlineDispatcher.Instance, errors.Add)))
            .Should().Throw<InvalidOperationException>("one section per panel");
        studio.ControlPanel.SetConnected(false); // The panel forwards the connection, so a stale status never authorises a click.
        native.CanToggle.Should().BeFalse();
        native.StateTitle.Should().Be("后台未连接");
        fake.Trace.Should().Equal("read", "read", "set(True)");
    }

    [Fact]
    public async Task Actual_panel_xaml_hides_the_section_until_attached_then_shows_the_honest_request_state_and_its_toggle()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy; rpc.Scene = CaseScene; rpc.Controllers = [Controller];
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var errors = new List<Exception>();
        var panel = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var detached = Render(panel);
        var fake = new FakeMenu { Current = Status(true, "Unavailable") };
        var native = new NativeMenuViewModel(() => fake, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        panel.AttachNativeMenu(native);
        await native.RefreshAsync();
        native.Status.Should().NotBeNull();
        var trace = fake.Trace;
        var attached = Render(panel);

        using (new AssertionScope())
        {
            detached.Texts.Should().Contain(("原生菜单联动", false), "an App without the section attached shows nothing of it");
            attached.Texts.Should().Contain(("原生菜单联动", true));
            attached.Texts.Should().Contain((NativeMenuViewModel.Guide, true));
            attached.Texts.Should().Contain(("已请求开启：等待经验证的菜单来源（当前不可用）", true));
            attached.Texts.Should().Contain(t => t.Shown && t.Text.StartsWith("已请求：开启 · 菜单来源：未接入 · 实际：未生效", StringComparison.Ordinal));
            attached.Buttons.Should().ContainKey("关闭原生菜单联动").WhoseValue.Enabled.Should().BeTrue();
            attached.Buttons["关闭原生菜单联动"].ToolTip.Should().Contain("只保存本机偏好（默认关闭）").And.Contain("不进入或退出外设配置");
            attached.Buttons["关闭原生菜单联动"].Help.Should().Be(native.Message);
            attached.Buttons.Should().ContainKeys("全机熄灯", "恢复当前场景", "重连键盘灯光");
            attached.Native.Should().Contain([NativeMenuViewModel.Guide, native.StateTitle, native.StatusLine, native.Detail, native.Message]);
            foreach (var text in attached.Native) text.Should().NotContain("已上线").And.NotContain("运行");
            detached.Native.Should().BeEmpty();
            fake.Trace.Should().Equal(trace, "binding calls nothing");
            rpc.Writes.Should().BeEmpty(); errors.Should().BeEmpty();
        }
    }

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        try { Directory.Delete(_data, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static NativeMenuStatusDto Status(bool desired, string state) =>
        new(desired, true, false, false, state, $"fake {state} detail", null, At, null);

    private static void Claims(NativeMenuViewModel vm)
    {
        foreach (var text in new[] { vm.StateTitle, vm.StatusLine, vm.Detail, vm.Message, vm.LastOutcomeText, vm.LastErrorText, vm.ToggleLabel, NativeMenuViewModel.Guide })
            text.Should().NotContain("已上线").And.NotContain("运行");
    }

    // Loads the actual panel XAML on an STA thread without a window; Shown means the element and every logical ancestor are visible.
    private static Rendered Render(CaseControlPanelViewModel vm)
    {
        var buttons = new Dictionary<string, (bool Enabled, string ToolTip, string Help)>();
        var texts = new List<(string Text, bool Shown)>();
        var native = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = vm };
                view.Measure(new(300, 1600)); view.Arrange(new(0, 0, 300, 1600)); view.UpdateLayout();
                foreach (var (button, _) in Logical<System.Windows.Controls.Button>(view, true))
                    buttons[button.Content?.ToString() ?? string.Empty] = (button.IsEnabled, button.ToolTip?.ToString() ?? string.Empty,
                        System.Windows.Automation.AutomationProperties.GetHelpText(button));
                foreach (var (text, shown) in Logical<System.Windows.Controls.TextBlock>(view, true))
                {
                    texts.Add((text.Text, shown));
                    if (shown && text.DataContext is NativeMenuViewModel) native.Add(text.Text);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
        return new(buttons, texts, native);
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

    // Native: the shown texts of the native-menu section, whose data context is its view model.
    private sealed record Rendered(Dictionary<string, (bool Enabled, string ToolTip, string Help)> Buttons, List<(string Text, bool Shown)> Texts,
        List<string> Native);

    /// <summary>Callable INativeMenuService fake: records every read and request in order.</summary>
    private sealed class FakeMenu : INativeMenuService
    {
        private readonly List<string> _trace = [];
        public NativeMenuStatusDto Current { get; set; } = Status(false, "Off");
        public Func<bool, CancellationToken, Task<NativeMenuStatusDto>>? WriteOverride { get; set; }

        public string[] Trace
        {
            get { lock (_trace) return [.. _trace]; }
        }

        public Task<NativeMenuStatusDto> GetNativeMenuStatusAsync(CancellationToken ct = default)
        {
            lock (_trace) _trace.Add("read");
            return Task.FromResult(Current);
        }

        public Task<NativeMenuStatusDto> SetNativeMenuEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            lock (_trace) _trace.Add($"set({enabled})");
            if (WriteOverride is { } write) return write(enabled, ct);
            Current = Status(enabled, enabled ? "Unavailable" : "Off");
            return Task.FromResult(Current);
        }
    }
}
