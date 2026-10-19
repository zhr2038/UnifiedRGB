using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

public sealed record PackageChoice(ScenePackageInfoDto Dto)
{
    public string Id => Dto.ContentId;
    public string Name => Dto.Name;
    public string Detail => $"修订 {Dto.Revision} · {Dto.DurationMs / 1000:0.##} 秒 · {Dto.LightingRoles} 灯区 / {Dto.ScreenRoles} 屏幕";
}

public sealed record PackageTarget(string Key, string Name, ZoneRefDto? Zone = null, string? ScreenId = null);

public sealed class PackageRoleChoice : ObservableObject
{
    private PackageTarget? _selected;
    public PackageRoleChoice(ScenePackageRoleDto dto, IReadOnlyList<PackageTarget> targets, PackageTarget selected)
    {
        Dto = dto; Targets = targets; _selected = selected; Colors = dto.PreviewColors.Take(128).ToArray();
        if (dto.PreviewPngBase64 is { } png)
        {
            using var stream = new MemoryStream(Convert.FromBase64String(png));
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320; image.StreamSource = stream; image.EndInit(); image.Freeze(); Preview = image;
        }
    }
    public ScenePackageRoleDto Dto { get; }
    public string Label => Dto.Label;
    public string Detail => Dto.Kind == "lighting" ? $"灯光 · {Dto.LedCount} 个逻辑灯位 · {Dto.Frames} 帧" : $"屏幕 · {Dto.Width} × {Dto.Height} · {Dto.Frames} 帧";
    public IReadOnlyList<string> Colors { get; }
    public string PreviewNote => Colors.Count < Dto.PreviewColors.Count ? $"首帧 · 显示前 {Colors.Count} 个灯位" : "";
    public BitmapSource? Preview { get; }
    public IReadOnlyList<PackageTarget> Targets { get; }
    public PackageTarget? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public ScenePackageBindingDto Build() => new(Dto.Role, Selected?.Zone, Selected?.ScreenId);
}

/// <summary>Immutable package content, local role bindings and explicit apply. Reading and previewing never apply content.</summary>
public sealed class ScenePackagesViewModel : ObservableObject
{
    private readonly Func<IScenePackageService> _service;
    private readonly Func<Task<IReadOnlyList<ControllerDto>>> _controllers;
    private readonly Func<Task<IReadOnlyList<ScreenDeviceDto>>> _screens;
    private readonly Func<string?> _chooseFile;
    private readonly Func<string, bool> _confirm;
    private readonly Action<Exception> _error;
    private PackageChoice? _selected;
    private ScenePackageDetailsDto? _details;
    private ScenePackageBindingStateDto? _binding;
    private IReadOnlyList<ControllerDto> _inventory = [];
    private IReadOnlyList<ScreenDeviceDto> _displays = [];
    private bool _busy, _loading, _dirty, _available, _updatingList;
    private string _message = "导入场景包后，为内容角色选择本机设备。";
    private long _selectionRequest, _draftVersion, _refreshRequest;

    public ScenePackagesViewModel(Func<IScenePackageService> service,
        Func<Task<IReadOnlyList<ControllerDto>>> controllers, Func<Task<IReadOnlyList<ScreenDeviceDto>>> screens,
        Func<string?> chooseFile, Action<Exception> onError, Func<string, bool>? confirm = null)
    {
        _service = service; _controllers = controllers; _screens = screens; _chooseFile = chooseFile; _error = onError;
        _confirm = confirm ?? (_ => true);
        RefreshCommand = new AsyncCommand(_ => RefreshAsync(), Report, _ => !Busy);
        ImportCommand = new AsyncCommand(_ => ImportAsync(), Report, _ => !Busy && Available);
        SaveBindingsCommand = new AsyncCommand(_ => SaveBindingsAsync(), Report, _ => CanEdit && Roles.Count > 0);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(), Report, _ => CanApply);
        OpenDeveloperDocsCommand = new RelayCommand(() =>
        {
            try
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "Docs", "ScenePackages");
                if (!Directory.Exists(folder)) throw new InvalidOperationException("开发资料尚未安装，请更新界面程序。");
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { Report(ex); }
        });
    }
    public ObservableCollection<PackageChoice> Packages { get; } = [];
    public ObservableCollection<PackageRoleChoice> Roles { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand SaveBindingsCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public RelayCommand OpenDeveloperDocsCommand { get; }
    public event EventHandler? Applied;
    public bool Available { get => _available; private set { if (SetProperty(ref _available, value)) NotifyState(); } }
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyState(); } }
    public bool Dirty { get => _dirty; private set { if (SetProperty(ref _dirty, value)) NotifyState(); } }
    public bool HasDetails => _details is not null;
    public bool CanSelect => !Busy;
    public bool CanEdit => Available && !Busy && HasDetails;
    public bool CanApply => CanEdit && !Dirty && _binding is { Ready: true, BindingId: not null };
    public string Title => _details?.Info.Name ?? "外部场景包";
    public string Detail => _details is { Info: var p } ? $"修订 {p.Revision} · {p.DurationMs / 1000:0.##} 秒循环 · {p.LightingRoles} 个灯光角色 · {p.ScreenRoles} 个屏幕角色" : "场景内容独立保存，设备绑定留在本机。";
    public string Provenance => _details is { Info: var p } ? $"内容 {p.ContentId[..Math.Min(12, p.ContentId.Length)]} · {p.PlaybackContract}" : "";
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string BindingStatus => Dirty ? "设备绑定尚未保存" : _binding is { Ready: true } ? "设备绑定已保存，可以应用" : "请完成并保存设备绑定";
    public PackageChoice? Selected
    {
        get => _selected;
        set
        {
            if (_updatingList) return;
            if (_selected?.Id == value?.Id) return;
            if (Busy || (Dirty && !_confirm("放弃尚未保存的设备绑定并切换场景包？"))) { OnPropertyChanged(); return; }
            SetProperty(ref _selected, value);
            ClearDetails();
            _ = ReadSelectionAsync();
        }
    }

    public async Task RefreshOptionalAsync()
    {
        try { await RefreshAsync().ConfigureAwait(true); }
        catch (StreamJsonRpc.RemoteMethodNotFoundException) { Available = false; Message = "当前后台尚未支持场景包，请更新后台后刷新。"; }
        catch (InvalidOperationException) { MarkDisconnected(); }
    }

    public async Task RefreshAsync()
    {
        if (Busy) return;
        var request = Interlocked.Increment(ref _refreshRequest);
        var list = await _service().ListScenePackagesAsync().ConfigureAwait(true);
        var controllers = await _controllers().ConfigureAwait(true);
        var screens = await _screens().ConfigureAwait(true);
        if (request != Volatile.Read(ref _refreshRequest)) return;
        Available = true; _inventory = controllers; _displays = screens;
        var id = _selected?.Id;
        _updatingList = true;
        try
        {
            Packages.Clear(); foreach (var item in list) Packages.Add(new(item));
            // Replacing the list must not discard a binding draft or select another package implicitly.
            _selected = Packages.FirstOrDefault(p => p.Id == id); OnPropertyChanged(nameof(Selected));
        }
        finally { _updatingList = false; }
        if (_selected is null)
        {
            if (id is not null) ClearDetails();
            Message = Packages.Count == 0 ? "尚未安装场景包。点击“导入场景包”选择 ZIP 文件。" : "选择一个场景包查看首帧和设备绑定。";
        }
        else if (!Dirty) await ReadSelectionAsync().ConfigureAwait(true);
        else Message = "场景包列表已更新，未保存的设备绑定已保留。";
    }

    public void MarkDisconnected()
    {
        Interlocked.Increment(ref _refreshRequest); Interlocked.Increment(ref _selectionRequest);
        Available = false; Message = "后台未连接，设备绑定草稿已保留。";
    }

    public async Task ReadSelectionAsync()
    {
        var request = Interlocked.Increment(ref _selectionRequest);
        var id = _selected?.Id; var revision = _draftVersion;
        if (id is null) { ClearDetails(); return; }
        try
        {
            var service = _service();
            var detail = await service.GetScenePackageAsync(id).ConfigureAwait(true);
            var binding = await service.GetScenePackageBindingsAsync(id).ConfigureAwait(true);
            if (request != Volatile.Read(ref _selectionRequest) || _selected?.Id != id || revision != _draftVersion) return;
            _details = detail; _binding = binding;
            _loading = true;
            try
            {
                Roles.Clear();
                foreach (var role in detail.Roles)
                {
                    var targets = TargetsFor(role).ToList();
                    var saved = binding.Bindings.FirstOrDefault(b => b.Role == role.Role);
                    var target = targets.FirstOrDefault(t => Matches(t, saved));
                    if (target is null && saved is not null && (saved.Zone is not null || saved.ScreenId is not null))
                    {
                        target = new("missing", "已保存设备 · 当前未发现", saved.Zone, saved.ScreenId); targets.Add(target);
                    }
                    var row = new PackageRoleChoice(role, targets, target ?? targets[0]);
                    row.PropertyChanged += (_, e) => { if (!_loading && e.PropertyName == nameof(PackageRoleChoice.Selected)) { _draftVersion++; Dirty = true; } };
                    Roles.Add(row);
                }
                Dirty = false; Message = binding.Ready ? "首帧预览已读取。应用后才会切换硬件内容。" : Problems(binding.Problems);
            }
            finally { _loading = false; }
            NotifyDetails();
        }
#pragma warning disable CA1031 // Background selection read reports errors without crashing the dispatcher.
        catch (Exception ex) { if (request == Volatile.Read(ref _selectionRequest)) { ClearDetails(); Report(ex); } }
#pragma warning restore CA1031
    }

    private IEnumerable<PackageTarget> TargetsFor(ScenePackageRoleDto role)
    {
        yield return new("unbound", "选择设备…");
        if (role.Kind == "lighting")
        {
            foreach (var c in _inventory)
                foreach (var z in c.Zones.Where(z => z.LedCount == role.LedCount))
                    yield return new(c.Id + "/" + z.Id, $"{DisplayNames.Controller(c.Id)} · {z.Name} · {z.LedCount} 灯位" + (c.State == ControllerStates.Active ? "" : " · 暂不可用"), new(c.Id, z.Id));
        }
        else
            foreach (var screen in _displays)
                yield return new(screen.Id, new ScreenChoice(screen).Name + " · " + new ScreenChoice(screen).Detail, ScreenId: screen.Id);
    }
    private static bool Matches(PackageTarget target, ScenePackageBindingDto? saved) => saved is not null && target.Zone == saved.Zone && target.ScreenId == saved.ScreenId;

    private async Task ImportAsync()
    {
        if (!Available || Busy) return;
        var path = _chooseFile(); if (path is null) return;
        var previousSelection = Selected?.Id; var revision = _draftVersion;
        Busy = true;
        ScenePackageDetailsDto imported;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 64L * 1024 * 1024) throw new ArgumentException("场景包必须是 64 MiB 以内的 ZIP 文件。");
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            if (bytes.Length > 64L * 1024 * 1024) throw new ArgumentException("场景包超过 64 MiB。");
            Message = "正在校验并安装场景包…";
            imported = await _service().ImportScenePackageAsync(new(file.Name, Convert.ToBase64String(bytes))).ConfigureAwait(true);
        }
        finally { Busy = false; }
        var hadDraft = Dirty;
        await RefreshAsync().ConfigureAwait(true);
        if (!hadDraft && !Dirty && previousSelection == Selected?.Id && revision == _draftVersion) { Selected = Packages.FirstOrDefault(p => p.Id == imported.Info.ContentId); await ReadSelectionAsync().ConfigureAwait(true); }
        Message = $"已安装“{imported.Info.Name}”。" + (hadDraft ? "当前设备绑定草稿已保留。" : "请检查设备绑定后应用。");
    }

    private async Task SaveBindingsAsync()
    {
        if (!CanEdit || Selected is null) return;
        var id = Selected.Id; var revision = _draftVersion; var bindings = Roles.Select(r => r.Build()).ToArray();
        Busy = true;
        try
        {
            var result = await _service().SaveScenePackageBindingsAsync(new(id, bindings)).ConfigureAwait(true);
            if (Selected?.Id != id || revision != _draftVersion) { Message = "设备绑定已提交；后续修改仍保留在草稿中。"; return; }
            _binding = result;
            Dirty = !result.Ready;
            Message = result.Ready ? "设备绑定已保存，硬件内容未切换。" : Problems(result.Problems);
        }
        finally { Busy = false; NotifyState(); }
    }

    private async Task ApplyAsync()
    {
        if (!CanApply || Selected is null) return;
        var id = Selected.Id; var bindingId = _binding!.BindingId;
        Busy = true;
        try
        {
            var service = _service();
            var current = await service.GetScenePackageBindingsAsync(id).ConfigureAwait(true);
            if (!current.Ready || current.BindingId != bindingId)
            {
                _binding = current; Dirty = true; Message = "设备绑定已变化或设备暂不可用。请刷新、检查并重新保存后应用。"; return;
            }
            var result = await service.ApplyScenePackageAsync(id).ConfigureAwait(true);
            Message = result.Applied ? "场景包已应用，请查看实际灯光与屏幕效果。" : "应用未完成，原场景保留。\n" + result.Detail;
            if (result.Applied) Applied?.Invoke(this, EventArgs.Empty);
        }
        finally { Busy = false; }
    }

    private void ClearDetails() { _details = null; _binding = null; Roles.Clear(); Dirty = false; NotifyDetails(); }
    private void NotifyDetails() { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(Provenance)); OnPropertyChanged(nameof(HasDetails)); NotifyState(); }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(BindingStatus));
        OnPropertyChanged(nameof(CanSelect));
        RefreshCommand.RaiseCanExecuteChanged(); ImportCommand.RaiseCanExecuteChanged(); SaveBindingsCommand.RaiseCanExecuteChanged(); ApplyCommand.RaiseCanExecuteChanged();
    }
    private static string Problems(IReadOnlyList<string> problems) => problems.Count == 0 ? "为每个内容角色选择设备，然后保存绑定。" : "绑定尚未就绪：\n" + string.Join("\n", problems.Select(p => p == "No binding has been saved for this package." ? "此场景包尚未保存设备绑定。" : p));
    private void Report(Exception ex) { Message = "操作未完成：" + ex.Message; _error(ex); }
}
