using System.Diagnostics;
using StreamJsonRpc;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>
/// Optional keyboard/mouse handoff to the Gear Link web page. Only the keyboard/mouse peripheral integration pauses; case
/// lighting and screens keep running. ConfigurationMode is the requested (persisted) handoff, never proof of release: the
/// web page is offered only while the current connection reports Ready with InterfacesReleased. One explicit intent sends at
/// most one request, on the proxy captured for it; an unanswered request is read back, never resent. Every getter and intent
/// uses only an observation that is fresh at that moment, so an expired one is refused before any poll retires it. This view
/// model opens no device and simulates no release; every state shown comes from the Host.
/// A handoff requested by an owned firmware maintenance (Intent) is shown as maintenance, not as a web handoff: while its lease
/// runs nothing is retried or resumed. Nothing here ever requests a resume by itself: no status read, refresh, connection,
/// queued UI update or completed lease sends Exit. The Host has no atomic lease-scoped finish (PM-HOST-1 is not implemented)
/// and the current Host never reports an Intent; an ordinary Exit cannot tell the observed lease from a newer manual request,
/// so it is sent only for the user's explicit finish, with no automatic fallback. Every handoff, whatever its origin or
/// lease outcome, ends only by that explicit action.
/// </summary>
public sealed class PeripheralConfigurationViewModel : ObservableObject
{
    public static readonly Uri GearLinkUri = new("https://gearlink.asus.com");
    public const string UnsupportedMessage = "当前后台版本不支持外设配置交接（只读提示）：键鼠联动保持原样，未发送任何指令。";
    private const string ActiveState = "Active", EnteringState = "Entering", ReadyState = "Ready", ResumingState = "Resuming", ErrorState = "Error";
    private const string ExpiredMessage = "外设配置状态已过期，等待新的后台回读。";
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);
    private readonly Func<IPeripheralConfigurationService> _service;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _error;
    private readonly Action<Uri> _openLink;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;
    private PeripheralConfigurationStatusDto? _status;
    private DateTimeOffset? _observedAt;
    private string _message = "外设配置状态尚未读取。";
    private bool _connected = true;
    private bool _unsupported;
    private long _epoch;
    private long _version;
    private long? _requestEpoch;

    // timeout optionally bounds every call (tests); the defaults are 2 s per status read and 10 s per request.
    public PeripheralConfigurationViewModel(Func<IPeripheralConfigurationService> service, IUiDispatcher ui, Action<Exception> error,
        Action<Uri> openLink, Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _openLink = openLink ?? throw new ArgumentNullException(nameof(openLink));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _readTimeout = timeout ?? TimeSpan.FromSeconds(2);
        _writeTimeout = timeout ?? TimeSpan.FromSeconds(10);
        ReadCommand = new AsyncCommand(RefreshAsync, error);
        EnterCommand = new AsyncCommand(_ => EnterAsync(), error, _ => CanEnter);
        ExitCommand = new AsyncCommand(_ => ExitAsync(), error, _ => CanExit);
        OpenGearLinkCommand = new RelayCommand(_ => OpenGearLink(), _ => CanOpenGearLink);
    }

    public AsyncCommand ReadCommand { get; }
    public AsyncCommand EnterCommand { get; }
    public AsyncCommand ExitCommand { get; }
    public RelayCommand OpenGearLinkCommand { get; }

    /// <summary>The current connection's last fresh Host status, or null when unknown, pending, expired or disconnected.</summary>
    public PeripheralConfigurationStatusDto? Status => Observed;
    public bool HasStatus => Observed is not null;
    public bool IsUnsupported => _unsupported;
    // A fresh Error always allows an explicit Enter retry. Resume is withheld while a requested handoff is in Error with its
    // interfaces unreleased: the Host requires an Enter reattempt there, and resume must not bypass it. Neither is offered while
    // an owned firmware maintenance holds the peripherals inside its lease.
    public bool CanEnter => Idle && !MaintenanceHeld && Observed is { } s && (s.State == ErrorState || s is { ConfigurationMode: false, State: ActiveState });
    public bool CanExit => Idle && !MaintenanceHeld && Observed is { } s && (s.State == ErrorState ? !s.ConfigurationMode || s.InterfacesReleased : s.ConfigurationMode);

    /// <summary>The finish verdict for the current fresh status (null when unknown); see <see cref="PeripheralMaintenanceFinish"/>.</summary>
    public PeripheralFinishVerdict? Finish => Observed is { } s ? PeripheralMaintenanceFinish.Evaluate(s, _now()) : null;
    /// <summary>The fresh status says an owned firmware maintenance requested this handoff (whatever its outcome).</summary>
    public bool FirmwareMaintenance => Finish?.FirmwareMaintenance == true;
    /// <summary>Gear Link is for a user's web configuration; a firmware-maintenance handoff never offers it.</summary>
    public bool CanOpenGearLink => IsReady && !FirmwareMaintenance;
    public string EnterLabel => FirmwareMaintenance ? "重试释放键鼠接口" : "配置键鼠（暂时交给网页）";
    public string ExitLabel => FirmwareMaintenance ? "完成维护，恢复联动" : "完成配置，恢复联动";
    public string OriginNote => (Finish, Observed?.Intent) switch
    {
        ({ FirmwareMaintenance: true } f, { } i) =>
            $"交接来源：固件维护（租约 {f.LeaseId ?? "未提供"}，{LeaseText(i.LeaseState)}；维护时连接方式 {Role(i.LeaseRole)}，当前 {Role(i.CurrentRole)}）。",
        ({ Kind: PeripheralFinishKind.ManualIntent }, _) => "交接来源：你手动发起的 Gear Link 配置；不会自动清除，只在你点“完成配置，恢复联动”后恢复。",
        ({ Kind: PeripheralFinishKind.OriginUnknown }, _) when Observed is { ConfigurationMode: true } =>
            "交接来源：后台未报告（可能是手动 Gear Link，也可能是固件维护遗留）；不会自动清除，只按你的明确操作重试或恢复。",
        _ => string.Empty,
    };
    public bool HasOriginNote => OriginNote.Length != 0;
    private bool MaintenanceHeld => Finish?.Kind == PeripheralFinishKind.MaintenanceRunning;

    /// <summary>Handoff success: Ready AND interfaces actually released on the current connection; a requested mode alone never counts.</summary>
    public bool IsReady => _connected && !Requesting && Observed is { ConfigurationMode: true, InterfacesReleased: true, State: ReadyState };

    /// <summary>True only when this connection confirmed normal keyboard/mouse integration, or the Host has no handoff at all.</summary>
    public bool KeyboardIntegrationConfirmed => _connected && !Requesting && (_unsupported || Observed is { ConfigurationMode: false, State: ActiveState });
    public string KeyboardHoldNote => Requesting ? "正在交接或恢复键鼠联动，暂不切换键盘显示。"
        : Observed is null ? "外设配置状态未确认，暂不切换键盘显示。" : "键鼠联动已暂停或未确认恢复（外设配置中），暂不切换键盘显示。";

    public string StateTitle => Observed is { } s ? MaintenanceTitle(Finish) ?? Title(s) : Requesting ? "正在等待后台答复"
        : !_connected ? "后台未连接" : _unsupported ? "当前后台不支持外设配置" : "外设配置状态未知";
    public string StatusLine => Observed is { } s
        ? $"后台状态 {s.State} · 已请求交接：{YesNo(s.ConfigurationMode)} · 接口已释放：{YesNo(s.InterfacesReleased)}"
            + (s.UpdatedAt is { } at ? $" · 更新于 {at.ToLocalTime():HH:mm:ss}" : string.Empty)
        : "后台状态未读取 · 不使用上次状态判断";
    public string Detail => Observed?.Detail ?? string.Empty;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Observed?.Detail);
    public bool HasLastError => !string.IsNullOrWhiteSpace(Observed?.LastError);
    public string LastErrorText => Observed?.LastError is { } e && !string.IsNullOrWhiteSpace(e) ? "最近错误：" + e : string.Empty;
    public string Guidance => Observed switch
    {
        null => string.Empty,
        _ when MaintenanceGuidance(Finish) is { } maintenance => maintenance,
        { State: ReadyState, ConfigurationMode: true, InterfacesReleased: true } => "请手动点击“打开 Gear Link 网页”配置键鼠（不会自动打开或连接）；完成后建议先在网页中断开设备，再点“完成配置，恢复联动”。",
        { State: ActiveState, ConfigurationMode: false } => "需要用网页配置键鼠时，再点“配置键鼠（暂时交给网页）”。",
        { State: ResumingState } => "后台正在重新接管键鼠，请稍候。",
        { State: ErrorState, ConfigurationMode: true, InterfacesReleased: false } => "交接未完成，键鼠接口未释放：请点“配置键鼠（暂时交给网页）”明确重试释放；就绪前不提供“完成配置，恢复联动”。",
        { State: ErrorState } => "可点“配置键鼠（暂时交给网页）”重试交接，或点“完成配置，恢复联动”尝试恢复联动。",
        _ => "已请求交接不代表接口已释放；就绪前请勿在网页中连接键鼠。",
    };
    public string NextStep
    {
        get
        {
            if (Requesting) return "正在等待后台答复；请勿重复操作。恢复联动或打开网页须等新的实际状态。";
            if (!_connected) return "后台未连接，不能确认键鼠接口状态；恢复联动和打开网页暂不可用，重新连接后再读取。";
            if (_unsupported) return "当前后台仅支持只读提示，不能交接或恢复；恢复联动和打开网页均不可用。";
            if (Observed is not { } s) return "状态未知、未读到或已过期：恢复联动和打开网页暂不可用，请等待新的后台回读。";
            if (Finish is { FirmwareMaintenance: true } finish) return MaintenanceNextStep(finish.Kind, s);
            return s switch
            {
                { State: ReadyState, ConfigurationMode: true, InterfacesReleased: true } =>
                    "下一步：先点“打开 Gear Link 网页”完成配置；再点“完成配置，恢复联动”。两步都需手动点击。",
                { State: ActiveState, ConfigurationMode: false } =>
                    "尚未开始网页交接：先点“配置键鼠（暂时交给网页）”。接口未确认释放前，恢复联动和打开网页不可用。",
                { State: ResumingState } => "后台正在恢复键鼠联动；完成前请稍候，不要重复操作。",
                { State: ErrorState, ConfigurationMode: true, InterfacesReleased: false } =>
                    "交接未完成且接口未释放：先点“配置键鼠（暂时交给网页）”重试；恢复联动和打开网页不可用。",
                { State: ErrorState } => "可重试交接或恢复联动；只有后台确认 Ready 且接口已释放后，才能打开 Gear Link。",
                { State: EnteringState } => "已请求交接，正在等待接口实际释放；确认 Ready 后才能打开网页。",
                _ => "已请求交接不等于接口已释放；确认 Ready 且接口已释放后才能打开网页。",
            };
        }
    }
    public string Message => _status is not null && Observed is null ? ExpiredMessage : _message;

    // The last status only while it is fresh by the injected clock; a clock that moved backwards leaves its age unknown.
    private PeripheralConfigurationStatusDto? Observed => _observedAt is { } at && Fresh(at) ? _status : null;
    private bool Requesting => _requestEpoch == Volatile.Read(ref _epoch);
    private bool Idle => _connected && !_unsupported && _requestEpoch is null;

    /// <summary>Bounded read-only status poll. An old Host is not probed again on the same connection; an in-flight request owns the feedback.</summary>
    public async Task RefreshAsync()
    {
        ExpireObservation();
        var epoch = Volatile.Read(ref _epoch);
        if (!_connected || _unsupported || _requestEpoch == epoch) return;
        var version = Interlocked.Increment(ref _version);
        var (status, error) = await ReadAsync(_service).ConfigureAwait(true);
        if (error is RemoteMethodNotFoundException) PostUnsupported(epoch, version);
        else Post(epoch, version, status, status is null ? Unread(error) : "以上为后台回读的实际状态。");
    }

    public Task EnterAsync() => RequestAsync(enter: true);
    public Task ExitAsync() => RequestAsync(enter: false);

    /// <summary>One explicit intent: at most one request, on one proxy captured for it; an unanswered request is read back, never resent.</summary>
    private async Task RequestAsync(bool enter)
    {
        var epoch = Volatile.Read(ref _epoch);
        ExpireObservation(); // An expired status never authorises a request; the click retires it for the bindings.
        if (!(enter ? CanEnter : CanExit)) return; // ExecuteAsync does not consult CanExecute.
        var action = enter ? FirmwareMaintenance ? "重试释放" : "交接键鼠" : "恢复联动";
        IPeripheralConfigurationService proxy;
        // One proxy per intent: neither the request nor its readback can move to a connection this intent never saw.
        try { proxy = _service(); }
        catch (InvalidOperationException ex) // Not connected or blocked: nothing was sent.
        {
            Post(epoch, Interlocked.Increment(ref _version), null, Unread(ex) + $"未发送{action}请求。");
            return;
        }
        _requestEpoch = epoch;
        try
        {
            // The request retires every earlier status read, so no earlier Ready can stay on screen.
            var version = Interlocked.Increment(ref _version);
            Post(epoch, version, null, $"正在请求{action}，等待后台答复…");
            var read = $"{action}结果未确认：未收到后台答复，未自动重发；以上为回读状态。";
            var unread = $"{action}结果未知：未收到后台答复，状态也未读到；未自动重发，请待回读后再操作。";
            try
            {
                var answer = await BoundAsync(ct => enter ? proxy.EnterPeripheralConfigurationAsync(ct) : proxy.ExitPeripheralConfigurationAsync(ct), _writeTimeout).ConfigureAwait(true);
                // An answer from a connection that is already gone is never shown; the current connection is read below.
                if (answer is not null && Current(epoch, version))
                {
                    Post(epoch, version, answer, $"后台已答复{action}请求，以上为后台返回的实际状态。");
                    return;
                }
            }
            catch (RemoteMethodNotFoundException)
            {
                PostUnsupported(epoch, version); // Old Host: nothing was applied.
                return;
            }
            catch (RemoteInvocationException ex)
            {
                // The Host answered with an error (for example busy); read back instead of assuming nothing changed.
                Post(epoch, version, null, $"后台对{action}请求返回错误，正在回读状态…");
                read = $"后台对{action}请求返回错误（见错误提示），以上为回读状态。";
                unread = $"后台对{action}请求返回错误，状态也未读到，不能确认是否已生效。";
                ReportHostError(epoch, ex);
            }
#pragma warning disable CA1031 // A deadline, cancellation or lost connection can follow a request that already applied.
            catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
            {
                Post(epoch, version, null, $"{action}结果未确认：未收到后台答复，请求可能已生效；未自动重发，正在回读…");
            }
            if (!Current(epoch, version))
            {
                // The captured connection is gone: never read or write through it again; read the current connection instead.
                if (_connected) await RefreshAsync().ConfigureAwait(true);
                return;
            }
            var (observed, error) = await ReadAsync(() => proxy).ConfigureAwait(true);
            if (error is RemoteMethodNotFoundException) PostUnsupported(epoch, version);
            else Post(epoch, version, observed, observed is null ? unread : read);
        }
        finally
        {
            if (_requestEpoch == epoch) _requestEpoch = null;
            _ui.Post(Notify);
        }
    }

    /// <summary>Opens the web page only for this explicit click, and only while the handoff is actually Ready.</summary>
    private void OpenGearLink()
    {
        ExpireObservation();
        if (!CanOpenGearLink) return;
        try
        {
            _openLink(GearLinkUri);
            _message = "已请求系统浏览器打开 Gear Link；配置完成后点“完成配置，恢复联动”。";
            OnPropertyChanged(nameof(Message));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException) { _error(ex); }
    }

    /// <summary>Default launcher: the system browser, used only by the explicit Gear Link click.</summary>
    public static void OpenInBrowser(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        Interlocked.Increment(ref _epoch);
        // A new connection may reach a different Host: forget its compatibility and every earlier observation.
        _status = null; _observedAt = null; _unsupported = false;
        _message = connected ? "等待读取外设配置状态…" : "后台未连接，外设配置状态未知；重新连接后再读取。";
        Notify();
    }

    public void ExpireObservation()
    {
        if (_observedAt is not { } at || Fresh(at)) return;
        _status = null; _observedAt = null;
        _message = ExpiredMessage;
        Notify();
    }

    /// <summary>Bounded status read; failures are returned, never shown as the last observation.</summary>
    private async Task<(PeripheralConfigurationStatusDto? Status, Exception? Error)> ReadAsync(Func<IPeripheralConfigurationService> service)
    {
        try { return (await BoundAsync(ct => service().GetPeripheralConfigurationStatusAsync(ct), _readTimeout).ConfigureAwait(true), null); }
#pragma warning disable CA1031 // Optional peripheral status cannot invalidate the independent case readback.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex); }
#pragma warning restore CA1031
    }

    // A proxy task may ignore cancellation, so the deadline also ends the wait itself.
    private static async Task<T> BoundAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan limit)
    {
        using var bound = new CancellationTokenSource(limit);
        return await call(bound.Token).WaitAsync(bound.Token).ConfigureAwait(true);
    }

    private void Post(long epoch, long version, PeripheralConfigurationStatusDto? status, string message) => _ui.Post(() =>
    {
        if (!Current(epoch, version)) return;
        _status = status; _observedAt = status is null ? null : _now(); _message = message;
        Notify();
    });

    private void PostUnsupported(long epoch, long version) => _ui.Post(() =>
    {
        if (!Current(epoch, version)) return;
        _unsupported = true; _status = null; _observedAt = null; _message = UnsupportedMessage;
        Notify();
    });

    // Host-provided rejections reach the error banner; deadlines and lost connections are shown as unknown results instead.
    private void ReportHostError(long epoch, RemoteInvocationException ex) =>
        _ui.Post(() => { if (_connected && epoch == Volatile.Read(ref _epoch)) _error(ex); });

    private bool Fresh(DateTimeOffset observedAt)
    {
        var age = _now() - observedAt;
        return age >= TimeSpan.Zero && age <= Freshness;
    }

    private bool Current(long epoch, long version) => _connected && epoch == Volatile.Read(ref _epoch) && version == Volatile.Read(ref _version);

    private void Notify()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); OnPropertyChanged(nameof(IsUnsupported));
        OnPropertyChanged(nameof(CanEnter)); OnPropertyChanged(nameof(CanExit)); OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(StateTitle)); OnPropertyChanged(nameof(StatusLine)); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(LastErrorText)); OnPropertyChanged(nameof(HasLastError)); OnPropertyChanged(nameof(Guidance)); OnPropertyChanged(nameof(NextStep)); OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(KeyboardHoldNote)); OnPropertyChanged(nameof(KeyboardIntegrationConfirmed));
        OnPropertyChanged(nameof(Finish)); OnPropertyChanged(nameof(FirmwareMaintenance)); OnPropertyChanged(nameof(CanOpenGearLink));
        OnPropertyChanged(nameof(EnterLabel)); OnPropertyChanged(nameof(ExitLabel)); OnPropertyChanged(nameof(OriginNote)); OnPropertyChanged(nameof(HasOriginNote));
        EnterCommand.RaiseCanExecuteChanged(); ExitCommand.RaiseCanExecuteChanged(); OpenGearLinkCommand.RaiseCanExecuteChanged();
    }

    private static string Title(PeripheralConfigurationStatusDto s) => s switch
    {
        { State: ReadyState, ConfigurationMode: true, InterfacesReleased: true } => "已就绪：键鼠接口已释放，可在网页中配置",
        { State: ReadyState } => "尚未就绪：后台未同时确认交接与接口释放",
        { State: EnteringState } => "正在交接：等待后台实际释放键鼠接口",
        { State: ResumingState } => "正在恢复键鼠联动",
        { State: ErrorState } => "外设配置出错",
        { State: ActiveState, ConfigurationMode: true } => "已请求交接，接口尚未释放",
        { State: ActiveState } => "键鼠联动运行中",
        _ => "未知状态：无法确认键鼠接口是否已释放",
    };

    private static string? MaintenanceTitle(PeripheralFinishVerdict? finish) => finish?.Kind switch
    {
        PeripheralFinishKind.MaintenanceRunning => "固件维护进行中：键鼠联动暂停",
        PeripheralFinishKind.ManualFinishRequired => "维护程序报告已完成：键鼠接口已释放，需手动恢复联动",
        PeripheralFinishKind.RetryRequired => "固件维护遗留的交接出错：需要明确重试释放",
        PeripheralFinishKind.MaintenanceFailed => "固件维护失败：不当作就绪，需要你的明确操作",
        PeripheralFinishKind.OutcomeUnknown => "固件维护结果未知：不当作就绪",
        PeripheralFinishKind.LeaseExpired => "固件维护租约已过期：不当作就绪，需要你的明确操作",
        PeripheralFinishKind.RoleUnmatched => "固件维护的连接方式不一致或未确认：不当作就绪",
        _ => null,
    };

    private static string? MaintenanceGuidance(PeripheralFinishVerdict? finish) => finish?.Kind switch
    {
        PeripheralFinishKind.MaintenanceRunning => "维护程序持有键鼠接口：维护结束前不重试释放、不恢复联动，也不提供 Gear Link 网页。",
        PeripheralFinishKind.ManualFinishRequired => "维护程序报告已完成并释放了键鼠接口。界面不会自动恢复联动（后台没有按维护租约限定的完成接口，普通恢复无法区分维护与之后的手动交接）：确认无误后手动点“完成维护，恢复联动”。",
        PeripheralFinishKind.RetryRequired => "只点恢复联动不会解除这个错误（后台需先重新释放并恢复原始键盘设置）：请先唤醒键盘，再点“重试释放键鼠接口”；后台确认就绪后点“完成维护，恢复联动”。",
        PeripheralFinishKind.Waiting => "后台正在处理固件维护后的交接，请等待新的回读。",
        PeripheralFinishKind.MaintenanceFailed => "固件维护失败：不会自动恢复联动，也不当作就绪；需要时请明确重试或恢复（后台的恢复日志、所有权和厂商软件检查照常生效）。",
        PeripheralFinishKind.OutcomeUnknown => "固件维护结果未知：不会自动恢复联动，也不当作就绪；需要时请明确重试或恢复（后台的恢复日志、所有权和厂商软件检查照常生效）。",
        PeripheralFinishKind.LeaseExpired => "维护租约已过期，维护程序不再拥有键鼠接口：不会自动恢复联动；需要时请明确重试或恢复。",
        PeripheralFinishKind.RoleUnmatched => "维护租约只适用于开始时的连接方式；当前连接方式不同或未确认（键盘可能睡眠或离线）：不跨连接方式恢复，也不重放状态。唤醒键盘后再明确操作。",
        _ => null,
    };

    private static string MaintenanceNextStep(PeripheralFinishKind kind, PeripheralConfigurationStatusDto s) => (kind, s) switch
    {
        (PeripheralFinishKind.MaintenanceRunning, _) => "固件维护进行中：等待维护程序结束；重试释放、恢复联动和打开网页均不可用。",
        (PeripheralFinishKind.Waiting, _) => "后台正在处理交接；完成前请稍候，不要重复操作。",
        (_, { State: ErrorState, InterfacesReleased: false }) => "下一步：唤醒键盘后点“重试释放键鼠接口”；后台确认就绪后再点“完成维护，恢复联动”。",
        (_, { State: ReadyState, ConfigurationMode: true, InterfacesReleased: true }) => "下一步：确认无误后手动点“完成维护，恢复联动”（不会自动恢复）。",
        _ => "等待新的后台回读后再操作；不会自动恢复联动。",
    };

    private static string LeaseText(string state) => state switch
    {
        PeripheralMaintenanceLeaseStates.Active => "进行中",
        PeripheralMaintenanceLeaseStates.Completed => "已完成",
        PeripheralMaintenanceLeaseStates.Failed => "失败",
        PeripheralMaintenanceLeaseStates.Expired => "已过期",
        _ => "结果未知",
    };

    private static string Role(string? role) => role switch
    {
        PeripheralRoles.Wired => "有线 USB",
        PeripheralRoles.Rf => "2.4G 无线",
        null => "未确认（可能睡眠或离线）",
        _ => role,
    };

    private static string Unread(Exception? error) => error switch
    {
        HostNotConnectedException => "后台未连接，外设配置状态未知。",
        HostAccessBlockedException => "界面已停止向后台发送操作，外设配置状态未知。",
        RemoteInvocationException => "后台返回错误，外设配置状态未读到；不能确认键鼠接口是否已释放；稍后会自动重新读取状态（只读）。",
        _ => "外设配置状态未读到，不能确认键鼠接口是否已释放；稍后会自动重新读取状态（只读）。",
    };

    private static string YesNo(bool value) => value ? "是" : "否";
}
