using System.Globalization;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// The local keyboard OLED animation of the stock/legacy owner: a default-off sidecar preference that binds one screen asset of
/// the current screen scene to the SAME shared <see cref="IScreenTimeline"/> the case screens and package RGB play. Frames are
/// prepared off the owner path (bounded bytes/time/cancellation, all or nothing) by one live media worker shared with the
/// preview: a cancelled decode that has not ended holds back the next, and only the latest binding waits. The owner's display
/// <c>latest</c> calls <see cref="Select"/>, which is the only cadence gate (so the upload check and the lease wake agree and an
/// idle lease never spins). Installed native-menu ownership suppresses it entirely; the native artwork, lever and status pixels are untouched.
/// </summary>
internal sealed class KeyboardDisplayAnimation : IDisposable
{
    internal const string FileName = "keyboard-display-animation.json";
    private const int PreviewFrames = 4;
    private const int PreviewTimeoutMs = 10_000;

    private enum Decision { None, Inactive, Unbound, Blocked, Gated, Playing }

    private sealed record Plan(KeyboardOledFrames Frames, string TimelineId, int IntervalMs, long IntervalTicks, long MaxUploadTicks);
    private sealed record SettingsFile(int Schema, bool Enabled, string? AssetId, string? TimelineId, string Fit, string Encoding, int IntervalMs);
    private readonly record struct Key(string AssetId, string Fit, string Encoding);

    /// <summary>One keyboard media request (Key null = a preview). It holds the one worker until its media task has completed,
    /// which is only after the actual decode/encode has ended; a cancelled request keeps it until then.</summary>
    private sealed record Work(Key? Key, long Generation, CancellationTokenSource Cancel);

    private readonly KeyboardDisplayOptions _options;
    private readonly IKeyboardOledMedia? _media;
    private readonly IScreenTimeline? _timeline;
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly object _gate = new();

    // guarded by _gate (RPC, monitor and preparation completion; never the owner path)
    private KeyboardDisplayAnimationSettingsDto _settings;
    private string? _settingsError;
    private IReadOnlyList<KeyboardDisplayAnimationChoiceDto> _choices = [];
    private KeyboardOledFrames? _frames;
    private Key? _failed, _pending;
    private Work? _work;
    private long _generation;
    private string? _prepareError;
    private bool _disposed;

    // published to the owner
    private Plan? _plan;
    private string? _breaker;

    // owner actor only (Select / Uploaded / EndSession run sequentially on the one OLED session)
    private Plan? _gatePlan;
    private byte[]? _held, _observed, _lastResult;
    private long _nextAllowed, _selectedAt;
    private ScreenSceneDto? _checkedScene;
    private Plan? _checkedPlan;
    private bool _bound;

    // diagnostics, read by Status
    private int _decision;
    private string? _blocked;
    private long _selected, _acknowledged, _skipped, _lastUploadMs = -1;

    public KeyboardDisplayAnimation(KeyboardDisplayOptions options, string dataDirectory, IKeyboardOledMedia? media,
        IScreenTimeline? timeline, TimeProvider? time = null)
    {
        _options = options;
        _media = media;
        _timeline = timeline;
        _time = time ?? TimeProvider.System;
        _path = Path.Combine(dataDirectory, "peripherals", FileName);
        _settings = Defaults;
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<SettingsFile>(KeyboardDisplayService.ReadBytes(_path))
                    ?? throw new InvalidDataException("Missing keyboard animation settings.");
                if (saved.Schema != 1) throw new InvalidDataException("Unknown keyboard animation settings schema.");
                var dto = new KeyboardDisplayAnimationSettingsDto(saved.Enabled, saved.AssetId, saved.TimelineId, saved.Fit, saved.Encoding, saved.IntervalMs);
                if (Invalid(dto, 1000) is { } why) throw new InvalidDataException(why);
                _settings = dto;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Animation stays off; the unreadable file is kept as it is until an explicit save replaces it.
            _settings = Defaults;
            _settingsError = "键盘动画设置无法读取；动画保持关闭，原文件未被覆盖";
        }
    }

    private KeyboardDisplayAnimationSettingsDto Defaults => new(false, null, null, "contain", KeyboardDisplayAnimationEncodings.LumaRound, MinimumIntervalMs);

    internal string? AdmissionProblem => _options.AnimationAdmissionProblem
        ?? (_media is null ? "no keyboard media service" : _timeline is null ? "no shared timeline" : null);

    internal int MinimumIntervalMs => _options.AnimationMinimumIntervalMs is >= 1000 and <= KeyboardDisplayOptions.AnimationMaximumIntervalMs
        ? _options.AnimationMinimumIntervalMs : 2000;

    /// <summary>True when the monitor should refresh choices and preparation (admitted, requested, readable settings).</summary>
    internal bool Wanted { get { lock (_gate) return AdmissionProblem is null && _settingsError is null && _settings.Enabled; } }

    // ---- owner path ----

    /// <summary>
    /// The display bytes for this owner turn: <paramref name="status"/> unless an admitted, requested, prepared, bound animation
    /// is playable on a known unblocked shared timeline under the stock owner; then the frame for the timeline position, held
    /// until the cadence gate opens. I/O-free, lock-free and allocation-free here (the timeline provider returns one sample).
    /// </summary>
    public byte[] Select(byte[] status, bool pageEnabled, bool nativeOwnsDisplay)
    {
        var plan = Volatile.Read(ref _plan);
        if (plan is null || !pageEnabled || nativeOwnsDisplay || Volatile.Read(ref _breaker) is not null) return Fallback(status, Decision.Inactive);
        if (!Bound(plan)) return Fallback(status, Decision.Unbound);
        var sample = _timeline!.Position(plan.TimelineId);
        if (sample is null || sample.Blocked is not null)
        {
            Volatile.Write(ref _blocked, sample?.Blocked ?? "shared timeline is unavailable");
            return Fallback(status, Decision.Blocked);
        }
        var candidate = plan.Frames.FrameAt(plan.Frames.IndexAt(sample.PositionMs));
        if (!ReferenceEquals(plan, _gatePlan))
        {
            // A different prepared loop never shows a frame held from the previous one.
            if (_gatePlan is null || !ReferenceEquals(plan.Frames, _gatePlan.Frames)) _held = _observed = null;
            _gatePlan = plan;
        }
        var now = _time.GetTimestamp();
        if (_held is not null && (ReferenceEquals(candidate, _held) || now < _nextAllowed))
        {
            if (!ReferenceEquals(candidate, _observed) && !ReferenceEquals(candidate, _held)) Interlocked.Increment(ref _skipped);
            _observed = candidate;
            return Result(_held, Decision.Playing);
        }
        if (now < _nextAllowed) return Result(status, Decision.Gated); // re-entry after a fallback also waits for the gate
        _held = _observed = candidate;
        _selectedAt = now;
        _nextAllowed = now + plan.IntervalTicks;
        Interlocked.Increment(ref _selected);
        return Result(candidate, Decision.Playing);
    }

    /// <summary>
    /// After the owner's acknowledged full upload. True when it was an animation frame: then the gate stays closed for at least
    /// max(interval, selection-to-ACK); a slower-than-budget upload trips the breaker (status page from the next turn, reason
    /// published; no resend, retry, drain or reopen). Unknown outcomes never reach here: they keep the owner's sticky policy.
    /// </summary>
    public bool Uploaded()
    {
        if (_held is not { } held || !ReferenceEquals(_lastResult, held) || _gatePlan is not { } plan) return false;
        Interlocked.Increment(ref _acknowledged);
        var now = _time.GetTimestamp();
        var duration = Math.Max(0, now - _selectedAt);
        var ms = duration * 1000 / _time.TimestampFrequency;
        Interlocked.Exchange(ref _lastUploadMs, ms);
        if (duration > plan.MaxUploadTicks)
        {
            Volatile.Write(ref _breaker, string.Create(CultureInfo.InvariantCulture,
                $"一次完整动画上传（选择到确认）用时 {ms} ms，超过上限 {_options.AnimationMaximumUploadMs} ms；已停止选择新动画帧并回到状态屏，未重发或重试；重新保存动画设置后才会再次尝试"));
            _held = _observed = null;
        }
        _nextAllowed = Math.Max(_nextAllowed, now + Math.Max(plan.IntervalTicks, duration));
        return true;
    }

    /// <summary>The owner session ended: a new session starts at the current shared position, never with a held frame.</summary>
    public void EndSession()
    {
        _gatePlan = null; _held = _observed = _lastResult = null; _nextAllowed = 0; _checkedScene = null; _checkedPlan = null;
        Volatile.Write(ref _decision, (int)Decision.None);
    }

    private byte[] Fallback(byte[] status, Decision decision)
    {
        _held = _observed = null;
        return Result(status, decision);
    }

    private byte[] Result(byte[] value, Decision decision)
    {
        _lastResult = value;
        Volatile.Write(ref _decision, (int)decision);
        return value;
    }

    private bool Bound(Plan plan)
    {
        var scene = _media!.CurrentReferences();
        if (ReferenceEquals(scene, _checkedScene) && ReferenceEquals(plan, _checkedPlan)) return _bound;
        _checkedScene = scene; _checkedPlan = plan; _bound = false;
        if (scene is null) return false;
        var assignments = scene.Assignments;
        for (var i = 0; i < assignments.Count; i++)
        {
            if (assignments[i].BackgroundAssetId == plan.Frames.AssetId && assignments[i].TimelineId == plan.TimelineId) { _bound = true; break; }
        }
        return _bound;
    }

    // ---- control path (RPC / monitor) ----

    /// <summary>Reads the current screen scene and assets (bounded) and reconciles preparation. Never throws but cancellation.</summary>
    public async Task RefreshAsync(IScreenService screens, CancellationToken ct)
    {
        if (AdmissionProblem is not null) return;
        IReadOnlyList<KeyboardDisplayAnimationChoiceDto> choices;
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct); bound.CancelAfter(5000);
            var scene = await screens.GetScreenSceneAsync(bound.Token).ConfigureAwait(false);
            var assets = await screens.ListScreenAssetsAsync(bound.Token).ConfigureAwait(false);
            choices = Choices(scene, assets);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested) { return; }
        lock (_gate)
        {
            _choices = choices;
            Reconcile();
        }
    }

    public async Task<KeyboardDisplayAnimationStatusDto> SaveAsync(KeyboardDisplayAnimationSettingsDto value, IScreenService screens,
        bool pageEnabled, bool nativeOwnsDisplay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Invalid(value, MinimumIntervalMs) is { } why) throw new ArgumentException(why, nameof(value));
        if (value.Enabled)
        {
            if (AdmissionProblem is { } problem) throw new InvalidOperationException("主机未准许键盘动画（" + problem + "）；设置未保存");
            await RefreshAsync(screens, ct).ConfigureAwait(false);
            lock (_gate)
                if (!_choices.Any(c => c.AssetId == value.AssetId && c.TimelineId == value.TimelineId))
                    throw new InvalidOperationException("所选素材不在当前场景的共享时间轴上；设置未保存");
        }
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Write(new SettingsFile(1, value.Enabled, value.AssetId, value.TimelineId, value.Fit, value.Encoding, value.IntervalMs));
            _settings = value;
            _settingsError = null;
            _failed = null; _prepareError = null;
            Volatile.Write(ref _breaker, null); // an explicit save is the only re-arm
            Reconcile();
        }
        return Status(pageEnabled, nativeOwnsDisplay);
    }

    public async Task<KeyboardDisplayAnimationPreviewDto> PreviewAsync(KeyboardDisplayAnimationPreviewRequestDto request, IScreenService screens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Invalid(new(true, request.AssetId, request.TimelineId, request.Fit, request.Encoding, MinimumIntervalMs), MinimumIntervalMs) is { } why)
            throw new ArgumentException(why, nameof(request));
        if (AdmissionProblem is { } problem) throw new InvalidOperationException("主机未准许键盘动画（" + problem + "）");
        await RefreshAsync(screens, ct).ConfigureAwait(false);
        lock (_gate)
            if (!_choices.Any(c => c.AssetId == request.AssetId && c.TimelineId == request.TimelineId))
                throw new InvalidOperationException("所选素材不在当前场景的共享时间轴上");
        var work = new Work(null, 0, CancellationTokenSource.CreateLinkedTokenSource(ct));
        lock (_gate)
        {
            if (_disposed || _work is not null)
            {
                work.Cancel.Dispose();
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Preview and preparation share the one worker: a busy refusal starts no second decoder.
                throw new InvalidOperationException(_work!.Key is null ? "上一个键盘预览仍在生成"
                    : "键盘画面正在后台准备或等待已取消的解码结束；预览未开始，请稍后再试");
            }
            _work = work;
        }
        try
        {
            work.Cancel.CancelAfter(PreviewTimeoutMs + 5000);
            var samples = await _media!.SampleKeyboardOledAsync(request.AssetId, request.Fit, request.Encoding, _options.AnimationGridFloorMs,
                PreviewFrames, PreviewTimeoutMs, work.Cancel.Token).ConfigureAwait(false);
            work.Cancel.Token.ThrowIfCancellationRequested(); // samples completed after cancellation or the preview bound are not accepted
            return new(KeyboardOledFormat.Width, KeyboardOledFormat.Height, request.Encoding,
                [.. samples.Select(s => new KeyboardDisplayAnimationPreviewFrameDto(s.PositionMs, Convert.ToBase64String(s.Packed)))],
                "实际打包的 4 位灰度上传数据（低半字节为偶数像素）；位置为素材循环内毫秒；不代表键盘刷新率");
        }
        catch (KeyboardOledPrepareException ex) { throw new InvalidOperationException(ex.Message, ex); }
        finally
        {
            lock (_gate)
            {
                _work = null; // the media task completes only after its actual work has ended
                Reconcile(); // a binding saved meanwhile is prepared now
            }
            work.Cancel.Dispose();
        }
    }

    public KeyboardDisplayAnimationStatusDto Status(bool pageEnabled, bool nativeOwnsDisplay)
    {
        KeyboardDisplayAnimationSettingsDto settings;
        string? settingsError, prepareError;
        IReadOnlyList<KeyboardDisplayAnimationChoiceDto> choices;
        KeyboardOledFrames? frames;
        bool preparing, retiring, previewing, waiting;
        lock (_gate)
        {
            settings = _settings; settingsError = _settingsError; prepareError = _prepareError;
            choices = _choices; frames = _frames;
            preparing = _work is { Key: not null } w && w.Generation == _generation;
            retiring = _work is { Key: not null } && !preparing;
            previewing = _work is { Key: null };
            waiting = _pending is not null;
        }
        var admission = AdmissionProblem;
        var plan = Volatile.Read(ref _plan);
        var breaker = Volatile.Read(ref _breaker);
        var decision = (Decision)Volatile.Read(ref _decision);
        var listed = choices.Any(c => c.AssetId == settings.AssetId && c.TimelineId == settings.TimelineId);
        var interval = Interval(settings);
        var (state, detail) =
            settingsError is not null ? (KeyboardDisplayAnimationStates.SettingsError, settingsError)
            : admission is not null ? (KeyboardDisplayAnimationStates.NotAdmitted, "主机未准许键盘动画（" + admission + "）；已保存的偏好保持不变，键盘显示状态屏")
            : !settings.Enabled ? (KeyboardDisplayAnimationStates.Off, "键盘动画未开启；键盘显示状态屏")
            : nativeOwnsDisplay ? (KeyboardDisplayAnimationStates.SuppressedByNativeMenu,
                "已安装的原生光域菜单拥有键盘屏幕输出：不发送动画画面，原生状态画面与拨杆不受影响；绑定与开启偏好已保留")
            : !pageEnabled ? (KeyboardDisplayAnimationStates.PageOff, "键盘状态屏已关闭，键盘显示 ROG 原动画；开启状态屏后才会播放")
            : !listed ? (KeyboardDisplayAnimationStates.Unavailable, BindingUnavailableDetail(settings, choices))
            : prepareError is not null ? (KeyboardDisplayAnimationStates.Error, prepareError + "；显示状态屏，重新保存设置后才会再次准备")
            : breaker is not null ? (KeyboardDisplayAnimationStates.BreakerTripped, breaker)
            : preparing || plan is null ? (KeyboardDisplayAnimationStates.Preparing,
                !waiting ? "正在后台准备键盘画面；显示状态屏"
                : previewing ? "键盘预览正在生成；结束后才准备当前绑定；显示状态屏"
                : "上一次键盘画面准备已取消，正在等待其底层解码结束（不强行终止）；结束后才准备当前绑定；显示状态屏")
            : decision == Decision.Playing ? (KeyboardDisplayAnimationStates.Playing, string.Create(CultureInfo.InvariantCulture,
                $"按共享场景时间轴选帧；最多每 {interval / 1000.0:0.#} 秒更新一次（不是帧率）"))
            : decision == Decision.Blocked ? (KeyboardDisplayAnimationStates.Fallback, "共享时间轴不可用（" + Volatile.Read(ref _blocked) + "）；显示状态屏")
            : decision == Decision.Gated ? (KeyboardDisplayAnimationStates.Ready, "已准备；等待更新间隔后恢复动画，当前显示状态屏")
            : decision == Decision.Unbound ? (KeyboardDisplayAnimationStates.Unavailable, BindingUnavailableDetail(settings, choices))
            : (KeyboardDisplayAnimationStates.Ready, string.Create(CultureInfo.InvariantCulture, $"已准备 {plan.Frames.Count} 帧；等待键盘状态屏运行"));
        if (retiring && !waiting) detail += "；已取消的键盘画面准备仍在等待底层解码结束";
        if (Interlocked.Read(ref _lastUploadMs) is >= 0 and var last && state is KeyboardDisplayAnimationStates.Playing)
            detail += string.Create(CultureInfo.InvariantCulture, $"；最近一次动画上传（选择到确认）{last} ms");
        return new(admission is null, settings, state, detail, admission is null ? choices : [], MinimumIntervalMs,
            KeyboardDisplayOptions.AnimationMaximumIntervalMs, frames?.SourceFrames ?? 0, frames?.Count ?? 0, frames?.Bytes ?? 0,
            Interlocked.Read(ref _selected), Interlocked.Read(ref _acknowledged), Interlocked.Read(ref _skipped),
            _time.GetUtcNow(), state == KeyboardDisplayAnimationStates.Error ? prepareError : settingsError);
    }

    private static string BindingUnavailableDetail(KeyboardDisplayAnimationSettingsDto settings, IReadOnlyList<KeyboardDisplayAnimationChoiceDto> choices)
    {
        // Only parseable package timeline ids are compared; anything else keeps the generic text. No display state is claimed.
        if (choices.Any(c => c.AssetId == settings.AssetId && c.TimelineId == settings.TimelineId))
            return "键盘动画绑定暂不可用：最近一次检查发现，当前屏幕场景已不含所选素材与已保存共享时间轴的组合（可选素材列表尚未更新）；已保存的键盘设置未改变；场景未恢复时需要明确重新绑定";
        var newBinding = false;
        var otherPackage = false;
        if (settings.AssetId is not null && ScenePackageIds.TryParse(settings.TimelineId, out var savedContent, out var savedBinding))
        {
            foreach (var choice in choices)
            {
                if (choice.AssetId != settings.AssetId || !ScenePackageIds.TryParse(choice.TimelineId, out var content, out var binding)) continue;
                if (content != savedContent) otherPackage = true;
                else if (binding != savedBinding) newBinding = true;
            }
        }
        return newBinding ? "键盘动画绑定不可用：所选素材仍在当前场景中，但同一场景包已改用新的绑定快照，共享时间轴随之改变；已保存的键盘设置未改变；需要明确重新绑定"
            : otherPackage ? "键盘动画绑定不可用：所选素材现在位于另一个场景包的共享时间轴上；已保存的键盘设置未改变；需要明确重新绑定"
            : "所选素材不在当前场景的共享时间轴上；显示状态屏";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
            _work?.Cancel.Cancel(); // never waits: the running request ends on its own and disposes its token source
            Volatile.Write(ref _plan, null);
        }
    }

    // ---- preparation (guarded by _gate) ----

    private void Reconcile()
    {
        var s = _settings;
        if (_disposed || AdmissionProblem is not null || _settingsError is not null || !s.Enabled || s.AssetId is null || s.TimelineId is null)
        {
            Retire();
            _frames = null;
            Volatile.Write(ref _plan, null);
            return;
        }
        var key = new Key(s.AssetId, s.Fit, s.Encoding);
        if (_frames is { } frames && new Key(frames.AssetId, frames.Fit, frames.Encoding) == key)
        {
            var interval = Interval(s);
            var current = Volatile.Read(ref _plan);
            if (current is null || !ReferenceEquals(current.Frames, frames) || current.TimelineId != s.TimelineId || current.IntervalMs != interval)
            {
                var f = _time.TimestampFrequency;
                Volatile.Write(ref _plan, new Plan(frames, s.TimelineId, interval, interval * f / 1000, _options.AnimationMaximumUploadMs * f / 1000));
            }
            return;
        }
        // A different binding never plays the previous loop, even while the new one is prepared.
        Volatile.Write(ref _plan, null);
        _frames = null;
        if (_work is { Key: { } running } busy && running == key && busy.Generation == _generation) return; // already preparing it
        Retire();
        // a refusal is not retried until an explicit save; a binding not in the current scene is not prepared
        if (_failed == key || !_choices.Any(c => c.AssetId == s.AssetId && c.TimelineId == s.TimelineId)) return;
        if (_work is not null) { _pending = key; return; } // only the latest binding waits for the one worker
        var work = new Work(key, ++_generation, new CancellationTokenSource());
        _work = work; _prepareError = null;
        var limits = new KeyboardOledLimits(_options.AnimationCacheBytes, _options.AnimationGridFloorMs, _options.AnimationPrepareTimeoutMs);
        _ = Task.Run(() => PrepareAsync(work, limits));
    }

    private async Task PrepareAsync(Work work, KeyboardOledLimits limits)
    {
        var key = work.Key!.Value;
        KeyboardOledFrames? frames = null;
        string? error = null;
        try { frames = await _media!.PrepareKeyboardOledAsync(key.AssetId, key.Fit, key.Encoding, limits, work.Cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (work.Cancel.IsCancellationRequested) { }
        catch (KeyboardOledPrepareException ex) { error = ex.Message; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = "键盘画面准备失败：" + ex.GetType().Name; }
        if (frames is not null && new Key(frames.AssetId, frames.Fit, frames.Encoding) != key) { frames = null; error = "键盘画面准备结果与请求不符"; }
        try
        {
            lock (_gate)
            {
                _work = null; // the media task completes only after its actual work has ended: the one worker is free
                if (work.Generation == _generation && !_disposed) // a retired generation is dropped, never published
                {
                    if (frames is not null && !work.Cancel.IsCancellationRequested) _frames = frames;
                    else if (error is not null) { _prepareError = error; _failed = key; }
                }
                Reconcile(); // publishes these frames, or starts the latest pending binding
            }
        }
        finally { work.Cancel.Dispose(); }
    }

    /// <summary>Cancels the current preparation and drops the pending binding. The cancelled request keeps the worker until its
    /// actual work has ended (a running decode is never aborted); its late result is dropped.</summary>
    private void Retire()
    {
        _pending = null;
        if (_work is { Key: not null } work && work.Generation == _generation)
        {
            work.Cancel.Cancel();
            _generation++;
        }
    }

    private int Interval(KeyboardDisplayAnimationSettingsDto s) =>
        Math.Clamp(s.IntervalMs, MinimumIntervalMs, KeyboardDisplayOptions.AnimationMaximumIntervalMs);

    private void Write(SettingsFile value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 2048) throw new ArgumentException("Keyboard animation settings exceed bound.");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, _path, true);
    }

    private static string? Invalid(KeyboardDisplayAnimationSettingsDto s, int minimumIntervalMs) =>
        s.Fit is not ("contain" or "cover") ? "适配方式必须是 contain 或 cover"
        : !KeyboardDisplayAnimationEncodings.IsKnown(s.Encoding) ? "未知的灰度编码"
        : s.IntervalMs < minimumIntervalMs || s.IntervalMs > KeyboardDisplayOptions.AnimationMaximumIntervalMs
            ? string.Create(CultureInfo.InvariantCulture, $"更新间隔必须在 {minimumIntervalMs}..{KeyboardDisplayOptions.AnimationMaximumIntervalMs} ms")
        : s.AssetId is not null && !IsAssetId(s.AssetId) ? "素材标识无效"
        : s.TimelineId is not null && !ScenePackageIds.TryParse(s.TimelineId, out _, out _) ? "共享时间轴标识无效"
        : s.Enabled && (s.AssetId is null || s.TimelineId is null) ? "开启动画需要先选择当前场景中的素材"
        : null;

    private static bool IsAssetId(string id) => id.Length == 16 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Assignments of the current screen scene with a shared TimelineId and a stored asset, one per (asset, timeline).</summary>
    internal static IReadOnlyList<KeyboardDisplayAnimationChoiceDto> Choices(ScreenSceneDto scene, IReadOnlyList<ScreenAssetDto> assets)
    {
        var byId = assets.Where(a => IsAssetId(a.Id)).GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return [.. scene.Assignments
            .Where(a => a.BackgroundAssetId is not null && a.TimelineId is not null && ScenePackageIds.TryParse(a.TimelineId, out _, out _)
                && byId.ContainsKey(a.BackgroundAssetId))
            .OrderBy(a => a.ScreenId, StringComparer.Ordinal)
            .DistinctBy(a => (a.BackgroundAssetId, a.TimelineId))
            .Select(a =>
            {
                var asset = byId[a.BackgroundAssetId!];
                return new KeyboardDisplayAnimationChoiceDto(asset.Id, a.TimelineId!, a.ScreenId, asset.Name, asset.FrameCount, asset.DurationMs, asset.Kind);
            })];
    }
}
