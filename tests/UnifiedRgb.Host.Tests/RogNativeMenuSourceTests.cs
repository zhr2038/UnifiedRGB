using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>Only fake devices. The fixture is Astra's actual compiled C producer, not a hand-authored flags2 release.</summary>
[Collection("Host")]
public sealed partial class RogNativeMenuSourceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const ulong Cookie = 0x1122334455667788;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Uninstalled_or_operationally_off_has_zero_extra_queries_enumeration_and_input_opens(bool requested)
    {
        var time = new ManualTimeProvider(); var input = new Input(time, 1); using var io = new Connection(time, 1);
        var source = new RogNativeMenuSource(new(), input, input, time);
        source.RequestEnabled(requested); source.IsPresent.Should().BeFalse();
        await using var actor = source.Attach(io);
        for (var i = 0; i < 3; i++) (await actor.StepAsync(Pixels, default)).Should().BeFalse();
        io.Writes.Should().BeEmpty(); input.Opens.Should().Be(0); input.Enumerations.Should().Be(0);
        ((Action)(() => source.Attach(io))).Should().Throw<InvalidOperationException>();
        using var registration = source.Connect(_ => throw new InvalidOperationException("No event expected."));
        ((Action)(() => source.Connect(_ => { }))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Installed_but_operationally_off_has_zero_native_IO()
    {
        await using var rig = new Rig(1);
        await rig.Actor.StepAsync(Pixels, default);
        rig.Io.Writes.Should().BeEmpty(); rig.Input.Opens.Should().Be(0); rig.Input.Enumerations.Should().Be(0);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task Other_native_modes_never_arm_or_open_input(byte mode)
    {
        await using var rig = new Rig(1); rig.Io.Mode = mode; rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Writes.Should().Equal("12/7D"); rig.Input.Opens.Should().Be(0); rig.Events.Should().BeEmpty();
    }

    public static IEnumerable<object[]> ProducerCases()
    {
        using var stream = typeof(RogNativeMenuSourceTests).Assembly.GetManifestResourceStream("Actual25Producer.json")!;
        using var json = JsonDocument.Parse(stream);
        foreach (var item in json.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("transport").GetByte(), item.GetProperty("rawProfile").GetByte(),
                item.GetProperty("events21").EnumerateArray().Select(e => e.GetString()!).ToArray()];
    }

    [Theory]
    [MemberData(nameof(ProducerCases))]
    public async Task Actual_compiled_producer_native9_neutral_and_up_are_consumed_in_all_twelve_contexts(
        byte transport, byte profile, string[] vectors)
    {
        await using var rig = new Rig(transport); rig.Io.Profile = profile; rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        foreach (var vector in vectors) rig.Input.Send(Convert.FromHexString(vector));
        await rig.Input.WaitReads(vectors.Length);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var presses = rig.Events.OfType<NativeMenuPress>().Select(e => e.Intent).ToArray();
        presses.Should().BeEmpty("the sealed actual-C producer vector is UP/mask1, now intentionally non-actionable");
        rig.Events.OfType<NativeMenuContextBegun>().Should().ContainSingle().Which.Should()
            .Match<NativeMenuContextBegun>(e => e.ProfileGeneration == profile && e.ContextGeneration == 2);
        rig.Actor.ReceivedNotifications.Should().Be(vectors.Length);
        var state = (RogNativeMenuState)typeof(RogNativeMenuSource.Actor).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig.Actor)!;
        state.Sequence.Should().Be(2, "both unchanged compiled-C neutral/up events were consumed in order even though UP is not an action");
        state.Scope!.Value.Profile.Should().Be(profile);
        rig.Io.Arms.Should().Be(1); rig.Io.PendingControl.Should().Be(0);
        rig.Io.Packets.Where(p => p[1] == 0x67 && p[2] == 1).Should().HaveCount(transport == 1 ? 111 : 113);
        rig.Io.Rebuilt.Should().Equal(Pixels());
        rig.Input.Opens.Should().Be(1);
    }

    [Fact]
    public async Task Source_to_C_coordinator_to_existing_case_power_and_feedback_artwork_uses_the_one_Host()
    {
        await using var rig = new Rig(1, recordEvents: false);
        var data = Path.Combine(Path.GetTempPath(), "SolNativeSourceCase-" + Guid.NewGuid().ToString("N"));
        var host = new MenuHostFake(new([new([new("lianli:1", 0), new("lianli:1", 1)], "Rainbow", ["#FF0000", "#00FF00"])]));
        var options = new UnifiedRgbOptions { DataDirectory = data };
        using var display = new KeyboardDisplayService(options, host.Connection.Rgb, host.Connection.Screens!, new NoOled(), new NoGuard());
        await using var menu = new NativeMenuService(options, host.Connection.Rgb, host.Connection.Screens, rig.Source, display, time: rig.Time);
        try
        {
            await menu.StartAsync(default); await menu.SetNativeMenuEnabledAsync(true);
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
            rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1));
            await rig.Input.WaitReads(2); await rig.Actor.StepAsync(Pixels, default);
            await Until(() => menu.Trace.Any(e => e == "artwork #2: Pending"));
            host.Applied.Should().ContainSingle();
            display.ArtworkPixels.ToArray().Should().Contain(b => b != 0);
            rig.Input.Send(Notice(3, 0, 1)); rig.Input.Send(Notice(4, 2, 1));
            await rig.Input.WaitReads(4); await rig.Actor.StepAsync(Pixels, default);
            await Until(() => menu.Trace.Any(e => e == "artwork #4: Active"));
            host.Applied.Should().HaveCount(2);
            host.Calls.Should().NotContain(c => c.StartsWith("UNEXPECTED", StringComparison.Ordinal));
            var feedback = display.ArtworkPixels.ToArray();
            (await rig.Actor.StepAsync(() => feedback, default)).Should().BeTrue();
            rig.Io.Arms.Should().Be(1, "feedback replaces artwork in the same live epoch and reader");
            rig.Io.Rebuilt.Should().Equal(feedback);
            rig.Input.MaximumPending.Should().Be(1); rig.Io.MaximumControl.Should().Be(1);
            await menu.SetNativeMenuEnabledAsync(false);
            (await rig.Actor.StepAsync(() => feedback, default)).Should().BeFalse();
            rig.Input.Pending.Should().Be(0); rig.Input.LiveHandles.Should().Be(0);
            host.Applied.Should().HaveCount(2);
        }
        finally { await menu.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Theory]
    [InlineData("profile")] [InlineData("cookie")] [InlineData("sequence")] [InlineData("epoch")] [InlineData("replay")]
    public async Task Scope_drift_gap_or_replay_disarms_without_a_case_press(string mutation)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        var notice = Notice(1, 0, 3);
        switch (mutation)
        {
            case "profile": notice[5] = 1; break;
            case "cookie": notice[11] ^= 1; break;
            case "sequence": BinaryPrimitives.WriteUInt16LittleEndian(notice.AsSpan(19), 2); break;
            case "epoch": BinaryPrimitives.WriteUInt32LittleEndian(notice.AsSpan(7), 3); break;
            case "replay": rig.Input.Send(notice); break;
        }
        rig.Input.Send(notice); await rig.Input.WaitReads(mutation == "replay" ? 2 : 1);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle();
        rig.Actor.Armed.Should().BeFalse(); rig.Input.Pending.Should().Be(0);
    }

    [Theory]
    [InlineData("mode")] [InlineData("profile")] [InlineData("epoch")] [InlineData("firmware")]
    public async Task Current_capability_changes_end_the_context_before_queued_presses(string mutation)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1)); await rig.Input.WaitReads(2);
        switch (mutation)
        {
            case "mode": rig.Io.Mode = 7; break;
            case "profile": rig.Io.Profile = 1; break;
            case "epoch": rig.Io.Epoch++; break;
            case "firmware": rig.Io.BadBuild = true; break;
        }
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle();
        rig.Input.Pending.Should().Be(0);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("epoch")]
    [InlineData("menu-notification")]
    public async Task Normal_menu_transition_can_reconnect_on_same_owner_and_accept_a_fresh_press(string transition)
    {
        await using var rig = new Rig(1);
        rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        if (transition == "profile") rig.Io.Profile = 1;
        else if (transition == "epoch") rig.Io.Epoch++;
        else
        {
            var outside = new byte[21]; outside[0] = 3; outside[1] = 0x91; outside[4] = 0;
            rig.Input.Send(outside);
            await rig.Input.WaitReads(1);
        }
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue("a normal menu transition must not disable the owner permanently");
        rig.Io.Arms.Should().Be(2);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        foreach (var (sequence, mask, flags) in new[] { (1, 0, 3), (2, 2, 1) })
        {
            var report = Notice((ushort)sequence, (byte)mask, (byte)flags);
            report[5] = rig.Io.Profile;
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(7), rig.Io.Epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(11), Cookie + 1);
            rig.Input.Send(report);
        }
        await rig.Input.WaitReads(transition == "menu-notification" ? 3 : 2);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Action.Should().Be(DesiredAction.Toggle);
    }

    [Fact]
    public async Task Untagged72_and_up_and_mode91_never_create_cached_mode_actions()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        var untagged = new byte[21]; untagged[0] = 3; untagged[1] = 0x72; untagged[2] = 4;
        rig.Input.Send(untagged); rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 1, 1));
        var nativeMode = new byte[21]; nativeMode[0] = 3; nativeMode[1] = 0x91; nativeMode[4] = RogNativeMenuCodec.NativeMode; rig.Input.Send(nativeMode);
        await rig.Input.WaitReads(4); await rig.Actor.StepAsync(Pixels, default);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Actor.Armed.Should().BeTrue();
        var outside = (byte[])nativeMode.Clone(); outside[4] = 0; rig.Input.Send(outside);
        await Until(() => rig.Events.OfType<NativeMenuContextEnded>().Any());
        // A stale physical press queued before the actor observes the mode change cannot act after the End.
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Io.Arms.Should().Be(1);
    }

    [Fact]
    public async Task Read_admission_UTC_is_preserved_when_queue_processing_is_late()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        var at = rig.Time.GetUtcNow(); rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1));
        await rig.Input.WaitReads(2); rig.Time.Advance(TimeSpan.FromSeconds(3));
        await rig.Actor.StepAsync(Pixels, default);
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Timestamp.Should().Be(at);
        // C's separate coordinator rejects >2s-old intents; it never receives a re-stamped fresh time from this adapter.
    }

    [Fact]
    public async Task Backlog_overflow_ends_context_and_reader_without_any_action_or_rearm()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        for (ushort i = 1; i <= 17; i++) rig.Input.Send(Notice(i, 0, i == 1 ? (byte)3 : (byte)1));
        await Until(() => rig.Events.OfType<NativeMenuContextEnded>().Any());
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Io.Arms.Should().Be(1); rig.Input.Pending.Should().Be(0);
    }

    [Theory]
    [InlineData("container")] [InlineData("usage")] [InlineData("rid")] [InlineData("length")]
    public async Task Input_requires_exact_positive_descriptor_and_same_physical_container_before_ARM(string mutation)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        switch (mutation)
        {
            case "container": rig.Input.OtherContainer = true; break;
            case "usage": rig.Input.Collection = rig.Input.Collection with { UsagePage = 1 }; break;
            case "rid": rig.Input.ReportId = 1; break;
            case "length": rig.Input.DescribedLength = 64; break;
        }
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        rig.Io.Arms.Should().Be(0); rig.Events.Should().BeEmpty(); rig.Input.LiveHandles.Should().Be(0);
    }

    [Fact]
    public async Task Ordinary_profile6_and_wrong_build_cannot_ARM_and_never_probe_in_a_loop()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); rig.Io.Profile = 6;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Profile = 0;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Writes.Should().Equal("12/7D"); rig.Input.Opens.Should().Be(0); rig.Source.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task RF_factory_pairing_proof_expires_without_second_control_open_or_fake_refresh()
    {
        await using var rig = new Rig(2); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        rig.Time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Reason.Contains("pairing proof expired", StringComparison.Ordinal));
        rig.Io.Disarms.Should().Be(1); rig.Input.Pending.Should().Be(0); rig.Source.IsPresent.Should().BeFalse();
        var writes = rig.Io.Writes.Count; await rig.Actor.StepAsync(Pixels, default); rig.Io.Writes.Should().HaveCount(writes);
    }

    [Theory]
    [InlineData("arm")] [InlineData("begin")] [InlineData("chunk")]
    public async Task Unknown_control_outcome_never_retries_disarms_drains_or_reopens(string stage)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); rig.Io.FailStage = stage;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>();
        var writes = rig.Io.Writes.Count;
        await rig.Actor.StopAsync(default); await rig.Actor.StopAsync(default);
        rig.Io.Writes.Should().HaveCount(writes); rig.Input.Opens.Should().BeLessThanOrEqualTo(1);
        rig.Input.Pending.Should().Be(0); rig.Actor.Armed.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_actor_keeps_RGB_and_saved_page_ON_without_legacy_fallback_under_installed_policy()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        var session = new RogWiredOledSession(rig.Io); await session.SnapshotAsync(rig.Io.FirmwareHeader, default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach();
        using var stop = new CancellationTokenSource(); var pageEnabled = true;
        var run = session.RunAsync(Pixels, () => { }, stop.Token, owner, display: false,
            displayEnabled: () => pageEnabled && !rig.Source.OwnsDisplayOutput,
            nativeTick: token => rig.Actor.StepAsync(Pixels, token), nativeWake: () => rig.Actor.HasChanges,
            nativeStopped: token => rig.Actor.StopAsync(token));
        try
        {
            await Until(() => rig.Io.Arms == 1);
            await bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);
            rig.Io.Colours.Should().Contain(new Rgb(11, 22, 33));
            pageEnabled.Should().BeTrue();
            rig.Io.Mode = 7;
            await Until(() => !rig.Actor.Armed && rig.Io.NativeRestores > 0);
            pageEnabled.Should().BeTrue("installed ownership suppresses output without saving or flipping the preference");
            session.Frames.Should().Be(0); rig.Io.ImageBegins.Should().Be(1); rig.Io.RejectedImages.Should().Be(0);
            rig.Io.Arms.Should().Be(1); rig.Input.LiveHandles.Should().Be(0);
            rig.Source.RequestEnabled(false);
            await bridge.WriteAsync(new(44, 55, 66), default).WaitAsync(Patience);
            rig.Io.Colours.Should().Contain(new Rgb(44, 55, 66));
            pageEnabled.Should().BeTrue(); session.Frames.Should().Be(0); rig.Io.ImageBegins.Should().Be(1);
            rig.Io.MaximumControl.Should().Be(1); rig.Io.ArmWhileInputReading.Should().BeFalse();
        }
        finally
        {
            stop.Cancel();
            try { await run.WaitAsync(Patience); } catch (OperationCanceledException) { }
            bridge.Detach(owner);
        }
        rig.Input.Pending.Should().Be(0); rig.Io.PendingControl.Should().Be(0);
        rig.Io.PendingLeaseReaders.Should().Be(0);
    }

    [Fact]
    public async Task Changed_physical_container_ends_and_blocks_every_cross_scope_control_restore()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1)); await rig.Input.WaitReads(2);
        rig.Input.OtherContainer = true; var writes = rig.Io.Writes.Count;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        await rig.Actor.StopAsync(default);
        rig.Io.Writes.Should().HaveCount(writes); rig.Actor.RecoveryBlocked.Should().BeTrue();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Disconnected);
        rig.Input.Pending.Should().Be(0); rig.Source.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task Replacement_BEGIN_keeps_the_live_reader_and_never_rearms()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        await Until(() => rig.Input.Pending == 1); rig.Input.IgnoreCancellation = true;
        rig.Input.Send(Notice(1, 0, 3)); await rig.Input.WaitReads(1); await Until(() => rig.Input.Pending == 1);
        var changed = Pixels(); changed[0] ^= 1;
        (await rig.Actor.StepAsync(() => changed, default).WaitAsync(Patience)).Should().BeTrue();
        rig.Io.Arms.Should().Be(1); rig.Io.ArmWhileInputReading.Should().BeFalse(); rig.Input.Opens.Should().Be(1);
        rig.Events.OfType<NativeMenuContextBegun>().Should().ContainSingle();
        rig.Events.OfType<NativeMenuContextEnded>().Should().BeEmpty();
        rig.Input.IgnoreCancellation = false;
        rig.Input.Send(new byte[21]); await Until(() => rig.Input.Pending == 0);
    }

    [Fact]
    public async Task Unjoined_underlying_read_latches_the_owner_and_never_disarms_or_rearms()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); rig.Input.IgnoreCancellation = true;
        await rig.Actor.StepAsync(Pixels, default); await Until(() => rig.Input.Pending == 1);
        var writes = rig.Io.Writes.Count; rig.Io.Mode = 7;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<TimeoutException>();
        rig.Io.Arms.Should().Be(1); rig.Io.Disarms.Should().Be(0); rig.Actor.RecoveryBlocked.Should().BeTrue();
        await ((Func<Task>)(() => rig.Actor.DisposeAsync().AsTask())).Should().ThrowAsync<TimeoutException>();
        ((Action)(() => rig.Source.Attach(rig.Io))).Should().Throw<InvalidOperationException>();
        rig.Io.Writes.Should().HaveCount(writes + 1, "the one capability preceded the failed join; no ARM or cleanup control followed it");
        rig.Input.IgnoreCancellation = false; rig.Input.Send(new byte[21]); await Until(() => rig.Input.Pending == 0);
    }

    [Fact]
    public async Task Delayed_admission_is_rejected_by_C_freshness_without_re_stamping_or_case_writes()
    {
        await using var rig = new Rig(1, recordEvents: false);
        var data = Path.Combine(Path.GetTempPath(), "SolNativeStale-" + Guid.NewGuid().ToString("N"));
        var host = new MenuHostFake(new([new([new("lianli:1", 0), new("lianli:1", 1)], "Rainbow", ["#FF0000"])]));
        await using var menu = new NativeMenuService(new() { DataDirectory = data }, host.Connection.Rgb, host.Connection.Screens, rig.Source, time: rig.Time);
        try
        {
            await menu.StartAsync(default); await menu.SetNativeMenuEnabledAsync(true); await rig.Actor.StepAsync(Pixels, default);
            rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1)); await rig.Input.WaitReads(2);
            rig.Time.Advance(TimeSpan.FromSeconds(3)); await rig.Actor.StepAsync(Pixels, default);
            menu.Trace.Should().Contain(e => e.Contains("press #2 Toggle rejected: Expired", StringComparison.Ordinal));
            host.Calls.Should().BeEmpty();
        }
        finally { await menu.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private static KeyboardNativeMenuOptions Policy(byte transport = 1) => new()
    {
        Enabled = true, SourceControlNotifications = true, NativeBuild = "GYM1:25314D49",
        ConnectionKind = transport == 1 ? "wired" : "wireless",
        FirmwareHeader = transport == 1 ? "001200000025000100" : "0800070025000100",
        DeviceKey = transport == 1 ? RogWiredOledProtocol.DeviceKey(new("fake#mi_01#control", 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0)) : new string('A', 64),
        CandidateSha256 = "0d7cd76d3056671bf6580196ca58041ab2417b824c980de2c9a3034d63341958",
        ContractSha256 = "b6ff1d41add209a95fb3dfebbcee7993aafbb1199b9ffb7a8551788028d4a585",
    };
    private static byte[] Pixels() => Enumerable.Range(0, 6656).Select(i => (byte)(i * 73 + i / 251)).ToArray();
    private static byte[] Notice(ushort sequence, byte mask, byte flags, byte transport = 1)
    {
        var p = new byte[21]; p[0] = transport == 1 ? (byte)3 : (byte)4; p[1] = 0x72; p[2] = mask; p[3] = 0xD5; p[4] = RogNativeMenuCodec.NativeMode; p[6] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), 2); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), Cookie);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(19), sequence); return p;
    }
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Fake pipeline did not reach the expected boundary."); await Task.Delay(5); }
    }

    // Wall/metadata clock can advance without pretending the OS completed hundreds of timers synchronously.
    private sealed class SourceClock : TimeProvider
    {
        private long _wallTicks, _timestampTicks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _wallTicks));
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + Interlocked.Read(ref _timestampTicks);
        public void Advance(TimeSpan value)
        {
            Interlocked.Add(ref _wallTicks, value.Ticks);
            Interlocked.Add(ref _timestampTicks, (long)(value.TotalSeconds * TimestampFrequency));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public SourceClock Time { get; } = new();
        public Input Input { get; }
        public Connection Io { get; }
        public RogNativeMenuSource Source { get; }
        public RogNativeMenuSource.Actor Actor { get; }
        public ConcurrentQueue<NativeMenuSourceEvent> Events { get; } = new();
        private readonly IDisposable? _registration;
        public Rig(byte transport, bool recordEvents = true)
        {
            Input = new(Time, transport); Io = new(Time, transport) { Input = Input };
            ulong nextCookie = Cookie;
            Source = new(Policy(transport), Input, Input, Time, () => nextCookie++);
            if (recordEvents) _registration = Source.Connect(Events.Enqueue);
            Actor = Source.Attach(Io); Input.Admitted = () => Actor.ReceivedNotifications;
        }
        public async ValueTask DisposeAsync() { await Actor.DisposeAsync(); _registration?.Dispose(); Input.Dispose(); Io.Dispose(); }
    }

    private sealed class Input : IHidInputReportDevices, IHidContainerResolver, IDisposable
    {
        private readonly TimeProvider _time;
        private readonly Guid _container = Guid.NewGuid(), _other = Guid.NewGuid();
        private Handle? _handle;
        public Input(TimeProvider time, byte transport)
        {
            _time = time; ReportId = transport == 1 ? (byte)3 : (byte)4;
            Collection = new("fake#mi_03#vendor", 0x0B05, transport == 1 ? (ushort)0x1C24 : (ushort)0x1ACE, 3, 0xFFC0, 1, 21, 0, 0);
        }
        public HidCollectionInfo Collection { get; set; }
        public byte ReportId { get; set; }
        public int DescribedLength { get; set; } = 21;
        public bool OtherContainer { get; set; }
        public bool IgnoreCancellation { get; set; }
        public int Enumerations { get; private set; }
        public int Opens { get; private set; }
        public int LiveHandles { get; private set; }
        public int Pending { get; private set; }
        public int MaximumPending { get; private set; }
        public int Reads { get; private set; }
        public Func<long>? Admitted { get; set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) { Enumerations++; return [Collection]; }
        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection) { Opens++; LiveHandles++; return _handle = new(this); }
        public HidContainerResolution Resolve(HidCollectionInfo collection)
        {
            var method = typeof(HidPhysicalContainer).GetMethod("FromPlatformContainerId", BindingFlags.NonPublic | BindingFlags.Static)!;
            return HidContainerResolution.Resolved((HidPhysicalContainer)method.Invoke(null,
                [OtherContainer && collection.Interface == 3 ? _other : _container, _time.GetTimestamp()])!);
        }
        public void Dispose() => _handle?.Dispose();
        public void Send(byte[] report) => (_handle ?? throw new InvalidOperationException("Input not open.")).Queue.Writer.TryWrite(report).Should().BeTrue();
        public Task WaitReads(int count) => Until(() => (Admitted?.Invoke() ?? Reads) >= count);
        private sealed class Handle(Input owner) : IHidInputReportHandle
        {
            public Channel<byte[]> Queue { get; } = Channel.CreateUnbounded<byte[]>();
            private bool _disposed;
            public HidInputDescriptor Describe() => new(owner.Collection.Vid, owner.Collection.Pid, owner.Collection.UsagePage, owner.Collection.Usage,
                owner.DescribedLength, 0, 0, [owner.ReportId]);
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                owner.MaximumPending = Math.Max(owner.MaximumPending, ++owner.Pending);
                try { var p = await Queue.Reader.ReadAsync(owner.IgnoreCancellation ? CancellationToken.None : ct); p.CopyTo(buffer); owner.Reads++; return p.Length; }
                finally { owner.Pending--; }
            }
            public void Dispose() { if (_disposed) return; _disposed = true; owner.LiveHandles--; }
        }
    }

    private sealed class Connection : IRogOledConnection
    {
        private readonly TimeProvider _time;
        private readonly byte _transport;
        private readonly long _pairedAt;
        private ulong _cookie;
        private ushort _sequence;
        private readonly List<byte> _image = [];
        private readonly RogKeyboardNativeState _bank = RogProtocol.KeyboardColourFrame(new(1, 2, 3), 25, 80);
        public Connection(TimeProvider time, byte transport) { _time = time; _transport = transport; _pairedAt = time.GetTimestamp(); }
        public Input? Input { get; init; }
        public string DeviceKey => _transport == 1 ? RogWiredOledProtocol.DeviceKey(Control) : new string('A', 64);
        public string FirmwareHeader => _transport == 1 ? "001200000025000100" : "0800070025000100";
        public string ConnectionKind => _transport == 1 ? "wired" : "wireless";
        public int PixelPayloadCapacity => _transport == 1 ? 60 : 59;
        private HidCollectionInfo Control => new(_transport == 1 ? "fake#mi_01#control" : "fake#mi_02#control", 0x0B05,
            _transport == 1 ? (ushort)0x1C24 : (ushort)0x1ACE, _transport == 1 ? 1 : 2, 0xFF00, 1, _transport == 1 ? 65 : 64, _transport == 1 ? 65 : 64, 0);
        public RogNativeControlBinding? NativeControlBinding => new([Control], _transport == 2 ? new(0x1C25, 2, 4) : null, _pairedAt);
        // Native9 accepts image BEGIN/chunks only in an armed native mode9 session.
        public bool EnforceNativeImageGate { get; init; } = true;
        public int RejectedImages { get; private set; }
        public int ImageBegins { get; private set; }
        public int ImageCommits { get; private set; }
        public Action? ReplacementChunk { get; set; }
        public Action? FinalChunk { get; set; }
        private bool _frameAwaitingRenewal;
        public byte Mode { get; set; } = RogNativeMenuCodec.NativeMode;
        public byte Profile { get; set; }
        public uint Epoch { get; set; } = 1;
        public bool BadBuild { get; set; }
        public string? FailStage { get; set; }
        public string? SemanticFault { get; set; }
        public List<string> Writes { get; } = [];
        public List<byte[]> Packets { get; } = [];
        public List<Rgb> Colours { get; } = [];
        public byte[] Rebuilt => _image.ToArray();
        public int Arms { get; private set; }
        public int Disarms { get; private set; }
        public int NativeRestores { get; private set; }
        public int PendingControl { get; private set; }
        public int MaximumControl { get; private set; }
        public int PendingLeaseReaders { get; private set; }
        public bool ArmWhileInputReading { get; private set; }
        public bool SameDevice() => true;
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) { }
        public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MaximumControl = Math.Max(MaximumControl, ++PendingControl);
            try
            {
                if (PendingControl != 1 || PendingLeaseReaders != 0) throw new InvalidOperationException("Two actor/control operations overlap.");
                Packets.Add((byte[])request.Clone()); Writes.Add($"{request[1]:X2}/{request[2]:X2}");
                if (EnforceNativeImageGate && request[1] == 0x67 && request[2] is 0 or 1 && (Mode != RogNativeMenuCodec.NativeMode || _cookie == 0))
                {
                    RejectedImages++;
                    throw new InvalidDataException("Native9 rejects image67 outside ARMED native mode9.");
                }
                var b = new byte[65]; request.AsSpan(0, 5).CopyTo(b);
                switch (request[1], request[2])
                {
                    case (0x12, 0x7D): return Task.FromResult(Capability(0x12));
                    case (0x71, 0x7D):
                        ArmWhileInputReading |= Input?.Pending > 0;
                        if (FailStage == "arm") throw new IOException("Injected unknown ARM outcome.");
                        BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(19)).Should().Be(Epoch);
                        request[23].Should().Be(Profile); request[24].Should().Be(_transport);
                        Epoch++; _cookie = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(11)); _sequence = 0;
                        if (request[10] == 1) Arms++; else Disarms++;
                        var changed = Capability(0x71);
                        if (request[10] == 1)
                        {
                            if (SemanticFault == "arm-cookie") BinaryPrimitives.WriteUInt64LittleEndian(changed.AsSpan(19), _cookie + 1);
                            if (SemanticFault == "arm-epoch") BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(15), Epoch - 1);
                            if (SemanticFault == "arm-build") BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(29), 0);
                        }
                        else
                        {
                            if (SemanticFault == "disarm-epoch") BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(15), Epoch - 1);
                            if (SemanticFault == "disarm-status")
                            { changed[10] = 1; changed[14] = 17; BinaryPrimitives.WriteUInt64LittleEndian(changed.AsSpan(19), Cookie); }
                            if (SemanticFault == "disarm-build") BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(29), 0);
                        }
                        return Task.FromResult(changed);
                    case (0x67, 2):
                        if (FailStage == "renew") throw new IOException("Injected unknown renewal outcome.");
                        if (FailStage == "renew-ack") { b[5] = 1; break; }
                        if (_frameAwaitingRenewal && _image.Count == 6656) { ImageCommits++; _frameAwaitingRenewal = false; }
                        break;
                    case (0x67, 0): ImageBegins++; if (FailStage == "begin") throw new IOException("Injected unknown BEGIN outcome."); _image.Clear(); _frameAwaitingRenewal = true; break;
                    case (0x67, 1):
                        if (FailStage == "chunk") throw new IOException("Injected unknown chunk outcome.");
                        _image.AddRange(request.AsSpan(5, Math.Min(PixelPayloadCapacity, 6656 - _image.Count)).ToArray());
                        if (ImageBegins == 2 && ReplacementChunk is { } callback) { ReplacementChunk = null; callback(); }
                        if (_image.Count == 6656 && FinalChunk is { } final) { FinalChunk = null; final(); }
                        break;
                    case (0x12, 0): b[9] = 6; b[11] = 6; break;
                    case (0x21, 0): b[5] = 3; break;
                    case (0x23, 2): b[5] = 1; break;
                    case (0x23, 3): b[5] = 100; break;
                    case (0x27, 0): b[5] = 0; break;
                    case (0x61, 0): NativeRestores++; break;
                    case (0x68, 0): b = (byte[])request.Clone(); break;
                    case (0x25, 0x0E):
                        Convert.FromHexString(_bank.Payloads[RogProtocol.KeyboardPayloadLengths(0).Count - 1 - request[4]]).CopyTo(b, 5); break;
                    case (0x51, 0x2C):
                        Colours.Add(new(request[10], request[11], request[12])); b = (byte[])request.Clone(); break;
                    default: throw new InvalidOperationException("Unexpected fake command.");
                }
                return Task.FromResult(b);
            }
            finally { PendingControl--; }
        }
        private byte[] Capability(byte command)
        {
            var p = new byte[65]; p[1] = command; p[2] = 0x7D; "GYM1"u8.CopyTo(p.AsSpan(5)); p[9] = 2;
            p[10] = _cookie == 0 ? (byte)0 : (byte)1; p[11] = Mode; p[12] = Profile; p[13] = _transport; p[14] = _cookie == 0 ? (byte)48 : (byte)49;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), Epoch); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), _cookie);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(27), _sequence); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), BadBuild ? 0u : 0x25314D49);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208); p[35] = 64; p[36] = (byte)PixelPayloadCapacity; p[37] = 3; return p;
        }
        public async Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct)
        {
            PendingLeaseReaders++;
            try { for (var n = 0; n < 20; n++) { ct.ThrowIfCancellationRequested(); renewed(); await Task.Delay(10, ct); if (changed()) break; } }
            finally { PendingLeaseReaders--; }
        }
        public void Dispose() { }
    }

    private sealed class NoOled : IRogWiredOledFactory
    { public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("No control open in this test."); }
    private sealed class NoGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => throw new InvalidOperationException("No hardware owner start.");
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException();
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException();
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException();
    }
}
