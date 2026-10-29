using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>Host-owned keyboard display, independent of the case-screen sleep set and UI process lifetime.</summary>
public sealed class KeyboardDisplayService : BackgroundService, IKeyboardDisplayService
{
    private readonly UnifiedRgbOptions _options;
    private readonly IRgbService _rgb;
    private readonly IScreenService _screens;
    private readonly IRogWiredOledFactory _factory;
    private readonly IRogWirelessOledFactory? _wirelessFactory;
    private readonly IWriterGuard _guard;
    private readonly RogNativeMenuSource? _nativeMenu;
    private readonly RogWiredLightingBridge _lighting;
    private readonly PeripheralConfigurationGate? _configurationGate;
    private readonly KeyboardDisplayAnimation _animation;
    private readonly IKeyboardNativePresentationCapability? _nativePresentation;
    private readonly Func<byte[], byte[]>? _nativeFrameOverlay;
    private readonly Action<byte[]>? _nativeFrameAcknowledged;
    private readonly TimeProvider _time;
    private byte[]? _entryLogo;
    private KeyboardNativePresentation? _presentation; // the running owner session's declared native presentation (volatile)
    private readonly object _gate = new();
    private readonly string _settingsPath, _journalPath, _nativePreferencePath;
    private string _lastRestoreOrigin = "recorded-native";
    private volatile bool _enabled;
    private CancellationTokenSource? _sessionStop;
    private Task? _activeSession;
    private TaskCompletionSource<KeyboardResumeResult>? _resumeCompletion;
    private KeyboardDisplayStatusDto _status;
    private byte[] _pixels = new byte[6656];
    private CaseStatusObservation? _lastObservation; // guarded by _artwork, with _artworkVersion
    private readonly object _artwork = new();
    private long _artworkVersion;

    // Keep the saved preference separate from installed firmware output ownership. Operational native disable is not legacy admission.
    private bool LegacyDisplayEnabled => _enabled && _nativeMenu?.OwnsDisplayOutput != true;
    private string InactiveDisplayDetail => (_nativeMenu?.OwnsDisplayOutput == true
        ? "键盘屏幕由已安装的原生光域菜单策略管理；状态屏偏好已保留；" + NativePresentationNote()
        : "键盘原动画已恢复；") + $"native-brightness-policy={_lastRestoreOrigin}";

    // nativeFrameOverlay: optional pure scene overlay for post-logo native artwork only (never the logo). It gets a copy of the
    // selected packed frame and returns a packed frame of the same size; no I/O. Throwing or invalid answers keep the ordinary
    // frame and native control and are reported as unavailable.
    public KeyboardDisplayService(UnifiedRgbOptions options, IRgbService rgb, IScreenService screens,
        IRogWiredOledFactory factory, IWriterGuard guard, RogWiredLightingBridge? lighting = null,
        IRogWirelessOledFactory? wirelessFactory = null, PeripheralConfigurationGate? configurationGate = null,
        RogNativeMenuSource? nativeMenu = null, IKeyboardOledMedia? keyboardMedia = null, IScreenTimeline? sharedTimeline = null,
        TimeProvider? time = null, IKeyboardNativePresentationCapability? nativePresentation = null,
        Func<byte[], byte[]>? nativeFrameOverlay = null, Action<byte[]>? nativeFrameAcknowledged = null)
    {
        _options = options; _rgb = rgb; _screens = screens; _factory = factory; _guard = guard;
        _wirelessFactory = wirelessFactory;
        _configurationGate = configurationGate;
        _nativeMenu = nativeMenu;
        _nativePresentation = nativePresentation;
        _nativeFrameOverlay = nativeFrameOverlay;
        _nativeFrameAcknowledged = nativeFrameAcknowledged;
        _time = time ?? TimeProvider.System;
        _lighting = lighting ?? new RogWiredLightingBridge();
        _settingsPath = Path.Combine(options.ResolvedDataDirectory, "peripherals", "keyboard-display.json");
        _journalPath = Path.Combine(options.ResolvedDataDirectory, "peripherals", "keyboard-display-session.json");
        _nativePreferencePath = Path.Combine(options.ResolvedDataDirectory, "peripherals", "keyboard-native-display.json");
        _enabled = options.KeyboardDisplay.Enabled;
        string? error = null;
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = Read<LocalSettings>(_settingsPath);
                if (settings.Schema != 1) throw new InvalidDataException("Unknown keyboard settings schema.");
                _enabled = settings.Enabled;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _enabled = false; error = "本机控制屏设置无法读取"; }
        _status = new(_enabled, error is null ? "Stopped" : "Error", error ?? "键盘状态屏未启动", 0, 0, null, error);
        _animation = new KeyboardDisplayAnimation(options.KeyboardDisplay, options.ResolvedDataDirectory, keyboardMedia, sharedTimeline, time);
    }

    private bool NativeOwnsDisplay => _nativeMenu?.OwnsDisplayOutput == true;

    // Installed-native ownership suppresses the animation unless THIS owner session's firmware declared native presentation.
    private bool AnimationSuppressed => NativeOwnsDisplay && Volatile.Read(ref _presentation) is null;

    public async Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default)
    {
        await _animation.RefreshAsync(_screens, ct).ConfigureAwait(false);
        return AnimationStatus(_animation.Status(_enabled, AnimationSuppressed));
    }

    public async Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default) =>
        AnimationStatus(await _animation.SaveAsync(settings, _screens, _enabled, AnimationSuppressed, ct).ConfigureAwait(false));

    private KeyboardDisplayAnimationStatusDto AnimationStatus(KeyboardDisplayAnimationStatusDto status)
    {
        if (!NativeOwnsDisplay || Volatile.Read(ref _presentation) is null) return status;
        // Page OFF under a declared native presentation is not the legacy stock animation: the legacy wording must not claim it.
        var detail = _enabled ? status.Detail : status.State switch
        {
            KeyboardDisplayAnimationStates.Off => "键盘动画未开启",
            KeyboardDisplayAnimationStates.PageOff => "键盘状态屏偏好为关，不发送动画画面",
            KeyboardDisplayAnimationStates.NotAdmitted => "主机未准许键盘动画（" + _animation.AdmissionProblem + "）；已保存的偏好保持不变",
            _ => status.Detail,
        };
        return status with { Detail = detail + "；固件已声明原生光域画面支持：" + NativePresentationNote() + "拨杆仍由原生菜单控制" };
    }

    /// <summary>What the running declared presentation does, as the owner status must say it: each true native entry shows the
    /// logo first; then status page ON shows the case status, OFF asks for the stock animation, which has no verified in-epoch
    /// request with the lever kept and is reported as unavailable (the logo is held, never presented as the stock animation).</summary>
    private string NativePresentationNote()
    {
        if (Volatile.Read(ref _presentation) is not { } presentation) return string.Empty;
        var note = presentation.LogoAvailable ? "每次进入原生光域模式先显示光域标志约 1.2 秒，" : "光域标志渲染失败，进入原生光域模式直接显示所选画面，";
        note += _enabled ? "随后显示机箱状态（或已单独开启并绑定的键盘动画）；"
            : "状态屏偏好为关：原生模式下保留拨杆的键盘原动画尚无已验证接口，" + "沿用更新前的关闭页面输出" + "（不是原动画）；";
        if (presentation.OverlayProblem is { } problem) note += problem + "，场景叠加暂不可用，显示普通画面；";
        return note;
    }

    public Task<KeyboardDisplayAnimationPreviewDto> PreviewKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationPreviewRequestDto request, CancellationToken ct = default) =>
        _animation.PreviewAsync(request, _screens, ct);

    /// <summary>Terminal Host stop: the animation is retired before the owner session stops (its running preparation or preview is
    /// cancelled, the pending binding dropped and no later work admitted; an unfinished decode keeps the one worker until it ends).
    /// A session end, reconnect or page toggle never does this.</summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _animation.Dispose(); // the same idempotent, nonblocking retirement as Dispose
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _animation.Dispose();
        base.Dispose();
    }

    public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Volatile.Read(ref _status)); }

    public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (enabled) RequirePins();
            Save(_settingsPath, new LocalSettings(1, enabled));
            _enabled = enabled;
            var restartFaultedOwner = Volatile.Read(ref _status).State == "Error";
            // A declared native presentation re-selects its post-logo artwork in the same epoch: the page preference is not a
            // native entry, so it never restarts that owner (no DISARM/re-ARM, no fresh logo, the lever stays live).
            var sameEpoch = Volatile.Read(ref _presentation) is not null && _nativeMenu?.Requested == true;
            if ((!_lighting.Available && !sameEpoch) || restartFaultedOwner) _sessionStop?.Cancel();
            Publish(_nativeMenu?.OwnsDisplayOutput == true && !restartFaultedOwner ? "Stopped" : enabled ? "Connecting" : "Stopping", _nativeMenu?.OwnsDisplayOutput == true
                ? InactiveDisplayDetail : enabled ? "等待接入键盘状态屏" : "正在恢复键盘原显示设置");
        }
        return Task.CompletedTask;
    }

    private void RequirePins()
    {
        var o = _options.KeyboardDisplay;
        var native = _nativeMenu?.Requested == true && _nativeMenu.QualifiedRoute is not null;
        if ((!HasWiredPins(o) && !HasWirelessPins(o) && !native) || o.Brightness > 100)
            throw new InvalidOperationException("Keyboard display needs a verified wired or wireless identity and firmware pin.");
    }

    private static bool HasWiredPins(KeyboardDisplayOptions o) => o.HasVerifiedWiredIdentity;
    private static bool HasWirelessPins(KeyboardDisplayOptions o) => o.HasVerifiedWirelessIdentity;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool faulted = _status.LastError is not null;
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationTokenSource sessionStop;
            lock (_gate)
            {
                // Explicit enable/disable clears a sticky communication fault; a failed protocol is never retried in a loop.
                if (_status.State is "Connecting" or "Stopping") faulted = false;
                sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); _sessionStop = sessionStop;
            }
            using (sessionStop)
            {
                var sessionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    if (_configurationGate?.RogAcquisitionAllowed == false && _configurationGate.FreshResumeAllowed == false)
                    {
                        if (Volatile.Read(ref _status).LastError is null) Publish("Stopped", "ROG interfaces are released for peripheral configuration");
                        await Task.Delay(500, sessionStop.Token).ConfigureAwait(false); continue;
                    }
                    if ((!_enabled && !_options.KeyboardDisplay.HasVerifiedLightingRoute && _nativeMenu?.Requested != true) || faulted)
                    {
                        if (!_enabled && !faulted) Publish("Stopped", "键盘状态屏已关闭");
                        await Task.Delay(500, sessionStop.Token).ConfigureAwait(false); continue;
                    }
                    RequirePins();
                    var verdict = _guard.Check(DeviceClass.AsusRog);
                    if (verdict.State != GuardState.Clear) throw new InvalidOperationException("Keyboard display blocked by an existing lighting writer.");
                    lock (_gate) _activeSession = sessionDone.Task;
                    var connection = await OpenConnectionAsync(sessionStop.Token).ConfigureAwait(false);
                    using (connection)
                    {
                        if (connection is null)
                        {
                            sessionDone.TrySetResult();
                            CompleteResume(new KeyboardResumeResult(false, null));
                            Publish(_enabled ? "Waiting" : "Stopped", _enabled ? "等待已绑定键盘的 USB 或 2.4G 无线连接" : "键盘状态屏已关闭；等待已绑定灯光连接");
                            await Task.Delay(5000, sessionStop.Token).ConfigureAwait(false); continue;
                        }
                        await RunSessionAsync(connection, sessionStop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                catch (RogOledConnectionLostException ex)
                {
                    CompleteResume(new KeyboardResumeResult(true, ex));
                    Publish("Waiting", "键盘控制接口已断开；保留原设置并等待有线或 2.4G 无线重连");
                    // A toggle or Host stop may cancel this delay after control has already entered the catch.
                    try { await Task.Delay(1000, sessionStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
                catch (RogOledTimeoutException ex)
                {
                    CompleteResume(new KeyboardResumeResult(true, ex));
                    // Recovery was already bounded by RunSessionAsync. An ambiguous ACK must not start an upload retry loop.
                    faulted = true;
                    Publish("Error", "键盘响应超时；检查连接后关闭再开启，未自动重发画面", error: ex.GetType().Name + ": " + ex.Message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    CompleteResume(new KeyboardResumeResult(true, ex));
                    faulted = true; Publish("Error", ex is RecoveryScopeException scope ? scope.UserDetail : "键盘状态屏接入失败；检查连接后关闭再开启", error: ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_sessionStop, sessionStop)) _sessionStop = null;
                        if (ReferenceEquals(_activeSession, sessionDone.Task)) _activeSession = null;
                    }
                    sessionDone.TrySetResult();
                }
            }
            faulted = Volatile.Read(ref _status).LastError is not null;
        }
    }

    private Task<IRogOledConnection?> OpenConnectionAsync(CancellationToken ct)
    {
        return _configurationGate is null
            ? Task.FromResult(OpenCurrentConnection())
            : _configurationGate.RunAcquisitionAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(_configurationGate.RogAcquisitionAllowed || _configurationGate.FreshResumeAllowed
                    ? OpenCurrentConnection() : null);
            }, ct);

        IRogOledConnection? OpenCurrentConnection()
        {
            if (_nativeMenu?.Requested == true)
            {
                var native = _nativeMenu.QualifiedRoute;
                // Native admission is independent of legacy OLED/lighting pins. A miss stays a miss:
                // never fall through to another factory or a legacy image owner after native selection.
                // The configured native header is diagnostic; the route opens on its known identity family anchor.
                return native?.ConnectionKind switch
                {
                    "wired" => _factory.TryOpen(native.DeviceKey, native.RouteAnchor),
                    "wireless" => _wirelessFactory?.TryOpen(native.DeviceKey, native.RouteAnchor),
                    _ => null,
                };
            }
            IRogOledConnection? connection = HasWiredPins(_options.KeyboardDisplay)
                && (_enabled || _options.KeyboardDisplay.HasVerifiedWiredLighting || _nativeMenu?.Requested == true) ? _factory.TryOpen(_options.KeyboardDisplay.DeviceKey, WiredLabel) : null;
            if (connection is null && _wirelessFactory is not null && HasWirelessPins(_options.KeyboardDisplay)
                && (_enabled || _options.KeyboardDisplay.HasVerifiedWirelessLighting || _nativeMenu?.Requested == true))
                connection = _wirelessFactory.TryOpen(_options.KeyboardDisplay.WirelessReceiverKey, WirelessLabel);
            return connection;
        }
    }

    public async Task ReleaseForPeripheralConfigurationAsync(CancellationToken ct)
    {
        CancellationTokenSource? stop;
        Task? active;
        lock (_gate)
        {
            stop = _sessionStop;
            active = _activeSession;
        }
        stop?.Cancel();
        if (active is not null) await active.WaitAsync(ct).ConfigureAwait(false);
        if (!File.Exists(_journalPath)) return;
        if (active is not null) throw new IOException("Keyboard original settings remain unrecovered; journal retained and no restoration retry was made.");
        var prior = Read<RecoveryRecord>(_journalPath);
        IRogOledConnection? connection;
        var native = _nativeMenu?.QualifiedRoute;
        // A journal is recovered only through a route of the same device and transport label (the profile is checked on the open
        // connection); its recorded OEM bytes and today's header notes are diagnostic, so older labels stay readable as-is.
        if (native is not null && prior.DeviceKey == native.DeviceKey && SameProtocol(prior.FirmwareHeader, native.RouteAnchor))
            connection = native.ConnectionKind == "wired" ? _factory.TryOpen(native.DeviceKey, native.RouteAnchor)
                : _wirelessFactory?.TryOpen(native.DeviceKey, native.RouteAnchor);
        else if (HasWiredPins(_options.KeyboardDisplay) && prior.DeviceKey == _options.KeyboardDisplay.DeviceKey
            && SameProtocol(prior.FirmwareHeader, WiredLabel))
            connection = _factory.TryOpen(_options.KeyboardDisplay.DeviceKey, WiredLabel);
        else if (_wirelessFactory is not null && HasWirelessPins(_options.KeyboardDisplay)
            && prior.DeviceKey == _options.KeyboardDisplay.WirelessReceiverKey
            && SameProtocol(prior.FirmwareHeader, WirelessLabel))
            connection = _wirelessFactory.TryOpen(_options.KeyboardDisplay.WirelessReceiverKey, WirelessLabel);
        else
            throw new RecoveryScopeException("Recovery journal does not match a verified configured transport; journal retained.",
                "恢复记录与已验证的有线或无线传输不匹配；已保留，未替换或猜测设备");
        using (connection ?? throw new IOException("The exact recorded keyboard transport is absent; original settings remain unrecovered and the journal is retained."))
            await RunSessionAsync(connection, ct, recoveryOnly: true).ConfigureAwait(false);
        if (File.Exists(_journalPath)) throw new IOException("Recorded-transport keyboard recovery was not verified; journal retained.");
    }

    public async Task ResumeAfterPeripheralConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_enabled && !_options.KeyboardDisplay.HasVerifiedLightingRoute && _nativeMenu?.Requested != true) return;
        Task<KeyboardResumeResult> completion;
        lock (_gate)
        {
            var fresh = new TaskCompletionSource<KeyboardResumeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeCompletion = fresh;
            completion = fresh.Task;
            Publish(_enabled ? "Connecting" : "Stopping", "正在重新读取键盘当前显示与灯光设置");
            _sessionStop?.Cancel();
        }
        var result = await completion.ConfigureAwait(false);
        if (result.Failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Failure).Throw();
    }

    private void CompleteResume(KeyboardResumeResult result)
    {
        TaskCompletionSource<KeyboardResumeResult>? completion;
        lock (_gate)
        {
            completion = _resumeCompletion;
            _resumeCompletion = null;
        }
        completion?.TrySetResult(result);
    }

    private async Task RunSessionAsync(IRogOledConnection connection, CancellationToken ct, bool recoveryOnly = false)
    {
        var session = new RogWiredOledSession(connection);
        await using var nativeMenu = _nativeMenu?.Attach(connection);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? monitor = null;
        byte? original = null;
        RogWiredLightingBridge.Owner? lighting = null;
        var display = LegacyDisplayEnabled;
        // Lighting follows the manually verified transport and selected device; configured header notes are diagnostic.
        var ownsLighting = connection.ConnectionKind == "wired"
            ? _options.KeyboardDisplay.HasVerifiedWiredLighting && connection.DeviceKey == _options.KeyboardDisplay.DeviceKey
            : connection.ConnectionKind == "wireless" && _options.KeyboardDisplay.HasVerifiedWirelessLighting
                && connection.DeviceKey == _options.KeyboardDisplay.WirelessReceiverKey;
        Exception? failure = null;
        var confirmedRemoval = false;
        var recoveryComplete = false;
        try
        {
            Publish("Connecting", "正在核对键盘与原显示设置");
            var brightness = await session.SnapshotAsync(connection.FirmwareHeader, ct).ConfigureAwait(false);
            if (File.Exists(_journalPath))
            {
                var priorBytes = ReadBytes(_journalPath);
                var prior = JsonSerializer.Deserialize<RecoveryRecord>(priorBytes) ?? throw new InvalidDataException("Missing keyboard recovery record.");
                if (prior.DeviceKey != connection.DeviceKey || !SameProtocol(prior.FirmwareHeader, ConnectionLabel(connection))
                    || prior.Brightness > 100 || (prior.Lighting is null) != (prior.StaticBank is null)) throw new InvalidDataException("Previous OLED session recovery identity differs.");
                if (prior.Lighting is not null && !ownsLighting)
                    throw new InvalidDataException("Previous lighting recovery requires verification for the current transport; journal retained.");
                if (prior.Schema == 1)
                {
                    // Never infer an old profile from the current one or replay
                    // an unscoped snapshot. Only adopt an already matching fresh
                    // state, preserving the old bytes as an archived record.
                    var matches = brightness == prior.Brightness;
                    if (prior.Lighting is not null && prior.StaticBank is not null)
                    {
                        await session.SnapshotLightingAsync(ct).ConfigureAwait(false);
                        matches &= session.OriginalLighting!.SameContent(prior.Lighting)
                            && session.OriginalStaticBank!.SameContent(prior.StaticBank);
                    }
                    if (!matches) throw MissingRecoveryScope();
                    var archive = _journalPath + ".schema1-" + Convert.ToHexString(SHA256.HashData(priorBytes)) + ".archive";
                    if (File.Exists(archive))
                    {
                        if (!ReadBytes(archive).AsSpan().SequenceEqual(priorBytes)) throw new InvalidDataException("Recovery archive differs.");
                    }
                    else
                    {
                        using var archiveStream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        archiveStream.Write(priorBytes);
                        archiveStream.Flush(true);
                    }
                }
                else
                {
                    if (prior.Schema != 2 || prior.ProfileToken is < 1 or > 6) throw MissingRecoveryScope();
                    if (prior.ProfileToken != session.VerifiedProfileToken)
                        throw new RecoveryScopeException("Previous OLED session belongs to a different keyboard profile; journal retained.",
                            "恢复记录属于其他键盘配置档；切回原档后重新启用");
                    brightness = prior.Brightness;
                    await RestoreNativeGoalAsync(session, connection, brightness, allowFreshNative: false, ct).ConfigureAwait(false);
                    if (prior.Lighting is not null && prior.StaticBank is not null)
                        await session.RestoreLightingAsync(prior.Lighting, prior.StaticBank, ct).ConfigureAwait(false);
                }
            }
            original = brightness;
            if (ownsLighting) await session.SnapshotLightingAsync(ct).ConfigureAwait(false);
            if (recoveryOnly)
            {
                recoveryComplete = true;
                return;
            }
            // Persist recovery intent before the first mutation, including process termination/restart.
            Save(_journalPath, new RecoveryRecord(2, connection.DeviceKey, ConnectionLabel(connection), brightness, session.VerifiedProfileToken,
                session.OriginalLighting, session.OriginalStaticBank));
            await RefreshArtworkAsync(ct).ConfigureAwait(false); // No raw image is presented as a completed device write.
            if (display) await session.StartOutputAsync(_options.KeyboardDisplay.Brightness, ct).ConfigureAwait(false);
            else
            {
                var goal = await RestoreNativeGoalAsync(session, connection, brightness, allowFreshNative: true, ct).ConfigureAwait(false);
                if (session.CurrentNativeBrightness != goal) await session.RestoreAsync(goal, ct).ConfigureAwait(false);
                Publish("Stopped", InactiveDisplayDetail);
            }
            CompleteResume(new KeyboardResumeResult(true, null));
            if (ownsLighting) lighting = _lighting.Attach();
            monitor = MonitorAsync();
            var presentation = nativeMenu is not null && NativePresentationDeclared(connection)
                ? new KeyboardNativePresentation(_animation, EntryLogo(), _time, _nativeFrameOverlay, _nativeFrameAcknowledged) : null;
            // Ordinary application output has its own entry too. It must not upload a status/animation frame before a later
            // native entry gets its logo. Reuse only the pure selector and the existing owner's upload acknowledgement/lease.
            var ordinaryPresentation = !NativeOwnsDisplay ? new KeyboardNativePresentation(_animation, EntryLogo(), _time) : null;
            Volatile.Write(ref _presentation, presentation);
            if (presentation is not null && !display) Publish("Stopped", InactiveDisplayDetail); // now says what an entry presents
            // The animation selector is the display's only cadence gate. Without declared native presentation the native ticks keep
            // reading the status pixels directly; with it, a native entry asks the presentation, and the legacy selector then
            // leaves the animation's held frame alone. Installed ownership keeps display false, so the legacy path never uploads
            // and the presentation's logo is the first image of every native entry (ARM, then logo, then the page's artwork).
            await session.RunAsync(() => ordinaryPresentation is not null && LegacyDisplayEnabled
                    ? ordinaryPresentation.Latest(Volatile.Read(ref _pixels), pageEnabled: true)
                    : presentation?.Entered == true ? Volatile.Read(ref _pixels)
                    : _animation.Select(Volatile.Read(ref _pixels), _enabled, NativeOwnsDisplay),
                () =>
                {
                    var animated = ordinaryPresentation?.Acknowledge() ?? _animation.Uploaded();
                    var artwork = ordinaryPresentation?.Shown == KeyboardNativeArtwork.Logo ? "光域标志" : animated ? "动画" : "状态";
                    Publish("Running", $"键盘{artwork}画面已获{(connection.ConnectionKind == "wireless" ? "无线" : "有线")}通信确认；拨杆未启用", session);
                }, ct, lighting, display,
                () => LegacyDisplayEnabled, _options.KeyboardDisplay.Brightness,
                on =>
                {
                    if (!on) { ordinaryPresentation?.Stepped(false); Publish("Stopped", InactiveDisplayDetail, session); }
                },
                restoreNativeGoal: token => RestoreNativeGoalAsync(session, connection, brightness, allowFreshNative: false, token),
                nativeBrightnessChanged: (current, token) => PersistObservedNativeAsync(connection, session.VerifiedProfileToken, current, token),
                nativeTick: nativeMenu is null ? null : presentation is null
                    ? token => nativeMenu.StepAsync(() => Volatile.Read(ref _pixels), token)
                    : async token =>
                    {
                        var armed = presentation.Stepped(await nativeMenu.StepAsync(
                            () => presentation.Latest(Volatile.Read(ref _pixels), _enabled, newEntry: !nativeMenu.Armed), token).ConfigureAwait(false));
                        if (presentation.TakeChanged() && Volatile.Read(ref _status) is { LastError: null } current)
                            Publish(current.State, InactiveDisplayDetail, session);
                        return armed;
                    },
                nativeWake: nativeMenu is null ? null : () => nativeMenu.HasChanges,
                nativeStopped: nativeMenu is null ? null : token => nativeMenu.StopAsync(token)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex; }
        finally
        {
            Volatile.Write(ref _presentation, null);
            _animation.EndSession();
            if (nativeMenu is not null)
            {
                try { await nativeMenu.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failure ??= ex; }
            }
            monitorStop.Cancel();
            if (monitor is not null) await monitor.ConfigureAwait(false);
            if (lighting is not null) _lighting.Detach(lighting);
            var errors = new List<Exception>();
            confirmedRemoval = failure is RogOledConnectionLostException && !connection.SameDevice();
            // Native faults suppress unsafe cleanup; they do not prove removal or authorize an automatic new owner.
            var recoveryBlocked = confirmedRemoval || nativeMenu?.RecoveryBlocked == true;
            if (!recoveryComplete && !recoveryBlocked && session.LightingTouched && session.OriginalLighting is { } native && session.OriginalStaticBank is { } bank)
            {
                try { using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await session.RestoreLightingAsync(native, bank, recovery.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
            }
            if (!recoveryComplete && !recoveryBlocked && original.HasValue && session.Touched)
            {
                try { using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await RestoreNativeGoalAsync(session, connection, original.Value, allowFreshNative: false, recovery.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
            }
            if (errors.Count > 0)
            {
                if (failure is not null) errors.Insert(0, failure);
                failure = new AggregateException("Keyboard original settings recovery incomplete; journal retained.", errors);
            }
            else if (original.HasValue && !recoveryBlocked) File.Delete(_journalPath);
        }
        if (failure is RogOledConnectionLostException && !confirmedRemoval)
            failure = new IOException("Keyboard control stream ended without confirmed physical removal; no automatic retry.", failure);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task MonitorAsync()
        {
            try
            {
                while (!monitorStop.IsCancellationRequested)
                {
                    await Task.Delay(2000, monitorStop.Token).ConfigureAwait(false);
                    if (_guard.Check(DeviceClass.AsusRog).State != GuardState.Clear)
                        throw new InvalidOperationException("Another ROG writer appeared.");
                    await RefreshArtworkAsync(monitorStop.Token).ConfigureAwait(false);
                    if (_animation.Wanted) await _animation.RefreshAsync(_screens, monitorStop.Token).ConfigureAwait(false);
                    var s = Volatile.Read(ref _status);
                    if (s.State == "Running") Publish("Running", s.Detail, session);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Publish("Error", "状态读取或显示监测失败", session, ex.GetType().Name);
                lock (_gate) _sessionStop?.Cancel();
            }
        }
    }

    private async Task<byte> RestoreNativeGoalAsync(RogWiredOledSession session, IRogOledConnection connection,
        byte sourceBrightness, bool allowFreshNative, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = new NativeScope(connection.DeviceKey, ConnectionLabel(connection), session.VerifiedProfileToken);
        var preference = ReadNativePreference(scope);
        byte goal;
        string origin;
        if (allowFreshNative && sourceBrightness > 0)
        {
            goal = sourceBrightness;
            origin = "fresh-native-while-disabled";
        }
        else if (preference is not null)
        {
            goal = preference.Brightness;
            origin = preference.Origin;
        }
        else if (sourceBrightness > 0)
        {
            goal = sourceBrightness;
            origin = "pre-takeover-native";
        }
        else
        {
            goal = 100;
            origin = "explicit-fallback-100";
        }
        SaveNativePreference(new NativeDisplayPreference(1, scope.DeviceKey, scope.FirmwareHeader, scope.ProfileToken, goal, origin));
        _lastRestoreOrigin = origin;
        await session.RestoreAsync(goal, ct).ConfigureAwait(false);
        return goal;
    }

    private Task PersistObservedNativeAsync(IRogOledConnection connection, byte profile, byte brightness, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (brightness == 0 || brightness > 100) return Task.CompletedTask;
        SaveNativePreference(new NativeDisplayPreference(1, connection.DeviceKey, ConnectionLabel(connection), profile,
            brightness, "fresh-native-while-disabled"));
        _lastRestoreOrigin = "fresh-native-while-disabled";
        return Task.CompletedTask;
    }

    private NativeDisplayPreference? ReadNativePreference(NativeScope scope)
    {
        if (!File.Exists(_nativePreferencePath)) return null;
        var preference = Read<NativeDisplayPreference>(_nativePreferencePath);
        if (preference.Schema != 1 || preference.Brightness is 0 or > 100
            || preference.Origin is not ("fresh-native-while-disabled" or "pre-takeover-native" or "explicit-fallback-100"))
            throw new InvalidDataException("Native display preference is invalid; restoration is refused.");
        return preference.DeviceKey == scope.DeviceKey && SameProtocol(preference.FirmwareHeader, scope.FirmwareHeader)
            && preference.ProfileToken == scope.ProfileToken ? preference : null;
    }

    // Recorded scope binds the same device key and profile (checked by callers) plus the same transport label; OEM bytes in
    // either label are diagnostic. Records stay in their existing format and existing bytes are never rewritten.
    private static bool SameProtocol(string? recorded, string? current) =>
        recorded is not null && recorded == current || RogRuntimeCompatibility.IsCompatibleAnchor(recorded, current);

    // Configured header notes are diagnostic: a note already in its transport's label form is kept verbatim, anything else
    // (blank, stale, arbitrary) routes under the neutral transport label.
    private string WiredLabel => RogRuntimeCompatibility.RouteLabel("wired", _options.KeyboardDisplay.FirmwareHeader)!;
    private string WirelessLabel => RogRuntimeCompatibility.RouteLabel("wireless", _options.KeyboardDisplay.WirelessFirmwareHeader)!;
    private static string ConnectionLabel(IRogOledConnection connection) =>
        RogRuntimeCompatibility.RouteLabel(connection.ConnectionKind, connection.FirmwareHeader) ?? connection.FirmwareHeader;

    private void SaveNativePreference(NativeDisplayPreference preference)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_nativePreferencePath)!);
        var temporary = _nativePreferencePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preference);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, _nativePreferencePath, true);
    }

    /// <summary>
    /// Shows a native-menu case readback through this owner's own artwork at once instead of at the next 2 s refresh, which still
    /// replaces it as usual. Refused while ROG interfaces are held for peripheral configuration, and when <paramref name="current"/>
    /// (checked last, under the artwork lock) says the readback is no longer the caller's latest.
    /// </summary>
    internal bool PresentCaseObservation(CaseStatusObservation observation, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(current);
        if (_configurationGate?.RogAcquisitionAllowed == false) return false;
        var pixels = KeyboardStatusArtwork.Render(observation);
        lock (_artwork)
        {
            if (_configurationGate?.RogAcquisitionAllowed == false || !current()) return false;
            _artworkVersion++;
            Volatile.Write(ref _pixels, pixels);
            _lastObservation = observation;
        }
        return true;
    }

    internal ReadOnlyMemory<byte> ArtworkPixels => Volatile.Read(ref _pixels);

    /// <summary>Only an explicit Available answer under installed-native ownership; absent, unrecognized or failing is Unavailable.</summary>
    private bool NativePresentationDeclared(IRogOledConnection connection)
    {
        if (_nativePresentation is null || !NativeOwnsDisplay) return false;
        try
        {
            return _nativePresentation.Evaluate(new(connection.DeviceKey, connection.FirmwareHeader, connection.ConnectionKind))
                == KeyboardNativePresentationSupport.Available;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>The entry logo, rendered once off the actor. A failed render skips the overlay; the content is still presented.</summary>
    private byte[]? EntryLogo()
    {
        if (Volatile.Read(ref _entryLogo) is { } logo) return logo;
        try { logo = KeyboardStatusArtwork.RenderLogo(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        Volatile.Write(ref _entryLogo, logo);
        return logo;
    }

    internal ReadOnlyMemory<byte> EntryLogoPixels => EntryLogo();

    private async Task RefreshArtworkAsync(CancellationToken ct)
    {
        var version = Interlocked.Read(ref _artworkVersion);
        CaseStatusObservation observation;
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct); bound.CancelAfter(5000);
            var before = await _rgb.GetSceneAsync(bound.Token).ConfigureAwait(false);
            var controllers = await _rgb.ListControllersAsync(bound.Token).ConfigureAwait(false);
            var scene = await _screens.GetScreenSceneAsync(bound.Token).ConfigureAwait(false);
            var screens = await _screens.ListScreensAsync(bound.Token).ConfigureAwait(false);
            var after = await _rgb.GetSceneAsync(bound.Token).ConfigureAwait(false);
            observation = JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after)
                ? CaseStatusSummary.Describe(after, controllers, scene, screens)
                : new(CaseStatusState.Pending, "场景正在变化", "等待新的后台回读");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        { observation = new(CaseStatusState.Unknown, "等待光域连接", "后台状态暂不可用"); }
        ct.ThrowIfCancellationRequested();
        // A native-menu readback presented while this one was read is newer: keep it until the next refresh.
        lock (_artwork) if (version != _artworkVersion || observation == _lastObservation) return;
        var pixels = KeyboardStatusArtwork.Render(observation);
        lock (_artwork)
        {
            if (version != _artworkVersion) return;
            Volatile.Write(ref _pixels, pixels);
            _lastObservation = observation;
        }
    }

    private void Publish(string state, string detail, RogWiredOledSession? session = null, string? error = null) =>
        Volatile.Write(ref _status, new(_enabled, state, detail, session?.Frames ?? 0, session?.Renewals ?? 0, DateTimeOffset.UtcNow, error));

    private static T Read<T>(string path)
    {
        var bytes = ReadBytes(path);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Missing keyboard setting.");
    }

    internal static byte[] ReadBytes(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[2049];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes.AsSpan(count));
            if (read == 0) break;
            count += read;
        }
        if (count > 2048) throw new InvalidDataException("Keyboard setting exceeds bound.");
        return bytes[..count];
    }

    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value)); File.Move(temp, path, true);
    }

    private sealed record LocalSettings(int Schema, bool Enabled);
    private sealed record KeyboardResumeResult(bool Present, Exception? Failure);
    private sealed record NativeScope(string DeviceKey, string FirmwareHeader, byte ProfileToken);
    private sealed record NativeDisplayPreference(int Schema, string DeviceKey, string FirmwareHeader, byte ProfileToken,
        byte Brightness, string Origin);
    private static RecoveryScopeException MissingRecoveryScope() => new(
        "Previous OLED session recovery lacks verified profile scope; journal retained.",
        "旧恢复记录缺少配置档标识；已保留，未恢复键盘设置");
    private sealed class RecoveryScopeException(string message, string userDetail) : IOException(message)
    {
        public string UserDetail { get; } = userDetail;
    }
    private sealed record RecoveryRecord(int Schema, string DeviceKey, string FirmwareHeader, byte Brightness, byte ProfileToken,
        RogKeyboardNativeState? Lighting = null, RogKeyboardNativeState? StaticBank = null);
}
