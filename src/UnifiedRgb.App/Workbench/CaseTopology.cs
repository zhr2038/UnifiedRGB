using System.Collections;
using System.Collections.Specialized;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnifiedRgb.App.Workbench;

/// <summary>Selectable case diagram and optional path-direction illustration, never device readback.</summary>
public sealed class CaseTopology : FrameworkElement
{
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(nameof(Zones), typeof(IEnumerable), typeof(CaseTopology), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ZonesChanged));
    public static readonly DependencyProperty SelectedZoneProperty = DependencyProperty.Register(nameof(SelectedZone), typeof(StudioZone), typeof(CaseTopology), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(CaseTopology), new FrameworkPropertyMetadata("#FF0000", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(nameof(Playing), typeof(bool), typeof(CaseTopology), new FrameworkPropertyMetadata(false, ChangedPlaying));
    public static readonly DependencyProperty DraftProperty = DependencyProperty.Register(nameof(Draft), typeof(SceneDraft), typeof(CaseTopology), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _time = new();
    private readonly List<(Rect Rect, StudioZone Zone)> _hits = [];
    private INotifyCollectionChanged? _collection;
    private bool _watchingCollection;
    private static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");
    private static readonly CultureInfo LabelCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly FrozenDictionary<string, SolidColorBrush> Palette = new[]
    {
        "#141414", "#161616", "#181818", "#1B1B1B", "#1C1C1C", "#222222", "#282828", "#292929", "#2B2B2B",
        "#2D2D2D", "#2E2E2E", "#313131", "#353535", "#393939", "#3C3C3C", "#3D3D3D", "#3F3F3F", "#444444",
        "#534B3F", "#A9A39A", "#D8B578", "#F3EFE8",
    }.ToFrozenDictionary(hex => hex, CreateBrush, StringComparer.Ordinal);
    private readonly Dictionary<(string Text, double Size, string Hex), FormattedText> _labels = [];
    private double _labelDpi;
    private string? _lastColor;
    private SolidColorBrush? _colorBrush, _dimBrush;
    private double _scale = 1;
    private Point _origin;

    public CaseTopology()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        System.Windows.Automation.AutomationProperties.SetName(this, "机箱分区示意图，可用方向键选择分区");
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => InvalidateVisual(), Dispatcher);
        _timer.Stop();
        Unloaded += (_, _) => { _timer.Stop(); WatchCollection(false); };
        Loaded += (_, _) => { WatchCollection(true); UpdateTimer(); };
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public IEnumerable? Zones { get => (IEnumerable?)GetValue(ZonesProperty); set => SetValue(ZonesProperty, value); }
    public StudioZone? SelectedZone { get => (StudioZone?)GetValue(SelectedZoneProperty); set => SetValue(SelectedZoneProperty, value); }
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }
    public SceneDraft? Draft { get => (SceneDraft?)GetValue(DraftProperty); set => SetValue(DraftProperty, value); }

    private static void ZonesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CaseTopology)d;
        if (view._watchingCollection && view._collection is not null) view._collection.CollectionChanged -= view.InvalidateCollection;
        view._collection = e.NewValue as INotifyCollectionChanged;
        if (view._watchingCollection && view._collection is not null) view._collection.CollectionChanged += view.InvalidateCollection;
    }
    private void WatchCollection(bool watch)
    {
        if (_watchingCollection == watch) return;
        if (_collection is not null)
        {
            if (watch) _collection.CollectionChanged += InvalidateCollection;
            else _collection.CollectionChanged -= InvalidateCollection;
        }
        _watchingCollection = watch;
    }
    private void InvalidateCollection(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    private static void ChangedPlaying(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CaseTopology)d).UpdateTimer();
    private void UpdateTimer()
    {
        if (Playing && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation) { _time.Restart(); _timer.Start(); }
        else { _timer.Stop(); _time.Reset(); InvalidateVisual(); }
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var dc = drawingContext;
        _hits.Clear();
        _scale = Math.Max(.1, Math.Min(ActualWidth / 650, ActualHeight / 600));
        _origin = new((ActualWidth - 650 * _scale) / 2, (ActualHeight - 600 * _scale) / 2);
        dc.PushTransform(new TranslateTransform(_origin.X, _origin.Y));
        dc.PushTransform(new ScaleTransform(_scale, _scale));
        var edge = new Pen(Brush("#393939"), 1);
        dc.DrawRoundedRectangle(Brush("#161616"), edge, new Rect(70, 38, 500, 520), 18, 18);
        dc.DrawRoundedRectangle(Brush("#1B1B1B"), new Pen(Brush("#292929"), 1), new Rect(86, 54, 468, 488), 10, 10);
        dc.DrawLine(new Pen(Brush("#393939"), 5), new Point(103, 565), new Point(153, 565));
        dc.DrawLine(new Pen(Brush("#393939"), 5), new Point(487, 565), new Point(537, 565));
        dc.DrawRoundedRectangle(null, new Pen(Brush("#2E2E2E"), 1), new Rect(150, 139, 281, 159), 8, 8);
        var zones = Zones?.OfType<StudioZone>().ToArray() ?? [];
        foreach (var z in zones)
        {
            var id = z.Controller.Id.ToUpperInvariant();
            var rgb = z.Controller.State == "Active" ? Color(z) : Brush("#444444");
            var line = new Pen(rgb, 2.8);
            var selected = z.Ref == SelectedZone?.Ref;
            Rect hit;
            if (id.Contains("1B-7B-F1", StringComparison.Ordinal))
            {
                hit = new(116, 62, 323, 70);
                for (var n = 0; n < 3; n++) Fan(dc, new Rect(123 + n * 106, 65, 91, 63), line, false, false);
                Text(dc, "顶排  TL120", 126, 45, 12, "#A9A39A");
            }
            else if (id.Contains("06-A8-F0", StringComparison.Ordinal))
            {
                hit = new(453, 145, 87, 290);
                for (var n = 0; n < 3; n++) Fan(dc, new Rect(460, 150 + n * 96, 70, 85), line, true, false);
                Text(dc, "侧排", 484, 440, 12, "#A9A39A");
            }
            else if (id.Contains("35-F7-EF", StringComparison.Ordinal))
            {
                hit = new(116, 453, 323, 73);
                for (var n = 0; n < 3; n++) Fan(dc, new Rect(123 + n * 106, 458, 91, 63), line, false, true);
                Text(dc, "底排  TL140 LCD", 126, 530, 12, "#A9A39A");
            }
            else if (id.Contains("EF-7E-63", StringComparison.Ordinal))
            {
                hit = new(227, 176, 110, 110);
                dc.DrawEllipse(Brush("#141414"), new Pen(Brush("#3F3F3F"), 1), new Point(282, 230), 49, 49);
                dc.DrawEllipse(null, new Pen(rgb, 6), new Point(282, 230), 43, 43);
                Text(dc, "H2", 269, 212, 19, "#F3EFE8");
                Text(dc, "水冷头", 264, 239, 12, "#A9A39A");
            }
            else if (id.StartsWith("ENEDRAM:", StringComparison.Ordinal))
            {
                var x = id.EndsWith("0X71", StringComparison.Ordinal) ? 351 : 378;
                hit = new(x - 5, 169, 23, 122);
                dc.DrawRoundedRectangle(Brush("#2D2D2D"), null, new Rect(x, 174, 12, 111), 3, 3);
                for (var n = 0; n < 8; n++) dc.DrawRoundedRectangle(rgb, null, new Rect(x + 2, 177 + n * 13, 8, 9), 2, 2);
                Text(dc, "内存", 351, 298, 12, "#A9A39A");
            }
            else if (id.StartsWith("ASUSAURA:", StringComparison.Ordinal))
            {
                hit = new(160, 168, 54, 55);
                var geometry = Geometry.Parse("M164,178 L209,186 L192,210 L174,201 L193,197 L184,187 Z M177,187 L165,184 L166,194 L181,201 L173,193 Z");
                dc.DrawGeometry(rgb, null, geometry);
                Text(dc, "ROG", 169, 219, 12, "#A9A39A");
            }
            else if (id.StartsWith("MSIGPU:", StringComparison.Ordinal))
            {
                if (z.Zone.Id == 0)
                {
                    hit = new(150, 342, 285, 87);
                    dc.DrawRoundedRectangle(Brush("#282828"), edge, new Rect(150, 324, 285, 105), 9, 9);
                    for (var n = 0; n < 3; n++)
                    {
                        var x = 202 + n * 90;
                        dc.DrawEllipse(Brush("#1C1C1C"), new Pen(Brush("#3F3F3F"), 2), new Point(x, 380), 33, 33);
                        dc.DrawEllipse(Brush("#3C3C3C"), null, new Point(x, 380), 10, 10);
                    }
                    for (var n = 0; n < 2; n++)
                    {
                        var x = 241 + n * 90;
                        dc.DrawLine(line, new Point(x, 345), new Point(x + 10, 376));
                        dc.DrawLine(line, new Point(x + 10, 376), new Point(x + 4, 412));
                    }
                }
                else
                {
                    hit = new(155, 325, 275, 17);
                    dc.DrawLine(line, new Point(160, 333), new Point(425, 333));
                    Text(dc, "MSI  RTX 5090", 163, 301, 12, "#A9A39A");
                }
            }
            else continue;
            _hits.Add((hit, z));
            if (selected)
            {
                hit.Inflate(5, 5);
                dc.DrawRoundedRectangle(null, new Pen(Brush("#D8B578"), 1.4) { DashStyle = DashStyles.Dash }, hit, 8, 8);
            }
        }
        // Screen silhouette only: it does not imply a connected or configured screen transport.
        dc.DrawRoundedRectangle(Brush("#2B2B2B"), new Pen(Brush("#534B3F"), 1), new Rect(441, 88, 105, 33), 5, 5);
        Text(dc, "机箱屏幕", 470, 97, 12, "#F3EFE8");
        Text(dc, "分区示意 · 点击选择", 246, 578, 12, "#A9A39A");
        dc.Pop(); dc.Pop();
    }

    private SolidColorBrush Color(StudioZone zone)
    {
        if (_colorBrush is null || _lastColor != ColorHex)
        {
            _lastColor = ColorHex;
            _colorBrush = Brush(ColorHex);
            var c = _colorBrush.Color;
            _dimBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(c.R * .12), (byte)(c.G * .12), (byte)(c.B * .12)));
            _dimBrush.Freeze();
        }
        var brush = _colorBrush;
        if (!Playing || Draft is null) return brush;
        var stages = Draft.Stages;
        var total = stages.Sum(s => s.Length);
        if (total <= 0) return brush;
        var source = (_time.Elapsed.TotalSeconds % 8) / 8 * total;
        var offset = 0;
        foreach (var s in stages)
        {
            if (s.Bindings.Any(b => b.Zone.Ref == zone.Ref))
            {
                if (source >= offset && source < offset + s.Length) return brush;
                return _dimBrush!;
            }
            offset += s.Length;
        }
        return Brush("#3F3F3F");
    }
    private static void Fan(DrawingContext dc, Rect rect, Pen line, bool vertical, bool lcd)
    {
        dc.DrawRoundedRectangle(Brush("#222222"), new Pen(Brush("#3F3F3F"), 1), rect, 8, 8);
        var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        dc.DrawEllipse(Brush("#181818"), new Pen(Brush("#3D3D3D"), 1), c, 23, 23);
        dc.DrawEllipse(Brush(lcd ? "#353535" : "#313131"), null, c, lcd ? 13 : 9, lcd ? 13 : 9);
        if (vertical)
        {
            dc.DrawLine(line, new Point(rect.Left + 3, rect.Top + 12), new Point(rect.Left + 3, rect.Bottom - 12));
            dc.DrawLine(line, new Point(rect.Right - 3, rect.Top + 12), new Point(rect.Right - 3, rect.Bottom - 12));
        }
        else
        {
            dc.DrawLine(line, new Point(rect.Left + 12, rect.Top + 3), new Point(rect.Right - 12, rect.Top + 3));
            dc.DrawLine(line, new Point(rect.Left + 12, rect.Bottom - 3), new Point(rect.Right - 12, rect.Bottom - 3));
        }
    }
    private void Text(DrawingContext dc, string text, double x, double y, double size, string hex)
    {
        if (_scale < .7 && text != "H2") return; // Avoid illegible labels on the compact editor diagram; full names remain in the zone list.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _labelDpi) { _labels.Clear(); _labelDpi = dpi; }
        var key = (text, size, hex);
        if (!_labels.TryGetValue(key, out var label))
        {
            if (_labels.Count >= 64) _labels.Clear();
            label = new FormattedText(text, LabelCulture, FlowDirection.LeftToRight, LabelTypeface, size, Brush(hex), dpi);
            _labels.Add(key, label);
        }
        dc.DrawText(label, new Point(x, y));
    }
    private static SolidColorBrush Brush(string hex) => Palette.TryGetValue(hex, out var brush) ? brush : CreateBrush(hex);
    private static SolidColorBrush CreateBrush(string hex)
    {
        SolidColorBrush brush;
        try { brush = new((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { brush = new(Colors.Gray); }
        brush.Freeze();
        return brush;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        p = new((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(p));
        if (hit.Zone is not null) SetCurrentValue(SelectedZoneProperty, hit.Zone);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var zones = Zones?.OfType<StudioZone>().ToArray() ?? [];
        if (zones.Length == 0) return;
        var i = Array.IndexOf(zones, SelectedZone);
        SetCurrentValue(SelectedZoneProperty, zones[(i + (e.Key is Key.Left or Key.Up ? -1 : 1) + zones.Length) % zones.Length]);
        e.Handled = true;
    }
}


