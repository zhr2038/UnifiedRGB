using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class CapabilityInputIntegrationTests
{
    private static readonly string RfFamily = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;

    [Theory]
    [InlineData(0x24, 0x25314D51u)]
    [InlineData(0x25, 0x44434241u)]
    [InlineData(0x26, 0u)]
    public void Compatible_identity_live_protocol2_capability_and_RF_framing_compose_without_build_admission(
        byte version, uint diagnosticTag)
    {
        using var io = new NoIo();
        using var connection = Connection(io);

        ((Action)(() => connection.RequireFirmwareIdentity(Identity(version), RfFamily))).Should().NotThrow();
        var capability = RogNativeMenuCodec.ParseCapability(Capability(diagnosticTag), 0x12, 2);
        RogNativeMenuCodec.SupportsInEpochArtwork(capability).Should().BeTrue();

        connection.ObservedFirmwareHeader.Should().Be($"09000700{version:X2}000100");
        connection.FirmwareHeader.Should().Be(RfFamily);
        var pixels = Pixels();
        var packets = RogWiredOledProtocol.Pack(pixels, connection.PixelPayloadCapacity);
        packets.Should().HaveCount(114);
        var rebuilt = packets.Skip(1).SelectMany((p, i) => p.AsSpan(5, i == 112 ? 48 : 59).ToArray()).ToArray();
        rebuilt.Should().Equal(pixels);
        packets[^1].AsSpan(53).ToArray().Should().OnlyContain(b => b == 0);
        io.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("geometry")]
    [InlineData("identity")]
    [InlineData("profile")]
    [InlineData("context")]
    public void Wrong_live_scope_blocks_only_native_feature_and_does_not_reclassify_ordinary_input(string fault)
    {
        var idle = RogNativeMenuCodec.ParseCapability(Capability(), 0x12, 2);
        var armed = RogNativeMenuCodec.ParseCapability(
            Capability(status: 1, flags: 49, command: 0x71), 0x71, 2);
        var state = new RogNativeMenuState();
        state.Arm(idle, armed, armed.Cookie);

        switch (fault)
        {
            case "protocol":
                var protocol = Capability();
                protocol[9] = 1;
                ((Action)(() => RogNativeMenuCodec.ParseCapability(protocol, 0x12, 2)))
                    .Should().Throw<InvalidDataException>();
                break;
            case "geometry":
                var geometry = Capability();
                geometry[36] = 60;
                ((Action)(() => RogNativeMenuCodec.ParseCapability(geometry, 0x12, 2)))
                    .Should().Throw<InvalidDataException>();
                break;
            case "identity":
                using (var io = new NoIo())
                using (var connection = Connection(io))
                {
                    var identity = Identity(0x25);
                    identity[1] = 0x13;
                    ((Action)(() => connection.RequireFirmwareIdentity(identity, RfFamily)))
                        .Should().Throw<InvalidDataException>();
                    io.Commands.Should().Be(0);
                }
                break;
            case "profile":
                var profile = RogNativeMenuCodec.ParseCapability(
                    Capability(status: 1, flags: 49, command: 0x71, profile: 1), 0x71, 2);
                state.Matches(profile).Should().BeFalse();
                break;
            default:
                var context = RogNativeMenuCodec.ParseCapability(
                    Capability(status: 1, flags: 49, command: 0x71, cookie: 0x8877665544332211), 0x71, 2);
                state.Matches(context).Should().BeFalse();
                break;
        }

        foreach (var transport in new byte[] { 1, 2 })
        {
            var ordinary = new byte[21];
            ordinary[0] = transport == 1 ? (byte)3 : (byte)4;
            ordinary[1] = 0x01;
            ordinary[5] = 0xFF;
            RogNativeMenuCodec.ParseNotice(ordinary, transport, out _).Should().Be(RogNativeNoticeKind.Ignore);

            var ordinary72 = new byte[21];
            ordinary72[0] = ordinary[0];
            ordinary72[1] = 0x72;
            ordinary72[2] = 2;
            RogNativeMenuCodec.ParseNotice(ordinary72, transport, out _).Should().Be(RogNativeNoticeKind.Ignore);
        }
    }

    private static RogWirelessOledConnection Connection(IHidReportChannel io) =>
        new(new RogReceiver(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()),
            io, RfFamily, () => true);

    private static byte[] Identity(byte version)
    {
        var identity = new byte[65];
        identity[1] = 0x12;
        identity[5] = 9;
        identity[7] = 7;
        identity[9] = 6;
        identity[11] = 6;
        identity[12] = version;
        identity[14] = 1;
        return identity;
    }

    private static byte[] Capability(uint diagnosticTag = RogNativeMenuCodec.BuildTag, byte status = 0,
        byte flags = 48, byte command = 0x12, byte profile = 0, ulong cookie = 0x1122334455667788)
    {
        var p = new byte[65];
        p[1] = command;
        p[2] = 0x7D;
        "GYM1"u8.CopyTo(p.AsSpan(5));
        p[9] = 2;
        p[10] = status;
        p[11] = RogNativeMenuCodec.NativeMode;
        p[12] = profile;
        p[13] = 2;
        p[14] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), status == 0 ? 7u : 8u);
        if (status != 0) BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), cookie);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), diagnosticTag);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208);
        p[35] = 64;
        p[36] = 59;
        p[37] = 3;
        return p;
    }

    private static byte[] Pixels() => Enumerable.Range(0, RogWiredOledProtocol.PixelBytes)
        .Select(i => (byte)(1 + (i * 73 + i / 251) % 255)).ToArray();

    private sealed class NoIo : IHidReportChannel
    {
        public int Commands { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            Commands++;
            throw new InvalidOperationException("No I/O in this integration fixture.");
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Commands++;
            throw new InvalidOperationException("No I/O in this integration fixture.");
        }

        public void Dispose() { }
    }
}
