using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Preview;

/// <summary>Source-media scrubbing only; does not own a screen transport.</summary>
public sealed class PackageScreenPlayer : Grid, IDisposable
{
    private readonly MediaElement _video = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, Volume = 0, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    private readonly Image _still = new() { Stretch = Stretch.Uniform };
    private readonly ScenePackagePreviewScreenDto _info;
    private readonly ScenePackagesViewModel _vm;
    private readonly CancellationTokenSource _stop = new();
    private string? _file;
    private string? _request;
    private bool _opened;
    private bool _closed;
    private double _wantedMs;
    private PreviewGif? _gif;
    private readonly TextBlock _overlay = new() { Foreground = Brushes.White, FontSize = 7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3), IsHitTestVisible = false };
    private readonly Border _overlayBox = new() { IsHitTestVisible = false };

    public PackageScreenPlayer(ScenePackagesViewModel vm, ScenePackagePreviewScreenDto info)
    {
        _vm = vm; _info = info; Background = Brushes.Black; ClipToBounds = true;
        Children.Add(_still); Children.Add(_video);
        _overlayBox.Child = _overlay; Children.Add(_overlayBox);
        _video.MediaOpened += (_, _) => { if (_closed) return; _opened = true; _video.Pause(); SetPosition(); };
        _video.MediaFailed += (_, _) => { _opened = false; ToolTip = "此素材无法由本机媒体组件预演；可在屏幕页生成实际编码预览。"; };
        Unloaded += (_, _) => Close();
    }

    public void Tick()
    {
        if (_closed || _vm.PreviewData is not { } data || ScenePackagePlayback.Validate(_vm.PreviewPlayback) is not null) return;
        var source = ScenePackagePlayback.SourcePosition(_vm.PreviewPositionMs, data.Info.DurationMs, _vm.PreviewPlayback);
        source %= Math.Max(1, _info.Clips.Sum(c => c.DurationMs));
        var offset = 0.0; var clip = _info.Clips.Count == 0 ? null : _info.Clips[^1];
        foreach (var candidate in _info.Clips) { if (source < offset + candidate.DurationMs) { clip = candidate; break; } offset += candidate.DurationMs; }
        if (clip is null) return;
        _wantedMs = Math.Clamp(source - offset, 0, Math.Max(0, clip.DurationMs - 1));
        var row = _vm.Roles.FirstOrDefault(r => r.Dto.Role == _info.Role);
        Opacity = (row?.ScreenBrightness ?? 100) / 100;
        Visibility = row?.ScreenEnabled == false ? Visibility.Hidden : Visibility.Visible;
        var fit = row?.ScreenFit ?? _info.Fit; _video.Stretch = _still.Stretch = fit == "cover" ? Stretch.UniformToFill : Stretch.Uniform;
        UpdateOverlay(row);
        if (_vm.PreviewCache.TryGetFile(clip.Sha256, out var path))
        {
            if (_file != path) { _file = path; _ = OpenAsync(path); }
            SetPosition();
        }
        else if (_request != clip.Sha256)
        {
            _request = clip.Sha256; _ = ReadAsync(data.Info.ContentId, clip);
        }
    }

    private async Task ReadAsync(string contentId, ScenePackagePreviewClipDto clip)
    {
        try
        {
            var file = await _vm.PreviewCache.GetMediaAsync(contentId, clip, _stop.Token).ConfigureAwait(true);
            if (!_closed && _request == clip.Sha256) { _file = file; await OpenAsync(file).ConfigureAwait(true); SetPosition(); }
        }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031 // a failed preview never escapes onto the UI dispatcher or changes hardware
        catch (Exception ex) { if (!_closed) ToolTip = "素材预演未完成：" + ex.Message; }
#pragma warning restore CA1031
    }
    private async Task OpenAsync(string file)
    {
        _opened = false; _video.Close(); _video.Source = null; _gif = null; _still.Source = null;
        try
        {
        if (Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            _still.Source = null; _video.Source = new Uri(file); _video.Play();
        }
        else if (Path.GetExtension(file).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var gif = await PreviewGif.ReadAsync(file, _stop.Token).ConfigureAwait(true);
            if (!_closed && _file == file) { _gif = gif; SetPosition(); }
        }
        else
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 640; image.UriSource = new Uri(file); image.EndInit(); image.Freeze(); _still.Source = image;
        }
        }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031 // local preview decoder failure must not enter the app fault state
        catch (Exception ex) { if (!_closed) ToolTip = "素材预演失败：" + ex.Message; }
#pragma warning restore CA1031
    }
    private void SetPosition()
    {
        if (_opened && Math.Abs(_video.Position.TotalMilliseconds - _wantedMs) > 18) _video.Position = TimeSpan.FromMilliseconds(_wantedMs);
        if (_gif is not null) _still.Source = _gif.At(_wantedMs);
    }
    private void UpdateOverlay(PackageRoleChoice? row)
    {
        var kind = row?.ScreenOverlay ?? _info.Overlay; _overlayBox.Visibility = kind == "none" ? Visibility.Collapsed : Visibility.Visible;
        if (kind == "none") return;
        var data = _vm.TelemetrySnapshot; var fresh = data is not null && DateTimeOffset.UtcNow - data.Timestamp < TimeSpan.FromSeconds(5);
        static string Value(double? v, string unit) => v is { } n ? n.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + unit : "—";
        _overlay.Text = fresh ? $"CPU  {Value(data!.CpuUsagePercent, "%")}    GPU  {Value(data.GpuUsagePercent, "%")}\nGPU  {Value(data.GpuTemperatureC, "°C")}    RAM  {Value(data.MemoryUsedBytes / 1073741824.0, " GB")}" : "电脑数据待更新";
        var region = kind == "dashboard" && row?.UseRegion == true;
        var x = region ? Math.Clamp(row!.RegionX / 100, 0, 1) : 0; var y = region ? Math.Clamp(row!.RegionY / 100, 0, 1) : .55;
        var w = region ? Math.Clamp(row!.RegionWidth / 100, .01, 1 - x) : 1; var h = region ? Math.Clamp(row!.RegionHeight / 100, .01, 1 - y) : .45;
        _overlayBox.HorizontalAlignment = HorizontalAlignment.Left; _overlayBox.VerticalAlignment = VerticalAlignment.Top;
        _overlayBox.Margin = new Thickness(x * Width, y * Height, 0, 0); _overlayBox.Width = w * Width; _overlayBox.Height = h * Height;
        var alpha = region ? Math.Clamp(row!.RegionOpacity / 100, 0, 1) : .6;
        _overlayBox.Background = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), 0, 0, 0));
        var color = row?.ScreenAccent ?? _info.AccentColor;
        if (color.Length == 7 && color[0] == '#' && color.AsSpan(1).ToString().All(char.IsAsciiHexDigit)) _overlay.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
    public void Close() => Dispose();
    public void Dispose()
    {
        if (_closed) return; _closed = true; _stop.Cancel(); _stop.Dispose(); _video.Close();
    }
}
