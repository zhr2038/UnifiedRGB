using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.Tests;

/// <summary>The owner's slv3 inventory (PnP, 2026-10-17) plus a wired receiver and a foreign VID reusing the sender PID.</summary>
internal sealed class FakeUsb : IUsbEnumerator, IHotplugSource
{
    public const string Guid = "4a1e2b3c-0000-4000-8000-00000000c0de";

    public List<UsbDeviceInfo> Present { get; } =
    [
        UsbDeviceInfo.FromPath(@"\\?\usb#vid_0416&pid_8040#6&aa&0&1#{" + Guid + "}")!, // "SLV3TX_V1.6": RF sender, the only device opened
        UsbDeviceInfo.FromPath(@"\\?\usb#vid_0416&pid_8041#6&ab&0&2#{" + Guid + "}")!, // "SLV3RX_V1.6": receiver, device list only
        UsbDeviceInfo.FromPath(@"\\?\usb#vid_1a86&pid_8091#6&bb&0&3#{" + Guid + "}")!, // hub: never opened
        UsbDeviceInfo.FromPath(@"\\?\usb#vid_1cbe&pid_0006#6&bc&0&4#{" + Guid + "}")!, // "TL-LCD Wireless-1.3": never opened
        UsbDeviceInfo.FromPath(@"\\?\usb#vid_43a8&pid_0101#6&cc&0&5#{" + Guid + "}")!, // wired LED receiver: other command set
        UsbDeviceInfo.FromPath(@"\\?\usb#vid_1234&pid_8040#6&dd&0&6#{" + Guid + "}")!, // other vendor
    ];

    public event Action<DeviceInterfaceChange, string>? Changed;

    public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass) => Present;

    public void Raise(DeviceInterfaceChange c, string path) => Changed?.Invoke(c, path);

    public void Dispose()
    {
    }
}

/// <summary>Test stand-in for tinyuz: passes the frame through so the framing around it can be checked. Not a real encoder.</summary>
internal sealed class IdentityEncoder : IRgbAnimationEncoder
{
    public byte[] Encode(ReadOnlySpan<byte> rgbData) => rgbData.ToArray();
}

internal static class Rf
{
    public static readonly RfMac Master = RfMac.Parse("11-22-33-44-55-66");
    public static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Local);

    public static byte[] MasterReply()
    {
        var reply = new byte[64];
        reply[0] = 17;
        Master.Write(reply.AsSpan(1));
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(7), 0x0001_0000);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(11), 0x0106);
        return reply;
    }

    public static byte[] Packet(RfCmd cmd, byte fill = 0)
    {
        var p = Enumerable.Range(0, LianLiWire.RfPacketLength).Select(i => (byte)(i * 7 + fill)).ToArray();
        (p[0], p[1]) = (OpcodeLayout.RfPacketTag, (byte)cmd);
        return p;
    }

    /// <summary>Groups relay frames into (frames, packet); USB 17 frames are returned as single-frame groups with a null packet.</summary>
    public static List<(byte[][] Frames, byte[]? Packet)> Group(IReadOnlyList<byte[]> sent)
    {
        var list = new List<(byte[][], byte[]?)>();
        for (var i = 0; i < sent.Count;)
        {
            if (sent[i][0] != (byte)UsbCmd.SendRf)
            {
                list.Add(([sent[i]], null));
                i++;
                continue;
            }

            var frames = sent.Skip(i).Take(LianLiWire.ChunksPerPacket).ToArray();
            if (frames.Length < LianLiWire.ChunksPerPacket && i + frames.Length == sent.Count)
            {
                break; // a background send (e.g. a header repeat) still in flight when the capture was read
            }

            list.Add((frames, RfRelay.Parse(frames).Packet));
            i += LianLiWire.ChunksPerPacket;
        }

        return list;
    }
}

public class LianLiProtocolTests
{
    [Fact]
    public void Opcode_enums_contain_no_denied_value()
    {
        Enum.GetValues<UsbCmd>().Select(v => (byte)v).Should().NotIntersectWith(OpcodeFirewall.DeniedUsbOpcodes);
        Enum.GetValues<RfCmd>().Select(v => (byte)v).Should().NotIntersectWith(OpcodeFirewall.DeniedRfOpcodes);
        Enum.GetValues<UsbCmd>().Select(v => (byte)v).Should().BeEquivalentTo(new byte[] { 16, 17 }, "the IL never sends USB 14 or 18");
        Enum.GetValues<RfCmd>().Select(v => (byte)v).Should().BeEquivalentTo(new byte[] { 16, 18, 20, 21, 25, 32, 36, 38, 39 }, "RF 21 only behind the D2 §5 gate");
    }

    [Fact]
    public void Relay_frames_split_a_240_byte_packet_into_four_chunks_the_firewall_sequence_accepts()
    {
        var to = new RfAddress(8, 3);
        foreach (var rf in Enum.GetValues<RfCmd>())
        {
            var packet = Rf.Packet(rf);
            var frames = RfRelay.Frames(to, packet);
            frames.Should().HaveCount(4).And.OnlyContain(f => f.Length == 64);
            for (var i = 0; i < 4; i++)
            {
                frames[i][..4].Should().Equal(16, (byte)i, 8, 3);
                frames[i][4..].Should().Equal(packet[(i * 60)..((i + 1) * 60)]);
            }

            frames[0][OpcodeLayout.LianLi.RfTagOffset].Should().Be(18);
            frames[0][OpcodeLayout.LianLi.RfCmdOffset].Should().Be((byte)rf);
            var seq = new RelaySequence();
            foreach (var f in frames)
            {
                seq.Check(f, OpcodeLayout.LianLi);
            }

            RfRelay.Parse(frames).Should().BeEquivalentTo((to, packet));
        }

        var shortPacket = () => RfRelay.Frames(to, new byte[239]);
        shortPacket.Should().Throw<ArgumentException>();
        var swapped = RfRelay.Frames(to, Rf.Packet(RfCmd.RgbSync)).Reverse().ToArray();
        var parse = () => RfRelay.Parse(swapped);
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Master_info_parses_the_get_mac_reply()
    {
        UsbFrames.GetMac(8).Should().Equal(new byte[] { 17, 8 }.Concat(new byte[62]));
        var info = UsbFrames.ParseMasterInfo(Rf.MasterReply());
        info.Should().Be(new MasterInfo(Rf.Master, 0x0001_0000, 0x0106));
        info.ClockMs.Should().Be(40960);

        var zeroMac = Rf.MasterReply();
        zeroMac.AsSpan(1, 6).Clear();
        var wrongCmd = Rf.MasterReply();
        wrongCmd[0] = 16;
        foreach (var bad in new[] { zeroMac, wrongCmd, Rf.MasterReply()[..12], Array.Empty<byte>() })
        {
            var act = () => UsbFrames.ParseMasterInfo(bad);
            act.Should().Throw<InvalidDataException>();
        }

        RfMac.Parse("aa:bb:cc:dd:ee:0f").ToString().Should().Be("AA-BB-CC-DD-EE-0F");
    }

    [Fact]
    public void Rgb_upload_header_and_data_packets_match_sync_rgb_data()
    {
        var target = RfMac.Parse("AA-BB-CC-DD-EE-01");
        var data = Enumerable.Range(0, 500).Select(i => (byte)(i * 13)).ToArray();
        var packets = RfPackets.RgbUpload(target, Rf.Master, 0x0293_2E00, new RgbAnimation(500, 2, 160, 1000.0 / 30), data);

        packets.Should().HaveCount(4, "1 header + ceil(500 / 220) data packets");
        for (var i = 0; i < 4; i++)
        {
            var p = packets[i];
            p.Should().HaveCount(240);
            p[..2].Should().Equal(18, 32);
            RfMac.Read(p.AsSpan(2)).Should().Be(target);
            RfMac.Read(p.AsSpan(8)).Should().Be(Rf.Master);
            p[14..18].Should().Equal(0x02, 0x93, 0x2E, 0x00);
            (p[18], p[19]).Should().Be(((byte)i, (byte)4));
        }

        var h = packets[0];
        h[20..24].Should().Equal(0, 0, 1, 0xF4);
        h[24].Should().Be(0);
        h[25..27].Should().Equal(0, 2);
        h[27].Should().Be(160);
        h[32..35].Should().Equal(0, 33, 33);
        h[35..40].Should().OnlyContain(b => b == 0);
        packets.Skip(1).SelectMany(p => p[20..]).Take(500).Should().Equal(data);
        packets[3][(20 + 60)..].Should().OnlyContain(b => b == 0);

        var oversize = () => RfPackets.RgbUpload(target, Rf.Master, 1, new RgbAnimation(12289, 1, 10, 33), new byte[12289]);
        oversize.Should().Throw<ArgumentException>();
        var mismatch = () => RfPackets.RgbUpload(target, Rf.Master, 1, new RgbAnimation(10, 1, 10, 33), new byte[11]);
        mismatch.Should().Throw<ArgumentException>();
        var tooManyLeds = () => RfPackets.RgbUpload(target, Rf.Master, 1, new RgbAnimation(3, 1, 256, 33), new byte[3]);
        tooManyLeds.Should().Throw<ArgumentOutOfRangeException>();
        RfPackets.TimeStamp(Rf.Now).Should().Be(43_200_000u);
    }

    [Fact]
    public void Clock_sync_carries_master_mac_and_date_and_control_carries_address_and_sequence()
    {
        var c = RfPackets.ClockSync(Rf.Master, new DateTime(2026, 10, 17, 12, 34, 56, DateTimeKind.Local));
        c.Should().HaveCount(240);
        c[..2].Should().Equal(18, 20);
        c[2..8].Should().OnlyContain(b => b == 0, "target MAC is zero");
        RfMac.Read(c.AsSpan(8)).Should().Be(Rf.Master);
        c[46..53].Should().Equal(0x07, 0xEA, 10, 17, 12, 34, 56);
        c.Where((_, i) => i >= 14 && i is < 46 or > 52).Should().OnlyContain(b => b == 0);

        var dev = RfMac.Parse("AA-BB-CC-DD-EE-01");
        var p = RfPackets.Control(RfCmd.V150MBSyncSwitch, dev, Rf.Master, new RfAddress(8, 3), 2, 7, 1);
        p[..2].Should().Equal(18, 36);
        RfMac.Read(p.AsSpan(2)).Should().Be(dev);
        RfMac.Read(p.AsSpan(8)).Should().Be(Rf.Master);
        p[14..21].Should().Equal(3, 8, 2, 7, 0, 0, 1);
        foreach (var notControl in new[] { RfCmd.Bind, RfCmd.ClockSync, RfCmd.RgbSync })
        {
            var act = () => RfPackets.Control(notControl, dev, Rf.Master, new RfAddress(8, 3), 0, 1, 0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(26, 20)]
    [InlineData(27, 28)]
    [InlineData(36, 36)]
    [InlineData(40, 40)]
    [InlineData(42, 41)]
    [InlineData(43, 43)]
    [InlineData(50, 43)]
    [InlineData(51, 51)]
    [InlineData(59, 59)]
    [InlineData(63, 63)]
    [InlineData(127, 126)]
    [InlineData(19, 0)]
    public void Rec_type_follows_rf_device_fan_type_ranges(byte fanType, byte recType) =>
        RfDeviceRecord.RecTypeOfFan(fanType).Should().Be(recType);

    [Fact]
    public void Device_list_records_parse_and_untagged_records_are_skipped()
    {
        var reply = new byte[LianLiWire.DeviceListPage];
        (reply[0], reply[1]) = (16, 3);
        for (var i = 0; i < 3; i++)
        {
            var r = reply.AsSpan(4 + (i * RfDeviceRecord.Length), RfDeviceRecord.Length);
            RfMac.Parse($"AA-BB-CC-DD-EE-0{i + 1}").Write(r);
            Rf.Master.Write(r[6..]);
            (r[12], r[13], r[18], r[19]) = (8, (byte)(i + 1), 0, 13);
            BinaryPrimitives.WriteUInt32BigEndian(r[20..], 1234);
            (r[24], r[36], r[40], r[41]) = (22, 50, 9, i == 1 ? (byte)0 : RfDeviceRecord.Tag);
        }

        var list = RfDeviceRecord.ParseList(reply);
        list.Select(d => d.Mac.ToString()).Should().Equal("AA-BB-CC-DD-EE-01", "AA-BB-CC-DD-EE-03");
        var d0 = list[0];
        (d0.MasterMac, d0.Address, d0.FanCount, d0.EffectIndex, d0.CmdSeq, d0.RecType).Should().Be((Rf.Master, new RfAddress(8, 1), 3, 1234u, (byte)9, (byte)20));
        d0.Pwm[0].Should().Be(50);
        (list[0] with { DevType = 3 }).RecType.Should().Be(1);
        (list[0] with { DevType = 66 }).RecType.Should().Be(66);
        var bad = () => RfDeviceRecord.ParseList([17, 0, 0, 0]);
        bad.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Twelve_family_assemblers_with_il_rec_types()
    {
        FamilyDetector.All.Select(a => a.Family).Should().BeEquivalentTo("P28", "HS2", "INFV3", "SLv4", "CL", "H2", "INF", "L88", "SLv3", "Strimer", "V150", "TL");
        FamilyDetector.All.Where(a => !a.Spec.IsRfRelayed).Select(a => a.Family).Should().BeEquivalentTo("HS2", "L88", "Strimer");
        FamilyDetector.ByRecType(20).Select(a => a.Family).Should().Equal("SLv3");
        FamilyDetector.ByRecType(36).Select(a => a.Family).Should().Equal("INF");
        FamilyDetector.ByRecType(43).Select(a => a.Family).Should().Equal("INFV3");
        FamilyDetector.ByRecType(63).Select(a => a.Family).Should().Equal("P28");
        FamilyDetector.ByRecType(126).Select(a => a.Family).Should().Equal("CL");
        FamilyDetector.ByRecType(28).Select(a => a.Family).Should().Equal("TL");
        FamilyDetector.ByRecType(51).Select(a => a.Family).Should().Equal("TL");
        FamilyDetector.ByRecType(10).Select(a => a.Family).Should().Equal("H2");
        FamilyDetector.ByRecType(1).Should().BeEmpty("RecType 1 (dev types 1..9) is not modelled");
        FamilyDetector.ByRecType(0x7F).Should().BeEmpty();
        UnifiedRgb.Core.Effects.LianLi.FamilyModeTable.Default.Families.Should().BeEquivalentTo(FamilyDetector.All.Select(a => a.Family),
            "every family in the mode table has a packet assembler");
    }

    [Theory]
    [InlineData("SLv3", 160, 4, LayoutKind.Ring)]
    [InlineData("SLv3", 50, 1, LayoutKind.Linear)]
    [InlineData("INFV3", 200, 4, LayoutKind.Ring)]
    [InlineData("INF", 88, 2, LayoutKind.Ring)]
    [InlineData("P28", 36, 4, LayoutKind.Ring)]
    [InlineData("SLv4", 104, 2, LayoutKind.Ring)]
    [InlineData("V150", 88, 1, LayoutKind.Linear)]
    [InlineData("Strimer", 120, 1, LayoutKind.Matrix)]
    [InlineData("L88", 60, 1, LayoutKind.Linear)]
    [InlineData("TL", 78, 1, LayoutKind.Matrix)]
    [InlineData("H2", 24, 1, LayoutKind.Ring)]
    public void Zones_follow_the_family_shape(string family, int leds, int zones, LayoutKind kind)
    {
        var z = FamilyDetector.ByFamily(family)!.Zones(leds);
        z.Should().HaveCount(zones);
        z.Should().OnlyContain(l => l.Kind == kind);
        z.Sum(l => l.LedCount).Should().Be(leds);
    }

    [Fact]
    public void Detector_rejects_unknown_types_and_implausible_led_counts()
    {
        var slv3 = FamilyDetector.ByFamily("SLv3")!;
        FamilyDetector.IsDrivable(slv3, 160).Should().BeTrue();
        FamilyDetector.IsDrivable(slv3, 161).Should().BeFalse("more than 4 × 40");
        FamilyDetector.IsDrivable(slv3, 0).Should().BeFalse();
        FamilyDetector.IsDrivable(FamilyDetector.ByFamily("SLv4")!, 208).Should().BeTrue();
        FamilyDetector.IsDrivable(FamilyDetector.ByFamily("HS2")!, 35).Should().BeFalse("HS2 is a USB device, not RF-relayed");
        FamilyDetector.All.Where(a => a.Spec.IsRfRelayed).Should().OnlyContain(a => !FamilyDetector.IsDrivable(a, 256), "the upload header's LED count is one byte");
    }

    [Theory]
    [InlineData("SLv3")]
    [InlineData("CL")]
    [InlineData("V150")]
    public void Assembler_writes_rgb_only(string family)
    {
        var a = FamilyDetector.ByFamily(family)!;
        var leds = new[] { new Rgb(1, 2, 3), new Rgb(4, 5, 6) };
        var buf = new byte[PacketAssembler.PayloadLength(2)];
        a.Assemble(leds, buf).Should().Be(6);
        buf.Should().Equal(1, 2, 3, 4, 5, 6); // Util.GetRgbData: R,G,B for every family, no header

        var hs2 = () => FamilyDetector.ByFamily("HS2")!.Assemble(leds, buf);
        hs2.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Usb_ids_come_from_the_il_and_only_rf_dongles_have_a_role()
    {
        LianLiUsbIds.RoleOf(0x0416, 0x8040).Should().Be(RfDongleRole.Sender);
        LianLiUsbIds.RoleOf(0x1A86, 0xE304).Should().Be(RfDongleRole.Sender);
        LianLiUsbIds.RoleOf(0x0416, 0x8041).Should().Be(RfDongleRole.Receiver);
        LianLiUsbIds.RoleOf(0x1A86, 0xE305).Should().Be(RfDongleRole.Receiver);
        foreach (var (vid, pid) in new (ushort, ushort)[] { (0x1A86, 0x8091), (0x1A86, 0x2107), (0x0416, 0x8050), (0x43A8, 0x0101), (0x43A8, 0x0108), (0x1CBE, 0x0006), (0x1CBE, 0xA018) })
        {
            LianLiUsbIds.RoleOf(vid, pid).Should().BeNull();
            LianLiUsbIds.IsOtherSlv3Device(vid, pid).Should().BeTrue();
        }

        LianLiUsbIds.RfDongles.Should().OnlyContain(d => !LianLiUsbIds.IsOtherSlv3Device(d.Vid, d.Pid));
        (LianLiWire.OutEndpoint, LianLiWire.InEndpoint, LianLiWire.ShortFrame).Should().Be(((byte)0x01, (byte)0x81, 64));
        (LianLiWire.RfPacketLength, LianLiWire.ChunksPerPacket, LianLiWire.UploadChunk, LianLiWire.MaxCompressedAnimation).Should().Be((240, 4, 220, 12288));
    }
}

public class LianLiAdapterTests
{
    private static LianLiDeviceConfig Dev(string mac, string family, int leds, int channel = 8, int rx = 1, int sender = 0) =>
        new() { Mac = mac, Family = family, Leds = leds, Channel = channel, Rx = rx, Sender = sender };

    private static LianLiOptions Options(bool verified = true, params LianLiDeviceConfig[] devices)
    {
        var o = new LianLiOptions { WireFormatVerified = verified, ReceiverInterfaceGuid = FakeUsb.Guid };
        foreach (var d in devices.Length > 0 ? devices : [Dev("AA-BB-CC-DD-EE-01", "SLv3", 160, rx: 1), Dev("AA-BB-CC-DD-EE-02", "CL", 96, rx: 2)])
        {
            o.Devices.Add(d);
        }

        return o;
    }

    private static (LianLiAdapter Adapter, RecordingTransport Wire, FakeUsb Usb, List<UsbDeviceInfo> Opened) Rig(LianLiOptions options, bool encoder = true)
    {
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        var opened = new List<UsbDeviceInfo>();
        wire.EnqueueRead(Rf.MasterReply());
        var a = new LianLiAdapter(options, usb, d => { opened.Add(d); return wire; }, usb, NullLogger.Instance,
            encoder ? new IdentityEncoder() : null, _ => { }, () => Rf.Now);
        return (a, wire, usb, opened);
    }

    private static LoopFrames Loop(int leds, Func<int, int, Rgb> color, int frames = 1, uint index = 43_200_000, double intervalMs = 20)
    {
        var rgb = new byte[frames * leds * 3];
        for (var k = 0; k < frames; k++)
        {
            for (var i = 0; i < leds; i++)
            {
                var c = color(k, i);
                var o = ((k * leds) + i) * 3;
                (rgb[o], rgb[o + 1], rgb[o + 2]) = (c.R, c.G, c.B);
            }
        }

        return new LoopFrames(frames, leds, intervalMs, index, rgb);
    }

    /// <summary>Sent frames without the keep-alive timer's traffic (USB 17 queries and RF 20 ClockSync packets).</summary>
    private static List<byte[]> WithoutKeepAlive(RecordingTransport wire) =>
        Rf.Group(wire.SentFrames).Where(g => g.Packet is not null && g.Packet[1] != (byte)RfCmd.ClockSync).SelectMany(g => g.Frames).ToList();

    [Fact]
    public async Task Discovery_is_passive_uses_only_the_sender_and_filters_family_led_count_and_rf_address()
    {
        var (a, wire, usb, opened) = Rig(Options(true,
            Dev("AA-BB-CC-DD-EE-01", "SLv3", 160), Dev("AA-BB-CC-DD-EE-03", "SLv3", 161), Dev("AA-BB-CC-DD-EE-04", "Nope", 16),
            Dev("FF-FF-FF-FF-FF-FF", "CL", 24), Dev("bad", "CL", 24), Dev("AA-BB-CC-DD-EE-05", "HS2", 35), Dev("AA-BB-CC-DD-EE-06", "Strimer", 120),
            Dev("AA-BB-CC-DD-EE-07", "CL", 24, channel: 0), Dev("AA-BB-CC-DD-EE-08", "CL", 24, rx: 0), Dev("AA-BB-CC-DD-EE-09", "CL", 24, rx: 14),
            Dev("AA-BB-CC-DD-EE-0A", "CL", 24, sender: 1)));
        LianLiDiscovery.Senders(Options(), usb).Select(s => (s.Vid, s.Pid)).Should().Equal(((ushort)0x0416, (ushort)0x8040));
        var found = await a.DiscoverAsync(CancellationToken.None);
        found.Should().ContainSingle();
        var d = found[0];
        d.Id.Value.Should().Be("lianli:rf:AA-BB-CC-DD-EE-01");
        d.Class.Should().Be(DeviceClass.LianLi);
        d.Capabilities.Delivery.Should().Be(Delivery.UploadedLoop);
        (d.Capabilities.MaxFps, d.Capabilities.MaxLoopFrames, d.Capabilities.MinIntervalMs, d.Capabilities.MaxUploadBytes).Should().Be((0, 200, 20.0, 12288));
        d.Zones.Should().ContainSingle("one RfDevice = one chain = one zone (D2 §1)").Which.Layout.Should().Be(ZoneLayout.Linear(160));
        d.Zones[0].Name.Should().Be("Chain (4 fans)");
        LianLiAdapter.Describe(LianLiDiscovery.Discover(Options(true, Dev("AA-BB-CC-DD-EE-01", "SLv3", 40)), usb, NullLogger.Instance)[0])
            .Zones.Single().Layout.Should().Be(ZoneLayout.Ring(40), "a single fan stays a ring");
        wire.Frames.Should().BeEmpty("discovery performs no device I/O");
        opened.Should().BeEmpty();

        var none = new LianLiAdapter(new LianLiOptions(), new FakeUsb(), _ => throw new InvalidOperationException(), null, NullLogger.Instance);
        (await none.DiscoverAsync(CancellationToken.None)).Should().BeEmpty("no dongle interface GUID configured");
    }

    [Fact]
    public async Task Unverified_wire_format_never_opens_or_writes()
    {
        var (a, wire, _, opened) = Rig(Options(verified: false));
        var d = (await a.DiscoverAsync(CancellationToken.None))[0];
        var open = () => a.OpenAsync(d.Id, CancellationToken.None);
        await open.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not verified*");
        var write = () => a.WriteFrame(d.Id, new ZoneId(0), new Rgb[160]);
        write.Should().Throw<NotSupportedException>();
        var upload = () => a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(160, (_, _) => Rgb.White), CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not open*");
        var save = () => a.SaveConfigAsync("test", CancellationToken.None);
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not verified*");
        var bind = () => a.BindAsync(new FakeUsb().Present[0], RfMac.Parse("AA-BB-CC-DD-EE-09"), new BindConfirmation("owner", DateTimeOffset.UtcNow), CancellationToken.None);
        await bind.Should().ThrowAsync<InvalidOperationException>();
        wire.Frames.Should().BeEmpty();
        opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_codec_the_device_is_blocked_and_nothing_is_opened()
    {
        foreach (var verified in new[] { true, false })
        {
            var (a, wire, _, opened) = Rig(Options(verified), encoder: false);
            var d = (await a.DiscoverAsync(CancellationToken.None))[0];
            var open = () => a.OpenAsync(d.Id, CancellationToken.None);
            (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().StartWith("yuz.dll missing");
            wire.Frames.Should().BeEmpty();
            opened.Should().BeEmpty();
        }

        var missing = new LianLiAdapter(Options(), new FakeUsb(), _ => throw new InvalidOperationException(), null, NullLogger.Instance,
            encoderFactory: () => LianLi.Codec.TinyuzEncoder.Open(@"C:\nowhere\yuz.dll"));
        var id = (await missing.DiscoverAsync(CancellationToken.None))[0].Id;
        var act = () => missing.OpenAsync(id, CancellationToken.None);
        (await act.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain(@"C:\nowhere\yuz.dll");
    }

    [Theory]
    [InlineData("P28", 36)]
    [InlineData("INFV3", 200)]
    [InlineData("SLv4", 208)]
    [InlineData("CL", 96)]
    [InlineData("H2", 24)]
    [InlineData("TL", 78)]
    [InlineData("INF", 176)]
    [InlineData("SLv3", 160)]
    [InlineData("V150", 88)]
    public async Task Static_and_gradient_frames_match_recorded_fixture(string family, int leds)
    {
        var (a, wire, _, opened) = Rig(Options(true, Dev("AA-BB-CC-DD-EE-01", family, leds, channel: 8, rx: 1)));
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        await a.DisposeAsyncKeepingWire(d, () =>
        {
            a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(leds, (_, _) => new Rgb(255, 0, 0)), CancellationToken.None).GetAwaiter().GetResult();
            a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(leds, (_, i) => new Rgb((byte)(i * 255 / leds), 0, (byte)(255 - (i * 255 / leds))), index: 43_200_001),
                CancellationToken.None).GetAwaiter().GetResult();
        });

        opened.Should().ContainSingle().Which.Pid.Should().Be(0x8040);
        wire.SentFrames[0].Should().Equal(UsbFrames.GetMac(8), "the master MAC is read first");
        var outFrames = WithoutKeepAlive(wire);
        UsbFixture.Match($"{family.ToLowerInvariant()}_static_gradient", outFrames);

        var packets = Rf.Group(outFrames).Select(g => g.Packet!).ToList();
        var perUpload = 4 + (((3 * leds) + 219) / 220); // header ×4 + data packets
        packets.Should().HaveCount(2 * perUpload).And.OnlyContain(p => p[1] == (byte)RfCmd.RgbSync);
        packets.Select(p => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(14))).Distinct().Should().Equal(43_200_000u, 43_200_001u);
        packets[0][27].Should().Be((byte)leds);
        (packets[0][32], packets[0][33], packets[0][34]).Should().Be(((byte)0, (byte)20, (byte)0), "20 ms interval");
        outFrames.Should().OnlyContain(f => f[2] == 8 && f[3] == 1);
    }

    [Fact]
    public async Task Write_frame_throws_and_a_loop_upload_is_one_header_four_times_then_the_data()
    {
        var (a, wire, _, _) = Rig(Options());
        var d = (await a.DiscoverAsync(CancellationToken.None))[0];
        await a.OpenAsync(d.Id, CancellationToken.None);
        List<byte[]> Uploads() => Rf.Group(wire.SentFrames).Where(g => g.Packet?[1] == (byte)RfCmd.RgbSync).Select(g => g.Packet!).ToList();
        var write = () => a.WriteFrame(d.Id, LianLiAdapter.ChainZone, Enumerable.Repeat(Rgb.White, 160).ToArray());
        write.Should().Throw<NotSupportedException>("Lian Li RF chains take uploaded loops only (D2 §1)");
        Uploads().Should().BeEmpty();

        await a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(160, (k, _) => k == 0 ? Rgb.White : Rgb.Black, frames: 3, intervalMs: 20.83), CancellationToken.None);
        var up = Uploads();
        up.Count(p => p[18] == 0).Should().Be(4, "the header is sent once and repeated 3 times");
        up.Count(p => p[18] != 0).Should().Be(7, "3 × 160 LEDs → 1440 bytes → 7 × 220-byte packets");
        up.Skip(4).SelectMany(p => p[20..]).Take(480).Should().OnlyContain(b => b == 255);
        var h = up[0];
        BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(25)).Should().Be(3);
        (h[32], h[33], h[34]).Should().Be(((byte)0, (byte)20, (byte)83), "20.83 ms → 20 + 83 hundredths");
        RfPackets.HeaderOf([.. up.Take(1), .. up.Skip(4)]).Should().Be(new LoopHeader(3, 160, 1440, 8));

        var stats = a.LastUpload(d.Id)!;
        (stats.EffectIndex, stats.Frames, stats.RawBytes, stats.CompressedBytes, stats.Packets, stats.Decimations, stats.CodecSha256)
            .Should().Be((43_200_000u, 3, 1440, 1440, 8, 0, nameof(IdentityEncoder)));
        stats.StartedTimestamp.Should().BePositive();

        var wrongZone = () => a.UploadLoopAsync(d.Id, new ZoneId(1), Loop(160, (_, _) => Rgb.White), CancellationToken.None);
        await wrongZone.Should().ThrowAsync<ArgumentException>();
        var wrongLeds = () => a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(159, (_, _) => Rgb.White), CancellationToken.None);
        await wrongLeds.Should().ThrowAsync<ArgumentException>();
        await a.DisposeAsync();
    }

    [Fact]
    public async Task Keep_alive_queries_the_master_and_broadcasts_clock_sync_and_never_binds()
    {
        var (a, wire, _, _) = Rig(Options());
        var found = await a.DiscoverAsync(CancellationToken.None);
        foreach (var d in found)
        {
            await a.OpenAsync(d.Id, CancellationToken.None);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Rf.Group(wire.SentFrames).Any(g => g.Packet?[1] == (byte)RfCmd.ClockSync) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await a.UploadLoopAsync(found[1].Id, LianLiAdapter.ChainZone, Loop(96, (_, _) => Rgb.Black), CancellationToken.None);
        await a.DisposeAsync();
        var groups = Rf.Group(wire.SentFrames);
        groups.Count(g => g.Packet is null).Should().BeGreaterThanOrEqualTo(2, "USB 17 on open and on every keep-alive");
        groups.Where(g => g.Packet is null).Should().OnlyContain(g => g.Frames[0].Take(2).SequenceEqual(new byte[] { 17, 8 }));
        var sync = groups.First(g => g.Packet?[1] == (byte)RfCmd.ClockSync);
        sync.Frames.Should().OnlyContain(f => f[2] == 8 && f[3] == 0xFF, "broadcast on the master channel");
        sync.Packet.Should().Equal(RfPackets.ClockSync(Rf.Master, Rf.Now));
        groups.Should().NotContain(g => g.Packet != null && g.Packet[1] == (byte)RfCmd.Bind);

        var seq = new RelaySequence();
        foreach (var f in wire.SentFrames)
        {
            seq.Check(f, OpcodeLayout.LianLi);
        }

        var before = wire.Frames.Count;
        var bind = () => a.BindAsync(new FakeUsb().Present[0], RfMac.Parse("AA-BB-CC-DD-EE-09"), new BindConfirmation("owner", DateTimeOffset.UtcNow), CancellationToken.None);
        await bind.Should().ThrowAsync<NotSupportedException>().WithMessage("*PWM*");
        wire.Frames.Should().HaveCount(before);
    }

    [Fact]
    public async Task Sync_switch_sends_a_control_packet_and_accepts_only_36_38_39()
    {
        var (a, wire, _, _) = Rig(Options());
        var found = await a.DiscoverAsync(CancellationToken.None);
        var d = found[1];
        await a.OpenAsync(d.Id, CancellationToken.None);
        await a.SetSyncSwitchAsync(d.Id, RfCmd.V150MBSyncSwitch, true, CancellationToken.None);
        await a.SetSyncSwitchAsync(d.Id, RfCmd.V150LightSyncSwitch, false, CancellationToken.None);
        var act = () => a.SetSyncSwitchAsync(d.Id, RfCmd.RgbSync, true, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await a.DisposeAsync();

        var controls = Rf.Group(wire.SentFrames).Where(g => g.Packet?[1] is 36 or 38).ToList();
        controls.Should().HaveCount(2);
        controls.Should().OnlyContain(g => g.Frames.All(f => f[2] == 8 && f[3] == 2));
        var p = controls[0].Packet!;
        RfMac.Read(p.AsSpan(2)).Should().Be(RfMac.Parse("AA-BB-CC-DD-EE-02"));
        RfMac.Read(p.AsSpan(8)).Should().Be(Rf.Master);
        p[14..21].Should().Equal(2, 8, 1, 1, 0, 0, 1);
        controls[1].Packet![17].Should().Be(2, "command sequence increments");
        controls[1].Packet![20].Should().Be(0);
    }

    [Theory]
    [InlineData(new byte[] { 32 })]
    [InlineData(new byte[] { 35 })]
    [InlineData(new byte[] { 16, 0, 8, 1, 18, 23 })]
    [InlineData(new byte[] { 16, 0, 8, 1, 18, 24 })]
    [InlineData(new byte[] { 16, 0, 8, 1, 18, 33 })]
    [InlineData(new byte[] { 16, 0, 8, 1, 0, 32 })]
    [InlineData(new byte[] { 16, 1, 8, 1, 18, 32 })]
    [InlineData(new byte[] { 16, 4, 8, 1, 18, 32 })]
    public async Task Sender_chain_blocks_denied_untagged_and_orphan_frames_before_the_wire(byte[] head)
    {
        var wire = new RecordingTransport();
        var chain = RfSender.BuildChain(wire, new DeviceState());
        var frame = new byte[64];
        head.CopyTo(frame, 0);
        var act = async () => await chain.SendAsync(frame, CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        wire.Frames.Should().BeEmpty();
        var odd = async () => await chain.SendAsync(new byte[100], CancellationToken.None);
        await odd.Should().ThrowAsync<FrameRejectedException>();
    }

    [Fact]
    public async Task Sender_unplug_raises_hotplug_and_blocks_writes_until_replug()
    {
        var (a, _, usb, _) = Rig(Options());
        var events = new List<HotplugEvent>();
        a.Hotplug += events.Add;
        var found = await a.DiscoverAsync(CancellationToken.None);
        await a.OpenAsync(found[0].Id, CancellationToken.None);

        usb.Raise(DeviceInterfaceChange.Removed, usb.Present[0].Path.ToUpperInvariant());
        events.Select(e => e.Kind).Should().Equal(HotplugKind.Removed, HotplugKind.Removed);
        var write = () => a.UploadLoopAsync(found[0].Id, LianLiAdapter.ChainZone, Loop(160, (_, _) => Rgb.White), CancellationToken.None);
        (await write.Should().ThrowAsync<FrameRejectedException>()).Which.Reason.Should().Be(FrameRejectReason.DeviceOffline);

        usb.Raise(DeviceInterfaceChange.Removed, usb.Present[1].Path);
        events.Should().HaveCount(2, "the receiver is not the dongle these devices are driven through");
        usb.Raise(DeviceInterfaceChange.Arrived, usb.Present[0].Path);
        events.Should().HaveCount(4);
        await write();
        await a.DisposeAsync();
    }
}

internal static class LianLiTestExtensions
{
    /// <summary>Runs <paramref name="body"/> then disposes the adapter, so the keep-alive timer cannot interleave after it.</summary>
    public static async Task DisposeAsyncKeepingWire(this LianLiAdapter a, ControllerDescriptor d, Action body)
    {
        body();
        await a.CloseAsync(d.Id);
        await a.DisposeAsync();
    }
}

/// <summary>
/// tests/Fixtures/usbpcap/&lt;name&gt;.rec in RecordingTransport format. NOT live captures: regenerated (URGB_UPDATE_FIXTURES=1)
/// from the M5c builders, so the USB relay header, the 240-byte RF packet and the upload header are IL-pinned, while the
/// animation bytes are the uncompressed frame (identity stand-in for tinyuz, which is not in the IL). See the fixture README.
/// </summary>
internal static class UsbFixture
{
    public static void Match(string name, IReadOnlyList<byte[]> actual)
    {
        var path = Path.Combine(Fixture.RepoRoot(), "tests", "Fixtures", "usbpcap", name + ".rec");
        if (Environment.GetEnvironmentVariable("URGB_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var w = File.Create(path);
            RecordingTransport.Save(w, actual.Select(f => new RecordedFrame(RecordDirection.Out, 0, f)));
        }

        File.Exists(path).Should().BeTrue($"fixture {path} must exist");
        using var r = File.OpenRead(path);
        var expected = RecordingTransport.Load(r).Where(f => f.Direction == RecordDirection.Out).Select(f => f.Data).ToList();
        actual.Should().HaveCount(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            actual[i].Should().Equal(expected[i], $"frame {i} of {name}");
        }
    }
}
