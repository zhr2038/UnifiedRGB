using System.Buffers.Binary;
using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>Real RF owner over fake HID only. Windows-write completion and buffered colour replies are never optical proof.</summary>
public sealed class RogWirelessColourStreamTests
{
    private const string Header = "0800070024000100";
    private static RogKeyColor[] Keys => Enumerable.Range(0, 85)
        .Select(i => new RogKeyColor((byte)i, new((byte)i, (byte)(255 - i), (byte)(i * 3)))).ToArray();

    private static RogWirelessOledConnection Open(Fake io) => new(
        new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, Header, () => true)
    {
        NativeControlBinding = new([], new(0x1C25, 2, 4), 1234),
        KeyReportDelay = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
    };

    [Fact]
    public async Task Actual_RF_dispatch_writes_all_85_independent_colours_without_reading_or_inventing_ACKs()
    {
        using var io = new Fake(); using var actual = Open(io); IRogOledConnection connection = actual;
        var before = connection.PairingEvidence;
        var pauses = new List<(TimeSpan Delay, int Sent)>();
        actual.KeyReportDelay = (delay, _) => { pauses.Add((delay, io.Writes.Count)); return Task.CompletedTask; };
        await connection.WriteLightingFrameAsync(Keys, default);
        pauses.Should().Equal(Enumerable.Range(0, 7).Select(i => (TimeSpan.FromMilliseconds(8), i)));
        io.Writes.Select(Convert.ToHexString).Should().Equal(RogProtocol.KeyboardFrame(2, Keys).Select(Convert.ToHexString));
        io.Writes.Select(p => p[3]).Should().Equal(14, 14, 14, 14, 14, 14, 1);
        Keys.Select(k => k.Color).Distinct().Should().HaveCount(85);
        io.Reads.Should().Be(0); connection.PairingEvidence.Should().Be(before);
        (await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))[5].Should().Be(37);
        io.Reads.Should().Be(1); connection.PairingEvidence.Should().Be(before);
    }

    [Theory]
    [InlineData("basic")] [InlineData("capability")] [InlineData("settings")] [InlineData("arm")]
    public async Task Buffered_seven_ACKs_and_duplicates_cannot_complete_a_strict_control_exchange(string command)
    {
        using var io = new Fake { AutoControlReplies = false }; using var connection = Open(io);
        await connection.WriteLightingFrameAsync(Keys, default);
        var before = connection.PairingEvidence;
        foreach (var p in io.Writes.ToArray()) io.Enqueue(ColourReply(p[3]));
        for (var i = 0; i < 12; i++) io.Enqueue(ColourReply(14));
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OnRead = n => { if (n == 20) drained.TrySetResult(); };
        var request = Request(command);
        var exchange = connection.ExchangeAsync(request, false, default);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        exchange.IsCompleted.Should().BeFalse("RGB replies cannot acknowledge a strict control request");
        connection.PairingEvidence.Should().Be(before, "discarded RGB replies are not keyboard pairing evidence");
        var ownReply = ControlReply(request); io.Enqueue(ownReply);
        var result = await exchange;
        result[1..64].Should().Equal(ownReply[1..]);
        connection.PairingEvidence!.Observations.Should().Be(command == "settings" ? 0 : 1);
        io.PendingReads.Should().Be(0); io.MaximumReaders.Should().Be(1);
    }

    [Theory]
    [InlineData("basic")] [InlineData("capability")] [InlineData("settings")]
    public async Task Missing_strict_control_reply_still_times_out_and_faults_after_colour_ACKs(string command)
    {
        using var io = new Fake { AutoControlReplies = false }; using var connection = Open(io);
        await connection.WriteLightingFrameAsync(Keys, default);
        io.Enqueue(ColourReply(14)); io.Enqueue(ColourReply(1));
        await ((Func<Task>)(() => connection.ExchangeAsync(Request(command), false, default)))
            .Should().ThrowAsync<RogOledTimeoutException>();
        connection.PairingEvidence!.Observations.Should().Be(0);
        connection.PairingEvidence.Invalidated.Should().NotBeNull();
        io.PendingReads.Should().Be(0); await RequirePoisoned(connection, io);
    }

    [Theory]
    [InlineData("reserved")] [InlineData("tail")] [InlineData("zero-count")] [InlineData("large-count")]
    [InlineData("unwritten-count")] [InlineData("unwritten-owner")] [InlineData("rid")] [InlineData("short")]
    [InlineData("refused")] [InlineData("unrelated")]
    public async Task Malformed_refused_or_unowned_colour_replies_remain_terminal(string kind)
    {
        using var io = new Fake(); using var connection = Open(io);
        if (kind != "unwritten-owner") await connection.WriteLightingFrameAsync(Keys, default);
        io.Enqueue(BadReply(kind));
        await ((Func<Task>)(() => connection.ExchangeAsync(Request("settings"), false, default)))
            .Should().ThrowAsync<InvalidDataException>();
        await RequirePoisoned(connection, io);
    }

    [Theory]
    [InlineData(128, true)] [InlineData(129, false)]
    public async Task Colour_drain_has_its_own_finite_limit_and_preserves_the_old_ordinary_budget(int count, bool succeeds)
    {
        using var io = new Fake(); using var connection = Open(io);
        await connection.WriteLightingFrameAsync(Keys, default);
        for (var i = 0; i < count; i++) io.Enqueue(ColourReply(i % 2 == 0 ? (byte)14 : (byte)1));
        for (var i = 0; i < 7; i++) io.Enqueue(Wireless(RogWiredOledProtocol.Renew()));
        var run = () => connection.ExchangeAsync(Request("settings"), false, default);
        if (succeeds)
        {
            (await run()).Should().Equal(Request("settings"));
            io.Reads.Should().Be(136); io.Queued.Should().Be(0);
        }
        else
        {
            await run.Should().ThrowAsync<InvalidDataException>().WithMessage("*stage=buffered-colour-reply*");
            io.Reads.Should().Be(129); await RequirePoisoned(connection, io);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Lease_drains_late_colour_replies_and_joins_its_only_reader_before_next_frame(bool cancel)
    {
        using var io = new Fake(); using var connection = Open(io); using var stop = new CancellationTokenSource();
        await connection.WriteLightingFrameAsync(Keys, default);
        var before = connection.PairingEvidence;
        for (var i = 0; i < 14; i++) io.Enqueue(ColourReply(i % 2 == 0 ? (byte)14 : (byte)1));
        if (cancel) io.OnRead = n => { if (n == 15) stop.Cancel(); };
        var renewed = 0;
        var lease = () => connection.LeaseAsync(() => renewed > 0, () => renewed++, stop.Token);
        if (cancel) await lease.Should().ThrowAsync<OperationCanceledException>(); else await lease();
        io.Reads.Should().Be(15); io.PendingReads.Should().Be(0); io.MaximumReaders.Should().Be(1);
        connection.PairingEvidence.Should().Be(before);
        io.OnRead = null;
        await connection.WriteLightingFrameAsync(Keys, default);
        io.PendingReads.Should().Be(0);
    }

    [Theory]
    [InlineData("refused")] [InlineData("tail")]
    public async Task Lease_colour_refusals_and_malformed_replies_still_fault_and_join(string kind)
    {
        using var io = new Fake(); using var connection = Open(io);
        await connection.WriteLightingFrameAsync(Keys, default); io.Enqueue(BadReply(kind));
        await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => { }, default)))
            .Should().ThrowAsync<InvalidDataException>();
        io.PendingReads.Should().Be(0); await RequirePoisoned(connection, io);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_write_failure_or_deadline_faults_without_retry_or_leftover_reader(bool timeout)
    {
        using var io = new Fake(); using var connection = Open(io);
        io.WriteGate = timeout ? (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)
            : (_, _) => Task.FromException(new IOException("Synthetic write failure."));
        var write = () => connection.WriteLightingFrameAsync(Keys, default);
        if (timeout) await write.Should().ThrowAsync<RogOledTimeoutException>();
        else await write.Should().ThrowAsync<IOException>();
        io.Writes.Should().HaveCount(1); io.PendingWrites.Should().Be(0); io.Reads.Should().Be(0);
        await RequirePoisoned(connection, io);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Cancelling_a_report_pause_sends_no_next_report_and_does_not_poison_the_owner(int pause)
    {
        using var io = new Fake { AutoColourReplies = true }; using var connection = Open(io);
        using var stop = new CancellationTokenSource(); var pauses = 0;
        connection.KeyReportDelay = (_, ct) => { if (++pauses == pause) stop.Cancel(); return Task.Delay(TimeSpan.Zero, ct); };
        await ((Func<Task>)(() => connection.WriteLightingFrameAsync(Keys, stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        io.Writes.Should().HaveCount(pause - 1); io.Reads.Should().Be(0);
        await connection.ExchangeAsync(Request("settings"), false, default);
        connection.PairingEvidence!.Invalidated.Should().BeNull(); io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task Admitted_write_is_joined_on_cancellation_and_concurrent_operations_cannot_take_ownership()
    {
        using var io = new Fake { AutoColourReplies = true }; using var connection = Open(io);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.WriteGate = async (_, ct) => { entered.TrySetResult(); await finish.Task.WaitAsync(ct); };
        var write = connection.WriteLightingFrameAsync(Keys, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        write.IsCompleted.Should().BeFalse();
        await ((Func<Task>)(() => connection.ExchangeAsync(Request("settings"), false, default))).Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => connection.LeaseAsync(() => true, () => { }, default))).Should().ThrowAsync<InvalidOperationException>();
        io.Writes.Should().HaveCount(1); finish.TrySetResult();
        await ((Func<Task>)(() => write)).Should().ThrowAsync<OperationCanceledException>();
        io.PendingWrites.Should().Be(0); io.Reads.Should().Be(0); io.WriteGate = null;
        await connection.ExchangeAsync(Request("settings"), false, default);
        connection.PairingEvidence!.Invalidated.Should().BeNull();
    }

    [Fact]
    public async Task Entire_invalid_frame_is_rejected_before_any_stream_write_and_owner_remains_usable()
    {
        using var io = new Fake(); using var connection = Open(io); var invalid = Keys; invalid[^1] = invalid[0];
        await ((Func<Task>)(() => connection.WriteLightingFrameAsync(invalid, default))).Should().ThrowAsync<ArgumentException>();
        io.Writes.Should().BeEmpty(); await connection.ExchangeAsync(Request("settings"), false, default);
    }

    [Fact]
    public async Task Shared_session_dispatches_the_actual_RF_stream_with_all_RGB_replies_missing()
    {
        using var io = new Fake(); using var connection = Open(io);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(Header, default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        await bridge.WriteKeysAsync(Keys, RogPerKeyLighting.WirelessTransport, default);
        io.Writes.Count(p => p[1] == 0xC0 && p[2] == 0x81).Should().Be(7);
        stop.Cancel(); await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
        session.LightingTouched.Should().BeTrue(); io.PendingReads.Should().Be(0); bridge.Detach(owner);
    }

    [Theory]
    [InlineData("wired")] [InlineData("wireless")]
    public async Task Interface_default_for_wired_and_old_fakes_keeps_strict_colour_reply_validation(string kind)
    {
        using var fake = new StrictDefault(kind); IRogOledConnection connection = fake;
        await connection.WriteLightingFrameAsync(Keys, default); fake.Exchanges.Should().Be(7);
        fake.BadReply = true;
        await ((Func<Task>)(() => connection.WriteLightingFrameAsync(Keys, default))).Should().ThrowAsync<InvalidDataException>();
        fake.Exchanges.Should().Be(8);
    }

    private static async Task RequirePoisoned(RogWirelessOledConnection connection, Fake io)
    {
        var writes = io.Writes.Count; var reads = io.Reads;
        await ((Func<Task>)(() => connection.WriteLightingFrameAsync(Keys, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        await ((Func<Task>)(() => connection.ExchangeAsync(Request("settings"), false, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Writes.Should().HaveCount(writes); io.Reads.Should().Be(reads); io.PendingReads.Should().Be(0);
    }

    private static byte[] Request(string command) => command switch
    {
        "basic" => RogWiredOledProtocol.Query(0x12, 0),
        "capability" => RogNativeMenuCodec.CapabilityRequest(),
        "arm" => RogNativeMenuCodec.ArmRequest(new(0, 9, 0, 2, 32, 123, 0, 0), 456),
        _ => RogWiredOledProtocol.Brightness(20),
    };

    private static byte[] ControlReply(byte[] p)
    {
        var result = (byte[])p.Clone();
        if (p[1] == 0x12 && p[2] == 0)
        { result[5] = 8; result[7] = 7; result[9] = 6; result[11] = 6; result[12] = 0x24; result[14] = 1; }
        if (p[2] == 0x7D)
        {
            Array.Clear(result, 5, 60); "GYM1"u8.CopyTo(result.AsSpan(5)); result[9] = 2; result[11] = 9; result[13] = 2; result[14] = 32;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(15), 123);
            if (p[1] == 0x71) { result[10] = 1; result[14] = 49; BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(19), 456); }
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(33), 208); result[35] = 64; result[36] = 59; result[37] = 3;
        }
        return Wireless(result);
    }

    private static byte[] ColourReply(byte count)
    {
        var p = new byte[64]; p[0] = 2; p[1] = 0xC0; p[2] = 0x82; p[3] = count; p.AsSpan(5, 23).Fill(0xA5); return p;
    }

    private static byte[] BadReply(string kind)
    {
        var p = ColourReply(14);
        switch (kind)
        {
            case "reserved": p[4] = 1; break;
            case "tail": p[28] = 1; break;
            case "zero-count": p[3] = 0; break;
            case "large-count": p[3] = 15; break;
            case "unwritten-count": p[3] = 2; break;
            case "rid": p[0] = 3; break;
            case "short": return p[..63];
            case "refused": p[1] = 0xFF; p[2] = 0xAA; break;
            case "unrelated": return Wireless(RogWiredOledProtocol.Query(0x21, 0));
        }
        return p;
    }

    private static byte[] Wireless(byte[] p)
    { var result = new byte[64]; p.AsSpan(1, 63).CopyTo(result.AsSpan(1)); result[0] = 2; return result; }

    private sealed class Fake : IHidReportChannel
    {
        private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>();
        private readonly RogWiredOledTests.OledFake _inner = new() { Brightness = 37 };
        public List<byte[]> Writes { get; } = [];
        public int Reads { get; private set; }
        public int PendingReads { get; private set; }
        public int PendingWrites { get; private set; }
        public int MaximumReaders { get; private set; }
        public int Queued => _queue.Reader.Count;
        public bool AutoControlReplies { get; set; } = true;
        public bool AutoColourReplies { get; init; }
        public Action<int>? OnRead { get; set; }
        public Func<byte[], CancellationToken, Task>? WriteGate { get; set; }
        public void Enqueue(byte[] p) => _queue.Writer.TryWrite(p).Should().BeTrue();

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var p = report.ToArray(); Writes.Add(p); PendingWrites++;
            try
            {
                p.Length.Should().Be(64); p[0].Should().Be(2);
                if (WriteGate is not null) await WriteGate(p, ct);
                if (p[1] == 0xC0 && p[2] == 0x81)
                { PendingReads.Should().Be(0, "the preceding lease reader must have joined"); if (AutoColourReplies) Enqueue(ColourReply(p[3])); return; }
                if (!AutoControlReplies) return;
                var wired = new byte[65]; p.AsSpan(1).CopyTo(wired.AsSpan(1));
                if (p[2] == 0x7D) { Enqueue(ControlReply(wired)); return; }
                await _inner.WriteAsync(wired, ct);
                if (p[1] == 0x67 && p[2] == 2) return;
                var reply = new byte[65]; (await _inner.ReadAsync(reply, ct)).Should().Be(65);
                Enqueue(p[1] == 0x12 && p[2] == 0 ? ControlReply(wired) : Wireless(reply));
            }
            finally { PendingWrites--; }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++; PendingReads++; MaximumReaders = Math.Max(MaximumReaders, PendingReads);
            try { OnRead?.Invoke(Reads); var reply = await _queue.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
            finally { PendingReads--; }
        }

        public void Dispose() { _inner.Dispose(); _queue.Writer.TryComplete(); }
    }

    private sealed class StrictDefault(string kind) : IRogOledConnection
    {
        public string DeviceKey => "fake";
        public string FirmwareHeader => "fake";
        public string ConnectionKind => kind;
        public int PixelPayloadCapacity => 60;
        public int Exchanges { get; private set; }
        public bool BadReply { get; set; }
        public bool SameDevice() => true;
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) { }
        public Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
        {
            Exchanges++; var reply = new byte[65]; ColourReply(request[3]).CopyTo(reply, 0); reply[0] = 0;
            if (BadReply) reply[28] = 1; return Task.FromResult(reply);
        }
        public void Dispose() { }
    }
}
