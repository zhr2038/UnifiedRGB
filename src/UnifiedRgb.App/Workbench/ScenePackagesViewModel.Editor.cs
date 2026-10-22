using System.Collections.ObjectModel;
using System.Diagnostics;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Preview;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

public sealed partial class ScenePackagesViewModel
{
    private bool _editorAvailable, _editorDirty, _previewPlaying;
    private double _speed = 1, _lightBrightness = 100, _hue, _previewPosition;
    private bool _reverse, _lightingEnabled = true;
    private string _variantName = "", _previewNote = "选择场景包以加载动态预览。";
    private int _inspectorPage;
    private long _previewTick;
    private CancellationTokenSource? _previewStop;
    private PackageRoleChoice? _selectedRole;
    private ScenePackageEditVersionDto? _selectedVersion;
    private PackagePreviewData? _previewData;
    private ScenePackagePreviewDto? _previewMetadata;
    private ScenePackagePlaybackDto _previewPlayback = new();
    private readonly Dictionary<object, int> _inputErrors = [];
    public PcTelemetryDto? TelemetrySnapshot { get; set; }
    public Func<Task<PcTelemetryDto>>? ReadPreviewTelemetry { get; set; }
    private bool _previewTelemetryBusy, _previewDisposed;
    private long _telemetryTick;
    public async Task RefreshPreviewTelemetryAsync()
    {
        if (_previewDisposed || _previewTelemetryBusy || !Available || ReadPreviewTelemetry is not { } read) return;
        var now = Stopwatch.GetTimestamp();
        if (_telemetryTick != 0 && Stopwatch.GetElapsedTime(_telemetryTick, now).TotalSeconds < 2) return;
        _telemetryTick = now; _previewTelemetryBusy = true;
        try { var data = await read().ConfigureAwait(true); if (!_previewDisposed) TelemetrySnapshot = data; }
#pragma warning disable CA1031 // read-only preview telemetry may be unavailable; stale samples are labelled in the preview
        catch (Exception) { }
#pragma warning restore CA1031
        finally { _previewTelemetryBusy = false; }
    }
    public ScenePackagePlaybackDto PreviewPlayback => _previewPlayback;
    public PackagePreviewCache PreviewCache { get; private set; } = null!;
    public AsyncCommand SaveEditsCommand { get; private set; } = null!;
    public AsyncCommand ApplyEditsCommand { get; private set; } = null!;
    public AsyncCommand SaveVariantCommand { get; private set; } = null!;
    public AsyncCommand LoadVersionCommand { get; private set; } = null!;
    public RelayCommand ResetEditsCommand { get; private set; } = null!;
    public RelayCommand TogglePreviewCommand { get; private set; } = null!;
    public RelayCommand PreviewStartCommand { get; private set; } = null!;
    public ObservableCollection<ScenePackageEditVersionDto> Versions { get; } = [];
    public IReadOnlyList<ValueChoice> ScreenOverlays { get; } = [new("none", "不叠加信息"), new("compact", "简洁监控"), new("dashboard", "监控仪表盘")];
    public IReadOnlyList<ValueChoice> ScreenFits { get; } = [new("contain", "完整显示"), new("cover", "铺满并裁切")];
    public IReadOnlyList<double> SpeedChoices { get; } = [.25, .5, .75, 1, 1.25, 1.5, 2, 3, 4];
    public bool EditorAvailable { get => _editorAvailable; private set { if (SetProperty(ref _editorAvailable, value)) NotifyEditorState(); } }
    public bool EditorDirty { get => _editorDirty; private set { if (SetProperty(ref _editorDirty, value)) { NotifyState(); OnPropertyChanged(nameof(EditorDraftState)); } } }
    public bool CanAdjust => HasDetails && EditorAvailable && !Busy;
    public bool CanCommitEdits => CanAdjust && Available && _inputErrors.Count == 0 && ParameterProblem.Length == 0;
    public string ParameterProblem => ScenePackagePlayback.Validate(_previewPlayback) ?? "";
    public void UpdateInputErrors(object source, int count) { if (count == 0) _inputErrors.Remove(source); else _inputErrors[source] = count; NotifyEditorState(); }
    public double PlaybackSpeed { get => _speed; set { if (SetProperty(ref _speed, value)) MarkEditorChanged(); } }
    public double LightingBrightness { get => _lightBrightness; set { if (SetProperty(ref _lightBrightness, value)) MarkEditorChanged(); } }
    public double HueDegrees { get => _hue; set { if (SetProperty(ref _hue, value)) MarkEditorChanged(); } }
    public bool PlaybackReverse { get => _reverse; set { if (SetProperty(ref _reverse, value)) MarkEditorChanged(); } }
    public bool LightingEnabled { get => _lightingEnabled; set { if (SetProperty(ref _lightingEnabled, value)) MarkEditorChanged(); } }
    public string VariantName { get => _variantName; set => SetProperty(ref _variantName, value); }
    public int InspectorPage { get => _inspectorPage; set => SetProperty(ref _inspectorPage, value); }
    public PackageRoleChoice? SelectedRole { get => _selectedRole; set => SetProperty(ref _selectedRole, value); }
    public ScenePackageEditVersionDto? SelectedVersion { get => _selectedVersion; set => SetProperty(ref _selectedVersion, value); }
    public PackagePreviewData? PreviewData { get => _previewData; private set { if (SetProperty(ref _previewData, value)) OnPropertyChanged(nameof(HasDynamicPreview)); } }
    public bool HasDynamicPreview => PreviewData is not null;
    public string PreviewNote { get => _previewNote; private set => SetProperty(ref _previewNote, value); }
    public string EditorDraftState => EditorDirty || Dirty ? "本地草稿 · 尚未应用" : EditorAvailable ? "已保存版本 · 预览不改变输出" : "后台版本暂不支持参数编辑";
    public bool PreviewPlaying { get => _previewPlaying; set { if (SetProperty(ref _previewPlaying, value)) { _previewTick = Stopwatch.GetTimestamp(); OnPropertyChanged(nameof(PreviewButtonText)); } } }
    public string PreviewButtonText => PreviewPlaying ? "Ⅱ 暂停预览" : "▶ 播放预览";
    public double PreviewDurationMs => Math.Max(1, (PreviewData?.Info.DurationMs ?? _details?.Info.DurationMs ?? 1) / (double.IsFinite(PlaybackSpeed) ? Math.Clamp(PlaybackSpeed, .25, 4) : 1));
    public double PreviewPositionMs { get => _previewPosition; set { PreviewPlaying = false; SetPreviewPosition(value); } }
    public string PreviewTime => FormattableString.Invariant($"{TimeSpan.FromMilliseconds(_previewPosition):mm\\:ss\\.fff} / {TimeSpan.FromMilliseconds(PreviewDurationMs):mm\\:ss\\.fff}");
    public event EventHandler? PreviewChanged;

    private void InitializeEditor()
    {
        PreviewCache = new(_service);
        SaveEditsCommand = new AsyncCommand(_ => SaveEditsAsync(), Report, _ => CanCommitEdits);
        ApplyEditsCommand = new AsyncCommand(async _ => { if (EditorDirty || Dirty) await SaveEditsAsync().ConfigureAwait(true); await ApplyAsync().ConfigureAwait(true); }, Report, _ => CanCommitEdits);
        SaveVariantCommand = new AsyncCommand(_ => SaveVariantAsync(), Report, _ => CanCommitEdits);
        LoadVersionCommand = new AsyncCommand(_ => LoadVersionAsync(), Report, _ => CanCommitEdits && SelectedVersion is not null);
        ResetEditsCommand = new RelayCommand(() => { if (CanAdjust) { LoadPlayback(new()); MarkEditorChanged(); } });
        TogglePreviewCommand = new RelayCommand(() => PreviewPlaying = !PreviewPlaying);
        PreviewStartCommand = new RelayCommand(() => PreviewPositionMs = 0);
    }

    public void SelectCurrent(string? contentId)
    {
        if (Selected is null && !Dirty && !EditorDirty) Selected = Packages.FirstOrDefault(p => p.Id == contentId);
    }

    public ScenePackagePlaybackDto BuildPlayback()
    {
        var screens = Roles.Select(r => r.BuildScreenEdit()).OfType<ScenePackageScreenEditDto>().ToArray();
        return new(PlaybackSpeed, PlaybackReverse, LightingBrightness / 100, HueDegrees, LightingEnabled, screens.Length == 0 ? null : screens);
    }

    private void LoadPlayback(ScenePackagePlaybackDto p)
    {
        var loading = _loading; _loading = true;
        try
        {
            PlaybackSpeed = p.Speed; PlaybackReverse = p.Reverse; LightingBrightness = p.LightingBrightness * 100; HueDegrees = p.HueDegrees; LightingEnabled = p.LightingEnabled;
            if ((PreviewData?.Info ?? _previewMetadata) is { } data) foreach (var screen in data.Screens)
                Roles.FirstOrDefault(r => r.Dto.Role == screen.Role)?.LoadScreen(screen, p.Screens?.FirstOrDefault(e => e.Role == screen.Role));
        }
        finally { _loading = loading; }
        _previewPlayback = BuildPlayback();
        OnPropertyChanged(nameof(PreviewDurationMs)); PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkEditorChanged()
    {
        if (_loading) return;
        _draftVersion++; _previewPlayback = BuildPlayback(); EditorDirty = true;
        NotifyState(); OnPropertyChanged(nameof(ParameterProblem));
        OnPropertyChanged(nameof(PreviewDurationMs)); OnPropertyChanged(nameof(PreviewTime));
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReadEditorAsync(string id, long request, long revision)
    {
        try
        {
            var svc = _service(); var editor = await svc.GetScenePackageEditorAsync(id).ConfigureAwait(true);
            var preview = await svc.GetScenePackagePreviewAsync(id).ConfigureAwait(true);
            if (request != _selectionRequest || Selected?.Id != id || revision != _draftVersion) return;
            _previewMetadata = preview;
            _binding = new(editor.ContentId, editor.BindingId, editor.Bindings, editor.Ready, editor.Problems);
            _loading = true;
            try { foreach (var row in Roles) { var b = editor.Bindings.FirstOrDefault(b => b.Role == row.Dto.Role); row.Selected = row.Targets.FirstOrDefault(t => Matches(t, b)) ?? row.Targets[0]; } }
            finally { _loading = false; }
            LoadPlayback(editor.Playback); EditorDirty = false;
            Versions.Clear(); foreach (var v in editor.Versions) Versions.Add(v);
            EditorAvailable = true;
            _previewStop?.Cancel(); _previewStop?.Dispose(); _previewStop = new();
            var ct = _previewStop.Token;
            PreviewNote = "正在校验并读取原始灯光帧…";
            var data = await PreviewCache.ReadLightingAsync(preview, ct).ConfigureAwait(true);
            if (request != _selectionRequest || Selected?.Id != id || ct.IsCancellationRequested) return;
            PreviewData = data;
            SelectedRole = Roles.FirstOrDefault(); VariantName = (_details?.Info.Name ?? "场景") + " · 自定义";
            PreviewNote = "原始帧动态预演 · 结构与光晕为示意；预览不发送到硬件。";
            SetPreviewPosition(0); PreviewPlaying = true; NotifyState();
        }
        catch (Exception ex) when (ex is NotSupportedException or StreamJsonRpc.RemoteMethodNotFoundException)
        {
            EditorAvailable = false; PreviewNote = "请更新后台以使用外部效果调参和动态预览。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException)
        {
            PreviewNote = "动态预览未能加载：" + ex.Message;
        }
    }

    public void AdvancePreview()
    {
        if (!PreviewPlaying || PreviewData is null) return;
        var now = Stopwatch.GetTimestamp(); var delta = _previewTick == 0 ? 0 : Math.Min(100, Stopwatch.GetElapsedTime(_previewTick, now).TotalMilliseconds);
        _previewTick = now; SetPreviewPosition((_previewPosition + delta) % PreviewDurationMs);
    }
    private void SetPreviewPosition(double value)
    {
        _previewPosition = double.IsFinite(value) ? Math.Clamp(value, 0, PreviewDurationMs) : 0;
        OnPropertyChanged(nameof(PreviewPositionMs)); OnPropertyChanged(nameof(PreviewTime)); PreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    private async Task SaveEditsAsync()
    {
        if (!CanCommitEdits || Selected is null) return;
        var options = BuildPlayback(); if (ScenePackagePlayback.Validate(options) is { } bad) throw new ArgumentException(bad);
        var id = Selected.Id; var revision = _draftVersion; Busy = true;
        try
        {
            var saved = await _service().SaveScenePackageEditAsync(new(id, _binding?.BindingId, Roles.Select(r => r.Build()).ToArray(), options)).ConfigureAwait(true);
            if (Selected?.Id != id) return;
            _binding = new(id, saved.BindingId, saved.Bindings, saved.Ready, saved.Problems);
            if (revision == _draftVersion) { Dirty = false; EditorDirty = false; }
            Versions.Clear(); foreach (var version in saved.Versions) Versions.Add(version);
            Message = saved.Ready ? "参数和绑定已保存为新版本，当前机箱输出未切换。" : "版本已保存；设备尚未就绪：\n" + string.Join("\n", saved.Problems);
        }
        finally { Busy = false; NotifyState(); }
    }
    private async Task SaveVariantAsync()
    {
        if (!CanCommitEdits || Selected is null) return;
        if (EditorDirty || Dirty) await SaveEditsAsync().ConfigureAwait(true);
        if (EditorDirty || Dirty || _binding?.BindingId is not { } binding) return;
        Busy = true;
        try { await _service().SaveScenePackageVariantAsync(new(Selected.Id, binding, VariantName)).ConfigureAwait(true); Message = "已另存为场景“" + VariantName + "”，当前输出未切换。"; }
        finally { Busy = false; }
    }
    private async Task LoadVersionAsync()
    {
        if (Selected is null || SelectedVersion is null || !CanCommitEdits) return;
        if ((Dirty || EditorDirty) && !_confirm("放弃当前草稿并载入此版本？")) return;
        var id = Selected.Id; var version = SelectedVersion.BindingId; Busy = true;
        try
        {
            var state = await _service().GetScenePackageEditorAsync(id, version).ConfigureAwait(true);
            _loading = true;
            try { foreach (var row in Roles) { var b = state.Bindings.FirstOrDefault(b => b.Role == row.Dto.Role); row.Selected = row.Targets.FirstOrDefault(t => Matches(t, b)) ?? row.Targets[0]; } LoadPlayback(state.Playback); }
            finally { _loading = false; }
            MarkEditorChanged(); Dirty = true; Message = "已载入历史版本为草稿。保存并应用后才会切换机箱。";
        }
        finally { Busy = false; }
    }
    private void ClearEditor()
    {
        _previewStop?.Cancel(); _previewStop?.Dispose(); _previewStop = null; PreviewPlaying = false; PreviewData = null; _previewMetadata = null; EditorDirty = false;
        SelectedRole = null; Versions.Clear(); EditorAvailable = false; _previewPosition = 0;
    }
    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(CanAdjust)); OnPropertyChanged(nameof(CanCommitEdits)); OnPropertyChanged(nameof(EditorDraftState));
        SaveEditsCommand?.RaiseCanExecuteChanged(); ApplyEditsCommand?.RaiseCanExecuteChanged(); SaveVariantCommand?.RaiseCanExecuteChanged(); LoadVersionCommand?.RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        _previewDisposed = true;
        _previewStop?.Cancel(); _previewStop?.Dispose(); _previewStop = null;
        PreviewPlaying = false; PreviewCache.Dispose();
    }
}
