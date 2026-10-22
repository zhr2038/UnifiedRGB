using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

public sealed partial class PackageRoleChoice
{
    private ScenePackagePreviewScreenDto? _originalScreen;
    private double _screenBrightness = 100, _regionX, _regionY, _regionWidth = 100, _regionHeight = 100, _regionOpacity;
    private int _screenFps = 5, _regionColumns = 2;
    private string _screenOverlay = "none", _screenAccent = "#5DDAC8", _screenFit = "contain";
    private bool _screenEnabled = true, _useRegion;
    public bool IsScreen => Dto.Kind == "screen";
    public bool AllowsRegion => Selected?.Family is { } family ? family == "Turzx" : _originalScreen is { } s && s.Width != s.Height;
    public bool ShowScreenEditor => IsScreen && _originalScreen is not null;
    public double ScreenBrightness { get => _screenBrightness; set => SetProperty(ref _screenBrightness, value); }
    public int ScreenFps { get => _screenFps; set => SetProperty(ref _screenFps, value); }
    public string ScreenOverlay { get => _screenOverlay; set => SetProperty(ref _screenOverlay, value); }
    public string ScreenAccent { get => _screenAccent; set => SetProperty(ref _screenAccent, value); }
    public string ScreenFit { get => _screenFit; set => SetProperty(ref _screenFit, value); }
    public bool ScreenEnabled { get => _screenEnabled; set => SetProperty(ref _screenEnabled, value); }
    public bool UseRegion { get => _useRegion; set => SetProperty(ref _useRegion, value); }
    public double RegionX { get => _regionX; set => SetProperty(ref _regionX, value); }
    public double RegionY { get => _regionY; set => SetProperty(ref _regionY, value); }
    public double RegionWidth { get => _regionWidth; set => SetProperty(ref _regionWidth, value); }
    public double RegionHeight { get => _regionHeight; set => SetProperty(ref _regionHeight, value); }
    public int RegionColumns { get => _regionColumns; set => SetProperty(ref _regionColumns, value); }
    public double RegionOpacity { get => _regionOpacity; set => SetProperty(ref _regionOpacity, value); }

    internal void LoadScreen(ScenePackagePreviewScreenDto original, ScenePackageScreenEditDto? edit)
    {
        _originalScreen = original;
        ScreenBrightness = (edit?.Brightness ?? 1) * 100; ScreenFps = edit?.Fps ?? original.Fps;
        ScreenOverlay = edit?.Overlay ?? original.Overlay; ScreenAccent = edit?.AccentColor ?? original.AccentColor;
        ScreenFit = edit?.Fit ?? original.Fit; ScreenEnabled = edit?.Enabled ?? true;
        var region = edit?.ClearOverlayLayout == true ? null : edit?.OverlayLayout ?? original.OverlayLayout;
        UseRegion = region is not null; RegionX = (region?.X ?? 0) * 100; RegionY = (region?.Y ?? 0) * 100;
        RegionWidth = (region?.Width ?? 1) * 100; RegionHeight = (region?.Height ?? 1) * 100;
        RegionColumns = region?.Columns ?? 2; RegionOpacity = (region?.BackgroundOpacity ?? 0) * 100;
        OnPropertyChanged(nameof(ShowScreenEditor)); OnPropertyChanged(nameof(AllowsRegion));
    }

    internal ScenePackageScreenEditDto? BuildScreenEdit()
    {
        if (_originalScreen is not { } s) return null;
        var region = UseRegion && ScreenOverlay == "dashboard" ? new ScreenOverlayLayoutDto(RegionX / 100, RegionY / 100, RegionWidth / 100, RegionHeight / 100, RegionColumns, RegionOpacity / 100) : null;
        var regionChanged = !SameRegion(region, s.OverlayLayout);
        if (ScreenBrightness == 100 && ScreenFps == s.Fps && ScreenOverlay == s.Overlay && ScreenAccent == s.AccentColor && ScreenFit == s.Fit && ScreenEnabled && !regionChanged) return null;
        return new(Dto.Role, ScreenBrightness == 100 ? null : ScreenBrightness / 100, ScreenOverlay == s.Overlay ? null : ScreenOverlay,
            ScreenAccent == s.AccentColor ? null : ScreenAccent, ScreenFit == s.Fit ? null : ScreenFit, ScreenFps == s.Fps ? null : ScreenFps,
            ScreenEnabled ? null : false, regionChanged ? region : null, regionChanged && region is null);
    }

    private static bool SameRegion(ScreenOverlayLayoutDto? a, ScreenOverlayLayoutDto? b) => a == b || a is not null && b is not null
        && Math.Abs(a.X - b.X) < 1e-10 && Math.Abs(a.Y - b.Y) < 1e-10 && Math.Abs(a.Width - b.Width) < 1e-10
        && Math.Abs(a.Height - b.Height) < 1e-10 && a.Columns == b.Columns && Math.Abs(a.BackgroundOpacity - b.BackgroundOpacity) < 1e-10;
}
