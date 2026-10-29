using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamJsonRpc;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>
/// Optional local keyboard OLED animation (Host default off). Lists the assets the current screen scene plays on a shared
/// timeline, saves one explicit local preference per click (never resent; an unanswered request is read back) and shows a
/// preview decoded from the Host's actual packed 4-bit upload bytes. It claims no frame rate: the interval is an upper bound
/// on how often the keyboard image may change. It opens no device and changes no scene, asset, package or lighting.
/// </summary>
public sealed class KeyboardAnimationViewModel : ObservableObject
{
    public const string Scope = "本机键盘显示，不属于场景包；不改动场景、素材或灯光。";
    public const string NativeNote = "已安装的原生光域菜单拥有键盘屏幕时，动画被抑制：不发送动画画面，原生状态画面与拨杆保持不变；以后单独审核原生菜单下的动画。";
    public const string UnsupportedMessage = "当前后台版本不支持键盘动画（只读提示）：未发送任何指令。";
    public const string RebindLabel = "重新绑定并保持开启";
    public const string RebindNote = "已保存的键盘动画绑定不在当前场景的共享时间轴上；已保存的设置未改变。请确认下方的素材选择，再点击“重新绑定并保持开启”明确重新绑定；不会自动保存。";
    private const string ExpiredMessage = "键盘动画状态已过期，等待新的后台回读。";
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);
    private static readonly int[] IntervalRungs = [2000, 3000, 5000, 10000];
    private readonly Func<IKeyboardDisplayService> _service;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _error;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _readTimeout, _writeTimeout, _previewTimeout;
    private KeyboardDisplayAnimationStatusDto? _status;
    private DateTimeOffset? _observedAt;
    private string _message = "键盘动画状态尚未读取。";
    private string _previewMessage = "预览显示后台实际打包的 4 位灰度数据。";
    private IReadOnlyList<PreviewFrame> _preview = [];
    private KeyboardDisplayAnimationChoiceDto? _choice;
    private Option<string> _fit, _encoding;
    private Option<int> _interval;
    private bool _dirty, _connected = true, _unsupported, _previewing;
    private long _epoch, _version, _draft;
    private long? _requestEpoch;

    public sealed record Option<T>(T Value, string Label);

    /// <summary>One decoded preview frame; <see cref="Levels"/> are the unpacked 0..15 levels (low nibble = even pixel).</summary>
    public sealed record PreviewFrame(int PositionMs, string Label, BitmapSource Image, byte[] Levels);

    // timeout optionally bounds every call (tests); the defaults are 2 s per read, 5 s per save and 20 s per preview.
    public KeyboardAnimationViewModel(Func<IKeyboardDisplayService> service, IUiDispatcher ui, Action<Exception> error,
        Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _readTimeout = timeout ?? TimeSpan.FromSeconds(2);
        _writeTimeout = timeout ?? TimeSpan.FromSeconds(5);
        _previewTimeout = timeout ?? TimeSpan.FromSeconds(20);
        _fit = Fits[0]; _encoding = Encodings[0]; _interval = Interval(2000);
        ReadCommand = new AsyncCommand(RefreshAsync, error);
        EnableCommand = new AsyncCommand(_ => SaveAsync(true), error, _ => CanEnable);
        DisableCommand = new AsyncCommand(_ => SaveAsync(false), error, _ => CanDisable);
        PreviewCommand = new AsyncCommand(_ => PreviewAsync(), error, _ => CanPreview);
    }

    public AsyncCommand ReadCommand { get; }
    public AsyncCommand EnableCommand { get; }
    public AsyncCommand DisableCommand { get; }
    public AsyncCommand PreviewCommand { get; }

    public IReadOnlyList<Option<string>> Fits { get; } = [new("contain", "完整显示（留黑边）"), new("cover", "铺满（居中裁切）")];
    public IReadOnlyList<Option<string>> Encodings { get; } =
        [new(KeyboardDisplayAnimationEncodings.LumaRound, "灰度就近（luma-round-v1）"), new(KeyboardDisplayAnimationEncodings.LumaBayer4, "有序抖动（luma-bayer4-v1）")];
    public IReadOnlyList<Option<int>> Intervals => [.. IntervalRungs
        .Where(ms => ms >= (Observed?.MinimumIntervalMs ?? 2000) && ms <= (Observed?.MaximumIntervalMs ?? 10000)).Select(Interval)];

    /// <summary>The current connection's last fresh Host status, or null when unknown, pending, expired or disconnected.</summary>
    public KeyboardDisplayAnimationStatusDto? Status => Observed;
    public bool IsUnsupported => _unsupported;
    public IReadOnlyList<KeyboardDisplayAnimationChoiceDto> Choices => Observed?.Choices ?? [];
    public bool HasChoices => Choices.Count > 0;

    public KeyboardDisplayAnimationChoiceDto? SelectedChoice { get => _choice; set => Edit(ref _choice, value); }
    public Option<string> SelectedFit { get => _fit; set => Edit(ref _fit, value); }
    public Option<string> SelectedEncoding { get => _encoding; set => Edit(ref _encoding, value); }
    public Option<int> SelectedInterval { get => _interval; set => Edit(ref _interval, value); }

    public bool CanEdit => _connected && !_unsupported && _requestEpoch is null && Observed is { Admitted: true };
    public bool CanEnable => CanEdit && _choice is not null;
    public bool CanDisable => CanEdit && Observed is { Settings.Enabled: true };
    public bool CanPreview => CanEdit && _choice is not null && !_previewing;
    public string EnableLabel => NeedsRebind ? RebindLabel : Observed is { Settings.Enabled: true } ? "保存并保持开启" : "保存并开启键盘动画";

    /// <summary>The fresh Host status reports the saved, enabled binding as unavailable while other choices are listed. Read-only:
    /// it never selects, saves or previews anything; the hint does not depend on the (possibly edited) selection.</summary>
    public bool NeedsRebind => Observed is { Settings.Enabled: true, State: KeyboardDisplayAnimationStates.Unavailable } s
        && s.Choices.Count > 0 && !s.Choices.Any(c => c.AssetId == s.Settings.AssetId && c.TimelineId == s.Settings.TimelineId);
    public string RebindHint => NeedsRebind ? RebindNote : string.Empty;

    public string StateTitle => Observed is { } s ? Title(s.State) : Requesting ? "正在等待后台答复"
        : !_connected ? "后台未连接" : _unsupported ? "当前后台不支持键盘动画" : "键盘动画状态未知";
    public string StatusLine => Observed is { } s
        ? string.Create(CultureInfo.InvariantCulture,
            $"已请求：{(s.Settings.Enabled ? "开启" : "关闭")} · 主机准许：{(s.Admitted ? "是" : "否")} · 已选帧 {s.Selected} · 已确认上传 {s.Acknowledged} · 门控跳过 {s.Skipped} · 缓存 {s.CachedFrames}/{s.SourceFrames} 帧")
            + (s.UpdatedAt is { } at ? $" · 更新于 {at.ToLocalTime():HH:mm:ss}" : string.Empty)
        : "后台状态未读取 · 不使用上次状态判断";
    public string Detail => Observed?.Detail ?? string.Empty;
    public string IntervalNote => string.Create(CultureInfo.InvariantCulture,
        $"最多每 {_interval.Value / 1000} 秒更新一次；不是帧率，后台不测量也不承诺可见帧率。");
    public string Message => _status is not null && Observed is null ? ExpiredMessage : _message;
    public IReadOnlyList<PreviewFrame> Preview => _preview;
    public bool HasPreview => _preview.Count > 0;
    public string PreviewMessage => _previewMessage;

    private KeyboardDisplayAnimationStatusDto? Observed => _observedAt is { } at && Fresh(at) ? _status : null;
    private bool Requesting => _requestEpoch == Volatile.Read(ref _epoch);

    /// <summary>Bounded read-only status poll. An old Host is not probed again on the same connection.</summary>
    public async Task RefreshAsync()
    {
        ExpireObservation();
        var epoch = Volatile.Read(ref _epoch);
        if (!_connected || _unsupported || _requestEpoch == epoch) return;
        var version = Interlocked.Increment(ref _version);
        var (status, error) = await ReadAsync(_service).ConfigureAwait(true);
        if (Unsupported(error)) PostUnsupported(epoch, version);
        else Post(epoch, version, status, status is null ? Unread(error) : "以上为后台回读的状态；开启请求不等于正在播放。");
    }

    /// <summary>One explicit click: at most one save request on one captured proxy, then (if unanswered) one readback.</summary>
    public async Task SaveAsync(bool enable)
    {
        var epoch = Volatile.Read(ref _epoch);
        ExpireObservation();
        if (enable ? !CanEnable : !CanDisable) return;
        var saved = Observed!.Settings;
        var settings = enable
            ? new KeyboardDisplayAnimationSettingsDto(true, _choice!.AssetId, _choice.TimelineId, _fit.Value, _encoding.Value, _interval.Value)
            : saved with { Enabled = false }; // disabling keeps the saved binding and options
        IKeyboardDisplayService proxy;
        try { proxy = _service(); }
        catch (InvalidOperationException ex)
        {
            Post(epoch, Interlocked.Increment(ref _version), null, Unread(ex) + "未发送请求。");
            return;
        }
        _requestEpoch = epoch;
        var draft = _draft;
        var action = enable ? "保存并开启键盘动画" : "停用键盘动画";
        try
        {
            var version = Interlocked.Increment(ref _version);
            Post(epoch, version, null, $"正在请求{action}，等待后台答复…");
            var read = $"{action}结果未确认：未收到后台答复，未自动重发；以上为回读状态。";
            var unread = $"{action}结果未知：未收到后台答复，状态也未读到；未自动重发，请待回读后再操作。";
            try
            {
                var answer = await BoundAsync(ct => proxy.SetKeyboardDisplayAnimationAsync(settings, ct), _writeTimeout).ConfigureAwait(true);
                if (Current(epoch, version))
                {
                    Post(epoch, version, answer, $"后台已答复{action}请求：以上为后台返回的状态。");
                    _ui.Post(() => { if (_draft == draft) _dirty = false; });
                    return;
                }
            }
            catch (Exception ex) when (Unsupported(ex))
            {
                PostUnsupported(epoch, version);
                return;
            }
            catch (RemoteInvocationException ex)
            {
                // A refusal (for example an asset no longer in the scene) changed nothing; read back rather than assume.
                read = $"后台拒绝了{action}请求（见错误提示），以上为回读状态。";
                unread = $"后台拒绝了{action}请求，状态也未读到。";
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
                if (_connected) await RefreshAsync().ConfigureAwait(true);
                return;
            }
            var (observed, error) = await ReadAsync(() => proxy).ConfigureAwait(true);
            if (Unsupported(error)) PostUnsupported(epoch, version);
            else Post(epoch, version, observed, observed is null ? unread : read);
        }
        finally
        {
            if (_requestEpoch == epoch) _requestEpoch = null;
            _ui.Post(Notify);
        }
    }

    /// <summary>Asks the Host for a few packed frames of the selected asset/fit/encoding and decodes exactly those bytes.</summary>
    public async Task PreviewAsync()
    {
        var epoch = Volatile.Read(ref _epoch);
        ExpireObservation();
        if (!CanPreview) return;
        var request = new KeyboardDisplayAnimationPreviewRequestDto(_choice!.AssetId, _choice.TimelineId, _fit.Value, _encoding.Value);
        var draft = _draft;
        IKeyboardDisplayService proxy;
        try { proxy = _service(); }
        catch (InvalidOperationException ex) { PostPreview(epoch, draft, [], Unread(ex)); return; }
        _previewing = true; Notify();
        try
        {
            var answer = await BoundAsync(ct => proxy.PreviewKeyboardDisplayAnimationAsync(request, ct), _previewTimeout).ConfigureAwait(true);
            if (answer.Width != KeyboardOledFormat.Width || answer.Height != KeyboardOledFormat.Height || answer.Encoding != request.Encoding)
                throw new System.IO.InvalidDataException("预览尺寸或编码与请求不符。");
            PostPreview(epoch, draft, [.. answer.Frames.Select(Decode)], answer.Note);
        }
        catch (Exception ex) when (Unsupported(ex)) { PostUnsupported(epoch, Interlocked.Increment(ref _version)); }
        catch (RemoteInvocationException ex) { PostPreview(epoch, draft, [], "预览失败：" + ex.Message); }
        catch (Exception ex) when (ex is FormatException or System.IO.InvalidDataException or ArgumentException) { PostPreview(epoch, draft, [], "预览数据无效：" + ex.Message); }
#pragma warning disable CA1031 // A preview is read-only; a deadline or lost connection only leaves it empty.
        catch (Exception ex) when (ex is not OutOfMemoryException) { PostPreview(epoch, draft, [], "预览未完成（超时或连接中断）。"); }
#pragma warning restore CA1031
        finally { _previewing = false; _ui.Post(Notify); }
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        Interlocked.Increment(ref _epoch);
        _status = null; _observedAt = null; _unsupported = false; _dirty = false; _preview = [];
        _message = connected ? "等待读取键盘动画状态…" : "后台未连接，键盘动画状态未知；重新连接后再读取。";
        Notify();
    }

    public void ExpireObservation()
    {
        if (_observedAt is not { } at || Fresh(at)) return;
        _status = null; _observedAt = null;
        _message = ExpiredMessage;
        Notify();
    }

    /// <summary>Unpacks one Host frame (low nibble = even pixel) and shows level L as gray L*17.</summary>
    internal static PreviewFrame Decode(KeyboardDisplayAnimationPreviewFrameDto frame)
    {
        var packed = Convert.FromBase64String(frame.PackedBase64);
        if (packed.Length != KeyboardOledFormat.PackedBytes) throw new System.IO.InvalidDataException("预览帧长度不是 6656 字节。");
        var levels = new byte[KeyboardOledFormat.Pixels];
        KeyboardOledFormat.Unpack(packed, levels);
        var gray = new byte[levels.Length];
        for (var i = 0; i < levels.Length; i++) gray[i] = (byte)(levels[i] * 17);
        var image = BitmapSource.Create(KeyboardOledFormat.Width, KeyboardOledFormat.Height, 96, 96, PixelFormats.Gray8, null, gray, KeyboardOledFormat.Width);
        image.Freeze();
        return new(frame.PositionMs, string.Create(CultureInfo.InvariantCulture, $"循环内 {frame.PositionMs} ms"), image, levels);
    }

    private void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        // A list refresh may clear a combo box selection; that is not a user edit.
        if (value is null || !SetProperty(ref field, value, name)) return;
        _dirty = true; _draft++; _preview = [];
        _previewMessage = "设置已改变；需要时重新生成预览。";
        Notify();
    }

    private async Task<(KeyboardDisplayAnimationStatusDto? Status, Exception? Error)> ReadAsync(Func<IKeyboardDisplayService> service)
    {
        try { return (await BoundAsync(ct => service().GetKeyboardDisplayAnimationAsync(ct), _readTimeout).ConfigureAwait(true), null); }
#pragma warning disable CA1031 // Optional animation status cannot invalidate the keyboard status page or case readback.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex); }
#pragma warning restore CA1031
    }

    private static async Task<T> BoundAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan limit)
    {
        using var bound = new CancellationTokenSource(limit);
        return await call(bound.Token).WaitAsync(bound.Token).ConfigureAwait(true);
    }

    private void Post(long epoch, long version, KeyboardDisplayAnimationStatusDto? status, string message) => _ui.Post(() =>
    {
        if (!Current(epoch, version)) return;
        _status = status; _observedAt = status is null ? null : _now(); _message = message;
        if (status is not null && !_dirty) AdoptSaved(status);
        Notify();
    });

    private void PostPreview(long epoch, long draft, IReadOnlyList<PreviewFrame> frames, string message) => _ui.Post(() =>
    {
        // A preview of settings that changed meanwhile, or from another connection, is never shown.
        if (!_connected || epoch != Volatile.Read(ref _epoch) || draft != _draft) return;
        _preview = frames; _previewMessage = message;
        Notify();
    });

    private void PostUnsupported(long epoch, long version) => _ui.Post(() =>
    {
        if (!Current(epoch, version)) return;
        _unsupported = true; _status = null; _observedAt = null; _message = UnsupportedMessage; _preview = [];
        Notify();
    });

    private void ReportHostError(long epoch, RemoteInvocationException ex) =>
        _ui.Post(() => { if (_connected && epoch == Volatile.Read(ref _epoch)) _error(ex); });

    // The editor follows the Host's saved settings until the user changes something.
    private void AdoptSaved(KeyboardDisplayAnimationStatusDto status)
    {
        var s = status.Settings;
        _choice = status.Choices.FirstOrDefault(c => c.AssetId == s.AssetId && c.TimelineId == s.TimelineId)
            ?? status.Choices.FirstOrDefault(c => c.AssetId == s.AssetId)
            ?? (status.Choices.Count > 0 ? status.Choices[0] : null);
        _fit = Fits.FirstOrDefault(f => f.Value == s.Fit) ?? Fits[0];
        _encoding = Encodings.FirstOrDefault(e => e.Value == s.Encoding) ?? Encodings[0];
        var rungs = Intervals;
        _interval = rungs.FirstOrDefault(i => i.Value == s.IntervalMs) ?? rungs.FirstOrDefault(i => i.Value >= s.IntervalMs) ?? (rungs.Count > 0 ? rungs[^1] : Interval(status.MinimumIntervalMs));
    }

    private bool Fresh(DateTimeOffset observedAt)
    {
        var age = _now() - observedAt;
        return age >= TimeSpan.Zero && age <= Freshness;
    }

    private bool Current(long epoch, long version) => _connected && epoch == Volatile.Read(ref _epoch) && version == Volatile.Read(ref _version);

    private static bool Unsupported(Exception? error) => error is RemoteMethodNotFoundException or NotSupportedException;

    private static Option<int> Interval(int ms) => new(ms, string.Create(CultureInfo.InvariantCulture, $"最多每 {ms / 1000} 秒一次"));

    private void Notify()
    {
        foreach (var name in (string[])[nameof(Status), nameof(IsUnsupported), nameof(Choices), nameof(HasChoices), nameof(SelectedChoice),
            nameof(SelectedFit), nameof(SelectedEncoding), nameof(Intervals), nameof(SelectedInterval), nameof(CanEdit), nameof(CanEnable),
            nameof(CanDisable), nameof(CanPreview), nameof(EnableLabel), nameof(NeedsRebind), nameof(RebindHint), nameof(StateTitle), nameof(StatusLine), nameof(Detail),
            nameof(IntervalNote), nameof(Message), nameof(Preview), nameof(HasPreview), nameof(PreviewMessage)])
            OnPropertyChanged(name);
        EnableCommand.RaiseCanExecuteChanged(); DisableCommand.RaiseCanExecuteChanged(); PreviewCommand.RaiseCanExecuteChanged();
    }

    private static string Title(string state) => state switch
    {
        KeyboardDisplayAnimationStates.Off => "未开启（默认关闭）",
        KeyboardDisplayAnimationStates.NotAdmitted => "主机未准许键盘动画（默认关闭）",
        KeyboardDisplayAnimationStates.SettingsError => "动画设置无法读取：已按关闭处理",
        KeyboardDisplayAnimationStates.SuppressedByNativeMenu => "已被原生光域菜单抑制：不发送动画（偏好已保留）",
        KeyboardDisplayAnimationStates.PageOff => "键盘状态屏已关闭：显示 ROG 原动画",
        KeyboardDisplayAnimationStates.Unavailable => "已请求开启：所选素材不在当前场景",
        KeyboardDisplayAnimationStates.Preparing => "已请求开启：正在后台准备画面",
        KeyboardDisplayAnimationStates.Error => "准备失败：显示状态屏",
        KeyboardDisplayAnimationStates.Ready => "已准备：尚未发送动画画面",
        KeyboardDisplayAnimationStates.Fallback => "共享时间轴不可用：显示状态屏",
        KeyboardDisplayAnimationStates.BreakerTripped => "上传超出预算已停止：显示状态屏",
        KeyboardDisplayAnimationStates.Playing => "按共享时间轴选帧（以后台回读为准）",
        _ => "未知状态：不能确认是否在播放",
    };

    private static string Unread(Exception? error) => error switch
    {
        HostNotConnectedException => "后台未连接，键盘动画状态未知。",
        HostAccessBlockedException => "界面已停止向后台发送操作，键盘动画状态未知。",
        RemoteInvocationException => "后台返回错误，键盘动画状态未读到；稍后自动重试读取。",
        _ => "键盘动画状态未读到；稍后自动重试读取。",
    };
}
