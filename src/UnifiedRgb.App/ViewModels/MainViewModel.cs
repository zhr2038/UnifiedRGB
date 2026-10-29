using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.ViewModels;

public enum TrayState
{
    /// <summary>No Host session.</summary>
    Disconnected,

    /// <summary>Connected and every controller is Active.</summary>
    Ok,

    /// <summary>Connected but some controller is Blocked, Contended or Offline.</summary>
    Attention,
}

/// <summary>
/// Root view model: owns the page view models, reacts to connection changes (full refresh on every (re)connect) and to Host
/// notifications, and exposes the tray state and the last error for the status bar. Dispose stops the Native bring-up
/// heartbeat (it sends nothing; the Host ends the connection's sessions when the pipe closes).
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly PipeClient _client;
    private readonly IUiDispatcher _ui;
    private string _connection = "正在连接后台…";
    private string _error = string.Empty;
    private volatile string? _uiFault;
    private TrayState _tray = TrayState.Disconnected;
    private int _workbenchSource;
    private bool _initialSourceSelected;
    public int WorkbenchSourceIndex { get => _workbenchSource; set => SetProperty(ref _workbenchSource, value); }

    /// <param name="confirm">Yes/no prompt before flash writes ("Save current lighting to fans"); null: no prompt (tests).</param>
    /// <param name="openLink">Opens the Gear Link page on an explicit click only; null: the system browser.</param>
    /// <param name="startHostTask">Requests the fixed existing Host task (App: <see cref="HostStartup.StartTaskAsync"/>); null: no start action.</param>
    /// <param name="waitHostConnected">Connection wait of the start action; null: the client's own <see cref="PipeClient.WaitConnectedAsync(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="hostStartTime">Clock bounding the start action; null: the system clock.</param>
    public MainViewModel(PipeClient client, IUiDispatcher ui, IAutostartRegistry autostart, string appExePath, string hostDataDirectory, string appLogDirectory,
        Func<string, bool>? confirm = null, Func<string?>? chooseMediaFile = null, Func<string?>? choosePackageFile = null, Action<Uri>? openLink = null,
        Func<CancellationToken, Task>? startHostTask = null, Func<TimeSpan, CancellationToken, Task<bool>>? waitHostConnected = null, TimeProvider? hostStartTime = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        HostStart = new HostStartViewModel(client, ui, startHostTask, waitHostConnected ?? client.WaitConnectedAsync, ReportError, () => IsUiFaulted, hostStartTime);
        Devices = new DevicesViewModel(client, ui, ReportError, confirm);
        Effects = new EffectsViewModel(client, ui, ReportError);
        Effects.LiveApply = false;
        Studio = new StudioViewModel(client, ui, ReportError, confirm, client.RequireScreens, client.RequireKeyboardDisplay);
        Studio.ControlPanel.AttachPeripheralConfiguration(new(client.RequirePeripheralConfiguration, ui, ReportError, openLink ?? PeripheralConfigurationViewModel.OpenInBrowser));
        Studio.ControlPanel.AttachNativeMenu(new(client.RequireNativeMenu, ui, ReportError));
        Studio.ControlPanel.AttachKeyboardAnimation(new(client.RequireKeyboardDisplay, ui, ReportError));
        Screens = new ScreensViewModel(client.RequireScreens, ui, ReportError, chooseMediaFile ?? (() => null), confirm, deviceSettings: client.RequireScreenSettings);
        Screens.ErrorDismissed += (_, _) => Error = string.Empty;
        Scene = new SceneViewModel(client, ui, ReportError);
        Packages = new ScenePackagesViewModel(client.RequirePackages,
            () => client.Require().ListControllersAsync(), () => client.RequireScreens().ListScreensAsync(),
            choosePackageFile ?? (() => null), ReportError, confirm, client.RequireLibrary);
        Profiles = new ProfilesViewModel(client, ui, ReportError, Packages, confirm);
        Packages.ReadPreviewTelemetry = () => client.RequireScreens().GetPcTelemetryAsync();
        Screens.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScreensViewModel.TelemetrySnapshot)) Packages.TelemetrySnapshot = Screens.TelemetrySnapshot; };
        Profiles.Applied += (_, _) => { _ = Studio.ReadCommand.ExecuteAsync(); _ = Screens.RefreshOptionalAsync(); };
        Packages.Applied += (_, _) => { _ = Studio.ReadCommand.ExecuteAsync(); _ = Screens.RefreshOptionalAsync(); };
        Bringup = new NativeBringupViewModel(client, ui, ReportError);
        Diagnostics = new DiagnosticsViewModel(ui, hostDataDirectory, appLogDirectory, ReportError, () => client.Require().GetStatusAsync(diagnostics: true), Bringup);
        Settings = new SettingsViewModel(autostart, appExePath);
        if (client.State == ConnectionState.Connected) ConnectionText = "后台已连接";
        RefreshCommand = new AsyncCommand(RefreshAllAsync, ReportError);
        DismissErrorCommand = new RelayCommand(() => { Error = ""; Screens.DismissErrorCommand.Execute(null); });

        Devices.Changed += (_, _) =>
        {
            UpdateTray();
            Bringup.SetControllers(Devices.Snapshot);
        };
        client.StateChanged += OnStateChanged;
        client.ControllersChanged += OnControllersChanged;
        client.StatusChanged += (_, s) =>
        {
            Diagnostics.Update(s);
            Studio.UpdateStatus(s);
            _ui.Post(() => Settings.Update(s));
        };
    }

    public DevicesViewModel Devices { get; }

    public EffectsViewModel Effects { get; }
    public StudioViewModel Studio { get; }
    public ScreensViewModel Screens { get; }

    public SceneViewModel Scene { get; }

    public ProfilesViewModel Profiles { get; }
    public ScenePackagesViewModel Packages { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Diagnostics › Native bring-up (also reachable as <c>Diagnostics.Bringup</c>).</summary>
    public NativeBringupViewModel Bringup { get; }

    public SettingsViewModel Settings { get; }

    public AsyncCommand RefreshCommand { get; }
    public RelayCommand DismissErrorCommand { get; }

    /// <summary>Sidebar 启动后台 / 重新连接后台 next to the connection line; App's launch request runs through it too.</summary>
    public HostStartViewModel HostStart { get; }

    /// <summary>Sidebar connection line; frozen to the fault notice once <see cref="IsUiFaulted"/>.</summary>
    public string ConnectionText
    {
        get => _connection;
        private set
        {
            if (_uiFault is null)
            {
                SetProperty(ref _connection, value);
            }
        }
    }

    /// <summary>Error banner text; while <see cref="IsUiFaulted"/> it stays on the fault notice (dismiss/reports cannot clear it).</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (_uiFault is null)
            {
                SetProperty(ref _error, value);
            }
        }
    }

    /// <summary>True after an unsafe UI exception: the view may be inconsistent and Host operations are blocked until restart.</summary>
    public bool IsUiFaulted => _uiFault is not null;

    /// <summary>
    /// Enters the sticky fault state (first call wins). Must be called on the UI thread. Keeps all page view models and
    /// their drafts in memory; does not contact the Host.
    /// </summary>
    public void EnterUiFault(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Interlocked.CompareExchange(ref _uiFault, text, null) is not null)
        {
            return;
        }

        _error = text;
        _connection = UiFaultConnectionText;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(IsUiFaulted));
        HostStart.Refresh(); // a blocked client never reconnects: the start action is withdrawn
    }

    public const string UiFaultConnectionText = "界面需要重启 · 已停止向后台发送操作";

    /// <summary>Sidebar text while disconnected: retry notice plus the bounded connection diagnostic, if any.</summary>
    public static string DisconnectedText(string? lastError) =>
        string.IsNullOrWhiteSpace(lastError) ? "后台未连接 · 自动重试中" : lastError + " · 自动重试中";

    public TrayState TrayState
    {
        get => _tray;
        private set => SetProperty(ref _tray, value);
    }

    public static TrayState TrayStateOf(bool connected, IEnumerable<ControllerDto> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        return !connected ? TrayState.Disconnected
            : controllers.Any(c => !DisplayNames.IsDiscoveryPort(c)) && controllers.Where(c => !DisplayNames.IsDiscoveryPort(c)).All(c => c.State == ControllerStates.Active) ? TrayState.Ok
            : TrayState.Attention;
    }

    public async Task RefreshAllAsync()
    {
        await Devices.RefreshAsync().ConfigureAwait(true);
        await Effects.RefreshAsync().ConfigureAwait(true);
        await Scene.RefreshAsync().ConfigureAwait(true);
        await Profiles.RefreshAsync().ConfigureAwait(true);
        await Studio.RefreshAsync().ConfigureAwait(true);
        await Screens.RefreshOptionalAsync().ConfigureAwait(true);
        await Packages.RefreshOptionalAsync().ConfigureAwait(true);
        Packages.SelectCurrent(Studio.ActivePackageContentId);
        if (!_initialSourceSelected)
        {
            WorkbenchSourceIndex = Studio.ActivePackageContentId is null ? 0 : 1;
            _initialSourceSelected = true;
        }
        var status = await _client.Require().GetStatusAsync(false).ConfigureAwait(true);
        Diagnostics.Update(status);
        Studio.UpdateStatus(status);
        _ui.Post(() => Settings.Update(status));
    }

    public void ReportError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var text = ex is StreamJsonRpc.RemoteInvocationException r ? r.Message : $"{ex.GetType().Name}: {ex.Message}";
        _ui.Post(() => Error = text);
    }

    private void OnStateChanged(object? sender, ConnectionState state)
    {
        _ui.Post(() =>
        {
            ConnectionText = state switch
            {
                ConnectionState.Connected => "后台已连接",
                ConnectionState.Connecting => "正在连接后台…",
                _ => DisconnectedText(_client.LastError),
            };
            if (state == ConnectionState.Connected)
            {
                Error = string.Empty;
            }

            UpdateTray();
            Studio.SetConnected(state == ConnectionState.Connected);
            if (state != ConnectionState.Connected) { Screens.MarkDisconnected(); Packages.MarkDisconnected(); }
            if (state == ConnectionState.Connected && !IsUiFaulted)
            {
                _ = RefreshCommand.ExecuteAsync();
            }
        });
    }

    private void OnControllersChanged(object? sender, IReadOnlyList<ControllerDto> controllers)
    {
        Devices.Update(controllers);
        _ui.Post(() =>
        {
            Effects.SetControllers(controllers);
            Scene.RefreshCommand.Execute(null);
        });
    }

    public void Dispose() { HostStart.Dispose(); Studio.Dispose(); Packages.Dispose(); Bringup.Dispose(); }

    private void UpdateTray() => TrayState = TrayStateOf(_client.State == ConnectionState.Connected, Devices.Snapshot);
}

/// <summary>
/// Sidebar 启动后台 / 重新连接后台 for a Host that stopped while this window stays open; App's launch request runs through it
/// too, so both share one gate. A run is <see cref="HostStartup.EnsureAsync"/> with the fixed existing task: a Host that
/// connects within its grace period is left alone and a run requests the task at most once. A request that fails is reported,
/// and one that is unanswered, timed out or cancelled counts as unknown; neither is ever repeated by itself. After a request
/// the run only waits, bounded, for the pipe client's own reconnect, and while a request is still unanswered no later run
/// requests the task again. It touches no proxy, draft, scene, setting or configuration mode and never claims devices
/// resumed. Its line is judged against the connection as it is when shown; outside a run it is cleared once that connection
/// changes, so the connection text above stays the truth.
/// </summary>
public sealed class HostStartViewModel : ObservableObject, IDisposable
{
    /// <summary>How long a task request may stay unanswered before its outcome counts as unknown.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a run waits for the reconnect after a request; the pipe client keeps retrying after that.</summary>
    public static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(30);

    private readonly IHostConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly Func<CancellationToken, Task>? _startTask;
    private readonly Func<TimeSpan, CancellationToken, Task<bool>> _waitConnected;
    private readonly Action<Exception> _onError;
    private readonly Func<bool> _blocked;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private volatile Task? _unanswered; // the last task request until it ends, even after its run gave up on it
    private long _run;
    private volatile bool _running;
    private volatile bool _disposed;
    private string _status = string.Empty;
    private (int Connections, bool Connected) _statusFor; // the connection the line was judged against

    /// <param name="startTask">Requests the fixed existing task; null: nothing is offered or requested.</param>
    /// <param name="waitConnected">Bounded wait for a connection (the pipe client's own reconnect).</param>
    /// <param name="blocked">True while Host access is blocked after an unsafe UI fault.</param>
    public HostStartViewModel(IHostConnection connection, IUiDispatcher ui, Func<CancellationToken, Task>? startTask,
        Func<TimeSpan, CancellationToken, Task<bool>> waitConnected, Action<Exception> onError, Func<bool>? blocked = null, TimeProvider? time = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _waitConnected = waitConnected ?? throw new ArgumentNullException(nameof(waitConnected));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _startTask = startTask;
        _blocked = blocked ?? (() => false);
        _time = time ?? TimeProvider.System;
        StartCommand = new AsyncCommand(_ => RunAsync(), onError, _ => IsOffered);
        _connection.StateChanged += OnStateChanged;
    }

    /// <summary>One run per click; clicks while it runs are ignored.</summary>
    public AsyncCommand StartCommand { get; }

    /// <summary>Offered while not connected, with a start delegate and Host access not blocked.</summary>
    public bool IsOffered => _startTask is not null && !_disposed && !_blocked() && _connection.State != ConnectionState.Connected;

    /// <summary>启动后台 before this window's first connection, 重新连接后台 once one was lost.</summary>
    public string Label => _connection.Connections == 0 ? "启动后台" : "重新连接后台";

    /// <summary>What the last run did, or empty.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    private (int Connections, bool Connected) Identity => (_connection.Connections, _connection.State == ConnectionState.Connected);

    /// <summary>Re-reads the connection and the UI-fault block for the bindings; outside a run, a line judged against another
    /// connection is cleared. UI thread.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsOffered));
        OnPropertyChanged(nameof(Label));
        StartCommand.RaiseCanExecuteChanged();
        if (!_running && Identity != _statusFor)
        {
            Status = string.Empty;
        }
    }

    /// <summary>Cancels a running request or wait (app exit); nothing is shown afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.StateChanged -= OnStateChanged;
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        if (!IsOffered) return; // connected, blocked or nothing to start with; ExecuteAsync does not consult CanExecute
        var run = Interlocked.Increment(ref _run);
        var since = _connection.Connections;
        var token = _stop.Token;
        var requested = false;
        _running = true;
        try
        {
            string request;
            if (_unanswered is { IsCompleted: false })
            {
                request = "上一次启动请求仍未答复，未再次请求";
            }
            else
            {
                try
                {
                    if (!await HostStartup.EnsureAsync(grace => WaitAsync(grace, token), () => { requested = true; return RequestAsync(run, token); }, token).ConfigureAwait(true))
                    {
                        Show(run, () => string.Empty); // connected within the grace period: nothing was requested
                        return;
                    }

                    request = "已请求启动后台任务";
                }
                catch (Exception ex) when (requested && ex is TimeoutException or OperationCanceledException && !token.IsCancellationRequested)
                {
                    // no answer in time, or a cancellation that was not ours: the task may or may not be starting
                    request = "启动请求未答复，结果未知，未再次请求";
                }
            }

            Show(run, () => request + "，正在等待后台连接…");
            await WaitAsync(ConnectWait, token).ConfigureAwait(true);
            Show(run, () => Outcome(since, request));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // closing: no feedback
        }
#pragma warning disable CA1031 // a failed request is shown and reported, never retried
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Show(run, () => (requested ? "后台启动请求失败，未重试：" : "未请求启动后台：") + ex.Message);
            if (requested && _connection.State != ConnectionState.Connected) _onError(ex);
        }
        finally
        {
            _running = false;
        }
    }

    // The run's one task request. Its call is bounded here: one that ignores cancellation leaves the outcome unknown and blocks
    // further requests until it ends; its late result is observed, never shown.
    private Task RequestAsync(long run, CancellationToken token)
    {
        Show(run, () => "正在请求启动后台任务…");
        var call = _startTask!(token);
        _unanswered = call;
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return call.WaitAsync(AnswerTimeout, _time, token);
    }

    // The pipe client's wait, bounded here as well: false once the time is up, even for a wait that ignores its timeout.
    private async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        try
        {
            return await _waitConnected(timeout, token).WaitAsync(timeout, _time, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // Judged when shown, so a connection that came up and was lost again is never presented as the current one.
    private string Outcome(int since, string request) =>
        _connection.Connections <= since ? $"{request}；未等到新的后台连接（最多 {ConnectWait.TotalSeconds:0} 秒），不会自动再次请求（连接仍在自动重试）。"
        : _connection.State == ConnectionState.Connected ? "后台已连接；设备状态以界面读取为准。"
        : request + "；后台连上后又断开，未再次请求。";

    // Only the latest run writes the line, on the UI thread, recording the connection it was judged against.
    private void Show(long run, Func<string> text) => _ui.Post(() =>
    {
        if (_disposed || run != Interlocked.Read(ref _run))
        {
            return;
        }

        Status = text();
        _statusFor = Identity;
    });

    private void OnStateChanged(object? sender, ConnectionState state) => _ui.Post(Refresh);
}
