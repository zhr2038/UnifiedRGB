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
        session.Work.QueueSleep.Should().BeGreaterThanOrEqualTo(20);
        session.LastQueueDepth.Should().Be(0);
        session.Work.Matched.Should().Be(channel.Writes.Count);
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
            var replies = command == 121 && PacketsForNextData is { } next ? next(command, timestamp) : [Ack(command, timestamp), []];
            if (command == 121) PacketsForNextData = null;
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
