using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Composition;

/// <summary>Where a family's telemetry overlay may draw.</summary>
internal enum OverlayShape
{
    /// <summary>The whole rectangle (TURZX).</summary>
    Rectangle,

    /// <summary>
    /// Only the visible circle of a round panel (TL-LCD 400x400, H2 480x480): every overlay pixel stays within
    /// <see cref="OverlayRenderer.SafeRadius"/> of the centre. The background still fills the whole square raster.
    /// </summary>
    Round,
}

/// <summary>One telemetry reading: the main value and an optional detail that follows it ("42%" + " 61°C", "12.0" + "/32 GB").</summary>
internal sealed record Reading(string Label, string Primary, string? Detail)
{
    public string Value => Primary + Detail;
}

/// <summary>
/// Telemetry overlay text for one composition size (STA thread only). On a rectangle "compact" is one bottom bar and "dashboard"
/// four tiles (2x2 on squarish canvases, 4x1 on wide ones). On a round panel "compact" is two short rows on a panel just below
/// the centre and "dashboard" a 2x2 grid in the inscribed square (side 0.64 D, corners at 0.4525 D), both kept inside
/// <see cref="SafeRadius"/>. A value that is missing, or older than <see cref="StaleAfter"/>, renders as an em dash, never as 0.
/// The rendered layer is cached until its text changes, so a steady 1 Hz sampler costs one render a second.
/// With an overlay region (<see cref="ScreenOverlayLayoutDto"/>, rectangles only) the four readings sit in a Columns-wide grid
/// inside that region alone: optional midnight backing, accent labels and hairline separators, large stable figures; every pixel
/// outside the region stays transparent. Without one the historical layouts above are drawn exactly as before.
/// </summary>
/// <param name="rasterize">Test seam: replaces the RenderTargetBitmap rasterization of the drawn visual.</param>
internal sealed class OverlayRenderer(Func<DrawingVisual, int, int, PixelFrame>? rasterize = null)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    public const string Missing = "—";

    /// <summary>Round panels: overlay pixels stay within this fraction of the diameter from the centre (the visible edge is 0.5).</summary>
    public const double SafeRadius = 0.47;

    /// <summary>Round compact panel, in fractions of the diameter: its top and bottom below the centre, its corners on this radius.</summary>
    private const double CompactTop = 0.03;
    private const double CompactBottom = 0.23;
    private const double CompactCornerRadius = 0.45;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>
    /// The longest realistic readings (full loads, a GPU above 100 °C, 256 GB RAM, 32 GB VRAM). Round layouts size their text so
    /// these fit too, so the text keeps its size as values change; only longer values shrink it further.
    /// </summary>
    private static readonly Reading[] Longest =
        [new("CPU", "100%", null), new("GPU", "100%", " 105°C"), new("RAM", "255.9", "/256 GB"), new("VRAM", "31.9", "/32 GB")];

    private string? _cacheKey;
    private PixelFrame? _cache;

    /// <summary>The overlay shape of a family: TL-LCD and H2 panels are round, TURZX is a wide rectangle.</summary>
    public static OverlayShape ShapeOf(ScreenFamily family) => family is ScreenFamily.TlLcd or ScreenFamily.H2 ? OverlayShape.Round : OverlayShape.Rectangle;

    /// <summary>The four (label, value) pairs shown: CPU, GPU (usage and temperature), RAM and VRAM.</summary>
    public static IReadOnlyList<(string Label, string Value)> Values(PcTelemetryDto? t, DateTimeOffset now) =>
        [.. Readings(t, now).Select(r => (r.Label, r.Value))];

    /// <summary>The four readings behind <see cref="Values"/>, split into main value and detail for the round layouts.</summary>
    public static IReadOnlyList<Reading> Readings(PcTelemetryDto? t, DateTimeOffset now)
    {
        if (t is not null && (now - t.Timestamp > StaleAfter || t.Timestamp - now > StaleAfter))
        {
            t = null;
        }

        var temp = t?.GpuTemperatureC is { } c && double.IsFinite(c) ? " " + c.ToString("0", CultureInfo.InvariantCulture) + "°C" : null;
        return
        [
            new("CPU", Percent(t?.CpuUsagePercent), null),
            new("GPU", Percent(t?.GpuUsagePercent), temp),
            Gigabytes("RAM", t?.MemoryUsedBytes, t?.MemoryTotalBytes),
            Gigabytes("VRAM", t?.GpuMemoryUsedBytes, t?.GpuMemoryTotalBytes),
        ];
    }

    /// <summary>
    /// The overlay layer (premultiplied, transparent where empty) for a <paramref name="width"/>x<paramref name="height"/> content
    /// area, or null when WPF produced a completely transparent layer. Every layout draws translucent backing panels, so an empty
    /// layer means offscreen rendering did not run (see <see cref="HeadlessRendering"/>); it is not cached, and the next call retries.
    /// </summary>
    /// <param name="layout">
    /// Optional region (rectangle shape and the dashboard overlay only, validated by the scene); null keeps the historical layout.
    /// </param>
    public PixelFrame? Render(
        int width, int height, OverlayShape shape, string overlay, Color accent, PcTelemetryDto? telemetry, DateTimeOffset now, ScreenOverlayLayoutDto? layout = null)
    {
        if (layout is not null && (shape != OverlayShape.Rectangle || overlay != OverlayLayouts.RegionOverlay || OverlayLayouts.Problem(layout) is not null))
        {
            throw new ArgumentException("An overlay region needs a rectangular panel, the dashboard overlay and valid bounds.", nameof(layout));
        }
        if (layout is not null && OverlayLayouts.CanvasProblem(layout, width, height) is { } canvasProblem)
        {
            throw new ArgumentException(canvasProblem, nameof(layout));
        }

        var readings = Readings(telemetry, now);
        var key = string.Join('|', width, height, shape, overlay, accent, string.Join(';', readings));
        if (layout is not null)
        {
            key += "|" + OverlayLayouts.Key(layout);
        }
        if (key == _cacheKey && _cache is not null)
        {
            return _cache;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var values = readings.Select(r => (r.Label, r.Value)).ToList();
            switch (shape, overlay)
            {
                case (OverlayShape.Rectangle, _) when layout is not null:
                    DrawRegion(dc, OverlayLayouts.Pixels(layout, width, height), layout, accent, readings);
                    break;
                case (OverlayShape.Round, "compact"):
                    DrawRoundCompact(dc, width, height, accent, readings);
                    break;
                case (OverlayShape.Round, _):
                    DrawRoundDashboard(dc, width, height, accent, readings);
                    break;
                case (_, "compact"):
                    DrawCompact(dc, width, height, accent, values);
                    break;
                default:
                    DrawDashboard(dc, width, height, accent, values);
                    break;
            }
        }

        var layer = (rasterize ?? Rasterize)(visual, width, height);
        if (layout is not null)
        {
            ClearOutside(layer, OverlayLayouts.Pixels(layout, width, height)); // strict: nothing, not even antialiasing, leaves the region
        }

        if (!HasAnyAlpha(layer))
        {
            _cache = null;
            _cacheKey = null;
            return null;
        }

        _cache = layer;
        _cacheKey = key;
        return _cache;
    }

    private static PixelFrame Rasterize(DrawingVisual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return WpfCodec.ToPixels(rtb);
    }

    /// <summary>Makes every pixel outside <paramref name="region"/> fully transparent (premultiplied: all four bytes 0).</summary>
    private static void ClearOutside(PixelFrame frame, (int X, int Y, int Width, int Height) region)
    {
        var p = frame.Pixels;
        var stride = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
        {
            var row = p.AsSpan(y * stride, stride);
            if (y < region.Y || y >= region.Y + region.Height)
            {
                row.Clear();
                continue;
            }

            row[..(region.X * 4)].Clear();
            row[((region.X + region.Width) * 4)..].Clear();
        }
    }

    private static bool HasAnyAlpha(PixelFrame frame)
    {
        var p = frame.Pixels;
        for (var i = 3; i < p.Length; i += 4)
        {
            if (p[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void DrawCompact(DrawingContext dc, int w, int h, Color accent, IReadOnlyList<(string Label, string Value)> values)
    {
        var barH = Math.Max(24, h / (w >= h * 2 ? 5 : 8));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, new Rect(0, h - barH, w, barH));
        dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(0, h - barH, w, Math.Max(2, barH / 14.0)));
        var text = string.Join("   ", values.Select(v => v.Label + " " + v.Value));
        var size = barH * 0.5;
        var ft = Text(text, Face, size, Brushes.White);
        while (ft.Width > w * 0.94 && size > 6)
        {
            size *= 0.9;
            ft = Text(text, Face, size, Brushes.White);
        }

        dc.DrawText(ft, new Point((w - ft.Width) / 2, h - (barH / 2.0) - (ft.Height / 2) + (barH / 28.0)));
    }

    private static void DrawDashboard(DrawingContext dc, int w, int h, Color accent, List<(string Label, string Value)> values)
    {
        var wide = w >= h * 2;
        var cols = wide ? 4 : 2;
        var rows = wide ? 1 : 2;
        var pad = Math.Min(w, h) * 0.04;
        var tileW = (w - (pad * (cols + 1))) / cols;
        var tileH = (h - (pad * (rows + 1))) / rows;
        var panel = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        var accentBrush = new SolidColorBrush(accent);
        for (var i = 0; i < values.Count; i++)
        {
            var x = pad + ((i % cols) * (tileW + pad));
            var y = pad + ((i / cols) * (tileH + pad));
            dc.DrawRoundedRectangle(panel, null, new Rect(x, y, tileW, tileH), tileH * 0.08, tileH * 0.08);
            dc.DrawRectangle(accentBrush, null, new Rect(x + (tileW * 0.08), y + (tileH * 0.12), Math.Max(2, tileW * 0.02), tileH * 0.22));
            var label = Text(values[i].Label, LabelFace, tileH * 0.16, accentBrush);
            dc.DrawText(label, new Point(x + (tileW * 0.14), y + (tileH * 0.12)));
            var size = tileH * 0.34;
            var value = Text(values[i].Value, Face, size, Brushes.White);
            while (value.Width > tileW * 0.86 && size > 6)
            {
                size *= 0.9;
                value = Text(values[i].Value, Face, size, Brushes.White);
            }

            dc.DrawText(value, new Point(x + ((tileW - value.Width) / 2), y + (tileH * 0.42) + (((tileH * 0.5) - value.Height) / 2)));
        }
    }

    /// <summary>
    /// Round compact: a rounded panel from 0.03 D to 0.23 D below the centre whose bottom corners lie on 0.45 D, an accent line
    /// along its top, and two rows (CPU, GPU / RAM, VRAM) of accent label, white value and smaller white detail on one baseline.
    /// The panel does not depend on the values; both rows share one text size, fitted to the actual and the <see cref="Longest"/>
    /// rows.
    /// </summary>
    private static void DrawRoundCompact(DrawingContext dc, int w, int h, Color accent, IReadOnlyList<Reading> readings)
    {
        var d = Math.Min(w, h);
        var half = Math.Sqrt((CompactCornerRadius * CompactCornerRadius) - (CompactBottom * CompactBottom)) * d;
        var panel = new Rect((w / 2.0) - half, (h / 2.0) + (CompactTop * d), 2 * half, (CompactBottom - CompactTop) * d);
        var corner = 0.05 * d;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, panel, corner, corner);
        var accentBrush = new SolidColorBrush(accent);
        var inset = corner + (0.01 * d);
        dc.DrawRectangle(accentBrush, null, new Rect(panel.X + inset, panel.Y, panel.Width - (2 * inset), Math.Max(2, d / 150.0)));

        var pad = 0.02 * d;
        var rowH = (panel.Height - (2 * pad)) / 2;
        var room = panel.Width - (2 * pad);
        var rows = new[] { Runs(readings[0], readings[1], accentBrush), Runs(readings[2], readings[3], accentBrush) };
        var sizing = rows.Append(Runs(Longest[0], Longest[1], accentBrush)).Append(Runs(Longest[2], Longest[3], accentBrush)).ToList();
        var size = rowH / 1.34; // Segoe UI's line height is about 1.33 em
        while (sizing.Max(r => RowWidth(r, size)) > room && size > 6)
        {
            size *= 0.95;
        }

        var digit = Text("0", Face, size, Brushes.White);
        for (var i = 0; i < rows.Length; i++)
        {
            var baseline = panel.Y + pad + (i * rowH) + ((rowH - digit.Height) / 2) + digit.Baseline;
            DrawRow(dc, rows[i], size, w / 2.0, baseline);
        }
    }

    /// <summary>
    /// Round dashboard: a 2x2 grid in the inscribed square (0.18 D..0.82 D on both axes). Each tile has an accent label and
    /// underline, the main value large and centred, and the detail (GPU temperature, memory total) on a smaller line below it.
    /// Labels, values and details each have one size for all tiles, fitted to the actual and the <see cref="Longest"/> readings.
    /// </summary>
    private static void DrawRoundDashboard(DrawingContext dc, int w, int h, Color accent, IReadOnlyList<Reading> readings)
    {
        var d = Math.Min(w, h);
        var side = 0.64 * d;
        var gap = 0.025 * d;
        var tile = (side - gap) / 2;
        var x0 = (w - side) / 2;
        var y0 = (h - side) / 2;
        var panel = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        var accentBrush = new SolidColorBrush(accent);
        var all = readings.Concat(Longest).ToList();
        var labelSize = FitSize(all.Select(r => r.Label), LabelFace, tile * 0.15, tile * 0.8);
        var valueSize = FitSize(all.Select(r => r.Primary), Face, tile * 0.34, tile * 0.84);
        var detailSize = FitSize(all.Select(r => r.Detail?.Trim()).OfType<string>(), LabelFace, tile * 0.15, tile * 0.84);
        for (var i = 0; i < readings.Count; i++)
        {
            var x = x0 + ((i % 2) * (tile + gap));
            var y = y0 + ((i / 2) * (tile + gap));
            var cx = x + (tile / 2);
            dc.DrawRoundedRectangle(panel, null, new Rect(x, y, tile, tile), tile * 0.12, tile * 0.12);
            var label = Text(readings[i].Label, LabelFace, labelSize, accentBrush);
            dc.DrawText(label, new Point(cx - (label.Width / 2), y + (tile * 0.07)));
            var lineW = tile * 0.18;
            dc.DrawRectangle(accentBrush, null, new Rect(cx - (lineW / 2), y + (tile * 0.07) + label.Height, lineW, Math.Max(2, tile * 0.018)));
            var value = Text(readings[i].Primary, Face, valueSize, Brushes.White);
            dc.DrawText(value, new Point(cx - (value.Width / 2), y + (tile * 0.55) - (value.Height / 2)));
            if (readings[i].Detail is { } detail)
            {
                var small = Text(detail.Trim(), LabelFace, detailSize, Brushes.White);
                dc.DrawText(small, new Point(cx - (small.Width / 2), y + (tile * 0.84) - (small.Height / 2)));
            }
        }
    }

    /// <summary>
    /// A telemetry region: the four readings in <see cref="ScreenOverlayLayoutDto.Columns"/> columns and ceil(4/Columns) rows,
    /// clipped to the region. Optional midnight backing at BackgroundOpacity (0 = none); per cell an accent label, the main value
    /// large and white, the detail smaller below it; hairline accent separators between cells. Padding and text sizes are relative
    /// to the cell, and each of label, value and detail has ONE size for all cells, fitted to the actual and the
    /// <see cref="Longest"/> readings (100 %, 105 °C, 256 GB, em dash) so the figures keep their size as values change.
    /// A soft dark shadow keeps text readable over a moving authored background without a solid block.
    /// </summary>
    private static void DrawRegion(
        DrawingContext dc, (int X, int Y, int Width, int Height) px, ScreenOverlayLayoutDto layout, Color accent, IReadOnlyList<Reading> readings)
    {
        var region = new Rect(px.X, px.Y, px.Width, px.Height);
        dc.PushClip(new RectangleGeometry(region));
        if (layout.BackgroundOpacity > 0)
        {
            var alpha = (byte)Math.Round(layout.BackgroundOpacity * 255, MidpointRounding.AwayFromZero);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, 6, 10, 20)), null, region);
        }

        var cols = layout.Columns;
        var rows = (readings.Count + cols - 1) / cols;
        var pad = Math.Min(region.Width, region.Height) * 0.04;
        var cellW = (region.Width - (2 * pad)) / cols;
        var cellH = (region.Height - (2 * pad)) / rows;
        var inset = Math.Min(cellW, cellH) * 0.1;
        var room = Math.Max(1, cellW - (2 * inset));
        var tall = Math.Max(1, cellH - (2 * inset));

        // label 0.3 em, value 1 em and detail 0.36 em of the value size; Segoe UI's line height is about 1.33 em
        var all = readings.Concat(Longest).ToList();
        var valueSize = FitSize(all.Select(r => r.Primary), Face, tall / ((0.3 + 1 + 0.36) * 1.34), room);
        var labelSize = FitSize(all.Select(r => r.Label), LabelFace, valueSize * 0.3, room);
        var detailSize = FitSize(all.Select(r => r.Detail?.Trim()).OfType<string>(), LabelFace, valueSize * 0.36, room);

        var accentBrush = new SolidColorBrush(accent);
        var separator = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        var shadow = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        var detailBrush = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255));
        var hair = Math.Max(1, Math.Round(Math.Min(region.Width, region.Height) / 240.0));
        var offset = Math.Max(1, valueSize / 28);
        for (var i = 0; i < readings.Count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            var x = region.X + pad + (col * cellW);
            var y = region.Y + pad + (row * cellH);
            if (col > 0)
            {
                dc.DrawRectangle(separator, null, new Rect(x - (hair / 2), y + inset, hair, cellH - (2 * inset)));
            }

            if (row > 0)
            {
                dc.DrawRectangle(separator, null, new Rect(x + inset, y - (hair / 2), cellW - (2 * inset), hair));
            }

            var top = y + inset;
            top += Shadowed(dc, readings[i].Label, LabelFace, labelSize, accentBrush, shadow, new Point(x + inset, top), offset);
            top += Shadowed(dc, readings[i].Primary, Face, valueSize, Brushes.White, shadow, new Point(x + inset, top), offset);
            if (readings[i].Detail is { } detail)
            {
                Shadowed(dc, detail.Trim(), LabelFace, detailSize, detailBrush, shadow, new Point(x + inset, top), offset);
            }
        }

        dc.Pop();
    }

    /// <summary>Draws the text with a soft offset shadow; returns its line height.</summary>
    private static double Shadowed(DrawingContext dc, string text, Typeface face, double size, Brush brush, Brush shadow, Point at, double offset)
    {
        dc.DrawText(Text(text, face, size, shadow), new Point(at.X + offset, at.Y + offset));
        var ft = Text(text, face, size, brush);
        dc.DrawText(ft, at);
        return ft.Height;
    }

    /// <summary>One compact row: for each reading an accent label, the white value and its smaller white detail, with a gap between.</summary>
    private static (string Text, Typeface Face, double Scale, Brush Brush)[] Runs(Reading a, Reading b, Brush accent) =>
    [
        (a.Label + " ", LabelFace, 0.68, accent), (a.Primary, Face, 1, Brushes.White), (a.Detail ?? string.Empty, LabelFace, 0.68, Brushes.White),
        ("\u2002\u2002", Face, 0.8, Brushes.White),
        (b.Label + " ", LabelFace, 0.68, accent), (b.Primary, Face, 1, Brushes.White), (b.Detail ?? string.Empty, LabelFace, 0.68, Brushes.White),
    ];

    private static double RowWidth((string Text, Typeface Face, double Scale, Brush Brush)[] runs, double size) =>
        runs.Sum(r => Text(r.Text, r.Face, size * r.Scale, r.Brush).WidthIncludingTrailingWhitespace);

    /// <summary>Draws the runs centred on <paramref name="centreX"/>, all on one baseline.</summary>
    private static void DrawRow(DrawingContext dc, (string Text, Typeface Face, double Scale, Brush Brush)[] runs, double size, double centreX, double baseline)
    {
        var x = centreX - (RowWidth(runs, size) / 2);
        foreach (var r in runs)
        {
            var ft = Text(r.Text, r.Face, size * r.Scale, r.Brush);
            dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
            x += ft.WidthIncludingTrailingWhitespace;
        }
    }

    /// <summary>The largest size, from <paramref name="size"/> down in 5 % steps, at which every text is at most <paramref name="maxWidth"/> wide.</summary>
    private static double FitSize(IEnumerable<string> texts, Typeface face, double size, double maxWidth)
    {
        var all = texts.ToList();
        while (size > 6 && all.Any(t => Text(t, face, size, Brushes.White).Width > maxWidth))
        {
            size *= 0.95;
        }

        return size;
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(1, size), brush, 1.0);

    private static string Percent(double? v) =>
        v is { } p && double.IsFinite(p) ? p.ToString("0", CultureInfo.InvariantCulture) + "%" : Missing;

    private static Reading Gigabytes(string label, long? used, long? total)
    {
        if (used is not { } u || total is not { } t || t <= 0 || u < 0)
        {
            return new(label, Missing, null);
        }

        const double Gb = 1024.0 * 1024 * 1024;
        return new(label, (u / Gb).ToString("0.0", CultureInfo.InvariantCulture), "/" + (t / Gb).ToString("0", CultureInfo.InvariantCulture) + " GB");
    }
}

/// <summary>
/// The WPF AppContext switch that keeps offscreen rendering alive in an interactive process with no display device (monitor
/// asleep or disconnected). WPF reads it once, when its MediaContext starts, so it belongs in the executable's runtimeconfig
/// (the Host sets it; see UnifiedRgb.Host.csproj). This library never sets process-wide switches itself.
/// </summary>
internal static class HeadlessRendering
{
    public const string SwitchName = "Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable";

    /// <summary>True when the switch is configured on for this process.</summary>
    public static bool Enabled => AppContext.TryGetSwitch(SwitchName, out var on) && on;
}
