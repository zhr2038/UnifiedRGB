using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using Color = System.Windows.Media.Color;

namespace UnifiedRgb.Screens.Tests;

/// <summary>Pixel geometry of round-panel overlays, in fractions of the diameter D = min(width, height).</summary>
internal static class RoundGeometry
{
    /// <summary>Distance of pixel (x, y)'s centre from the canvas centre, as a fraction of D.</summary>
    public static double Radius(PixelFrame f, int x, int y)
    {
        var dx = x + 0.5 - (f.Width / 2.0);
        var dy = y + 0.5 - (f.Height / 2.0);
        return Math.Sqrt((dx * dx) + (dy * dy)) / Math.Min(f.Width, f.Height);
    }

    /// <summary>Overlay pixels with any alpha farther than <paramref name="radius"/> from the centre.</summary>
    public static int PaintedOutside(PixelFrame layer, double radius) => Count(layer, (x, y, p) => p[3] != 0 && Radius(layer, x, y) > radius);

    /// <summary>The largest radius of any overlay pixel with alpha.</summary>
    public static double MaxPaintedRadius(PixelFrame layer)
    {
        var max = 0.0;
        Count(layer, (x, y, p) =>
        {
            if (p[3] != 0)
            {
                max = Math.Max(max, Radius(layer, x, y));
            }

            return false;
        });
        return max;
    }

    /// <summary>Pixels that differ between two opaque canvases farther than <paramref name="radius"/> from the centre.</summary>
    public static int ChangedOutside(PixelFrame a, PixelFrame b, double radius) =>
        Count(a, (x, y, p) => Radius(a, x, y) > radius && !p.SequenceEqual(b.Pixels.AsSpan((((y * b.Width) + x) * 4), 4).ToArray()));

    /// <summary>Red accent pixels (accent #FF0000 over the dark panels).</summary>
    public static int Accent(PixelFrame layer) => Count(layer, (_, _, p) => p[3] > 180 && p[2] >= 100 && p[1] <= 40 && p[0] <= 40);

    /// <summary>
    /// Heights of the horizontal bands of rows that contain white glyph pixels (see <see cref="HeadlessProbe.Glyphs"/>), top to
    /// bottom: one band per line of white text.
    /// </summary>
    public static IReadOnlyList<int> GlyphBands(PixelFrame layer)
    {
        var bands = new List<int>();
        var run = 0;
        for (var y = 0; y < layer.Height; y++)
        {
            var white = false;
            for (var x = 0; x < layer.Width && !white; x++)
            {
                var i = ((y * layer.Width) + x) * 4;
                var p = layer.Pixels;
                white = p[i + 3] > 180 && p[i] >= 128 && p[i + 1] >= 128 && p[i + 2] >= 128;
            }

            if (white)
            {
                run++;
            }
            else if (run > 0)
            {
                bands.Add(run);
                run = 0;
            }
        }

        if (run > 0)
        {
            bands.Add(run);
        }

        return bands;
    }

    private static int Count(PixelFrame f, Func<int, int, byte[], bool> predicate)
    {
        var n = 0;
        var px = new byte[4];
        for (var y = 0; y < f.Height; y++)
        {
            for (var x = 0; x < f.Width; x++)
            {
                Buffer.BlockCopy(f.Pixels, ((y * f.Width) + x) * 4, px, 0, 4);
                n += predicate(x, y, px) ? 1 : 0;
            }
        }

        return n;
    }
}

/// <summary>One written example: what it shows and its measured geometry.</summary>
internal sealed record RoundExample(
    string File,
    string Family,
    string Overlay,
    string Values,
    string Note,
    double MaxOverlayRadius,
    int ChangedOutsideSafe,
    IReadOnlyList<int> GlyphBandHeights);

/// <summary>
/// Four inspectable round-panel examples (TL-LCD 400 and H2 480, compact and dashboard) composed exactly as the preview and the
/// device worker compose them, over a busy background asset. Each PNG holds the exact device raster on the left and, on the right,
/// the same raster as the round panel shows it (outside the visible circle blanked) with the 0.47 D safe circle dashed.
/// </summary>
internal static class RoundExamples
{
    public const string Argument = "--round-examples";

    private const double Gb = 1024.0 * 1024 * 1024;

    /// <summary>The longest realistic values: 100 % loads, a 105 °C GPU, 255.9/256 GB RAM and 31.9/32 GB VRAM.</summary>
    public static readonly PcTelemetryDto Long =
        new(HeadlessProbe.Now, 100, (long)(255.94 * Gb), 256L << 30, 100, 105, (long)(31.94 * Gb), 32L << 30, "examples", null);

    /// <summary>A machine without GPU telemetry: GPU and VRAM render as em dashes.</summary>
    public static readonly PcTelemetryDto NoGpu = new(HeadlessProbe.Now, 7, 12L << 30, 32L << 30, null, null, null, null, "examples", "no GPU");

    /// <summary>Writes the four examples into <paramref name="directory"/> (STA thread only).</summary>
    public static IReadOnlyList<RoundExample> Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var cases = new (string Name, ScreenFamilyInfo Family, string Overlay, PcTelemetryDto Telemetry, string Accent)[]
        {
            ("tl-lcd-400-compact", ScreenFamilyInfo.TlLcd, "compact", HeadlessProbe.Telemetry, "#FFC400"),
            ("tl-lcd-400-dashboard", ScreenFamilyInfo.TlLcd, "dashboard", Long, "#FFC400"),
            ("h2-480-compact", ScreenFamilyInfo.H2, "compact", Long, "#00C8FF"),
            ("h2-480-dashboard", ScreenFamilyInfo.H2, "dashboard", NoGpu, "#00C8FF"),
        };

        var root = Path.Combine(Path.GetTempPath(), "urgb-round-examples", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssetStore(root, 1 << 24, 4, NullLogger.Instance);
            var png = WpfCodec.EncodePng(Background(480));
            var asset = store.Commit(AssetStore.IdOf(png), "background", ImageImporter.Import(png, ImportLimits.Default)).Id;
            var compositor = new ScreenCompositor(store, 0);
            var written = new List<RoundExample>();
            foreach (var (name, family, overlay, telemetry, accent) in cases)
            {
                var a = new ScreenAssignmentDto($"screen:{family.Token}:example", asset, "#101018", overlay, AccentColor: accent);
                var composed = compositor.Compose(family, a, 0, telemetry, HeadlessProbe.Now);
                var plain = compositor.Compose(family, a with { Overlay = "none" }, 0, telemetry, HeadlessProbe.Now).Canvas;
                var encoded = ScreenFrameEncoder.Encode(family, composed.Canvas, 90);
                var (ar, ag, ab) = ScreenColor.Parse(accent);
                var layer = new OverlayRenderer().Render(family.Width, family.Height, OverlayRenderer.ShapeOf(family.Family), overlay,
                    Color.FromRgb(ar, ag, ab), telemetry, HeadlessProbe.Now)!;
                var file = name + ".png";
                File.WriteAllBytes(Path.Combine(directory, file), WpfCodec.EncodePng(SideBySide(encoded.Shown)));
                written.Add(new RoundExample(file, family.Token, overlay,
                    string.Join("  ", OverlayRenderer.Values(telemetry, HeadlessProbe.Now).Select(v => v.Label + " " + v.Value)),
                    encoded.Note + (composed.Warning is { } w ? "; " + w : string.Empty),
                    Math.Round(RoundGeometry.MaxPaintedRadius(layer), 4),
                    RoundGeometry.ChangedOutside(encoded.Shown, plain, OverlayRenderer.SafeRadius),
                    RoundGeometry.GlyphBands(layer)));
            }

            return written;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>A busy background: diagonal purple-to-orange gradient with bright rings.</summary>
    private static PixelFrame Background(int size)
    {
        var f = new PixelFrame(size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var t = (x + y) / (2.0 * size);
                var ring = Math.Sin(Math.Sqrt(((x - (size * 0.3)) * (x - (size * 0.3))) + ((y - (size * 0.35)) * (y - (size * 0.35)))) / 9.0) > 0.85;
                var i = ((y * size) + x) * 4;
                f.Pixels[i] = (byte)(ring ? 120 : 90 * (1 - t));
                f.Pixels[i + 1] = (byte)(ring ? 230 : 20 + (110 * t));
                f.Pixels[i + 2] = (byte)(ring ? 255 : 60 + (195 * t));
                f.Pixels[i + 3] = 255;
            }
        }

        return f;
    }

    /// <summary>Left: the exact raster. Right: the round panel's view (outside 0.5 D blank) with the safe circle dashed in white.</summary>
    private static PixelFrame SideBySide(PixelFrame shown)
    {
        int w = shown.Width, h = shown.Height, gap = 16;
        var o = PixelFrame.Solid((2 * w) + gap, h, 40, 40, 40);
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(shown.Pixels, y * shown.Stride, o.Pixels, y * o.Stride, shown.Stride);
            for (var x = 0; x < w; x++)
            {
                var r = RoundGeometry.Radius(shown, x, y);
                var dst = ((y * o.Width) + w + gap + x) * 4;
                if (r > 0.5)
                {
                    continue;
                }

                Buffer.BlockCopy(shown.Pixels, ((y * w) + x) * 4, o.Pixels, dst, 4);
                var angle = Math.Atan2(y + 0.5 - (h / 2.0), x + 0.5 - (w / 2.0));
                if (Math.Abs((r - OverlayRenderer.SafeRadius) * Math.Min(w, h)) < 0.75 && ((int)Math.Floor((angle + Math.PI) / (Math.PI / 48)) % 2) == 0)
                {
                    o.Pixels[dst] = o.Pixels[dst + 1] = o.Pixels[dst + 2] = 255;
                }
            }
        }

        return o;
    }
}
