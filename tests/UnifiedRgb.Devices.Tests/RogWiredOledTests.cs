using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogWiredOledTests
{
    [Fact]
    public void Packet_layout_retains_all_independent_pixels_and_exact_chunk_order()
    {
        var pixels = Enumerable.Range(0, 6656).Select(i => (byte)i).ToArray();
        var packets = RogWiredOledProtocol.Pack(pixels);
        packets.Should().HaveCount(112);
        packets[0].AsSpan(7, 6).ToArray().Should().Equal(111, 0, 208, 0, 64, 0);
        var rebuilt = new List<byte>();
        for (var i = 0; i < 111; i++)
        {
            packets[i + 1][3].Should().Be((byte)(110 - i));
            rebuilt.AddRange(packets[i + 1].Skip(5).Take(Math.Min(60, 6656 - i * 60)));
        }
        rebuilt.Should().Equal(pixels);
        packets[^1].AsSpan(61, 4).ToArray().Should().OnlyContain(b => b == 0);
    }

    [Theory]
    [InlineData(59, 113, 48)]
    [InlineData(60, 111, 56)]
    public void Explicit_verified_capacity_retains_every_byte_and_the_exact_down_counter(int capacity, int chunks, int finalBytes)
    {
        var pixels = Enumerable.Range(0, 6656).Select(i => (byte)(1 + (i * 73 + i / 251) % 255)).ToArray();
        var packets = RogWiredOledProtocol.Pack(pixels, capacity);
        packets.Should().HaveCount(chunks + 1);
        packets[0].AsSpan(7, 6).ToArray().Should().Equal((byte)chunks, 0, 208, 0, 64, 0);
        var rebuilt = new List<byte>();
        for (var i = 0; i < chunks; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packets[i + 1].AsSpan(3)).Should().Be((ushort)(chunks - 1 - i));
            rebuilt.AddRange(packets[i + 1].AsSpan(5, Math.Min(capacity, pixels.Length - rebuilt.Count)).ToArray());
        }
        rebuilt.Should().Equal(pixels);
        packets[^1].AsSpan(5 + finalBytes).ToArray().Should().OnlyContain(b => b == 0);
        if (capacity == 59) packets.Should().OnlyContain(packet => packet[64] == 0);
        else packets.Select(Convert.ToHexString).Should().Equal(RogWiredOledProtocol.Pack(pixels).Select(Convert.ToHexString));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(58)] [InlineData(61)] [InlineData(65535)]
    public void Unverified_payload_capacity_is_rejected(int capacity) =>
        ((Action)(() => RogWiredOledProtocol.Pack(new byte[6656], capacity))).Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(0)] [InlineData(6655)] [InlineData(6657)]
    public void Reject_invalid_frame_before_transport(int bytes) =>
        ((Action)(() => RogWiredOledProtocol.Pack(new byte[bytes]))).Should().Throw<ArgumentException>();

    [Fact]
    public void No_raw_storage_or_unknown_selector_command_surface()
    {
        ((Action)(() => RogWiredOledProtocol.Query(0x71, 2))).Should().Throw<ArgumentException>();
        ((Action)(() => RogWiredOledProtocol.Preset(4))).Should().Throw<NotSupportedException>();
        ((Action)(() => RogWiredOledProtocol.Brightness(101))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)] [InlineData(25)] [InlineData(100)]
    public async Task Permanent_lifecycle_joins_reads_then_restores_fresh_original_brightness(byte original)
    {
        using var io = new OledFake { Brightness = original };
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        (await session.SnapshotAsync("001200000024000100", default)).Should().Be(original);
        await session.StartOutputAsync(100, default);
        using var stop = new CancellationTokenSource();
        io.AfterRenewal = n => { if (n == 3) stop.Cancel(); };
        var run = () => session.RunAsync(() => new byte[6656], () => { }, stop.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        io.PendingReads.Should().Be(0);
        await session.RestoreAsync(original, default);
        io.Brightness.Should().Be(original);
        session.Frames.Should().Be(1);
        io.DataPackets.Should().Be(111);
        var sent = io.Writes; await Task.Delay(30); io.Writes.Should().Be(sent);
    }

    [Fact]
    public async Task First_firmware_label_is_diagnostic_but_a_change_within_the_owner_is_rejected()
    {
        using var io = new OledFake { FirmwareByte = 0x25 };
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        (await session.SnapshotAsync("001200000024000100", default)).Should().Be(io.Brightness);
        io.FirmwareByte = 0x26;
        await ((Func<Task>)(() => session.SnapshotAsync("001200000024000100", default))).Should().ThrowAsync<InvalidDataException>();
        io.Mutations.Should().Be(0); session.Touched.Should().BeFalse();
    }

    [Fact]
    public async Task Refused_upload_poison_blocks_same_connection_recovery_and_requires_an_explicit_fresh_owner()
    {
        using var io = new OledFake { RefuseBegin = true };
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.StartOutputAsync(100, default);
        await ((Func<Task>)(() => session.RunAsync(() => new byte[6656], () => { }, default))).Should().ThrowAsync<InvalidDataException>();
        io.BeginPackets.Should().Be(1); io.DataPackets.Should().Be(0);
        var writes = io.Writes;
        await ((Func<Task>)(() => session.RestoreAsync(0, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Writes.Should().Be(writes);
        using var freshIo = new OledFake { Brightness = io.Brightness, ProfileToken = io.ProfileToken, FirmwareByte = io.FirmwareByte };
        using var freshConnection = new RogWiredOledConnection(freshIo, () => true);
        var fresh = new RogWiredOledSession(freshConnection);
        await fresh.SnapshotAsync("001200000024000100", default); await fresh.RestoreAsync(0, default);
        freshIo.Brightness.Should().Be(0); io.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task Recovery_never_targets_replacement_physical_keyboard()
    {
        using var io = new OledFake(); using var connection = new RogWiredOledConnection(io, () => false);
        var session = new RogWiredOledSession(connection);
        await ((Func<Task>)(() => session.RestoreAsync(0, default))).Should().ThrowAsync<InvalidOperationException>();
        io.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_rejects_a_changed_active_profile_before_setting_or_lighting_writes(bool lighting)
    {
        using var io = new OledFake();
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default);
        await session.SnapshotLightingAsync(default);
        io.ProfileToken = 5;
        var writes = io.NativeWrites;
        var mutations = io.Mutations;
        Func<Task> restore = lighting
            ? () => session.RestoreLightingAsync(session.OriginalLighting!, session.OriginalStaticBank!, default)
            : () => session.RestoreAsync(0, default);
        await restore.Should().ThrowAsync<InvalidDataException>();
        io.NativeWrites.Should().Be(writes);
        io.Mutations.Should().Be(mutations);
    }

    [Fact]
    public async Task Lighting_snapshot_rejects_a_profile_switch_since_identity_was_read()
    {
        using var io = new OledFake();
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default);
        io.ProfileToken = 5;
        await ((Func<Task>)(() => session.SnapshotLightingAsync(default))).Should().ThrowAsync<InvalidDataException>();
        io.NativeWrites.Should().Be(0);
        io.Mutations.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Unknown_profile_is_rejected_during_identity_preflight(byte profile)
    {
        using var io = new OledFake { ProfileToken = profile };
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await ((Func<Task>)(() => session.SnapshotAsync("001200000024000100", default))).Should().ThrowAsync<InvalidDataException>();
        io.Mutations.Should().Be(0);
        io.NativeWrites.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_profile_prevents_output_setup_and_a_new_frame(bool frame)
    {
        using var io = new OledFake();
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default);
        io.ProfileToken = 5;
        Func<Task> output = frame
            ? () => session.RunAsync(() => new byte[6656], () => { }, default)
            : () => session.StartOutputAsync(100, default);
        await output.Should().ThrowAsync<InvalidDataException>();
        io.Mutations.Should().Be(0);
        io.NativeWrites.Should().Be(0);
        io.BeginPackets.Should().Be(0);
    }

    [Fact]
    public async Task Delayed_optional_renewal_response_is_not_a_new_command_ack()
    {
        using var io = new OledFake { QueueOptionalBeforeAck = true };
        using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default);
        await session.StartOutputAsync(100, default);
        io.Brightness.Should().Be(100);
        await session.RestoreAsync(0, default);
    }

    public sealed class OledFake : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateBounded<byte[]>(8);
        public byte Brightness { get; set; }
        public byte FirmwareByte { get; set; } = 0x24;
        public byte ProfileToken { get; set; } = 6;
        public byte SoftwareControl { get; set; }
        public bool IgnoreSoftwareControlSetter { get; set; }
        public bool RefuseBegin { get; set; }
        public bool QueueOptionalBeforeAck { get; set; }
        public Action<int>? AfterRenewal { get; set; }
        public int Writes { get; private set; }
        public int Mutations { get; private set; }
        public int DataPackets { get; private set; }
        public int BeginPackets { get; private set; }
        public int PendingReads { get; private set; }
        private int _renewals;
        public byte ActiveEffect { get; private set; }
        public int NativeWrites { get; private set; }
        public List<byte> Opcodes { get; } = [];
        public List<(byte Command, byte Key)> Operations { get; } = [];
        private readonly Dictionary<byte, string[]> _banks = [];
        public RogKeyboardNativeState Native => new(ActiveEffect, _banks[ActiveEffect]);
        public OledFake() => SetBanks(RogProtocol.KeyboardColourFrame(new(10, 20, 30), 25, 80), RogProtocol.KeyboardColourFrame(new(10, 20, 30), 25, 80));
        public void SetBanks(RogKeyboardNativeState active, RogKeyboardNativeState bank)
        { _banks[0] = bank.Payloads.ToArray(); _banks[active.EffectType] = active.Payloads.ToArray(); ActiveEffect = active.EffectType; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var p = report.ToArray(); Writes++; Opcodes.Add(p[1]); Operations.Add((p[1], p[2]));
            var b = new byte[65]; p.AsSpan(0, 5).CopyTo(b);
            switch (p[1], p[2])
            {
                case (0x12, 0): b[5] = FirmwareByte; b[7] = 1; b[9] = 6; b[10] = ActiveEffect; b[11] = ProfileToken; break;
                case (0x25, 0x0E):
                    Convert.FromHexString(_banks[p[3]][RogProtocol.KeyboardPayloadLengths(p[3]).Count - 1 - p[4]]).CopyTo(b, 5); break;
                case (0x51, 0x2C):
                    NativeWrites++;
                    var layout = RogProtocol.KeyboardPayloadLengths(p[3]); var index = layout.Count - 1 - p[4];
                    _banks[p[3]][index] = Convert.ToHexString(p.AsSpan(5, layout[index]));
                    if (p[4] == 0) ActiveEffect = p[3]; b = p; break;
                case (0x21, 0): b[5] = 3; break;
                case (0x23, 2): b[5] = 1; break;
                case (0x23, 3): b[5] = Brightness; break;
                case (0x27, 0): b[5] = SoftwareControl; break;
                case (0x74, 0):
                    if (!IgnoreSoftwareControlSetter && SoftwareControl != p[5]) { SoftwareControl = p[5]; Mutations++; }
                    b[5] = p[5]; break;
                case (0x61, 0): Mutations++; break;
                case (0x68, 0): Mutations++; Brightness = p[5]; b = p; break;
                case (0x67, 0): BeginPackets++; Mutations++; if (RefuseBegin) { b[1] = 0xFF; b[2] = 0xAA; } break;
                case (0x67, 1): DataPackets++; Mutations++; break;
                case (0x67, 2): AfterRenewal?.Invoke(++_renewals); return ValueTask.CompletedTask;
                default: throw new InvalidOperationException("Unexpected OLED command in fake.");
            }
            if (QueueOptionalBeforeAck) _replies.Writer.TryWrite(RogWiredOledProtocol.Renew()).Should().BeTrue();
            _replies.Writer.TryWrite(b).Should().BeTrue(); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            PendingReads++;
            try { var b = await _replies.Reader.ReadAsync(ct); b.CopyTo(buffer); return b.Length; }
            finally { PendingReads--; }
        }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
