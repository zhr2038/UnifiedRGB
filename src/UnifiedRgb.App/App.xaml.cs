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
    private const string SingleInstanceMutexName = @"Local\UnifiedRgb.App";
    private const string ShowWindowEventName = @"Local\UnifiedRgb.App.ShowWindow";

    private Mutex? _single;
    private bool _ownsSingleInstance;
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _wakeStop;
    private Thread? _wakeThread;
    private PipeClient? _client;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private readonly CancellationTokenSource _startupStop = new();
    private UiExceptionCoordinator? _uiFaults;

    public static string HostDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UnifiedRgb");

    public static string AppLogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        using var maintenance = InstallationGate.TryEnter();
        if (maintenance is null)
        {
            MessageBox.Show("光域正在安装或更新，请完成后重新打开。", "光域", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _single = new Mutex(true, SingleInstanceMutexName, out var first);
        _ownsSingleInstance = first;
        if (!first)
        {
            maintenance.Dispose();
            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            {
                SignalExistingWindow();
            }
            _ = EnsureHostForExistingAppAsync();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _wakeStop = new CancellationTokenSource();
        StartWakeListener(_showWindowEvent, _wakeStop.Token);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppLogDirectory, "app-.log"), rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 5 * 1024 * 1024, retainedFileCountLimit: 10, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        var uiFaults = new UiExceptionCoordinator(
            LogUiException,
            reason => _client?.BlockForUiFault(reason),
            ShowUiFault,
            ex => _vm?.ReportError(ex),
            () => _exiting);
        _uiFaults = uiFaults;
        DispatcherUnhandledException += (_, args) => args.Handled = uiFaults.Handle(args.Exception);

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
            },
            startHostTask: HostStartup.StartTaskAsync);
        _vm = vm;
        vm.HostStart.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HostStartViewModel.Status) && vm.HostStart.HasStatus) Log.Information("Host start: {Status}", vm.HostStart.Status);
        };
        if (e.Args.Contains("--workbench", StringComparer.OrdinalIgnoreCase)) vm.Studio.Page = 1;
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
        maintenance.Dispose(); // the visible App now blocks an upgrade; task startup may proceed
        _client.Start();
        _ = vm.HostStart.StartCommand.ExecuteAsync(); // the launch request shares the sidebar action's gate: a click meanwhile requests nothing more
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            ShowWindow();
        }
    }

    private static void LogUiException(UiFaultLogLevel level, Exception? ex, string message)
    {
        var serilogLevel = level switch
        {
            UiFaultLogLevel.Fatal => Serilog.Events.LogEventLevel.Fatal,
            UiFaultLogLevel.Error => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Warning,
        };
        Log.Write(serilogLevel, ex, "{UiFaultMessage}", message);
    }

    /// <summary>
    /// In-window unsafe-fault state (no dialog): sticky banner/sidebar text, a title marker, and the page area disabled so
    /// local edits (drafts, autostart toggle) cannot continue on possibly inconsistent state. The tray Exit still works.
    /// </summary>
    private void ShowUiFault(string text)
    {
#pragma warning disable CA1031 // each step is independent; one failing must not skip the others or recurse
        try
        {
            _vm?.EnterUiFault(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not set the UI fault banner");
        }

        try
        {
            if (_window is not null)
            {
                _window.ShowFaultState(text);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not disable the page area after a UI fault");
        }
#pragma warning restore CA1031
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        if (_uiFaults is { IsFaulted: true } faults)
        {
            Log.Warning("UI exited after an unsafe fault; {Suppressed} further exceptions were counted but not logged", faults.SuppressedCount);
        }

        StopWakeListener();
        _startupStop.Cancel();
        _startupStop.Dispose();
        _tray?.Dispose();
        if (_ownsSingleInstance && _single is { } mutex)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _single?.Dispose();
        _single = null;
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void SignalExistingWindow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
            {
                using (existing) existing.Set();
                return;
            }

            using var created = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            created.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Best-effort wake signal; secondary instance still checks Host startup.
        }
    }

    private void StartWakeListener(EventWaitHandle showEvent, CancellationToken ct)
    {
        var handles = new WaitHandle[] { showEvent, ct.WaitHandle };
        _wakeThread = new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var signaled = WaitHandle.WaitAny(handles);
                    if (signaled != 0 || ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!_exiting)
                    {
                        _ = Dispatcher.BeginInvoke(ShowWindow);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutdown race guard.
            }
        })
        {
            IsBackground = true,
            Name = "UnifiedRgb.App.ShowWindowListener",
        };
        _wakeThread.Start();
    }

    private void StopWakeListener()
    {
        if (_wakeStop is { } wakeStop)
        {
            try
            {
                if (!wakeStop.IsCancellationRequested) wakeStop.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        if (_wakeThread is { } thread)
        {
            _wakeThread = null;
            if (thread.IsAlive)
            {
                thread.Join(TimeSpan.FromMilliseconds(500));
            }
        }

        _showWindowEvent?.Dispose();
        _showWindowEvent = null;
        _wakeStop?.Dispose();
        _wakeStop = null;
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

    private void ShowWindow()
    {
        if (_window is null || _exiting)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        if (!_window.IsActive)
        {
            _window.Topmost = true;
            _window.Topmost = false;
        }
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
        StopWakeListener();
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
