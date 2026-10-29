using System.Runtime.Intrinsics;

namespace UnifiedRgb.Screens.Encoding;

/// <summary>
/// BGRA8 to NV12 for <see cref="MfH264IntraEncoder"/>: byte-for-byte the historical scalar conversion (BT.709 integer coefficients,
/// full 0..255 or limited 16..235/16..240 range, +128 rounding, arithmetic right shifts, the same clamps, chroma from the rounded
/// 2x2 averages (sum + 2) / 4, interleaved U,V). Alpha is ignored. Only the evaluation order changes: 32 columns of a row pair at a
/// time in 256-bit integer vectors (every operation is the same exact integer arithmetic, clamps included), then the remaining
/// columns with the scalar formula; without 256-bit hardware acceleration every column takes the scalar formula. Writes straight
/// into the caller's buffer (the encoder's locked input sample), so no per-picture managed raster is allocated.
/// </summary>
internal static class Nv12Converter
{
    private const int Block = 32; // pixels per row and vector step: 4 x 8 BGRA pixels; 16 chroma pairs

    /// <param name="vector">Test seam: false forces the scalar formula for every column.</param>
    public static void Convert(ReadOnlySpan<byte> bgra, int width, int height, bool fullRange, Span<byte> nv12, bool vector = true)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 needs an even raster.");
        var pixels = checked(width * height);
        if (bgra.Length < checked(pixels * 4) || nv12.Length < pixels * 3 / 2)
            throw new ArgumentException("NV12 conversion buffers are too small.", nameof(nv12));
        var vectorWidth = vector && Vector256.IsHardwareAccelerated ? width - (width % Block) : 0;
        for (var y = 0; y < height; y += 2)
        {
            var row0 = bgra.Slice(y * width * 4, width * 4);
            var row1 = bgra.Slice((y + 1) * width * 4, width * 4);
            var luma0 = nv12.Slice(y * width, width);
            var luma1 = nv12.Slice((y + 1) * width, width);
            var chroma = nv12.Slice(pixels + (y / 2 * width), width);
            if (fullRange)
            {
                for (var x = 0; x < vectorWidth; x += Block) VectorBlock(row0, row1, luma0, luma1, chroma, x, Full);
                ScalarFull(row0, row1, luma0, luma1, chroma, vectorWidth, width);
            }
            else
            {
                for (var x = 0; x < vectorWidth; x += Block) VectorBlock(row0, row1, luma0, luma1, chroma, x, Limited);
                ScalarLimited(row0, row1, luma0, luma1, chroma, vectorWidth, width);
            }
        }
    }

    private static void ScalarFull(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, Span<byte> luma0, Span<byte> luma1, Span<byte> chroma, int from, int width)
    {
        for (var x = from; x < width; x += 2)
        {
            int sumR = 0, sumG = 0, sumB = 0;
            for (var xx = 0; xx < 2; xx++)
            {
                var i = (x + xx) * 4;
                int b = row0[i], g = row0[i + 1], r = row0[i + 2];
                luma0[x + xx] = (byte)Math.Clamp((54 * r + 183 * g + 19 * b + 128) >> 8, 0, 255);
                sumR += r; sumG += g; sumB += b;
                b = row1[i]; g = row1[i + 1]; r = row1[i + 2];
                luma1[x + xx] = (byte)Math.Clamp((54 * r + 183 * g + 19 * b + 128) >> 8, 0, 255);
                sumR += r; sumG += g; sumB += b;
            }
            var r4 = (sumR + 2) / 4; var g4 = (sumG + 2) / 4; var b4 = (sumB + 2) / 4;
            chroma[x] = (byte)Math.Clamp(128 + ((-29 * r4 - 99 * g4 + 128 * b4 + 128) >> 8), 0, 255);
            chroma[x + 1] = (byte)Math.Clamp(128 + ((128 * r4 - 116 * g4 - 12 * b4 + 128) >> 8), 0, 255);
        }
    }

    private static void ScalarLimited(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, Span<byte> luma0, Span<byte> luma1, Span<byte> chroma, int from, int width)
    {
        for (var x = from; x < width; x += 2)
        {
            int sumR = 0, sumG = 0, sumB = 0;
            for (var xx = 0; xx < 2; xx++)
            {
                var i = (x + xx) * 4;
                int b = row0[i], g = row0[i + 1], r = row0[i + 2];
                luma0[x + xx] = (byte)Math.Clamp(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8), 16, 235);
                sumR += r; sumG += g; sumB += b;
                b = row1[i]; g = row1[i + 1]; r = row1[i + 2];
                luma1[x + xx] = (byte)Math.Clamp(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8), 16, 235);
                sumR += r; sumG += g; sumB += b;
            }
            var r4 = (sumR + 2) / 4; var g4 = (sumG + 2) / 4; var b4 = (sumB + 2) / 4;
            chroma[x] = (byte)Math.Clamp(128 + ((-26 * r4 - 87 * g4 + 112 * b4 + 128) >> 8), 16, 240);
            chroma[x + 1] = (byte)Math.Clamp(128 + ((112 * r4 - 102 * g4 - 10 * b4 + 128) >> 8), 16, 240);
        }
    }

    /// <summary>The integer coefficients, offsets and clamps of one range (exactly the scalar formulas above).</summary>
    private readonly record struct Coefficients(int YR, int YG, int YB, int YOffset, int YMin, int YMax,
        int UR, int UG, int UB, int VR, int VG, int VB, int CMin, int CMax);

    private static readonly Coefficients Full = new(54, 183, 19, 0, 0, 255, -29, -99, 128, 128, -116, -12, 0, 255);
    private static readonly Coefficients Limited = new(47, 157, 16, 16, 16, 235, -26, -87, 112, 112, -102, -10, 16, 240);

    /// <summary>Columns x..x+31 of one row pair: 64 luma bytes and 16 interleaved U,V pairs.</summary>
    private static void VectorBlock(ReadOnlySpan<byte> row0, ReadOnlySpan<byte> row1, Span<byte> luma0, Span<byte> luma1, Span<byte> chroma,
        int x, in Coefficients c)
    {
        var at = x * 4;
        var a0 = Vector256.Create(row0.Slice(at, 32)).AsUInt32(); var a1 = Vector256.Create(row0.Slice(at + 32, 32)).AsUInt32();
        var a2 = Vector256.Create(row0.Slice(at + 64, 32)).AsUInt32(); var a3 = Vector256.Create(row0.Slice(at + 96, 32)).AsUInt32();
        var b0 = Vector256.Create(row1.Slice(at, 32)).AsUInt32(); var b1 = Vector256.Create(row1.Slice(at + 32, 32)).AsUInt32();
        var b2 = Vector256.Create(row1.Slice(at + 64, 32)).AsUInt32(); var b3 = Vector256.Create(row1.Slice(at + 96, 32)).AsUInt32();
        Luma(a0, a1, a2, a3, c).CopyTo(luma0.Slice(x, 32));
        Luma(b0, b1, b2, b3, c).CopyTo(luma1.Slice(x, 32));
        Vector256.Narrow(ChromaWords(a0, b0, a1, b1, c), ChromaWords(a2, b2, a3, b3, c)).AsByte().CopyTo(chroma.Slice(x, 32));
    }

    /// <summary>32 luma bytes from 32 BGRA pixels: clamp(offset + ((cr*r + cg*g + cb*b + 128) >> 8)), narrowed after the clamp.</summary>
    private static Vector256<byte> Luma(Vector256<uint> p0, Vector256<uint> p1, Vector256<uint> p2, Vector256<uint> p3, in Coefficients c) =>
        Vector256.Narrow(Vector256.Narrow(Luma(p0, c), Luma(p1, c)), Vector256.Narrow(Luma(p2, c), Luma(p3, c)));

    private static Vector256<uint> Luma(Vector256<uint> p, in Coefficients c)
    {
        var mask = Vector256.Create(0xFFu);
        var b = (p & mask).AsInt32();
        var g = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32();
        var r = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32();
        var y = Vector256.ShiftRightArithmetic((r * c.YR) + (g * c.YG) + (b * c.YB) + Vector256.Create(128), 8) + Vector256.Create(c.YOffset);
        return Vector256.Min(Vector256.Max(y, Vector256.Create(c.YMin)), Vector256.Create(c.YMax)).AsUInt32();
    }

    /// <summary>
    /// 8 chroma words (U | V &lt;&lt; 8) from two 8-pixel column pairs of the row pair (blocks 0-3 from p0/q0, 4-7 from p1/q1).
    /// Sums are exact: per lane B (and G) occupy bits 0-15 and R (and alpha) bits 16-31, at most 4 x 255 each, so no field carries.
    /// </summary>
    private static Vector256<uint> ChromaWords(Vector256<uint> p0, Vector256<uint> q0, Vector256<uint> p1, Vector256<uint> q1, in Coefficients c)
    {
        var (rb0, ga0) = ColumnSums(p0, q0);
        var (rb1, ga1) = ColumnSums(p1, q1);
        var rb = Vector256.Narrow(PairSums(rb0), PairSums(rb1));
        var ga = Vector256.Narrow(PairSums(ga0), PairSums(ga1));
        var low = Vector256.Create(0xFFFFu);
        var two = Vector256.Create(2);
        var r4 = Vector256.ShiftRightArithmetic(Vector256.ShiftRightLogical(rb, 16).AsInt32() + two, 2); // (sum + 2) / 4, sum >= 0
        var g4 = Vector256.ShiftRightArithmetic((ga & low).AsInt32() + two, 2);
        var b4 = Vector256.ShiftRightArithmetic((rb & low).AsInt32() + two, 2);
        var bias = Vector256.Create(128);
        var min = Vector256.Create(c.CMin); var max = Vector256.Create(c.CMax);
        var u = Vector256.Min(Vector256.Max(bias + Vector256.ShiftRightArithmetic((r4 * c.UR) + (g4 * c.UG) + (b4 * c.UB) + bias, 8), min), max);
        var v = Vector256.Min(Vector256.Max(bias + Vector256.ShiftRightArithmetic((r4 * c.VR) + (g4 * c.VG) + (b4 * c.VB) + bias, 8), min), max);
        return (u | Vector256.ShiftLeft(v, 8)).AsUInt32();
    }

    /// <summary>Vertical sums of 8 columns: (B | R &lt;&lt; 16) and (G | A &lt;&lt; 16), each field at most 2 x 255.</summary>
    private static (Vector256<uint> Rb, Vector256<uint> Ga) ColumnSums(Vector256<uint> top, Vector256<uint> bottom)
    {
        var mask = Vector256.Create(0x00FF00FFu);
        return ((top & mask) + (bottom & mask),
            (Vector256.ShiftRightLogical(top, 8) & mask) + (Vector256.ShiftRightLogical(bottom, 8) & mask));
    }

    /// <summary>Adds horizontally adjacent columns (lanes 2k and 2k+1): one 2x2 block per 64-bit lane, below 2^32.</summary>
    private static Vector256<ulong> PairSums(Vector256<uint> columns)
    {
        var wide = columns.AsUInt64();
        return (wide & Vector256.Create(0xFFFFFFFFul)) + Vector256.ShiftRightLogical(wide, 32);
    }
}
