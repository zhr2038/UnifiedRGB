using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using UnifiedRgb.Devices;

namespace UnifiedRgb.App.Preview;

/// <summary>
/// Draws a <see cref="PreviewRenderer"/> live: rings as circles of dots, matrices as grids, linear zones as a strip, points
/// as one swatch; group renderers draw each zone side by side. Animates on CompositionTarget.Rendering while visible.
/// </summary>
public sealed class ZonePreviewControl : FrameworkElement
{
    public static readonly DependencyProperty RendererProperty = DependencyProperty.Register(
        nameof(Renderer), typeof(PreviewRenderer), typeof(ZonePreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private bool _hooked;

    public ZonePreviewControl()
    {
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
    }

    public PreviewRenderer? Renderer
    {
        get => (PreviewRenderer?)GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width, 48);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var r = Renderer;
        drawingContext.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        if (r is null)
        {
            return;
        }

        var px = r.Render(Clock.Elapsed.TotalSeconds);
        var cell = RenderSize.Width / Math.Max(1, r.Layouts.Count);
        var offset = 0;
        for (var z = 0; z < r.Layouts.Count; z++)
        {
            var layout = r.Layouts[z];
            var box = new Rect(z * cell, 0, cell, RenderSize.Height);
            box.Inflate(-2, -2);
            DrawZone(drawingContext, layout, px.AsSpan(offset, layout.LedCount), box, z < r.Faces.Count ? r.Faces[z] : null);
            offset += layout.LedCount;
        }
    }

    private static void DrawZone(DrawingContext dc, ZoneLayout layout, ReadOnlySpan<Rgb> px, Rect box, RingFace? face)
    {
        switch (layout.Kind)
        {
            case LayoutKind.Ring:
            {
                var c = new Point(box.X + (box.Width / 2), box.Y + (box.Height / 2));
                var radius = (Math.Min(box.Width, box.Height) / 2) - 3;
                var dot = Math.Max(1.5, Math.Min(4, Math.PI * radius / px.Length));
                // WP-14: a vertical-fill H2 is drawn as calibrated, LED 0 at the bottom (θ_i = (i + s)·15° clockwise from it)
                var start = face is { } f ? (Math.PI / 2) + (f.HalfStep ? Math.PI / px.Length : 0) : -Math.PI / 2;
                for (var i = 0; i < px.Length; i++)
                {
                    var a = (2 * Math.PI * i / px.Length) + start;
                    dc.DrawEllipse(Brush(px[i]), null, new Point(c.X + (radius * Math.Cos(a)), c.Y + (radius * Math.Sin(a))), dot, dot);
                }

                break;
            }

            case LayoutKind.Matrix:
            {
                var w = box.Width / layout.Width;
                var h = box.Height / layout.Height;
                for (var i = 0; i < px.Length; i++)
                {
                    dc.DrawRectangle(Brush(px[i]), null, new Rect(box.X + (i % layout.Width * w), box.Y + (i / layout.Width * h), w - 1, h - 1));
                }

                break;
            }

            default:
            {
                var w = box.Width / px.Length;
                for (var i = 0; i < px.Length; i++)
                {
                    dc.DrawRectangle(Brush(px[i]), null, new Rect(box.X + (i * w), box.Y + (box.Height / 4), Math.Max(1, w - 1), box.Height / 2));
                }

                break;
            }
        }
    }

    private static SolidColorBrush Brush(Rgb c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private void Hook(bool on)
    {
        if (on && !_hooked)
        {
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
        else if (!on && _hooked)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();
}
