using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using System.Text.Json;

namespace UnifiedRgb.App.Workbench;

public enum CaseControlState { Unknown, Reading, Requesting, Blackout, Active, Pending, Error }

/// <summary>Host observations, never an optical acknowledgement or keyboard transport acknowledgement.</summary>
public sealed record CaseControlDisplayState(CaseControlState State, string Title, string Detail);

/// <summary>
/// Software control panel shared by the desktop preview and a future verified external display.
/// It owns no HID/input subscription and never includes the control display in the case-screen sleep targets.
/// </summary>
public sealed class CaseControlPanelViewModel : ObservableObject
{
    private readonly CasePowerActions _actions;
    private readonly Func<IRgbService> _rgb;
    private readonly Func<IScreenService>? _screens;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _error;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IKeyboardDisplayService>? _keyboardDisplay;
    private readonly TimeSpan _keyboardReadTimeout;
    private readonly TimeSpan _keyboardWriteTimeout;
    private PeripheralConfigurationViewModel? _peripherals;
    private NativeMenuViewModel? _nativeMenu;
    private KeyboardAnimationViewModel? _keyboardAnimation;
    private KeyboardDisplayStatusDto? _keyboardStatus;
    private string _keyboardMessage = TransportNote;
    private long _epoch;
    private long _readVersion;
    private long _requestVersion;
    private long _keyboardVersion;
    private long? _keyboardRequestEpoch;
    private bool _reading;
    private int _requests;
    private bool _connected = true;
    private DateTimeOffset? _observedAt;
    private CaseControlDisplayState _display = new(CaseControlState.Unknown, "状态未读取", "请连接光域后台");

    // keyboardTimeout optionally bounds each keyboard-display call (tests); the defaults are 2 s per read and 5 s per write.
    public CaseControlPanelViewModel(CasePowerActions actions, Func<IRgbService> rgb, Func<IScreenService>? screens,
        IUiDispatcher ui, Action<Exception> error, Func<DateTimeOffset>? now = null, Func<IKeyboardDisplayService>? keyboardDisplay = null,
        TimeSpan? keyboardTimeout = null)
    {
        _actions = actions; _rgb = rgb; _screens = screens; _ui = ui; _error = error;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _keyboardDisplay = keyboardDisplay;
        _keyboardReadTimeout = keyboardTimeout ?? TimeSpan.FromSeconds(2);
        _keyboardWriteTimeout = keyboardTimeout ?? TimeSpan.FromSeconds(5);
        ReadCommand = new AsyncCommand(RefreshAsync, error);
        OffCommand = new AsyncCommand(_ => OffAsync(), error, _ => CanControl);
        OnCommand = new AsyncCommand(_ => EnsureOnAsync(), error, _ => CanControl);
        ToggleKeyboardDisplayCommand = new AsyncCommand(_ => ToggleKeyboardAsync(), error, _ => _connected && _keyboardDisplay is not null && !KeyboardHeld);
        // Like the toggle, CanExecute follows only the connection and handoff; the status-dependent rest is CanReconnectKeyboardLighting.
        ReconnectKeyboardLightingCommand = new AsyncCommand(_ => ReconnectKeyboardLightingAsync(), error, _ => _connected && _keyboardDisplay is not null && !KeyboardHeld);
    }

    /// <summary>Adds the optional keyboard/mouse handoff section; the keyboard toggle then waits for its confirmation on each connection.</summary>
    public void AttachPeripheralConfiguration(PeripheralConfigurationViewModel peripherals)
    {
        ArgumentNullException.ThrowIfNull(peripherals);
        if (_peripherals is not null) throw new InvalidOperationException("Peripheral configuration is already attached.");
        _peripherals = peripherals;
        peripherals.SetConnected(_connected);
        peripherals.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PeripheralConfigurationViewModel.KeyboardIntegrationConfirmed)) { NotifyKeyboard(); RaiseKeyboardCommands(); }
        };
        OnPropertyChanged(nameof(Peripherals)); OnPropertyChanged(nameof(HasPeripheralConfiguration));
        NotifyKeyboard(); RaiseKeyboardCommands();
    }

    /// <summary>Adds the optional native-menu section; it changes only its own Host preference, never the case, screens or keyboard.</summary>
    public void AttachNativeMenu(NativeMenuViewModel nativeMenu)
    {
        ArgumentNullException.ThrowIfNull(nativeMenu);
        if (_nativeMenu is not null) throw new InvalidOperationException("Native menu is already attached.");
        _nativeMenu = nativeMenu;
        nativeMenu.SetConnected(_connected);
        OnPropertyChanged(nameof(NativeMenu)); OnPropertyChanged(nameof(HasNativeMenu));
    }

    /// <summary>Adds the optional local keyboard animation section; it changes only its own Host preference.</summary>
    public void AttachKeyboardAnimation(KeyboardAnimationViewModel keyboardAnimation)
    {
        ArgumentNullException.ThrowIfNull(keyboardAnimation);
        if (_keyboardAnimation is not null) throw new InvalidOperationException("Keyboard animation is already attached.");
        _keyboardAnimation = keyboardAnimation;
        keyboardAnimation.SetConnected(_connected);
        OnPropertyChanged(nameof(KeyboardAnimation)); OnPropertyChanged(nameof(HasKeyboardAnimation));
    }

    public AsyncCommand ReadCommand { get; }
    public AsyncCommand OffCommand { get; }
    public AsyncCommand OnCommand { get; }
    public AsyncCommand ToggleKeyboardDisplayCommand { get; }
    public AsyncCommand ReconnectKeyboardLightingCommand { get; }
    public bool CanControl => _connected && _requests == 0;
    public CaseControlDisplayState Display { get => _display; private set => SetProperty(ref _display, value); }
    public string ObservationNote => _observedAt is { } at ? $"{at.ToLocalTime():HH:mm:ss} 后台回读 · 非光学确认" : "尚无有效回读";
    public const string TransportNote = "本地预览，未发送到键盘；拨杆尚未启用。";
    /// <summary>Explains the Host's restore policy; the UI shows no brightness value and implements no policy itself.</summary>
    public const string KeyboardDisplayGuide = "“恢复键盘原动画”只停用光域状态屏、请求键盘播放自带动画，不改变 RGB 灯光跟随和机箱状态。亮度由后台决定：优先沿用键盘原生的正值亮度（包括您手动调过的）；都没有可用正值时才以 100 兜底，100 不代表原来的亮度。原动画是否可见，以键盘实际显示为准。";
    // Our page disabled means the keyboard's own animation was requested; a Stopped or ACK readback never proves it is visible.
    public string KeyboardTransportNote => _connected && KeyboardHeld ? _peripherals!.KeyboardHoldNote : _keyboardStatus is { } s
        ? (s.Enabled ? string.Empty : $"光域状态屏未启用，目标为键盘原动画（后台状态 {s.State}，是否可见以键盘实际显示为准）· ")
            + $"{s.Detail} · 已确认 {s.FramesAcknowledged} 幅画面（通信回执，非光学确认）" : _keyboardMessage;
    public string KeyboardToggleLabel => _keyboardStatus?.Enabled == true ? "恢复键盘原动画" : "开启光域状态屏";
    public bool HasKeyboardDisplayService => _keyboardStatus is not null;
    public bool CanToggleKeyboardDisplay => HasKeyboardDisplayService && !KeyboardHeld && !KeyboardRequestInFlight;
    /// <summary>Offered only for a current Error read with a trustworthy page preference; <see cref="KeyboardReconnectNote"/> says why not otherwise.</summary>
    public bool CanReconnectKeyboardLighting => ReconnectRefusal is null;
    public string KeyboardReconnectNote => ReconnectRefusal
        ?? $"后台报告键盘错误：可点“重连键盘灯光”，按回读的开关原样请求一次（光域状态屏保持{PageState(_keyboardStatus!.Enabled)}）；不另发亮度、机箱或外设配置指令，不绕过固件与身份校验，不保证恢复，以回读为准。";
    public PeripheralConfigurationViewModel? Peripherals => _peripherals;
    public bool HasPeripheralConfiguration => _peripherals is not null;
    public NativeMenuViewModel? NativeMenu => _nativeMenu;
    public bool HasNativeMenu => _nativeMenu is not null;
    public KeyboardAnimationViewModel? KeyboardAnimation => _keyboardAnimation;
    public bool HasKeyboardAnimation => _keyboardAnimation is not null;
    // A keyboard/mouse handoff may own the keyboard: offer no toggle until this connection confirmed normal integration.
    private bool KeyboardHeld => _peripherals is { KeyboardIntegrationConfirmed: false };
    // One keyboard intent at a time on the current connection: the other keyboard action waits for this one's readback.
    private bool KeyboardRequestInFlight => _keyboardRequestEpoch == Volatile.Read(ref _epoch);

    // Why the explicit reconnect is unavailable now, or null. The Host never retries a faulted keyboard session by itself; its
    // other states are healthy, in transition or already waiting for the keyboard, so only a current Error is offered.
    private string? ReconnectRefusal => !_connected ? "后台未连接：暂不能重连键盘灯光。"
        : KeyboardRequestInFlight ? "正在等待键盘指令的后台答复：暂不重连，请待回读。"
        : KeyboardHeld ? "键鼠外设配置中或未确认恢复联动：暂不重连键盘灯光。"
        : _keyboardStatus is { } s ? ReconnectRefusalFor(s) : "键盘状态未知、未读到或后台不支持：暂不能重连键盘灯光，请等待新的后台回读。";

    private static string? ReconnectRefusalFor(KeyboardDisplayStatusDto s) => s switch
    {
        // The Host fell back to "disabled" because it could not read the local setting; reasserting would overwrite the owner's choice.
        { State: "Error", LastError: UnreadableSettings } => "本机键盘显示设置无法读取，开关值不可信：不重连，也不覆盖该设置。",
        { State: "Error" } => null,
        { State: "Connecting" or "Stopping" } => $"后台正在连接或恢复键盘（状态 {s.State}）：请等待新的回读，暂不重连。",
        { State: "Waiting" } => "键盘未接入（后台状态 Waiting）：后台继续等待已绑定的键盘接入，暂不重连。",
        _ => $"后台未报告键盘错误（状态 {s.State}）：暂不提供重连；灯光是否同步以实际显示为准。",
    };

    private const string UnreadableSettings = "本机控制屏设置无法读取";
    private static string PageState(bool enabled) => enabled ? "开启" : "未启用";
    private sealed record KeyboardIntent(bool Enabled, string Request, string Action, string Outcome);

    private async Task RefreshKeyboardAsync()
    {
        var epoch = Volatile.Read(ref _epoch);
        // An in-flight toggle owns the keyboard feedback until its own bounded readback.
        if (_keyboardDisplay is null || _keyboardRequestEpoch == epoch) return;
        var version = Interlocked.Increment(ref _keyboardVersion);
        var (state, error) = await ReadKeyboardAsync(_keyboardDisplay).ConfigureAwait(true);
        PostKeyboard(epoch, version, state, error is null ? TransportNote : "键盘状态未知：本次未读到，不能确认是否已发送；下次回读时重试。");
    }

    private Task ToggleKeyboardAsync() => SendKeyboardIntentAsync(current => !current.Enabled
        // Disabling our page requests the keyboard's own visible animation, never a panel-off; no answer proves it plays.
        ? new(true, "开启光域状态屏", "开启", "是否已切换") : new(false, "恢复键盘原动画", "恢复原动画", "原动画是否已恢复"), "切换指令", static () => true);

    /// <summary>
    /// Explicit way out of a sticky keyboard Error: reasserts exactly the page preference of a fresh read, never its inverse, so a
    /// disabled page stays disabled. No answer proves the keyboard lighting recovered; the readback shows what the Host reports.
    /// </summary>
    private Task ReconnectKeyboardLightingAsync() => SendKeyboardIntentAsync(current => ReconnectRefusalFor(current) is null
        ? new(current.Enabled, $"重连键盘灯光（光域状态屏保持{PageState(current.Enabled)}）", "重连键盘灯光", "是否已重新接入") : null,
        "重连请求", () => CanReconnectKeyboardLighting);

    /// <summary>One user intent sends at most one write, chosen from a fresh read on the same connection; a lost answer is read back, never resent.</summary>
    private async Task SendKeyboardIntentAsync(Func<KeyboardDisplayStatusDto, KeyboardIntent?> choose, string unsent, Func<bool> offered)
    {
        var epoch = Volatile.Read(ref _epoch);
        var keyboard = _keyboardDisplay;
        _peripherals?.ExpireObservation(); // An expired handoff confirmation holds the toggle at the click, not only at the next poll.
        if (keyboard is null || !_connected || KeyboardHeld || KeyboardRequestInFlight || !offered()) return;
        IKeyboardDisplayService? proxy = null;
        // One proxy per intent: a reconnect not yet seen here can never receive a write chosen on the old connection.
        IKeyboardDisplayService Service() => proxy ??= keyboard();
        _keyboardRequestEpoch = epoch;
        NotifyKeyboard();
        try
        {
            // The preflight retires every earlier keyboard read.
            var version = Interlocked.Increment(ref _keyboardVersion);
            var (current, readError) = await ReadKeyboardAsync(Service).ConfigureAwait(true);
            if (current is null)
            {
                PostKeyboard(epoch, version, null, readError is null ? TransportNote : $"键盘状态未读到，未发送{unsent}；请稍后重试。");
                ReportHostError(epoch, readError);
                return;
            }
            // Never write through a connection that dropped or reconnected while the preflight was awaited.
            if (!CurrentKeyboard(epoch, version)) return;
            // Nor once a keyboard/mouse handoff began, or its confirmation expired, meanwhile, or once the fresh read no longer
            // calls for this intent: publish the read, send nothing.
            _peripherals?.ExpireObservation();
            if (KeyboardHeld || choose(current) is not { } intent) { PostKeyboard(epoch, version, current, TransportNote); return; }
            var (enable, request, action, outcome) = intent;
            version = Interlocked.Increment(ref _keyboardVersion);
            PostKeyboard(epoch, version, null, $"正在请求{request}，等待后台答复…");
            string unread;
            try
            {
                await BoundAsync(ct => Service().SetKeyboardDisplayEnabledAsync(enable, ct), _keyboardWriteTimeout).ConfigureAwait(true);
                PostKeyboard(epoch, version, null, $"后台已接受{action}指令，正在回读键盘状态…");
                unread = $"后台已接受{action}指令，但键盘状态未读到，不能确认{outcome}。";
            }
            catch (StreamJsonRpc.RemoteMethodNotFoundException)
            {
                PostKeyboard(epoch, version, null, TransportNote); // Old Host: nothing was sent to the keyboard.
                return;
            }
            catch (StreamJsonRpc.RemoteInvocationException ex)
            {
                // The Host answered with an error; read back instead of assuming nothing changed.
                PostKeyboard(epoch, version, null, $"后台对{action}指令返回错误，正在回读键盘状态…");
                unread = $"后台对{action}指令返回错误，键盘状态也未读到，不能确认{outcome}。";
                ReportHostError(epoch, ex);
            }
#pragma warning disable CA1031 // A deadline, cancellation or lost connection can follow a write that already applied.
            catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
            {
                PostKeyboard(epoch, version, null, $"{action}结果未确认：未收到后台答复，指令可能已生效；未自动重发，正在回读…");
                unread = $"{action}结果未知：未收到后台答复，键盘状态也未读到；未自动重发，请待回读后再操作。";
            }
            if (!CurrentKeyboard(epoch, version)) return; // The readback must use the same connection epoch.
            var (observed, error) = await ReadKeyboardAsync(Service).ConfigureAwait(true);
            PostKeyboard(epoch, version, observed, error is null ? TransportNote : unread);
        }
        finally
        {
            if (_keyboardRequestEpoch == epoch) _keyboardRequestEpoch = null;
            _ui.Post(NotifyKeyboard); // Both keyboard actions become available again only through property bindings.
        }
    }

    /// <summary>Bounded status read; an old Host without the endpoint yields neither a status nor an error.</summary>
    private async Task<(KeyboardDisplayStatusDto? State, Exception? Error)> ReadKeyboardAsync(Func<IKeyboardDisplayService> service)
    {
        try { return (await BoundAsync(ct => service().GetKeyboardDisplayStatusAsync(ct), _keyboardReadTimeout).ConfigureAwait(true), null); }
        catch (StreamJsonRpc.RemoteMethodNotFoundException) { return (null, null); } // Old Host: keep a truthful local-only preview.
#pragma warning disable CA1031 // Optional control-display status cannot invalidate the independent case readback.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex); }
#pragma warning restore CA1031
    }

    // A proxy task may ignore cancellation, so the deadline also ends the wait itself.
    private static async Task<T> BoundAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan limit)
    {
        using var bound = new CancellationTokenSource(limit);
        return await call(bound.Token).WaitAsync(bound.Token).ConfigureAwait(true);
    }

    private static async Task BoundAsync(Func<CancellationToken, Task> call, TimeSpan limit)
    {
        using var bound = new CancellationTokenSource(limit);
        await call(bound.Token).WaitAsync(bound.Token).ConfigureAwait(true);
    }

    private void PostKeyboard(long epoch, long version, KeyboardDisplayStatusDto? state, string note) => _ui.Post(() =>
    {
        if (!CurrentKeyboard(epoch, version)) return;
        _keyboardStatus = state;
        _keyboardMessage = note;
        NotifyKeyboard();
    });

    // Host-provided rejections reach the error banner; deadlines and lost connections are shown as unknown keyboard feedback instead.
    private void ReportHostError(long epoch, Exception? ex)
    {
        if (ex is StreamJsonRpc.RemoteInvocationException) _ui.Post(() => { if (_connected && epoch == Volatile.Read(ref _epoch)) _error(ex); });
    }

    public Task<CasePowerActionResult> OffAsync() => RequestAsync(_actions.OffAsync, "正在熄灯…");
    public Task<CasePowerActionResult> EnsureOnAsync() => RequestAsync(_actions.EnsureOnAsync, "正在恢复…");
    public Task<CasePowerActionResult> RestoreBackupAsync() => RequestAsync(_actions.RestoreBackupAsync, "正在恢复…");

    private async Task<CasePowerActionResult> RequestAsync(Func<CancellationToken, Task<CasePowerActionResult>> action, string title)
    {
        var epoch = Volatile.Read(ref _epoch);
        var request = Interlocked.Increment(ref _requestVersion);
        _requests++; Interlocked.Increment(ref _readVersion);
        _ui.Post(() =>
        {
            if (CurrentRequest(epoch, request))
            {
                _observedAt = null; Display = new(CaseControlState.Requesting, title, "等待后台接受指令");
                OnPropertyChanged(nameof(ObservationNote));
            }
            NotifyControls();
        });
        try
        {
            var result = await action(CancellationToken.None).ConfigureAwait(true);
            // A request the Host state already satisfied sent nothing; do not present it as a submitted command.
            _ui.Post(() => { if (CurrentRequest(epoch, request)) Display = new(CaseControlState.Pending, result.Changed ? "指令已提交" : "未发送新指令", "正在读取设备状态"); });
            return result;
        }
        catch
        {
            // A write without an answer may have applied: leave it to the read-back instead of claiming it was not done.
            var unconfirmed = _actions.Status.State == CasePowerRequestState.Unconfirmed;
            _ui.Post(() =>
            {
                if (!CurrentRequest(epoch, request)) return;
                _observedAt = null;
                Display = unconfirmed ? new(CaseControlState.Pending, "结果未确认", "未收到后台答复，等待回读")
                    : new(CaseControlState.Error, "操作未完成", "请查看光域错误信息");
                OnPropertyChanged(nameof(ObservationNote));
            });
            throw;
        }
        finally
        {
            _requests--;
            _ui.Post(NotifyControls);
            // An accepted request is not a completed screen transition. Read separately, with errors reported as unknown.
            if (_actions.Status.State != CasePowerRequestState.Failed) await RefreshAsync().ConfigureAwait(true);
        }
    }

    public async Task RefreshAsync()
    {
        ExpireObservation();
        if (_reading || _requests > 0 || !_connected) return;
        _reading = true;
        var epoch = Volatile.Read(ref _epoch);
        var read = Interlocked.Increment(ref _readVersion);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = timeout.Token;
            var host = _rgb();
            var scene = await host.GetSceneAsync(ct).ConfigureAwait(true);
            var controllers = await host.ListControllersAsync(ct).ConfigureAwait(true);
            IReadOnlyList<ScreenDeviceDto>? screens = null;
            ScreenSceneDto? screenScene = null;
            if (_screens is not null)
            {
                var service = _screens();
                screenScene = await service.GetScreenSceneAsync(ct).ConfigureAwait(true);
                screens = await service.ListScreensAsync(ct).ConfigureAwait(true);
            }
            var after = await host.GetSceneAsync(ct).ConfigureAwait(true);
            var display = JsonSerializer.Serialize(scene) == JsonSerializer.Serialize(after)
                ? Describe(scene, controllers, screenScene, screens)
                : new CaseControlDisplayState(CaseControlState.Pending, "场景正在变化", "等待下一次后台回读");
            _ui.Post(() =>
            {
                if (!Current(epoch, read)) return;
                _observedAt = _now(); Display = display;
                OnPropertyChanged(nameof(ObservationNote));
            });
            // Keyboard status is optional and separately bounded; it never delays or replaces the case readback.
            await RefreshKeyboardAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Optional status read boundary: show unknown and route failures to the existing UI error banner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ui.Post(() =>
            {
                if (!Current(epoch, read)) return;
                _observedAt = null; Display = new(CaseControlState.Unknown, "状态未读到", "请检查后台连接");
                OnPropertyChanged(nameof(ObservationNote)); _error(ex);
            });
        }
        finally { _reading = false; }
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        Interlocked.Increment(ref _epoch);
        _observedAt = null;
        _keyboardStatus = null;
        _keyboardMessage = "键盘状态未知，等待新的后台回读。";
        _peripherals?.SetConnected(connected);
        _nativeMenu?.SetConnected(connected);
        _keyboardAnimation?.SetConnected(connected);
        NotifyKeyboard();
        Display = new(CaseControlState.Unknown, connected ? "等待读取" : "后台未连接", "不使用上次状态判断");
        OnPropertyChanged(nameof(ObservationNote)); NotifyControls();
    }

    public void ExpireObservation()
    {
        if (_observedAt is not { } at || _now() - at <= TimeSpan.FromSeconds(10)) return;
        _observedAt = null;
        Display = new(CaseControlState.Unknown, "状态已过期", "等待新的后台回读");
        OnPropertyChanged(nameof(ObservationNote));
    }

    private bool Current(long epoch, long read) => _connected && _requests == 0 && epoch == Volatile.Read(ref _epoch) && read == Volatile.Read(ref _readVersion);
    private bool CurrentRequest(long epoch, long request) => _connected && epoch == Volatile.Read(ref _epoch) && request == Volatile.Read(ref _requestVersion);
    // Keyboard feedback has its own version, independent of the case readback: a later keyboard read or toggle step always wins.
    private bool CurrentKeyboard(long epoch, long version) => _connected && epoch == Volatile.Read(ref _epoch) && version == Volatile.Read(ref _keyboardVersion);
    private void NotifyControls() { OnPropertyChanged(nameof(CanControl)); OffCommand.RaiseCanExecuteChanged(); OnCommand.RaiseCanExecuteChanged(); RaiseKeyboardCommands(); }
    private void RaiseKeyboardCommands() { ToggleKeyboardDisplayCommand.RaiseCanExecuteChanged(); ReconnectKeyboardLightingCommand.RaiseCanExecuteChanged(); }
    private void NotifyKeyboard()
    {
        OnPropertyChanged(nameof(KeyboardTransportNote)); OnPropertyChanged(nameof(KeyboardToggleLabel));
        OnPropertyChanged(nameof(HasKeyboardDisplayService)); OnPropertyChanged(nameof(CanToggleKeyboardDisplay));
        OnPropertyChanged(nameof(CanReconnectKeyboardLighting)); OnPropertyChanged(nameof(KeyboardReconnectNote));
    }

    internal static CaseControlDisplayState Describe(SceneDto scene, IReadOnlyList<ControllerDto> controllers,
        ScreenSceneDto? screenScene, IReadOnlyList<ScreenDeviceDto>? screens)
    {
        var observed = CaseStatusSummary.Describe(scene, controllers, screenScene, screens);
        return new((CaseControlState)observed.State, observed.Title, observed.Detail);
    }
}
