using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UnifiedRgb.Screens.Protocol;

/// <summary>PID A021 volatile video stream only: WinUsbH2.PlayH264/GetBaseCmdBuf/Encrypt, independently cross-checked
/// against MIT lian-li-linux f55f3e5 crypto.rs/H2WinUsbLcd. No pump, fan, RGB, flash, storage, reset, brightness or rotation.</summary>
internal static class H2VideoProtocol
{
    public const byte DataCommand = 121;
    public const int MaxChunkBytes = 202752;
    public static byte[] Stop(uint stamp) => Header(123, stamp);
    public static byte[] QueueStatus(uint stamp) => Header(122, stamp);
    public static byte[] FrameRate(uint stamp, int fps)
    {
        if (fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(fps));
        var plain = Plain(15, stamp); plain[8] = (byte)fps; return Encrypt(plain);
    }
    public static byte[] Data(ReadOnlySpan<byte> chunk, uint stamp)
    {
        if (chunk.IsEmpty || chunk.Length > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(chunk));
        var plain = Plain(DataCommand, stamp);
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(8), chunk.Length);
        // byte12=0: continuous live stream, not end-of-file. byte13=0: original WinUsbH2 path, no device loop request.
        var packet = new byte[512 + chunk.Length]; Encrypt(plain).CopyTo(packet, 0); chunk.CopyTo(packet.AsSpan(512));return packet;
    }
    private static byte[] Header(byte command, uint stamp) => Encrypt(Plain(command, stamp));
    private static byte[] Plain(byte command, uint stamp)
    {
        var plain = new byte[500]; plain[0] = command; plain[2] = 0x1a; plain[3] = 0x6d;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), stamp); return plain;
    }
#pragma warning disable CA5351 // device framing, not cryptographic security
    private static byte[] Encrypt(byte[] plain)
    {
        using var des=DES.Create();var key="slv3tuzx"u8.ToArray();des.Key=key;
        var packet=new byte[512];des.EncryptCbc(plain,key,PaddingMode.PKCS7).CopyTo(packet,0);
        packet[510]=0xa1;packet[511]=0x1a;return packet;
    }
#pragma warning restore CA5351
}
