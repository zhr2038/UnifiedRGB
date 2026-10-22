using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// A matching acknowledgement shorter than its 9-byte buffer status leaves the reply suffix unverified: the session must fault, so
/// neither Stop nor any later command writes to that channel (the worker reopens it instead).
/// </summary>
public sealed class TurzxShortStatusTests
{
    private static readonly byte[] Picture = [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1];

    [Fact]
    public void A_short_data_status_faults_the_session_so_stop_sends_no_cleanup()
    {
        var channel = Channel((command, ack) => command == TurzxVideoProtocol.DataCommand ? ack[..8] : ack);
        var session = Session(channel);
        session.Begin(Png);
        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        var writes = channel.Writes.Count;
        session.Stop();
        channel.Writes.Count.Should().Be(writes, "an unverified short status must not be followed by stop controls on the same channel");
        send.Should().Throw<InvalidOperationException>();
        channel.Writes.Count.Should().Be(writes);
    }

    [Fact]
    public void A_short_busy_wait_status_faults_the_session_so_stop_sends_no_cleanup()
    {
        var channel = Channel((command, ack) =>
        {
            if (command == TurzxVideoProtocol.DataCommand) ack[8] = 4; // busy: the sender polls the queue status
            return command == 122 ? ack[..8] : ack;
        });
        var session = Session(channel);
        session.Begin(Png);
        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        channel.Writes.Select(Command).Should().EndWith([TurzxVideoProtocol.DataCommand, 122]);
        var writes = channel.Writes.Count;
        session.Stop();
        channel.Writes.Count.Should().Be(writes, "an unverified short status must not be followed by stop controls on the same channel");
    }

    [Fact]
    public void Valid_replies_keep_the_exact_begin_frame_and_stop_command_sequence()
    {
        var channel = Channel((_, ack) => ack);
        var session = Session(channel);
        session.Begin(Png);
        session.SendFrame(Picture);
        session.SendOverlay(Png);
        session.Stop();
        channel.Writes.Select(Command).Should().Equal(111, 112, 41, 102, 15, 121, 102, 123, 111, 114);
        (session.AccessUnits, session.OverlayWrites, session.LastQueueDepth).Should().Be((1, 2, 0));
    }

    [MediaFoundationFact]
    public async Task The_worker_reopens_after_a_short_data_status_without_cleanup_writes_on_the_unverified_channel()
    {
        using var h = await NativeWorkerHarness.StartAsync(NativeWorkerHarness.NewTrial("short-status"), overlay: "none");
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        h.Reply = (command, stamp, _) => command == TurzxVideoProtocol.DataCommand ? NativeWorkerHarness.Ack(command, stamp)[..8] : null;
        var first = h.Channel;
        var before = first.Writes.Count;
        h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
        await NativeWorkerHarness.SettleAsync(() => first.Disposed, "the worker closes the channel after the short status");
        var after = first.Writes.Skip(before).Select(NativeWorkerHarness.Command).ToArray();
        after.Should().Equal([TurzxVideoProtocol.DataCommand], "only the failing access unit; no 123/111/114 cleanup on an unverified reply suffix");
        h.Aborts.Should().BeGreaterThan(0, "the failed transfer still ends the trial through the existing path");
    }

    private static int Command(byte[] packet) => NativeWorkerHarness.Command(packet);

    private static FakeChannel Channel(Func<byte, byte[], byte[]> reply)
    {
        var channel = new FakeChannel();
        channel.Reply = packet => reply(NativeWorkerHarness.Command(packet), NativeWorkerHarness.Ack(NativeWorkerHarness.Command(packet),
            NativeWorkerHarness.Stamp(packet)));
        return channel;
    }

    private static TurzxVideoSession Session(FakeChannel channel)
    {
        var fixedTime = new DateTime(2026, 10, 22, 12, 0, 0);
        return new(channel, new ScreenTimestamp(ScreenFamily.Turzx, () => fixedTime, () => fixedTime), () => false);
    }
}
