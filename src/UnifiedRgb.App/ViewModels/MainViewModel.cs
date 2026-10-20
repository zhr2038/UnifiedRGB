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
    private TrayState _tray = TrayState.Disconnected;

    /// <param name="confirm">Yes/no prompt before flash writes ("Save current lighting to fans"); null: no prompt (tests).</param>
    public MainViewModel(PipeClient client, IUiDispatcher ui, IAutostartRegistry autostart, string appExePath, string hostDataDirectory, string appLogDirectory,
        Func<string, bool>? confirm = null, Func<string?>? chooseMediaFile = null, Func<string?>? choosePackageFile = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Devices = new DevicesViewModel(client, ui, ReportError, confirm);
        Effects = new EffectsViewModel(client, ui, ReportError);
        Effects.LiveApply = false;
        Studio = new StudioViewModel(client, ui, ReportError, confirm);
        Screens = new ScreensViewModel(client.RequireScreens, ui, ReportError, chooseMediaFile ?? (() => null), confirm, deviceSettings: client.RequireScreenSettings);
        Screens.ErrorDismissed += (_, _) => Error = string.Empty;
        Scene = new SceneViewModel(client, ui, ReportError);
        Packages = new ScenePackagesViewModel(client.RequirePackages,
            () => client.Require().ListControllersAsync(), () => client.RequireScreens().ListScreensAsync(),
            choosePackageFile ?? (() => null), ReportError, confirm, client.RequireLibrary);
        Profiles = new ProfilesViewModel(client, ui, ReportError, Packages, confirm);
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

    public string ConnectionText
    {
        get => _connection;
        private set => SetProperty(ref _connection, value);
    }

    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

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
                _ => _client.LastError ?? "后台未连接 · 自动重试中",
            };
            if (state == ConnectionState.Connected)
            {
                Error = string.Empty;
            }

            UpdateTray();
            Studio.SetConnected(state == ConnectionState.Connected);
            if (state != ConnectionState.Connected) { Screens.MarkDisconnected(); Packages.MarkDisconnected(); }
            if (state == ConnectionState.Connected)
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

    public void Dispose() => Bringup.Dispose();

    private void UpdateTray() => TrayState = TrayStateOf(_client.State == ConnectionState.Connected, Devices.Snapshot);
}
