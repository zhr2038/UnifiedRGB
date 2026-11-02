using System.Globalization;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>Optional read-only observation and explicit preference writes; never retries an uncertain write.</summary>
public sealed class KeyboardCodexUsageViewModel : ObservableObject
{
    private readonly Func<IKeyboardDisplayService> _service;
    private readonly IUiDispatcher _ui;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _timeout;
    private KeyboardCodexUsageDto? _status;
    private DateTimeOffset? _observedAt;
    private bool _connected = true, _unsupported;
    private long _epoch, _version;
    private long? _writing, _reading;
    private string _message = "默认关闭；开启后用键盘原有实体翻页键选择独立 Codex 页。";

    public KeyboardCodexUsageViewModel(Func<IKeyboardDisplayService> service, IUiDispatcher ui, Action<Exception> error,
        Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        _service = service; _ui = ui; _now = now ?? (() => DateTimeOffset.UtcNow); _timeout = timeout ?? TimeSpan.FromSeconds(3);
        ReadCommand = new AsyncCommand(RefreshAsync, error);
        ShowCommand = new AsyncCommand(_ => SetEnabledAsync(true), error, _ => CanShow);
        StopCommand = new AsyncCommand(_ => SetEnabledAsync(false), error, _ => CanStop);
    }

    public AsyncCommand ReadCommand { get; }
    public AsyncCommand ShowCommand { get; }
    public AsyncCommand StopCommand { get; }
    public KeyboardCodexUsageDto? Status => _observedAt is { } at && _now() - at >= TimeSpan.Zero && _now() - at <= TimeSpan.FromSeconds(10) ? _status : null;
    public bool CanShow => CanWrite && Status is { IndependentPageRouting: true, Enabled: false, State: not "SettingsError" };
    public bool CanStop => CanWrite && Status is { Enabled: true };
    private bool CanWrite => _connected && !_unsupported && _writing != _epoch;
    public string Message => _message;
    public IReadOnlyList<KeyboardCodexWindowDto> PreviewWindows => Status is { } status
        ? new[] { status.Primary, status.Secondary }.OfType<KeyboardCodexWindowDto>().Where(w => w.ResetsAt > _now())
            .Select(w => w with { ResetsAt = w.ResetsAt.ToLocalTime() }).ToArray()
        : [];
    public string OpenActionStatus => "打开 Codex：" + (Status?.OpenActionStatus switch
    {
        "Requested" => "已提交打开请求", "Queued" => "正在打开", "Failed" => "打开失败",
        "Unknown" => "结果未确认，请查看电脑窗口", "Cancelled" => "已取消", "Unsupported" => "当前桌面暂不可用",
        "NotRequested" => "在键盘 Codex 页按下拨杆即可打开", _ => "尚无操作",
    });
    public string Summary => Status is { } s
        ? (s.Enabled ? "已请求显示" : "已停止") + " · " + StateLabel(s.State) + (s.Bucket is null ? "" : " · " + s.Bucket)
            + "\n" + Window(s.Primary) + (s.Secondary is null ? "" : "\n" + Window(s.Secondary))
            + (s.Error is null ? "" : "\n" + s.Error)
            + "\n" + (!s.IndependentPageRouting ? "请更新后台后使用独立页面" : s.DedicatedPageActive ? "键盘已选择 Codex 页" : s.DedicatedPageSupported == true ? "可用键盘切页键选择 Codex" : s.DedicatedPageSupported == false ? "请在固件更新页安装独立页面固件" : "等待键盘连接")
        : "状态未知（不沿用旧连接数据）";

    public async Task RefreshAsync()
    {
        ExpireObservation();
        var epoch = _epoch;
        if (!_connected || _unsupported || _writing == epoch || _reading == epoch) return;
        _reading = epoch;
        var version = ++_version;
        try
        {
            var status = await BoundAsync(ct => _service().GetKeyboardCodexUsageAsync(ct)).ConfigureAwait(true);
            Post(epoch, version, status, "额度每分钟更新；仅在实体 Codex 页显示，不替换光域素材。", false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Post(epoch, version, null, Unsupported(ex) ? "当前后台不支持 Codex 配额；未更改设置。" : "配额状态未读到；未更改设置。", Unsupported(ex)); }
        finally { if (_reading == epoch) _reading = null; }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        ExpireObservation();
        if (enabled ? !CanShow : !CanStop) return;
        var epoch = _epoch; var version = ++_version;
        IKeyboardDisplayService proxy;
        try { proxy = _service(); }
        catch (InvalidOperationException)
        { Post(epoch, version, null, "后台未连接或操作已暂停；未发送请求。", false); return; }
        _writing = epoch; Notify();
        try
        {
            try
            {
                var status = await BoundAsync(ct => proxy.SetKeyboardCodexUsageEnabledAsync(enabled, ct)).ConfigureAwait(true);
                Post(epoch, version, status, enabled ? "已请求开启独立 Codex 页；请用键盘原有实体翻页键选择，需要固件支持。" : "已停止独立 Codex 页；光域素材不受影响。", false);
            }
            catch (Exception ex) when (Unsupported(ex))
            { Post(epoch, version, null, "当前后台不支持 Codex 配额；未自动重发。", true); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Post(epoch, version, null, "操作结果未确认，可能已生效；未自动重发，正在回读。", false);
                if (!_connected || _epoch != epoch) return;
                try
                {
                    var status = await BoundAsync(ct => proxy.GetKeyboardCodexUsageAsync(ct)).ConfigureAwait(true);
                    Post(epoch, version, status, "操作结果未确认；以上为回读状态，未自动重发。", false);
                }
                catch (Exception readError) when (readError is not OutOfMemoryException)
                { Post(epoch, version, null, "操作结果未知，回读也未完成；未自动重发。", Unsupported(readError)); }
            }
        }
        finally { if (_writing == epoch) _writing = null; _ui.Post(Notify); }
    }

    public void SetConnected(bool connected)
    {
        _connected = connected; _epoch++; _version++; _status = null; _observedAt = null; _unsupported = false;
        _message = connected ? "等待后台回读 Codex 配额偏好。" : "后台未连接；配额状态未知。"; Notify();
    }

    public void ExpireObservation()
    {
        if (_status is null || Status is not null) return;
        _status = null; _observedAt = null; _message = "配额观察已过期，等待后台回读。"; Notify();
    }

    private async Task<T> BoundAsync<T>(Func<CancellationToken, Task<T>> call)
    {
        using var stop = new CancellationTokenSource(_timeout);
        return await call(stop.Token).WaitAsync(stop.Token).ConfigureAwait(true);
    }

    private void Post(long epoch, long version, KeyboardCodexUsageDto? status, string message, bool unsupported) => _ui.Post(() =>
    {
        if (!_connected || _epoch != epoch || _version != version) return;
        _status = status; _observedAt = status is null ? null : _now(); _message = message; _unsupported = unsupported; Notify();
    });

    private void Notify()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(CanShow)); OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(PreviewWindows)); OnPropertyChanged(nameof(OpenActionStatus));
        ShowCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged();
    }

    private static bool Unsupported(Exception ex) => ex is NotSupportedException or RemoteMethodNotFoundException;
    private static string StateLabel(string state) => state switch
    {
        "Off" => "关闭", "Loading" => "读取中", "Ready" => "可用", "Stale" => "旧数据（不可当作当前额度）",
        "SettingsError" => "设置损坏，已关闭", _ => "未知 / 不可用",
    };
    private string Window(KeyboardCodexWindowDto? window) => window is null ? "窗口不可用"
        : window.ResetsAt <= _now() ? window.Label + "：已到重置时间，旧数值失效"
        : window.Label + " 剩余 " + window.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% · 重置 "
            + window.ResetsAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
}
