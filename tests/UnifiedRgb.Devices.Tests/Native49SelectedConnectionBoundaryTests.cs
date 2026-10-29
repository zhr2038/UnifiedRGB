using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>The real selected factory and connection; only the platform descriptor/resolver/channel beneath them are fakes.
/// No OS HID stream, PnP enumeration or native operation is invoked. This does not certify the kernel or physical playback.</summary>
public sealed class Native49SelectedConnectionBoundaryTests
{
    [Fact]
    public async Task Selected_factory_connection_passes_proto2_ARM_all111_chunks_and_existing_renewal()
    {
        var devices = new Devices(); var channel = new Channel(); var opens = 0;
        var factory = new RogWiredOledFactory(devices, new Resolver(), _ => { opens++; return channel; });
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), "001200000025000100")!;
        var identity = await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        connection.RequireFirmwareIdentity(identity, "001200000025000100");
        var cap = RogNativeMenuCodec.ParseCapability(await connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, default), 0x12, 1);
        var arm = RogNativeMenuCodec.ArmRequest(cap, 0x1122334455667788);
        var armed = RogNativeMenuCodec.ParseCapability(await connection.ExchangeAsync(arm, false, default), 0x71, 1);
        new RogNativeMenuState().Arm(cap, armed, 0x1122334455667788);
        var pixels = Enumerable.Range(0, 6656).Select(i => (byte)i).ToArray();
        foreach (var packet in RogWiredOledProtocol.Pack(pixels))
            RogWiredOledProtocol.RequireAck(await connection.ExchangeAsync(packet, true, default), packet);
        RogWiredOledProtocol.RequireRenewalReply(await connection.ExchangeAsync(RogWiredOledProtocol.Renew(), true, default));
        opens.Should().Be(1); devices.GenericOpens.Should().Be(0, "the actual factory selected its exclusive-writer seam");
        channel.Writes.Should().HaveCount(116); channel.Writes.Should().OnlyContain(p => p.Length == 65 && p[0] == 0);
        channel.Writes.Single(p => p[1] == 0x71)[9].Should().Be(2);
        channel.Writes.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(111);
        channel.Writes.Last().Should().Equal(RogWiredOledProtocol.Renew());
        channel.Pixels.Should().Equal(pixels); connection.SameDevice().Should().BeTrue();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Selected_connection_rejects_wrong_length_or_RID_before_fake_write(bool wrongRid)
    {
        var devices = new Devices(); var channel = new Channel();
        var factory = new RogWiredOledFactory(devices, new Resolver(), _ => channel);
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), "001200000025000100")!;
        var packet = wrongRid ? RogNativeMenuCodec.CapabilityRequest() : new byte[64]; if (wrongRid) packet[0] = 1;
        await ((Func<Task>)(async () => await connection.ExchangeAsync(packet, false, default))).Should().ThrowAsync<ArgumentException>();
        channel.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Selected_connection_poisoned_ACK_never_retries_or_reaches_later_channel_write()
    {
        var devices = new Devices(); var channel = new Channel { CorruptAck = true };
        var factory = new RogWiredOledFactory(devices, new Resolver(), _ => channel);
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), "001200000025000100")!;
        await ((Func<Task>)(async () => await connection.ExchangeAsync(RogWiredOledProtocol.Renew(), true, default))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(async () => await connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, default))).Should().ThrowAsync<IOException>();
        channel.Writes.Should().ContainSingle();
    }

    private sealed class Devices : IHidReportDevices
    {
        public HidCollectionInfo Collection { get; } = new("inert#mi_01#native49", 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0);
        public int GenericOpens { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => [Collection];
        public IHidReportChannel Open(HidCollectionInfo collection) { GenericOpens++; throw new InvalidOperationException("Wrong generic HID ownership path"); }
    }
    private sealed class Resolver : IHidContainerResolver
    {
        private readonly HidPhysicalContainer container = HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), TimeProvider.System.GetTimestamp());
        public HidContainerResolution Resolve(HidCollectionInfo collection) => HidContainerResolution.Resolved(container);
    }
    private sealed class Channel : IHidReportChannel
    {
        public List<byte[]> Writes { get; } = []; public List<byte> Pixels { get; } = []; public bool CorruptAck { get; init; }
        private byte[] reply = [];
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var p = report.ToArray(); Writes.Add(p); reply = new byte[65]; p.AsSpan(0, 5).CopyTo(reply);
            if (p[1] == 0x12 && p[2] == 0) Convert.FromHexString("001200000025000100060006").CopyTo(reply, 0);
            else if (p[1] is 0x12 or 0x71 && p[2] == 0x7D)
            {
                "GYM1"u8.CopyTo(reply.AsSpan(5)); reply[9] = 2; reply[10] = p[1] == 0x71 ? (byte)1 : (byte)0;
                reply[11] = 9; reply[13] = 1; reply[14] = p[1] == 0x71 ? (byte)49 : (byte)48;
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(15), p[1] == 0x71 ? 2u : 1u);
                if (p[1] == 0x71) p.AsSpan(11, 8).CopyTo(reply.AsSpan(19));
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(29), 0x25314D49); BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(33), 208);
                reply[35] = 64; reply[36] = 60; reply[37] = 3;
            }
            else if (p[1] == 0x67 && p[2] == 1) Pixels.AddRange(p.AsSpan(5, Math.Min(60, 6656 - Pixels.Count)).ToArray());
            if (CorruptAck) reply[5] = 1;
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { ct.ThrowIfCancellationRequested(); reply.CopyTo(buffer); return ValueTask.FromResult(reply.Length); }
        public void Dispose() { }
    }
}
