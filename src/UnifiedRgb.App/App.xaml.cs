using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Tray;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.App;

/// <summary>Posts to the WPF dispatcher (the GUI's IUiDispatcher).</summary>
internal sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>
/// Tray app (ARCHITECTURE §9): one instance per user session, unelevated, talks to the Host only over the pipe. Closing the
/// window hides it to the tray; Exit in the tray menu quits. <c>--minimized</c> (the Run entry) starts in the tray.
/// </summary>
#pragma warning disable CA1001 // process-lifetime fields; released in OnExit (Application is not IDisposable)
public partial class App : Application
#pragma warning restore CA1001
{
    private Mutex? _single;
    private PipeClient? _client;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private readonly CancellationTokenSource _startupStop = new();

    public static string HostDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UnifiedRgb");

    public static string AppLogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\UnifiedRgb.App", out var first);
        if (!first)
        {
            _ = EnsureHostForExistingAppAsync();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppLogDirectory, "app-.log"), rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 5 * 1024 * 1024, retainedFileCountLimit: 10, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };

        _client = new PipeClient(PipeNames.Default);
        _client.StateChanged += (_, s) => Log.Information("Host connection {State}", s);
        var vm = new MainViewModel(
            _client,
            new WpfDispatcher(Dispatcher),
            new RunKeyAutostart(),
            Environment.ProcessPath ?? "UnifiedRgb.App.exe",
            HostDataDirectory,
            AppLogDirectory,
            q => MessageBox.Show(q, "光域", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
            () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入屏幕背景或动画", Filter = "图片、GIF 与 MP4 视频|*.png;*.jpg;*.jpeg;*.gif;*.mp4", Multiselect = false };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            },
            () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入独立场景包", Filter = "场景包 ZIP|*.zip", Multiselect = false };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            });
        _vm = vm;
        _window = new MainWindow { DataContext = vm };
        _window.Closing += OnWindowClosing;
        _tray = new TrayIcon(ShowWindow, () => vm.Devices.RescanCommand.Execute(null), () => _ = ExitAsync());
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.TrayState))
            {
                _tray.Set(vm.TrayState);
            }
            else if (args.PropertyName == nameof(MainViewModel.Error) && vm.Error.Length > 0)
            {
                Log.Warning("{Error}", vm.Error);
            }
        };
        _client.Start();
        _ = EnsureHostAsync();
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            ShowWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _startupStop.Cancel();
        _startupStop.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private async Task EnsureHostForExistingAppAsync()
    {
        // Reopening the desktop entry can also recover a stopped backend while the original GUI stays in the tray.
        var exitCode = 0;
        try
        {
            await using var connection = new PipeClient(PipeNames.Default);
            connection.Start();
            await HostStartup.EnsureAsync(connection.WaitConnectedAsync, () => HostStartup.StartTaskAsync(_startupStop.Token), _startupStop.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_exiting) { }
#pragma warning disable CA1031 // this secondary instance has no view model; show a useful startup error instead of exiting silently
        catch (Exception ex)
#pragma warning restore CA1031
        {
            exitCode = 1;
            MessageBox.Show(ex.Message, "光域 · 后台启动", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Shutdown(exitCode);
    }

    private async Task EnsureHostAsync()
    {
        if (_client is null) return;
        try
        {
            if (await HostStartup.EnsureAsync(_client.WaitConnectedAsync, () => HostStartup.StartTaskAsync(_startupStop.Token), _startupStop.Token).ConfigureAwait(true))
                Log.Information("Requested existing Host task at app startup; waiting for trusted IPC");
        }
        catch (OperationCanceledException) when (_exiting) { }
#pragma warning disable CA1031 // startup failure is visible in the GUI and log, never an unobserved UI task
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Warning(ex, "Could not request Host startup");
            if (!_exiting && _client.State != ConnectionState.Connected) _vm?.ReportError(ex);
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            _window?.Hide();
        }
    }

    private async Task ExitAsync()
    {
        _exiting = true;
        await _startupStop.CancelAsync().ConfigureAwait(true);
        _window?.Close();
        _vm?.Dispose(); // stops the bring-up heartbeat; closing the pipe below ends this connection's sessions in the Host
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(true);
        }

        Shutdown(0);
    }
}
