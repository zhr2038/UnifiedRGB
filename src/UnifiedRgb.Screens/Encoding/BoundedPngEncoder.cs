using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Encoding;

/// <summary>Non-interlaced RGBA8 PNG, with a fixed two-pass lossless filter/compression search and a raster bound.</summary>
internal static class BoundedPngEncoder
{
    public const int MaxPixels = 1920 * 480;
    public const int MaxPasses = 2;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] Optimize(PixelFrame native, int desiredBytes, byte[]? referencePng = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        if ((long)native.Width * native.Height > MaxPixels) throw new ArgumentException("PNG optimization raster exceeds its bound.", nameof(native));
        ArgumentOutOfRangeException.ThrowIfLessThan(desiredBytes, 1);
        var metadata = Metadata(referencePng);
        var raw = Rgba(native);
        // Retain RGBA8/color type6: this adds no unproved firmware colour type.
        var adaptive = Encode(raw, native.Width, native.Height, metadata, adaptive: true);
        if (adaptive.Length <= desiredBytes) return adaptive;
        var paeth = Encode(raw, native.Width, native.Height, metadata, adaptive: false);
        return paeth.Length < adaptive.Length ? paeth : adaptive;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static byte[] Rgba(PixelFrame frame)
    {
        var raw = new byte[frame.ByteCount]; var p = frame.Pixels;
        for (var i = 0; i < raw.Length; i += 4)
        {
            var a = p[i + 3];
            raw[i] = Straight(p[i + 2], a); raw[i + 1] = Straight(p[i + 1], a); raw[i + 2] = Straight(p[i], a); raw[i + 3] = a;
        }
        return raw;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Straight(byte value, byte alpha) => alpha == 255 ? value : alpha == 0 ? (byte)0 : (byte)Math.Min(255, ((value * 255) + (alpha / 2)) / alpha);

    private static byte[] Encode(byte[] rgba, int width, int height, byte[] metadata, bool adaptive)
    {
        var stride = checked(width * 4); var filtered = new byte[checked((stride + 1) * height)];
        var trial = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = rgba.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> previous = y == 0 ? [] : rgba.AsSpan((y - 1) * stride, stride);
            var target = filtered.AsSpan((y * (stride + 1)) + 1, stride);
            if (!adaptive)
            {
                Filter(row, previous, target, 4); filtered[y * (stride + 1)] = 4; continue;
            }
            long best = long.MaxValue; byte chosen = 0;
            for (byte f = 0; f <= 4; f++)
            {
                var score = Filter(row, previous, trial, f);
                if (score < best) { best = score; chosen = f; trial.CopyTo(target); }
            }
            filtered[y * (stride + 1)] = chosen;
        }
        using var compressed = new MemoryStream(filtered.Length + 65536);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(filtered);
        if (compressed.Length > filtered.Length + 65536L) throw new InvalidDataException("PNG compressed output exceeds its bound.");
        using var png = new MemoryStream(checked((int)compressed.Length + 57 + metadata.Length));
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> ihdr = stackalloc byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr, width); BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(png, "IHDR"u8, ihdr); png.Write(metadata);
        Chunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length)); Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>Retain the WIC reference's colour interpretation and density, including the original chunk CRCs.</summary>
    private static byte[] Metadata(byte[]? reference)
    {
        if (reference is null) return [];
        if (reference.Length < 8 || !reference.AsSpan(0, 8).SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
            throw new InvalidDataException("Invalid reference PNG signature.");
        using var result = new MemoryStream(128);
        for (var offset = 8; offset <= reference.Length - 12;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(reference.AsSpan(offset, 4));
            if (length < 0 || length > reference.Length - offset - 12) throw new InvalidDataException("Invalid reference PNG chunk.");
            var tag = reference.AsSpan(offset + 4, 4);
            if (tag.SequenceEqual("IDAT"u8) || tag.SequenceEqual("IEND"u8)) break;
            if (tag.SequenceEqual("sRGB"u8) || tag.SequenceEqual("gAMA"u8) || tag.SequenceEqual("pHYs"u8))
            {
                // These fixed-size WIC chunks total 50 bytes; reject malformed or repeated oversized input before writing.
                if (length > 9 || result.Length + length + 12 > 128) throw new InvalidDataException("Reference PNG metadata exceeds its bound.");
                result.Write(reference.AsSpan(offset, length + 12));
            }
            offset += length + 12;
        }
        return result.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Filter(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, Span<byte> target, int filter)
    {
        long score = 0;
        for (var i = 0; i < row.Length; i++)
        {
            var left = i >= 4 ? row[i - 4] : 0; var up = previous.IsEmpty ? 0 : previous[i]; var corner = i >= 4 && !previous.IsEmpty ? previous[i - 4] : 0;
            var predictor = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, 4 => Paeth(left, up, corner), _ => throw new ArgumentOutOfRangeException(nameof(filter)) };
            var value = unchecked((byte)(row[i] - predictor)); target[i] = value; score += Math.Abs((int)unchecked((sbyte)value));
        }
        return score;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    internal static void Chunk(Stream output, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, data.Length); output.Write(bytes); output.Write(tag); output.Write(data);
        var crc = uint.MaxValue; foreach (var b in tag) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8); foreach (var b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, crc ^ uint.MaxValue); output.Write(bytes);
    }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++) { var c = i; for (var b = 0; b < 8; b++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; table[i] = c; }
        return table;
    }
}


