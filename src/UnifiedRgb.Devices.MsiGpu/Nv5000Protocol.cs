using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.MsiGpu;

/// <summary>
/// Live family decode of register 0xEC (NATIVE design §3.4, addendum 0xEC): <c>LEDType = d[0]</c>,
/// <c>ModuleType = (d[1] &lt;&lt; 8) | d[2]</c>, <c>FWVersion = d[3]</c>. The NV5000 indexed branch of the installed vendor code is
/// <c>ModuleType == 0x5000 || LEDType == 0x81</c>.
/// </summary>
public readonly record struct Nv5000Family(byte LedType, ushort ModuleType, byte FwVersion)
{
    /// <summary>The cached 2026-10-16 expectation (MS91 / 0x5000 / 09): recorded beside the live bytes, never forced.</summary>
    public const string CachedExpectationHex = "91500009";

    public bool IsNv5000 => ModuleType == 0x5000 || LedType == 0x81;

    public override string ToString() => $"LEDType=0x{LedType:X2} ModuleType=0x{ModuleType:X4} FWVersion=0x{FwVersion:X2}";

    /// <summary>
    /// Decodes a 0xEC read. Valid only when the NVAPI status is OK, the driver returned exactly 4 bytes and the bytes are
    /// plausible (neither all 0x00 nor all 0xFF); otherwise <paramref name="why"/> says which check failed.
    /// </summary>
    public static bool TryDecode(int status, int returnedSize, ReadOnlySpan<byte> data, out Nv5000Family family, out string why)
    {
        family = default;
        if (status != 0)
        {
            why = $"NVAPI status {status}";
            return false;
        }

        if (returnedSize != Nv5000.FamilyLength || data.Length != Nv5000.FamilyLength)
        {
            why = $"returned size {returnedSize}, expected {Nv5000.FamilyLength}";
            return false;
        }

        if (!data.ContainsAnyExcept((byte)0x00) || !data.ContainsAnyExcept((byte)0xFF))
        {
            why = $"implausible bytes {Convert.ToHexString(data)}";
            return false;
        }

        family = new Nv5000Family(data[0], (ushort)((data[1] << 8) | data[2]), data[3]);
        why = string.Empty;
        return true;
    }
}

/// <summary>Byte-exact NV5000 frames as they enter the chain: <c>[0x68, register, payload…]</c> (design §3.2).</summary>
public static class Nv5000Frames
{
    /// <summary>Session write 0x06 ← <paramref name="value"/> (0x00 enter, 0x01 release).</summary>
    public static byte[] Session(byte value) => [Nv5000.Device, Nv5000.SessionRegister, value];

    /// <summary>
    /// Frame write 0x04 ← <c>[0x01, (i, R, G, B, 0x01) × N]</c> for indices 0..N-1 of <paramref name="byIndex"/>; wire length
    /// 3 + 5N. N must be within 1..<see cref="Nv5000.EvidencedIndices"/> (the firewall checks the configured bound too).
    /// </summary>
    public static byte[] Frame(ReadOnlySpan<Rgb> byIndex)
    {
        if (byIndex.Length < 1 || byIndex.Length > Nv5000.EvidencedIndices)
        {
            throw new ArgumentOutOfRangeException(nameof(byIndex), byIndex.Length, $"an NV5000 frame carries 1..{Nv5000.EvidencedIndices} records");
        }

        var f = new byte[Nv5000.WireLength(byIndex.Length)];
        f[0] = Nv5000.Device;
        f[1] = Nv5000.FrameRegister;
        f[2] = Nv5000.FrameHeader;
        for (var i = 0; i < byIndex.Length; i++)
        {
            var o = 3 + (i * Nv5000.RecordLength);
            f[o] = (byte)i;
            f[o + 1] = byIndex[i].R;
            f[o + 2] = byIndex[i].G;
            f[o + 3] = byIndex[i].B;
            f[o + 4] = Nv5000.RecordTrailer;
        }

        return f;
    }

    /// <summary>All <paramref name="records"/> indices black: the hold-black and end-marker frame.</summary>
    public static byte[] Black(int records) => Frame(new Rgb[records]);
}
