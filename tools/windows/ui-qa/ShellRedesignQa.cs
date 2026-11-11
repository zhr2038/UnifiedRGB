using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Views;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.UiQa;

/// <summary>Actual native shell and view models, with an unstarted private pipe and memory-only screen service.</summary>
internal static class ShellRedesignQa
{
    public static void Run(IReadOnlyDictionary<string, string> options)
    {
        var directory = Path.GetFullPath(options.GetValueOrDefault("--record-dir", "shell-redesign-qa"));
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UnifiedRgb.App;component/Themes/StudioTheme.xaml", UriKind.Relative) });
        var dispatcher = new QaDispatcher(Dispatcher.CurrentDispatcher);
        // Never start this client: no connection to the production Host, tasks, settings, or devices.
        var client = new PipeClient("UnifiedRgb.UiQa.Unstarted");
        var main = new MainViewModel(client, dispatcher, new MemoryAutostart(), "qa.exe", directory, directory, _ => false);
        main.Studio.SetConnected(false);
        var screenService = new FakeScreenRpc();
        var errors = new List<string>();
        var screens = new ScreensViewModel(() => screenService, dispatcher, ex => errors.Add(ex.ToString()), () => null, _ => false, deviceSettings: () => screenService);
        var root = new ShellRoot(main, screens);
        var controllers = SampleControllers();
        foreach (var c in controllers) foreach (var z in c.Zones) main.Studio.Zones.Add(new StudioZone(c, z));
        main.Studio.Draft.Name = "全机联动 · 界面示例";
        main.Studio.Draft.Colors = "#D8B578";
        var window = new MainWindow { Title = "光域 · 新版界面验收 · 未连接硬件", DataContext = root };
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await screens.RefreshCommand.ExecuteAsync();
                var nav = (ListBox)window.FindName("MainNavigation");
                var report = new List<object>();
                var cases = new[] { (1460d, 940d), (1120d, 760d), (1000d, 640d) };
                foreach (var (width, height) in cases)
                {
                    window.Width = width; window.Height = height;
                    foreach (var item in ShellNavigation.Pages)
                    {
                        main.Studio.Page = item.Page;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        window.UpdateLayout();
                        if (!Equals(nav.SelectedValue, item.Page)) throw new InvalidOperationException("Page to navigation binding failed: " + item.Page);
                        var index = ShellNavigation.Pages.ToList().IndexOf(item);
                        nav.SelectedIndex = index;
                        if (main.Studio.Page != item.Page) throw new InvalidOperationException("Navigation to page binding failed.");
                        var pageHost = (Grid)window.FindName("PageHost");
                        var visible = pageHost.Children.OfType<UserControl>().Where(v => v.IsVisible).ToArray();
                        if (visible.Length != 1) throw new InvalidOperationException("Exactly one workspace must be visible.");
                        if (visible[0] is CaseControlPanelView or DevicesView)
                        {
                            var tabs = Program.Descendants(visible[0]).OfType<TabControl>().First();
                            for (var tab = 0; tab < tabs.Items.Count; tab++)
                            {
                                tabs.SelectedIndex = tab;
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                Save(window, Path.Combine(directory, $"{width:0}-page{item.Page}-tab{tab}.diagnostic-layout.png"));
                            }
                            tabs.SelectedIndex = 0;
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        }
                        var view = visible[0];
                        var bounds = view.TransformToAncestor(window).TransformBounds(new Rect(view.RenderSize));
                        if (bounds.Right > window.ActualWidth + 1 || bounds.Bottom > window.ActualHeight + 1)
                            throw new InvalidOperationException("Workspace extends outside window: " + item.Title);
                        var clipping = Program.Descendants(view).OfType<Button>().Where(b => b.IsVisible && b.ActualWidth > 0)
                            .Where(b => b.Content is string text && text.Length > 0)
                            .Select(b => new { Text = (string)b.Content, b.ActualWidth, b.ActualHeight }).ToArray();
                        report.Add(new { width, height, page = item.Page, title = item.Title, workspace = view.GetType().Name, view.ActualWidth, view.ActualHeight, buttons = clipping });
                        Save(window, Path.Combine(directory, $"{width:0}-page{item.Page}.diagnostic-layout.png"));
                    }
                }
                // Local shortcuts must keep the stable page IDs after navigation reorder.
                foreach (var id in new[] { 1, 2, 4, 7 })
                {
                    main.Studio.NavigateCommand.Execute(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (main.Studio.Page != id) throw new InvalidOperationException("Overview shortcut failed.");
                }
                if (screenService.Writes.Count != 0 || errors.Count != 0) throw new InvalidOperationException("Unexpected writes or UI failures: " + string.Join(";", errors));
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { Passed = true, NoProductionConnection = true, screenService.Writes, Errors = errors, Pages = report }, new JsonSerializerOptions { WriteIndented = true }));
                window.Width = 1120; window.Height = 760; main.Studio.Page = 7;
                if (options.GetValueOrDefault("--exit-after-check") == "true") app.Shutdown();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "failed.txt"), ex.ToString()); Environment.ExitCode = 1; app.Shutdown();
            }
        };
        window.Closed += (_, _) => main.Dispose();
        app.Run(window);
    }

    private static void Save(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }

    private static ControllerDto[] SampleControllers() =>
    [
        Sample("lianli:rf:1B-7B-F1-qa", "顶排", 78), Sample("lianli:rf:06-A8-F0-qa", "侧排", 78),
        Sample("lianli:rf:35-F7-EF-qa", "底排", 78), Sample("lianli:rf:EF-7E-63-qa", "冷头", 24),
        Sample("enedram:qa:0x71", "内存一", 8), Sample("enedram:qa:0x73", "内存二", 8),
        Sample("asusaura:qa", "主板", 2), Sample("msigpu:qa", "显卡", 12),
    ];

    private static ControllerDto Sample(string id, string name, int leds) => new(id, "QA", name, "Virtual", "Linear", "", [new(0, name, "Linear", leds, 0, 0)], 60, "Active", null, true);

    private sealed class MemoryAutostart : IAutostartRegistry
    {
        public string? Get() => null;
        public void Set(string? command) => throw new InvalidOperationException("QA cannot change autostart.");
    }
}

internal sealed class ShellRoot(MainViewModel main, ScreensViewModel screens)
{
    public StudioViewModel Studio => main.Studio;
    public ScreensViewModel Screens => screens;
    public DevicesViewModel Devices => main.Devices;
    public ProfilesViewModel Profiles => main.Profiles;
    public ScenePackagesViewModel Packages => main.Packages;
    public DiagnosticsViewModel Diagnostics => main.Diagnostics;
    public SettingsViewModel Settings => main.Settings;
    public HostStartViewModel HostStart => main.HostStart;
    public int WorkbenchSourceIndex { get; set; }
    public string ConnectionText => "界面验收 · 模拟数据";
    public string Error => string.Empty;
    public RelayCommand RefreshCommand { get; } = new(() => { });
    public RelayCommand DismissErrorCommand { get; } = new(() => { });
}
