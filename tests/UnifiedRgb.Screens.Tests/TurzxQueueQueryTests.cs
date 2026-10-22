using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The typed D1 query on the concrete TURZX session: one strict 122 through the existing control path, returning the REPORTED
/// status byte without assigning LastQueueDepth. A short, empty or mismatched status faults the session, so nothing (no resend,
/// stop or later command) is written to that channel again.
/// </summary>
public sealed class TurzxQueueQueryTests
{
    private static readonly byte[] Picture = [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1];

    [Fact]
    public void A_query_is_one_strict_status_control_that_leaves_LastQueueDepth_and_counts_as_control_work()
    {
        var channel = Channel((command, ack) =>
        {
            ack[8] = command switch { TurzxVideoProtocol.DataCommand => 1, 122 => 3, _ => 0 };
            return ack;
        });
        var session = Session(channel);
        session.Begin(Png);
        session.SendFrame(Picture);
        session.SendOverlay(Png);
        var before = session.Work;
        session.QueryQueueStatus().Should().Be(3);
        var after = session.Work;
        channel.Writes.Select(Command).Should().Equal(111, 112, 41, 102, 15, 121, 102, 122);
        (session.LastQueueDepth, session.AccessUnits, session.OverlayWrites).Should().Be((1, 1, 2), "the query never assigns the data path's depth");
        (after.ControlWrite + after.ControlRead).Should().BeGreaterThanOrEqualTo(before.ControlWrite + before.ControlRead);
        (after.DataWrite, after.DataRead, after.OverlayWrite, after.OverlayRead).Should().Be((before.DataWrite, before.DataRead, before.OverlayWrite, before.OverlayRead));
        session.Stop();
        channel.Writes.Select(Command).Skip(8).Should().Equal(123, 111, 114);
    }

    [Fact]
    public void A_query_outside_a_session_writes_nothing()
    {
        var channel = Channel((_, ack) => ack);
        var session = Session(channel);
        FluentActions.Invoking(() => session.QueryQueueStatus()).Should().Throw<InvalidOperationException>();
        channel.Writes.Should().BeEmpty();
        session.Begin(Png);
        session.Stop();
        var writes = channel.Writes.Count;
        FluentActions.Invoking(() => session.QueryQueueStatus()).Should().Throw<InvalidOperationException>();
        channel.Writes.Count.Should().Be(writes);
    }

    public static TheoryData<string> BadStatuses => ["short", "empty", "stamp", "command"];

    [Theory]
    [MemberData(nameof(BadStatuses))]
    public void A_bad_status_reply_faults_the_session_without_resend_or_cleanup(string kind)
    {
        var channel = Channel((command, ack) => command != 122 ? ack : kind switch
        {
            "short" => ack[..8],
            "empty" => [],
            "stamp" => Mutate(ack, 2),
            _ => Mutate(ack, 0),
        });
        var session = Session(channel);
        session.Begin(Png);
        session.SendFrame(Picture);
        session.SendOverlay(Png);
        FluentActions.Invoking(() => session.QueryQueueStatus()).Should().Throw<ScreenTransferException>()
            .Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        channel.Writes.Select(Command).Should().Equal(111, 112, 41, 102, 15, 121, 102, 122);
        session.Stop();
        FluentActions.Invoking(() => session.SendFrame(Picture)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => session.QueryQueueStatus()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => session.SendOverlay(Png)).Should().Throw<IOException>();
        channel.Writes.Count.Should().Be(8, "no resend of the query, no stop controls and no later command on the unverified channel");
        session.LastQueueDepth.Should().Be(0);
    }

    private static byte[] Mutate(byte[] ack, int index)
    {
        ack[index] ^= 0x5a;
        return ack;
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
