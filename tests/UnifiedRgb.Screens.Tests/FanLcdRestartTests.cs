using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Manual fan-LCD group restart (design/OPUS-LCD-RESTART-UI-BACKEND-20261018.md), against a fake RF recovery: config and RF
/// precheck before any I/O, one operation in flight, cooldown from the first actual send only, ONLY the mapped TL workers
/// quiesced (hotplug included), H2/TURZX/unmapped screens untouched, scene and StopScreens pause preserved, the mutation gate
/// held, and the workers restored on success, failure and cancellation. Acknowledgement and reconnection are separate results.
/// </summary>
public sealed class FanLcdRestartTests
{
    private const string Controller = "lianli:rf:35-F7-EF-72-AB-3C";
    private static readonly string Tl1 = Devices.Tl1.Id;
    private static readonly string Tl2 = Devices.Tl2.Id;
    private static readonly string Tl3 = Devices.Tl3.Id;
    private static readonly string H2 = Devices.H2.Id;
    private static readonly string Turzx = Devices.Turzx.Id;

    private static readonly ScreenSceneDto AllScene = Images.Scene(
        new ScreenAssignmentDto(Tl1, BackgroundColor: "#FF0000", Fps: 15),
        new ScreenAssignmentDto(Tl2, BackgroundColor: "#00FF00", Fps: 15),
        new ScreenAssignmentDto(Tl3, BackgroundColor: "#0000FF", Fps: 15),
        new ScreenAssignmentDto(H2, BackgroundColor: "#FFFFFF", Fps: 15),
        new ScreenAssignmentDto(Turzx, BackgroundColor: "#808080"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_closed_master_or_family_gate_refuses_restart_without_hardware_io(bool closeMaster)
    {
        var recovery = new FakeRecovery();
        await using var rig = new Rig(recovery, o =>
        {
            Mapped(Tl1, Tl2, Tl3)(o);
            if (closeMaster) o.Enabled = false; else o.TlLcd.Output = false;
        }, Devices.Tl1, Devices.Tl2, Devices.Tl3);
        var result = await rig.Manager.RestartFanLcdAsync();
        result.Message.Should().StartWith("Disabled:"); result.Attempts.Should().Be(0);
        (recovery.Validates, recovery.Resets, rig.Opener.Opens).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task The_total_deadline_cancels_a_slow_recovery_and_restores_quiesced_workers()
    {
        var recovery = new FakeRecovery { Reset = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(false, 0, "never"); } };
        await using var rig = new Rig(recovery, Mapped(Tl1, Tl2, Tl3), Devices.Tl1, Devices.Tl2, Devices.Tl3);
        rig.Manager.RestartTotal = TimeSpan.FromMilliseconds(100);
        await FluentActions.Awaiting(() => rig.Manager.RestartFanLcdAsync()).Should().ThrowAsync<OperationCanceledException>();
        rig.Manager.RestartQuiescing.Should().BeFalse();
    }

    private sealed class FakeRecovery : IFanLcdGroupRecovery
    {
        private int _validates;
        private int _resets;
        private int _sends;

        public Func<string, int, Exception?>? ValidateError { get; set; }

        /// <summary>Default: one send, acknowledged.</summary>
        public Func<Action, CancellationToken, Task<FanLcdGroupResetResult>> Reset { get; set; } = (send, _) =>
        {
            send();
            return Task.FromResult(new FanLcdGroupResetResult(true, 1, "receiver reported command sequence 2"));
        };

        public int Validates => Volatile.Read(ref _validates);

        public int Resets => Volatile.Read(ref _resets);

        public int Sends => Volatile.Read(ref _sends);

        public (string Controller, int Fans)? Last { get; private set; }

        public void Validate(string controllerId, int lcdFans)
        {
            Interlocked.Increment(ref _validates);
            if (ValidateError?.Invoke(controllerId, lcdFans) is { } ex)
            {
                throw ex;
            }
        }

        public Task<FanLcdGroupResetResult> ResetAsync(string controllerId, int lcdFans, Action beforeFirstSend, CancellationToken ct)
        {
            Interlocked.Increment(ref _resets);
            Last = (controllerId, lcdFans);
            return Reset(() =>
            {
                Interlocked.Increment(ref _sends);
                beforeFirstSend();
            }, ct);
        }
    }

    private static Action<ScreensOptions> Mapped(params string[] ids) => o =>
    {
        Rig.AllOutput(o);
        o.FanLcdRestart = new FanLcdRestartOptions { Enabled = true, ControllerId = Controller, ScreenIds = ids };
    };

    private static async Task<Rig> RunningAsync(FakeRecovery recovery, Action<ScreensOptions> configure)
    {
        var rig = new Rig(recovery, configure, Devices.Tl1, Devices.Tl2, Devices.Tl3, Devices.H2, Devices.Turzx);
        rig.Manager.RestartSettle = TimeSpan.FromMilliseconds(50);
        rig.Manager.RestartReconnectWait = TimeSpan.FromSeconds(10);
        await rig.Manager.SetScreenSceneAsync(AllScene);
        rig.Manager.Start();
        await Wait.UntilAsync(() => new[] { Tl1, Tl2, Tl3, H2, Turzx }.All(id => rig.Opener.For(id) is [{ Writes.Count: >= 2 }]), "all five screens sending");
        return rig;
    }

    public static TheoryData<string, string, string[]> BadConfigs => new()
    {
        { "H2 has no verified reset", Controller, [Tl1, H2] },
        { "TURZX has no verified reset", Controller, [Turzx] },
        { "duplicate", Controller, [Tl1, Tl1] },
        { "no screens", Controller, [] },
        { "nine screens", Controller, [.. Enumerable.Range(0, 9).Select(i => $"screen:tl-lcd:{i:x16}")] },
        { "not a screen id", Controller, ["tl-lcd:a4396dc0631e0a66"] },
        { "not a Lian Li controller", "msi:gpu:0", [Tl1] },
        { "empty controller", "", [Tl1] },
    };

    [Theory]
    [MemberData(nameof(BadConfigs))]
    public async Task Invalid_mapping_is_refused_before_any_io(string why, string controller, string[] ids)
    {
        var recovery = new FakeRecovery();
        await using var rig = new Rig(recovery, o =>
        {
            Rig.AllOutput(o);
            o.FanLcdRestart = new FanLcdRestartOptions { Enabled = true, ControllerId = controller, ScreenIds = ids };
        }, Devices.Tl1, Devices.H2, Devices.Turzx);
        var r = await rig.Manager.RestartFanLcdAsync();
        (r.CommandAccepted, r.ScreensReconnected, r.Attempts).Should().Be((false, false, 0), why);
        r.Message.Should().StartWith("Precheck:").And.Contain("nothing was sent");
        (recovery.Validates, recovery.Resets, rig.Enumerator.Calls, rig.Opener.Opens).Should().Be((0, 0, 0, 0), "no RF, enumeration or USB I/O");
    }

    [Fact]
    public async Task Disabled_or_without_a_recovery_nothing_happens()
    {
        var recovery = new FakeRecovery();
        await using (var rig = new Rig(recovery, o =>
        {
            Rig.AllOutput(o);
            o.FanLcdRestart = new FanLcdRestartOptions { Enabled = false, ControllerId = Controller, ScreenIds = [Tl1] };
        }, Devices.Tl1))
        {
            var r = await rig.Manager.RestartFanLcdAsync();
            r.Message.Should().StartWith("Disabled:");
            r.ScreenIds.Should().Equal(Tl1);
        }

        await using (var rig = new Rig(null, Mapped(Tl1), Devices.Tl1))
        {
            (await rig.Manager.RestartFanLcdAsync()).Message.Should().StartWith("Precheck:").And.Contain("no Lian Li fan-LCD recovery");
            (rig.Enumerator.Calls, rig.Opener.Opens).Should().Be((0, 0));
        }

        recovery.Validates.Should().Be(0);
        new ScreensOptions().FanLcdRestart.Should().BeEquivalentTo(new FanLcdRestartOptions(), "source default: disabled, empty, no hardcoded identity");
        new ScreensOptions().FanLcdRestart.Enabled.Should().BeFalse();
        new ScreensOptions().FanLcdRestart.ScreenIds.Should().BeEmpty();
        new ScreensOptions().FanLcdRestart.ControllerId.Should().BeEmpty();
    }

    [Fact]
    public async Task Rf_precheck_failure_sends_nothing_disturbs_no_worker_and_consumes_no_cooldown()
    {
        var recovery = new FakeRecovery { ValidateError = (_, _) => new InvalidOperationException("lianli:rf:35-F7-EF-72-AB-3C is not actively owned by this Host.") };
        await using var rig = await RunningAsync(recovery, Mapped(Tl1, Tl2, Tl3));
        var opens = rig.Opener.Opens;
        var r = await rig.Manager.RestartFanLcdAsync();
        r.Message.Should().StartWith("Precheck:").And.Contain("not actively owned").And.Contain("Nothing was sent");
        (r.Attempts, recovery.Resets).Should().Be((0, 0));
        rig.Opener.Opens.Should().Be(opens);
        rig.Opener.Channels.Should().OnlyContain(c => !c.Disposed, "a refused restart quiesces nothing");
        recovery.ValidateError = null;
        (await rig.Manager.RestartFanLcdAsync()).Message.Should().StartWith("Restarted:", "the refusal did not start the cooldown");
        recovery.Last.Should().Be((Controller, 3));
    }

    [Fact]
    public async Task Only_mapped_workers_are_quiesced_and_restored_with_the_scene_unchanged()
    {
        var recovery = new FakeRecovery();
        await using var rig = await RunningAsync(recovery, Mapped(Tl1, Tl2));
        var scene = await rig.Manager.GetScreenSceneAsync();
        var before = rig.Opener.Opened.ToArray();
        bool[]? during = null;
        recovery.Reset = (send, _) =>
        {
            during = [rig.Manager.HasWorker(Tl1), rig.Manager.HasWorker(Tl2), rig.Manager.HasWorker(Tl3), rig.Manager.HasWorker(H2), rig.Manager.HasWorker(Turzx),
                rig.Opener.For(Tl1)[0].Disposed, rig.Opener.For(Tl2)[0].Disposed, rig.Manager.RestartQuiescing];
            send();
            return Task.FromResult(new FanLcdGroupResetResult(true, 1, "acknowledged"));
        };
        var r = await rig.Manager.RestartFanLcdAsync();
        during.Should().Equal(false, false, true, true, true, true, true, true);
        (r.CommandAccepted, r.ScreensReconnected, r.Attempts).Should().Be((true, true, 1));
        r.ScreenIds.Should().Equal(Tl1, Tl2);
        r.Message.Should().StartWith("Restarted:").And.Contain("Not optical proof");
        foreach (var id in new[] { Tl1, Tl2 })
        {
            rig.Opener.For(id).Should().HaveCount(2, "a fresh worker reopened after the restart");
            rig.Opener.For(id)[1].Writes.Should().NotBeEmpty("ScreensReconnected required new frames while output runs");
        }

        foreach (var id in new[] { Tl3, H2, Turzx })
        {
            rig.Opener.For(id).Should().ContainSingle().Which.Disposed.Should().BeFalse("non-target screens are untouched");
        }

        rig.Opener.Opened.Take(before.Length).Should().Equal(before);
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Should().Equal(scene.Assignments);
        rig.Manager.RestartQuiescing.Should().BeFalse();
    }

    [Fact]
    public async Task Hotplug_during_the_restart_creates_no_worker_for_a_mapped_screen()
    {
        var recovery = new FakeRecovery();
        await using var rig = await RunningAsync(recovery, Mapped(Tl1, Tl2, Tl3));
        var listed = new List<string>();
        recovery.Reset = async (send, ct) =>
        {
            send();
            rig.Enumerator.Set(Devices.H2, Devices.Turzx); // the group dropped off the bus
            await Task.Delay(600, ct); // > two discovery passes
            rig.Enumerator.Set(Devices.Tl1, Devices.Tl2, Devices.Tl3, Devices.H2, Devices.Turzx); // ...and re-enumerated
            await Task.Delay(600, ct);
            listed.AddRange((await rig.Manager.ListScreensAsync(ct)).Where(s => s.Family == "TlLcd").Select(s => s.State));
            new[] { Tl1, Tl2, Tl3 }.Should().OnlyContain(id => !rig.Manager.HasWorker(id));
            rig.Opener.For(Tl1).Should().ContainSingle("hotplug during the restart must not reopen a mapped screen");
            return new FanLcdGroupResetResult(true, 1, "acknowledged");
        };
        var r = await rig.Manager.RestartFanLcdAsync();
        listed.Should().Equal(ScreenStates.Stopped, ScreenStates.Stopped, ScreenStates.Stopped);
        (r.CommandAccepted, r.ScreensReconnected).Should().Be((true, true));
        new[] { Tl1, Tl2, Tl3 }.Should().OnlyContain(id => rig.Opener.For(id).Length == 2);
        rig.Opener.For(H2).Should().ContainSingle();
    }

    [Fact]
    public async Task A_stop_screens_pause_before_or_during_the_restart_is_preserved()
    {
        var recovery = new FakeRecovery();
        await using var rig = await RunningAsync(recovery, Mapped(Tl1));
        await rig.Manager.StopScreensAsync();
        var r = await rig.Manager.RestartFanLcdAsync();
        (r.CommandAccepted, r.ScreensReconnected).Should().Be((true, true));
        r.Message.Should().Contain("enumeration only");
        await Task.Delay(400);
        rig.Manager.HasWorker(Tl1).Should().BeTrue("restored, but paused");
        rig.Opener.For(Tl1).Should().ContainSingle("a paused worker never reopens: the restart never unpauses");
        (await rig.ScreenAsync(Tl1)).State.Should().Be(ScreenStates.Stopped);

        await using var rig2 = await RunningAsync(recovery, Mapped(Tl1));
        recovery.Reset = async (send, _) =>
        {
            send();
            await rig2.Manager.StopScreensAsync(CancellationToken.None); // pause is not a mutation-gated change
            return new FanLcdGroupResetResult(true, 1, "acknowledged");
        };
        (await rig2.Manager.RestartFanLcdAsync()).Message.Should().Contain("enumeration only");
        await Task.Delay(400);
        rig2.Opener.For(Tl1).Should().ContainSingle("the pause taken during the restart is honoured by the restore");
        (await rig2.ScreenAsync(Tl1)).State.Should().Be(ScreenStates.Stopped);
        await Wait.UntilAsync(() => rig2.Opener.For(H2) is [{ Disposed: true }], "the pause applied to the untouched H2 too");
    }

    [Fact]
    public async Task The_mutation_gate_is_held_and_a_second_request_is_busy()
    {
        using var inside = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var recovery = new FakeRecovery
        {
            Reset = async (send, ct) =>
            {
                send();
                inside.Release();
                await release.WaitAsync(ct);
                return new FanLcdGroupResetResult(true, 1, "acknowledged");
            },
        };
        await using var rig = await RunningAsync(recovery, Mapped(Tl1));
        var restart = rig.Manager.RestartFanLcdAsync();
        (await inside.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        var busy = await rig.Manager.RestartFanLcdAsync();
        busy.Message.Should().StartWith("Busy:");
        busy.Attempts.Should().Be(0);
        var change = rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl1, BackgroundColor: "#123456", Fps: 15)));
        await Task.Delay(300);
        change.IsCompleted.Should().BeFalse("a scene change waits for the restart's screen gate");
        (await rig.Manager.ListScreensAsync()).Should().HaveCount(5, "reads stay responsive");
        release.Release();
        (await restart).CommandAccepted.Should().BeTrue();
        await change.WaitAsync(TimeSpan.FromSeconds(10));
        recovery.Resets.Should().Be(1);
    }

    [Fact]
    public async Task A_held_screen_gate_refuses_with_busy_before_any_send()
    {
        using var inside = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var recovery = new FakeRecovery
        {
            Reset = async (send, ct) =>
            {
                send();
                inside.Release();
                await release.WaitAsync(ct);
                return new FanLcdGroupResetResult(true, 1, "acknowledged");
            },
        };
        await using var rig = await RunningAsync(recovery, Mapped(Tl1));
        rig.Manager.RestartGateWait = TimeSpan.FromMilliseconds(100);
        using var tx = await rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl1, BackgroundColor: "#123456", Fps: 15)));
        var r = await rig.Manager.RestartFanLcdAsync();
        r.Message.Should().StartWith("Busy:").And.Contain("holds the screen gate");
        (recovery.Resets, recovery.Sends).Should().Be((0, 0));
        rig.Opener.For(Tl1).Should().ContainSingle().Which.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Cooldown_starts_at_the_first_actual_send_only()
    {
        var recovery = new FakeRecovery { Reset = (_, _) => throw new TimeoutException("An RF upload stayed in flight; no RF command was sent.") };
        await using var rig = await RunningAsync(recovery, Mapped(Tl1));
        rig.Manager.RestartCooldown = TimeSpan.FromMilliseconds(700);
        var failed = await rig.Manager.RestartFanLcdAsync();
        failed.Message.Should().StartWith("Precheck:").And.Contain("upload stayed in flight").And.Contain("restored");
        failed.Attempts.Should().Be(0);
        await Wait.UntilAsync(() => rig.Opener.For(Tl1) is [_, { Writes.Count: > 0 }], "restored after the pre-send failure");

        recovery.Reset = (send, _) =>
        {
            send();
            return Task.FromResult(new FanLcdGroupResetResult(false, 3, "receiver still reports command sequence 1"));
        };
        var sent = await rig.Manager.RestartFanLcdAsync();
        sent.Message.Should().StartWith("NotAccepted:", "the pre-send failure consumed no cooldown");
        (sent.CommandAccepted, sent.ScreensReconnected, sent.Attempts).Should().Be((false, true, 3), "acknowledgement and re-enumeration are separate");
        var cooling = await rig.Manager.RestartFanLcdAsync();
        cooling.Message.Should().StartWith("Cooldown:");
        recovery.Resets.Should().Be(2);
        await Task.Delay(800);
        (await rig.Manager.RestartFanLcdAsync()).Attempts.Should().Be(3);
        recovery.Resets.Should().Be(3);
    }

    [Fact]
    public async Task Missing_screens_give_a_partial_result_within_the_reconnect_bound()
    {
        var recovery = new FakeRecovery();
        await using var rig = await RunningAsync(recovery, Mapped(Tl1, Tl2));
        rig.Manager.RestartReconnectWait = TimeSpan.FromMilliseconds(800);
        recovery.Reset = (send, _) =>
        {
            send();
            rig.Enumerator.Set(Devices.Tl1, Devices.Tl3, Devices.H2, Devices.Turzx); // Tl2 never returns
            return Task.FromResult(new FanLcdGroupResetResult(true, 1, "acknowledged"));
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = await rig.Manager.RestartFanLcdAsync();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        (r.CommandAccepted, r.ScreensReconnected).Should().Be((true, false));
        r.Message.Should().StartWith("Partial:").And.Contain(Tl2 + ": ABSENT").And.Contain("NOT all enumerated");
        await Wait.UntilAsync(() => rig.Opener.For(Tl1) is [_, { Writes.Count: > 0 }], "the present mapped screen resumed");
    }

    [Fact]
    public async Task Cancellation_throws_and_still_restores_the_workers()
    {
        using var inside = new SemaphoreSlim(0);
        var recovery = new FakeRecovery
        {
            Reset = async (send, ct) =>
            {
                send();
                inside.Release();
                await Task.Delay(Timeout.Infinite, ct);
                return new FanLcdGroupResetResult(true, 1, "unreachable");
            },
        };
        await using var rig = await RunningAsync(recovery, Mapped(Tl1, Tl2));
        using var cts = new CancellationTokenSource();
        var restart = rig.Manager.RestartFanLcdAsync(cts.Token);
        (await inside.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        rig.Manager.HasWorker(Tl1).Should().BeFalse();
        await cts.CancelAsync();
        await FluentActions.Awaiting(Wait.Outcome(restart)).Should().ThrowAsync<OperationCanceledException>();
        rig.Manager.RestartQuiescing.Should().BeFalse();
        await Wait.UntilAsync(() => new[] { Tl1, Tl2 }.All(id => rig.Opener.For(id) is [_, { Writes.Count: > 0 }]), "workers restored after cancellation");
        recovery.Reset = (send, _) =>
        {
            send();
            return Task.FromResult(new FanLcdGroupResetResult(true, 1, "acknowledged"));
        }; // a cooldown regression then fails instead of hanging
        (await rig.Manager.RestartFanLcdAsync()).Message.Should().StartWith("Cooldown:", "the cancelled restart did send");
        recovery.Resets.Should().Be(1);
    }
}
