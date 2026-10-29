using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Measured Azoth X USB/report0 OLED path. No storage, firmware, pairing or input-remapping commands.</summary>
public static class RogWiredOledProtocol
{
    public const int PixelBytes = 6656;
    public const string LegacyFirmwareHeader = "001200000024000100";

    /// <summary>Format gate for an explicitly verified local pin; it never discovers or adopts firmware.</summary>
    public static bool IsConfiguredFirmwareHeader(string? header) => header is { Length: 18 }
        && header.All(char.IsAsciiHexDigit) && header.StartsWith("0012000000", StringComparison.OrdinalIgnoreCase)
        && !header.AsSpan(10).SequenceEqual("00000000");
    public static string DeviceKey(HidCollectionInfo collection) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes((collection ?? throw new ArgumentNullException(nameof(collection))).Path.ToUpperInvariant())));

    public static bool Matches(HidCollectionInfo c) => c is { Vid: 0x0B05, Pid: 0x1C24, Interface: 1,
        UsagePage: 0xFF00, Usage: 1, InputReportLength: 65, OutputReportLength: 65, FeatureReportLength: 0, Error: null };

    public static byte[] Query(byte command, byte key)
    {
        if ((command, key) is not ((0x12, 0) or (0x21, 0) or (0x23, 2) or (0x23, 3)))
            throw new ArgumentException("Not an OLED preflight getter.");
        var p = new byte[65]; p[1] = command; p[2] = key; return p;
    }

    public static byte[] Preset(byte originalSelector)
    {
        if (originalSelector != 3) throw new NotSupportedException("Only the measured same-selector3 recovery is supported.");
        var p = new byte[65]; p[1] = 0x61; p[5] = originalSelector; return p;
    }

    public static byte[] Brightness(byte brightness)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brightness, 100);
        var p = new byte[65]; p[1] = 0x68; p[5] = brightness; return p;
    }

    public static byte[] Renew() { var p = new byte[65]; p[1] = 0x67; p[2] = 2; return p; }

    public static IReadOnlyList<byte[]> Pack(ReadOnlySpan<byte> pixels) => Pack(pixels, 60);

    /// <summary>USB carries 60 pixel bytes per chunk; the verified RF backend carries 59.</summary>
    public static IReadOnlyList<byte[]> Pack(ReadOnlySpan<byte> pixels, int payloadCapacity)
    {
        if (pixels.Length != PixelBytes) throw new ArgumentException("Exactly208x64 packed4bit pixels required.", nameof(pixels));
        if (payloadCapacity is not (59 or 60)) throw new ArgumentOutOfRangeException(nameof(payloadCapacity), "Only verified 59/60-byte OLED payloads are supported.");
        var chunks = (PixelBytes + payloadCapacity - 1) / payloadCapacity;
        var begin = new byte[65]; begin[1] = 0x67;
        BinaryPrimitives.WriteUInt16LittleEndian(begin.AsSpan(7), (ushort)chunks);
        BinaryPrimitives.WriteUInt16LittleEndian(begin.AsSpan(9), 208);
        BinaryPrimitives.WriteUInt16LittleEndian(begin.AsSpan(11), 64);
        var packets = new List<byte[]>(chunks + 1) { begin };
        for (var i = 0; i < chunks; i++)
        {
            var p = new byte[65]; p[1] = 0x67; p[2] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), (ushort)(chunks - 1 - i));
            pixels.Slice(i * payloadCapacity, Math.Min(payloadCapacity, pixels.Length - i * payloadCapacity)).CopyTo(p.AsSpan(5)); packets.Add(p);
        }
        return packets;
    }

    public static void RequireAck(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> request)
    {
        if (reply.Length != 65 || request.Length != 65 || !reply[..5].SequenceEqual(request[..5])
            || reply[5..].ContainsAnyExcept((byte)0)) throw new InvalidDataException("OLED acknowledgement changed; no retry.");
    }

    public static void RequireRenewalReply(ReadOnlySpan<byte> reply) => RequireAck(reply, Renew());
}
