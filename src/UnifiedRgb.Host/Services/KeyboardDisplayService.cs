using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>Host-owned keyboard display, independent of the case-screen sleep set and UI process lifetime.</summary>
public sealed partial class KeyboardDisplayService : BackgroundService, IKeyboardDisplayService
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
    private readonly Func<long>? _nativeFrameOverlayRevision;
    private readonly TimeProvider _time;
    private readonly CodexEntryHold _codexEntry;
    private readonly Func<byte[]?>? _idleFrame;
    private readonly Func<byte[]?>? _homeAssistantFrame;
    private readonly Func<KeyboardCodexUsageDto>? _codexStatus;
    private readonly Func<bool, CancellationToken, KeyboardCodexUsageDto>? _setCodexEnabled;
    private readonly Func<string, bool> _wiredRoutePresent;
    private byte[]? _entryLogo;
    private KeyboardNativePresentation? _presentation; // the running owner session's declared native presentation (volatile)
    private readonly object _gate = new();
    private readonly object _nativePreferenceGate = new();
    private readonly string _settingsPath, _journalPath, _nativePreferencePath;
    private string _lastRestoreOrigin = "recorded-native";
    private volatile bool _enabled;
    private CancellationTokenSource? _sessionStop;
    private Task? _activeSession;
    private IRogOledConnection? _routeRecoveryOnly;
    private TaskCompletionSource<KeyboardResumeResult>? _resumeCompletion;
    // Fresh-owner attempts after an RF keyboard was not ready at startup; reset by a successful startup or an explicit request.
    private const int ColdStartRetries = 3;
    private int _coldStartRetries;
    private int _observationReplacements;
    private long _nativeImageRetries;
    private long _intentRevision;
    private KeyboardDisplayStatusDto _status;
    private byte[] _pixels = new byte[6656];
    private CaseStatusObservation? _lastObservation; // guarded by _artwork, with _artworkVersion
    private readonly object _artwork = new();
    private long _artworkVersion;
    private int _caseBlackout = -1; // -1 unknown, 0 active, 1 confirmed blackout; owner reads cached state only

    private bool CaseBlackout => Volatile.Read(ref _caseBlackout) == 1;

    // Keep the saved preference separate from installed firmware output ownership. Operational native disable is not legacy admission.
    private bool LegacyDisplayEnabled => _options.KeyboardDisplay.HostArtworkEnabled && _enabled && _nativeMenu?.OwnsDisplayOutput != true;
    private const string HostArtworkPausedDetail = "主机 OLED 画面上传已暂停：仅保留原生控制和现有 RGB 路线；设备入场标志由键盘显示，素材、时间轴与开启偏好均已保留，未重发失败画面。";
    private string InactiveDisplayDetail => (_nativeMenu?.OwnsDisplayOutput == true
        ? "键盘屏幕由已安装的原生光域菜单策略管理；状态屏偏好已保留；" + NativePresentationNote()
        : "键盘原动画已恢复；") + $"native-brightness-policy={_lastRestoreOrigin}";

    // nativeFrameOverlay: optional pure scene overlay for the native interaction popup only (never the logo or idle). It gets a copy of the
    // selected packed frame and returns a packed frame of the same size; no I/O. Throwing or invalid answers keep the ordinary
    // frame and native control and are reported as unavailable.
    public KeyboardDisplayService(UnifiedRgbOptions options, IRgbService rgb, IScreenService screens,
        IRogWiredOledFactory factory, IWriterGuard guard, RogWiredLightingBridge? lighting = null,
        IRogWirelessOledFactory? wirelessFactory = null, PeripheralConfigurationGate? configurationGate = null,
        RogNativeMenuSource? nativeMenu = null, IKeyboardOledMedia? keyboardMedia = null, IScreenTimeline? sharedTimeline = null,
        TimeProvider? time = null, IKeyboardNativePresentationCapability? nativePresentation = null,
        Func<byte[], byte[]>? nativeFrameOverlay = null, Action<byte[]>? nativeFrameAcknowledged = null,
        Func<long>? nativeFrameOverlayRevision = null, IClock? masterClock = null, Func<string, int?>? sceneLoopDurationMs = null,
        Func<byte[]?>? idleFrame = null, Func<KeyboardCodexUsageDto>? codexStatus = null,
        Func<bool, CancellationToken, KeyboardCodexUsageDto>? setCodexEnabled = null,
        Func<string, bool>? wiredRoutePresent = null, Func<byte[]?>? homeAssistantFrame = null)
    {
        _options = options; _rgb = rgb; _screens = screens; _factory = factory; _guard = guard;
        _idleFrame = idleFrame; _codexStatus = codexStatus; _setCodexEnabled = setCodexEnabled;
        _homeAssistantFrame = homeAssistantFrame;
        _wiredRoutePresent = wiredRoutePresent ?? ExactWiredPresent;
        _wirelessFactory = wirelessFactory;
        _configurationGate = configurationGate;
        _nativeMenu = nativeMenu;
        _nativePresentation = nativePresentation;
        _nativeFrameOverlay = nativeFrameOverlay;
        _nativeFrameAcknowledged = nativeFrameAcknowledged;
        _nativeFrameOverlayRevision = nativeFrameOverlayRevision;
        _time = time ?? TimeProvider.System;
        _codexEntry = new(_time);
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
        _animation = new KeyboardDisplayAnimation(options.KeyboardDisplay, options.ResolvedDataDirectory, keyboardMedia, sharedTimeline, time, masterClock, sceneLoopDurationMs);
    }

    private bool NativeOwnsDisplay => _nativeMenu?.OwnsDisplayOutput == true;

    public Task<KeyboardCodexUsageDto> GetKeyboardCodexUsageAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CodexStatus(_codexStatus?.Invoke() ?? throw new NotSupportedException("Codex usage is unavailable.")));
    }

    public Task<KeyboardCodexUsageDto> SetKeyboardCodexUsageEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CodexStatus(_setCodexEnabled?.Invoke(enabled, ct) ?? throw new NotSupportedException("Codex usage is unavailable.")));
    }

    private KeyboardCodexUsageDto CodexStatus(KeyboardCodexUsageDto status) => status with
    {
        IndependentPageRouting = true,
        LastSuccess = null,
        DedicatedPageSupported = _nativeMenu?.DedicatedPageSupported,
        DedicatedPageActive = _nativeMenu?.DedicatedPageActive == true && _options.KeyboardDisplay.HostArtworkEnabled,
        OpenActionStatus = _nativeMenu?.OpenActionStatus ?? status.OpenActionStatus,
    };

    // Installed-native ownership suppresses the animation unless THIS owner session's firmware declared native presentation.
    private bool AnimationSuppressed => NativeOwnsDisplay && Volatile.Read(ref _presentation) is null;

    // A declared native presentation chooses its idle from the animation preference alone; the case status-page preference keeps
    // its ordinary meaning for non-native output.
    private bool AnimationPageEnabled => _enabled || (NativeOwnsDisplay && Volatile.Read(ref _presentation) is not null);

    public async Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default)
    {
        await _animation.RefreshAsync(_screens, ct).ConfigureAwait(false);
        return AnimationStatus(_animation.Status(AnimationPageEnabled, AnimationSuppressed));
    }

    public async Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default) =>
        AnimationStatus(await _animation.SaveAsync(settings, _screens, AnimationPageEnabled, AnimationSuppressed, ct).ConfigureAwait(false));

    private KeyboardDisplayAnimationStatusDto AnimationStatus(KeyboardDisplayAnimationStatusDto status)
    {
        if (!_options.KeyboardDisplay.HostArtworkEnabled)
            return status with { State = KeyboardDisplayAnimationStates.Fallback, Detail = HostArtworkPausedDetail };
        if (!NativeOwnsDisplay || Volatile.Read(ref _presentation) is null) return status;
        var policy = CaseBlackout ? "机箱已熄灯：待机请求键盘原厂动画，场景素材偏好保留"
            : "机箱开灯或状态待确认：待机显示场景素材；素材不可用时显示状态卡，不切换原厂动画";
        return status with { Detail = policy + "；" + status.Detail + "；" + NativePresentationNote() };
    }

    private string NativeIdlePolicyNote() => CaseBlackout
        ? "待机：机箱已熄灯，请求键盘原厂动画；"
        : "待机：机箱开灯时显示场景素材；准备或不可用时显示状态卡；只有熄灯才请求原厂动画；";

    /// <summary>What the running declared presentation does, as the owner status must say it: the entry logo once, then the idle
    /// chosen by case power (scene content while ON, original only at confirmed blackout), and a brief status popup after a lever action. The
    /// stored animation is only requested through the existing restore; its reload or visibility is never claimed.</summary>
    private string NativePresentationNote()
    {
        if (!_options.KeyboardDisplay.HostArtworkEnabled) return HostArtworkPausedDetail;
        if (Volatile.Read(ref _presentation) is not { } presentation) return string.Empty;
        var note = _options.KeyboardDisplay.DeviceShowsNativeEntryLogo
            ? "进入原生光域时由键盘自行显示一次标志；"
            : presentation.LogoAvailable ? "进入原生光域时主机显示一次光域标志约 1.2 秒；"
            : "光域标志渲染失败，进入原生光域后直接进入待机；";
        var custom = _animation.Status(pageEnabled: true, nativeOwnsDisplay: false);
        note += NativeIdlePolicyNote();
        note += presentation.IdleChoice == KeyboardNativeArtwork.HomeAssistantScenes ? "独立 HA 页：上下拨选家庭场景，中按执行；"
            : presentation.IdleChoice == KeyboardNativeArtwork.CodexQuota ? "独立 Codex 页：显示本机额度缓存；通过键盘原有实体翻页键选择，不影响光域素材或机箱电源；" : custom.State switch
        {
            KeyboardDisplayAnimationStates.Off => "场景素材偏好为关；",
            KeyboardDisplayAnimationStates.NotAdmitted => "主机未准许场景素材；",
            KeyboardDisplayAnimationStates.SettingsError => "场景素材设置无法读取；",
            _ when presentation.IdleChoice == KeyboardNativeArtwork.DeviceAnimation =>
                "原厂动画请求状态以机箱熄灯状态为准；",
            _ when presentation.IdleChoice == KeyboardNativeArtwork.StatusPage =>
                "场景素材当前无法播放（" + CustomIdleReason(custom) + "），显示状态卡；",
            _ when KeyboardDisplayAnimation.IsLocal(custom.Settings.TimelineId) =>
                "待机：按共享主时钟的当前位置播放本机素材库中的键盘动画，不依赖机箱屏幕、场景或灯光；",
            _ => "待机：按共享场景时间轴的当前位置播放跟随场景包的键盘动画；",
        };
        note += "只有有效的拨杆上下拨或按下操作会在操作期间及之后短暂显示状态或场景名称，随后恢复待机；状态屏偏好不改变原生待机；"
            + "键盘原动画仅通过现有恢复请求申请，主机不能确认其已重新加载或可见，以键盘实际显示为准；";
        if (presentation.OverlayProblem is { } problem) note += problem + "，场景名称叠加暂不可用，显示普通状态画面；";
        return note;
    }

    // The concrete reason a custom idle is not playable now, by source; the state code stays as the diagnostic value.
    private static string CustomIdleReason(KeyboardDisplayAnimationStatusDto custom)
    {
        var local = KeyboardDisplayAnimation.IsLocal(custom.Settings.TimelineId);
        return custom.State switch
        {
            KeyboardDisplayAnimationStates.Preparing => "正在后台准备画面",
            KeyboardDisplayAnimationStates.Unavailable => local ? "所选本机素材不在素材库中" : "所选素材不在当前场景的共享时间轴上",
            KeyboardDisplayAnimationStates.Error => "画面准备失败",
            KeyboardDisplayAnimationStates.Fallback => local ? "共享主时钟不可用" : "场景包共享时间轴不可用",
            KeyboardDisplayAnimationStates.BreakerTripped => "动画上传超出预算已停止",
            KeyboardDisplayAnimationStates.Ready => "已准备，等待更新间隔",
            _ => "当前不可播放",
        } + "，状态 " + custom.State;
    }

    /// <summary>
    /// A valid physical lever action admitted by the native-menu owner (a centre press accepted, or a browse step queued). Under a
    /// declared native presentation it opens the transient status/scene-name popup; <paramref name="awaitsCompletion"/> keeps it
    /// open until <see cref="NativeInteractionCompleted"/>, then a brief hold. Notification only: no I/O, never blocks; background
    /// refreshes, ARM/CAP, RGB frames and ordinary rendering never call it. Without a running declared presentation it is ignored.
    /// </summary>
    internal void NativeInteractionBegan(bool awaitsCompletion) => Volatile.Read(ref _presentation)?.InteractionBegan(awaitsCompletion);

    /// <summary>The pending valid action's outcome (and its readback artwork, if any) arrived; the popup's brief hold starts now.</summary>
    internal void NativeInteractionCompleted() => Volatile.Read(ref _presentation)?.InteractionCompleted();

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
    {
        ct.ThrowIfCancellationRequested(); var status = Volatile.Read(ref _status);
        var retries = Interlocked.Read(ref _nativeImageRetries);
        return Task.FromResult(retries == 0 ? status : status with { Detail = status.Detail + $"；累计发生 {retries} 次画面重试" });
    }

    public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (enabled) RequirePins();
            Save(_settingsPath, new LocalSettings(1, enabled));
            _enabled = enabled;
            Interlocked.Increment(ref _intentRevision);
            Interlocked.Exchange(ref _observationReplacements, 0);
            Volatile.Write(ref _coldStartRetries, 0);
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
        var native = _nativeMenu?.Requested == true && _nativeMenu.QualifiedRoutes.Count != 0;
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
                IRogOledConnection? acquiredConnection = null;
                try
                {
                    if (_configurationGate?.RogAcquisitionAllowed == false && _configurationGate.FreshResumeAllowed == false)
                    {
                        if (Volatile.Read(ref _status).LastError is null) Publish("Stopped", "ROG interfaces are released for peripheral configuration");
                        await Task.Delay(500, sessionStop.Token).ConfigureAwait(false); continue;
                    }
                    if (faulted && RetiredRouteChangeProven())
                    {
                        faulted = false;
                        Publish("Connecting", "已确认旧接口移除或新的有线接口就绪；保留恢复记录，正在接入键盘当前连接方式");
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
                    acquiredConnection = connection;
                    if (connection is null)
                    {
                        sessionDone.TrySetResult();
                        CompleteResume(new KeyboardResumeResult(false, null));
                        var requested = _enabled || _options.KeyboardDisplay.HasVerifiedLightingRoute || _nativeMenu?.Requested == true;
                        Publish(requested ? "Waiting" : "Stopped", requested ? "等待已绑定键盘的 USB 或 2.4G 无线连接；设备上线后自动接管" : "键盘状态屏已关闭");
                        await Task.Delay(5000, sessionStop.Token).ConfigureAwait(false); continue;
                    }
                    if (ReferenceEquals(Interlocked.Exchange(ref _routeRecoveryOnly, null), connection))
                    {
                        var expectedRecovery = ReadBytes(_journalPath);
                        var peerUnavailable = false;
                        using (connection)
                        {
                            try { await RunSessionAsync(connection, sessionStop.Token, recoveryOnly: true, expectedJournal: expectedRecovery).ConfigureAwait(false); }
                            catch (RogKeyboardNotReadyException)
                            {
                                // This typed snapshot outcome is raised before settings/output. Receiver presence alone
                                // does not mean its keyboard is still on RF after a physical switch to USB.
                                peerUnavailable = true;
                            }
                        }
                        if (peerUnavailable)
                        {
                            if (!ReadBytes(_journalPath).AsSpan().SequenceEqual(expectedRecovery)) throw MissingRecoveryScope();
                            var prior = ReadRecoveryForRoute(_journalPath, deferred: false);
                            if (prior.Schema != 2 || !SameRecoveryRoute(prior, connection)) throw MissingRecoveryScope();
                            TransferRecovery(_journalPath, DeferredPath(RecoveryKind(prior)));
                        }
                        continue; // restore-only owner is disposed before the selected current route is reacquired
                    }
                    await RunOwnerWithObservationRecoveryAsync(connection, sessionStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                catch (KeyboardTransportChangedException)
                {
                    Publish("Connecting", "已检测到绑定键盘的 USB 连接；旧无线会话已关闭，正在接入有线并重新协商原生页面");
                }
                catch (KeyboardRecoveryIntentChangedException)
                {
                    Publish("Connecting", "键盘使用请求已更新，正在按当前请求重新核对连接");
                }
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
                    RememberRetiredRouteFault(acquiredConnection);
                    CompleteResume(new KeyboardResumeResult(true, ex));
                    // Recovery was already bounded by RunSessionAsync. An ambiguous ACK must not start an upload retry loop.
                    faulted = true;
                    Publish("Error", "键盘响应超时；检查连接后关闭再开启，未自动重发画面", error: ex.GetType().Name + ": " + ex.Message);
                }
                catch (RogKeyboardNotReadyException) when (Volatile.Read(ref _coldStartRetries) < ColdStartRetries)
                {
                    // Raised only before any write. That owner is already disposed; the next turn acquires a fresh one through the
                    // same factory and pairing query. A pending Resume stays pending until a startup succeeds or attempts run out.
                    var attempt = Interlocked.Increment(ref _coldStartRetries);
                    Publish("Connecting", $"2.4G 接收器已应答但键盘尚未就绪；未写入任何设置，正在重新连接（{attempt}/{ColdStartRetries}）");
                    try { await Task.Delay(500, sessionStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
                catch (RogKeyboardNotReadyException) when (_configurationGate is { RogAcquisitionAllowed: true, FreshResumeAllowed: false })
                {
                    // This typed startup outcome was raised before settings/output. Normal acquisition may wait for a wake;
                    // an explicit maintenance FreshResume keeps its existing bounded failure behavior instead.
                    Publish("Waiting", "2.4G 接收器已应答但键盘尚未就绪；未写入设置，等待唤醒并每 5 秒重新读取");
                    try { await Task.Delay(5000, sessionStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
                catch (RogKeyboardStartupNotReadyException) when (_configurationGate is { RogAcquisitionAllowed: true, FreshResumeAllowed: false })
                {
                    Publish("Waiting", "键盘只读初始化尚未就绪；保留当前设置，每 5 秒等待自动接管");
                    try { await Task.Delay(5000, sessionStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
                catch (KeyboardPresentationNotReadyException) when (_configurationGate is { RogAcquisitionAllowed: true, FreshResumeAllowed: false })
                {
                    Publish("Waiting", "键盘恢复所需的只读状态尚未就绪；保留恢复记录，每 5 秒重新核对设备");
                    try { await Task.Delay(5000, sessionStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RememberRetiredRouteFault(acquiredConnection);
                    CompleteResume(new KeyboardResumeResult(true, ex));
                    faulted = true; Publish("Error", ex is RecoveryScopeException scope ? scope.UserDetail
                        : ex is RogKeyboardNotReadyException ? "2.4G 键盘多次未就绪；未写入任何设置，唤醒键盘后关闭再开启"
                        : "键盘状态屏接入失败；检查连接后关闭再开启", error: ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    // Acquisition can fail before RunOwner enters its using scope; every captured handle must still close.
                    try { acquiredConnection?.Dispose(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _retiredRouteFault = null;
                        Publish("Error", "旧键盘会话关闭未完成；保留恢复记录，停止自动接管", error: ex.GetType().Name);
                    }
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
            ? Task.FromResult(SelectConnection())
            : _configurationGate.RunAcquisitionAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(_configurationGate.RogAcquisitionAllowed || _configurationGate.FreshResumeAllowed
                    ? SelectConnection() : null);
            }, ct);
    }

    // This is not a packet retry: each failed generation has fully unwound and been disposed before the next open.
    private async Task RunOwnerWithObservationRecoveryAsync(IRogOledConnection first, CancellationToken ct)
    {
        var connection = first;
        byte[]? expectedJournal = null;
        ObservationOwnerRetiredException? pendingObservation = null;
        var retryingInterruptedRestore = false;
        while (true)
        {
            ObservationOwnerRetiredException retired;
            var verifyingRecovery = expectedJournal is not null;
            var waitingForWake = false;
            var newlyRetired = false;
            var brightnessWasNotSent = false;
            using (connection)
            {
                try
                {
                    await RunSessionAsync(connection, ct, expectedJournal: expectedJournal,
                        requirePresentationReadiness: (pendingObservation?.ArtworkRefused == true || retryingInterruptedRestore)
                            && _nativeMenu?.Requested == true,
                        recoveryVerified: () =>
                        {
                            verifyingRecovery = false;
                            retryingInterruptedRestore = false;
                            if (pendingObservation is not null)
                                Serilog.Log.Information("Keyboard recovery owner ready: transport={Transport}, phase={Phase}, profile={Profile}",
                                    pendingObservation.ConnectionKind, pendingObservation.Phase, pendingObservation.ProfileToken);
                        }).ConfigureAwait(false); return;
                }
                catch (ObservationOwnerRetiredException ex)
                { retired = ex; pendingObservation = ex; waitingForWake = ex.PeerUnavailable; newlyRetired = true; }
                catch (RogKeyboardNotReadyException) when (verifyingRecovery && pendingObservation is not null)
                {
                    // Snapshot proved an empty RF peer before ANY settings. Preserve the same recovery record/intent and
                    // wait for a new read-only owner; this is not another observation replacement attempt.
                    retired = pendingObservation;
                    waitingForWake = true;
                }
                catch (RogKeyboardStartupNotReadyException) when (verifyingRecovery && pendingObservation is not null)
                {
                    retired = pendingObservation;
                    waitingForWake = true;
                }
                catch (KeyboardPresentationNotReadyException) when (verifyingRecovery && pendingObservation is not null)
                {
                    retired = pendingObservation;
                    waitingForWake = true;
                }
                catch (RogOledEnvelopeRefusedException ex) when (ex.UnsentCommand == 0x68 && verifyingRecovery
                    && pendingObservation is not null && connection.ConnectionKind == "wireless"
                    && ex.Data["KeyboardStartupStage"] is "restore-native-goal" && !ct.IsCancellationRequested)
                {
                    // The same-selector preset completed. The proposed brightness write never reached the wire.
                    // Retry the recorded restore only on a fresh owner, within the original episode budget.
                    retired = pendingObservation;
                    brightnessWasNotSent = true;
                    retryingInterruptedRestore = true;
                }
                catch (Exception ex) when (verifyingRecovery && ex is not OutOfMemoryException and not KeyboardRecoveryIntentChangedException
                    && !(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    var diagnostic = RecoveryFailureDetail(ex);
                    Serilog.Log.Warning("Keyboard replacement startup/recovery failed: {Diagnostic}", diagnostic);
                    throw new IOException("Recorded replacement startup/recovery failed; no further automatic recovery; journal retained. " + diagnostic, ex);
                }
            }
            ct.ThrowIfCancellationRequested();
            RequireObservationReplacement(retired, "after-dispose");
            if (newlyRetired)
                Serilog.Log.Information("Keyboard temporary presentation owner retired: transport={Transport}, phase={Phase}, profile={Profile}",
                    retired.ConnectionKind, retired.Phase, retired.ProfileToken);
            if (brightnessWasNotSent)
            {
                if (Volatile.Read(ref _observationReplacements) >= 3)
                    throw ObservationDenied(retired, "budget-exhausted", "before-native-restore-replacement");
                var attempt = Interlocked.Increment(ref _observationReplacements);
                Serilog.Log.Information("Keyboard recovery brightness was not sent; retired RF owner, attempt {Attempt}/3 after delay", attempt);
                Publish("Waiting", $"恢复亮度前收到无线拒绝；亮度指令未发送，5 秒后重新核对原设备和恢复记录（{attempt}/3）");
                await Task.Delay(5000, ct).ConfigureAwait(false);
            }
            else if (waitingForWake)
            {
                Publish("Waiting", "原设备恢复连接已应答，但键盘尚未就绪；未写入设置，保留同一恢复记录并每 5 秒等待唤醒",
                    error: null);
                await Task.Delay(5000, ct).ConfigureAwait(false);
            }
            else
            {
                if (retired.HealthyFor >= TimeSpan.FromSeconds(60)) Interlocked.Exchange(ref _observationReplacements, 0);
                if (Volatile.Read(ref _observationReplacements) >= 3) throw ObservationDenied(retired, "budget-exhausted", "before-replacement");
                var attempt = Interlocked.Increment(ref _observationReplacements);
                Publish("Recovering", retired.ArtworkRefused
                    ? $"键盘临时画面会话已退出；旧连接已关闭，按原设备恢复后重新连接（{attempt}/3），未重发旧图块"
                    : $"键盘独立只读{(retired.InnerException is RogReadOnlyObservationEnvelopeRefusedException ? "查询收到拒绝应答" : retired.InnerException is RogReadOnlyObservationUnexpectedReplyException ? "应答不匹配" : "检查超时")}；旧连接已关闭，按原设备恢复后重新连接（{attempt}/3），未重发旧报文",
                    error: retired.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            RequireObservationReplacement(retired, "before-open");
            expectedJournal = retired.Journal;
            while (true)
            {
                try
                {
                    connection = await _configurationGate!.RunAcquisitionAsync(token =>
                    {
                        token.ThrowIfCancellationRequested();
                        RequireObservationReplacement(retired, "inside-acquisition");
                        if (!File.Exists(_journalPath) || !ReadBytes(_journalPath).AsSpan().SequenceEqual(retired.Journal))
                            throw new RecoveryScopeException("Recorded recovery changed or disappeared; no automatic replacement writes.", "恢复记录已改变或缺失；已停止自动重新连接");
                        var record = JsonSerializer.Deserialize<RecoveryRecord>(retired.Journal) ?? throw MissingRecoveryScope();
                        if (RecoveryKind(record) == "wireless" && WiredSwitchKey is { } wiredKey && CanSwitchToKnownWired(wiredKey))
                            throw new KeyboardTransportChangedException();
                        return Task.FromResult(OpenRecordedConnection(record)
                            ?? throw new IOException("The exact recorded keyboard route is absent; recovery record retained."));
                    }, ct).ConfigureAwait(false);
                    break;
                }
                catch (RogKeyboardNotReadyException)
                {
                    RequireObservationReplacement(retired, "sleeping-peer-before-open");
                    Publish("Waiting", "接收器已应答，键盘处于休眠或离线；保留原设置，每 5 秒等待自动接管");
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
                catch (RogOledConnectionLostException ex)
                { throw new IOException("Recorded replacement acquisition failed; no startup retry or route substitution; journal retained.", ex); }
            }
            var afterOpenDenial = ObservationReplacementDenial(retired);
            if (ct.IsCancellationRequested || afterOpenDenial is not null)
            {
                connection.Dispose(); // an intent/gate change during the open must not reach even startup I/O
                ct.ThrowIfCancellationRequested();
                throw ObservationDenied(retired, afterOpenDenial!, "after-open");
            }
        }
    }

    private string? ObservationReplacementDenial(ObservationOwnerRetiredException retired)
    {
        if (_configurationGate is null) return "normal-gate-missing";
        if (!_configurationGate.RogAcquisitionAllowed) return "acquisition-blocked";
        if (_configurationGate.FreshResumeAllowed) return "maintenance-fresh-resume";
        if (retired.IntentRevision != Interlocked.Read(ref _intentRevision)) return "newer-display-intent";
        if (retired.NativeRevision != (_nativeMenu?.RequestRevision ?? 0)) return "newer-native-intent";
        if (!_enabled && !_options.KeyboardDisplay.HasVerifiedLightingRoute && _nativeMenu?.Requested != true) return "no-current-consumer";
        try { return _guard.Check(DeviceClass.AsusRog).State == GuardState.Clear ? null : "writer-guard-not-clear"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "writer-guard-failed:" + ex.GetType().Name; }
    }

    private void RequireObservationReplacement(ObservationOwnerRetiredException retired, string phase)
    {
        if (ObservationReplacementDenial(retired) is { } reason) throw ObservationDenied(retired, reason, phase);
    }

    // Fault-only diagnostics: distinguish guard/intent denial from an exhausted episode, without exposing packet bodies.
    private IOException ObservationDenied(ObservationOwnerRetiredException retired, string reason, string phase) => new(
        $"replacementDenied={reason}; phase={phase}; attempts={Volatile.Read(ref _observationReplacements)}/3; "
        + $"failedSessionHealthyMs={retired.HealthyFor.TotalMilliseconds:0}; faultIntent={retired.IntentRevision}; currentIntent={Interlocked.Read(ref _intentRevision)}; "
        + $"faultNative={retired.NativeRevision}; currentNative={_nativeMenu?.RequestRevision ?? 0}; "
        + $"normalGate={_configurationGate?.RogAcquisitionAllowed}; freshResume={_configurationGate?.FreshResumeAllowed}; primary={retired.Message}", retired);

    private sealed class ObservationOwnerRetiredException(IOException cause, byte[] journal,
        TimeSpan healthyFor, long intentRevision, long nativeRevision, string connectionKind, byte profileToken) : IOException(cause.Message, cause)
    {
        public bool ArtworkRefused { get; } = cause is RogNativeArtworkUploadRefusedException or RogNativePresentationUnavailableException;
        public bool PeerUnavailable { get; } = cause is RogReadOnlyObservationPeerUnavailableException;
        public string ConnectionKind { get; } = connectionKind;
        public byte ProfileToken { get; } = profileToken;
        public string Phase { get; } = cause switch
        {
            RogNativeArtworkUploadRefusedException artwork => artwork.Phase,
            RogNativePresentationUnavailableException presentation => presentation.Phase,
            RogReadOnlyObservationException observation => observation.Operation.ToString(),
            _ => "unknown",
        };
        public byte[] Journal { get; } = journal;
        public TimeSpan HealthyFor { get; } = healthyFor;
        public long IntentRevision { get; } = intentRevision;
        public long NativeRevision { get; } = nativeRevision;
    }

    /// <summary>The existing OLED owner selects one exact configured transport; native control borrows that same connection.</summary>
    internal IRogOledConnection? SelectConnection()
    {
        RequireNativeOwnerDetached();
        ValidateDeferredRecovery();
        if (File.Exists(_journalPath))
        {
            var prior = ReadRecoveryForRoute(_journalPath, deferred: false);
            var selected = SelectFreshConnection();
            if (selected is null) return null;
            if (SameRecoveryRoute(prior, selected)) return selected;
            if (RecoveryKind(prior) == "wireless" && IsKnownAvailableWiredRoute(selected))
            {
                // This is a fresh-owner boundary: the previous RF actor/input/lighting have already joined. The exact
                // USB keyboard is now present, while a still-present receiver is not proof its keyboard remains on RF.
                // Preserve that RF obligation without opening/querying it; USB restores only its own scoped record.
                try
                {
                    if (prior.Schema != 2) throw MissingRecoveryScope();
                    TransferRecovery(_journalPath, DeferredPath("wireless"));
                    ActivateDeferredRecovery(selected);
                    return selected;
                }
                catch { selected.Dispose(); throw; }
            }
            selected.Dispose();
            // A speculative current-route handle is closed before opening the restoration owner. Never borrow both.
            var recorded = OpenRecordedConnection(prior);
            if (recorded is not null)
            {
                Volatile.Write(ref _routeRecoveryOnly, recorded);
                return recorded;
            }
            // Only a factory's positive absence result permits deferral. Failed opens/replies never reach this branch.
            if (prior.Schema != 2) throw MissingRecoveryScope();
            TransferRecovery(_journalPath, DeferredPath(RecoveryKind(prior)));
            selected = SelectFreshConnection();
            if (selected is null) return null;
            try
            {
                ActivateDeferredRecovery(selected);
                return selected;
            }
            catch { selected.Dispose(); throw; }
        }
        var fresh = SelectFreshConnection();
        if (fresh is null) return null;
        try { ActivateDeferredRecovery(fresh); return fresh; }
        catch { fresh.Dispose(); throw; }
    }

    private IRogOledConnection? SelectFreshConnection()
    {
        if (_nativeMenu?.Requested == true)
        {
            foreach (var native in _nativeMenu.QualifiedRoutes)
            {
                // Only an absent exact route permits the next known transport. Exceptions and live CAP rejection do not.
                // The configured native header is diagnostic; every open uses its neutral transport label.
                IRogOledConnection? selected = native.ConnectionKind switch
                {
                    "wired" => _factory.TryOpen(native.DeviceKey, native.RouteAnchor),
                    "wireless" => _wirelessFactory?.TryOpen(native.DeviceKey, native.RouteAnchor),
                    _ => null,
                };
                if (selected is not null) return selected;
            }
            return null;
        }
        IRogOledConnection? connection = HasWiredPins(_options.KeyboardDisplay)
            && (_enabled || _options.KeyboardDisplay.HasVerifiedWiredLighting) ? _factory.TryOpen(_options.KeyboardDisplay.DeviceKey, WiredLabel) : null;
        if (connection is null && _wirelessFactory is not null && HasWirelessPins(_options.KeyboardDisplay)
            && (_enabled || _options.KeyboardDisplay.HasVerifiedWirelessLighting))
            connection = _wirelessFactory.TryOpen(_options.KeyboardDisplay.WirelessReceiverKey, WirelessLabel);
        return connection;
    }

    public async Task ReleaseForPeripheralConfigurationAsync(CancellationToken ct)
    {
        CancellationTokenSource? stop;
        Task? active;
        lock (_gate)
        {
            Interlocked.Increment(ref _intentRevision);
            stop = _sessionStop;
            active = _activeSession;
        }
        stop?.Cancel();
        if (active is not null) await active.WaitAsync(ct).ConfigureAwait(false);
        ValidateDeferredRecovery();
        if (active is not null && File.Exists(_journalPath))
            throw new IOException("Keyboard original settings remain unrecovered; journal retained and no restoration retry was made.");
        // Deferred transport records are still recovery obligations. Maintenance cannot claim Ready or erase them
        // merely because another route was connected more recently.
        foreach (var path in KeyboardRecoveryJournals.Paths(_journalPath))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var prior = ReadRecoveryForRoute(path, deferred: path != _journalPath);
            using var connection = OpenRecordedConnection(prior)
                ?? throw new IOException($"The recorded {RecoveryKind(prior)} keyboard transport is absent; recovery is deferred and its exact journal is retained.");
            if (path != _journalPath) TransferRecovery(path, _journalPath);
            await RunSessionAsync(connection, ct, recoveryOnly: true).ConfigureAwait(false);
            if (File.Exists(_journalPath)) throw new IOException("Recorded-transport keyboard recovery was not verified; journal retained.");
        }
    }

    private IRogOledConnection? OpenRecordedConnection(RecoveryRecord prior)
    {
        RequireNativeOwnerDetached();
        IRogOledConnection? connection;
        var native = _nativeMenu?.QualifiedRoutes.FirstOrDefault(route => prior.DeviceKey == route.DeviceKey
            && SameProtocol(prior.FirmwareHeader, route.RouteAnchor));
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
        return connection;
    }

    private void RequireNativeOwnerDetached()
    {
        if (_nativeMenu?.OwnerDetached == false)
            throw new IOException("Previous native keyboard owner has not detached; no control factory or receiver pairing query may run.");
    }

    public async Task ResumeAfterPeripheralConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_enabled && !_options.KeyboardDisplay.HasVerifiedLightingRoute && _nativeMenu?.Requested != true) return;
        Task<KeyboardResumeResult> completion;
        lock (_gate)
        {
            Interlocked.Increment(ref _intentRevision);
            Interlocked.Exchange(ref _observationReplacements, 0);
            var fresh = new TaskCompletionSource<KeyboardResumeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeCompletion = fresh;
            completion = fresh.Task;
            Volatile.Write(ref _coldStartRetries, 0);
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

    private async Task RunSessionAsync(IRogOledConnection connection, CancellationToken ct, bool recoveryOnly = false,
        byte[]? expectedJournal = null, Action? recoveryVerified = null, bool requirePresentationReadiness = false)
    {
        var routeSwitch = connection.ConnectionKind == "wireless" && WiredSwitchKey is { } wiredKey
            ? new KeyboardTransportSwitch(_time, () => CanSwitchToKnownWired(wiredKey)) : null;
        long? lastWiredRouteCheck = null;
        void CheckRoute()
        {
            routeSwitch?.Check();
            if (connection.ConnectionKind != "wired") return;
            var now = _time.GetTimestamp();
            if (lastWiredRouteCheck is { } prior && _time.GetElapsedTime(prior, now) < TimeSpan.FromMilliseconds(100)) return;
            lastWiredRouteCheck = now;
            if (connection.SameDevice()) return;
            if (connection.PhysicalRouteAbsent)
                throw new RogOledConnectionLostException("The original USB route is confirmed absent; retire its actor before selecting RF.",
                    new IOException("The exact original HID path is no longer enumerated."));
            throw new InvalidDataException("USB physical identity or metadata changed without positive absence proof; no automatic reacquisition.");
        }
        Action checkRoute = CheckRoute;
        var session = new RogWiredOledSession(connection) { BeforeOperation = checkRoute };
        await using var nativeMenu = _nativeMenu?.Attach(connection);
        if (nativeMenu is not null) nativeMenu.BeforeOperation = checkRoute;
        if (nativeMenu is not null) nativeMenu.PageObserved = _codexEntry.Observe;
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
        var observationRetired = false;
        var nativeRecoveryAdopted = false;
        var nativeGoalRestored = false;
        long? runningAt = null;
        long? failedAt = null;
        long intentRevision = Interlocked.Read(ref _intentRevision), nativeRevision = _nativeMenu?.RequestRevision ?? 0;
        var nativeRequestedAtStart = _nativeMenu?.Requested == true;
        var startupStage = "snapshot";

        async Task RestoreNativeAtStartupAsync(byte brightness, bool allowFreshNative)
        {
            startupStage = "restore-native-goal";
            try { await RestoreNativeGoalAsync(session, connection, brightness, allowFreshNative, ct).ConfigureAwait(false); }
            catch (Exception ex) when (nativeRequestedAtStart && !session.MutationAdmitted && ReadOnlyRecoveryUnavailable(ex))
            {
                // RestoreAsync first reads the current device/profile. That read can fail after a successful
                // snapshot on a route that has just switched. Retire it like the other startup reads.
                ct.ThrowIfCancellationRequested();
                if (intentRevision != Interlocked.Read(ref _intentRevision) || nativeRevision != (_nativeMenu?.RequestRevision ?? 0))
                    throw new KeyboardRecoveryIntentChangedException();
                throw new KeyboardPresentationNotReadyException(ex);
            }
        }

        try
        {
            Publish("Connecting", "正在核对键盘与原显示设置");
            byte brightness;
            try { brightness = await session.SnapshotAsync(connection.FirmwareHeader, ct).ConfigureAwait(false); }
            catch (Exception ex) when (nativeRequestedAtStart
                && ex is RogKeyboardStartupNotReadyException or RogKeyboardNotReadyException)
            {
                ct.ThrowIfCancellationRequested();
                if (intentRevision != Interlocked.Read(ref _intentRevision) || nativeRevision != (_nativeMenu?.RequestRevision ?? 0))
                    throw new KeyboardRecoveryIntentChangedException();
                throw;
            }
            catch (Exception ex) when (nativeRequestedAtStart && !session.MutationAdmitted && ReadOnlyRecoveryUnavailable(ex))
            {
                ct.ThrowIfCancellationRequested();
                if (intentRevision != Interlocked.Read(ref _intentRevision) || nativeRevision != (_nativeMenu?.RequestRevision ?? 0))
                    throw new KeyboardRecoveryIntentChangedException();
                throw new KeyboardPresentationNotReadyException(ex);
            }
            startupStage = "recovery-record";
            if (expectedJournal is not null && !File.Exists(_journalPath))
                throw new RecoveryScopeException("Expected recovery record disappeared.", "恢复记录已缺失；未写入恢复设置");
            if (File.Exists(_journalPath))
            {
                var priorBytes = ReadBytes(_journalPath);
                if (expectedJournal is not null && !priorBytes.AsSpan().SequenceEqual(expectedJournal))
                    throw new RecoveryScopeException("Expected recovery record changed.", "恢复记录已改变；未写入恢复设置");
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
                    if (!recoveryOnly && !display)
                    {
                        startupStage = "reconcile-native-recovery";
                        nativeRecoveryAdopted = await TryResumeNativeRecoveryAsync(session, connection, prior, priorBytes, ownsLighting, ct).ConfigureAwait(false);
                    }
                    if (!nativeRecoveryAdopted)
                    {
                        if (requirePresentationReadiness)
                        {
                            startupStage = "snapshot-native-readiness";
                            await RequireFreshPresentationReadinessAsync(session, connection, ct).ConfigureAwait(false);
                        }
                        await RestoreNativeAtStartupAsync(brightness, allowFreshNative: false).ConfigureAwait(false);
                        nativeGoalRestored = true;
                        if (prior.Lighting is not null && prior.StaticBank is not null)
                        {
                            startupStage = "restore-lighting";
                            await session.RestoreLightingAsync(prior.Lighting, prior.StaticBank, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            original = brightness;
            startupStage = "snapshot-lighting";
            if (ownsLighting && !nativeRecoveryAdopted)
            {
                try { await session.SnapshotLightingAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (nativeRequestedAtStart && !session.MutationAdmitted && ReadOnlyRecoveryUnavailable(ex))
                {
                    ct.ThrowIfCancellationRequested();
                    if (intentRevision != Interlocked.Read(ref _intentRevision) || nativeRevision != (_nativeMenu?.RequestRevision ?? 0))
                        throw new KeyboardRecoveryIntentChangedException();
                    throw new KeyboardPresentationNotReadyException(ex);
                }
            }
            if (recoveryOnly)
            {
                recoveryComplete = true;
                return;
            }
            // Persist recovery intent before the first mutation, including process termination/restart.
            if (!nativeRecoveryAdopted)
                Save(_journalPath, new RecoveryRecord(2, connection.DeviceKey, ConnectionLabel(connection), brightness, session.VerifiedProfileToken,
                    session.OriginalLighting, session.OriginalStaticBank));
            startupStage = "start-output";
            await RefreshArtworkAsync(ct).ConfigureAwait(false); // No raw image is presented as a completed device write.
            if (display) await session.StartOutputAsync(_options.KeyboardDisplay.Brightness, ct).ConfigureAwait(false);
            else
            {
                if (!nativeRecoveryAdopted && !nativeGoalRestored)
                    await RestoreNativeAtStartupAsync(brightness, allowFreshNative: true).ConfigureAwait(false);
                Publish("Stopped", InactiveDisplayDetail);
            }
            Volatile.Write(ref _coldStartRetries, 0);
            CompleteResume(new KeyboardResumeResult(true, null));
            recoveryVerified?.Invoke(); // only the recorded restoration/startup has completed; this never resets the episode budget
            if (ownsLighting) lighting = _lighting.Attach();
            monitor = MonitorAsync();
            var presentation = nativeMenu is not null && NativePresentationDeclared(connection)
                ? new KeyboardNativePresentation(_animation, _options.KeyboardDisplay.DeviceShowsNativeEntryLogo ? null : EntryLogo(), _time,
                    _nativeFrameOverlay, _nativeFrameAcknowledged, _nativeFrameOverlayRevision, () => CaseBlackout, _idleFrame, _codexEntry, _homeAssistantFrame) : null;
            // Ordinary application output has its own entry too. It must not upload a status/animation frame before a later
            // native entry gets its logo. Reuse only the pure selector and the existing owner's upload acknowledgement/lease.
            var ordinaryPresentation = !NativeOwnsDisplay ? new KeyboardNativePresentation(_animation, EntryLogo(), _time, idleFrame: _idleFrame) : null;
            Volatile.Write(ref _presentation, presentation);
            if (presentation is not null && !display) Publish("Stopped", InactiveDisplayDetail); // now says what an entry presents
            // The animation selector is the display's only cadence gate. Without declared native presentation the native ticks keep
            // reading the status pixels directly; with it, a native entry asks the presentation, and the legacy selector then
            // leaves the animation's held frame alone. Installed ownership keeps display false, so the legacy path never uploads
            // and the presentation's logo is the first image of every native entry (ARM, then logo, then the chosen idle).
            runningAt = _time.GetTimestamp();
            startupStage = "running";
            await session.RunAsync(() => ordinaryPresentation is not null && LegacyDisplayEnabled
                    ? ordinaryPresentation.Latest(Volatile.Read(ref _pixels), pageEnabled: true)
                    : presentation?.Entered == true ? Volatile.Read(ref _pixels)
                    : _animation.Select(Volatile.Read(ref _pixels), _enabled, NativeOwnsDisplay),
                () =>
                {
                    var animated = ordinaryPresentation?.Acknowledge() ?? _animation.Uploaded();
                    var artwork = ordinaryPresentation?.Shown == KeyboardNativeArtwork.Logo ? "光域标志"
                        : ordinaryPresentation?.Shown == KeyboardNativeArtwork.CodexQuota ? "Codex 额度" : animated ? "动画" : "状态";
                    Publish("Running", $"键盘{artwork}画面已获{(connection.ConnectionKind == "wireless" ? "无线" : "有线")}通信确认；拨杆未启用", session);
                }, ct, lighting, display,
                () => LegacyDisplayEnabled, _options.KeyboardDisplay.Brightness,
                on =>
                {
                    if (!on) { ordinaryPresentation?.Stepped(false); Publish("Stopped", InactiveDisplayDetail, session); }
                },
                // The actor performs the one wire restore after this goal-only callback; do not restore here too.
                restoreNativeGoal: token => Task.FromResult(ResolveNativeGoal(session, connection, brightness, allowFreshNative: false, token)),
                nativeBrightnessChanged: (current, token) => PersistObservedNativeAsync(connection, session.VerifiedProfileToken, current, token),
                nativeTick: nativeMenu is null ? null : presentation is null
                    ? token => nativeMenu.StepAsync(capability => !_options.KeyboardDisplay.HostArtworkEnabled ? null : capability.Mode switch
                    {
                        RogNativeMenuCodec.CodexMode => _codexEntry.Holding ? null : _idleFrame?.Invoke(),
                        RogNativeMenuCodec.HomeAssistantMode => _codexEntry.Holding ? null : _homeAssistantFrame?.Invoke(),
                        _ => Volatile.Read(ref _pixels),
                    }, token)
                    : async token =>
                    {
                        // Control and Host artwork are separate: a null answer keeps the lever armed while the keyboard shows its
                        // own stored animation; the device-idle re-ARM gap continues the same entry (no second logo). The return
                        // stays artwork-active, so the session's existing artwork-to-idle restore runs when artwork ends.
                        bool artwork;
                        try
                        {
                            artwork = await nativeMenu.StepAsync(capability => _options.KeyboardDisplay.HostArtworkEnabled
                                ? presentation.NativePage(capability, Volatile.Read(ref _pixels),
                                    newEntry: !nativeMenu.Armed && !nativeMenu.RearmingForDeviceIdle) : null, token).ConfigureAwait(false);
                        }
                        catch (RogNativeImageContinuationException)
                        {
                            Interlocked.Increment(ref _nativeImageRetries);
                            _animation.EndSession(); // discard the failed held selection; next artwork samples current shared time
                            throw; // keep the presentation/popup and acknowledge nothing; the session still serves RGB
                        }
                        presentation.SteppedNative(nativeMenu.Armed || nativeMenu.RearmingForDeviceIdle, artwork);
                        if (presentation.TakeChanged() && Volatile.Read(ref _status) is { LastError: null } current)
                            Publish(current.State, InactiveDisplayDetail, session);
                        return artwork;
                    },
                nativeWake: () => { checkRoute?.Invoke(); return nativeMenu?.HasChanges == true; },
                // RunSession's finally is the sole native teardown owner: it preserves a typed read fault and can retire
                // without touching the poisoned wire. A nested Stop here would replace that primary failure first.
                nativeStopped: null,
                perKeyPacing: _options.AsusRog.KeyboardPerKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ex.Data["KeyboardStartupStage"] = startupStage;
            // A different exact configured route is a new acquisition intent, not a retry of this RF command.
            // Even if USB enumeration followed an in-flight RF failure, no old-route cleanup or packet is replayed.
            failure = !ct.IsCancellationRequested && routeSwitch?.Observe(force: true) == true
                ? ex as KeyboardTransportChangedException ?? new KeyboardTransportChangedException(ex) : ex;
            if (!ct.IsCancellationRequested && failure is not KeyboardTransportChangedException && connection.PhysicalRouteAbsent)
                failure = ex as RogOledConnectionLostException ?? new RogOledConnectionLostException(
                    "The original keyboard HID route is confirmed absent; retain its recovery record and acquire a fresh configured route.", ex);
            failedAt = _time.GetTimestamp();
            if (ex is RogReadOnlyObservationException or RogNativeArtworkUploadRefusedException or RogNativePresentationUnavailableException)
            {
                // Healthy same-session changes are legitimate. Freeze the LATEST intent at the classified fault; any
                // changes after this point still invalidate replacement before/inside/after the fresh open.
                intentRevision = Interlocked.Read(ref _intentRevision);
                nativeRevision = _nativeMenu?.RequestRevision ?? 0;
            }
        }
        finally
        {
            // Eligibility is captured BEFORE stopping native input; never reinterpret a native/physical/join fault as a poll failure.
            var classifiedObservation = failure is RogReadOnlyObservationException or RogNativeArtworkUploadRefusedException or RogNativePresentationUnavailableException;
            var transportChanged = failure is KeyboardTransportChangedException;
            var samePhysical = false;
            if (classifiedObservation)
            {
                try { samePhysical = connection.SameDevice(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { samePhysical = false; }
            }
            var observation = classifiedObservation && samePhysical && nativeMenu?.RecoveryBlocked != true;
            Volatile.Write(ref _presentation, null);
            _animation.EndSession();
            if (nativeMenu is not null)
            {
                try
                {
                    if (observation || transportChanged) await nativeMenu.RetireAfterRecoverableFaultAsync(CancellationToken.None).ConfigureAwait(false);
                    else await nativeMenu.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failure = failure is null
                        ? new AggregateException("Owner retirement failed; automatic replacement refused.", ex)
                        : new AggregateException("Owner retirement failed; automatic replacement refused.", failure, ex);
                }
            }
            observationRetired = observation && failure is RogReadOnlyObservationException or RogNativeArtworkUploadRefusedException or RogNativePresentationUnavailableException
                && nativeMenu?.RecoveryBlocked != true;
            monitorStop.Cancel();
            if (monitor is not null) await monitor.ConfigureAwait(false);
            if (lighting is not null) _lighting.Detach(lighting);
            var errors = new List<Exception>();
            confirmedRemoval = failure is RogOledConnectionLostException && connection.PhysicalRouteAbsent;
            // Native faults suppress unsafe cleanup; they do not prove removal or authorize an automatic new owner.
            var recoveryBlocked = transportChanged || confirmedRemoval || nativeMenu?.RecoveryBlocked == true || classifiedObservation;
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
            else if (original.HasValue && !recoveryBlocked
                && (failure is null || failure is OperationCanceledException && ct.IsCancellationRequested)) File.Delete(_journalPath);
        }
        if (observationRetired && failure is IOException observed)
        {
            try
            {
                var bytes = ReadBytes(_journalPath); // never invent or silently replace missing/corrupt recovery records
                var record = JsonSerializer.Deserialize<RecoveryRecord>(bytes) ?? throw MissingRecoveryScope();
                if (record.Schema != 2 || record.ProfileToken != session.VerifiedProfileToken
                    || observed is RogReadOnlyObservationException { ProfileToken: { } token } && record.ProfileToken != token
                    || observed is RogNativePresentationUnavailableException presentation
                        && (record.ProfileToken != presentation.ProfileToken || connection.ConnectionKind != presentation.ConnectionKind)
                    || record.DeviceKey != (observed switch
                    {
                        RogReadOnlyObservationException read => read.DeviceKey,
                        RogNativeArtworkUploadRefusedException artwork => artwork.DeviceKey,
                        RogNativePresentationUnavailableException lease => lease.DeviceKey,
                        _ => throw MissingRecoveryScope(),
                    })
                    || !SameProtocol(record.FirmwareHeader, ConnectionLabel(connection))) throw MissingRecoveryScope();
                var healthy = runningAt is { } start && failedAt is { } end && end >= start ? _time.GetElapsedTime(start, end) : TimeSpan.Zero;
                failure = new ObservationOwnerRetiredException(observed, bytes, healthy, intentRevision, nativeRevision,
                    connection.ConnectionKind, session.VerifiedProfileToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { failure = new AggregateException("Classified owner fault has no matching recorded recovery scope; journal retained.", observed, ex); }
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
        var goal = ResolveNativeGoal(session, connection, sourceBrightness, allowFreshNative, ct);
        await session.RestoreAsync(goal, ct).ConfigureAwait(false);
        return goal;
    }

    // Resolves and persists only the recorded goal. The caller owns the single fresh checked hardware restore.
    private byte ResolveNativeGoal(RogWiredOledSession session, IRogOledConnection connection,
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
        lock (_nativePreferenceGate)
        {
            if (!File.Exists(_nativePreferencePath)) return null;
            var preference = Read<NativeDisplayPreference>(_nativePreferencePath);
            if (preference.Schema != 1 || preference.Brightness is 0 or > 100
                || preference.Origin is not ("fresh-native-while-disabled" or "pre-takeover-native" or "explicit-fallback-100"))
                throw new InvalidDataException("Native display preference is invalid; restoration is refused.");
            return preference.DeviceKey == scope.DeviceKey && SameProtocol(preference.FirmwareHeader, scope.FirmwareHeader)
                && preference.ProfileToken == scope.ProfileToken ? preference : null;
        }
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
        // Owner observations and explicit lifecycle recovery can publish concurrently. Serialize the complete write/replace
        // transaction (and readers above), so neither caller truncates the other's temporary file or replaces an open reader.
        lock (_nativePreferenceGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_nativePreferencePath)!);
            var temporary = _nativePreferencePath + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(preference);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            // MoveFileEx cannot replace an open destination on Windows even for a cooperative read-only snapshot. ReplaceFile
            // preserves that old reader's snapshot while publishing the complete new record at the original path.
            if (File.Exists(_nativePreferencePath)) File.Replace(temporary, _nativePreferencePath, null);
            else File.Move(temporary, _nativePreferencePath);
        }
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
            if (observation.State is CaseStatusState.Blackout or CaseStatusState.Active)
                Volatile.Write(ref _caseBlackout, observation.State == CaseStatusState.Blackout ? 1 : 0);
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
        int? blackout = null;
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct); bound.CancelAfter(5000);
            var before = await _rgb.GetSceneAsync(bound.Token).ConfigureAwait(false);
            var controllers = await _rgb.ListControllersAsync(bound.Token).ConfigureAwait(false);
            var scene = await _screens.GetScreenSceneAsync(bound.Token).ConfigureAwait(false);
            var screens = await _screens.ListScreensAsync(bound.Token).ConfigureAwait(false);
            var after = await _rgb.GetSceneAsync(bound.Token).ConfigureAwait(false);
            var stable = JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after);
            if (stable) blackout = CaseStatusSummary.IsBlackout(after) ? 1 : 0;
            observation = stable
                ? CaseStatusSummary.Describe(after, controllers, scene, screens)
                : new(CaseStatusState.Pending, "场景正在变化", "等待新的后台回读");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        { observation = new(CaseStatusState.Unknown, "等待光域连接", "后台状态暂不可用"); }
        ct.ThrowIfCancellationRequested();
        // A native-menu readback presented while this one was read is newer: keep it until the next refresh.
        lock (_artwork)
        {
            if (version != _artworkVersion) return;
            if (blackout is { } confirmed) Volatile.Write(ref _caseBlackout, confirmed);
            if (observation == _lastObservation) return;
        }
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
