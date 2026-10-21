using System.Windows.Media.Imaging;

namespace UnifiedRgb.Screens.Media;

/// <summary>Upload bounds (OPUS-SCREENS work order) plus our normalization box.</summary>
internal sealed record ImportLimits(
    int MaxBytes = 12 * 1024 * 1024,
    long MaxRasterPixels = 40_000_000,
    int MaxFrames = 300,
    int MaxDurationMs = 30_000,
    long MaxTotalPixels = 400_000_000,
    int NormalizeBox = 1920,
    long MaxNormalizedBytes = 256L * 1024 * 1024)
{
    public static ImportLimits Default { get; } = new();
    public string? ScratchDirectory { get; init; }

    /// <summary>GIF delays below this (0 and 1 centisecond included) play as <see cref="ShortDelayMs"/>, as browsers do.</summary>
    public const int MinDelayMs = 20;

    public const int ShortDelayMs = 100;
}

/// <summary>A decoded, composited and normalized upload: PNG frames no larger than the normalization box.</summary>
internal sealed record ImportResult(string Format, int Width, int Height, IReadOnlyList<byte[]> FramePngs, IReadOnlyList<int> DelaysMs)
{
    public bool Animated => FramePngs.Count > 1;

    public int DurationMs => Animated ? DelaysMs.Sum() : 0;
}

/// <summary>
/// Strict, bounded decoding of an untrusted upload (STA thread only). The format is sniffed from magic bytes and decoded with
/// that built-in decoder alone; dimensions, frame count, duration and total decode work are checked from headers BEFORE any pixel
/// is decoded. GIF frames are composited on their logical screen (disposal 0/1 keep, 2 clear to transparent, 3 restore) and
/// every result frame is fitted (no upscaling) into the normalization box and re-encoded as PNG. No path, URL or code is involved.
/// </summary>
internal static class ImageImporter
{
    public static ImportResult Import(byte[] data, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(limits);
        if (data.Length == 0 || data.Length > limits.MaxBytes)
        {
            throw new ArgumentException($"An asset must be 1..{limits.MaxBytes} bytes (got {data.Length}).", nameof(data));
        }

        var format = WpfCodec.Sniff(data) ?? throw new ArgumentException("Only JPEG, PNG and GIF images are accepted.", nameof(data));
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var decoder = WpfCodec.Decoder(format, ms);
            return format == "gif" ? ImportGif(decoder, limits) : ImportStill(format, decoder, limits);
        }
        catch (Exception ex) when (ex is not ArgumentException && WpfCodec.IsDecodeFailure(ex))
        {
            throw new ArgumentException($"The {format.ToUpperInvariant()} data could not be decoded: {ex.Message}", nameof(data), ex);
        }
    }

    private static ImportResult ImportStill(string format, BitmapDecoder decoder, ImportLimits limits)
    {
        if (decoder.Frames.Count != 1)
        {
            throw new ArgumentException($"A {format.ToUpperInvariant()} asset must have exactly one frame.");
        }

        var frame = decoder.Frames[0];
        RequireRaster(frame.PixelWidth, frame.PixelHeight, limits);
        var pixels = WpfCodec.ToPixels(frame);
        var normalized = Normalize(pixels, limits);
        return new ImportResult(format, normalized.Width, normalized.Height, [WpfCodec.EncodePng(normalized)], [0]);
    }

    private static ImportResult ImportGif(BitmapDecoder decoder, ImportLimits limits)
    {
        var count = decoder.Frames.Count;
        if (count < 1 || count > limits.MaxFrames)
        {
            throw new ArgumentException($"A GIF may have 1..{limits.MaxFrames} frames (got {count}).");
        }

        // logical screen from the global descriptor; a frame extending past it widens the screen (still bounded below)
        var meta = decoder.Metadata;
        var lw = WpfCodec.Query(meta, "/logscrdesc/Width", 0);
        var lh = WpfCodec.Query(meta, "/logscrdesc/Height", 0);
        var rects = new (int X, int Y, int W, int H, int Delay, int Disposal)[count];
        for (var i = 0; i < count; i++)
        {
            var f = decoder.Frames[i];
            var md = f.Metadata as BitmapMetadata;
            var x = WpfCodec.Query(md, "/imgdesc/Left", 0);
            var y = WpfCodec.Query(md, "/imgdesc/Top", 0);
            var delay = WpfCodec.Query(md, "/grctlext/Delay", 0) * 10;
            rects[i] = (x, y, f.PixelWidth, f.PixelHeight, delay < ImportLimits.MinDelayMs ? ImportLimits.ShortDelayMs : delay,
                WpfCodec.Query(md, "/grctlext/Disposal", 0));
            RequireRaster(f.PixelWidth, f.PixelHeight, limits);
            lw = Math.Max(lw, x + f.PixelWidth);
            lh = Math.Max(lh, y + f.PixelHeight);
        }

        RequireRaster(lw, lh, limits);
        if ((long)lw * lh * count > limits.MaxTotalPixels)
        {
            throw new ArgumentException($"{count} frames of {lw}x{lh} exceed the {limits.MaxTotalPixels} pixel decode budget.");
        }

        var duration = count == 1 ? 0 : rects.Sum(r => r.Delay);
        if (duration > limits.MaxDurationMs)
        {
            throw new ArgumentException($"A GIF may last at most {limits.MaxDurationMs} ms (got {duration} ms).");
        }

        var canvas = new PixelFrame(lw, lh); // transparent
        var pngs = new List<byte[]>(count);
        long total = 0;
        (int W, int H) size = default;
        for (var i = 0; i < count; i++)
        {
            var r = rects[i];
            var saved = r.Disposal == 3 ? canvas.Clone() : null;
            DrawGifFrame(canvas, WpfCodec.ToPixels(decoder.Frames[i]), r.X, r.Y);
            var normalized = Normalize(canvas, limits);
            size = (normalized.Width, normalized.Height);
            var png = WpfCodec.EncodePng(normalized);
            total += png.Length;
            if (total > limits.MaxNormalizedBytes)
            {
                throw new ArgumentException($"The normalized animation exceeds {limits.MaxNormalizedBytes} bytes.");
            }

            pngs.Add(png);
            if (r.Disposal == 2)
            {
                ClearRect(canvas, r.X, r.Y, r.W, r.H);
            }
            else if (saved is not null)
            {
                canvas = saved;
            }
        }

        return new ImportResult("gif", size.W, size.H, pngs, count == 1 ? [0] : rects.Select(r => r.Delay).ToArray());
    }

    private static void RequireRaster(int w, int h, ImportLimits limits)
    {
        if (w < 1 || h < 1 || (long)w * h > limits.MaxRasterPixels)
        {
            throw new ArgumentException($"A {w}x{h} raster is outside 1..{limits.MaxRasterPixels} pixels.");
        }
    }

    /// <summary>GIF pixels are fully opaque or fully transparent: opaque ones replace the canvas, transparent ones keep it.</summary>
    private static void DrawGifFrame(PixelFrame canvas, PixelFrame frame, int x, int y)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(canvas.Width, x + frame.Width);
        var y1 = Math.Min(canvas.Height, y + frame.Height);
        for (var yy = y0; yy < y1; yy++)
        {
            var di = ((yy * canvas.Width) + x0) * 4;
            var si = (((yy - y) * frame.Width) + (x0 - x)) * 4;
            for (var xx = x0; xx < x1; xx++, di += 4, si += 4)
            {
                if (frame.Pixels[si + 3] != 0)
                {
                    Buffer.BlockCopy(frame.Pixels, si, canvas.Pixels, di, 4);
                }
            }
        }
    }

    private static void ClearRect(PixelFrame canvas, int x, int y, int w, int h)
    {
        var x0 = Math.Max(0, x);
        var x1 = Math.Min(canvas.Width, x + w);
        if (x1 <= x0)
        {
            return;
        }

        for (var yy = Math.Max(0, y); yy < Math.Min(canvas.Height, y + h); yy++)
        {
            Array.Clear(canvas.Pixels, ((yy * canvas.Width) + x0) * 4, (x1 - x0) * 4);
        }
    }

    private static PixelFrame Normalize(PixelFrame frame, ImportLimits limits)
    {
        var s = Math.Min(1.0, Math.Min(limits.NormalizeBox / (double)frame.Width, limits.NormalizeBox / (double)frame.Height));
        if (s >= 1.0)
        {
            return frame;
        }

        return PixelOps.Resize(frame, Math.Max(1, (int)Math.Round(frame.Width * s)), Math.Max(1, (int)Math.Round(frame.Height * s)));
    }
}
