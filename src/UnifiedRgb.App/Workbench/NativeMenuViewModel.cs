using StreamJsonRpc;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>
/// Optional native-menu case control. Shows the Host's persisted request (default off) apart from whether a verified source
/// actually armed it, and changes only that request. One explicit click sends at most one request, the inverse of a fresh read
/// on the proxy captured for it; an unanswered request is read back, never resent. Every getter and the click use only an
/// observation that is fresh at that moment. It opens no device, sends no case action and never enters or leaves the
/// keyboard/mouse handoff.
/// </summary>
public sealed class NativeMenuViewModel : ObservableObject
{
    public const string Guide = "开启只是本机请求（默认关闭），开启本身不执行任何动作；经验证后按下切换开/关，上下拨暂不控制机箱。每次按下按后台当前场景决定；未接入来源时显示不可用。";
    public const string UnsupportedMessage = "当前后台版本不支持原生菜单联动（只读提示）：未发送任何指令。";
    private const string ExpiredMessage = "原生菜单联动状态已过期，等待新的后台回读。";
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);
    private readonly Func<INativeMenuService> _service;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _error;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;
    private NativeMenuStatusDto? _status;
    private DateTimeOffset? _observedAt;
    private string _message = "原生菜单联动状态尚未读取。";
    private bool _connected = true;
    private bool _unsupported;
    private long _epoch;
    private long _version;
    private long? _requestEpoch;

    // timeout optionally bounds every call (tests); the defaults are 2 s per status read and 5 s per request.
    public NativeMenuViewModel(Func<INativeMenuService> service, IUiDispatcher ui, Action<Exception> error,
        Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _readTimeout = timeout ?? TimeSpan.FromSeconds(2);
        _writeTimeout = timeout ?? TimeSpan.FromSeconds(5);
        ReadCommand = new AsyncCommand(RefreshAsync, error);
        ToggleCommand = new AsyncCommand(_ => ToggleAsync(), error, _ => CanToggle);
    }

    public AsyncCommand ReadCommand { get; }
    public AsyncCommand ToggleCommand { get; }

    /// <summary>The current connection's last fresh Host status, or null when unknown, pending, expired or disconnected.</summary>
    public NativeMenuStatusDto? Status => Observed;
    public bool IsUnsupported => _unsupported;
    public bool CanToggle => _connected && !_unsupported && _requestEpoch is null && Observed is not null;
    public string ToggleLabel => Observed?.DesiredEnabled == true ? "关闭原生菜单联动" : "开启原生菜单联动";
    public string StateTitle => Observed is { } s ? Title(s) : Requesting ? "正在等待后台答复"
        : !_connected ? "后台未连接" : _unsupported ? "当前后台不支持原生菜单联动" : "原生菜单联动状态未知";
    public string StatusLine => Observed is { } s
        ? $"已请求：{(s.DesiredEnabled ? "开启" : "关闭")} · 菜单来源：{(s.SourcePresent ? "已接入" : "未接入")} · 实际：{(s.Active ? "已就绪" : "未生效")}"
            + (s.UpdatedAt is { } at ? $" · 更新于 {at.ToLocalTime():HH:mm:ss}" : string.Empty)
        : "后台状态未读取 · 不使用上次状态判断";
    public string Detail => Observed?.Detail ?? string.Empty;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Observed?.Detail);
    public string LastOutcomeText => Observed?.LastOutcome is { } o && !string.IsNullOrWhiteSpace(o) ? "最近一次：" + o : string.Empty;
    public bool HasLastOutcome => LastOutcomeText.Length > 0;
    public string LastErrorText => Observed?.LastError is { } e && !string.IsNullOrWhiteSpace(e) ? "最近错误：" + e : string.Empty;
    public bool HasLastError => LastErrorText.Length > 0;
    public string Message => _status is not null && Observed is null ? ExpiredMessage : _message;

    // The last status only while it is fresh by the injected clock; a clock that moved backwards leaves its age unknown.
    private NativeMenuStatusDto? Observed => _observedAt is { } at && Fresh(at) ? _status : null;
    private bool Requesting => _requestEpoch == Volatile.Read(ref _epoch);

    /// <summary>Bounded read-only status poll. An old Host is not probed again on the same connection; an in-flight request owns the feedback.</summary>
    public async Task RefreshAsync()
    {
        ExpireObservation();
        var epoch = Volatile.Read(ref _epoch);
        if (!_connected || _unsupported || _requestEpoch == epoch) return;
        var version = Interlocked.Increment(ref _version);
        var (status, error) = await ReadAsync(_service).ConfigureAwait(true);
        if (error is RemoteMethodNotFoundException) PostUnsupported(epoch, version);
        else Post(epoch, version, status, status is null ? Unread(error) : "以上为后台回读的状态；开启请求不等于已生效。");
    }

    /// <summary>One explicit click: a fresh read and at most one request for its inverse, on one captured proxy, then one readback.</summary>
    public async Task ToggleAsync()
    {
        var epoch = Volatile.Read(ref _epoch);
        ExpireObservation(); // An expired status never authorises a request.
        if (!CanToggle) return; // ExecuteAsync does not consult CanExecute.
        INativeMenuService proxy;
        // One proxy per intent: neither the request nor its readback can move to a connection this intent never saw.
        try { proxy = _service(); }
        catch (InvalidOperationException ex) // Not connected or blocked: nothing was sent.
        {
            Post(epoch, Interlocked.Increment(ref _version), null, Unread(ex) + "未发送请求。");
            return;
        }
        _requestEpoch = epoch;
        try
        {
            // The preflight retires every earlier status read.
            var version = Interlocked.Increment(ref _version);
            Post(epoch, version, null, "正在读取最新状态…");
            var (current, readError) = await ReadAsync(() => proxy).ConfigureAwait(true);
            if (readError is RemoteMethodNotFoundException) { PostUnsupported(epoch, version); return; }
            if (current is null)
            {
                Post(epoch, version, null, Unread(readError) + "未发送请求。");
                if (readError is RemoteInvocationException rejected) ReportHostError(epoch, rejected);
                return;
            }
            if (!Current(epoch, version)) return; // Never write through a connection that dropped or reconnected meanwhile.
            var enable = !current.DesiredEnabled;
            var action = enable ? "开启原生菜单联动" : "关闭原生菜单联动";
            Post(epoch, version, null, $"正在请求{action}，等待后台答复…");
            var read = $"{action}结果未确认：未收到后台答复，未自动重发；以上为回读状态。";
            var unread = $"{action}结果未知：未收到后台答复，状态也未读到；未自动重发，请待回读后再操作。";
            try
            {
                var answer = await BoundAsync(ct => proxy.SetNativeMenuEnabledAsync(enable, ct), _writeTimeout).ConfigureAwait(true);
                // An answer from a connection that is already gone is never shown; the current connection is read below.
                if (answer is not null && Current(epoch, version))
                {
                    Post(epoch, version, answer, $"后台已答复{action}请求：以上为后台返回的状态，“实际”一栏才表示是否生效。");
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
                // The Host answered with an error; read back instead of assuming nothing changed.
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

    public void SetConnected(bool connected)
    {
        _connected = connected;
        Interlocked.Increment(ref _epoch);
        // A new connection may reach a different Host: forget its compatibility and every earlier observation.
        _status = null; _observedAt = null; _unsupported = false;
        _message = connected ? "等待读取原生菜单联动状态…" : "后台未连接，原生菜单联动状态未知；重新连接后再读取。";
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
    private async Task<(NativeMenuStatusDto? Status, Exception? Error)> ReadAsync(Func<INativeMenuService> service)
    {
        try { return (await BoundAsync(ct => service().GetNativeMenuStatusAsync(ct), _readTimeout).ConfigureAwait(true), null); }
#pragma warning disable CA1031 // Optional native-menu status cannot invalidate the independent case readback.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex); }
#pragma warning restore CA1031
    }

    // A proxy task may ignore cancellation, so the deadline also ends the wait itself.
    private static async Task<T> BoundAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan limit)
    {
        using var bound = new CancellationTokenSource(limit);
        return await call(bound.Token).WaitAsync(bound.Token).ConfigureAwait(true);
    }

    private void Post(long epoch, long version, NativeMenuStatusDto? status, string message) => _ui.Post(() =>
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
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(IsUnsupported)); OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleLabel)); OnPropertyChanged(nameof(StateTitle)); OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(HasDetail)); OnPropertyChanged(nameof(LastOutcomeText));
        OnPropertyChanged(nameof(HasLastOutcome)); OnPropertyChanged(nameof(LastErrorText)); OnPropertyChanged(nameof(HasLastError));
        OnPropertyChanged(nameof(Message));
        ToggleCommand.RaiseCanExecuteChanged();
    }

    // Never claims more than the Host reported: a request alone is not "running", and only Active means a verified source is armed.
    private static string Title(NativeMenuStatusDto s) => s switch
    {
        { State: "Active", Active: true, SourcePresent: true } => "已就绪：经验证的菜单来源已接入（以后台回读为准）",
        { State: "Unavailable" } => "已请求开启：等待经验证的菜单来源（当前不可用）",
        { State: "Paused" } => "已暂停：键鼠外设配置中",
        { State: "Off" } => "未开启（默认关闭）",
        { State: "Error", PreferenceReadable: false } => "偏好无法读取：后台已按关闭处理",
        { State: "Error" } => "原生菜单联动出错",
        { State: "Stopped" } => "后台正在停止：原生菜单联动已解除",
        _ => "未知状态：不能确认是否已生效",
    };

    private static string Unread(Exception? error) => error switch
    {
        HostNotConnectedException => "后台未连接，原生菜单联动状态未知。",
        HostAccessBlockedException => "界面已停止向后台发送操作，原生菜单联动状态未知。",
        RemoteInvocationException => "后台返回错误，原生菜单联动状态未读到；稍后自动重试读取。",
        _ => "原生菜单联动状态未读到；稍后自动重试读取。",
    };
}
