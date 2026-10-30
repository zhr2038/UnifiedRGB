using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogWirelessOledTests
{
    private const string Header = "0800070024000100";

    [Fact]
    public async Task Wireless_transport_preserves_all_pixels_and_restores_original_brightness()
    {
        using var io = new WirelessFake { Brightness = 37 };
        using var connection = Connection(io);
        var session = new RogWiredOledSession(connection);
        (await session.SnapshotAsync(Header, default)).Should().Be(37);
        await session.StartOutputAsync(100, default);
        using var stop = new CancellationTokenSource();
        io.Inner.AfterRenewal = n => { if (n == 3) stop.Cancel(); };
        var pixels = Pixels();
        var run = () => session.RunAsync(() => pixels, () => { }, stop.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        await session.RestoreAsync(37, default);
        io.Inner.DataPackets.Should().Be(113);
        io.Model.Complete.Should().BeTrue();
        io.Model.Received.Should().Equal(pixels);
        io.Model.CopyLengths[^1].Should().Be(48);
        io.Model.Remaining.Should().Be(0);
        io.Inner.PendingReads.Should().Be(0);
        io.Inner.Brightness.Should().Be(37);
        io.Requests.Should().OnlyContain(p => p.Length == 64 && p[0] == 2);
        connection.DeviceKey.Should().Be(new string('A', 64));
        connection.ConnectionKind.Should().Be("wireless");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    public async Task Initial_receiver_and_keyboard_version_notes_are_diagnostic_and_live_changes_are_rejected(int changedIndex)
    {
        using var io = new WirelessFake { IdentityMutationIndex = changedIndex };
        using var connection = Connection(io);
        var session = new RogWiredOledSession(connection);
        (await session.SnapshotAsync(Header, default)).Should().Be(io.Brightness);
        io.IdentityMutationIndex = null;
        await ((Func<Task>)(() => session.SnapshotAsync(Header, default))).Should().ThrowAsync<InvalidDataException>();
        io.Inner.Mutations.Should().Be(0);
    }

    [Fact]
    public void Wireless_identity_validates_the_normalized_65_byte_rid0_reply()
    {
        using var io = new WirelessFake();
        using var connection = Connection(io);
        var wired = new byte[65]; wired[1] = 0x12; wired[5] = 8; wired[7] = 7; wired[12] = 0x24; wired[14] = 1;
        ((Action)(() => connection.RequireFirmwareIdentity(wired, Header))).Should().NotThrow();
        ((Action)(() => connection.RequireFirmwareIdentity(new byte[64], Header))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Nonzero_65th_byte_is_rejected_before_any_wireless_write()
    {
        using var io = new WirelessFake();
        using var connection = Connection(io);
        var unsafePacket = RogWiredOledProtocol.Pack(Pixels())[1];
        unsafePacket[64].Should().NotBe(0);
        await ((Func<Task>)(() => connection.ExchangeAsync(unsafePacket, true, default))).Should().ThrowAsync<ArgumentException>();
        io.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_111_chunk_transfer_can_ack_every_counter_without_completing_the_image()
    {
        using var io = new WirelessFake();
        var packets = RogWiredOledProtocol.Pack(Pixels());
        var reply = new byte[64];
        foreach (var wired in packets)
        {
            var packet = new byte[64]; packet[0] = 2; wired.AsSpan(1, 63).CopyTo(packet.AsSpan(1));
            await io.WriteAsync(packet, default);
            (await io.ReadAsync(reply, default)).Should().Be(64);
            reply.AsSpan(1, 4).ToArray().Should().Equal(packet.AsSpan(1, 4).ToArray());
        }
        io.Model.Remaining.Should().Be(0);
        io.Inner.DataPackets.Should().Be(111);
        io.Model.Complete.Should().BeFalse();
        io.Model.Received.Should().HaveCount(6549);
        io.Model.Received.Should().NotEqual(Pixels().Take(6549));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_eof_is_typed_and_never_retried(bool wireless)
    {
        using var io = new ControlledChannel { Eof = true };
        using var connection = ControlledConnection(io, wireless);
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<RogOledConnectionLostException>();
        io.Writes.Should().Be(1); io.PendingReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_deadline_is_distinct_from_caller_cancellation(bool wireless)
    {
        using var io = new ControlledChannel();
        using var connection = ControlledConnection(io, wireless);
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<RogOledTimeoutException>();
        io.Writes.Should().Be(1); io.PendingReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_stop_cannot_retire_an_unanswered_command_and_the_deadline_poison_joins_its_read(bool wireless)
    {
        using var io = new ControlledChannel();
        using var connection = ControlledConnection(io, wireless);
        using var stop = new CancellationTokenSource();
        var command = connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, stop.Token);
        await io.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await ((Func<Task>)(() => command)).Should().ThrowAsync<RogOledTimeoutException>();
        io.Writes.Should().Be(1); io.PendingReads.Should().Be(0);
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Writes.Should().Be(1); io.PendingReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_eof_keeps_the_typed_failure_and_retires_its_reader(bool wireless)
    {
        using var io = new ControlledChannel { Eof = true };
        using var connection = ControlledConnection(io, wireless);
        await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => { }, default)))
            .Should().ThrowAsync<RogOledConnectionLostException>();
        io.PendingReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_content_joins_the_lease_reader_before_the_next_transaction(bool wireless)
    {
        using var io = new ControlledChannel();
        using var connection = ControlledConnection(io, wireless);
        await connection.LeaseAsync(() => io.Writes != 0, () => { }, default);
        io.PendingReads.Should().Be(0);
        io.Eof = true;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<RogOledConnectionLostException>();
        io.MaximumReaders.Should().Be(1); io.PendingReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_caller_cancellation_joins_reader_without_reclassifying_it_as_io_failure(bool wireless)
    {
        using var io = new ControlledChannel();
        using var connection = ControlledConnection(io, wireless);
        using var stop = new CancellationTokenSource();
        var lease = connection.LeaseAsync(() => false, () => { }, stop.Token);
        await io.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await ((Func<Task>)(() => lease)).Should().ThrowAsync<OperationCanceledException>();
        io.PendingReads.Should().Be(0);
    }

    private static IRogOledConnection ControlledConnection(ControlledChannel io, bool wireless) => wireless
        ? new RogWirelessOledConnection(new RogReceiver(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, Header, () => true)
        : new RogWiredOledConnection(io, () => true);

    private sealed class ControlledChannel : IHidReportChannel
    {
        public bool Eof { get; set; }
        public int Writes { get; private set; }
        public int PendingReads { get; private set; }
        public int MaximumReaders { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Writes++; return ValueTask.CompletedTask; }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            PendingReads++; MaximumReaders = Math.Max(MaximumReaders, PendingReads); ReadStarted.TrySetResult();
            try { if (Eof) return 0; await Task.Delay(Timeout.Infinite, ct); return 0; }
            finally { PendingReads--; }
        }
        public void Dispose() { }
    }

    private static byte[] Pixels() => Enumerable.Range(0, 6656).Select(i => (byte)(1 + (i * 73 + i / 251) % 255)).ToArray();

    // Models the actual .24 67 backend: declared down-counter and min(capacity, total-received).
    // It deliberately does not infer completion from the final acknowledgement.
    private sealed class ReceiverModel
    {
        public List<byte> Received { get; } = [];
        public List<int> CopyLengths { get; } = [];
        public int Remaining { get; private set; }
        private int _total;
        public bool Complete => Received.Count == _total && _total == 6656;
        public void Accept(byte[] packet)
        {
            if (packet[1] != 0x67) return;
            if (packet[2] == 0)
            {
                Remaining = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(7));
                _total = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(9)) * BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(11)) / 2;
                Received.Clear(); CopyLengths.Clear();
            }
            else if (packet[2] == 1)
            {
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3)).Should().Be((ushort)(Remaining - 1));
                Remaining--;
                var count = Math.Min(59, _total - Received.Count);
                Received.AddRange(packet.AsSpan(5, count).ToArray()); CopyLengths.Add(count);
            }
        }
    }

    private static RogWirelessOledConnection Connection(WirelessFake io) => new(
        new RogReceiver(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, Header, () => true);

    private sealed class WirelessFake : IHidReportChannel
    {
        public RogWiredOledTests.OledFake Inner { get; } = new();
        public List<byte[]> Requests { get; } = [];
        public ReceiverModel Model { get; } = new();
        public int? IdentityMutationIndex { get; set; }
        public byte Brightness { get => Inner.Brightness; set => Inner.Brightness = value; }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var wireless = report.ToArray(); Requests.Add(wireless);
            if (wireless.Length != 64 || wireless[0] != 2) throw new InvalidDataException("Wrong wireless request envelope.");
            var wired = new byte[65]; wireless.AsSpan(1, 63).CopyTo(wired.AsSpan(1));
            Model.Accept(wireless);
            await Inner.WriteAsync(wired, ct);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var wired = new byte[65];
            var length = await Inner.ReadAsync(wired, ct);
            if (length != 65) throw new InvalidDataException("Wrong fake wired reply.");
            if (wired[1] == 0x12 && wired[2] == 0)
            {
                wired[5] = 8; wired[6] = 0; wired[7] = 7; wired[8] = 0;
                wired[12] = 0x24; wired[13] = 0; wired[14] = 1; wired[15] = 0;
                if (IdentityMutationIndex is { } index) wired[index]++;
            }
            var wireless = new byte[64]; wired.AsSpan(1, 63).CopyTo(wireless.AsSpan(1)); wireless[0] = 2;
            wireless.CopyTo(buffer); return wireless.Length;
        }

        public void Dispose() => Inner.Dispose();
    }
}
