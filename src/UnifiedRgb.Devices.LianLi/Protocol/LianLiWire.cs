using System.Buffers.Binary;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.LianLi.Protocol;

/// <summary>
/// Wire constants, IL-pinned (analysis/il; M5b: endpoints and report size, M5c: <c>MasterDevice</c>). Every RF packet is
/// <see cref="RfPacketLength"/> bytes and goes out through the SENDER dongle as <see cref="ChunksPerPacket"/> USB frames
/// <c>[16][chunk][channel][rx][60 bytes]</c> (<c>SendRfData</c>). There is no separate long-frame opcode: the IL never sends
/// USB 14, and a long animation is many 240-byte packets.
/// </summary>
public static class LianLiWire
{
    /// <summary>OUT endpoint of every slv3 USB device (<c>OpenEndpointWriter(WriteEndpointID 1)</c>).</summary>
    public const byte OutEndpoint = 0x01;

    /// <summary>IN endpoint, interrupt (<c>OpenEndpointReader(ReadEndpointID 129, …, EndpointType 3)</c>).</summary>
    public const byte InEndpoint = 0x81;

    /// <summary>RF dongle read buffer (<c>InitRFDev</c>: 512) — replies are read in 64-byte packets.</summary>
    public const int RfReadBuffer = 512;

    /// <summary>Report size: every USB command is a 64-byte array, opcode at [0].</summary>
    public const int ShortFrame = 64;

    /// <summary>USB relay header <c>[16][chunk][channel][rx]</c>.</summary>
    public const int RelayHeader = 4;

    /// <summary>RF packet bytes per USB frame (<c>Array.Copy(RFdata, num, array, 4, 60)</c>).</summary>
    public const int ChunkData = ShortFrame - RelayHeader;

    /// <summary>Every packet <c>MasterDevice</c> builds is <c>new byte[240]</c>.</summary>
    public const int RfPacketLength = 240;

    public const int ChunksPerPacket = RfPacketLength / ChunkData;

    /// <summary>RF packet head <c>[18][RF_CMD][target MAC×6][master MAC×6]</c>; command fields start at 14.</summary>
    public const int RfHead = 14;

    /// <summary>Compressed animation bytes per upload packet (<c>lzo_rgb_rf_valid_len = 220</c>, at packet offset 20).</summary>
    public const int UploadChunk = 220;

    /// <summary><c>MasterDevice.LzoMaxRgbDataLen</c>: larger compressed animations are refused ("out of max uz length").</summary>
    public const int MaxCompressedAnimation = 12288;

    /// <summary><c>MasterDevice.MasterChannel</c> initial value; L-Connect moves it to 8 + 4·i when several masters are seen.</summary>
    public const byte DefaultMasterChannel = 8;

    /// <summary>rx byte of broadcast relays (ClockSync, SaveConfig: <c>SendRfData(MasterChannel, byte.MaxValue, …)</c>).</summary>
    public const byte BroadcastRx = 0xFF;

    /// <summary>One receiver device-list page (<c>defalut_page_length</c>): [16][count][2 status bytes] + 10 × 42-byte records.</summary>
    public const int DeviceListPage = 434;

    public static ReadOnlySpan<byte> Broadcast => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
}

public enum RfDongleRole
{
    Sender,
    Receiver,
}

/// <summary>
/// USB ids from <c>slv3.WinUsb</c> / <c>slv3.WinUsbLed</c> (analysis/il). Every
/// RF packet and the MAC/clock query go to <c>MasterDevice.RFSender</c>; optional receiver feedback only asks the device-list query
/// (<c>GetDev</c>). The other slv3 devices are listed so discovery can positively exclude them.
/// Owner's machine (PnP, 2026-10-17): 0416:8040 "SLV3TX_V1.6", 0416:8041 "SLV3RX_V1.6", 1A86:2107 (HID + MEDIA twin),
/// 2 × 1A86:8091, 3 × 1CBE:0006 "TL-LCD Wireless-1.3".
/// </summary>
public static class LianLiUsbIds
{
    public static IReadOnlyList<(ushort Vid, ushort Pid, RfDongleRole Role)> RfDongles { get; } =
    [
        (0x0416, 0x8040, RfDongleRole.Sender), // RFSenderVid/Pid 1046/32832 — "SLV3TX_V1.6"
        (0x1A86, 0xE304, RfDongleRole.Sender), // RFVid/RFSPid 6790/58116
        (0x0416, 0x8041, RfDongleRole.Receiver), // RFReceiverVid/Pid 1046/32833 — "SLV3RX_V1.6"
        (0x1A86, 0xE305, RfDongleRole.Receiver), // RFVid/RFRPid 6790/58117
    ];

    /// <summary>
    /// <c>RFController.HUBVID/HUBPID</c> = "1A86"/"8091". Declared but never read in the IL: slv3 never matches or opens it. Its
    /// only hub logic is <c>UsbHubHelper.GetWinUsbParentPort</c>, which looks up the parent hub of an already-opened dongle.
    /// Treated as a parent hub (WCH 1A86:8091 is a USB 2.0 hub chip — inferred, not in the IL); never opened.
    /// </summary>
    public static (ushort Vid, ushort Pid) Hub { get; } = (0x1A86, 0x8091);

    /// <summary>
    /// Recognised but never opened: the hub above, 8.8-inch LED 0416:8050, wired LED receivers 43A8:0101–0108, LCDs
    /// 1CBE:0005/0006/A018/A019 (0006 = "TL-LCD Wireless-1.3"), and 1A86:2107 — the HID interface slv3 only reads a MAC map
    /// from (<c>HIDUsb.AddMacMap</c>) and its MEDIA-class twin.
    /// </summary>
    public static bool IsOtherSlv3Device(ushort vid, ushort pid) =>
        (vid, pid) == Hub
        || (vid == 0x1A86 && pid == 0x2107)
        || (vid == 0x0416 && pid == 0x8050)
        || (vid == 0x43A8 && pid is >= 0x0101 and <= 0x0108)
        || (vid == 0x1CBE && pid is 0x0005 or 0x0006 or 0xA018 or 0xA019);

    public static RfDongleRole? RoleOf(ushort vid, ushort pid)
    {
        foreach (var d in RfDongles)
        {
            if (d.Vid == vid && d.Pid == pid)
            {
                return d.Role;
            }
        }

        return null;
    }
}

/// <summary>Six-byte RF address of a wireless device or master dongle.</summary>
public readonly record struct RfMac(ulong Value)
{
    public static RfMac Broadcast { get; } = new(0xFFFF_FFFF_FFFF);

    public static RfMac Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var hex = s.Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);
        if (hex.Length != 12)
        {
            throw new FormatException($"'{s}' is not a 6-byte MAC.");
        }

        return new RfMac(Convert.ToUInt64(hex, 16));
    }

    public static RfMac Read(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        for (var i = 0; i < 6; i++)
        {
            v = (v << 8) | b[i];
        }

        return new RfMac(v);
    }

    public void Write(Span<byte> dst)
    {
        for (var i = 0; i < 6; i++)
        {
            dst[i] = (byte)(Value >> (8 * (5 - i)));
        }
    }

    public override string ToString()
    {
        Span<byte> b = stackalloc byte[6];
        Write(b);
        return Convert.ToHexString(b).Insert(10, "-").Insert(8, "-").Insert(6, "-").Insert(4, "-").Insert(2, "-");
    }
}

/// <summary>The relay address of a wireless device: its RF channel and rx slot (device-list record bytes 12/13).</summary>
public readonly record struct RfAddress(byte Channel, byte Rx)
{
    public static RfAddress BroadcastOn(byte masterChannel) => new(masterChannel, LianLiWire.BroadcastRx);
}

/// <summary>The sender dongle's answer to USB 17 (<c>QuerryMasterMac</c>).</summary>
public readonly record struct MasterInfo(RfMac Mac, uint ClockTicks, ushort Version)
{
    /// <summary><c>SysClock = time_tmos × 0.625</c> (ms).</summary>
    public long ClockMs => (long)(ClockTicks * 0.625);
}

/// <summary>USB control frames to the sender dongle.</summary>
public static class UsbFrames
{
    /// <summary>USB 17: <c>[17][MasterChannel]</c>.</summary>
    public static byte[] GetMac(byte masterChannel)
    {
        var f = new byte[LianLiWire.ShortFrame];
        f[0] = (byte)UsbCmd.GetMac;
        f[1] = masterChannel;
        return f;
    }

    /// <summary>Reply <c>[17][mac×6][clock u32 BE][version u16 BE]</c>; an all-zero MAC or zero clock is not a valid master.</summary>
    public static MasterInfo ParseMasterInfo(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 13 || reply[0] != (byte)UsbCmd.GetMac)
        {
            throw new InvalidDataException("Not a GetMac reply.");
        }

        var info = new MasterInfo(RfMac.Read(reply[1..]), BinaryPrimitives.ReadUInt32BigEndian(reply[7..]), BinaryPrimitives.ReadUInt16BigEndian(reply[11..]));
        return info.Mac.Value == 0 || info.ClockTicks == 0 ? throw new InvalidDataException("GetMac reply carries no master MAC/clock.") : info;
    }
}

/// <summary>
/// <c>MasterDevice.SendRfData</c>: one 240-byte RF packet → four 64-byte USB frames <c>[16][chunk 0..3][channel][rx][60]</c>.
/// The RF opcode is packet byte 1 = frame byte 5 of chunk 0, exactly where <see cref="OpcodeLayout.LianLi"/> looks.
/// </summary>
public static class RfRelay
{
    public static IReadOnlyList<byte[]> Frames(RfAddress to, ReadOnlySpan<byte> packet)
    {
        if (packet.Length != LianLiWire.RfPacketLength)
        {
            throw new ArgumentException($"RF packets are {LianLiWire.RfPacketLength} bytes.", nameof(packet));
        }

        var frames = new byte[LianLiWire.ChunksPerPacket][];
        for (var i = 0; i < frames.Length; i++)
        {
            var f = new byte[LianLiWire.ShortFrame];
            f[0] = (byte)UsbCmd.SendRf;
            f[1] = (byte)i;
            f[2] = to.Channel;
            f[3] = to.Rx;
            packet.Slice(i * LianLiWire.ChunkData, LianLiWire.ChunkData).CopyTo(f.AsSpan(LianLiWire.RelayHeader));
            frames[i] = f;
        }

        return frames;
    }

    /// <summary>Reassembles a packet from its four frames (tests and capture tooling).</summary>
    public static (RfAddress To, byte[] Packet) Parse(IReadOnlyList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count != LianLiWire.ChunksPerPacket)
        {
            throw new InvalidDataException("An RF packet is four relay frames.");
        }

        var packet = new byte[LianLiWire.RfPacketLength];
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f[0] != (byte)UsbCmd.SendRf || f[1] != i || f[2] != frames[0][2] || f[3] != frames[0][3])
            {
                throw new InvalidDataException($"Frame {i} is not chunk {i} of the same relay.");
            }

            f.AsSpan(LianLiWire.RelayHeader, LianLiWire.ChunkData).CopyTo(packet.AsSpan(i * LianLiWire.ChunkData));
        }

        return (new RfAddress(frames[0][2], frames[0][3]), packet);
    }
}

/// <summary>Header fields of an animation upload (<c>SyncRgbData</c> packet 0). Interval is the per-frame time in ms.</summary>
public sealed record RgbAnimation(int CompressedLength, int TotalFrames, int LedCount, double IntervalMs, double SubIntervalMs = 0, byte OuterMatchMax = 0, int TotalSubFrames = 0);

/// <summary>Builders for the 240-byte RF packets of <c>MasterDevice</c> (analysis/il), byte for byte.</summary>
public static class RfPackets
{
    /// <summary>Times the upload header packet is sent (once, then 3 repeats 20 ms apart).</summary>
    public const int UploadHeaderSends = 4;

    public static readonly TimeSpan UploadHeaderRepeatDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// <c>SyncControlInfo</c> commands (Select 18, wireless theme 25, sync switches 36/38/39):
    /// [14] target rx, [15] target channel, [16] slave index, [17] command sequence, [20] value.
    /// </summary>
    public static byte[] Control(RfCmd cmd, RfMac target, RfMac master, RfAddress at, byte slaveIndex, byte cmdSeq, byte value)
    {
        if (cmd is RfCmd.Bind or RfCmd.ClockSync or RfCmd.RgbSync or RfCmd.SaveConfig)
        {
            throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "Not a control command.");
        }

        var p = Head(cmd, target, master);
        p[14] = at.Rx;
        p[15] = at.Channel;
        p[16] = slaveIndex;
        p[17] = cmdSeq;
        p[20] = value;
        return p;
    }

    /// <summary>
    /// <c>SyncMasterClock</c> (RF 20, broadcast on the master channel, rx 0xFF): target MAC zero, master MAC at 8, then the
    /// 220-byte <c>cpuInfoParam</c> block at 14 with year (u16 BE), month, day, hour, minute, second at block offset 32.
    /// The rest of the block is LCD-fan theme/sensor data; this adapter sends it as zeros.
    /// </summary>
    public static byte[] ClockSync(RfMac master, DateTime now)
    {
        var p = new byte[LianLiWire.RfPacketLength];
        p[0] = OpcodeLayout.RfPacketTag;
        p[1] = (byte)RfCmd.ClockSync;
        master.Write(p.AsSpan(8));
        var t = p.AsSpan(LianLiWire.RfHead + 32);
        BinaryPrimitives.WriteUInt16BigEndian(t, (ushort)now.Year);
        (t[2], t[3], t[4], t[5], t[6]) = ((byte)now.Month, (byte)now.Day, (byte)now.Hour, (byte)now.Minute, (byte)now.Second);
        return p;
    }

    /// <summary>
    /// <c>SyncRgbData</c> (RF 32): packet 0 = header, packets 1..n = 220-byte chunks of the compressed animation at offset 20.
    /// Every packet: [2..7] target MAC, [8..13] master MAC, [14..17] effect index (u32 BE), [18] packet index, [19] n + 1.
    /// Header: [20..23] compressed length u32 BE, [24] 0, [25..26] frames u16 BE, [27] LED count, [32..33] interval u16 BE,
    /// [34] interval hundredths, [35..36] sub-interval u16 BE, [37] outer-match-max, [38..39] sub-frames u16 BE.
    /// </summary>
    public static IReadOnlyList<byte[]> RgbUpload(RfMac target, RfMac master, uint effectIndex, RgbAnimation animation, ReadOnlySpan<byte> compressed)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (compressed.Length == 0 || compressed.Length > LianLiWire.MaxCompressedAnimation || compressed.Length != animation.CompressedLength)
        {
            throw new ArgumentException($"Compressed animation must be 1..{LianLiWire.MaxCompressedAnimation} bytes and match the header.", nameof(compressed));
        }

        if (animation.LedCount is < 1 or > byte.MaxValue || animation.TotalFrames is < 1 or > ushort.MaxValue
            || !(animation.IntervalMs > 0) || animation.IntervalMs >= ushort.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(animation), "LED count must fit a byte, frames and the interval (ms) a u16.");
        }

        var chunks = (compressed.Length + LianLiWire.UploadChunk - 1) / LianLiWire.UploadChunk;
        var packets = new byte[chunks + 1][];
        for (var i = 0; i <= chunks; i++)
        {
            var p = Head(RfCmd.RgbSync, target, master);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(14), effectIndex);
            p[18] = (byte)i;
            p[19] = (byte)(chunks + 1);
            if (i == 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), (uint)compressed.Length);
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(25), (ushort)animation.TotalFrames);
                p[27] = (byte)animation.LedCount;
                var centi = (int)Math.Round(animation.IntervalMs * 100.0); // 20.83 → 20 + 83, not 20 + 82 (binary fraction)
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(32), (ushort)(centi / 100));
                p[34] = (byte)(centi % 100);
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(35), (ushort)(int)animation.SubIntervalMs);
                p[37] = animation.OuterMatchMax;
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(38), (ushort)animation.TotalSubFrames);
            }
            else
            {
                var o = (i - 1) * LianLiWire.UploadChunk;
                compressed.Slice(o, Math.Min(LianLiWire.UploadChunk, compressed.Length - o)).CopyTo(p.AsSpan(20));
            }

            packets[i] = p;
        }

        return packets;
    }

    /// <summary>The header fields FrameValidator checks (D2 §7), read back from built packet 0 and the packet count.</summary>
    public static LoopHeader HeaderOf(IReadOnlyList<byte[]> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var h = packets[0];
        if (h.Length != LianLiWire.RfPacketLength || h[1] != (byte)RfCmd.RgbSync || h[18] != 0)
        {
            throw new ArgumentException("Packet 0 is not an RF 32 upload header.", nameof(packets));
        }

        return new LoopHeader(BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(25)), h[27], (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(20)), packets.Count);
    }

    /// <summary>
    /// <c>MasterDevice.SaveConfig</c> (RF 21), broadcast on the master channel, rx 0xFF: [2..7] FF×6, [8..13] master MAC,
    /// [14] 0xFF, [15] 0, [16] 0. Built only for <see cref="LianLiAdapter.SaveConfigAsync"/> (D2 §5 gate).
    /// </summary>
    public static byte[] SaveConfig(RfMac master)
    {
        var p = Head(RfCmd.SaveConfig, RfMac.Broadcast, master);
        p[14] = 0xFF;
        return p;
    }

    /// <summary><c>Util.GetTimeStamp</c>: milliseconds since local midnight, used as the effect index.</summary>
    public static uint TimeStamp(DateTime now) => (uint)(now - now.Date).TotalMilliseconds;

    private static byte[] Head(RfCmd cmd, RfMac target, RfMac master)
    {
        var p = new byte[LianLiWire.RfPacketLength];
        p[0] = OpcodeLayout.RfPacketTag;
        p[1] = (byte)cmd;
        target.Write(p.AsSpan(2));
        master.Write(p.AsSpan(8));
        return p;
    }
}

/// <summary>
/// One 42-byte record of the receiver's device list (<c>MasterDevice.RefreshList</c>, reply to USB 16 on the RECEIVER dongle):
/// [0..5] MAC, [6..11] master MAC, [12] channel, [13] rx, [14..17] clock, [18] dev type, [19] fan count (+10 = INF right
/// attach), [20..23] effect index, [24..27] fan types, [28..35] fan speeds/flags, [36..39] PWM, [40] command sequence,
/// [41] = 28 (record tag). Parsing only: the query needs a receiver-role transport that is not wired in v1 (SELF_AUDIT M5c).
/// </summary>
/// <summary>
/// <c>RfDevice</c>'s LED-count rules (lianli.slv3.dll IL, setter path of the device record and the <c>UIDot</c> getter), so a
/// configured device can be checked against what L-Connect itself would compute.
/// </summary>
public static class RfLedCounts
{
    /// <summary>
    /// <c>RfDevice.LedNum</c>: dev type 1/2/3/4 → 116/132/174/88, 10/11 (H2 waterblock) → 24 + 24·fans, 65 → 96, 66 → 88;
    /// otherwise by RecType: 20 → 40·fans, 59 → 52·fans, 36/43 → 44·fans, 41/126 → 24·fans, 63 → 9·fans, and every other
    /// RecType (TL 28/51 included) → 26·fans.
    /// </summary>
    public static int LedNum(byte devType, byte recType, int fans) => devType switch
    {
        1 => 116,
        2 => 132,
        3 => 174,
        4 => 88,
        10 or 11 => 24 + (fans * 24),
        65 => 96,
        66 => 88,
        _ => recType switch
        {
            20 => fans * 40,
            59 => fans * 52,
            36 or 43 => fans * 44,
            41 or 126 => fans * 24,
            63 => fans * 9,
            _ => fans * 26,
        },
    };

    /// <summary>
    /// <c>RfDevice.UIDot</c> (the L-Connect UI's dots, not LEDs) for the fan-group branch: 20 → 12·fans, 28/51 → 13·fans, 10/11 →
    /// 24 + 8·fans, 59 → 13·fans, 36/43/44/45/41/126 → 8·fans, 63 → 9·fans; 0 when the IL has no rule here.
    /// </summary>
    public static int UiDot(byte recType, int fans) => recType switch
    {
        20 => fans * 12,
        28 or 51 or 59 => fans * 13,
        10 or 11 => 24 + (fans * 8),
        36 or 43 or 44 or 45 or 41 or 126 => fans * 8,
        63 => fans * 9,
        _ => 0,
    };
}

public sealed record RfDeviceRecord(RfMac Mac, RfMac MasterMac, RfAddress Address, byte DevType, int FanCount, uint EffectIndex, byte[] FanTypes, byte[] Pwm, byte CmdSeq)
{
    public const int Length = 42;
    public const byte Tag = 28;

    /// <summary><c>RfDevice</c>'s RecType derivation for fan groups (dev type 0) from a per-fan type byte, and from the dev type otherwise.</summary>
    public byte RecType => DevType switch
    {
        0 => RecTypeOfFan(FanTypes[0]),
        >= 1 and <= 9 => 1,
        10 or 11 or 65 or 66 => DevType,
        _ => 0,
    };

    public static byte RecTypeOfFan(byte fanType) => fanType switch
    {
        >= 20 and < 27 => 20,
        >= 27 and < 36 => 28,
        >= 36 and < 40 => 36,
        40 => 40,
        41 or 42 => 41,
        >= 43 and <= 50 => 43,
        > 50 and < 59 => 51,
        > 58 and < 63 => 59,
        63 => 63,
        126 or 127 => 126,
        _ => 0,
    };

    public static IReadOnlyList<RfDeviceRecord> ParseList(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4 || reply[0] != (byte)UsbCmd.SendRf)
        {
            throw new InvalidDataException("Not a device-list reply.");
        }

        var list = new List<RfDeviceRecord>();
        for (int i = 0, o = 4; i < reply[1] && o + Length <= reply.Length; i++, o += Length)
        {
            var r = reply.Slice(o, Length);
            if (r[41] != Tag)
            {
                continue;
            }

            var fans = r[19];
            list.Add(new RfDeviceRecord(RfMac.Read(r), RfMac.Read(r[6..]), new RfAddress(r[12], r[13]), r[18], fans >= 10 ? fans - 10 : fans,
                BinaryPrimitives.ReadUInt32BigEndian(r[20..]), r.Slice(24, 4).ToArray(), r.Slice(36, 4).ToArray(), r[40]));
        }

        return list;
    }
}
