using System.Buffers.Binary;
using System.Collections.Concurrent;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_task_browse_updates_the_same_step_frame_without_rearming_or_new_owner(byte transport)
    {
        var selected = Pixels();
        var second = selected.ToArray(); second[20] ^= 0x0F;
        await using var rig = new TaskBrowseRig(transport, _ => selected = second);
        rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(_ => selected, default);
        rig.Send(1, 0, 3); rig.Send(2, 4); await rig.Input.WaitReads(2);
        await rig.Actor.StepAsync(_ => selected, default);
        rig.Io.Rebuilt.Should().Equal(second);
        rig.Io.ImageBegins.Should().Be(2); rig.Io.Arms.Should().Be(1); rig.Input.Opens.Should().Be(1);
        rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty(); rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_task_browse_fresh_up_down_select_display_only_and_center_still_opens_once(byte transport)
    {
        await using var rig = new TaskBrowseRig(transport);
        var activation = new CodexLaunchTests.FakeActivator();
        using var launch = new CodexLaunchCoordinator(activation, rig.Time);
        rig.Source.SetLaunchCoordinator(launch);
        await rig.Start();
        rig.Send(1, 0, 3); rig.Send(2, 1); rig.Send(3, 0); rig.Send(4, 4);
        rig.Send(5, 0); rig.Send(6, 2); rig.Send(7, 2); rig.Send(8, 0);
        await rig.Drain(8);
        await Until(() => activation.Calls == 1 && !launch.Busy);
        rig.Moves.Should().Equal(-1, 1);
        rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Source.ControlsRequested.Should().BeFalse();
        rig.Io.Colours.Should().BeEmpty(); rig.Io.Arms.Should().Be(1); rig.Input.Opens.Should().Be(1);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_task_browse_held_and_replayed_edges_cannot_repeat_navigation(byte transport)
    {
        await using var rig = new TaskBrowseRig(transport); await rig.Start();
        rig.Send(1, 0, 3); rig.Send(2, 1); rig.Send(3, 1); rig.Send(4, 0); rig.Send(5, 1);
        await rig.Drain(5); rig.Moves.Should().Equal(-1, -1);
        rig.Send(5, 1); await rig.Drain(6);
        rig.Moves.Should().Equal(-1, -1); rig.Actor.Armed.Should().BeFalse();
    }

    [Theory]
    [InlineData("no-neutral")] [InlineData("held-before-neutral")] [InlineData("stale")]
    [InlineData("cookie")] [InlineData("epoch")] [InlineData("profile")] [InlineData("page")]
    [InlineData("gap")] [InlineData("untagged")]
    public async Task Codex_task_browse_rejects_untrusted_or_stale_input(string fault)
    {
        await using var rig = new TaskBrowseRig(1); await rig.Start();
        var count = 0;
        if (fault is not ("no-neutral" or "held-before-neutral")) { rig.Send(1, 0, 3); count++; }
        var report = rig.Report(fault is "no-neutral" or "held-before-neutral" ? (ushort)1 : fault == "gap" ? (ushort)3 : (ushort)2, 1);
        switch (fault)
        {
            case "cookie": report[11] ^= 1; break;
            case "epoch": BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(7), rig.Io.Epoch + 1); break;
            case "profile": report[5]++; break;
            case "page": report[4] = 9; break;
            case "untagged": report[3] = 0; break;
        }
        rig.Input.Send(report); count++;
        if (fault == "held-before-neutral") { rig.Send(2, 1); count++; }
        await rig.Input.WaitReads(count);
        if (fault == "stale") rig.Time.Advance(TimeSpan.FromMilliseconds(751));
        await rig.Actor.StepAsync(Pixels, default);
        rig.Moves.Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Io.Colours.Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_task_browse_disabled_consumer_drops_queued_edges(byte transport)
    {
        await using var rig = new TaskBrowseRig(transport); await rig.Start();
        rig.Send(1, 0, 3); rig.Send(2, 4); await rig.Input.WaitReads(2);
        rig.Source.RequestEnabled(true); rig.Source.RequestCodexEnabled(false);
        await rig.Actor.StepAsync(Pixels, default);
        rig.Moves.Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
    }

    [Fact]
    public async Task Codex_task_browse_callback_failure_does_not_poison_the_keyboard_owner()
    {
        var calls = 0;
        await using var rig = new TaskBrowseRig(1, _ => { calls++; throw new InvalidOperationException("Fake display failure"); });
        await rig.Start(); rig.Send(1, 0, 3); rig.Send(2, 4); await rig.Drain(2);
        calls.Should().Be(1); rig.Actor.Armed.Should().BeTrue();
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Io.Colours.Should().BeEmpty(); rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Codex_task_browse_does_not_replace_mode9_scene_browse_or_case_press(byte transport)
    {
        await using var rig = new TaskBrowseRig(transport); rig.Io.Mode = 9;
        rig.Source.RequestEnabled(true); rig.Source.RequestCodexEnabled(true);
        await rig.Actor.StepAsync(Pixels, default);
        rig.Send(1, 0, 3); rig.Send(2, 1); rig.Send(3, 0); rig.Send(4, 4); rig.Send(5, 0); rig.Send(6, 2);
        await rig.Drain(6);
        rig.Moves.Should().BeEmpty();
        rig.Events.OfType<NativeMenuSceneBrowse>().Select(e => e.Move).Should().Equal(SceneCycleMove.Previous, SceneCycleMove.Next);
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Action.Should().Be(DesiredAction.Toggle);
    }

    private sealed class TaskBrowseRig : IAsyncDisposable
    {
        public SourceClock Time { get; } = new();
        public Input Input { get; }
        public Connection Io { get; }
        public RogNativeMenuSource Source { get; }
        public RogNativeMenuSource.Actor Actor { get; }
        public ConcurrentQueue<int> Moves { get; } = new();
        public ConcurrentQueue<NativeMenuSourceEvent> Events { get; } = new();
        private readonly IDisposable _registration;
        private readonly byte _transport;
        public TaskBrowseRig(byte transport, Action<int>? browse = null)
        {
            _transport = transport; Input = new(Time, transport);
            Io = new(Time, transport) { Input = Input, Mode = 10, DedicatedPage = true };
            ulong nextCookie = Cookie;
            Source = new(Policy(transport), Input, Input, Time, () => nextCookie++, codexTaskBrowse: browse ?? Moves.Enqueue);
            _registration = Source.Connect(Events.Enqueue); Actor = Source.Attach(Io); Input.Admitted = () => Actor.ReceivedNotifications;
        }
        public async Task Start() { Source.RequestCodexEnabled(true); await Actor.StepAsync(Pixels, default); }
        public byte[] Report(ushort sequence, byte mask, byte flags = 1)
        {
            var report = Notice(sequence, mask, flags, _transport); report[4] = Io.Mode; report[5] = Io.Profile;
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(7), Io.Epoch);
            return report;
        }
        public void Send(ushort sequence, byte mask, byte flags = 1) => Input.Send(Report(sequence, mask, flags));
        public async Task Drain(int reads) { await Input.WaitReads(reads); await Actor.StepAsync(Pixels, default); }
        public async ValueTask DisposeAsync() { await Actor.DisposeAsync(); _registration.Dispose(); Input.Dispose(); Io.Dispose(); }
    }
}
