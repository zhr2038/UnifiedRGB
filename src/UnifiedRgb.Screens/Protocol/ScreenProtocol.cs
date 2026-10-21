using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UnifiedRgb.Screens.Protocol;

/// <summary>
/// Image-push framing for the three families, from the primary sources only (vendor il-WinUsb.cs Send/GetBaseCmdBuf/Encrypt,
/// il-WinUsbH2.cs PushImage/GetBaseCmdBuf/Encrypt, TURZX operations.py _send_png_bytes + transport.py). The plain header is
/// cmd at [0], magic 1A 6D at [2..4), LE32 request timestamp at [4..8) and BE32 image length at [8..12), DES-CBC with key = IV =
/// "slv3tuzx":
/// <list type="bullet">
/// <item>TL-LCD: 504 plain bytes, PKCS7 → 512 cipher bytes; the image follows at 512 inside a fixed 102400-byte buffer, zero
/// padded (the vendor reuses a stale buffer; we zero it deterministically). Image ≤ 101888 bytes.</item>
/// <item>H2: 500 plain bytes, PKCS7 → 504 cipher bytes in a 512-byte block ending A1 1A at 510/511; then the image (variable).</item>
/// <item>TURZX: 500 plain bytes, ZERO padding → 504 cipher bytes, same 512-byte block and trailer; PNG ≤ 524288 bytes.</item>
/// </list>
/// Only the image-push commands of <see cref="AllowedCommands"/> can be framed by THIS API. Typed H2VideoProtocol,
/// TurzxVideoProtocol, LianLiBacklightProtocol and TurzxBacklightProtocol separately implement their narrowly gated operations.
/// This image API never frames init, frame rate, brightness, rotation, pump/fan, video, storage, firmware, reset or PWM commands.
/// The TURZX framing is derived from turing-smart-screen-cli ffc652ab (MIT, Copyright (c) 2025 Study Hsueh); see
/// THIRD-PARTY-NOTICES.md in this project.
/// </summary>
public static class ScreenProtocol
{
    public const byte PushJpegCommand = 101;
    public const byte PushPngCommand = 102;
    public const int HeaderBlockBytes = 512;
    public const int TlFrameBytes = 102400;
    public const int TlMaxJpegBytes = TlFrameBytes - HeaderBlockBytes;
    public const int TurzxMaxPngBytes = 524288;

    /// <summary>Our own sanity bound for the variable-length H2 push (a 480x480 JPEG is far smaller).</summary>
    public const int H2MaxImageBytes = 2 * 1024 * 1024;

    private static readonly byte[] Key = "slv3tuzx"u8.ToArray();
    private static readonly byte[] TlCommands = [PushJpegCommand];
    private static readonly byte[] H2Commands = [PushJpegCommand, PushPngCommand];
    private static readonly byte[] TurzxCommands = [PushPngCommand];

    public static IReadOnlyList<byte> AllowedCommands(ScreenFamily family) => family switch
    {
        ScreenFamily.TlLcd => TlCommands,
        ScreenFamily.H2 => H2Commands,
        ScreenFamily.Turzx => TurzxCommands,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    public static int PlainHeaderBytes(ScreenFamily family) => family == ScreenFamily.TlLcd ? 504 : 500;

    public static int MaxImageBytes(ScreenFamily family) => family switch
    {
        ScreenFamily.TlLcd => TlMaxJpegBytes,
        ScreenFamily.H2 => H2MaxImageBytes,
        ScreenFamily.Turzx => TurzxMaxPngBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    public static byte[] BuildPlainHeader(ScreenFamily family, byte command, uint timestamp, int imageLength)
    {
        RequireAllowed(family, command);
        ArgumentOutOfRangeException.ThrowIfNegative(imageLength);
        var h = new byte[PlainHeaderBytes(family)];
        h[0] = command;
        h[2] = 0x1A;
        h[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4, 4), timestamp);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(8, 4), imageLength);
        return h;
    }

    /// <summary>Encrypts a plain header into the 512-byte block sent first.</summary>
    public static byte[] EncryptHeader(ScreenFamily family, ReadOnlySpan<byte> plain)
    {
        if (plain.Length != PlainHeaderBytes(family))
        {
            throw new ArgumentException($"{family} plain header must be {PlainHeaderBytes(family)} bytes.", nameof(plain));
        }

        RequireAllowed(family, plain[0]);
        using var des = CreateDes();
        var block = new byte[HeaderBlockBytes];
        if (family == ScreenFamily.TlLcd)
        {
            var n = des.EncryptCbc(plain, Key, block, PaddingMode.PKCS7); // 504 + a full 8-byte PKCS7 block = exactly 512
            return n == HeaderBlockBytes ? block : throw new InvalidOperationException($"TL header encrypted to {n} bytes, expected 512.");
        }

        var cipher = des.EncryptCbc(plain, Key, family == ScreenFamily.H2 ? PaddingMode.PKCS7 : PaddingMode.Zeros);
        if (cipher.Length != 504)
        {
            throw new InvalidOperationException($"{family} header encrypted to {cipher.Length} bytes, expected 504.");
        }

        cipher.CopyTo(block, 0);
        block[510] = 0xA1;
        block[511] = 0x1A;
        return block;
    }

    /// <summary>Inverse of <see cref="EncryptHeader"/> (self-check and tests); verifies the padding and the A1 1A trailer.</summary>
    public static byte[] DecryptHeader(ScreenFamily family, ReadOnlySpan<byte> block)
    {
        if (block.Length < HeaderBlockBytes)
        {
            throw new ArgumentException("A header block is 512 bytes.", nameof(block));
        }

        using var des = CreateDes();
        if (family == ScreenFamily.TlLcd)
        {
            return des.DecryptCbc(block[..HeaderBlockBytes], Key, PaddingMode.PKCS7);
        }

        if (block[510] != 0xA1 || block[511] != 0x1A || block[504..510].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new CryptographicException($"{family} header block lacks the zero gap and A1 1A trailer.");
        }

        if (family == ScreenFamily.H2)
        {
            return des.DecryptCbc(block[..504], Key, PaddingMode.PKCS7);
        }

        var plain = des.DecryptCbc(block[..504], Key, PaddingMode.None);
        if (plain.AsSpan(500).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new CryptographicException("TURZX header zero padding is not zero.");
        }

        return plain[..500];
    }

    /// <summary>
    /// The complete bytes of one image push. The command must be allowed for the family and match the image's magic (JPEG for
    /// 101, PNG for 102); empty or over-long images are refused.
    /// </summary>
    public static byte[] BuildImagePacket(ScreenFamily family, byte command, ReadOnlySpan<byte> image, uint timestamp)
    {
        RequireAllowed(family, command);
        if (image.IsEmpty || image.Length > MaxImageBytes(family))
        {
            throw new ArgumentOutOfRangeException(nameof(image), image.Length, $"{family} image must be 1..{MaxImageBytes(family)} bytes.");
        }

        var magicOk = command == PushJpegCommand ? IsJpeg(image) : IsPng(image);
        if (!magicOk)
        {
            throw new ArgumentException($"Command {command} needs a {(command == PushJpegCommand ? "JPEG" : "PNG")} image.", nameof(image));
        }

        var header = EncryptHeader(family, BuildPlainHeader(family, command, timestamp, image.Length));
        var packet = new byte[family == ScreenFamily.TlLcd ? TlFrameBytes : HeaderBlockBytes + image.Length];
        header.CopyTo(packet, 0);
        image.CopyTo(packet.AsSpan(HeaderBlockBytes));
        return packet;
    }

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static void RequireAllowed(ScreenFamily family, byte command)
    {
        if (!AllowedCommands(family).Contains(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, $"Command {command} is not an allowed image push for {family}.");
        }
    }

#pragma warning disable CA5351 // DES is the devices' fixed obfuscation of the request header (vendor/reference protocol), not our security
    private static DES CreateDes()
    {
        var des = DES.Create();
        des.Key = Key;
        return des;
    }
#pragma warning restore CA5351
}

/// <summary>
/// The LE32 request stamp of the header. Vendor TL/H2: milliseconds from <c>DateTime.Today.AddDays(-1)</c> captured when the
/// session starts to <c>DateTime.UtcNow</c> (the vendor subtracts the mixed kinds' raw ticks), low 32 bits. TURZX reference:
/// milliseconds since local midnight. It is a request stamp, never an animation clock.
/// </summary>
public sealed class ScreenTimestamp(ScreenFamily family, Func<DateTime> utcNow, Func<DateTime> localNow)
{
    private readonly DateTime _vendorEpoch = localNow().Date.AddDays(-1);

    public static ScreenTimestamp System(ScreenFamily family) => new(family, () => DateTime.UtcNow, () => DateTime.Now);

    public uint Next()
    {
        if (family == ScreenFamily.Turzx)
        {
            return (uint)(localNow().TimeOfDay.Ticks / TimeSpan.TicksPerMillisecond);
        }

        var ms = (long)Math.Round((utcNow().Ticks - _vendorEpoch.Ticks) / (double)TimeSpan.TicksPerMillisecond, MidpointRounding.ToEven);
        return unchecked((uint)ms);
    }
}
