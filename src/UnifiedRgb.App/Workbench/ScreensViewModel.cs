using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

public sealed record ScreenChoice(ScreenDeviceDto Dto)
{
    public string Id => Dto.Id;
    public string Name => Dto.Family switch { "H2" => "冷头屏 · H2", "Turzx" => "机箱长条屏", _ => "风扇屏 · " + Dto.Id.Split(':')[^1][^Math.Min(4, Dto.Id.Split(':')[^1].Length)..].ToUpperInvariant() };
    public string Detail => $"{Dto.Width} × {Dto.Height} · {StateName(Dto.State)}";
    public override string ToString() => $"{Name}，{Detail}";
    public static string StateName(string state) => state switch
    {
        "Disabled" => "输出未开启", "Idle" => "待播放", "Running" => "正在发送", "Blocked" => "被其他程序占用",
        "Error" => "通信异常", "Disconnected" => "未连接", "Unsupported" => "协议未匹配", "Stopped" => "已暂停", "Sleeping" => "已休眠 · 背光关闭", _ => state,
    };
}
public sealed record AssetChoice(ScreenAssetDto? Dto)
{
    public string Name => Dto?.Name ?? "纯色背景";
    public string Detail => Dto is null ? "不使用图片" : $"{Dto.Width} × {Dto.Height} · {(Dto.Kind == "animation" ? $"{Dto.FrameCount} 帧动画" : "图片")}";
}
public sealed record ValueChoice(string Id, string Name);

/// <summary>Screen authoring. Import stores local media; preview composes pixels without USB. Apply is explicit.</summary>
public sealed class ScreensViewModel : ObservableObject
{
    private readonly Func<IScreenService> _service;
    private readonly Func<IScreenDeviceSettingsService>? _deviceSettings;
    private int _savedRotation;
    private ValueChoice? _quality;
    private bool _settingsDirty;
    private bool _settingsAvailable;
    private bool _savingSettings;
    private long _settingsVersion;
    private string _settingsNote = "本机安装方向独立保存，切换主题不会重置。";
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _error;
    private readonly Func<string?> _chooseFile;
    private readonly Func<string, bool> _confirm;
    private readonly Func<DateTimeOffset> _now;
    private ScreenSceneDto _baseline = new([]);
    private ScreenChoice? _selected;
    private AssetChoice? _asset;
    private ValueChoice? _overlay;
    private ValueChoice? _fit;
    private string _background = "#101722";
    private string _accent = "#5DDAC8";
    private double _brightness = 100;
    private int _rotation;
    private int _fps = 5;
    private bool _enabled = true;
    private bool _loading;
    private bool _dirty;
    private bool _available;
    private string _message = "正在检查屏幕服务…";
    private string _previewNote = "选择屏幕后，生成内容预览。预览不发送到硬件。";
    private BitmapSource? _preview;
    private string _telemetry = "电脑数据尚未读取";
    private bool _polling;
    private long _draftVersion;
    private bool _restartingLcd;
    private DateTimeOffset _restartCooldownUntil;
    private bool _updatingScreenChoices;
    private long _refreshId;
    private long _connectionEpoch;
    private long _pollingEpoch;
    private int _editorOperations;
    private string _busyText = string.Empty;
    private string _errorText = string.Empty;

    public ScreensViewModel(Func<IScreenService> service, IUiDispatcher ui, Action<Exception> onError, Func<string?> chooseFile, Func<string, bool>? confirm = null, Func<DateTimeOffset>? now = null, Func<IScreenDeviceSettingsService>? deviceSettings = null)
    {
        _service = service; _deviceSettings = deviceSettings; _ui = ui; _error = onError; _chooseFile = chooseFile; _confirm = confirm ?? (_ => true);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        EncodingQualities = [new("legacy", "兼容策略 · 原有编码"), new("lossless-first-v1", "保色优先 · 无损优化")];
        _quality = EncodingQualities[0];
        SaveDeviceSettingsCommand = new AsyncCommand(_ => RunEditorAsync("正在保存本机设置…", SaveDeviceSettingsAsync), Report, _ => CanSaveDeviceSettings);
        Overlays = [new("none", "只显示背景"), new("compact", "精简电脑信息"), new("dashboard", "完整监控面板")];
        Fits = [new("cover", "填满并裁切"), new("contain", "完整显示")];
        _overlay = Overlays[1]; _fit = Fits[0];
        Assets.Add(new(null)); _asset = Assets[0];
        RefreshCommand = new AsyncCommand(() => RunEditorAsync("正在读取屏幕…", RefreshAsync), Report);
        ImportCommand = new AsyncCommand(() => RunEditorAsync("正在导入素材…", ImportAsync), Report);
        PreviewCommand = new AsyncCommand(() => RunEditorAsync("正在生成实际编码预览…", PreviewAsync), Report);
        ApplyCommand = new AsyncCommand(() => RunEditorAsync("正在应用此屏幕内容…", ApplyAsync), Report);
        StopCommand = new AsyncCommand(() => RunEditorAsync("正在暂停屏幕发送…", StopAsync), Report);
        RemoveAssetCommand = new AsyncCommand(() => RunEditorAsync("正在删除所选素材…", DeleteAssetAsync), Report);
        PollCommand = new AsyncCommand(PollAsync, Report);
        RestartLcdCommand = new AsyncCommand(_ => RunEditorAsync("正在恢复风扇 LCD…", RestartLcdAsync), Report, _ => CanRestartLcd);
        DismissErrorCommand = new RelayCommand(() => { ErrorText = string.Empty; ErrorDismissed?.Invoke(this, EventArgs.Empty); });
        Screens.CollectionChanged += (_, _) => { UpdateRestartState(); NotifyEditorState(); };
    }
    public ObservableCollection<ScreenChoice> Screens { get; } = [];
    public ObservableCollection<AssetChoice> Assets { get; } = [];
    public IReadOnlyList<ValueChoice> Overlays { get; }
    public IReadOnlyList<ValueChoice> Fits { get; }
    public IReadOnlyList<ValueChoice> EncodingQualities { get; }
    public AsyncCommand SaveDeviceSettingsCommand { get; }
    public bool DeviceSettingsDirty { get => _settingsDirty; private set { if (SetProperty(ref _settingsDirty, value)) UpdateSettingsState(); } }
    public bool CanSaveDeviceSettings => _settingsAvailable && Available && Selected is not null && DeviceSettingsDirty && !_savingSettings && !_restartingLcd;
    public bool DeviceSettingsAvailable => _settingsAvailable && !_savingSettings;
    public string DeviceSettingsNote { get => _settingsNote; private set => SetProperty(ref _settingsNote, value); }
    public ValueChoice? EncodingQuality { get => _quality; set { if (SetProperty(ref _quality, value)) LocalChanged(); } }
    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RemoveAssetCommand { get; }
    public AsyncCommand PollCommand { get; }
    public AsyncCommand RestartLcdCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public event EventHandler? ErrorDismissed;
    public bool EditorBusy => _editorOperations != 0;
    public bool EditorIdle => !EditorBusy;
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public string ErrorText { get => _errorText; private set { if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorText.Length != 0;
    public bool HasSelection => Selected is not null;
    public bool HasScreens => Screens.Count != 0;
    public bool CanEditContent => HasSelection && EditorIdle;
    public bool CanApplyContent => CanChangeOutput && HasSelection && EditorIdle;
    public bool CanGeneratePreview => Available && HasSelection && EditorIdle;
    public string ContentDraftState => !HasSelection ? "请选择需要编辑的屏幕" : Dirty ? "内容草稿 · 尚未应用" : "内容与上次读取一致";
    public string LocalDraftState => !_settingsAvailable ? "本机设置尚未就绪" : DeviceSettingsDirty ? "本机设置 · 尚未保存" : "本机设置已读取";
    public string ConnectionHint => !Available ? "后台未连接。已有草稿保留，应用与保存暂不可用。"
        : !HasSelection ? "尚未发现屏幕。可以重新检测设备，当前不会发送画面。"
        : Selected!.Dto.State switch
        {
            "Blocked" => "设备被占用。可以准备和预览内容，发送需先解决占用。",
            "Disabled" => "设备输出未开启。预览与内容编辑可用，保存不代表已经播放。",
            "Unsupported" => "设备协议尚未匹配。当前状态不代表已接管。",
            "Disconnected" => "此屏幕已断开。草稿保留，实际发送需等待重新连接。",
            _ => "内容与本机设置分别保存；预览不会发送到设备。",
        };
    public bool Available { get => _available; private set { if (SetProperty(ref _available, value)) { UpdateRestartState(); OnPropertyChanged(nameof(ScreenDetail)); } } }
    public bool CanChangeOutput => Available && !_restartingLcd;
    public bool CanRestartLcd => CanChangeOutput && RestartCooldownSeconds == 0
        && Screens.Any(s => s.Dto.Family == "TlLcd" && s.Dto.State is not ("Disabled" or "Unsupported" or "Blocked"));
    public string RestartLcdText => _restartingLcd ? "正在重启 LCD…" : RestartCooldownSeconds > 0 ? $"重启 LCD · {RestartCooldownSeconds}s" : "重启风扇 LCD";
    public string RestartLcdHint => _restartingLcd ? "底排三块屏幕正在重新连接，完成后恢复原播放状态。"
        : RestartCooldownSeconds > 0 ? "LCD 刚刚重启，请等待倒计时结束后再操作。"
        : "底排三块风扇屏会短暂重启，随后恢复原播放状态。未应用的修改会保留。";
    private int RestartCooldownSeconds => Math.Max(0, (int)Math.Ceiling((_restartCooldownUntil - _now()).TotalSeconds));
    public bool Dirty { get => _dirty; private set { if (SetProperty(ref _dirty, value)) OnPropertyChanged(nameof(ContentDraftState)); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string PreviewNote { get => _previewNote; private set => SetProperty(ref _previewNote, value); }
    public BitmapSource? Preview { get => _preview; private set { if (SetProperty(ref _preview, value)) OnPropertyChanged(nameof(PreviewClip)); } }
    public Geometry? PreviewClip => Preview is { } image && Selected?.Dto.Family is "TlLcd" or "H2"
        ? new EllipseGeometry(new System.Windows.Rect(0, 0, image.PixelWidth, image.PixelHeight)) : null;
    public string Telemetry { get => _telemetry; private set => SetProperty(ref _telemetry, value); }
    public ScreenChoice? Selected
    {
        get => _selected;
        set
        {
            if (_updatingScreenChoices) return;
            if (_selected?.Id == value?.Id) return;
            if ((Dirty || DeviceSettingsDirty) && !_loading && !_confirm("放弃这块屏幕尚未保存的修改并切换？")) { OnPropertyChanged(); return; }
            SetProperty(ref _selected, value); LoadSelected(); OnPropertyChanged(nameof(ScreenDetail)); OnPropertyChanged(nameof(PreviewClip));
            NotifyEditorState();
        }
    }
    public AssetChoice? Asset { get => _asset; set { if (SetProperty(ref _asset, value)) Changed(); } }
    public ValueChoice? Overlay { get => _overlay; set { if (SetProperty(ref _overlay, value)) Changed(); } }
    public ValueChoice? Fit { get => _fit; set { if (SetProperty(ref _fit, value)) Changed(); } }
    public string Background { get => _background; set { if (SetProperty(ref _background, value)) Changed(); } }
    public string Accent { get => _accent; set { if (SetProperty(ref _accent, value)) Changed(); } }
    public double Brightness { get => _brightness; set { if (SetProperty(ref _brightness, value)) Changed(); } }
    public int Rotation { get => _rotation; set { if (SetProperty(ref _rotation, value)) { if (_deviceSettings is null) Changed(); else LocalChanged(); } } }
    public int Fps { get => _fps; set { if (SetProperty(ref _fps, value)) Changed(); } }
    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) Changed(); } }
    public string ScreenDetail => Selected is { } s
        ? $"{(Available ? string.Empty : "上次读取 · ")}{s.Detail}\n{s.Dto.FramesWritten} 次写入 · {s.Dto.Dropped} 次失败 · 实际发送 {s.Dto.EffectiveFps:0.0} fps\n{(s.Dto.ControlVerified ? "实机控制已确认" : "实机控制待确认")}{(s.Dto.Family == "TlLcd" ? " · 物理位置未校准" : "")}\n{s.Dto.BlockedBy ?? s.Dto.LastError}"
        : "尚未发现屏幕。灯光控制与屏幕连接相互独立。";

    public async Task RefreshAsync()
    {
        if ((Dirty || DeviceSettingsDirty) && !_confirm("放弃未保存的屏幕修改并重新读取？")) return;
        var epoch = Volatile.Read(ref _connectionEpoch);
        var revision = _draftVersion; var localRevision = _settingsVersion;
        var request = Interlocked.Increment(ref _refreshId);
        var service = _service();
        var screens = await service.ListScreensAsync().ConfigureAwait(true);
        var assets = await service.ListScreenAssetsAsync().ConfigureAwait(true);
        var scene = await service.GetScreenSceneAsync().ConfigureAwait(true);
        _ui.Post(() =>
        {
            if (!CurrentConnection(epoch) || request != Volatile.Read(ref _refreshId)) return;
            if ((revision != _draftVersion || localRevision != _settingsVersion))
            {
                UpdateScreenChoices(screens); Available = true;
                Message = "连接状态已更新，读取期间的新修改保留在草稿中。";
                return;
            }
            var id = _selected?.Id;
            _loading = true;
            try
            {
                Screens.Clear(); foreach (var s in screens) Screens.Add(new(s));
                Assets.Clear(); Assets.Add(new(null)); foreach (var a in assets) Assets.Add(new(a));
                _baseline = scene; _selected = Screens.FirstOrDefault(s => s.Id == id) ?? Screens.FirstOrDefault(); OnPropertyChanged(nameof(Selected));
                LoadSelected(); Available = true;
                Message = $"已发现 {screens.Count} 块屏幕。输出门槛与实机验收状态见所选屏幕。";
            }
            finally { _loading = false; }
        });
        if (CurrentConnection(epoch)) await PollAsync().ConfigureAwait(true);
    }
    public async Task RefreshOptionalAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        try
        {
            if (Dirty || DeviceSettingsDirty)
            {
                var screens = await _service().ListScreensAsync().ConfigureAwait(true);
                _ui.Post(() => { if (!CurrentConnection(epoch)) return; UpdateScreenChoices(screens); Available = true; Message = "屏幕服务已连接，未应用的修改已保留。"; });
                if (CurrentConnection(epoch)) await PollAsync().ConfigureAwait(true);
            }
            else await RefreshAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // stale RPC failures belong to a disconnected session and must not replace current UI state
        catch (Exception) when (!CurrentConnection(epoch)) { }
#pragma warning restore CA1031
        catch (StreamJsonRpc.RemoteMethodNotFoundException) { _ui.Post(() => { if (!CurrentConnection(epoch)) return; Available = false; Message = "当前后台尚未提供屏幕服务。灯光功能可正常使用。"; }); }
        catch (InvalidOperationException ex) { _ui.Post(() => { if (!CurrentConnection(epoch)) return; Available = false; Message = ex.Message; }); }
    }
    public void MarkDisconnected()
    {
        Interlocked.Increment(ref _connectionEpoch);
        Interlocked.Increment(ref _refreshId);
        Available = false; Message = "后台未连接，当前显示上次读取的屏幕配置。";
    }
    private bool CurrentConnection(long epoch) => epoch == Volatile.Read(ref _connectionEpoch);
    public async Task PollAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        if ((_polling && _pollingEpoch == epoch) || !Available) return;
        _polling = true; _pollingEpoch = epoch;
        try
        {
            var service = _service();
            var data = await service.GetPcTelemetryAsync().ConfigureAwait(true);
            var screens = await service.ListScreensAsync().ConfigureAwait(true);
            _ui.Post(() =>
            {
                if (!CurrentConnection(epoch)) return;
                var age = DateTimeOffset.UtcNow - data.Timestamp;
                Telemetry = age > TimeSpan.FromSeconds(5) ? "电脑数据已过期，等待新采样" : $"CPU {Number(data.CpuUsagePercent, "%")}    内存 {Bytes(data.MemoryUsedBytes)} / {Bytes(data.MemoryTotalBytes)}\nGPU {Number(data.GpuUsagePercent, "%")}    {Number(data.GpuTemperatureC, "°C")}    显存 {Bytes(data.GpuMemoryUsedBytes)} / {Bytes(data.GpuMemoryTotalBytes)}\n{data.Timestamp.ToLocalTime():HH:mm:ss} 更新";
                UpdateScreenChoices(screens);
            });
        }
#pragma warning disable CA1031 // ignore only obsolete-session failures; current-session failures retain normal reporting
        catch (Exception) when (!CurrentConnection(epoch)) { }
#pragma warning restore CA1031
        catch (StreamJsonRpc.RemoteInvocationException ex) { _ui.Post(() => { if (CurrentConnection(epoch)) Message = "屏幕状态读取失败：" + ex.Message; }); }
        catch (InvalidOperationException ex) { _ui.Post(() => { if (CurrentConnection(epoch)) Message = ex.Message; }); }
        finally { if (_pollingEpoch == epoch) _polling = false; _ui.Post(() => { if (CurrentConnection(epoch)) UpdateRestartState(); }); }
    }
    private void UpdateScreenChoices(IReadOnlyList<ScreenDeviceDto> screens)
    {
        var snapshots = screens.ToList();
        if (_selected is { } selected && snapshots.All(s => s.Id != selected.Id))
            snapshots.Add(selected.Dto with { State = "Disconnected", EffectiveFps = 0, BlockedBy = null });
        _updatingScreenChoices = true;
        try
        {
            for (var i = 0; i < snapshots.Count; i++)
            {
                var item = new ScreenChoice(snapshots[i]);
                if (i >= Screens.Count) Screens.Add(item);
                else if (Screens[i].Dto != item.Dto) Screens[i] = item;
            }
            while (Screens.Count > snapshots.Count) Screens.RemoveAt(Screens.Count - 1);
            if (_selected is not null) _selected = Screens.FirstOrDefault(s => s.Id == _selected.Id);
        }
        finally { _updatingScreenChoices = false; }
        OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(ScreenDetail));
        UpdateRestartState();
    }
    private async Task RestartLcdAsync()
    {
        // Recovery acts on the Host's current content. Never apply/reset this editor's draft.
        if (!CanRestartLcd) return;
        var epoch = Volatile.Read(ref _connectionEpoch);
        _restartingLcd = true; UpdateRestartState();
        Message = "正在重启底排风扇 LCD，屏幕会短暂熄灭…";
        try
        {
            var result = await _service().RestartFanLcdAsync().ConfigureAwait(true);
            if (result.Attempts > 0) _restartCooldownUntil = _now().AddSeconds(60);
            if (!CurrentConnection(epoch)) return;
            var outcome = result.Message.Split(':', 2)[0];
            Message = outcome switch
            {
                "Disabled" => "后台尚未启用风扇 LCD 重启，未发送重启指令。",
                "Precheck" => "LCD 重启条件未满足，请检查设备连接、接管状态和屏幕配置。",
                "Busy" => "已有屏幕操作正在进行，请稍后重试。",
                "Cooldown" => "LCD 刚刚重启，请等待冷却时间结束后再试。",
                _ => !result.CommandAccepted
                ? "LCD 重启指令未获接收端确认。当前内容设置已保留，请稍后重试。"
                : result.ScreensReconnected
                    ? "风扇 LCD 已重新连接，原播放状态已恢复。请确认实际画面。"
                    : "已确认 LCD 重启指令，部分屏幕尚未恢复。请查看屏幕连接状态。",
            };
            await PollAsync().ConfigureAwait(true);
        }
        catch (StreamJsonRpc.RemoteMethodNotFoundException)
        {
            if (CurrentConnection(epoch)) Message = "当前后台版本尚不支持重启 LCD，请先更新后台。";
        }
        finally { _restartingLcd = false; UpdateRestartState(); }
    }
    private async Task StopAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        await _service().StopScreensAsync().ConfigureAwait(true);
        if (!CurrentConnection(epoch)) return;
        Message = "屏幕发送已暂停，灯光不变。";
        await PollAsync().ConfigureAwait(true);
    }
    private void UpdateRestartState()
    {
        OnPropertyChanged(nameof(CanChangeOutput)); OnPropertyChanged(nameof(CanRestartLcd));
        OnPropertyChanged(nameof(RestartLcdText)); OnPropertyChanged(nameof(RestartLcdHint));
        RestartLcdCommand.RaiseCanExecuteChanged();
        UpdateSettingsState();
        NotifyEditorState();
    }
    public ScreenAssignmentDto Build()
    {
        if (Selected is null) throw new InvalidOperationException("请先选择屏幕。");
        if (!double.IsFinite(Brightness) || Brightness is < 0 or > 100 || Fps is < 1 or > 15) throw new InvalidOperationException("亮度范围为 0～100%，刷新帧率范围为 1～15。");
        var overlay = Overlay?.Id ?? "none";
        var region = overlay == "dashboard"
            ? _baseline.Assignments.FirstOrDefault(a => a.ScreenId == Selected.Id)?.OverlayLayout
            : null;
        return new(Selected.Id, Asset?.Dto?.Id, Background, overlay, _deviceSettings is null ? Rotation : _savedRotation, Brightness / 100, Fps, Enabled, Accent, Fit?.Id ?? "cover", OverlayLayout: region);
    }
    private void LoadSelected()
    {
        var loading = _loading; _loading = true;
        try
        {
            var a = _baseline.Assignments.FirstOrDefault(a => a.ScreenId == _selected?.Id) ?? new ScreenAssignmentDto(_selected?.Id ?? "screen:h2:offline", Overlay: "compact");
            var asset = Assets.FirstOrDefault(x => x.Dto?.Id == a.BackgroundAssetId);
            if (asset is null && a.BackgroundAssetId is { } missing)
            {
                asset = new(new(missing, "缺失素材 · " + missing, 0, 0, 0, 0, "missing"));
                Assets.Add(asset);
            }
            Asset = asset ?? Assets.FirstOrDefault();
            Background = a.BackgroundColor; Accent = a.AccentColor; Overlay = Overlays.First(o => o.Id == a.Overlay); Fit = Fits.First(f => f.Id == a.Fit);
            _savedRotation = a.Rotation; Rotation = a.Rotation;
            _quality = EncodingQualities[0]; OnPropertyChanged(nameof(EncodingQuality)); DeviceSettingsDirty = false;
            _settingsAvailable = false; _settingsVersion++; UpdateSettingsState();
            Brightness = a.Brightness * 100; Fps = a.Fps; Enabled = a.Enabled;
            Preview = null; PreviewNote = "内容预览尚未生成。预览只合成图片，不发送到硬件。"; Dirty = false;
            OnPropertyChanged(nameof(ScreenDetail));
            _draftVersion++;
        }
        finally { _loading = loading; }
        if (_deviceSettings is not null) _ = ReadDeviceSettingsAsync();
    }
    private async Task ImportAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        var path = _chooseFile();
        if (path is null) return;
        var file = new FileInfo(path);
        if (file.Length > 12 * 1024 * 1024) throw new InvalidOperationException("图片或 GIF 不可超过 12 MiB。");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        if (!CurrentConnection(epoch)) return;
        var version = _draftVersion;
        var selected = Selected?.Id;
        var asset = await _service().UploadScreenAssetAsync(new(file.Name, Convert.ToBase64String(bytes))).ConfigureAwait(true);
        if (!CurrentConnection(epoch)) return;
        var item = Assets.FirstOrDefault(a => a.Dto?.Id == asset.Id) ?? new AssetChoice(asset);
        if (!Assets.Contains(item)) Assets.Add(item);
        if (version == _draftVersion && selected == Selected?.Id)
        {
            Asset = item;
            Message = $"已导入“{asset.Name}”。内容仍是草稿，尚未发送。";
        }
        else Message = $"已导入“{asset.Name}”到素材库，当前草稿保持不变。";
    }
    private async Task PreviewAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        var assignment = Build(); var version = _draftVersion;
        Preview = null; PreviewNote = "正在合成并验证实际编码结果…";
        ScreenPreviewDto preview;
        try { preview = await _service().PreviewScreenAsync(assignment).ConfigureAwait(true); }
        catch
        {
            if (CurrentConnection(epoch) && version == _draftVersion) { Preview = null; PreviewNote = "编码失败，未生成可发送预览。请查看错误信息。"; }
            throw;
        }
        if (!CurrentConnection(epoch)) return;
        if (version != _draftVersion) { PreviewNote = "草稿已修改，请重新生成预览。"; return; }
        var image = new BitmapImage();
        using (var stream = new MemoryStream(Convert.FromBase64String(preview.PngBase64))) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); }
        Preview = image; PreviewNote = (DeviceSettingsDirty ? "本机设置尚未保存；预览使用已保存的方向与质量。\n" : "") + $"{preview.Width} × {preview.Height} · 实际编码预览，未发送\n{preview.Note}";
    }
    private async Task ApplyAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        var a = Build(); var service = _service(); var version = _draftVersion;
        var current = await service.GetScreenSceneAsync().ConfigureAwait(true);
        if (!CurrentConnection(epoch)) return;
        var baseline = _baseline.Assignments.FirstOrDefault(s => s.ScreenId == a.ScreenId);
        if (current.Assignments.FirstOrDefault(s => s.ScreenId == a.ScreenId) != baseline) throw new InvalidOperationException("此屏幕配置已被其他操作修改。请刷新后再应用。");
        await service.SetScreenSceneAsync(new(current.Assignments.Where(s => s.ScreenId != a.ScreenId).Append(a).ToArray())).ConfigureAwait(true);
        if (!CurrentConnection(epoch)) return;
        _baseline = new(current.Assignments.Where(s => s.ScreenId != a.ScreenId).Append(a).ToArray());
        if (version == _draftVersion) Dirty = false;
        Message = version == _draftVersion ? "屏幕内容已保存；实际发送取决于输出门槛与设备状态。" : "已保存点击应用时的内容，后续修改仍留在草稿中。";
        await PollAsync().ConfigureAwait(true);
    }
    private async Task DeleteAssetAsync()
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        if (Asset?.Dto is not { } asset || !_confirm($"删除素材“{asset.Name}”？引用此素材的旧场景之后将无法完整应用。")) return;
        await _service().DeleteScreenAssetAsync(asset.Id).ConfigureAwait(true);
        if (!CurrentConnection(epoch)) return;
        if (Asset?.Dto?.Id == asset.Id) Asset = Assets.FirstOrDefault(a => a.Dto is null);
        foreach (var item in Assets.Where(a => a.Dto?.Id == asset.Id).ToArray()) Assets.Remove(item);
        Message = "素材已删除。";
    }

    private void UpdateSettingsState()
    {
        OnPropertyChanged(nameof(CanSaveDeviceSettings)); OnPropertyChanged(nameof(DeviceSettingsAvailable));
        SaveDeviceSettingsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(LocalDraftState));
    }
    private void LocalChanged()
    {
        if (_loading || _deviceSettings is null) return;
        _settingsVersion++; DeviceSettingsDirty = true;
        if (_settingsAvailable) DeviceSettingsNote = "本机设置尚未保存。应用内容不会保存这两项；预览使用已保存值。";
    }
    public async Task ReadDeviceSettingsAsync()
    {
        if (_deviceSettings is null || Selected is not { } selected) return;
        var epoch = Volatile.Read(ref _connectionEpoch);
        var id = selected.Id; var version = _settingsVersion;
        try
        {
            var value = await _deviceSettings().GetScreenDeviceSettingsAsync(id).ConfigureAwait(true);
            if (!CurrentConnection(epoch) || Selected?.Id != id || version != _settingsVersion || DeviceSettingsDirty) return;
            var loading = _loading; _loading = true;
            try
            {
                _savedRotation = value.Rotation; Rotation = value.Rotation;
                EncodingQuality = EncodingQualities.FirstOrDefault(q => q.Id == value.EncodingQuality) ?? EncodingQualities[0];
                _settingsAvailable = true; DeviceSettingsDirty = false;
                DeviceSettingsNote = "方向与质量仅保存在本机。保色优先用于长条屏 PNG，复杂帧编码较慢；实时信息叠加也会影响缓存与帧率。调色板模式仍待设备验证，暂不提供。";
                UpdateSettingsState();
            }
            finally { _loading = loading; }
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteMethodNotFoundException or StreamJsonRpc.RemoteInvocationException or InvalidOperationException)
        {
            if (!CurrentConnection(epoch) || Selected?.Id != id || version != _settingsVersion) return;
            _settingsAvailable = false;
            DeviceSettingsNote = ex is StreamJsonRpc.RemoteMethodNotFoundException
                ? "当前后台版本尚不支持本机设置，请先更新后台。"
                : "本机设置读取失败：" + ex.Message;
            UpdateSettingsState();
        }
    }
    private async Task SaveDeviceSettingsAsync()
    {
        if (!CanSaveDeviceSettings || Selected is not { } selected || _deviceSettings is null) return;
        var epoch = Volatile.Read(ref _connectionEpoch);
        var id = selected.Id; var revision = _settingsVersion;
        var wanted = new ScreenDeviceSettingsUpdateDto(id, Rotation, EncodingQuality?.Id ?? "legacy");
        _savingSettings = true; UpdateSettingsState();
        try
        {
            var saved = await _deviceSettings().SetScreenDeviceSettingsAsync(wanted).ConfigureAwait(true);
            if (!CurrentConnection(epoch)) return;
            _baseline = new(_baseline.Assignments.Select(a => a.ScreenId == id ? a with { Rotation = saved.Rotation } : a).ToArray());
            if (Selected?.Id == id)
            {
                _savedRotation = saved.Rotation; Preview = null; PreviewNote = "本机设置已保存，请重新生成实际编码预览。";
                if (revision == _settingsVersion) DeviceSettingsDirty = false;
                DeviceSettingsNote = DeviceSettingsDirty ? "点击保存时的设置已保存，后续修改仍留在草稿中。" : "本机方向与编码质量已保存，切换场景和重启后继续使用。";
            }
            Message = "本机屏幕设置已保存，内容草稿保留。";
        }
        finally { _savingSettings = false; UpdateSettingsState(); }
    }

    private void Changed() { if (!_loading) { _draftVersion++; Dirty = true; Preview = null; PreviewNote = "草稿已修改，点击“生成预览”更新画面。"; } }
    private async Task RunEditorAsync(string caption, Func<Task> operation)
    {
        var epoch = Volatile.Read(ref _connectionEpoch);
        _editorOperations++; BusyText = caption; ErrorText = string.Empty;
        OnPropertyChanged(nameof(EditorBusy)); OnPropertyChanged(nameof(EditorIdle));
        NotifyEditorState();
        try { await operation().ConfigureAwait(true); }
#pragma warning disable CA1031 // an obsolete connection cannot overwrite the new session's UI error or drafts
        catch (Exception) when (!CurrentConnection(epoch)) { }
#pragma warning restore CA1031
        finally
        {
            _editorOperations--;
            if (!EditorBusy) BusyText = string.Empty;
            OnPropertyChanged(nameof(EditorBusy)); OnPropertyChanged(nameof(EditorIdle));
            NotifyEditorState();
        }
    }
    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(HasScreens)); OnPropertyChanged(nameof(ConnectionHint));
        OnPropertyChanged(nameof(CanApplyContent)); OnPropertyChanged(nameof(CanGeneratePreview));
        OnPropertyChanged(nameof(CanEditContent)); OnPropertyChanged(nameof(ContentDraftState));
    }
    private void Report(Exception ex) { Message = ex is StreamJsonRpc.RemoteMethodNotFoundException ? "当前后台尚未提供屏幕服务。" : ex.Message; ErrorText = Message; _error(ex); }
    private static string Number(double? value, string suffix) => value is { } n && double.IsFinite(n) ? n.ToString("0", CultureInfo.CurrentCulture) + suffix : "不可用";
    private static string Bytes(long? value) => value is { } n ? $"{n / 1073741824.0:0.0} GB" : "不可用";
}


