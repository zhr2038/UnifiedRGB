using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UnifiedRgb.Screens.Protocol;

/// <summary>Volatile LCD-only command14: STRController.SetBright/WinUsb.Send for PID0006 and
/// H2Controller.SetBrightness/WinUsbH2.Send for PIDA021. Never the mixed pump/fan parameters command.</summary>
public static class LianLiBacklightProtocol
{
    public const byte Command = 14;

    public static byte[] Build(ScreenFamily family, byte level, uint timestamp)
    {
        if (family is not (ScreenFamily.TlLcd or ScreenFamily.H2)) throw new ArgumentOutOfRangeException(nameof(family));
        if (level > 100) throw new ArgumentOutOfRangeException(nameof(level), "Lian Li LCD backlight is a percentage (0..100).");
        var plain = new byte[family == ScreenFamily.TlLcd ? 504 : 500];
        plain[0] = Command; plain[2] = 0x1a; plain[3] = 0x6d;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), timestamp); plain[8] = level;
#pragma warning disable CA5351 // fixed device framing, not cryptographic security
        using var des = DES.Create(); des.Key = "slv3tuzx"u8.ToArray();
        var cipher = des.EncryptCbc(plain, "slv3tuzx"u8, PaddingMode.PKCS7);
#pragma warning restore CA5351
        var packet = new byte[512]; cipher.CopyTo(packet, 0);
        if (family == ScreenFamily.H2) { packet[510] = 0xa1; packet[511] = 0x1a; }
        return packet;
    }

    public static bool MatchesReply(ReadOnlySpan<byte> reply, uint timestamp) => TurzxBacklightProtocol.MatchesReply(reply, timestamp);
}
