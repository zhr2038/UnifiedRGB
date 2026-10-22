using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.App.Preview;

/// <summary>Local RGB source rendering. No RPC, file I/O or hardware call occurs on the rendering path.</summary>
public sealed class PackageCaseDrawing : FrameworkElement
{
    private static readonly BitmapImage Background = LoadBackground();
    private readonly Dictionary<int, SolidColorBrush> _brushes = [];
    private readonly List<ProjectedTrack> _tracks = [];
    private PackagePreviewData? _data;
    public ScenePackagesViewModel? Model { get; set; }
    private sealed record ProjectedTrack(PackagePreviewTrack Track, PackageTarget? Target, Geometry[] Body, Geometry[] Glow);

    private static BitmapImage LoadBackground()
    {
        var b = new BitmapImage(new Uri("pack://application:,,,/UnifiedRgb.App;component/Assets/Workbench/case-schematic.png")); b.Freeze(); return b;
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawImage(Background, new Rect(0, 0, 776, 420));
        var vm = Model; var data = vm?.PreviewData;
        if (vm is null || data is null || ScenePackagePlayback.Validate(vm.PreviewPlayback) is not null) return;
        if (!ReferenceEquals(_data, data) || _tracks.Any(p => !ReferenceEquals(p.Target, vm.Roles.FirstOrDefault(r => r.Dto.Role == p.Track.Info.Role)?.Selected))) Rebuild(vm, data);
        var position = ScenePackagePlayback.SourcePosition(vm.PreviewPositionMs, data.Info.DurationMs, vm.PreviewPlayback);
        foreach (var track in _tracks)
        {
            var info = track.Track.Info;
            var frame = Math.Clamp((int)Math.Floor(position / info.FrameDurationMs), 0, info.Frames - 1);
            for (var i = 0; i < track.Body.Length; i++)
            {
                var sourceIndex = track.Body.Length == info.LedCount ? i : i * info.LedCount / track.Body.Length;
                var offset = (frame * info.LedCount + sourceIndex) * 3; var rgb = track.Track.Rgb;
                ScenePackagePlayback.TransformRgb(rgb[offset], rgb[offset + 1], rgb[offset + 2], vm.PreviewPlayback, out var r, out var g, out var b);
                if (r == 0 && g == 0 && b == 0) continue;
                dc.DrawGeometry(Brush(r, g, b, 28), null, track.Glow[i]); dc.DrawGeometry(Brush(r, g, b), null, track.Body[i]);
            }
        }
    }

    private void Rebuild(ScenePackagesViewModel vm, PackagePreviewData data)
    {
        _data = data; _tracks.Clear();
        var unknownRow = 0;
        foreach (var track in data.Tracks)
        {
            var target = vm.Roles.FirstOrDefault(r => r.Dto.Role == track.Info.Role)?.Selected;
            var slot = CaseProjection.Slot(target?.Zone, track.Info.Role);
            var count = Math.Min(track.Info.LedCount, 256);
            var points = slot is not null ? CaseProjection.LightingPoints(slot, count) : Enumerable.Range(0, count).Select(i => new Point(30 + 700.0 * i / count, 12 + unknownRow * 7)).ToArray();
            if (slot is null) unknownRow++;
            var body = new Geometry[count]; var glow = new Geometry[count];
            for (var i = 0; i < count; i++)
            {
                var a = points[i]; var next = i + 1 < count ? points[i + 1] : slot == "waterblock-ring" ? points[0] : a;
                var connect = (next - a).Length < 110 && (slot?.EndsWith("fans", StringComparison.Ordinal) != true || i % 13 != 12);
                var line = new LineGeometry(a, connect ? next : new Point(a.X + .05, a.Y));
                body[i] = line.GetWidenedPathGeometry(new Pen(Brushes.White, slot == "waterblock-ring" ? 4 : 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                glow[i] = line.GetWidenedPathGeometry(new Pen(Brushes.White, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                body[i].Freeze(); glow[i].Freeze();
            }
            _tracks.Add(new(track, target, body, glow));
        }
    }

    private SolidColorBrush Brush(byte r, byte g, byte b, byte alpha = 255)
    {
        var key = (alpha << 24) | (r << 16) | (g << 8) | b;
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        if (_brushes.Count > 4096) _brushes.Clear();
        brush = new(Color.FromArgb(alpha, r, g, b)); brush.Freeze(); _brushes[key] = brush; return brush;
    }
}
