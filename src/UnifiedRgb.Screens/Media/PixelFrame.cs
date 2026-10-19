using System.Runtime.InteropServices;

namespace UnifiedRgb.Screens.Media;

/// <summary>A premultiplied BGRA (Pbgra32) raster; thread-agnostic plain bytes.</summary>
internal sealed class PixelFrame
{
    public PixelFrame(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if ((long)width * height > 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "raster too large");
        }

        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
        {
            throw new ArgumentException("pixel buffer size mismatch", nameof(pixels));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    public int ByteCount => Pixels.Length;

    public static PixelFrame Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var f = new PixelFrame(width, height);
        var px = (uint)((a << 24) | ((r * a / 255) << 16) | ((g * a / 255) << 8) | (b * a / 255));
        MemoryMarshal.Cast<byte, uint>(f.Pixels.AsSpan()).Fill(px);
        return f;
    }

    public PixelFrame Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>B, G, R, A of one pixel (tests and probes).</summary>
    public (byte B, byte G, byte R, byte A) At(int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }
}

/// <summary>Deterministic managed raster operations on premultiplied BGRA.</summary>
internal static class PixelOps
{
    private const int WeightBits = 14;
    private const int WeightOne = 1 << WeightBits;

    /// <summary>Separable tent-filter resample: bilinear when enlarging, a radius-1/scale area-like average when reducing.</summary>
    public static PixelFrame Resize(PixelFrame src, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (src.Width == width && src.Height == height)
        {
            return src.Clone();
        }

        // order the passes so the intermediate raster is the smaller one
        var horizontalFirst = (long)width * src.Height <= (long)src.Width * height;
        if (horizontalFirst)
        {
            var tmp = new PixelFrame(width, src.Height);
            Pass(src, tmp, horizontal: true);
            var dst = new PixelFrame(width, height);
            Pass(tmp, dst, horizontal: false);
            return dst;
        }
        else
        {
            var tmp = new PixelFrame(src.Width, height);
            Pass(src, tmp, horizontal: false);
            var dst = new PixelFrame(width, height);
            Pass(tmp, dst, horizontal: true);
            return dst;
        }
    }

    /// <summary>Size of <paramref name="srcW"/>x<paramref name="srcH"/> fitted to the canvas: cover fills it (crop), contain fits inside.</summary>
    public static (int Width, int Height) FitSize(int srcW, int srcH, int canvasW, int canvasH, bool cover)
    {
        var sx = canvasW / (double)srcW;
        var sy = canvasH / (double)srcH;
        var s = cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var w = Math.Max(1, (int)Math.Round(srcW * s));
        var h = Math.Max(1, (int)Math.Round(srcH * s));
        return cover ? (Math.Max(w, canvasW), Math.Max(h, canvasH)) : (Math.Min(w, canvasW), Math.Min(h, canvasH));
    }

    /// <summary>Source-over of premultiplied <paramref name="src"/> at (x, y), clipped to <paramref name="dst"/>.</summary>
    public static void BlendOver(PixelFrame dst, PixelFrame src, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(dst.Width, x + src.Width);
        var y1 = Math.Min(dst.Height, y + src.Height);
        var d = dst.Pixels;
        var s = src.Pixels;
        for (var yy = y0; yy < y1; yy++)
        {
            var di = ((yy * dst.Width) + x0) * 4;
            var si = (((yy - y) * src.Width) + (x0 - x)) * 4;
            for (var xx = x0; xx < x1; xx++, di += 4, si += 4)
            {
                int a = s[si + 3];
                if (a == 255)
                {
                    d[di] = s[si];
                    d[di + 1] = s[si + 1];
                    d[di + 2] = s[si + 2];
                    d[di + 3] = 255;
                }
                else if (a != 0)
                {
                    var inv = 255 - a;
                    d[di] = (byte)(s[si] + (((d[di] * inv) + 127) / 255));
                    d[di + 1] = (byte)(s[si + 1] + (((d[di + 1] * inv) + 127) / 255));
                    d[di + 2] = (byte)(s[si + 2] + (((d[di + 2] * inv) + 127) / 255));
                    d[di + 3] = (byte)(a + (((d[di + 3] * inv) + 127) / 255));
                }
            }
        }
    }

    /// <summary>Exact clockwise rotation by 0/90/180/270 degrees (90/270 swap the dimensions).</summary>
    public static PixelFrame Rotate(PixelFrame src, int degreesClockwise)
    {
        ArgumentNullException.ThrowIfNull(src);
        var w = src.Width;
        var h = src.Height;
        var s = MemoryMarshal.Cast<byte, uint>(src.Pixels.AsSpan());
        switch (degreesClockwise)
        {
            case 0:
                return src.Clone();
            case 90:
            {
                var dst = new PixelFrame(h, w);
                var d = MemoryMarshal.Cast<byte, uint>(dst.Pixels.AsSpan());
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        d[(x * h) + (h - 1 - y)] = s[(y * w) + x];
                    }
                }

                return dst;
            }

            case 180:
            {
                var dst = new PixelFrame(w, h);
                var d = MemoryMarshal.Cast<byte, uint>(dst.Pixels.AsSpan());
                for (var i = 0; i < s.Length; i++)
                {
                    d[s.Length - 1 - i] = s[i];
                }

                return dst;
            }

            case 270:
            {
                var dst = new PixelFrame(h, w);
                var d = MemoryMarshal.Cast<byte, uint>(dst.Pixels.AsSpan());
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        d[((w - 1 - x) * h) + y] = s[(y * w) + x];
                    }
                }

                return dst;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(degreesClockwise), degreesClockwise, "0, 90, 180 or 270");
        }
    }

    /// <summary>Scales B, G and R by <paramref name="brightness"/> (0..1); alpha is kept.</summary>
    public static void ApplyBrightness(PixelFrame frame, double brightness)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var k = (int)Math.Round(Math.Clamp(brightness, 0, 1) * 256);
        if (k == 256)
        {
            return;
        }

        var p = frame.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)(((p[i] * k) + 128) >> 8);
            p[i + 1] = (byte)(((p[i + 1] * k) + 128) >> 8);
            p[i + 2] = (byte)(((p[i + 2] * k) + 128) >> 8);
        }
    }

    /// <summary>Quantizes B, G and R to <paramref name="bits"/> bits per channel (rescaled to 0..255); 8 is the identity.</summary>
    public static void Posterize(PixelFrame frame, int bits)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 8);
        if (bits == 8)
        {
            return;
        }

        var max = (1 << bits) - 1;
        var lut = new byte[256];
        for (var c = 0; c < 256; c++)
        {
            lut[c] = (byte)((((c >> (8 - bits)) * 255) + (max / 2)) / max);
        }

        var p = frame.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = lut[p[i]];
            p[i + 1] = lut[p[i + 1]];
            p[i + 2] = lut[p[i + 2]];
        }
    }

    /// <summary>Forces every pixel opaque over black (premultiplied colour is unchanged; alpha becomes 255).</summary>
    public static void Flatten(PixelFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var p = frame.Pixels;
        for (var i = 3; i < p.Length; i += 4)
        {
            p[i] = 255;
        }
    }

    private static void Pass(PixelFrame src, PixelFrame dst, bool horizontal)
    {
        var srcLen = horizontal ? src.Width : src.Height;
        var dstLen = horizontal ? dst.Width : dst.Height;
        var (start, taps, weights) = Weights(srcLen, dstLen);
        var lines = horizontal ? src.Height : src.Width;
        var s = src.Pixels;
        var d = dst.Pixels;
        var srcStep = horizontal ? 4 : src.Stride;
        var dstStep = horizontal ? 4 : dst.Stride;
        for (var line = 0; line < lines; line++)
        {
            var srcBase = horizontal ? line * src.Stride : line * 4;
            var dstBase = horizontal ? line * dst.Stride : line * 4;
            for (var o = 0; o < dstLen; o++)
            {
                int b = 0, g = 0, r = 0, a = 0;
                var wi = o * taps;
                var si = srcBase + (start[o] * srcStep);
                for (var t = 0; t < taps; t++, si += srcStep)
                {
                    var w = weights[wi + t];
                    if (w == 0)
                    {
                        continue;
                    }

                    b += s[si] * w;
                    g += s[si + 1] * w;
                    r += s[si + 2] * w;
                    a += s[si + 3] * w;
                }

                var di = dstBase + (o * dstStep);
                var aa = Clamp8(a);
                d[di + 3] = aa;
                d[di] = Math.Min(Clamp8(b), aa);
                d[di + 1] = Math.Min(Clamp8(g), aa);
                d[di + 2] = Math.Min(Clamp8(r), aa);
            }
        }
    }

    private static byte Clamp8(int acc) => (byte)Math.Clamp((acc + (WeightOne / 2)) >> WeightBits, 0, 255);

    /// <summary>Per output index: first source index and <c>taps</c> fixed-point weights summing to exactly 1 (edges clamped).</summary>
    private static (int[] Start, int Taps, int[] Weights) Weights(int srcLen, int dstLen)
    {
        var scale = dstLen / (double)srcLen;
        var support = scale < 1 ? 1 / scale : 1.0;
        var taps = Math.Min(srcLen, (int)Math.Ceiling(support * 2) + 1);
        var start = new int[dstLen];
        var weights = new int[dstLen * taps];
        var tmp = new double[taps];
        for (var o = 0; o < dstLen; o++)
        {
            var center = ((o + 0.5) / scale) - 0.5;
            var first = Math.Clamp((int)Math.Floor(center - support) + 1, 0, srcLen - taps);
            start[o] = first;
            var sum = 0.0;
            for (var t = 0; t < taps; t++)
            {
                var w = Math.Max(0, 1 - (Math.Abs(first + t - center) / support));
                tmp[t] = w;
                sum += w;
            }

            if (sum <= 0)
            {
                // the centre fell outside the window (edge): take the nearest source sample
                Array.Clear(tmp);
                tmp[Math.Clamp((int)Math.Round(center) - first, 0, taps - 1)] = 1;
                sum = 1;
            }

            var total = 0;
            var best = 0;
            for (var t = 0; t < taps; t++)
            {
                var wi = (int)Math.Round(tmp[t] / sum * WeightOne);
                weights[(o * taps) + t] = wi;
                total += wi;
                if (tmp[t] > tmp[best])
                {
                    best = t;
                }
            }

            weights[(o * taps) + best] += WeightOne - total;
        }

        return (start, taps, weights);
    }
}
