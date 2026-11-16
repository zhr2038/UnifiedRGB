using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// One persistent background STA Dispatcher worker reused across artwork renders so repeated OLED frames
/// never churn threads, HwndWrapper classes, or USER atoms, and cleanly retires its Dispatcher on Dispose.
/// </summary>
internal sealed class KeyboardArtworkRenderer : IDisposable
{
    private static readonly Lazy<Drawing> BrandMark = new(() =>
    {
        using var stream = typeof(KeyboardArtworkRenderer).Assembly.GetManifestResourceStream("UnifiedRgb.KeyboardBrand.xaml")
            ?? throw new InvalidOperationException("Compiled brand resource missing.");
        var resources = (ResourceDictionary)XamlReader.Load(stream);
        var mark = ((Drawing)resources["Brand.MarkDrawing"]).CloneCurrentValue();
        White(mark);
        mark.Freeze(); // One immutable logo can be shared by persistent rendering STA threads.
        return mark;
    });

    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private int _disposed;

    public KeyboardArtworkRenderer(string threadName = "Keyboard artwork")
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? startupFailure = null;
        _thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(ready.Set);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                startupFailure = ex;
                ready.Set();
            }
        }) { IsBackground = true, Name = threadName };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (startupFailure is not null || dispatcher is null)
            throw new InvalidOperationException("Keyboard artwork worker failed to start.", startupFailure);
        _dispatcher = dispatcher;
    }

    internal Dispatcher Dispatcher => _dispatcher;
    internal Thread Thread => _thread;

    public byte[] Render(CaseStatusObservation state) => Rasterize((dc, mark) => DrawStatus(dc, mark, state));

    public byte[] RenderLogo() => Rasterize(DrawLogo);

    public byte[] Rasterize(Action<DrawingContext, Drawing> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        byte[]? result = null;
        Exception? failure = null;
        var op = _dispatcher.BeginInvoke(() =>
        {
            try
            {
                var mark = BrandMark.Value;
                var visual = new DrawingVisual();
                TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 208, 64));
                    draw(dc, mark);
                }
                var bitmap = new RenderTargetBitmap(208, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var bgra = new byte[208 * 64 * 4];
                bitmap.CopyPixels(bgra, 208 * 4, 0);
                result = PackBgra(bgra);
            }
            catch (Exception ex) { failure = ex; }
        });
        var status = op.Wait(TimeSpan.FromSeconds(5));
        if (status != DispatcherOperationStatus.Completed)
        {
            op.Abort();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted, this);
            throw new TimeoutException("Keyboard artwork exceeded its rendering bound.");
        }
        if (failure is not null) throw new InvalidOperationException("Keyboard artwork failed before output.", failure);
        return result!;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_dispatcher.HasShutdownStarted)
        {
            try { _dispatcher.InvokeShutdown(); }
            catch (InvalidOperationException) { }
        }
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    private static void DrawStatus(DrawingContext dc, Drawing mark, CaseStatusObservation state)
    {
        dc.PushTransform(new MatrixTransform(17d / 256, 0, 0, 17d / 256, 6, 0)); dc.DrawDrawing(mark); dc.Pop();
        Text(dc, "光域 · 机箱状态", 13, 27, 0, 174);
        dc.DrawLine(new Pen(Brushes.White, 1), new Point(6, 18.5), new Point(202, 18.5));
        Text(dc, state.Title, 20, 6, 19, 196); Text(dc, state.Detail, 13, 6, 46, 196);
    }

    private static void DrawLogo(DrawingContext dc, Drawing mark)
    {
        dc.PushTransform(new MatrixTransform(52d / 256, 0, 0, 52d / 256, 46, 6)); dc.DrawDrawing(mark); dc.Pop();
        Text(dc, "光域", 32, 108, 8, 80);
    }

    internal static void Text(DrawingContext dc, string value, double size, double x, double y, double width) => dc.DrawText(new FormattedText(value,
        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        size, Brushes.White, 1) { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new Point(x, y));

    internal static byte[] PackBgra(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != 208 * 64 * 4) throw new ArgumentException("Expected one keyboard BGRA frame.", nameof(bgra));
        var result = new byte[6656];
        for (var p = 0; p < result.Length; p++) result[p] = (byte)(bgra[p * 8] / 17 | (bgra[p * 8 + 4] / 17) << 4);
        return result;
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
