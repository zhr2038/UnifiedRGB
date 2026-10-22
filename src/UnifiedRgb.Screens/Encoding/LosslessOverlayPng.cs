using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using UnifiedRgb.Screens.Protocol;

namespace UnifiedRgb.Screens.Encoding;

/// <summary>What <see cref="LosslessOverlayPng.Repack"/> made of a valid, supported input.</summary>
internal enum LosslessOverlayPngOutcome
{
    /// <summary><see cref="LosslessOverlayPngResult.Candidate"/> is the filter-NONE PNG, at most the requested budget long.</summary>
    Repacked,

    /// <summary>
    /// Both fixed zlib levels produced a PNG longer than the budget: <see cref="LosslessOverlayPngResult.Candidate"/> is null and
    /// nothing oversized is returned. <see cref="LosslessOverlayPngResult.Original"/> is still the validated input; it is a
    /// sendable fallback only when <see cref="LosslessOverlayPngResult.OriginalWithinBudget"/> is true.
    /// </summary>
    OverBudget,
}

/// <summary>The result of one <see cref="LosslessOverlayPng.Repack"/> call that passed validation (invalid input throws instead).</summary>
internal sealed class LosslessOverlayPngResult
{
    internal LosslessOverlayPngResult(LosslessOverlayPngOutcome outcome, byte[]? candidate, byte[] original, int budget, int candidateBytes,
        int zlibLevel, int width, int height, int[] inputFilterRows)
    {
        Outcome = outcome; Candidate = candidate; Original = original; Budget = budget; CandidateBytes = candidateBytes;
        ZlibLevel = zlibLevel; Width = width; Height = height; InputFilterRows = inputFilterRows;
    }

    public LosslessOverlayPngOutcome Outcome { get; }

    /// <summary>The repacked PNG: non-null exactly when <see cref="Outcome"/> is <see cref="LosslessOverlayPngOutcome.Repacked"/>.</summary>
    public byte[]? Candidate { get; }

    /// <summary>The input array itself (same reference; never copied or modified), fully validated.</summary>
    public byte[] Original { get; }

    /// <summary>The byte budget the candidate was held to.</summary>
    public int Budget { get; }

    /// <summary>Length of the smallest candidate built; above <see cref="Budget"/> when the outcome is OverBudget.</summary>
    public int CandidateBytes { get; }

    /// <summary>The zlib level (6, or 9 when level 6 did not fit) of the candidate counted in <see cref="CandidateBytes"/>.</summary>
    public int ZlibLevel { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Rows of the INPUT that used PNG filter types 0..4 (diagnostics; every candidate row uses type 0).</summary>
    public IReadOnlyList<int> InputFilterRows { get; }

    /// <summary>True when <see cref="Original"/> itself fits <see cref="Budget"/>.</summary>
    public bool OriginalWithinBudget => Original.Length <= Budget;
}

/// <summary>
/// Isolated candidate (not a product default and not a device-support claim): repacks a trusted, already encoded RGBA8 PNG — the
/// WIC output of <see cref="Media.WpfCodec.EncodePng"/> — so every scanline uses filter type 0 (NONE) and the IDAT stream is
/// recompressed with standard zlib. The original scanline bytes are unfiltered exactly, never round-tripped through a premultiplied
/// raster, so every straight RGBA value (RGB under alpha 0 included) is kept. Everything outside the IDAT run (IHDR, sRGB, gAMA,
/// pHYs and every other accepted chunk) is copied byte-for-byte, CRCs included, in its original position: same dimensions, colour
/// type 6, bit depth 8, no palette, quantization or resizing. The hypothesis is that a decoder spends less time unfiltering; that
/// is unmeasured on any device. Pure managed code with no shared mutable state: any thread, no STA, no disk, no cache.
/// </summary>
internal static class LosslessOverlayPng
{
    /// <summary>Largest raster in pixels (the TURZX canvas in either orientation); a width or height alone is at most <see cref="MaxSide"/>.</summary>
    public const int MaxPixels = 1920 * 480;
    public const int MaxSide = 1920;

    /// <summary>
    /// Largest accepted input. A stored (uncompressed) zlib encoding of the largest raster is about 3.7 MB, so every WIC output of
    /// an in-bound raster parses; anything longer is refused before any chunk is read.
    /// </summary>
    public const int MaxInputBytes = 4 << 20;

    /// <summary>Most chunks walked in one input (WIC writes IHDR, sRGB, gAMA, pHYs and IEND plus one IDAT per 64 KiB of zlib data).</summary>
    public const int MaxChunks = 1024;

    /// <summary>The two fixed zlib levels: 6 first, 9 only when level 6 does not fit the budget. Nothing is searched or trained.</summary>
    public const int PrimaryZlibLevel = 6;
    public const int FallbackZlibLevel = 9;

    /// <summary>Largest IDAT payload written: the largest WIC itself writes (64 KiB including the 12 chunk framing bytes).</summary>
    public const int MaxIdatPayload = 65536 - 12;

    private const int Bpp = 4;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>
    /// Ancillary chunks given a meaning here: whether they must precede IDAT, whether they may appear only once, and their fixed
    /// payload size for colour type 6 (0 = variable). All are copied unchanged; none depends on filtering or compression.
    /// </summary>
    private static readonly (uint Type, bool BeforeIdat, bool Once, int Length)[] KnownAncillary =
    [
        (Tag("sRGB"u8), true, true, 1), (Tag("iCCP"u8), true, true, 0), (Tag("gAMA"u8), true, true, 4), (Tag("cHRM"u8), true, true, 32),
        (Tag("sBIT"u8), true, true, 4), (Tag("cICP"u8), true, true, 4), (Tag("mDCV"u8), true, true, 24), (Tag("cLLI"u8), true, true, 8),
        (Tag("bKGD"u8), true, true, 6), (Tag("pHYs"u8), true, true, 9), (Tag("sPLT"u8), true, false, 0), (Tag("tIME"u8), false, true, 7),
        (Tag("tEXt"u8), false, false, 0), (Tag("zTXt"u8), false, false, 0), (Tag("iTXt"u8), false, false, 0),
    ];

    private const int Srgb = 0, Iccp = 1; // indices in KnownAncillary: the two are mutually exclusive

    /// <summary>
    /// Validates <paramref name="png"/> completely, then repacks it with filter type 0 on every scanline (see the class summary).
    /// </summary>
    /// <param name="png">A non-interlaced 8-bit RGBA (colour type 6) PNG; not modified, and returned as <see cref="LosslessOverlayPngResult.Original"/>.</param>
    /// <param name="maxOutputBytes">The candidate budget, 1..<see cref="ScreenProtocol.TurzxMaxPngBytes"/> (the protocol bound).</param>
    /// <returns>
    /// <see cref="LosslessOverlayPngOutcome.Repacked"/> with a candidate of at most <paramref name="maxOutputBytes"/>, or
    /// <see cref="LosslessOverlayPngOutcome.OverBudget"/> with no candidate. A result is only returned for input that passed every check.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="png"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputBytes"/> is outside 1..524288.</exception>
    /// <exception cref="NotSupportedException">
    /// A well-formed PNG outside this narrow profile: longer than <see cref="MaxInputBytes"/>; a colour type other than 6, a bit depth
    /// other than 8 or interlacing; a width or height above <see cref="MaxSide"/> or more than <see cref="MaxPixels"/> pixels; more
    /// than <see cref="MaxChunks"/> chunks; a PLTE or unknown critical chunk; an unknown ancillary chunk that is not safe to copy
    /// (APNG animation chunks included). The raster bound is checked before anything is allocated from IHDR.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Malformed or corrupt input: signature, chunk framing or type bytes; any CRC mismatch; IHDR not first or values the PNG
    /// specification forbids; misplaced, repeated or wrongly sized known chunks (sRGB, gAMA, pHYs, ...), sRGB with iCCP, tRNS or hIST
    /// in RGBA; a missing or non-contiguous IDAT run; IEND missing, non-empty or followed by any byte; an invalid zlib header or a
    /// preset dictionary; deflate data that is corrupt, truncated, ends before the IDAT data ends, or inflates to fewer or more bytes
    /// than IHDR declares (inflation stops one byte past the declared size); an Adler-32 mismatch; a filter type above 4.
    /// </exception>
    public static LosslessOverlayPngResult Repack(byte[] png, int maxOutputBytes = ScreenProtocol.TurzxMaxPngBytes)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOutputBytes, ScreenProtocol.TurzxMaxPngBytes);
        if (png.Length > MaxInputBytes) throw new NotSupportedException($"PNG input of {png.Length} B exceeds the {MaxInputBytes} B bound.");

        var layout = Walk(png);
        var stride = layout.Width * Bpp;
        var raw = Inflate(Concatenate(png, layout), (stride + 1) * layout.Height);
        var filters = Unfilter(raw, stride, layout.Height);

        // Everything before the first IDAT and after the last one is kept byte-for-byte; only the IDAT run is replaced.
        var prefix = png.AsSpan(0, layout.IdatStart); var suffix = png.AsSpan(layout.IdatEnd);
        int smallest = int.MaxValue, smallestLevel = 0;
        foreach (var level in (ReadOnlySpan<int>)[PrimaryZlibLevel, FallbackZlibLevel])
        {
            using var zlib = Deflate(raw, level);
            var idatChunks = (int)((zlib.Length + MaxIdatPayload - 1) / MaxIdatPayload);
            var total = checked(prefix.Length + suffix.Length + (int)zlib.Length + (12 * idatChunks));
            if (total < smallest) { smallest = total; smallestLevel = level; }
            if (total > maxOutputBytes) continue;

            var output = new byte[total];
            prefix.CopyTo(output);
            var at = prefix.Length;
            var data = zlib.GetBuffer().AsSpan(0, (int)zlib.Length);
            while (!data.IsEmpty)
            {
                var piece = data[..Math.Min(data.Length, MaxIdatPayload)];
                at += WriteChunk(output.AsSpan(at), "IDAT"u8, piece);
                data = data[piece.Length..];
            }
            suffix.CopyTo(output.AsSpan(at));
            return new(LosslessOverlayPngOutcome.Repacked, output, png, maxOutputBytes, total, level, layout.Width, layout.Height, filters);
        }

        return new(LosslessOverlayPngOutcome.OverBudget, null, png, maxOutputBytes, smallest, smallestLevel, layout.Width, layout.Height, filters);
    }

    /// <summary>IHDR dimensions and the contiguous IDAT run: first IDAT start, last IDAT end, total payload bytes.</summary>
    private readonly record struct Layout(int Width, int Height, int IdatStart, int IdatEnd, int IdatBytes);

    /// <summary>One bounded pass over every chunk: framing, CRC, IHDR profile, order and multiplicity; no allocation from IHDR.</summary>
    private static Layout Walk(byte[] png)
    {
        if (png.Length < Signature.Length + 25 + 12 + 12 || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("Not a complete PNG: signature or minimum length missing.");
        int width = 0, height = 0, idatStart = -1, idatEnd = -1, idatBytes = 0;
        var seen = new bool[KnownAncillary.Length];
        for (int offset = Signature.Length, chunks = 1; ; chunks++)
        {
            if (png.Length - offset < 12) throw new InvalidDataException($"PNG truncated at offset {offset}: no complete chunk (IEND missing).");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            if (length > (uint)(png.Length - offset - 12)) throw new InvalidDataException($"PNG chunk at offset {offset} declares {length} B beyond the input.");
            var typeBytes = png.AsSpan(offset + 4, 4);
            foreach (var c in typeBytes)
            {
                if (!char.IsAsciiLetter((char)c)) throw new InvalidDataException($"PNG chunk at offset {offset} has an invalid type.");
            }
            var data = png.AsSpan(offset + 8, (int)length);
            if (Crc(typeBytes, data) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + (int)length)))
                throw new InvalidDataException($"PNG {Name(png, offset)} chunk at offset {offset} fails its CRC.");
            if (chunks > MaxChunks) throw new NotSupportedException($"PNG has more than {MaxChunks} chunks.");
            var type = Tag(typeBytes);
            var next = offset + 12 + (int)length;

            if (chunks == 1)
            {
                if (type != Tag("IHDR"u8) || length != 13) throw new InvalidDataException("PNG does not start with a 13-byte IHDR.");
                (width, height) = Header(data);
            }
            else if (type == Tag("IHDR"u8)) throw new InvalidDataException("PNG has a second IHDR.");
            else if (type == Tag("IDAT"u8))
            {
                // Every later IDAT must start exactly where the previous one ended: one contiguous run.
                if (idatEnd >= 0 && idatEnd != offset) throw new InvalidDataException("PNG IDAT chunks are not contiguous.");
                if (idatStart < 0) idatStart = offset;
                idatEnd = next; idatBytes += (int)length;
            }
            else if (type == Tag("IEND"u8))
            {
                if (length != 0) throw new InvalidDataException("PNG IEND is not empty.");
                if (idatStart < 0) throw new InvalidDataException("PNG has no IDAT.");
                if (next != png.Length) throw new InvalidDataException($"PNG has {png.Length - next} B after IEND.");
                return new(width, height, idatStart, idatEnd, idatBytes);
            }
            else if (type == Tag("PLTE"u8)) throw new NotSupportedException("PNG PLTE (a suggested palette in RGBA) is not supported.");
            else if ((typeBytes[0] & 0x20) == 0) throw new NotSupportedException($"PNG has an unknown critical chunk {Name(png, offset)}.");
            else if (type == Tag("tRNS"u8) || type == Tag("hIST"u8)) throw new InvalidDataException($"PNG {Name(png, offset)} is not allowed in RGBA.");
            else if (Known(type) is var known and >= 0)
            {
                var rule = KnownAncillary[known];
                if (rule.BeforeIdat && idatStart >= 0) throw new InvalidDataException($"PNG {Name(png, offset)} follows IDAT.");
                if (rule.Once && seen[known]) throw new InvalidDataException($"PNG has a second {Name(png, offset)}.");
                if (rule.Length != 0 && length != rule.Length) throw new InvalidDataException($"PNG {Name(png, offset)} has {length} B, not {rule.Length}.");
                seen[known] = true;
                if (seen[Srgb] && seen[Iccp]) throw new InvalidDataException("PNG has both sRGB and iCCP.");
            }
            // PNG chunk naming: an unknown ancillary chunk may be copied unchanged only with the reserved bit clear and safe-to-copy set.
            else if ((typeBytes[2] & 0x20) != 0 || (typeBytes[3] & 0x20) == 0)
                throw new NotSupportedException($"PNG ancillary chunk {Name(png, offset)} is unknown and not safe to copy beside new image data.");
            offset = next;
        }
    }

    /// <summary>The IHDR profile; its dimensions are bounded here, before any allocation depends on them.</summary>
    private static (int Width, int Height) Header(ReadOnlySpan<byte> ihdr)
    {
        var width = BinaryPrimitives.ReadUInt32BigEndian(ihdr); var height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        byte depth = ihdr[8], colour = ihdr[9], compression = ihdr[10], filter = ihdr[11], interlace = ihdr[12];
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue) throw new InvalidDataException("PNG IHDR has an invalid width or height.");
        var validDepth = colour switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => depth is 8 or 16,
            _ => false,
        };
        if (!validDepth || compression != 0 || filter != 0 || interlace > 1)
            throw new InvalidDataException("PNG IHDR has an invalid colour type, bit depth, compression, filter or interlace method.");
        if (colour != 6 || depth != 8) throw new NotSupportedException($"PNG colour type {colour} at {depth} bits is not supported; only 8-bit RGBA.");
        if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not supported.");
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new NotSupportedException($"PNG raster {width}x{height} exceeds the {MaxSide}-pixel side or {MaxPixels}-pixel bound.");
        return ((int)width, (int)height); // so (4 * width + 1) * height <= 3,688,320: no later product can overflow
    }

    /// <summary>The payloads of the validated, contiguous IDAT run joined into one zlib stream (at most the input length).</summary>
    private static byte[] Concatenate(byte[] png, Layout layout)
    {
        var zlib = new byte[layout.IdatBytes];
        for (int offset = layout.IdatStart, at = 0; offset < layout.IdatEnd;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            png.AsSpan(offset + 8, length).CopyTo(zlib.AsSpan(at));
            at += length; offset += 12 + length;
        }
        return zlib;
    }

    /// <summary>Exactly <paramref name="expected"/> bytes from one complete zlib stream that ends exactly where <paramref name="zlib"/> ends.</summary>
    private static byte[] Inflate(byte[] zlib, int expected)
    {
        if (zlib.Length < 6) throw new InvalidDataException("PNG zlib stream is shorter than its header and checksum.");
        int cmf = zlib[0], flg = zlib[1];
        if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0) throw new InvalidDataException("PNG zlib header is invalid.");
        if ((flg & 0x20) != 0) throw new InvalidDataException("PNG zlib stream uses a preset dictionary.");

        var raw = GC.AllocateUninitializedArray<byte>(expected);
        var reader = new ExactEndReader(zlib);
        int read; bool excess;
        try
        {
            using var inflater = new ZLibStream(reader, CompressionMode.Decompress);
            read = inflater.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
            Span<byte> probe = stackalloc byte[1]; // inflation stops one byte past the declared size
            excess = read == raw.Length && inflater.Read(probe) != 0;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("PNG zlib data is corrupt.", ex);
        }
        if (read < raw.Length) throw new InvalidDataException($"PNG zlib data inflates to {read} B, fewer than the {expected} B IHDR declares.");
        if (excess) throw new InvalidDataException($"PNG zlib data inflates to more than the {expected} B IHDR declares.");

        // ZLibStream reports neither a stream cut inside its deflate end or checksum nor bytes after the stream; these two do.
        if (reader.Consumed != zlib.Length) throw new InvalidDataException($"PNG zlib stream ends {zlib.Length - reader.Consumed} B before the IDAT data.");
        if (Adler32(raw) != BinaryPrimitives.ReadUInt32BigEndian(zlib.AsSpan(zlib.Length - 4))) throw new InvalidDataException("PNG zlib data is truncated or fails its Adler-32.");
        return raw;
    }

    /// <summary>Reverses filter types 0..4 in place (4 bytes per pixel), sets every row's type to 0, and counts the input's types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int[] Unfilter(byte[] raw, int stride, int height)
    {
        var counts = new int[5];
        var zero = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var start = y * (stride + 1);
            var filter = raw[start];
            if (filter > 4) throw new InvalidDataException($"PNG row {y} has filter type {filter}.");
            counts[filter]++;
            raw[start] = 0;
            var row = raw.AsSpan(start + 1, stride);
            ReadOnlySpan<byte> up = y == 0 ? zero : raw.AsSpan(start - stride, stride); // the row above, already reconstructed
            switch (filter)
            {
                case 1:
                    for (var i = Bpp; i < row.Length; i++) row[i] += row[i - Bpp];
                    break;
                case 2:
                    for (var i = 0; i < row.Length; i++) row[i] += up[i];
                    break;
                case 3:
                    for (var i = 0; i < Bpp; i++) row[i] += (byte)(up[i] >> 1);
                    for (var i = Bpp; i < row.Length; i++) row[i] += (byte)((row[i - Bpp] + up[i]) >> 1);
                    break;
                case 4:
                    for (var i = 0; i < Bpp; i++) row[i] += up[i]; // Paeth with no left neighbour predicts the byte above
                    for (var i = Bpp; i < row.Length; i++) row[i] += Paeth(row[i - Bpp], up[i], up[i - Bpp]);
                    break;
            }
        }
        return counts;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Paeth(int a, int b, int c)
    {
        var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    /// <summary>One standard zlib stream (32 KiB window, default strategy) at a fixed level.</summary>
    private static MemoryStream Deflate(byte[] raw, int level)
    {
        var compressed = new MemoryStream((raw.Length / 8) + 1024);
        using (var zlib = new ZLibStream(compressed, new ZLibCompressionOptions { CompressionLevel = level, CompressionStrategy = ZLibCompressionStrategy.Default }, leaveOpen: true))
            zlib.Write(raw);
        return compressed;
    }

    private static int WriteChunk(Span<byte> target, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteInt32BigEndian(target, data.Length);
        type.CopyTo(target[4..]); data.CopyTo(target[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(target[(8 + data.Length)..], Crc(type, data));
        return 12 + data.Length;
    }

    private static uint Tag(ReadOnlySpan<byte> type) => BinaryPrimitives.ReadUInt32BigEndian(type);

    private static string Name(byte[] png, int offset) => System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);

    private static int Known(uint type)
    {
        for (var i = 0; i < KnownAncillary.Length; i++)
        {
            if (KnownAncillary[i].Type == type) return i;
        }
        return -1;
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return crc ^ uint.MaxValue;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++) { var c = i; for (var b = 0; b < 8; b++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; table[i] = c; }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        while (!data.IsEmpty)
        {
            var block = data[..Math.Min(data.Length, 5552)]; // the longest run before b can overflow 32 bits
            foreach (var x in block) { a += x; b += a; }
            a %= 65521; b %= 65521; data = data[block.Length..];
        }
        return (b << 16) | a;
    }

    /// <summary>
    /// Hands the zlib stream to the inflater, but never its final byte together with others. The inflater asks for input only while
    /// its stream is unfinished, so <see cref="Consumed"/> reaches the length only if the stream did not end before the last byte.
    /// </summary>
    private sealed class ExactEndReader(byte[] data) : Stream
    {
        public int Consumed { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            var available = Consumed < data.Length - 1 ? data.Length - 1 - Consumed : data.Length - Consumed;
            var count = Math.Min(buffer.Length, available);
            data.AsSpan(Consumed, count).CopyTo(buffer);
            Consumed += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
