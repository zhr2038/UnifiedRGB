using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using UnifiedRgb.App.Preview;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.Views;

public partial class PackageCasePreview : UserControl
{
    private ScenePackagesViewModel? _model;
    private PackagePreviewData? _data;
    private readonly List<PackageScreenPlayer> _players = [];
    private bool _hooked;
    private string _screenBindings = "";
    private long _lastRender;
    public PackageCasePreview()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        IsVisibleChanged += (_, _) => Hook(); Unloaded += (_, _) => { Stop(); Detach(); };
    }
    private void Attach()
    {
        if (!ReferenceEquals(_model, DataContext))
        {
            Detach(); _model = DataContext as ScenePackagesViewModel;
            if (_model is not null) _model.PreviewChanged += Changed;
            Drawing.Model = _model;
        }
        Hook();
    }
    private void Detach() { if (_model is not null) _model.PreviewChanged -= Changed; _model = null; Drawing.Model = null; }
    private void Hook()
    {
        if (IsLoaded && IsVisible && !_hooked) { CompositionTarget.Rendering += Render; _hooked = true; }
        else if ((!IsLoaded || !IsVisible) && _hooked) { CompositionTarget.Rendering -= Render; _hooked = false; }
    }
    private void Render(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastRender != 0 && Stopwatch.GetElapsedTime(_lastRender, now).TotalMilliseconds < 40) return;
        _lastRender = now;
        if (_model is not null) _ = _model.RefreshPreviewTelemetryAsync();
        if (_model?.PreviewPlaying == true) _model.AdvancePreview();
        else Draw();
    }
    private void Changed(object? sender, EventArgs e) => Draw();
    private void Draw()
    {
        Drawing.InvalidateVisual();
        var bindings = _model is null ? "" : string.Join("|", _model.Roles.Where(r => r.IsScreen).Select(r => r.Dto.Role + ":" + r.Selected?.ScreenId));
        if (!ReferenceEquals(_data, _model?.PreviewData) || _screenBindings != bindings)
        {
            _screenBindings = bindings;
            foreach (var player in _players) player.Close(); _players.Clear(); MediaCanvas.Children.Clear(); _data = _model?.PreviewData;
            if (_data is not null && _model is not null) foreach (var screen in _data.Info.Screens)
            {
                var target = _model.Roles.FirstOrDefault(r => r.Dto.Role == screen.Role)?.Selected;
                if (CaseProjection.ScreenSlot(screen.Role, target?.ScreenId) is not { } slot) continue;
                var player = new PackageScreenPlayer(_model, screen) { Width = slot.Bounds.Width, Height = slot.Bounds.Height };
                player.Clip = slot.Circle ? new EllipseGeometry(new Rect(0, 0, player.Width, player.Height)) : new RectangleGeometry(new Rect(0, 0, player.Width, player.Height));
                Canvas.SetLeft(player, slot.Bounds.X); Canvas.SetTop(player, slot.Bounds.Y); MediaCanvas.Children.Add(player); _players.Add(player);
            }
        }
        if (IsVisible) foreach (var p in _players) p.Tick();
    }
    private void Stop() { if (_hooked) { CompositionTarget.Rendering -= Render; _hooked = false; } foreach (var p in _players) p.Close(); _players.Clear(); _data = null; }
}
