using System.Buffers.Binary;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

public sealed class TurzxVideoReplyTests
{
    private static readonly byte[] Picture = [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1];

    [Fact]
    public void Data_requires_eight_own_full_conservative_pairs_and_controls_never_inherit_the_gate()
    {
        var channel = new Channel(); var session = Session(channel);
        session.Begin(Png);
        for (var i = 0; i < 8; i++) session.SendFrame(Picture);
        channel.Writes.Where(w => w.Command == 121).Should().OnlyContain(w => !w.Fast);
        session.SendOverlay(Png);
        session.SendFrame(Picture); session.SendFrame(Picture);
        var data = channel.Writes.Where(w => w.Command == 121).ToArray();
        data.Skip(8).Should().OnlyContain(w => w.Fast && w.Exact);
        channel.Writes.Where(w => w.Command != 121).Should().OnlyContain(w => !w.Fast && !w.Exact);
        session.Work.Correlated.Should().Be(2);
        session.Work.Reads.Should().Be(3 * (channel.Writes.Count - 2) + 2 * 2);
        session.Work.Timeouts.Should().Be(channel.Writes.Count - 2);
        channel.Writes.Select(w => w.Stamp).Should().OnlyHaveUniqueItems("same-millisecond native commands need distinct reply tokens");
        channel.Packets.Where(p => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, p)[0] == 121)
            .Should().OnlyContain(p => p.Skip(512).SequenceEqual(Picture), "transport optimization cannot alter the encoded picture");
    }

    [Fact]
    public void An_extra_reply_resets_training_and_reopening_requires_a_fresh_eight_data_pairs()
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        for (var i = 0; i < 7; i++) session.SendFrame(Picture);
        channel.PacketsForNextData = (c, t) => [Ack(c, t), Ack(c, t), []];
        session.SendFrame(Picture);
        for (var i = 0; i < 8; i++) session.SendFrame(Picture);
        channel.Writes.Should().OnlyContain(w => !w.Fast);
        session.SendFrame(Picture);
        channel.Writes.Last().Fast.Should().BeTrue();
        session.Stop();
        var reopened = new Channel(); var next = Session(reopened); next.Begin(Png); next.SendFrame(Picture);
        reopened.Writes.Should().OnlyContain(w => !w.Fast);
    }

    [Fact]
    public void Beginning_again_does_not_reuse_a_previous_native_streams_negotiation()
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        for (var i = 0; i < 9; i++) session.SendFrame(Picture);
        channel.Writes.Last().Fast.Should().BeTrue();
        session.Stop(); session.Begin(Png); session.SendFrame(Picture);
        channel.Writes.Last().Fast.Should().BeFalse();
    }

    [Fact]
    public void Queue_status_122_negotiates_its_own_eight_pair_gate_independently_of_data_and_faults_on_bad_fast_reply()
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        for (var i = 0; i < 10; i++) session.SendFrame(Picture);
        channel.Writes.Where(w => w.Command == 121).Skip(8).Should().OnlyContain(w => w.Fast && w.Exact);

        var timeoutsBefore122 = session.Work.Timeouts;
        for (var i = 0; i < 8; i++) session.QueryQueueStatus().Should().Be(0);
        channel.Writes.Where(w => w.Command == 122).Should().HaveCount(8).And.OnlyContain(w => !w.Fast && !w.Exact);
        session.Work.Timeouts.Should().Be(timeoutsBefore122 + 8);

        var timeoutsAfterNegotiation = session.Work.Timeouts;
        var correlatedBeforeFastStatus = session.Work.Correlated;
        session.QueryQueueStatus().Should().Be(0);
        session.QueryQueueStatus().Should().Be(0);
        channel.PacketsForNextData = (c, t) => [Ack(c, t, 4), []];
        channel.QueueStatusDepth = () => 2;
        session.SendFrame(Picture);
        var statusWrites = channel.Writes.Where(w => w.Command == 122).ToArray();
        statusWrites.Should().HaveCount(11);
        statusWrites.Take(8).Should().OnlyContain(w => !w.Fast && !w.Exact);
        statusWrites.Skip(8).Should().OnlyContain(w => w.Fast && w.Exact);
        session.Work.Timeouts.Should().Be(timeoutsAfterNegotiation, "fast 122 reads consume [512B ACK, ZLP] without waiting for drain timeout");
        session.Work.Correlated.Should().Be(correlatedBeforeFastStatus + 4);

        session.SendOverlay(Png);
        channel.Writes.Last(w => w.Command == 102).Fast.Should().BeFalse();

        session.Stop();
        channel.Writes.TakeLast(3).Should().OnlyContain(w => w.Command != 121 && w.Command != 122 && !w.Fast && !w.Exact);
        channel.QueueStatusDepth = null;
        session.Begin(Png);
        session.QueryQueueStatus().Should().Be(0);
        channel.Writes.Last().Fast.Should().BeFalse("reopening or beginning again resets the 122 gate too");

        for (var i = 0; i < 7; i++) session.QueryQueueStatus();
        channel.PacketsForNextStatus = (c, t) => [Ack(c, t, 2)]; // missing ZLP on fast 122
        var badFast122 = () => session.QueryQueueStatus();
        badFast122.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        var writesBeforeStop = channel.Writes.Count;
        session.Stop();
        channel.Writes.Count.Should().Be(writesBeforeStop, "a bad fast 122 reply faults the channel with zero follow-up writes");
    }

    [Theory]
    [InlineData(0)] // short ACK
    [InlineData(1)] // stale timestamp
    [InlineData(2)] // missing ZLP
    [InlineData(3)] // duplicate ACK before ZLP
    [InlineData(4)] // stale ZLP before ACK
    public void A_bad_fast_pair_faults_the_channel_without_stop_or_content_resends(int shape)
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        for (var i = 0; i < 8; i++) session.SendFrame(Picture);
        channel.PacketsForNextData = (c, t) => shape switch
        {
            0 => [Ack(c, t)[..9], []], 1 => [Ack(c, t - 1), []], 2 => [Ack(c, t)],
            3 => [Ack(c, t), Ack(c, t), []], _ => [[], Ack(c, t), []],
        };
        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        var writes = channel.Writes.Count;
        session.Stop();
        send.Should().Throw<InvalidOperationException>();
        channel.Writes.Count.Should().Be(writes, "an uncertain reply suffix requires reopening before another write");
    }

    [Fact]
    public void A_late_suffix_cannot_acknowledge_the_next_unique_request()
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        for (var i = 0; i < 8; i++) session.SendFrame(Picture);
        channel.PacketsForNextData = (c, t) => [Ack(c, t), [], Ack(c, t), []];
        session.SendFrame(Picture);
        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        channel.Writes.TakeLast(2).Select(w => w.Stamp).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Explicit_fallback_stays_conservative_and_queue_waits_are_measured_separately()
    {
        var channel = new Channel(); var session = Session(channel, allowFast: false); session.Begin(Png);
        for (var i = 0; i < 10; i++) session.SendFrame(Picture);
        channel.PacketsForNextData = (c, t) => [Ack(c, t, 4), []];
        session.SendFrame(Picture);
        channel.Writes.Should().OnlyContain(w => !w.Fast);
        channel.Writes.Count(w => w.Command == 122).Should().Be(1);
        session.Work.QueueSleep.Should().BeGreaterThan(0);
        session.LastQueueDepth.Should().Be(0);
        session.Work.Matched.Should().Be(channel.Writes.Count);
    }

    [Fact]
    public void Backpressure_polls_at_5ms_up_to_40_iterations_before_ReplyBacklog()
    {
        var channel = new Channel();
        var session = Session(channel, allowFast: false);
        session.Begin(Png);
        channel.PacketsForNextData = (c, t) => [Ack(c, t, 4), []];
        channel.QueueStatusDepth = () => 3; // stays above low-water mark (<= 2) for all 40 polls

        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ReplyBacklog);
        channel.Writes.Count(w => w.Command == 122).Should().Be(TurzxVideoSession.BackpressureMaxPolls);
        session.Work.QueueSleep.Should().BeGreaterThanOrEqualTo(50); // 40 x 5 ms bounded wait
    }

    [Fact]
    public void Backpressure_recovers_in_one_5ms_poll_when_queue_drains()
    {
        var channel = new Channel();
        var session = Session(channel, allowFast: false);
        session.Begin(Png);
        var polls = 0;
        channel.PacketsForNextData = (c, t) => [Ack(c, t, 4), []];
        channel.QueueStatusDepth = () => Interlocked.Increment(ref polls) == 1 ? (byte)2 : (byte)4;

        session.SendFrame(Picture);
        polls.Should().Be(1);
        channel.Writes.Count(w => w.Command == 122).Should().Be(1);
        session.LastQueueDepth.Should().Be(2);
    }

    [Fact]
    public void Backpressure_aborts_immediately_when_waitForCommand_signals_during_5ms_poll()
    {
        var channel = new Channel();
        var waitObserved = new List<int>();
        var fixedTime = new DateTime(2026, 10, 22, 12, 0, 0);
        var session = new TurzxVideoSession(channel, new ScreenTimestamp(ScreenFamily.Turzx, () => fixedTime, () => fixedTime),
            () => false, allowCorrelatedReplies: false, waitForCommand: ms => { waitObserved.Add(ms); return true; });
        session.Begin(Png);
        channel.PacketsForNextData = (c, t) => [Ack(c, t, 4), []];

        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.Aborted);
        waitObserved.Should().Equal(TurzxVideoSession.BackpressurePollDelayMs);
        channel.Writes.Count(w => w.Command == 122).Should().Be(0);
    }

    [Fact]
    public void A_pipe_abort_is_not_retried_on_the_same_channel()
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        for (var i = 0; i < 8; i++) session.SendFrame(Picture);
        channel.AbortNextRead = true;
        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.Aborted);
        var writes = channel.Writes.Count; session.Stop(); channel.Writes.Count.Should().Be(writes);
    }

    [Fact]
    public void An_unclassified_IO_failure_reaches_the_workers_reopen_path()
    {
        var channel = new Channel(); var session = Session(channel); session.Begin(Png);
        channel.IoFailureNextRead = true;
        var send = () => session.SendFrame(Picture);
        send.Should().Throw<ScreenTransferException>().Which.InnerException.Should().BeOfType<IOException>();
        var writes = channel.Writes.Count; session.Stop(); channel.Writes.Count.Should().Be(writes);
    }

    [Fact]
    public void Midnight_and_backwards_wall_clock_do_not_reuse_tokens_or_change_the_picture()
    {
        var now = new DateTime(2026, 10, 22, 23, 59, 59, 999);
        var channel = new Channel();
        var session = new TurzxVideoSession(channel, new ScreenTimestamp(ScreenFamily.Turzx, () => now, () => now), () => false);
        session.Begin(Png); session.SendFrame(Picture);
        now = now.Date.AddDays(1); session.SendFrame(Picture);
        now = now.AddHours(-1); session.SendFrame(Picture);
        var stamps = channel.Writes.Select(w => w.Stamp).ToArray();
        stamps.Should().OnlyHaveUniqueItems();
        stamps.Zip(stamps.Skip(1)).Should().OnlyContain(p => unchecked((int)(p.Second - p.First)) > 0);
    }

    private static TurzxVideoSession Session(Channel channel, bool allowFast = true)
    {
        var fixedTime = new DateTime(2026, 10, 22, 12, 0, 0);
        return new(channel, new ScreenTimestamp(ScreenFamily.Turzx, () => fixedTime, () => fixedTime), () => false, allowFast);
    }

    private static byte[] Ack(byte command, uint timestamp, byte depth = 0)
    {
        var bytes = new byte[512]; bytes[0] = command; bytes[1] = 200; bytes[8] = depth;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), timestamp); return bytes;
    }

    private sealed record WriteFact(byte Command, uint Stamp, bool Fast, bool Exact);
    private sealed class Channel : IScreenUsbChannel, IScreenUsbDiagnostics
    {
        private readonly Queue<byte[]> _packets = new();
        private byte _command; private uint _stamp;
        public List<WriteFact> Writes { get; } = [];
        public List<byte[]> Packets { get; } = [];
        public Func<byte, uint, byte[][]>? PacketsForNextData { get; set; }
        public Func<byte, uint, byte[][]>? PacketsForNextStatus { get; set; }
        public Func<byte>? QueueStatusDepth { get; set; }
        public bool AbortNextRead { get; set; }
        public bool IoFailureNextRead { get; set; }
        public byte OutEndpoint => 1;
        public byte InEndpoint => 0x81;
        public int? DeviceSpeedCode => 3;
        public ScreenReadCounts LastRead { get; private set; }
        public int ReplyQuietMs { private get; set; } = 10;
        public bool? CorrelatedReplies { private get; set; }
        public bool ExactReplyPair { private get; set; }
        public void ExpectResponse(byte command, uint timestamp) { _command = command; _stamp = timestamp; }
        public void Write(ReadOnlySpan<byte> bytes, int timeoutMs)
        {
            var packet = bytes.ToArray(); Packets.Add(packet);
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, bytes[..512]);
            var command = header[0]; var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            Writes.Add(new(command, timestamp, CorrelatedReplies == true, ExactReplyPair));
            var replies = command == 121 && PacketsForNextData is { } next
                ? next(command, timestamp)
                : command == 122 && PacketsForNextStatus is { } nextStatus
                ? nextStatus(command, timestamp)
                : [Ack(command, timestamp, command == 122 && QueueStatusDepth is { } depth ? depth() : (byte)0), []];
            if (command == 121) PacketsForNextData = null;
            if (command == 122) PacketsForNextStatus = null;
            foreach (var reply in replies) _packets.Enqueue(reply);
        }
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            LastRead = new(0, 0, 0, Correlated: CorrelatedReplies == true);
            return CorrelatedReplies == true && ExactReplyPair ? ScreenReplyFlow.ReadExactPair(ReadRaw, buffer, timeoutMs)
                : ScreenReplyFlow.ReadAndDrain(ReadRaw, buffer, timeoutMs, ReplyQuietMs);
        }
        private ScreenPipeRead ReadRaw(Span<byte> buffer, int timeoutMs)
        {
            LastRead = LastRead with { Reads = LastRead.Reads + 1 };
            if (IoFailureNextRead) { IoFailureNextRead = false; throw new IOException("fake unclassified I/O failure"); }
            if (AbortNextRead) { AbortNextRead = false; throw new ScreenTransferException(ScreenTransferError.Aborted, "fake abort"); }
            if (!_packets.TryDequeue(out var packet)) { LastRead = LastRead with { Timeouts = LastRead.Timeouts + 1 }; return new(0, true); }
            packet.CopyTo(buffer); var match = ScreenReplyFlow.MatchesImageReply(packet, _command, _stamp);
            LastRead = LastRead with { ZeroLength = LastRead.ZeroLength + (packet.Length == 0 ? 1 : 0), Matched = LastRead.Matched + (match ? 1 : 0) };
            return new(packet.Length, false, match);
        }
        public void Abort() { }
        public void Dispose() { }
    }
}
