using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Services;

/// <summary>Off-clock208x64 artwork; the compiled project logo and independently packed pixels match the accepted trial.</summary>
internal static class KeyboardStatusArtwork
{
    public static byte[] Render(CaseStatusObservation state) => Rasterize((dc, mark) =>
    {
        dc.PushTransform(new MatrixTransform(17d / 256, 0, 0, 17d / 256, 6, 0)); dc.DrawDrawing(mark); dc.Pop();
        Text(dc, "光域 · 机箱状态", 13, 27, 0, 174);
        dc.DrawLine(new Pen(Brushes.White, 1), new Point(6, 18.5), new Point(202, 18.5));
        Text(dc, state.Title, 20, 6, 19, 196); Text(dc, state.Detail, 13, 6, 46, 196);
    });

    /// <summary>The Guangyu entry logo: the same compiled mark, centred with the product name and no status text.</summary>
    public static byte[] RenderLogo() => Rasterize((dc, mark) =>
    {
        dc.PushTransform(new MatrixTransform(52d / 256, 0, 0, 52d / 256, 46, 6)); dc.DrawDrawing(mark); dc.Pop();
        Text(dc, "光域", 32, 108, 8, 80);
    });

    private static void Text(DrawingContext dc, string value, double size, double x, double y, double width) => dc.DrawText(new FormattedText(value,
        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        size, Brushes.White, 1) { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new Point(x, y));

    private static byte[] Rasterize(Action<DrawingContext, Drawing> draw)
    {
        byte[]? result = null; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var stream = typeof(KeyboardStatusArtwork).Assembly.GetManifestResourceStream("UnifiedRgb.KeyboardBrand.xaml")
                    ?? throw new InvalidOperationException("Compiled brand resource missing.");
                var resources = (ResourceDictionary)XamlReader.Load(stream);
                var mark = ((Drawing)resources["Brand.MarkDrawing"]).CloneCurrentValue();
                White(mark); mark.Freeze();
                var visual = new DrawingVisual();
                TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 208, 64));
                    draw(dc, mark);
                }
                var bitmap = new RenderTargetBitmap(208, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var bgra = new byte[208 * 64 * 4]; bitmap.CopyPixels(bgra, 208 * 4, 0);
                result = new byte[6656];
                for (var p = 0; p < result.Length; p++) result[p] = (byte)(bgra[p * 8] / 17 | (bgra[p * 8 + 4] / 17) << 4);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Keyboard artwork" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Keyboard artwork exceeded its rendering bound.");
        if (failure is not null) throw new InvalidOperationException("Keyboard artwork failed before output.", failure);
        return result!;
    }

    private static void White(Drawing drawing)
    {
        if (drawing is DrawingGroup g) foreach (var child in g.Children) White(child);
        else if (drawing is GeometryDrawing d)
        {
            if (d.Brush is not null and not SolidColorBrush { Color.A: 0 }) d.Brush = Brushes.White;
            if (d.Pen is not null) d.Pen.Brush = Brushes.White;
        }
    }
}
