using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnifiedRgb.App.Views;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.UiQa;

#pragma warning disable VSTHRD101 // WPF Loaded event; DispatcherUnhandledException is recorded by the isolated harness.

/// <summary>Real WPF package page, memory-only RPCs, no Host/pipe/production data or USB.</summary>
internal static class LibraryQa
{
    public static void Run(IReadOnlyDictionary<string, string> options)
    {
        string Get(string key, string fallback) => options.GetValueOrDefault(key, fallback);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UnifiedRgb.App;component/Themes/StudioTheme.xaml", UriKind.Relative) });
        var fake = new LibraryFake(); var errors = new List<string>();
        var vm = new ScenePackagesViewModel(() => fake, () => Task.FromResult<IReadOnlyList<ControllerDto>>([]),
            () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([]), () => null, ex => errors.Add(ex.Message), _ => false, () => fake);
        var view = new ScenePackagesView { DataContext = vm, Margin = new Thickness(24) };
        var window = new Window { Title = "光域 · 场景库隔离验收（模拟 RPC）", Content = view, MinWidth = 900, MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(Color.FromRgb(16,20,27)) };
        var record = Path.GetFullPath(Get("--record", "library-qa.json")); Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        window.Loaded += async (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Width = double.Parse(Get("--width", "1280"), System.Globalization.CultureInfo.InvariantCulture) / dpi.DpiScaleX;
            window.Height = double.Parse(Get("--height", "800"), System.Globalization.CultureInfo.InvariantCulture) / dpi.DpiScaleY;
            await vm.RefreshAsync(); vm.Selected = vm.Packages[0]; await vm.ReadSelectionAsync();
            if (Get("--state", "normal") == "blocked") await vm.RemoveCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (fake.Writes != 0 || errors.Count != 0) throw new InvalidOperationException("Read-only library QA wrote data or raised an error.");
            var buttons = Descendants(view).OfType<Button>().Select(b => new { Label = b.Content as string, b.IsEnabled, b.ActualWidth, b.ActualHeight }).ToArray();
            File.WriteAllText(record, JsonSerializer.Serialize(new { Window = window.Title, Theme = "dark", State = Get("--state", "normal"),
                Dpi = dpi.PixelsPerInchX, Physical = new { Width = window.ActualWidth * dpi.DpiScaleX, Height = window.ActualHeight * dpi.DpiScaleY },
                PageDip = new { view.ActualWidth, view.ActualHeight }, NativeClientDip = "not measured", Data = new { Packages = vm.Packages.Count, Roles = vm.Roles.Count },
                ScrollOffsets = Descendants(view).OfType<ScrollViewer>().Select(s => new { s.VerticalOffset, s.HorizontalOffset }).ToArray(),
                vm.Usage, vm.Message, fake.Writes, Buttons = buttons, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
            var raster = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            raster.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(raster));
            using (var output = File.Create(record + ".diagnostic-layout.png")) png.Save(output);
            if (Get("--exit-after-check", "false") == "true") app.Shutdown();
        };
        app.Run(window);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }

    private sealed class LibraryFake : IScenePackageService, ISceneLibraryService
    {
        private static readonly string Id = new('1', 64);
        public int Writes { get; private set; }
        private static readonly ScenePackageInfoDto Info = new(Id, "generic-test", "通用主题场景 · 中文长名称与容量信息验证", 3, "rgb24-hold-physical-ms-v1", 12000, 1, 0, 1);
        public Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScenePackageInfoDto>>([Info]);
        public Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default) => Task.FromResult(new ScenePackageDetailsDto(Info,
            [new("strip", "主灯区 · 原包内容保持不变", "lighting", 8, 0, 0, 200, 12000, ["#5DDAC8", "#FAD66D"], null)]));
        public Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default) => Task.FromResult(new ScenePackageBindingStateDto(Id, null, [], false, ["No binding has been saved for this package."]));
        public Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default) => throw new InvalidOperationException("QA import not permitted");
        public Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default) => throw new InvalidOperationException("QA save not permitted");
        public Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default) => throw new InvalidOperationException("QA apply not permitted");
        public Task<ScenePackageApplyDto> ApplyScenePackageCheckedAsync(ScenePackageApplyRequestDto request, CancellationToken ct = default) => throw new InvalidOperationException("QA apply not permitted");
        public Task<SceneLibraryUsageDto> GetSceneLibraryUsageAsync(CancellationToken ct = default) => Task.FromResult(new SceneLibraryUsageDto(32, 32, 891289600, 1073741824, 2097152));
        public Task<ScenePackageRemovalPlanDto> InspectScenePackageRemovalAsync(string contentId, CancellationToken ct = default) => Task.FromResult(new ScenePackageRemovalPlanDto(Id, Info.Name, 1234, ["当前灯光播放", "屏幕重启恢复内容", "已保存场景：“中文长名称场景备份”"], []));
        public Task<ScenePackageRemovalDto> RemoveScenePackageAsync(ScenePackageRemovalRequestDto request, CancellationToken ct = default) { Writes++; throw new InvalidOperationException("QA remove not permitted"); }
        public Task<SceneProfileRemovalDto> RemoveSavedSceneAsync(string name, CancellationToken ct = default) { Writes++; throw new InvalidOperationException("QA delete not permitted"); }
    }
}
