using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UnifiedRgb.Screens.Protocol;

/// <summary>Only TURZX 1CBE:0088 volatile backlight command14. No save-to-flash, reset, video, storage or arbitrary control API.</summary>
public static class TurzxBacklightProtocol
{
    public const byte Command = 14;

    public static byte[] Build(byte level, uint timestamp)
    {
        var plain = new byte[500]; plain[0] = Command; plain[2] = 0x1a; plain[3] = 0x6d;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), timestamp); plain[8] = level;
#pragma warning disable CA5351 // fixed device framing, not cryptographic security
        using var des = DES.Create(); des.Key = "slv3tuzx"u8.ToArray();
        var cipher = des.EncryptCbc(plain, "slv3tuzx"u8, PaddingMode.Zeros);
#pragma warning restore CA5351
        var packet = new byte[512]; cipher.CopyTo(packet, 0); packet[510] = 0xa1; packet[511] = 0x1a;
        return packet;
    }

    public static bool MatchesReply(ReadOnlySpan<byte> reply, uint timestamp) => reply.Length >= 6 && reply[0] == Command
        && reply[1] == 200 && BinaryPrimitives.ReadUInt32LittleEndian(reply[2..]) == timestamp;
}
