using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    private static byte[] CodexNotice(Rig rig, ushort sequence, byte mask, byte flags = 1, byte transport = 1)
    {
        var notice = Notice(sequence, mask, flags, transport); notice[4] = 10;
        BinaryPrimitives.WriteUInt32LittleEndian(notice.AsSpan(7), rig.Io.Epoch);
        return notice;
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_fresh_center_opens_computer_only_with_Guangyu_off_and_up_down_are_noops(byte transport)
    {
        await using var rig = new Rig(transport); var fake = new CodexLaunchTests.FakeActivator(); Func<Task>? queued = null;
        using var coordinator = new CodexLaunchCoordinator(fake, rig.Time, schedule: work => { queued = work; return Task.CompletedTask; });
        rig.Source.SetLaunchCoordinator(coordinator); rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default); rig.Source.ControlsRequested.Should().BeFalse();
        rig.Input.Send(CodexNotice(rig, 1, 0, 3, transport));
        rig.Input.Send(CodexNotice(rig, 2, 1, 1, transport));
        rig.Input.Send(CodexNotice(rig, 3, 0, 1, transport));
        rig.Input.Send(CodexNotice(rig, 4, 4, 1, transport));
        rig.Input.Send(CodexNotice(rig, 5, 0, 1, transport));
        await rig.Input.WaitReads(5); await rig.Actor.StepAsync(Pixels, default);
        queued.Should().BeNull(); fake.Calls.Should().Be(0);
        rig.Input.Send(CodexNotice(rig, 6, 2, 1, transport)); await rig.Input.WaitReads(6); await rig.Actor.StepAsync(Pixels, default);
        queued.Should().NotBeNull(); fake.Calls.Should().Be(0); await queued!(); fake.Calls.Should().Be(1);
        rig.Input.Send(CodexNotice(rig, 7, 2, 1, transport)); await rig.Input.WaitReads(7); await rig.Actor.StepAsync(Pixels, default);
        fake.Calls.Should().Be(1); coordinator.Busy.Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        rig.Io.Colours.Should().BeEmpty(); rig.Io.Arms.Should().Be(1); rig.Input.Opens.Should().Be(1);
    }

    [Theory]
    [InlineData("neutral-only")] [InlineData("no-neutral")] [InlineData("cookie")]
    [InlineData("epoch")] [InlineData("profile")] [InlineData("page")] [InlineData("untagged")]
    [InlineData("stale")] [InlineData("replay")] [InlineData("gap")] [InlineData("held")]
    public async Task Codex_invalid_input_cannot_schedule_launch_or_case_scene_actions(string fault)
    {
        await using var rig = new Rig(1); var fake = new CodexLaunchTests.FakeActivator(); var schedules = 0;
        using var coordinator = new CodexLaunchCoordinator(fake, rig.Time, schedule: _ => { schedules++; return Task.CompletedTask; });
        rig.Source.SetLaunchCoordinator(coordinator); rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default);
        var count = 0;
        if (fault != "no-neutral") { rig.Input.Send(CodexNotice(rig, 1, 0, 3)); count++; }
        if (fault == "held") { rig.Input.Send(CodexNotice(rig, 2, 1)); count++; }
        if (fault != "neutral-only")
        {
            var press = CodexNotice(rig, fault == "held" ? (ushort)3 : (ushort)2, 2);
            switch (fault)
            {
                case "cookie": press[11] ^= 1; break;
                case "epoch": press[7] ^= 1; break;
                case "profile": press[5] = 1; break;
                case "page": press[4] = 9; break;
                case "untagged": press[3] = 0; break;
                case "replay": BinaryPrimitives.WriteUInt16LittleEndian(press.AsSpan(19), 1); break;
                case "gap": BinaryPrimitives.WriteUInt16LittleEndian(press.AsSpan(19), 4); break;
            }
            rig.Input.Send(press); count++;
        }
        await rig.Input.WaitReads(count);
        if (fault == "stale") rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Actor.StepAsync(Pixels, default);
        schedules.Should().Be(0); fake.Calls.Should().Be(0);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("exit")] [InlineData("disable")] [InlineData("stop")]
    [InlineData("gap")] [InlineData("replay")] [InlineData("malformed")] [InlineData("cookie")] [InlineData("profile")]
    public async Task Codex_source_revokes_queued_launch_before_worker_start(string reason)
    {
        await using var rig = new Rig(1); var fake = new CodexLaunchTests.FakeActivator(); Func<Task>? queued = null;
        using var coordinator = new CodexLaunchCoordinator(fake, rig.Time, schedule: work => { queued = work; return Task.CompletedTask; });
        rig.Source.SetLaunchCoordinator(coordinator); rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default);
        rig.Input.Send(CodexNotice(rig, 1, 0, 3)); rig.Input.Send(CodexNotice(rig, 2, 2));
        await rig.Input.WaitReads(2); await rig.Actor.StepAsync(Pixels, default); queued.Should().NotBeNull();
        switch (reason)
        {
            case "exit": rig.Io.Mode = 9; rig.Time.Advance(TimeSpan.FromMilliseconds(101)); await rig.Actor.StepAsync(Pixels, default); break;
            case "disable": rig.Source.RequestCodexEnabled(false); break;
            case "stop": await rig.Actor.StopAsync(default); break;
            default:
                var invalid = CodexNotice(rig, reason == "gap" ? (ushort)4 : reason == "replay" ? (ushort)2 : (ushort)3, 0);
                if (reason == "malformed") invalid[6] = 0x80;
                if (reason == "cookie") invalid[11] ^= 1;
                if (reason == "profile") invalid[5] = 1;
                rig.Input.Send(invalid); await rig.Input.WaitReads(3); await rig.Actor.StepAsync(Pixels, default); break;
        }
        await queued!(); fake.Calls.Should().Be(0); coordinator.Status.Should().Be("Cancelled");
    }

    [Theory]
    [InlineData((byte)1, false)] [InlineData((byte)2, false)]
    [InlineData((byte)1, true)] [InlineData((byte)2, true)]
    public async Task Codex_accepted_center_survives_strict_quick_release_and_held_reports_without_duplicate_launch(byte transport, bool held)
    {
        await using var rig = new Rig(transport); var fake = new CodexLaunchTests.FakeActivator(); Func<Task>? queued = null; var schedules = 0;
        using var coordinator = new CodexLaunchCoordinator(fake, rig.Time, schedule: work => { schedules++; queued = work; return Task.CompletedTask; });
        rig.Source.SetLaunchCoordinator(coordinator); rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default);
        rig.Input.Send(CodexNotice(rig, 1, 0, 3, transport)); rig.Input.Send(CodexNotice(rig, 2, 2, 1, transport));
        rig.Input.Send(CodexNotice(rig, 3, held ? (byte)2 : (byte)0, 1, transport));
        rig.Input.Send(CodexNotice(rig, 4, 0, 1, transport));
        rig.Input.Send(CodexNotice(rig, 5, 2, 1, transport)); // another genuine press cannot replace the occupied slot
        await rig.Input.WaitReads(5); await rig.Actor.StepAsync(Pixels, default);
        schedules.Should().Be(1); fake.Calls.Should().Be(0); queued.Should().NotBeNull();
        await queued!(); fake.Calls.Should().Be(1); coordinator.Status.Should().Be("Requested");
        rig.Input.Send(CodexNotice(rig, 6, 2, 1, transport)); rig.Input.Send(CodexNotice(rig, 7, 0, 1, transport));
        rig.Input.Send(CodexNotice(rig, 8, 2, 1, transport)); // within the existing rate bound
        await rig.Input.WaitReads(8); await rig.Actor.StepAsync(Pixels, default);
        schedules.Should().Be(1); fake.Calls.Should().Be(1);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        rig.Source.ControlsRequested.Should().BeFalse(); rig.Io.Colours.Should().BeEmpty(); rig.Io.Arms.Should().Be(1);
    }

    [Fact]
    public async Task Codex_entry_hold_observes_stock_CAP_without_artwork_and_recovers_without_restarting()
    {
        await using var rig = new Rig(1); var hold = new CodexEntryHold(rig.Time);
        rig.Actor.PageObserved = hold.Observe; rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        byte[]? Select(RogNativeCapability _) => hold.Holding ? null : Pixels();
        await rig.Actor.StepAsync(Select, default); rig.Io.ImageBegins.Should().Be(0); hold.Holding.Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromMilliseconds(1200)); await rig.Actor.StepAsync(Select, default);
        rig.Io.ImageBegins.Should().Be(1); hold.Holding.Should().BeFalse();
        await rig.Actor.RetireAfterRecoverableFaultAsync(default);
        await rig.Actor.DisposeAsync();
        using var replacementIo = new Connection(rig.Time, 1) { Input = rig.Input, DedicatedPage = true, Mode = 10 };
        await using var replacement = rig.Source.Attach(replacementIo); replacement.PageObserved = hold.Observe;
        await replacement.StepAsync(Select, default); hold.Holding.Should().BeFalse();
        replacementIo.Mode = 3; rig.Time.Advance(TimeSpan.FromMilliseconds(101));
        await replacement.StepAsync(_ => throw new InvalidOperationException("Stock pages must not select artwork"), default);
        hold.Holding.Should().BeFalse();
        replacementIo.Mode = 10; rig.Time.Advance(TimeSpan.FromMilliseconds(101));
        await replacement.StepAsync(Select, default); hold.Holding.Should().BeTrue();
    }

    [Fact]
    public async Task Codex_refinement_preserves_mode9_center_and_up_down_scene_controls()
    {
        await using var rig = new Rig(1); var fake = new CodexLaunchTests.FakeActivator();
        using var coordinator = new CodexLaunchCoordinator(fake, rig.Time);
        rig.Source.SetLaunchCoordinator(coordinator); rig.Source.RequestEnabled(true); rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default);
        for (ushort sequence = 1; sequence <= 6; sequence++)
            rig.Input.Send(Notice(sequence, sequence % 2 == 1 ? (byte)0 : sequence == 2 ? (byte)1 : sequence == 4 ? (byte)4 : (byte)2, sequence == 1 ? (byte)3 : (byte)1));
        await rig.Input.WaitReads(6); await rig.Actor.StepAsync(Pixels, default);
        rig.Events.OfType<NativeMenuSceneBrowse>().Select(e => e.Move).Should().Equal(SceneCycleMove.Previous, SceneCycleMove.Next);
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Action.Should().Be(DesiredAction.Toggle);
        fake.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("observation")] [InlineData("arm")] [InlineData("chunk")]
    public async Task Dedicated_page_preserves_readonly_recovery_and_terminal_mutation_guards(string role)
    {
        await using var rig = new Rig(1);
        rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        if (role == "observation")
        {
            await rig.Actor.StepAsync(Pixels, default);
            rig.Time.Advance(TimeSpan.FromMilliseconds(101));
            rig.Io.ExchangeFailure = p => p[1] == 0x12 && p[2] == 0x7D
                ? new RogOledUnexpectedReplyException("fake unmatched reply", new InvalidDataException()) : null;
            await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<RogReadOnlyObservationUnexpectedReplyException>();
            rig.Actor.RecoveryBlocked.Should().BeFalse();
            var writes = rig.Io.Writes.Count;
            await rig.Actor.RetireAfterRecoverableFaultAsync(default);
            rig.Io.Writes.Should().HaveCount(writes); rig.Input.LiveHandles.Should().Be(0);
        }
        else
        {
            rig.Io.FailStage = role;
            await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>();
            rig.Actor.RecoveryBlocked.Should().BeTrue();
            var writes = rig.Io.Writes.Count; await rig.Actor.StopAsync(default);
            rig.Io.Writes.Should().HaveCount(writes); rig.Input.LiveHandles.Should().Be(0);
        }
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_only_consumer_never_arms9_but_arms_actual10_and_deduplicates_without_controls(byte transport)
    {
        await using var rig = new Rig(transport);
        rig.Io.DedicatedPage = true;
        rig.Source.RequestCodexEnabled(true);
        var selected = new List<byte>();
        byte[]? Select(RogNativeCapability capability) { selected.Add(capability.Mode); return Pixels(); }
        (await rig.Actor.StepAsync(Select, default)).Should().BeFalse();
        rig.Io.Arms.Should().Be(0); selected.Should().BeEmpty(); rig.Input.Opens.Should().Be(0);
        rig.Io.Mode = 10; rig.Time.Advance(TimeSpan.FromMilliseconds(101));
        (await rig.Actor.StepAsync(Select, default)).Should().BeTrue();
        selected.Should().OnlyContain(mode => mode == 10);
        rig.Actor.ControlArmed.Should().BeFalse(); rig.Actor.DedicatedPageActive.Should().BeTrue();
        rig.Source.IsPresent.Should().BeFalse();
        rig.Io.Packets.Single(p => p[1] == 0x71)[25].Should().Be(10);
        rig.Io.Rebuilt.Should().Equal(Pixels());
        rig.Io.Packets.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(transport == 1 ? 111 : 113);
        for (ushort sequence = 1; sequence <= 6; sequence++)
        {
            var mask = sequence % 2 == 1 ? (byte)0 : sequence == 2 ? (byte)1 : sequence == 4 ? (byte)2 : (byte)4;
            var notice = Notice(sequence, mask, sequence == 1 ? (byte)3 : (byte)1, transport);
            notice[4] = 10; BinaryPrimitives.WriteUInt32LittleEndian(notice.AsSpan(7), rig.Io.Epoch);
            rig.Input.Send(notice);
        }
        await rig.Input.WaitReads(6);
        (await rig.Actor.StepAsync(Select, default)).Should().BeTrue();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        rig.Io.Arms.Should().Be(1);
        rig.Io.Packets.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(transport == 1 ? 111 : 113,
            "unchanged quota bytes do not upload a second frame on the same owner");
        rig.Input.Opens.Should().Be(1);
        rig.Source.RequestEnabled(false); // Guangyu off must not remove the independent consumer
        rig.Source.Requested.Should().BeTrue();
        await rig.Actor.StepAsync(Select, default);
        (await rig.Actor.StepAsync(Select, default)).Should().BeTrue();
        rig.Source.RequestCodexEnabled(false);
        (await rig.Actor.StepAsync(Select, default)).Should().BeFalse();
        rig.Input.LiveHandles.Should().Be(0); rig.Io.MaximumControl.Should().Be(1);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Page_switch_invalidates_queued9_press_and_old_cookie_then_rearms_with_fresh_actual_scope(byte transport)
    {
        await using var rig = new Rig(transport);
        rig.Io.DedicatedPage = true; rig.Source.RequestEnabled(true); rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default);
        rig.Input.Send(Notice(1, 0, 3, transport)); rig.Input.Send(Notice(2, 2, 1, transport));
        await rig.Input.WaitReads(2);
        rig.Io.Mode = 10;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Input.Send(Notice(3, 2, 1, transport)); // retired mode9 cookie/epoch cannot act in mode10
        await rig.Input.WaitReads(3); await rig.Actor.StepAsync(Pixels, default);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        rig.Io.Mode = 9; rig.Time.Advance(TimeSpan.FromMilliseconds(101)); await rig.Actor.StepAsync(Pixels, default);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Actor.ControlArmed.Should().BeTrue();
        rig.Io.Packets.Where(p => p[1] == 0x71 && p[10] == 1).Select(p => p[25]).Should().Equal(9, 10, 9);
        rig.Io.Packets.Where(p => p[1] == 0x71 && p[10] == 1).Select(p => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(11)))
            .Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData((byte)10)] [InlineData((byte)11)] [InlineData((byte)255)]
    public async Task Old_descriptor_and_unknown_pages_never_select_callback_or_ARM(byte mode)
    {
        await using var rig = new Rig(1); rig.Source.RequestCodexEnabled(true); rig.Io.Mode = mode;
        (await rig.Actor.StepAsync(_ => throw new InvalidOperationException("No selection before admission"), default)).Should().BeFalse();
        rig.Io.Arms.Should().Be(0); rig.Input.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Provider_preference_is_independent_and_status_reads_do_not_change_source_intent()
    {
        await using var rig = new Rig(1); var data = Path.Combine(Path.GetTempPath(), "CodexPage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new KeyboardCodexUsageProvider(data, requestPage: rig.Source.RequestCodexEnabled);
            var revision = rig.Source.RequestRevision;
            _ = provider.Status; rig.Source.RequestRevision.Should().Be(revision);
            provider.SetEnabled(true, default); rig.Source.Requested.Should().BeTrue(); rig.Source.ControlsRequested.Should().BeFalse();
            rig.Source.RequestRevision.Should().BeGreaterThan(revision);
            rig.Source.RequestEnabled(false); rig.Source.Requested.Should().BeTrue();
            provider.SetEnabled(false, default); rig.Source.Requested.Should().BeFalse();
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }
}
