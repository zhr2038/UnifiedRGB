using System.Text;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.EneDram;

/// <summary>One supported ENE controller version: its direct colour base and the config-table byte holding the LED count.</summary>
public sealed record EneVersion(string Name, ushort DirectBase, int LedCountOffset)
{
    /// <summary>Most LEDs whose direct range [base, base + 3n) stays below this version's effect colours (V1 5, V2 32).</summary>
    public int MaxLeds => EneRegisters.MaxDirectLeds(DirectBase);
}

/// <summary>
/// The version table of NATIVE design §5.1, by device-name string (upstream ENESMBusController.cpp, GPL reference, not
/// copied): only the names listed there; any other name is "not in table" and its controller is blocked.
/// </summary>
public static class EneVersions
{
    /// <summary>config[0x02]: the LED count of every listed version except 0107/1110.</summary>
    public const int LedCountOffset = 0x02;

    /// <summary>config[0x03]: the LED count of AUMA0-E6K5-0107 and AUMA0-E6K5-1110.</summary>
    public const int LedCountOffset0107 = 0x03;

    public static IReadOnlyList<EneVersion> Table { get; } =
    [
        new("LED-0116", EneRegisters.DirectV1, LedCountOffset),
        new("DIMM_LED-0102", EneRegisters.DirectV1, LedCountOffset),
        new("DIMM_LED-0103", EneRegisters.DirectV1, LedCountOffset),
        new("AUMA0-E8K4-0101", EneRegisters.DirectV1, LedCountOffset),
        new("AUDA0-E6K5-0101", EneRegisters.DirectV2, LedCountOffset),
        new("AUMA0-E6K5-0104", EneRegisters.DirectV2, LedCountOffset),
        new("AUMA0-E6K5-0105", EneRegisters.DirectV2, LedCountOffset),
        new("AUMA0-E6K5-0106", EneRegisters.DirectV2, LedCountOffset),
        new("AUMA0-E6K5-0107", EneRegisters.DirectV2, LedCountOffset0107),
        new("AUMA0-E6K5-1110", EneRegisters.DirectV2, LedCountOffset0107),
    ];

    /// <summary>Exact (ordinal) match of the name string up to its first NUL; null = not in table.</summary>
    public static EneVersion? Find(string name) => Table.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));

    /// <summary>The 16 name bytes as the reference compares them: up to the first NUL; non-printable bytes shown as '?'.</summary>
    public static string NameOf(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        var sb = new StringBuilder();
        foreach (var b in end >= 0 ? bytes[..end] : bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }

        return sb.ToString();
    }

    /// <summary>True when the six bytes at 0x1030 read "Micron" (the reference skips such a device).</summary>
    public static bool IsMicron(ReadOnlySpan<byte> bytes) => bytes.Length >= 6 && bytes[..6].SequenceEqual("Micron"u8);
}
