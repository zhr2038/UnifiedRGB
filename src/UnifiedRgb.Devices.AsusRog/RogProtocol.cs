using System.Buffers.Binary;

namespace UnifiedRgb.Devices.AsusRog;

public sealed record RogPairedDevice(ushort ProductId, byte ReportId, byte NotifyReportId)
{
    public bool IsAzothX => ProductId == 0x1C25 && NotifyReportId == 4;
    public bool IsHarpeEdition20 => ProductId == 0x1D7C && NotifyReportId == 5;
}

public readonly record struct RogKeyColor(byte LedId, Rgb Color);
public sealed record RogMouseStaticState(byte Brightness, Rgb Color);
public sealed record RogKeyboardStaticState(byte Speed, byte Brightness, byte Direction, byte Width,
    Rgb Color, Rgb SecondColor, Rgb Background);
/// <summary>Known single-packet keyboard firmware effect; common payload positions match static RGB parameters.</summary>
public sealed record RogKeyboardEffectState(byte EffectType, byte Flags, RogKeyboardStaticState Parameters);
public sealed record RogKeyboardNativeState(byte EffectType, IReadOnlyList<string> Payloads)
{
    /// <summary>Original GET27 software-control flag when captured by the shared actor; absent in legacy journals.</summary>
    public byte? SoftwareControl { get; init; }
    public bool SameContent(RogKeyboardNativeState other) => EffectType == other.EffectType
        && Payloads.SequenceEqual(other.Payloads, StringComparer.Ordinal);
}

/// <summary>
/// Receiver-only framing established by cached local HID caps and pinned public protocol evidence.
/// Wired/BT, OLED, flash/profile, input, DPI, pairing changes and ambiguous firmware-effect layouts are deliberately absent.
/// Windows HID lengths INCLUDE the report id. A command echo proves communication, never physical display or persistence.
/// </summary>
public static class RogProtocol
{
    public const int ReportLength = 64;
    public const int KeysPerReport = 14;
    public const int MaximumKeys = 128;
    public const ushort VendorId = 0x0B05;

    public static byte[] PairingQuery() => Command(1, 0xA0, 0);

    public static byte[] KeyboardVersionQuery(byte reportId) => DeviceQuery(reportId, 0);

    public static byte[] KeyboardLayoutQuery(byte reportId) => DeviceQuery(reportId, 0x12);

    public static byte[] KeyboardStaticQuery(byte reportId) => KeyboardEffectQuery(reportId, 0);

    public static byte[] KeyboardEffectQuery(byte reportId, byte effectType)
    {
        RequireDeviceReportId(reportId);
        RequireSinglePacketEffect(effectType);
        var report = Command(reportId, 0x25, 0x0E);
        report[3] = effectType;
        return report;
    }

    public static RogKeyboardEffectState ParseKeyboardEffect(ReadOnlySpan<byte> reply, byte reportId, byte effectType)
    {
        RequireSinglePacketEffect(effectType);
        RogProtocol.RequireEcho(reply, reportId, 0x25, 0x0E);
        if (reply.Length != ReportLength || reply[3] != effectType || (reply[7] & ~0x11) != 0)
            throw new InvalidDataException("Keyboard firmware-effect snapshot does not match a known single-packet layout.");
        var normalized = reply.ToArray();
        normalized[3] = 0;
        normalized[7] = 0;
        return new(effectType, reply[7], ParseKeyboardStatic(normalized, reportId));
    }

    public static byte[] KeyboardEffectReport(byte reportId, RogKeyboardEffectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireSinglePacketEffect(state.EffectType);
        if ((state.Flags & ~0x11) != 0) throw new ArgumentException("Unknown keyboard colour flags.", nameof(state));
        var report = KeyboardStaticReport(reportId, state.Parameters);
        report[3] = state.EffectType;
        report[7] = state.Flags;
        return report;
    }

    private static void RequireSinglePacketEffect(byte effectType)
    {
        // Exact keyboard R enum (NOT the mouse effect enum): Reactive3, StarryNight6, Current8, RainDrop9.
        if (effectType is not (0 or 1 or 2 or 3 or 6 or 8 or 9)) throw new NotSupportedException("This keyboard effect needs a different restore format; no write is allowed.");
    }

    public static IReadOnlyList<int> KeyboardPayloadLengths(byte effectType) => effectType switch
    {
        0 or 1 or 2 or 3 or 6 or 8 or 9 => new[] { 14 },
        4 or 5 => new[] { 14, 16, 7 }, // Rainbow / Ripple, descending chunk indices2,1,0.
        7 => new[] { 14, 12 }, // Quicksand, descending1,0.
        _ => throw new NotSupportedException("Unknown keyboard effect layout; no partial restore is allowed."),
    };

    public static byte[] KeyboardNativeQuery(byte reportId, byte effectType, int packet)
    {
        RequireDeviceReportId(reportId);
        var layout = KeyboardPayloadLengths(effectType);
        if (packet < 0 || packet >= layout.Count) throw new ArgumentOutOfRangeException(nameof(packet));
        var report = Command(reportId, 0x25, 0x0E);
        report[3] = effectType;
        report[4] = (byte)(layout.Count - 1 - packet);
        return report;
    }

    public static string ParseKeyboardNativePayload(ReadOnlySpan<byte> reply, byte reportId, byte effectType, int packet)
    {
        var query = KeyboardNativeQuery(reportId, effectType, packet);
        RequireEcho(reply, reportId, 0x25, 0x0E);
        var lengths = KeyboardPayloadLengths(effectType);
        if (reply.Length != ReportLength || reply[3] != effectType || reply[4] != query[4]
            || reply[(5 + lengths[packet])..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Keyboard effect chunk does not match the complete known restore format.");
        var payload = reply.Slice(5, lengths[packet]);
        ValidateKeyboardNativePayload(effectType, packet, payload);
        return Convert.ToHexString(payload);
    }

    public static IReadOnlyList<byte[]> KeyboardNativeReports(byte reportId, RogKeyboardNativeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireDeviceReportId(reportId);
        var lengths = KeyboardPayloadLengths(state.EffectType);
        if (state.Payloads.Count != lengths.Count) throw new ArgumentException("All keyboard effect chunks are required.", nameof(state));
        var reports = new List<byte[]>(lengths.Count);
        for (var i = 0; i < lengths.Count; i++)
        {
            if (state.Payloads[i].Length != lengths[i] * 2) throw new ArgumentException("Keyboard chunk length mismatch.", nameof(state));
            var payload = Convert.FromHexString(state.Payloads[i]);
            ValidateKeyboardNativePayload(state.EffectType, i, payload);
            var report = Command(reportId, 0x51, 0x2C);
            report[3] = state.EffectType; report[4] = (byte)(lengths.Count - 1 - i);
            payload.CopyTo(report.AsSpan(5));
            reports.Add(report);
        }
        return reports.AsReadOnly();
    }

    public static RogKeyboardNativeState KeyboardColourFrame(Rgb colour, byte brightness, byte speed = 30)
    {
        var report = KeyboardStaticReport(2, new(speed, brightness, 0, 2, colour, Rgb.Black, Rgb.Black));
        return new(0, Array.AsReadOnly(new[] { Convert.ToHexString(report.AsSpan(5, 14)) }));
    }

    private static void ValidateKeyboardNativePayload(byte effectType, int packet, ReadOnlySpan<byte> payload)
    {
        if (packet == 0 && (payload[1] > 100 || (payload[2] & ~0x11) != 0))
            throw new InvalidDataException("Unknown keyboard brightness/colour flags; restoration is not safe.");
        if (effectType is 4 or 5)
        {
            if (packet == 0 && payload[5] > 7) throw new InvalidDataException("Too many keyboard colour stops.");
            var start = packet == 0 ? 6 : 0;
            var count = packet == 0 ? 2 : packet == 1 ? 4 : 1;
            for (var i = 0; i < count; i++)
            {
                var position = payload[start + i * 4];
                if (position > 100 && position != 255) throw new InvalidDataException("Unknown keyboard colour-stop position.");
            }
        }
    }

    public static RogKeyboardStaticState ParseKeyboardStatic(ReadOnlySpan<byte> reply, byte reportId)
    {
        RequireEcho(reply, reportId, 0x25, 0x0E);
        if (reply.Length != ReportLength || reply[3] != 0 || reply[4] != 0 || reply[6] > 100 || reply[7] != 0
            || reply[19..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Only the exact single-colour static keyboard record is restorable.");
        return new(reply[5], reply[6], reply[8], reply[9], new(reply[10], reply[11], reply[12]),
            new(reply[13], reply[14], reply[15]), new(reply[16], reply[17], reply[18]));
    }

    public static byte[] KeyboardStaticReport(byte reportId, RogKeyboardStaticState state)
    {
        RequireDeviceReportId(reportId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.Brightness, 100);
        var report = Command(reportId, 0x51, 0x2C);
        report[5] = state.Speed; report[6] = state.Brightness;
        report[8] = state.Direction; report[9] = state.Width;
        PutRgb(report.AsSpan(10), state.Color);
        PutRgb(report.AsSpan(13), state.SecondColor);
        PutRgb(report.AsSpan(16), state.Background);
        return report;
    }

    private static void PutRgb(Span<byte> destination, Rgb color)
    {
        destination[0] = color.R; destination[1] = color.G; destination[2] = color.B;
    }

    public static byte[] MouseLightingQuery(byte reportId, ushort area)
    {
        var report = DeviceQuery(reportId, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(3), area);
        return report;
    }

    /// <summary>Only the observed area0/static shape. Animated/directional/random fields are not silently discarded.</summary>
    public static RogMouseStaticState ParseMouseStatic(ReadOnlySpan<byte> reply, byte reportId)
    {
        RequireDeviceReportId(reportId);
        RequireEcho(reply, reportId, 0x12, 3);
        if (reply.Length != ReportLength || reply[3] != 0 || reply[4] != 0 || reply[5] != 0 || reply[6] > 100
            || reply[10..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Only an exact area0/static mouse snapshot with zero auxiliary fields can be restored.");
        return new(reply[6], new Rgb(reply[7], reply[8], reply[9]));
    }

    public static byte[] MouseStaticReport(byte reportId, RogMouseStaticState state)
    {
        RequireDeviceReportId(reportId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.Brightness, 100);
        var report = Command(reportId, 0x51, 0x28);
        report[6] = state.Brightness;
        report[7] = state.Color.R;
        report[8] = state.Color.G;
        report[9] = state.Color.B;
        return report;
    }

    public static IReadOnlyList<RogPairedDevice> ParsePairing(ReadOnlySpan<byte> reply)
    {
        RequireEcho(reply, 1, 0xA0, 0);
        if (reply.Length < 5 || reply[4] != 0 || reply[3] > 2 || reply.Length < 5 + reply[3] * 4)
            throw new InvalidDataException("Malformed ROG receiver pairing table.");
        var devices = new List<RogPairedDevice>(reply[3]);
        for (var i = 0; i < reply[3]; i++)
        {
            var at = 5 + i * 4;
            var product = BinaryPrimitives.ReadUInt16LittleEndian(reply[at..]);
            var rid = reply[at + 2];
            var notifyReportId = reply[at + 3];
            if (product == 0 || rid is not (2 or 3) || devices.Any(d => d.ReportId == rid)
                || product == 0x1C25 && notifyReportId != 4 || product == 0x1D7C && notifyReportId != 5)
                throw new InvalidDataException("Invalid or ambiguous ROG paired-device route.");
            devices.Add(new RogPairedDevice(product, rid, notifyReportId));
        }
        return devices.AsReadOnly();
    }

    public static bool IsKeyboardLayout(ReadOnlySpan<byte> reply, byte reportId) =>
        reply.Length >= 8 && reply[0] == reportId && reply[1] == 0x12 && reply[2] == 0x12
        && reply.Slice(3, 5).ContainsAnyExcept((byte)0);

    /// <summary>Entire frame is validated before any packet can be returned/sent. No missing last blue channel.</summary>
    public static IReadOnlyList<byte[]> KeyboardFrame(byte reportId, ReadOnlySpan<RogKeyColor> keys)
    {
        RequireDeviceReportId(reportId);
        if (keys.Length is < 1 or > MaximumKeys)
            throw new ArgumentOutOfRangeException(nameof(keys), "A verified keyboard frame needs 1..128 distinct LED records.");
        Span<bool> seen = stackalloc bool[256];
        seen.Clear();
        foreach (var key in keys)
        {
            if (seen[key.LedId]) throw new ArgumentException("Duplicate keyboard LED id.", nameof(keys));
            seen[key.LedId] = true;
        }
        var result = new List<byte[]>((keys.Length + KeysPerReport - 1) / KeysPerReport);
        for (var start = 0; start < keys.Length; start += KeysPerReport)
        {
            var count = Math.Min(KeysPerReport, keys.Length - start);
            var report = Command(reportId, 0xC0, 0x81);
            report[3] = (byte)count;
            for (var k = 0; k < count; k++)
            {
                var key = keys[start + k];
                var at = 5 + k * 4;
                report[at] = key.LedId;
                report[at + 1] = key.Color.R;
                report[at + 2] = key.Color.G;
                report[at + 3] = key.Color.B;
            }
            result.Add(report);
        }
        return result.AsReadOnly();
    }

    public static void RequireEcho(ReadOnlySpan<byte> reply, byte reportId, byte command, byte key)
    {
        if (reply.Length < 3 || reply[0] != reportId || reply[1] != command || reply[2] != key)
            throw new InvalidDataException($"ROG reply header {Convert.ToHexString(reply[..Math.Min(3, reply.Length)])} does not echo {reportId:X2}{command:X2}{key:X2}.");
    }

    private static byte[] DeviceQuery(byte reportId, byte key)
    {
        RequireDeviceReportId(reportId);
        return Command(reportId, 0x12, key);
    }

    private static void RequireDeviceReportId(byte reportId)
    {
        if (reportId is not (2 or 3)) throw new ArgumentOutOfRangeException(nameof(reportId), "Only paired-device report ids 2/3 are supported.");
    }

    private static byte[] Command(byte reportId, byte command, byte key)
    {
        var report = new byte[ReportLength];
        report[0] = reportId;
        report[1] = command;
        report[2] = key;
        return report;
    }
}
