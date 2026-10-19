using System.Buffers.Binary;
using System.Text;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.OpenRgb;

/// <summary>
/// OpenRGB SDK packet ids this product may send. Only these exist in code; the packet firewall rejects everything else
/// (profile save/load/delete, resize-zone and the mode-persisting packet are deliberately absent).
/// </summary>
public static class OpenRgbPacketIds
{
    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdated = 100; // server → client notification only
    public const uint UpdateLeds = 1050;
    public const uint UpdateZoneLeds = 1051;
    public const uint UpdateMode = 1101;

    /// <summary>Packet ids a client may send.</summary>
    public static IReadOnlyList<uint> Sendable { get; } =
        [RequestControllerCount, RequestControllerData, RequestProtocolVersion, SetClientName, UpdateLeds, UpdateZoneLeds, UpdateMode];
}

public readonly record struct OpenRgbHeader(uint DeviceIndex, uint PacketId, uint PayloadSize)
{
    public const int Size = 16;
    public static ReadOnlySpan<byte> Magic => "ORGB"u8;

    public static OpenRgbHeader Parse(ReadOnlySpan<byte> h)
    {
        if (h.Length < Size || !h[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Bad OpenRGB header magic.");
        }

        return new OpenRgbHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[12..]));
    }
}

public sealed record OpenRgbMode(
    string Name,
    int Value,
    uint Flags,
    uint SpeedMin,
    uint SpeedMax,
    uint BrightnessMin,
    uint BrightnessMax,
    uint ColorsMin,
    uint ColorsMax,
    uint Speed,
    uint Brightness,
    uint Direction,
    uint ColorMode,
    IReadOnlyList<uint> Colors);

public sealed record OpenRgbSegment(string Name, int Type, uint StartIndex, uint LedsCount);

/// <summary>Zone type: 0 single, 1 linear, 2 matrix.</summary>
public sealed record OpenRgbZone(
    string Name,
    int Type,
    uint LedsMin,
    uint LedsMax,
    uint LedsCount,
    uint MatrixHeight,
    uint MatrixWidth,
    IReadOnlyList<uint> MatrixMap,
    IReadOnlyList<OpenRgbSegment> Segments);

public sealed record OpenRgbLed(string Name, uint Value);

/// <summary>Controller data blob; <see cref="Type"/> uses OpenRGB device-type numbering (0 motherboard, 1 DRAM, 2 GPU, …).</summary>
public sealed record OpenRgbControllerData(
    int Type,
    string Name,
    string Vendor,
    string Description,
    string Version,
    string Serial,
    string Location,
    int ActiveMode,
    IReadOnlyList<OpenRgbMode> Modes,
    IReadOnlyList<OpenRgbZone> Zones,
    IReadOnlyList<OpenRgbLed> Leds,
    IReadOnlyList<uint> Colors);

/// <summary>Pure encoder/decoder for the OpenRGB SDK network protocol, versions 3 and 4 (little-endian).</summary>
public static class OpenRgbProtocol
{
    public const uint ClientProtocolVersion = 4;
    public const uint MinServerProtocolVersion = 3;

    public static byte[] Packet(uint deviceIndex, uint packetId, ReadOnlySpan<byte> payload)
    {
        var p = new byte[OpenRgbHeader.Size + payload.Length];
        OpenRgbHeader.Magic.CopyTo(p);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(p.AsSpan(OpenRgbHeader.Size));
        return p;
    }

    public static byte[] RequestControllerCount() => Packet(0, OpenRgbPacketIds.RequestControllerCount, []);

    public static byte[] RequestProtocolVersion(uint version = ClientProtocolVersion) =>
        Packet(0, OpenRgbPacketIds.RequestProtocolVersion, U32(version));

    public static byte[] RequestControllerData(uint index, uint protocol) =>
        Packet(index, OpenRgbPacketIds.RequestControllerData, U32(protocol));

    public static byte[] SetClientName(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        return Packet(0, OpenRgbPacketIds.SetClientName, bytes);
    }

    /// <summary>UpdateLeds: [u32 data_size][u16 count][count × (R,G,B,0)].</summary>
    public static byte[] UpdateLeds(uint index, ReadOnlySpan<byte> rgbTriplets)
    {
        var n = rgbTriplets.Length / 3;
        var size = 4 + 2 + (4 * n);
        var payload = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), checked((ushort)n));
        WriteColors(payload.AsSpan(6), rgbTriplets);
        return Packet(index, OpenRgbPacketIds.UpdateLeds, payload);
    }

    /// <summary>UpdateZoneLeds: [u32 data_size][u32 zone][u16 count][count × (R,G,B,0)].</summary>
    public static byte[] UpdateZoneLeds(uint index, uint zone, ReadOnlySpan<byte> rgbTriplets)
    {
        var n = rgbTriplets.Length / 3;
        var size = 4 + 4 + 2 + (4 * n);
        var payload = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), zone);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), checked((ushort)n));
        WriteColors(payload.AsSpan(10), rgbTriplets);
        return Packet(index, OpenRgbPacketIds.UpdateZoneLeds, payload);
    }

    public static int UpdateZoneLedsLength(int ledCount) => OpenRgbHeader.Size + 4 + 4 + 2 + (4 * ledCount);

    /// <summary>Allocation-free <see cref="UpdateZoneLeds(uint, uint, ReadOnlySpan{byte})"/> into a caller buffer; returns the packet length.</summary>
    public static int WriteUpdateZoneLeds(Span<byte> dst, uint index, uint zone, ReadOnlySpan<Rgb> colors)
    {
        var len = UpdateZoneLedsLength(colors.Length);
        if (dst.Length < len)
        {
            throw new ArgumentException("Destination too small.", nameof(dst));
        }

        var payloadSize = (uint)(len - OpenRgbHeader.Size);
        OpenRgbHeader.Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], index);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], OpenRgbPacketIds.UpdateZoneLeds);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[16..], payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[20..], zone);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[24..], checked((ushort)colors.Length));
        var o = 26;
        foreach (var c in colors)
        {
            dst[o] = c.R;
            dst[o + 1] = c.G;
            dst[o + 2] = c.B;
            dst[o + 3] = 0;
            o += 4;
        }

        return len;
    }

    /// <summary>UpdateMode: [u32 data_size][i32 mode index][mode block]. Selects a mode for this session only.</summary>
    public static byte[] UpdateMode(uint index, int modeIndex, OpenRgbMode mode, uint protocol)
    {
        ArgumentNullException.ThrowIfNull(mode);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(0u);
            w.Write(modeIndex);
            WriteMode(w, mode, protocol);
        }

        var payload = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
        return Packet(index, OpenRgbPacketIds.UpdateMode, payload);
    }

    public static uint ParseU32(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : throw new InvalidDataException("Payload too short.");

    public static OpenRgbControllerData ParseControllerData(ReadOnlySpan<byte> payload, uint protocol)
    {
        var r = new Reader(payload);
        var dataSize = r.U32();
        if (dataSize > payload.Length)
        {
            throw new InvalidDataException($"Controller data_size {dataSize} exceeds payload {payload.Length}.");
        }

        var type = r.I32();
        var name = r.Str();
        var vendor = protocol >= 1 ? r.Str() : string.Empty;
        var description = r.Str();
        var version = r.Str();
        var serial = r.Str();
        var location = r.Str();

        var modeCount = r.U16();
        var active = r.I32();
        var modes = new OpenRgbMode[modeCount];
        for (var i = 0; i < modeCount; i++)
        {
            var mName = r.Str();
            var value = r.I32();
            var flags = r.U32();
            var sMin = r.U32();
            var sMax = r.U32();
            uint bMin = 0, bMax = 0;
            if (protocol >= 3)
            {
                bMin = r.U32();
                bMax = r.U32();
            }

            var cMin = r.U32();
            var cMax = r.U32();
            var speed = r.U32();
            var brightness = protocol >= 3 ? r.U32() : 0;
            var direction = r.U32();
            var colorMode = r.U32();
            modes[i] = new OpenRgbMode(mName, value, flags, sMin, sMax, bMin, bMax, cMin, cMax, speed, brightness, direction, colorMode, r.U32List(r.U16()));
        }

        var zoneCount = r.U16();
        var zones = new OpenRgbZone[zoneCount];
        for (var i = 0; i < zoneCount; i++)
        {
            var zName = r.Str();
            var zType = r.I32();
            var lMin = r.U32();
            var lMax = r.U32();
            var lCount = r.U32();
            var matrixLen = r.U16();
            uint h = 0, wdt = 0;
            IReadOnlyList<uint> map = [];
            if (matrixLen > 0)
            {
                h = r.U32();
                wdt = r.U32();
                if ((2 + (h * wdt)) * 4 != matrixLen)
                {
                    throw new InvalidDataException($"Zone '{zName}' matrix length {matrixLen} does not match {wdt}×{h}.");
                }

                map = r.U32List((int)(h * wdt));
            }

            var segments = new List<OpenRgbSegment>();
            if (protocol >= 4)
            {
                var segCount = r.U16();
                for (var s = 0; s < segCount; s++)
                {
                    segments.Add(new OpenRgbSegment(r.Str(), r.I32(), r.U32(), r.U32()));
                }
            }

            zones[i] = new OpenRgbZone(zName, zType, lMin, lMax, lCount, h, wdt, map, segments);
        }

        var ledCount = r.U16();
        var leds = new OpenRgbLed[ledCount];
        for (var i = 0; i < ledCount; i++)
        {
            leds[i] = new OpenRgbLed(r.Str(), r.U32());
        }

        var colors = r.U32List(r.U16());
        return new OpenRgbControllerData(type, name, vendor, description, version, serial, location, active, modes, zones, leds, colors);
    }

    public static void WriteMode(BinaryWriter w, OpenRgbMode m, uint protocol)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        WriteStr(w, m.Name);
        w.Write(m.Value);
        w.Write(m.Flags);
        w.Write(m.SpeedMin);
        w.Write(m.SpeedMax);
        if (protocol >= 3)
        {
            w.Write(m.BrightnessMin);
            w.Write(m.BrightnessMax);
        }

        w.Write(m.ColorsMin);
        w.Write(m.ColorsMax);
        w.Write(m.Speed);
        if (protocol >= 3)
        {
            w.Write(m.Brightness);
        }

        w.Write(m.Direction);
        w.Write(m.ColorMode);
        w.Write(checked((ushort)m.Colors.Count));
        foreach (var c in m.Colors)
        {
            w.Write(c);
        }
    }

    /// <summary>uint16 length (including NUL) + ASCII bytes + NUL.</summary>
    public static void WriteStr(BinaryWriter w, string s)
    {
        ArgumentNullException.ThrowIfNull(w);
        var bytes = Encoding.ASCII.GetBytes((s ?? string.Empty) + "\0");
        w.Write(checked((ushort)bytes.Length));
        w.Write(bytes);
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static void WriteColors(Span<byte> dst, ReadOnlySpan<byte> rgb)
    {
        for (int i = 0, o = 0; i + 2 < rgb.Length; i += 3, o += 4)
        {
            dst[o] = rgb[i];
            dst[o + 1] = rgb[i + 1];
            dst[o + 2] = rgb[i + 2];
            dst[o + 3] = 0;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _p;

        public ushort U16()
        {
            Need(2);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_p..]);
            _p += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_p..]);
            _p += 4;
            return v;
        }

        public int I32() => unchecked((int)U32());

        public string Str()
        {
            int len = U16();
            Need(len);
            var s = _d.Slice(_p, len);
            _p += len;
            var nul = s.IndexOf((byte)0);
            return Encoding.ASCII.GetString(nul >= 0 ? s[..nul] : s);
        }

        public uint[] U32List(int count)
        {
            Need(count * 4);
            var a = new uint[count];
            for (var i = 0; i < count; i++)
            {
                a[i] = U32();
            }

            return a;
        }

        private readonly void Need(int n)
        {
            if (n < 0 || _p + n > _d.Length)
            {
                throw new InvalidDataException("Truncated OpenRGB controller data.");
            }
        }
    }
}
