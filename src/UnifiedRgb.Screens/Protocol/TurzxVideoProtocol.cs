using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UnifiedRgb.Screens.Protocol;

/// <summary>
/// Typed, volatile TURZX video-only controls. The existing raw image whitelist is NOT expanded. No arbitrary opcode,
/// orientation/backlight, device storage, persistent settings, reset or firmware operations are exposed.
/// Framing: pinned MIT TURZX reference; busy high/low-water marks cross-checked against the original .NET sender.
/// </summary>
internal static class TurzxVideoProtocol
{
    public const byte DataCommand = 121;
    public const int MaxChunkBytes = 202752;
    public static byte[] StopPlayback(uint stamp) => Header(111, stamp);
    public static byte[] Prepare(uint stamp) => Header(112, stamp);
    public static byte[] Start(uint stamp) => Header(41, stamp);
    public static byte[] Cadence(uint stamp) => Header(15, stamp, 60); // consume available pictures; the HOST clock selects content
    public static byte[] QueueStatus(uint stamp) => Header(122, stamp);
    public static byte[] EndStream(uint stamp) => Header(123, stamp);
    public static byte[] StopImagePlayback(uint stamp) => Header(114, stamp);

    public static byte[] Data(ReadOnlySpan<byte> chunk, uint stamp)
    {
        if (chunk.IsEmpty || chunk.Length > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(chunk));
        var plain = Plain(DataCommand, stamp);
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(8, 4), chunk.Length);
        var packet = new byte[512 + chunk.Length]; Encrypt(plain).CopyTo(packet, 0); chunk.CopyTo(packet.AsSpan(512));
        return packet;
    }

    private static byte[] Header(byte command, uint stamp, byte value = 0)
    {
        var plain = Plain(command, stamp); plain[8] = value; return Encrypt(plain);
    }
    private static byte[] Plain(byte command, uint stamp)
    {
        var plain = new byte[500]; plain[0] = command; plain[2] = 0x1A; plain[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4, 4), stamp); return plain;
    }
#pragma warning disable CA5351 // fixed device protocol obfuscation, not security
    private static byte[] Encrypt(byte[] plain)
    {
        using var des = DES.Create(); var key = "slv3tuzx"u8.ToArray(); des.Key = key;
        var block = new byte[512]; des.EncryptCbc(plain, key, PaddingMode.Zeros).CopyTo(block, 0);
        block[510] = 0xA1; block[511] = 0x1A; return block;
    }
#pragma warning restore CA5351
}
