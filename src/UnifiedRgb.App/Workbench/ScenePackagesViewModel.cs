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

public sealed record PackageTarget(string Key, string Name, ZoneRefDto? Zone = null, string? ScreenId = null, string? Family = null);

public sealed partial class PackageRoleChoice : ObservableObject
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
    public PackageTarget? Selected { get => _selected; set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(AllowsRegion)); } }
    public ScenePackageBindingDto Build() => new(Dto.Role, Selected?.Zone, Selected?.ScreenId);
}

/// <summary>Immutable package content, local role bindings and explicit apply. Reading and previewing never apply content.</summary>
public sealed partial class ScenePackagesViewModel : ObservableObject, IDisposable
{
    private readonly Func<IScenePackageService> _service;
    private readonly Func<Task<IReadOnlyList<ControllerDto>>> _controllers;
    private readonly Func<Task<IReadOnlyList<ScreenDeviceDto>>> _screens;
    private readonly Func<string?> _chooseFile;
    private readonly Func<string, bool> _confirm;
    private readonly Func<ISceneLibraryService>? _library;
    private bool _cleanRelatedMedia;
    private bool _checkedApplySupported = true;
    private bool _librarySupported;
    private string _usage = "";
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
        Func<string?> chooseFile, Action<Exception> onError, Func<string, bool>? confirm = null, Func<ISceneLibraryService>? library = null)
    {
        _service = service; _controllers = controllers; _screens = screens; _chooseFile = chooseFile; _error = onError;
        _confirm = confirm ?? (_ => true);
        _library = library;
        InitializeEditor();
        RefreshCommand = new AsyncCommand(_ => RefreshAsync(), Report, _ => !Busy);
        ImportCommand = new AsyncCommand(_ => ImportAsync(), Report, _ => !Busy && Available);
        SaveBindingsCommand = new AsyncCommand(_ => SaveBindingsAsync(), Report, _ => CanEdit && Roles.Count > 0);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(), Report, _ => CanApply);
        RemoveCommand = new AsyncCommand(_ => RemoveAsync(), Report, _ => CanEdit && !Dirty && MaintenanceAvailable);
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
    public AsyncCommand RemoveCommand { get; }
    public bool CleanRelatedMedia { get => _cleanRelatedMedia; set => SetProperty(ref _cleanRelatedMedia, value); }
    public string Usage { get => _usage; private set => SetProperty(ref _usage, value); }
    public bool MaintenanceAvailable => Available && _librarySupported;
    public RelayCommand OpenDeveloperDocsCommand { get; }
    public event EventHandler? Applied;
    public bool Available { get => _available; private set { if (SetProperty(ref _available, value)) NotifyState(); } }
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyState(); } }
    public bool Dirty { get => _dirty; private set { if (SetProperty(ref _dirty, value)) NotifyState(); } }
    public bool HasDetails => _details is not null;
    public bool CanSelect => !Busy;
    public bool CanEdit => Available && !Busy && HasDetails;
    public bool CanApply => CanEdit && _checkedApplySupported && !Dirty && !EditorDirty && _binding is { Ready: true, BindingId: not null };
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
            if (Busy || ((Dirty || EditorDirty) && !_confirm("放弃尚未保存的参数和设备绑定并切换场景包？"))) { OnPropertyChanged(); return; }
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
        SceneLibraryUsageDto? usage = null;
        if (_library is not null)
        {
            try { usage = await _library().GetSceneLibraryUsageAsync().ConfigureAwait(true); }
            catch (StreamJsonRpc.RemoteMethodNotFoundException) { /* older Host: content remains readable */ }
        }
        if (request != Volatile.Read(ref _refreshRequest)) return;
        _librarySupported = usage is not null;
        _checkedApplySupported = true;
        NotifyState();
        Usage = usage is null ? (_library is null ? "" : "当前后台尚不支持场景库维护，请更新后台后刷新。") : $"{usage.Packages} / {usage.MaxPackages} 个包 · {usage.Bytes / 1048576.0:0.0} / {usage.MaxBytes / 1048576.0:0} MiB"
            + (usage.PendingCleanupBytes > 0 ? $"\n待清理 {usage.PendingCleanupBytes / 1048576.0:0.0} MiB（下次启动重试）" : "");
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
        else if (!Dirty && !EditorDirty) await ReadSelectionAsync().ConfigureAwait(true);
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
                    row.PropertyChanged += (_, e) =>
                    {
                        if (_loading) return;
                        if (e.PropertyName == nameof(PackageRoleChoice.Selected)) { _draftVersion++; Dirty = true; NotifyState(); PreviewChanged?.Invoke(this, EventArgs.Empty); }
                        else if (row.IsScreen && e.PropertyName is not null && (e.PropertyName.StartsWith("Screen", StringComparison.Ordinal) || e.PropertyName.StartsWith("Region", StringComparison.Ordinal) || e.PropertyName == nameof(PackageRoleChoice.UseRegion))) MarkEditorChanged();
                    };
                    Roles.Add(row);
                }
                Dirty = false; Message = binding.Ready ? "首帧预览已读取。应用后才会切换硬件内容。" : Problems(binding.Problems);
            }
            finally { _loading = false; }
            NotifyDetails();
            await ReadEditorAsync(id, request, revision).ConfigureAwait(true);
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
                yield return new(screen.Id, new ScreenChoice(screen).Name + " · " + new ScreenChoice(screen).Detail, ScreenId: screen.Id, Family: screen.Family);
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
            imported = await UploadPackageAsync(_service(), file).ConfigureAwait(true);
        }
        finally { Busy = false; }
        var hadDraft = Dirty;
        await RefreshAsync().ConfigureAwait(true);
        if (!hadDraft && !Dirty && previousSelection == Selected?.Id && revision == _draftVersion) { Selected = Packages.FirstOrDefault(p => p.Id == imported.Info.ContentId); await ReadSelectionAsync().ConfigureAwait(true); }
        Message = $"已安装“{imported.Info.Name}”。" + (hadDraft ? "当前设备绑定草稿已保留。" : "请检查设备绑定后应用。");
    }

    private const long MaxPackageBytes = 64L * 1024 * 1024;
    private const int MaxChunkBytes = 1024 * 1024;

    /// <summary>
    /// Streams the ZIP to the Host in bounded chunks, so neither side holds the whole file plus its Base64 text. The legacy single
    /// Base64 call is used ONLY when the Host does not offer chunked import at all (older Host: missing method, or NotSupported from
    /// Begin), i.e. before any upload session exists. Once a session was opened, every failure aborts it and is reported; Finish is
    /// never retried through the legacy method.
    /// </summary>
    private async Task<ScenePackageDetailsDto> UploadPackageAsync(IScenePackageService service, FileInfo file)
    {
        long length; string sha;
        await using (var hashing = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            length = hashing.Length;
            if (length <= 0 || length > MaxPackageBytes) throw new ArgumentException("场景包必须是 64 MiB 以内的 ZIP 文件。");
            Message = "正在计算场景包校验值…";
            sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(hashing).ConfigureAwait(true)).ToLowerInvariant();
        }
        ScenePackageUploadSessionDto session;
        try { session = await service.BeginScenePackageUploadAsync(new(file.Name, length, sha)).ConfigureAwait(true); }
        catch (Exception ex) when (ChunkedImportUnsupported(ex)) { return await LegacyImportAsync(service, file).ConfigureAwait(true); }
        var buffer = new byte[Math.Clamp(session.MaxChunkBytes, 1, MaxChunkBytes)];
        try
        {
            using var check = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != length) throw new IOException("场景包文件在上传期间发生变化，已取消。");
            long offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset))).ConfigureAwait(true);
                if (read == 0) throw new IOException("场景包文件在上传期间发生变化，已取消。");
                check.AppendData(buffer, 0, read);
                var progress = await service.UploadScenePackageChunkAsync(new(session.Token, offset, Convert.ToBase64String(buffer, 0, read))).ConfigureAwait(true);
                offset += read;
                if (progress.Received != offset) throw new InvalidDataException("后台确认的上传进度不一致，已取消。");
                Message = $"正在上传场景包… {offset * 100 / length}%";
            }
            if (await stream.ReadAsync(buffer.AsMemory(0, 1)).ConfigureAwait(true) != 0 || !string.Equals(Convert.ToHexString(check.GetHashAndReset()), sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("场景包文件在上传期间发生变化，已取消。");
        }
        catch
        {
            await AbortQuietlyAsync(service, session.Token).ConfigureAwait(true);
            throw;
        }
        Message = "正在校验并安装场景包…";
        return await service.FinishScenePackageUploadAsync(new(session.Token)).ConfigureAwait(true); // ends the session; never retried via legacy
    }

    private async Task<ScenePackageDetailsDto> LegacyImportAsync(IScenePackageService service, FileInfo file)
    {
        var bytes = await File.ReadAllBytesAsync(file.FullName).ConfigureAwait(true);
        if (bytes.Length > MaxPackageBytes) throw new ArgumentException("场景包超过 64 MiB。");
        Message = "正在校验并安装场景包…";
        return await service.ImportScenePackageAsync(new(file.Name, Convert.ToBase64String(bytes))).ConfigureAwait(true);
    }

    /// <summary>True only for "this Host has no chunked import": a missing RPC method, or NotSupported (local or reported by the Host).</summary>
    internal static bool ChunkedImportUnsupported(Exception ex) => ex is StreamJsonRpc.RemoteMethodNotFoundException or NotSupportedException
        || (ex is StreamJsonRpc.RemoteInvocationException { DeserializedErrorData: StreamJsonRpc.Protocol.CommonErrorData { TypeName: "System.NotSupportedException" } });

    private static async Task AbortQuietlyAsync(IScenePackageService service, string token)
    {
        try { await service.AbortScenePackageUploadAsync(new(token)).ConfigureAwait(true); }
        catch (Exception) { } // best effort: the Host also ends an idle session by itself after a short timeout
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
            var result = await service.ApplyScenePackageCheckedAsync(new(id, bindingId!)).ConfigureAwait(true);
            if (!result.Applied && result.BindingId != bindingId) Dirty = true;
            Message = result.Applied ? "场景包已应用，请查看实际灯光与屏幕效果。" : "应用未完成，原场景保留。\n" + result.Detail;
            if (result.Applied) Applied?.Invoke(this, EventArgs.Empty);
        }
        catch (StreamJsonRpc.RemoteMethodNotFoundException)
        {
            _checkedApplySupported = false;
            Message = "当前后台版本不支持绑定版本校验。请更新或重启后台，再刷新场景库后重试。原场景保持不变。";
            NotifyState();
        }
        finally { Busy = false; }
    }

    private async Task RemoveAsync()
    {
        if (!CanEdit || Dirty || !MaintenanceAvailable || Selected is null || _library is null) return;
        var id = Selected.Id; var clean = CleanRelatedMedia; var removed = false;
        Busy = true;
        try
        {
            var service = _library();
            var plan = await service.InspectScenePackageRemovalAsync(id).ConfigureAwait(true);
            if (plan.References.Count > 0)
            {
                Message = "此包仍被引用，未删除：\n" + string.Join("\n", plan.References); return;
            }
            if (!_confirm($"删除场景包“{plan.Name}”？\n当前播放不会切换，本机绑定历史会保留。"
                + (clean ? "\n同时清理未被引用的同源屏幕素材（包括以相同文件单独上传的素材）。" : "\n屏幕素材缓存保留。"))) return;
            var result = await service.RemoveScenePackageAsync(new(id, clean)).ConfigureAwait(true);
            removed = result.Removed;
            Message = result.Removed ? $"场景包已删除，已回收 {result.ReclaimedBytes / 1048576.0:0.0} MiB。"
                + (result.CleanupPending ? "部分文件仍被占用或含未知内容；已保留，后台重启时重试。" : "")
                : "此包仍被引用，未删除：\n" + string.Join("\n", result.References);
        }
        finally { Busy = false; }
        if (removed)
        {
            var message = Message;
            await RefreshAsync().ConfigureAwait(true);
            Message = message;
        }
    }

    private void ClearDetails() { ClearEditor(); _details = null; _binding = null; Roles.Clear(); Dirty = false; NotifyDetails(); }
    private void NotifyDetails() { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(Provenance)); OnPropertyChanged(nameof(HasDetails)); NotifyState(); }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(BindingStatus));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(MaintenanceAvailable));
        RefreshCommand.RaiseCanExecuteChanged(); ImportCommand.RaiseCanExecuteChanged(); SaveBindingsCommand.RaiseCanExecuteChanged(); ApplyCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        NotifyEditorState();
    }
    private static string Problems(IReadOnlyList<string> problems) => problems.Count == 0 ? "为每个内容角色选择设备，然后保存绑定。" : "绑定尚未就绪：\n" + string.Join("\n", problems.Select(p => p == "No binding has been saved for this package." ? "此场景包尚未保存设备绑定。" : p));
    private void Report(Exception ex) { Message = "操作未完成：" + ex.Message; _error(ex); }
}
