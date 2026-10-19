using System.Collections.ObjectModel;
using System.Text.Json;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

public sealed record StudioAssignment(AssignmentDto Dto, int Index)
{
    public string Name => DisplayNames.Scene(Dto.GroupName ?? DisplayNames.Effect(Dto.EffectId));
    public string Detail => Dto.FrameSetId?.StartsWith("package:", StringComparison.Ordinal) == true
        ? $"独立场景包帧内容 · {Dto.Targets.Count} 个分区" : $"{DisplayNames.Effect(Dto.EffectId)} · {Dto.Targets.Count} 个分区 · {DisplayNames.Strategy(Dto.Strategy)}";
}

/// <summary>Unelevated authoring workspace. All edits are local until an explicit apply; reads never send frames.</summary>
public sealed class StudioViewModel : ObservableObject
{
    public const string BeforeLightsOffProfile = "Before Lights Off";
    private static readonly JsonSerializerOptions JsonOptions = ContractsJson.Configure(new JsonSerializerOptions());
    private readonly IHostConnection _client;
    private readonly IUiDispatcher _ui;
    private readonly Func<string, bool> _confirm;
    private SceneDto _scene = new([]);
    private StudioAssignment? _assignment;
    private StudioZone? _zone;
    private string _message = "正在读取机箱状态…";
    private string _clock = "—";
    private string _period = "—";
    private string _health = "等待后台服务";
    private string _written = "—";
    private string _updated = "尚未读取";
    private int _page;
    private bool _preview;
    private long _refreshId;
    private bool _updatingAssignments;

    public StudioViewModel(IHostConnection client, IUiDispatcher ui, Action<Exception> onError, Func<string, bool>? confirm = null)
    {
        _client = client;
        _ui = ui;
        _confirm = confirm ?? (_ => true);
        Draft.ChangedDraft += (_, _) => OnPropertyChanged(nameof(SceneDescription));
        ReadCommand = new AsyncCommand(ReadReplacingDraftAsync, onError);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(), onError, _ => CanEditDraft);
        NewCommand = new RelayCommand(NewDraft);
        AddParallelCommand = new RelayCommand(() => TryLocal(() => Draft.AddStage(Zones.Where(z => z.IsChosen)), onError));
        AddSerialCommand = new RelayCommand(() => TryLocal(() => { foreach (var z in Zones.Where(z => z.IsChosen)) Draft.AddStage([z]); }, onError));
        MoveUpCommand = new RelayCommand(() => Draft.MoveStage(-1));
        MoveDownCommand = new RelayCommand(() => Draft.MoveStage(1));
        RemoveStageCommand = new RelayCommand(Draft.RemoveStage);
        BlackoutCommand = new AsyncCommand(BlackoutAsync, onError);
        RestoreCommand = new AsyncCommand(RestoreAsync, onError);
        PreviewCommand = new RelayCommand(() => PreviewPlaying = !PreviewPlaying);
        RemoveAssignmentCommand = new AsyncCommand(RemoveAssignmentAsync, onError);
    }

    public SceneDraft Draft { get; } = new();
    public ObservableCollection<StudioZone> Zones { get; } = [];
    public ObservableCollection<StudioAssignment> Assignments { get; } = [];
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand AddParallelCommand { get; }
    public RelayCommand AddSerialCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand RemoveStageCommand { get; }
    public AsyncCommand BlackoutCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public AsyncCommand RemoveAssignmentCommand { get; }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Clock { get => _clock; private set => SetProperty(ref _clock, value); }
    public string Period { get => _period; private set => SetProperty(ref _period, value); }
    public string PeriodTitle => _scene.Assignments.Count > 0 && _scene.Assignments.All(a => a.EffectId == "StaticColor" && a.Strategy != "Frames") ? "播放状态" : "循环周期";
    public bool CanEditDraft => _assignment?.Dto.FrameSetId?.StartsWith("package:", StringComparison.Ordinal) != true;
    public string Health { get => _health; private set => SetProperty(ref _health, value); }
    public string Written { get => _written; private set => SetProperty(ref _written, value); }
    public string Updated { get => _updated; private set => SetProperty(ref _updated, value); }
    public int Page
    {
        get => _page;
        set { if (SetProperty(ref _page, value)) { PreviewPlaying = false; OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(PageSubtitle)); } }
    }
    public string PageTitle => Page switch { 0 => "机箱总览", 1 => "灯效编排", 2 => "屏幕内容", 3 => "设备与校准", 4 => "场景库", 5 => "运行诊断", _ => "偏好设置" };
    public string PageSubtitle => Page switch
    {
        0 => "一眼查看全机分区、当前编排与运行状态。",
        1 => "编辑光线的路径、配色与节奏，确认后再应用到机箱。",
        2 => "选择背景，叠加电脑信息，为每一块屏幕安排内容。",
        3 => "管理接管状态与物理安装方向，保留已验证的校准。",
        4 => "把灯效与屏幕内容保存在同一个场景中。",
        5 => "检查实际发送、同步时钟和设备错误。",
        _ => "调整启动与桌面行为。",
    };
    public bool PreviewPlaying { get => _preview; set { if (SetProperty(ref _preview, value)) OnPropertyChanged(nameof(PreviewButton)); } }
    public string PreviewButton => PreviewPlaying ? "Ⅱ 暂停路径预览" : "▷ 播放路径预览";
    public string SceneDescription => !CanEditDraft ? "独立场景包 · 按已保存的内容帧播放" : $"{DisplayNames.Effect(Draft.Effect?.Id ?? "Stacking")} · {Draft.Brightness:0}% 亮度{(Draft.ReturnEnabled ? " · 波浪回退" : string.Empty)}";
    public StudioAssignment? SelectedAssignment
    {
        get => _assignment;
        set
        {
            if (_updatingAssignments) return;
            if (Equals(_assignment, value)) return;
            if (Draft.Dirty && !_confirm("当前灯效有未应用的修改。放弃这些修改并切换编排？")) { OnPropertyChanged(); return; }
            SetProperty(ref _assignment, value);
            if (value is not null) Draft.Load(value.Dto, Zones);
            NotifyDraftAccess();
        }
    }
    public StudioZone? SelectedZone
    {
        get => _zone;
        set { if (SetProperty(ref _zone, value)) { OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedDetail)); } }
    }
    public string SelectedName => SelectedZone?.Name ?? "整个机箱";
    public string SelectedDetail => SelectedZone is { } z ? $"{z.Detail}\n{z.Controller.Id}\n分区 {z.Zone.Id} · {z.Zone.Layout}" : "点击机箱或分区列表查看设备；预览只在本机显示，不改变灯光。";

    public async Task RefreshAsync(bool discardDraft = false)
    {
        var revision = Draft.Revision;
        var request = Interlocked.Increment(ref _refreshId);
        var service = _client.Require();
        var controllers = await service.ListControllersAsync().ConfigureAwait(true);
        var effects = await service.ListEffectsAsync().ConfigureAwait(true);
        var scene = await service.GetSceneAsync().ConfigureAwait(true);
        _ui.Post(() =>
        {
            if (request != Volatile.Read(ref _refreshId)) return;
            var replaceDraft = revision == Draft.Revision && (!Draft.Dirty || discardDraft);
            UpdateZones(controllers);
            if (replaceDraft)
            {
                Draft.Effects.Clear();
                foreach (var e in effects) Draft.Effects.Add(new(e));
                _scene = scene;
                OnPropertyChanged(nameof(PeriodTitle));
                var i = _assignment?.Index ?? 0;
                SetAssignmentRows(scene.Assignments);
                _assignment = Assignments.ElementAtOrDefault(i) ?? Assignments.FirstOrDefault(); OnPropertyChanged(nameof(SelectedAssignment));
                Draft.Load(_assignment?.Dto, Zones);
                NotifyDraftAccess();
            }
            Updated = $"{DateTime.Now:HH:mm:ss} 已读取";
            Message = !CanEditDraft ? "此编排来自独立场景包。请在“场景库 → 外部场景包”管理设备绑定和播放。" : Draft.Dirty ? "已刷新运行状态，保留本地未应用的修改。" : "场景已读取。编辑不会立即改变机箱灯光。";
        });
    }
    private void UpdateZones(IReadOnlyList<ControllerDto> controllers)
    {
        var previous = Zones.ToDictionary(z => z.Ref);
        var chosen = SelectedZone?.Ref;
        var next = controllers.Where(c => !DisplayNames.IsDiscoveryPort(c))
            .SelectMany(c => c.Zones.Select(z =>
            {
                var key = new ZoneRefDto(c.Id, z.Id);
                if (previous.TryGetValue(key, out var kept)) { kept.Update(c, z); return kept; }
                return new StudioZone(c, z);
            })).ToArray();
        for (var i = 0; i < next.Length; i++)
            if (i >= Zones.Count) Zones.Add(next[i]); else if (!ReferenceEquals(Zones[i], next[i])) Zones[i] = next[i];
        while (Zones.Count > next.Length) Zones.RemoveAt(Zones.Count - 1);
        SelectedZone = Zones.FirstOrDefault(z => z.Ref == chosen);
    }
    private void SetAssignmentRows(IReadOnlyList<AssignmentDto> assignments, StudioAssignment? applied = null, AssignmentDto? replacement = null)
    {
        var selected = ReferenceEquals(_assignment, applied) && replacement is not null ? replacement : _assignment?.Dto;
        var key = selected is null ? null : JsonSerializer.Serialize(selected, JsonOptions);
        _updatingAssignments = true;
        try
        {
            Assignments.Clear();
            foreach (var a in assignments.Select((dto, n) => new StudioAssignment(dto, n))) Assignments.Add(a);
            _assignment = key is null ? null : Assignments.FirstOrDefault(a => JsonSerializer.Serialize(a.Dto, JsonOptions) == key);
        }
        finally { _updatingAssignments = false; }
        OnPropertyChanged(nameof(SelectedAssignment));
        NotifyDraftAccess();
    }

    public void UpdateStatus(StatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _ui.Post(() =>
        {
            var real = status.Devices.Where(d => d.Id != "enedram:p0" && d.Id != "enedram:p1").ToArray();
            var active = real.Count(d => d.State == ControllerStates.Active);
            Health = real.Length == 0 ? "未发现灯光设备" : $"{active} / {real.Length} 台灯光控制器运行中";
            Clock = $"{status.Clock.Fps:0.0} fps";
            var upload = real.Select(d => d.LastUpload).FirstOrDefault(u => u is not null);
            var derived = status.RfClock?.Assignments.FirstOrDefault(a => a.DerivedPeriodMs is > 0)?.DerivedPeriodMs;
            Period = derived is { } ms ? $"{ms / 1000:0.00} s" : upload is null ? "—" : $"{upload.PeriodMs / 1000:0.00} s · 标称";
            if (_scene.Assignments.Count > 0 && _scene.Assignments.All(a => a.EffectId == "StaticColor" && a.Strategy != "Frames"))
                Period = _scene.Assignments.All(a => a.Brightness == 0 || a.Colors.Count > 0 && a.Colors.All(c => c.Equals("#000000", StringComparison.OrdinalIgnoreCase))) ? "已熄灯" : "静态光";
            if (status.BlockedContent is { Count: > 0 } blocked) { Period = "内容被阻止"; Message = "场景内容无法播放：\n" + string.Join("\n", blocked); }
            Written = $"丢帧 {real.Sum(d => d.Dropped)} · 时钟超时 {status.Clock.Overruns}";
        });
    }

    public async Task ApplyAsync()
    {
        if (!CanEditDraft) throw new InvalidOperationException("独立场景包的内容帧不可在灯效编辑器中修改。请到场景库管理。");
        var next = Draft.Build();
        var revision = Draft.Revision;
        var selected = _assignment;
        var originalIndex = selected?.Index;
        var baseline = JsonSerializer.Serialize(_scene.Assignments, JsonOptions);
        var service = _client.Require();
        var current = await service.GetSceneAsync().ConfigureAwait(true);
        // Compare only RGB assignments: another screen edit must not erase screen content.
        if (JsonSerializer.Serialize(current.Assignments, JsonOptions) != baseline)
            throw new InvalidOperationException("运行中的灯效已被其他操作修改。请先重新读取，避免覆盖新场景。");
        var assignments = current.Assignments.ToList();
        if (originalIndex is { } oldIndex) assignments.RemoveAt(oldIndex);
        var overlap = assignments.Any(a => a.Targets.Intersect(next.Targets).Any());
        if (overlap) throw new InvalidOperationException("所选分区已由其他编排控制。请先移除对应编排，避免覆盖。");
        assignments.Insert(Math.Min(originalIndex ?? assignments.Count, assignments.Count), next);
        await service.SetSceneAsync(new(assignments)).ConfigureAwait(true);
        var reload = revision == Draft.Revision && ReferenceEquals(_assignment, selected);
        _scene = new(assignments); SetAssignmentRows(assignments, selected, next);
        if (reload)
        {
            await RefreshAsync(true).ConfigureAwait(true);
            Message = Draft.Dirty ? "已应用点击时的灯效，后续修改仍留在草稿中。" : "灯效已应用。发送成功不代表实机视觉效果已验收。";
        }
        else
        {
            Message = "已应用点击时的灯效，后续修改仍留在草稿中。";
        }
    }

    private async Task ReadReplacingDraftAsync()
    {
        if (Draft.Dirty && !_confirm("放弃未应用的修改，重新读取运行中的场景？")) return;
        await RefreshAsync(true).ConfigureAwait(true);
    }
    private void NewDraft()
    {
        if (Draft.Dirty && !_confirm("放弃未应用的修改，开始新的灯效编排？")) return;
        _assignment = null;
        OnPropertyChanged(nameof(SelectedAssignment));
        NotifyDraftAccess();
        Draft.Load(null, Zones);
        Message = "勾选分区后，添加为并行组或依次添加到路径。";
        Page = 1;
    }
    private async Task RemoveAssignmentAsync()
    {
        var selected = _assignment;
        if (selected is null || !_confirm($"移除“{selected.Name}”的灯效编排？")) return;
        var revision = Draft.Revision;
        var baseline = JsonSerializer.Serialize(_scene.Assignments, JsonOptions);
        var service = _client.Require();
        var current = await service.GetSceneAsync().ConfigureAwait(true);
        if (JsonSerializer.Serialize(current.Assignments, JsonOptions) != baseline) throw new InvalidOperationException("场景已更改，请重新读取。");
        var assignments = current.Assignments.Where((_, i) => i != selected.Index).ToArray();
        await service.SetSceneAsync(new(assignments)).ConfigureAwait(true);
        var reload = revision == Draft.Revision && ReferenceEquals(_assignment, selected);
        _scene = new(assignments); SetAssignmentRows(assignments);
        if (reload) await RefreshAsync(true).ConfigureAwait(true);
    }
    public async Task BlackoutAsync()
    {
        var service = _client.Require();
        var current = await service.GetSceneAsync().ConfigureAwait(true);
        if (IsPersistedBlackout(current))
        { Message = "灯光已处于熄灭状态；长屏休眠请求保持，不覆盖原有恢复点。实际状态见屏幕内容。"; return; }
        var controllers = await service.ListControllersAsync().ConfigureAwait(true);
        var targets = controllers.SelectMany(c => c.Zones.Select(z => new ZoneRefDto(c.Id, z.Id))).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("没有可以熄灭的灯光分区。");
        await service.SaveProfileAsync(new(BeforeLightsOffProfile, new(current.Assignments, Package: current.Package))).ConfigureAwait(true);
        await service.SetSceneAsync(new([new(targets, "StaticColor", ["#000000"], Brightness: 0, Strategy: "Vendor")])).ConfigureAwait(true);
        Message = "已保存熄灯前场景并关闭灯光；已接管的长屏将关闭背光并停止发帧，点击恢复后唤醒。";
    }
    internal static bool IsPersistedBlackout(SceneDto scene) => scene.Package is null && scene.Assignments.Count > 0
        && scene.Assignments.All(a => a.EffectId == "StaticColor" && a.FrameSetId is null
            && (a.Brightness == 0 || a.Colors.Count > 0 && a.Colors.All(c => c.Equals("#000000", StringComparison.OrdinalIgnoreCase))));
    private async Task RestoreAsync()
    {
        var service = _client.Require();
        var backup = await service.LoadProfileAsync(BeforeLightsOffProfile).ConfigureAwait(true);
        await service.SetSceneAsync(new(backup.Scene.Assignments, Package: backup.Scene.Package)).ConfigureAwait(true);
        await RefreshAsync(true).ConfigureAwait(true);
        Message = "已恢复熄灯前保存的场景；长屏在新画面就绪后恢复背光。";
    }
    private void NotifyDraftAccess() { OnPropertyChanged(nameof(CanEditDraft)); OnPropertyChanged(nameof(SceneDescription)); ApplyCommand.RaiseCanExecuteChanged(); }
    public void SetConnected(bool connected)
    {
        if (connected) return;
        Health = "后台未连接 · 显示上次读取";
        Clock = "—"; Period = "—";
        PreviewPlaying = false;
    }
    private static void TryLocal(Action action, Action<Exception> onError)
    {
        try { action(); }
        catch (InvalidOperationException ex) { onError(ex); }
    }
}
