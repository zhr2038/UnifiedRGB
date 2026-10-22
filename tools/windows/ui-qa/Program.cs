using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Views;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.UiQa;

#pragma warning disable VSTHRD101 // WPF Loaded is an event boundary; the harness records DispatcherUnhandledException.
#pragma warning disable VSTHRD003 // The busy-state scenario deliberately returns a test-owned incomplete RPC task.

internal static class Program
{
    private static readonly Queue<string> InputTrace = new();
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "qa-startup-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
    }

    private static void Run(string[] args)
    {
        var launchFile = Path.Combine(AppContext.BaseDirectory, "qa-options.json");
        if (args.Length == 0 && File.Exists(launchFile)) args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(launchFile))!;
        if (args.Any(x => !x.StartsWith("--", StringComparison.Ordinal) || !x.Contains('=', StringComparison.Ordinal)))
            throw new ArgumentException("QA options must be complete --name=value arguments; quote values containing spaces.");
        var options = args.Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        string Get(string key, string fallback) => options.GetValueOrDefault(key, fallback);
        if (Get("--workbench", "false") == "true") { WorkbenchQa.Run(options); return; }
        if (Get("--library", "false") == "true") { LibraryQa.Run(options); return; }
        var record = Path.GetFullPath(Get("--record", "ui-qa-state.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        if (Get("--software", "false") == "true") RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UnifiedRgb.App;component/Themes/StudioTheme.xaml", UriKind.Relative) });
        var service = new FakeScreenRpc();
        var dispatcher = new QaDispatcher(Dispatcher.CurrentDispatcher);
        var vm = new ScreensViewModel(() => service, dispatcher, _ => { }, () => null, _ => false, deviceSettings: () => service);
        var root = new QaRoot(vm);
        var baseline = Get("--baseline", "false") == "true";
        Window window = Get("--shell", "false") == "true" ? new MainWindow { DataContext = root }
            : new Window { Content = new ScreenWorkspaceView { DataContext = vm, Margin = new Thickness(20) }, MinWidth = 640, MinHeight = 440, Background = new SolidColorBrush(Color.FromRgb(16, 20, 27)), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Title = baseline ? "光域 · 屏幕页整改前（模拟 RPC）" : "光域 · 屏幕页 M1 验收（模拟 RPC）";
        var theme = Get("--theme", "dark");
        var scenario = Get("--state", "normal");
        var width = double.Parse(Get("--width", "1280"), CultureInfo.InvariantCulture);
        var height = double.Parse(Get("--height", "800"), CultureInfo.InvariantCulture);
        var errors = new List<string>();
        window.PreviewKeyDown += (_, e) => TraceInput($"key={e.Key}; source={DescribeFocus(e.OriginalSource as IInputElement)}; focus={DescribeFocus(Keyboard.FocusedElement)}");
        window.PreviewGotKeyboardFocus += (_, e) => TraceInput($"focus={DescribeFocus(e.NewFocus)}");
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); File.WriteAllText(record + ".error.txt", string.Join("\n", errors)); e.Handled = true; };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => SaveMetrics(window, vm, service, baseline, scenario, record, errors);
        window.Closed += (_, _) => timer.Stop();
        window.Loaded += async (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Width = width / dpi.DpiScaleX; window.Height = height / dpi.DpiScaleY;
            var current = Descendants(window).OfType<ScreenWorkspaceView>().Single();
            if (baseline)
            {
                var legacy = new UnifiedRgb.App.Views.LegacyScreenWorkspaceView { DataContext = vm };
                if (window.Content == current) { legacy.Margin = current.Margin; window.Content = legacy; }
                else
                {
                    var parent = (Panel)VisualTreeHelper.GetParent(current);
                    parent.Children.Remove(current); parent.Children.Add(legacy);
                }
            }
            else
            {
                ((ComboBox)current.FindName("AppearancePicker")).SelectedIndex = theme == "light" ? 1 : 0;
            }

            if (scenario == "empty") service.Empty = true;
            await vm.RefreshCommand.ExecuteAsync();
            if (vm.Screens.Count > 0)
            {
                vm.Selected = vm.Screens[^1];
                await vm.ReadDeviceSettingsAsync();
                if (scenario != "empty") await vm.PreviewCommand.ExecuteAsync();
            }
            if (scenario == "error") { service.FailPreview = true; await vm.PreviewCommand.ExecuteAsync(); }
            if (scenario == "readonly") vm.MarkDisconnected();
            if (scenario == "draft") { vm.Background = "#172B3D"; vm.Rotation = 90; }
            if (scenario == "busy") { service.HoldPreview = true; _ = vm.PreviewCommand.ExecuteAsync(); }
            if (!baseline && Get("--panel", "preview") == "properties") ((Button)current.FindName("PropertiesTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!baseline && Get("--details", "false") == "true")
            {
                ((Expander)current.FindName("LocalSettings")).IsExpanded = true;
                ((Expander)current.FindName("EncodingDetails")).IsExpanded = true;
            }
            if (!baseline && Get("--error-details", "false") == "true") ((Expander)current.FindName("CompactErrorDetails")).IsExpanded = true;
            if (Get("--maximized", "false") == "true") window.WindowState = WindowState.Maximized;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!baseline && Get("--scroll", "top") == "bottom") ((ScrollViewer)current.FindName("PropertiesRegion")).ScrollToBottom();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!baseline && Get("--verify", "false") == "true")
                await UiQaChecks.RunAsync(window, current, vm, service, record + ".checks.json", Get("--verify-writes", "false") == "true");
            if (Get("--fault", "false") == "true")
            {
                if (window is not MainWindow shell) throw new InvalidOperationException("Fault QA needs --shell=true.");
                var draft = vm.Background;
                shell.ShowFaultState(UiExceptionCoordinator.FaultMessage);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var notice = (Border)shell.FindName("FaultNotice");
                var text = (TextBlock)shell.FindName("FaultNoticeText");
                var pages = (UIElement)shell.FindName("PageHost");
                if (pages.IsEnabled || !notice.IsVisible || text.Text != UiExceptionCoordinator.FaultMessage || vm.Background != draft)
                    throw new InvalidOperationException("Unsafe UI fault must remain visible, disable page input and preserve draft state.");
                File.WriteAllText(record + ".fault-check.json", JsonSerializer.Serialize(new { Passed = true, PageEnabled = pages.IsEnabled,
                    NoticeVisible = notice.IsVisible, Message = text.Text, DraftPreserved = vm.Background == draft, service.Writes, Errors = errors }));
            }
            timer.Start(); SaveMetrics(window, vm, service, baseline, scenario, record, errors);
            if (Get("--diagnostic-render", "false") == "true")
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // Diagnostic layout raster only: never label this as a desktop screenshot or M2 visual acceptance.
                var raster = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                raster.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(raster));
                using var file = File.Create(record + ".diagnostic-layout.png"); png.Save(file);
            }
            if (Get("--exit-after-check", "false") == "true") app.Shutdown();
        };
        app.Run(window);
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static void SaveMetrics(Window window, ScreensViewModel vm, FakeScreenRpc service, bool baseline, string scenario, string path, List<string> errors)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        GetWindowRect(hwnd, out var outer); GetClientRect(hwnd, out var client);
        var dpi = VisualTreeHelper.GetDpi(window);
        var page = Descendants(window).OfType<UserControl>().FirstOrDefault(x => x is ScreenWorkspaceView || x is UnifiedRgb.App.Views.LegacyScreenWorkspaceView);
        var scrolls = page is null ? [] : Descendants(page).OfType<ScrollViewer>().Select(x => new { name = x.Name, x.IsVisible, x.VerticalOffset, x.ViewportHeight, x.ExtentHeight }).ToArray();
        DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int));
        var payload = new
        {
            capturedAt = DateTimeOffset.Now, windowTitle = window.Title, baseline, scenario,
            window.IsActive, window.IsVisible, window.WindowState,
            desktop = DesktopName(), renderMode = RenderOptions.ProcessRenderMode.ToString(),
            foregroundMatches = GetForegroundWindow() == hwnd, cloaked,
            keyboardFocus = (Keyboard.FocusedElement as FrameworkElement)?.Name,
            keyboardFocusDescription = DescribeFocus(Keyboard.FocusedElement),
            logicalFocus = (FocusManager.GetFocusedElement(window) as FrameworkElement)?.Name,
            inputTrace = InputTrace.ToArray(),
            effectiveDpi = new { x = GetDpiForWindow(hwnd), wpfX = dpi.PixelsPerInchX, wpfY = dpi.PixelsPerInchY },
            physicalWindow = new { outer.Left, outer.Top, width = outer.Right - outer.Left, height = outer.Bottom - outer.Top },
            physicalClient = new { width = client.Right, height = client.Bottom },
            clientDip = new { width = client.Right / dpi.DpiScaleX, height = client.Bottom / dpi.DpiScaleY },
            pageDip = new { width = page?.ActualWidth, height = page?.ActualHeight },
            theme = page is ScreenWorkspaceView v ? v.AppearanceName : "LegacyDark",
            layout = page is ScreenWorkspaceView p ? p.LayoutMode.ToString() : "LegacyFixed",
            selected = vm.Selected?.Id, contentDirty = vm.Dirty, localDirty = vm.DeviceSettingsDirty,
            busy = vm.EditorBusy, error = vm.ErrorText, writes = service.Writes.ToArray(), scrolls, errors = errors.ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TraceInput(string text)
    {
        InputTrace.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}");
        while (InputTrace.Count > 30) InputTrace.Dequeue();
    }

    private static string DescribeFocus(IInputElement? element) => element switch
    {
        FrameworkElement { Name.Length: > 0 } named => named.GetType().Name + ":" + named.Name,
        ContentControl { Content: string caption } content => content.GetType().Name + ":" + caption,
        _ => element?.GetType().Name ?? "none",
    };

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder text, uint length, out uint needed);
    private static string DesktopName()
    {
        var text = new StringBuilder(256);
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, text, 512, out _) ? text.ToString() : "unavailable";
    }
}

internal sealed class QaDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) { if (dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action); }
}

internal sealed class QaRoot(ScreensViewModel screens)
{
    public ScreensViewModel Screens => screens;
    public QaStudio Studio { get; } = new();
    public string ConnectionText => "模拟 RPC · 未连接硬件";
    public string Error => string.Empty;
    public RelayCommand RefreshCommand { get; } = new(() => { });
}

internal sealed class QaStudio : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private int _page = 2;
    public int Page { get => _page; set { if (_page != value) { _page = value; PropertyChanged?.Invoke(this, new(nameof(Page))); } } }
    public string PageTitle => "屏幕内容";
    public string PageSubtitle => "选择背景与电脑信息，预览后再应用到所选屏幕。";
    public string Health => "验收模式 · 不连接真实硬件";
    public string Updated => "验收数据 · 固定测试场景";
    public string Written => "模拟服务，写入记录仅保存在内存";
    public string Clock => "QA";
    public RelayCommand BlackoutCommand { get; } = new(() => { });
    public RelayCommand RestoreCommand { get; } = new(() => { });
}

internal sealed class FakeScreenRpc : IScreenService, IScreenDeviceSettingsService
{
    public List<string> Writes { get; } = [];
    public bool Empty { get; set; }
    public bool FailPreview { get; set; }
    public bool HoldPreview { get; set; }
    private readonly TaskCompletionSource<ScreenPreviewDto> _previewGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScreenSceneDto _scene = new([new("screen:h2:qa", Overlay: "dashboard", Rotation: 180), new("screen:turzx:qa", "aurora", Overlay: "compact", Fps: 10)]);
    private readonly Dictionary<string, ScreenDeviceSettingsDto> _settings = new();

    public Task<IReadOnlyList<ScreenDeviceDto>> ListScreensAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>(Empty ? [] :
    [
        new("screen:tl-lcd:qa0001", "TL test 1", "TlLcd", 400, 400, "Running", null, false, 1200, 0, null, 12.8),
        new("screen:tl-lcd:qa0002", "TL test 2", "TlLcd", 400, 400, "Running", null, false, 1200, 0, null, 12.8),
        new("screen:tl-lcd:qa0003", "TL test 3", "TlLcd", 400, 400, "Running", null, false, 1200, 0, null, 12.8),
        new("screen:h2:qa", "H2 test", "H2", 480, 480, "Running", null, false, 1200, 0, null, 12.7),
        new("screen:turzx:qa", "TURZX test", "Turzx", 1920, 480, "Running", null, false, 1200, 0, null, 8.8),
    ]);
    public Task<PcTelemetryDto> GetPcTelemetryAsync(CancellationToken ct = default) => Task.FromResult(new PcTelemetryDto(DateTimeOffset.UtcNow, 24, 20L << 30, 64L << 30, 36, 48, 8L << 30, 32L << 30, "fake", null));
    public Task<IReadOnlyList<ScreenAssetDto>> ListScreenAssetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScreenAssetDto>>([new("aurora", "极光漫游－超长中文素材名称－深蓝夜空与青色光带－十二秒无缝循环.gif", 1920, 480, 120, 12000, "animation")]);
    public Task<ScreenAssetDto> UploadScreenAssetAsync(ScreenAssetUploadDto asset, CancellationToken ct = default) { Writes.Add("UploadAsset"); return Task.FromResult(new ScreenAssetDto("new", asset.Name, 400, 400, 1, 0, "image")); }
    public Task DeleteScreenAssetAsync(string assetId, CancellationToken ct = default) { Writes.Add("DeleteAsset"); return Task.CompletedTask; }
    public Task<ScreenSceneDto> GetScreenSceneAsync(CancellationToken ct = default) => Task.FromResult(_scene);
    public Task SetScreenSceneAsync(ScreenSceneDto scene, CancellationToken ct = default) { Writes.Add("SetScreenScene"); _scene = scene; return Task.CompletedTask; }
    public Task StopScreensAsync(CancellationToken ct = default) { Writes.Add("StopScreens"); return Task.CompletedTask; }
    public Task<ScreenRestartDto> RestartFanLcdAsync(CancellationToken ct = default) { Writes.Add("RestartFanLcd"); return Task.FromResult(new ScreenRestartDto(true, true, 1, [], "Restarted")); }
    public Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string id, CancellationToken ct = default) => Task.FromResult(_settings.GetValueOrDefault(id, new ScreenDeviceSettingsDto(id, id.Contains(":h2:", StringComparison.Ordinal) ? 180 : 0, "legacy", false)));
    public Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto value, CancellationToken ct = default)
    {
        Writes.Add("SetScreenDeviceSettings"); var saved = new ScreenDeviceSettingsDto(value.ScreenId, value.Rotation, value.EncodingQuality, false); _settings[value.ScreenId] = saved;
        _scene = new(_scene.Assignments.Select(x => x.ScreenId == value.ScreenId ? x with { Rotation = value.Rotation } : x).ToArray());
        return Task.FromResult(saved);
    }
    public Task<ScreenPreviewDto> PreviewScreenAsync(ScreenAssignmentDto assignment, CancellationToken ct = default)
    {
        if (FailPreview) throw new InvalidOperationException("编码失败：测试帧超过容量限制。当前草稿已保留，请降低内容复杂度后重新生成预览。此处为模拟错误，不涉及设备通信。");
        if (HoldPreview) return _previewGate.Task;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 31, 48)), null, new Rect(0, 0, 1920, 480));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(93, 218, 200)), 8);
            for (var i = 0; i < 12; i++) dc.DrawLine(pen, new Point(i * 180 - 120, 460), new Point(i * 180 + 260, 20));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 15, 31, 48)), null, new Rect(0, 315, 1920, 165));
            var text = new FormattedText("CPU 24%      GPU 36% · 48°C      RAM 20 / 64 GB", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 60, Brushes.White, 1);
            dc.DrawText(text, new Point(100, 355));
        }
        var bitmap = new RenderTargetBitmap(1920, 480, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream);
        return Task.FromResult(new ScreenPreviewDto(Convert.ToBase64String(stream.ToArray()), 1920, 480, "模拟 RPC · 已编码 PNG 测试帧，未进行 USB 输出"));
    }
}
