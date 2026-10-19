using System.Globalization;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Composition;

/// <summary>
/// The rules of an overlay telemetry region (<see cref="ScreenOverlayLayoutDto"/>), shared by the scene-package parser, the screen
/// scene validator and the renderer: fractions of the content canvas before local rotation, finite, X,Y &gt;= 0, Width,Height &gt; 0,
/// every value and X+Width, Y+Height &lt;= 1, Columns 1..<see cref="MaxColumns"/>, BackgroundOpacity 0..1. A region needs the
/// "dashboard" overlay and a rectangular screen; it is never dropped or widened to the full screen.
/// </summary>
public static class OverlayLayouts
{
    public const int MaxColumns = 4;
    public const int MinCellPixels = 24;

    /// <summary>The only overlay a region may carry (the four readings in a grid).</summary>
    public const string RegionOverlay = "dashboard";

    /// <summary>Why the values are not a valid region, or null.</summary>
    public static string? Problem(double x, double y, double width, double height, int columns, double backgroundOpacity)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(backgroundOpacity))
        {
            return "every overlayLayout number must be finite";
        }

        if (x < 0 || y < 0 || x > 1 || y > 1)
        {
            return "x and y must be within 0..1";
        }

        if (!(width > 0) || !(height > 0) || width > 1 || height > 1)
        {
            return "width and height must be > 0 and <= 1";
        }

        if (x + width > 1 || y + height > 1)
        {
            return "the region must lie inside the canvas (x + width <= 1, y + height <= 1)";
        }

        if (columns is < 1 or > MaxColumns)
        {
            return $"columns must be an integer 1..{MaxColumns}";
        }

        return backgroundOpacity is < 0 or > 1 ? "backgroundOpacity must be within 0..1" : null;
    }

    /// <summary>Why <paramref name="layout"/> is not a valid region, or null.</summary>
    public static string? Problem(ScreenOverlayLayoutDto layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Problem(layout.X, layout.Y, layout.Width, layout.Height, layout.Columns, layout.BackgroundOpacity);
    }

    /// <summary>
    /// Why <paramref name="screenId"/> cannot show an overlay region, or null: an unknown id, or a round panel (TL-LCD, H2), whose
    /// overlays keep their own safe-area layouts.
    /// </summary>
    public static string? TargetProblem(string? screenId, ScreenOverlayLayoutDto? layout = null, int rotation = 0)
    {
        if (!ScreenIds.TryParse(screenId, out var family, out _))
        {
            return $"'{screenId}' is not a screen id";
        }

        if (OverlayRenderer.ShapeOf(family.Family) != OverlayShape.Rectangle)
        {
            return $"an overlay region is supported on rectangular screens only, not a round {family.Family} panel";
        }

        if (layout is null) return null;
        if (Problem(layout) is { } invalid) return invalid;
        var swap = rotation is 90 or 270;
        return CanvasProblem(layout, swap ? family.Height : family.Width, swap ? family.Width : family.Height);
    }

    /// <summary>Reject regions too small to show their readings rather than accepting an invisible layer.</summary>
    internal static string? CanvasProblem(ScreenOverlayLayoutDto layout, int width, int height)
    {
        var pixels = Pixels(layout, width, height);
        var rows = (4 + layout.Columns - 1) / layout.Columns;
        return pixels.Width < layout.Columns * MinCellPixels || pixels.Height < rows * MinCellPixels
            ? $"the overlay region is too small: each reading needs at least {MinCellPixels}x{MinCellPixels} content pixels"
            : null;
    }

    /// <summary>A culture-invariant, round-trip key of a layout ("" for none), for render caches.</summary>
    internal static string Key(ScreenOverlayLayoutDto? l) =>
        l is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"region:{l.X:R},{l.Y:R},{l.Width:R},{l.Height:R},{l.Columns},{l.BackgroundOpacity:R}");

    /// <summary>
    /// The region in whole pixels of a <paramref name="width"/>x<paramref name="height"/> content canvas: edges rounded, at least one
    /// pixel each way, never outside the canvas. Drawing is clipped to it and every pixel outside it stays transparent.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) Pixels(ScreenOverlayLayoutDto l, int width, int height)
    {
        var x0 = Math.Clamp((int)Math.Round(l.X * width, MidpointRounding.AwayFromZero), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Round(l.Y * height, MidpointRounding.AwayFromZero), 0, height - 1);
        var x1 = Math.Clamp((int)Math.Round((l.X + l.Width) * width, MidpointRounding.AwayFromZero), x0 + 1, width);
        var y1 = Math.Clamp((int)Math.Round((l.Y + l.Height) * height, MidpointRounding.AwayFromZero), y0 + 1, height);
        return (x0, y0, x1 - x0, y1 - y0);
    }
}
