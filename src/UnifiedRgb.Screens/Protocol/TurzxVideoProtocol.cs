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
    public const int HeaderBlockBytes = 512;
    public const byte DataCommand = 121;
    public const byte QueueStatusCommand = 122;
    public const int MaxChunkBytes = 202752;
    public static byte[] StopPlayback(uint stamp) => Header(111, stamp);
    public static byte[] Prepare(uint stamp) => Header(112, stamp);
    public static byte[] Start(uint stamp) => Header(41, stamp);
    public static byte[] Cadence(uint stamp) => Header(15, stamp, 60); // consume available pictures; the HOST clock selects content
    public static byte[] QueueStatus(uint stamp) => Header(QueueStatusCommand, stamp);
    public static byte[] EndStream(uint stamp) => Header(123, stamp);
    public static byte[] StopImagePlayback(uint stamp) => Header(114, stamp);

    public static byte[] Data(ReadOnlySpan<byte> chunk, uint stamp)
    {
        if (chunk.IsEmpty || chunk.Length > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(chunk));
        var packet = new byte[HeaderBlockBytes + chunk.Length];
        WriteDataPacket(chunk, stamp, packet);
        return packet;
    }

    /// <summary>Writes a framed data packet into <paramref name="destination"/> with zero heap allocations.</summary>
    public static int WriteDataPacket(ReadOnlySpan<byte> chunk, uint stamp, Span<byte> destination)
    {
        if (chunk.IsEmpty || chunk.Length > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(chunk));
        var total = HeaderBlockBytes + chunk.Length;
        if (destination.Length < total) throw new ArgumentException("Packet destination buffer is too short.", nameof(destination));
        Span<byte> plain = stackalloc byte[500];
        InitPlain(plain, DataCommand, stamp);
        BinaryPrimitives.WriteInt32BigEndian(plain.Slice(8, 4), chunk.Length);
        ScreenProtocol.EncryptTurzxHeaderBlock(plain, destination[..HeaderBlockBytes]);
        chunk.CopyTo(destination[HeaderBlockBytes..]);
        return total;
    }

    /// <summary>Writes a 512-byte encrypted control header into <paramref name="destination"/> with zero heap allocations.</summary>
    public static int WriteControlHeader(byte command, uint stamp, Span<byte> destination, byte value = 0)
    {
        if (destination.Length < HeaderBlockBytes) throw new ArgumentException("Header destination buffer is too short.", nameof(destination));
        Span<byte> plain = stackalloc byte[500];
        InitPlain(plain, command, stamp);
        plain[8] = value;
        ScreenProtocol.EncryptTurzxHeaderBlock(plain, destination[..HeaderBlockBytes]);
        return HeaderBlockBytes;
    }

    private static byte[] Header(byte command, uint stamp, byte value = 0)
    {
        var block = new byte[HeaderBlockBytes];
        WriteControlHeader(command, stamp, block, value);
        return block;
    }

    private static void InitPlain(Span<byte> plain, byte command, uint stamp)
    {
        plain.Clear();
        plain[0] = command;
        plain[2] = 0x1A;
        plain[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.Slice(4, 4), stamp);
    }
}
