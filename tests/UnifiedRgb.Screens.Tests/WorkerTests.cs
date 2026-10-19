using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

/// <summary>Display workers against fake USB: gates, contention, transfer failures, cancellation, hotplug and pause.</summary>
public sealed class WorkerTests
{
    private static readonly string Tl = Devices.Tl1.Id;

    [Fact]
    public async Task An_unsettled_reply_stream_closes_the_old_channel_before_writing_another_frame()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        var configured = 0;
        rig.Opener.Configure = channel =>
        {
            if (Interlocked.Increment(ref configured) == 1)
                channel.FailRead = () => new ScreenTransferException(ScreenTransferError.ReplyBacklog, "reply stream did not settle");
        };
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id, Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens >= 2 && rig.Opener.Channels.First().Disposed && rig.Opener.Last.WriteAttempts > 0,
            "old unsafe channel is replaced", 15_000);
        rig.Opener.Channels.First().WriteAttempts.Should().Be(1, "no second frame may follow an exhausted reply drain");
        (await rig.ScreenAsync(Devices.Turzx.Id)).Dropped.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task Writes_framed_tl_packets_at_the_assignment_rate()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, BackgroundColor: "#FF0000", Overlay: "compact", Fps: 10)));
        rig.Manager.Start();

        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && rig.Opener.Last.Writes.Count >= 3, "three TL frames");
        foreach (var packet in rig.Opener.Last.Writes)
        {
            packet.Should().HaveCount(102400);
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.TlLcd, packet.AsSpan(0, 512));
            header[0].Should().Be(101);
            var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
            len.Should().BeInRange(1, 101888);
            ScreenProtocol.IsJpeg(packet.AsSpan(512)).Should().BeTrue();
            packet.Skip(512 + len).Should().OnlyContain(b => b == 0);
        }

        rig.Opener.Last.Reads.Should().BeGreaterThan(0, "one bounded response read follows each write");
        var s = await rig.ScreenAsync(Tl);
        s.State.Should().Be(ScreenStates.Running);
        s.FramesWritten.Should().BeGreaterThanOrEqualTo(3);
        s.Family.Should().Be("TlLcd");
        s.Width.Should().Be(400);
        s.Name.Should().Be("TL-LCD fan screen a439");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_closed_output_gate_never_opens_the_device(bool enabled, bool familyOutput)
    {
        await using var rig = new Rig(o =>
        {
            o.Enabled = enabled;
            o.TlLcd.Output = familyOutput;
            o.H2.Output = true;
        }, Devices.Tl1);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl)));
        rig.Manager.Start();
        await Task.Delay(400);

        (await rig.ScreenAsync(Tl)).State.Should().Be(ScreenStates.Disabled);
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Disabled_master_gate_starts_no_discovery_loop()
    {
        await using var rig = new Rig(null, Devices.Tl1);
        rig.Manager.Start();
        await Task.Delay(400);

        rig.Enumerator.Calls.Should().Be(0);
        (await rig.Manager.ListScreensAsync()).Should().ContainSingle(s => s.Id == Tl && s.State == ScreenStates.Disabled);
        rig.Enumerator.Calls.Should().Be(1, "listing is a read-only on-demand enumeration");
    }

    [Fact]
    public async Task A_running_vendor_owner_blocks_without_opening_and_release_resumes()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        rig.Contention.TurzxOwner = "TURZX";
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Turzx.Id)).State == ScreenStates.Blocked, "Blocked");
        (await rig.ScreenAsync(Devices.Turzx.Id)).BlockedBy.Should().Be("TURZX");
        rig.Opener.Opens.Should().Be(0);

        rig.Contention.TurzxOwner = null;
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && !rig.Opener.Last.Writes.IsEmpty, "resume after the owner exits", 15_000);
        var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, rig.Opener.Last.Writes.First().AsSpan(0, 512));
        header[0].Should().Be(102);
    }

    [Fact]
    public async Task An_owner_appearing_while_running_closes_the_handle()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && !rig.Opener.Last.Writes.IsEmpty, "running");

        rig.Contention.LianLiOwner = "L-Connect 3";
        await Wait.UntilAsync(() => rig.Opener.Last.Disposed, "handle released to the vendor program");
        (await rig.ScreenAsync(Devices.H2.Id)).State.Should().Be(ScreenStates.Blocked);
    }

    [Fact]
    public async Task Access_denied_at_open_is_blocked_and_retried_with_backoff()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        rig.Opener.FailOpen = n => n <= 2 ? new ScreenTransferException(ScreenTransferError.AccessDenied, "CreateFile failed (5)", 5) : null;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl)));
        rig.Manager.Start();

        await Wait.UntilAsync(() => rig.Opener.Opens >= 3 && rig.Opener.Channels.Count == 1 && !rig.Opener.Last.Writes.IsEmpty, "third open succeeds");
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Running, "Running after the first completed frame");
        rig.Opener.Opens.Should().Be(3, "each failed open waits out its backoff");
    }

    [Fact]
    public async Task Access_denied_reports_the_generic_holder()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        rig.Opener.FailOpen = _ => new ScreenTransferException(ScreenTransferError.AccessDenied, "CreateFile failed (32)", 32);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Blocked, "Blocked");
        (await rig.ScreenAsync(Tl)).BlockedBy.Should().Be(ScreenStates.HeldByAnotherProgram);
    }

    [Fact]
    public async Task Endpoint_mismatch_is_unsupported_and_never_written_or_reopened()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        rig.Opener.FailOpen = _ => new ScreenTransferException(ScreenTransferError.EndpointMismatch, "bulk OUT 0x02 / IN 0x82 differ");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.H2.Id)).State == ScreenStates.Unsupported, "Unsupported");
        await Task.Delay(300);
        rig.Opener.Opens.Should().Be(1);
        (await rig.ScreenAsync(Devices.H2.Id)).LastError.Should().Contain("differ");

        // a pause and a new scene do not hide the verdict or cause another open
        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.H2.Id)).State == ScreenStates.Stopped, "Stopped");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#FFFFFF")));
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.H2.Id)).State == ScreenStates.Unsupported, "Unsupported again");
        (await rig.ScreenAsync(Devices.H2.Id)).LastError.Should().Contain("differ");
        rig.Opener.Opens.Should().Be(1);
    }

    [Fact]
    public async Task A_tolerated_response_read_error_is_reported_while_running()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        rig.Opener.Configure = c => c.FailRead = () => new ScreenTransferException(ScreenTransferError.Timeout, "read timed out", 121);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Running, "Running");
        var s = await rig.ScreenAsync(Tl);
        s.LastError.Should().Be("response read: read timed out");
        s.Dropped.Should().Be(0, "the image write completed");
        rig.Opener.Opens.Should().Be(1);
    }

    [Theory]
    [InlineData(ScreenTransferError.ShortWrite, "Error")]
    [InlineData(ScreenTransferError.Timeout, "Error")]
    [InlineData(ScreenTransferError.Disconnected, "Disconnected")]
    public async Task A_failed_write_drops_closes_and_reopens_after_backoff(ScreenTransferError kind, string state)
    {
        await using var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.BackoffInitialMs = 300;
            o.BackoffMaxMs = 300;
        }, Devices.Tl1);
        var failed = 0;
        rig.Opener.Configure = c => c.FailWrite = n =>
            Interlocked.Increment(ref failed) == 1 ? new ScreenTransferException(kind, kind + " on write") : null;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, Fps: 15)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).Dropped == 1, "one dropped frame");
        var during = await rig.ScreenAsync(Tl);
        during.State.Should().Be(state);
        during.LastError.Should().Contain(kind.ToString());
        rig.Opener.Channels.First().Disposed.Should().BeTrue();

        await Wait.UntilAsync(() => rig.Opener.Opens == 2 && !rig.Opener.Last.Writes.IsEmpty, "reopened");
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Running, "Running after the first completed frame");
        (await rig.ScreenAsync(Tl)).Dropped.Should().Be(1);
    }

    [Fact]
    public async Task Stop_aborts_a_pending_transfer_and_joins()
    {
        var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        rig.Opener.Configure = c => c.BlockWrites = true;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && rig.Opener.Last.WriteAttempts == 1, "a blocked write");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rig.DisposeAsync();

        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
        rig.Opener.Last.Aborts.Should().BeGreaterThan(0);
        rig.Opener.Last.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Unplug_detaches_the_worker_and_replug_reattaches()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1, Devices.Tl2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl), new ScreenAssignmentDto(Devices.Tl2.Id)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 2, "both open");

        rig.Enumerator.Set(Devices.Tl2);
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Disconnected, "Disconnected", 5000);
        await Wait.UntilAsync(() => rig.Opener.Channels.Count(c => c.Disposed) == 1, "the detached worker closed its handle"); // disposed after the lock
        (await rig.ScreenAsync(Devices.Tl2.Id)).State.Should().Be(ScreenStates.Running);

        rig.Enumerator.Set(Devices.Tl1, Devices.Tl2);
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Running, "re-attached", 5000);
        rig.Opener.Opens.Should().Be(3);
    }

    [Fact]
    public async Task Stop_screens_pauses_until_the_next_scene()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var scene = Images.Scene(new ScreenAssignmentDto(Tl));
        await rig.Manager.SetScreenSceneAsync(scene);
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && !rig.Opener.Last.Writes.IsEmpty, "running");

        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Stopped, "Stopped");
        rig.Opener.Last.Disposed.Should().BeTrue();
        var count = rig.Opener.Last.Writes.Count;
        await Task.Delay(300);
        rig.Opener.Last.Writes.Count.Should().Be(count);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene, "a pause does not clear the scene");

        await rig.Manager.SetScreenSceneAsync(scene);
        await Wait.UntilAsync(() => rig.Opener.Opens == 2 && !rig.Opener.Last.Writes.IsEmpty, "resumed");
    }

    [Fact]
    public async Task Disabled_assignment_or_no_assignment_is_idle_and_closed()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1, Devices.Tl2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, Enabled: false)));
        rig.Manager.Start();
        await Task.Delay(400);

        rig.Opener.Opens.Should().Be(0);
        (await rig.ScreenAsync(Tl)).State.Should().Be(ScreenStates.Idle);
        (await rig.ScreenAsync(Devices.Tl2.Id)).State.Should().Be(ScreenStates.Idle);
    }

    [Fact]
    public async Task An_unencodable_frame_is_dropped_without_a_write_and_the_worker_survives()
    {
        await using var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.TurzxLandscapeRotation = 45; // invalid: every TURZX encode fails clearly
        }, Devices.Turzx);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Turzx.Id)).Dropped >= 1, "dropped");
        var s = await rig.ScreenAsync(Devices.Turzx.Id);
        s.State.Should().Be(ScreenStates.Error);
        s.LastError.Should().Contain("TurzxLandscapeRotation");
        rig.Opener.Last.Writes.Should().BeEmpty();
    }
}
