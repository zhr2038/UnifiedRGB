using System.Text.Json;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Firmware;

/// <summary>Host-owned singleton. UI disconnect does not own task/process lifetime. All mutations are
/// revision-bound and durably deduplicated. A lost response is resolved by reading, never replaying.</summary>
public sealed class FirmwareJobBroker : IFirmwareUpdateService, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly FirmwareInstalledOptions _options;
    private readonly string _jobsDirectory;
    private readonly IFirmwareMaintenance _maintenance;
    private readonly PhysicalWorkflow _physical;
    private readonly IFirmwareWorkerRunner _runner;
    private FirmwareJobStatus? _status;
    private ReleasePackage? _package;
    private CatalogEntry? _catalog;
    private PhysicalBinding? _binding;
    private Guid? _leaseOwner;
    private bool _interfacesReleased;
    private string? _parentLedger, _parentBefore, _parentLedgerHash, _parentLineageHash;
    private Task? _ownedTask;
    private CancellationTokenSource? _prepareCancellation;
    private string? _operationDirectory;
    private readonly HashSet<Guid> _requests = [];
    private readonly Dictionary<Guid, FirmwareJobStatus> _history = [];
    private string? _featureError;
    private bool _historyUnavailable;
    private RetainedBaselineSelection? _retainedSelection;
    private RetainedBaselineReceipt? _retainedReceipt;
    private string? _actualAdmissionBackup;

    public FirmwareJobBroker(FirmwareInstalledOptions options, IFirmwareMaintenance maintenance,
        PhysicalWorkflow physical, IFirmwareWorkerRunner runner, string jobsDirectory)
    {
        _options = options; _maintenance = maintenance; _physical = physical; _runner = runner;
        _jobsDirectory = Path.GetFullPath(jobsDirectory);
        _featureError = options.FeatureError;
        try
        {
            LoadHistory();
            if (_status is null && _featureError is not null)
                _status = new(Guid.Empty, 0, new("", "固件功能已禁用", "", "", "", "", "", false, _featureError),
                    FirmwarePhase.Failed, _featureError, false, false, true, 0, "未知", _jobsDirectory, null, null, DateTimeOffset.UtcNow, FeatureError: _featureError);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _featureError = "固件回执无效或超出限制；原文件和未知维护状态已保留：" + ex.GetType().Name;
            _historyUnavailable = true;
            _status = new(Guid.Empty, 0, new("", "固件功能暂不可用", "", "", "", "", "", false, _featureError),
                FirmwarePhase.NeedsReviewedRecovery, _featureError, false, false, false, 0, "未知", _jobsDirectory, null, null, DateTimeOffset.UtcNow, FeatureError: _featureError);
        }
    }
    private void LoadHistory()
    {
        FirmwareFiles.NoReparse(_jobsDirectory);
        // Startup only reads local receipts; never probes, resumes or starts an executable.
        if (!Directory.Exists(_jobsDirectory)) return;
        var jobs = Directory.EnumerateDirectories(_jobsDirectory).Take(1025).ToArray();
        if (jobs.Length > 1024) throw new InvalidDataException("Retained job directory bound; archive through reviewed local maintenance, never silently discard jobs");
        var remaining = 8192;
        foreach (var job in jobs)
        {
            FirmwareFiles.NoReparse(job);
            var receipts = Directory.EnumerateFiles(job, "status-*.json").Take(remaining + 1).ToArray();
            remaining -= receipts.Length;
            if (remaining < 0) throw new InvalidDataException("Global immutable receipt enumeration bound exceeded");
            if (receipts.Length == 0) continue;
            var last = receipts.Order(StringComparer.Ordinal).Last();
            var status = FirmwareFiles.Parse<FirmwareJobStatus>(FirmwareFiles.ReadBounded(last, 65536));
            if (status.JobId == Guid.Empty || Path.GetFullPath(status.ReceiptDirectory) != Path.GetFullPath(job) ||
                Path.GetFileName(job) != status.JobId.ToString("N") || status.Revision < 1 || status.Package is null)
                throw new InvalidDataException("Local job receipt ownership/path mismatch");
            _history.Add(status.JobId, status);
        }
        if (_history.Count == 0) return;
        _status = _history.Values.OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.Revision).First();
        if (_status.Phase != FirmwarePhase.Finished && _status.Phase != FirmwarePhase.Loaded)
        {
            _leaseOwner = _status.JobId;
            _status = _status with { Phase = FirmwarePhase.NeedsReviewedRecovery, Running = false, CanCancel = false,
                WireClosed = false, Detail = "后台重启后结果未知：保留维护、备份及账本，不自动重试。须审核恢复。" };
        }
    }

    public Task<FirmwareJobStatus> LoadFirmwarePackageAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var package = ReleasePackage.Load(path); // read-only even with default-disabled hardware
        CatalogEntry? catalog = null;
        try { catalog = new InstalledTrust(_options).Review(package); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _featureError = "本机固件审核目录无效；仅检查文件：" + ex.GetType().Name; }
        lock (_gate)
        {
            if (_historyUnavailable) throw new InvalidOperationException(_featureError);
            if (_status?.Running == true || _leaseOwner is not null) throw new InvalidOperationException("An owned firmware job still requires explicit safe finish");
            var id = Guid.NewGuid(); var directory = Path.Combine(_jobsDirectory, id.ToString("N"));
            Directory.CreateDirectory(directory); FirmwareFiles.NoReparse(directory);
            // Snapshot the exact validated zip, never depend on a mutable picker path after admission.
            package.MaterializeSnapshot(Path.Combine(directory, "package.urgbfw"));
            _package = package; _catalog = catalog; _binding = null;
            _retainedSelection = null; _retainedReceipt = null; _actualAdmissionBackup = null;
            _parentLedger = _parentBefore = _parentLedgerHash = _parentLineageHash = null;
            var r = package.Release;
            var info = new FirmwarePackageInfo(r.ReleaseId, r.Title, r.Version, r.Notes, package.BundleSha256, r.Candidate.Sha256, r.Rollback.Sha256,
                catalog is not null, _featureError ?? (catalog is null ? "本机未安装此版本的独立审核记录；仅可检查文件" : "匹配本机软件审核目录；仍需实际设备准备与明确操作"), _options.HardwareEnabled && _featureError is null,
                catalog?.RetainedBaseline is not null);
            _status = new(id, 0, info, FirmwarePhase.Loaded, "包检查完成；当前设备未知。加载不会连接设备或启动刷写。", false, false, true, 0,
                "未知（尚未明确准备）", directory, null, null, DateTimeOffset.UtcNow, FeatureError: _featureError);
            return Task.FromResult(Publish(_status));
        }
    }

    public Task<FirmwareJobStatus?> GetFirmwareJobAsync(Guid? jobId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (jobId is null || _status?.JobId == jobId) return Task.FromResult(_status);
            return Task.FromResult(_history.GetValueOrDefault(jobId.Value));
        }
    }

    public Task<FirmwareJobStatus> UseRetainedFirmwareBaselineAsync(FirmwareRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = Check(request, hardware: false, ct);
            if (_requests.Contains(request.RequestId)) return Task.FromResult(current);
            if (current.Running || current.InstallRunId is not null || _catalog is null || _package is null ||
                current.Phase is not (FirmwarePhase.Loaded or FirmwarePhase.Prepared or FirmwarePhase.BackupComplete))
                throw new InvalidOperationException("Select retained reference only for a reviewed idle release before installation");
            var reviewed = new InstalledTrust(_options).Review(_package) ?? throw new InvalidDataException("Installed review mismatch");
            var (selection, receipt) = RetainedBaselinePolicy.LoadInstalled(_options, reviewed, _package);
            Accept(request);
            var directory = NewOperationDirectory("retained-reference");
            var image = Path.Combine(directory, "owner-confirmed-reference.bin");
            var receiptPath = Path.Combine(directory, "existing-owner-receipt.json");
            FirmwareFiles.CreateNew(image, FirmwareFiles.ReadBounded(selection.ImagePath, FirmwareFiles.ImageLength));
            FirmwareFiles.CreateNew(receiptPath, FirmwareFiles.ReadBounded(selection.ReceiptPath, 32768));
            _retainedSelection = selection with { ImagePath = image, ReceiptPath = receiptPath }; _retainedReceipt = receipt;
            RetainedBaselinePolicy.Validate(_retainedSelection, reviewed, _package);
            return Task.FromResult(Publish(current with { AdmissionReferencePath = image, AdmissionReferenceOrigin = RetainedBaselinePolicy.Origin,
                CurrentFirmware = "0.25（机主确认既有 2DCB 基线，未重新读取）",
                AdmissionReceiptPath = receiptPath, Detail = "已明确选择可信保留 2DCB / 0.25 基线（机主确认 / 既有回执，不是新读取）。可跳过单独备份；安装仍会实际双遍读取并保存刷机前镜像。" }));
        }
    }

    public Task<FirmwareJobStatus> PrepareFirmwareAsync(FirmwareRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = Check(request, hardware: true, ct);
            if (_requests.Contains(request.RequestId)) return Task.FromResult(current);
            if (current.Running || _ownedTask is { IsCompleted: false } || current.Phase is not (FirmwarePhase.Loaded or FirmwarePhase.Prepared or FirmwarePhase.BackupComplete or FirmwarePhase.AwaitingNormalReplug or FirmwarePhase.Cancelled) &&
                !(current.Phase == FirmwarePhase.Failed && current.WireClosed))
                throw new InvalidOperationException("Prepare not allowed in current phase");
            if (!request.OperatorConfirmedOtherWritersAbsent) throw new InvalidDataException("请明确确认已关闭其他固件工具；设备元数据不能证明没有其他写入者");
            Accept(request);
            _prepareCancellation?.Dispose();
            _prepareCancellation = new CancellationTokenSource();
            var token = _prepareCancellation.Token;
            var directory = NewOperationDirectory("physical");
            var published = Publish(current with { Phase = FirmwarePhase.Preparing, Running = true, CanCancel = true, Detail = "正在释放并等待键鼠会话退出，然后观察正常 USB 物理连接" });
            _ownedTask = Task.Run(async () =>
            {
                var entryAdmitted = false;
                try
                {
                    if (_featureError is not null) throw new InvalidOperationException(_featureError);
                    if (!_interfacesReleased)
                    {
                        // Mark local ownership before awaiting: release failure must not allow a replacement job.
                        lock (_gate) _leaseOwner = request.JobId;
                        await _maintenance.AcquireAsync(request.JobId, token).ConfigureAwait(false);
                        lock (_gate) _interfacesReleased = true;
                    }
                    Update(s => s with { Phase = FirmwarePhase.Preparing,
                        Detail = "键盘与鼠标控制已释放。请将键盘切到 USB 有线模式并保持线缆连接；程序会等待最多 120 秒，检测到正常 USB 接口后继续。此时尚未发送升级命令。" });
                    var binding = await _physical.PrepareAsync(directory, () => Update(s => s with
                    { Phase = FirmwarePhase.AwaitingBoot, Detail = request.AutomaticallyEnterUpgrade ?
                        "已绑定正常 USB；准备仅发送一次官方进入升级命令，不读取正常 ACK。" : "手动后备：请物理进入升级模式；仅观察同端口转换。" }), token,
                        new(request.JobId, _interfacesReleased, request.OperatorConfirmedOtherWritersAbsent), _retainedReceipt,
                        request.AutomaticallyEnterUpgrade, () =>
                        {
                            entryAdmitted = true;
                            Update(s => s with { Phase = FirmwarePhase.EnteringUpgrade, WireClosed = false,
                                Detail = "进入升级命令已准入；正在等待写入结束并关闭旧句柄，只观察同端口升级元数据。取消后也不能保证没有发生切换。" });
                        }).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_binding is not null && _binding.DeviceKey != binding.DeviceKey) throw new InvalidDataException("A new transition changed exact physical identity; no alias permitted");
                        _binding = binding;
                        Publish(_status! with { Phase = _status.BackupPath is null ? FirmwarePhase.Prepared : FirmwarePhase.BackupComplete,
                            Running = false, CanCancel = false, WireClosed = true, CurrentFirmware = binding.NormalHeaderOrigin == RetainedBaselinePolicy.Origin ?
                                "0.25（机主确认既有 2DCB 基线；仅观察 USB 物理连接，未 GET12）" : binding.NormalFirmwareHeader,
                            Detail = _status.InstallRunId is not null ? "已观察新的同一设备物理引导会话；原始备份与确切父安装账本保留，可明确恢复刷机前固件。" :
                                 _status.BackupPath is not null ? "已观察新的同一设备物理引导会话；已完成的原始备份保持不变，可明确安装。" :
                                 _retainedSelection is not null ? "已观察真实正常→升级转换；保留参考来源不变，可明确安装，也可选做单独备份。安装仍执行实际双遍前镜像核验。" :
                                 "已观察同一设备的正常→引导转换。请明确备份；备份不会安装。" });
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { Update(s => s with { Phase = entryAdmitted || ex is TransitionUncertainException ? FirmwarePhase.NeedsReviewedRecovery :
                        ex is OperationCanceledException ? FirmwarePhase.Cancelled : FirmwarePhase.Failed,
                    Running = false, CanCancel = false, WireClosed = !entryAdmitted,
                    Detail = entryAdmitted ? "已尝试进入升级，结果未建立；保留 intent 与维护，禁止重发，须正常重连/审核恢复。" + ex.Message : ex.Message }); }
            }, CancellationToken.None);
            return Task.FromResult(published);
        }
    }

    public Task<FirmwareJobStatus> BackupFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Start(request, FirmwareOperation.Backup, ct);
    public Task<FirmwareJobStatus> InstallFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Start(request, FirmwareOperation.Install, ct);
    public Task<FirmwareJobStatus> RestoreFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Start(request, FirmwareOperation.Restore, ct);
    private Task<FirmwareJobStatus> Start(FirmwareRequest request, FirmwareOperation operation, CancellationToken ct)
    {
        lock (_gate)
        {
            var current = Check(request, hardware: true, ct);
            if (_requests.Contains(request.RequestId)) return Task.FromResult(current);
            if (current.Running || _binding is null || _leaseOwner != current.JobId) throw new InvalidOperationException("Prepared owned physical job required");
            if (operation == FirmwareOperation.Backup && current.Phase != FirmwarePhase.Prepared ||
                operation == FirmwareOperation.Install && (current.Phase is not (FirmwarePhase.BackupComplete or FirmwarePhase.Prepared) ||
                    _actualAdmissionBackup is null && _retainedSelection is null || current.InstallRunId is not null) ||
                operation == FirmwareOperation.Restore && (current.Phase != FirmwarePhase.BackupComplete || current.InstallRunId is null ||
                    request.ParentInstallRunId != current.InstallRunId || _parentLedger is null))
                throw new InvalidOperationException("Explicit operation/backup/exact parent confirmation mismatch");
            PhysicalWorkflow.RequireFresh(_binding); // BEFORE executable selection, inventory and open
            _catalog = new InstalledTrust(_options).Review(_package!) ?? throw new InvalidDataException("Installed release review no longer matches");
            Accept(request);
            var runId = Guid.NewGuid(); var directory = NewOperationDirectory(operation.ToString()); _operationDirectory = directory;
            var workerRequest = new FirmwareWorkerRequest(current.JobId, runId, operation, Path.Combine(current.ReceiptDirectory, "package.urgbfw"),
                current.Package.BundleSha256, current.Package.ReleaseId, directory, _binding, _actualAdmissionBackup,
                _parentLedger, current.InstallRunId, _parentLedgerHash, _parentLineageHash, _parentBefore, _actualAdmissionBackup is null ? _retainedSelection : null);
            var phase = operation switch { FirmwareOperation.Backup => FirmwarePhase.BackingUp, FirmwareOperation.Install => FirmwarePhase.Installing, _ => FirmwarePhase.Restoring };
            var published = Publish(current with { Phase = phase, Running = true, CanCancel = true, WireClosed = false, VerifiedPages = 0,
                Detail = "正在执行只读双遍当前镜像校验；擦除前仍可请求取消" });
            _ownedTask = Task.Run(async () =>
            {
                try
                {
                    var result = await _runner.RunAsync(_catalog, workerRequest, p => Update(s => s with
                    { Phase = p.Phase, CanCancel = !p.Destructive, VerifiedPages = p.VerifiedPages, Detail = p.Detail })).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (operation == FirmwareOperation.Backup && result.Phase == FirmwarePhase.BackupComplete)
                            _actualAdmissionBackup = result.BackupPath;
                        if (operation == FirmwareOperation.Install && result.Destructive)
                        {
                            _parentLedger = result.LedgerPath; _parentBefore = Path.Combine(directory, "before-image.bin");
                            _parentLedgerHash = FirmwareFiles.HashFile(_parentLedger); _parentLineageHash = FirmwareFiles.HashFile(_parentLedger + ".lineage.json");
                        }
                        Publish(_status! with { Phase = result.Phase, Detail = result.Detail, Running = false, CanCancel = false,
                            WireClosed = result.WireClosed, VerifiedPages = result.VerifiedPages, BackupPath = result.BackupPath ?? _status.BackupPath,
                            BeforeImageProvenancePath = result.BeforeImageProvenancePath ?? _status.BeforeImageProvenancePath,
                            ObserverDiagnostic = result.ObserverDiagnostic,
                            AdmissionReferencePath = operation == FirmwareOperation.Backup && result.Phase == FirmwarePhase.BackupComplete ? result.BackupPath : _status.AdmissionReferencePath,
                            AdmissionReferenceOrigin = operation == FirmwareOperation.Backup && result.Phase == FirmwarePhase.BackupComplete ? RetainedBaselinePolicy.ActualReadOrigin : _status.AdmissionReferenceOrigin,
                            AdmissionReceiptPath = operation == FirmwareOperation.Backup && result.Phase == FirmwarePhase.BackupComplete ? result.BeforeImageProvenancePath : _status.AdmissionReceiptPath,
                            InstallRunId = operation == FirmwareOperation.Install && result.Destructive ? runId : _status.InstallRunId });
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { Update(s => s with { Phase = FirmwarePhase.NeedsReviewedRecovery, Detail = "结果未知：不自动重试。" + ex.Message,
                    Running = false, CanCancel = false, WireClosed = false }); }
            }, CancellationToken.None);
            return Task.FromResult(published);
        }
    }

    public Task<FirmwareJobStatus> CancelFirmwareIfSafeAsync(FirmwareRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = Check(request, hardware: false, ct);
            if (_requests.Contains(request.RequestId)) return Task.FromResult(current);
            if (!current.Running || !current.CanCancel) throw new InvalidOperationException("Cancellation unsafe or not running");
            Accept(request);
            if (_prepareCancellation is not null && current.Phase is FirmwarePhase.Preparing or FirmwarePhase.AwaitingBoot or FirmwarePhase.EnteringUpgrade) _prepareCancellation.Cancel();
            else if (_operationDirectory is not null) _runner.RequestCancel(_operationDirectory);
            return Task.FromResult(Publish(current with { CanCancel = false, Detail = "已请求仅在擦除前取消；以后台回读结果为准，界面关闭不会停止任务。" }));
        }
    }
    public Task<FirmwareJobStatus> FinishFirmwareAsync(FirmwareRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = Check(request, hardware: true, ct);
            if (_requests.Contains(request.RequestId)) return Task.FromResult(current);
            if (current.Running || !current.WireClosed || current.Phase == FirmwarePhase.NeedsReviewedRecovery || !_interfacesReleased || _leaseOwner != current.JobId || _ownedTask is { IsCompleted: false })
                throw new InvalidOperationException("Cannot finish an active/unknown/unjoined job");
            Accept(request);
            var published = Publish(current with { Running = true, CanCancel = false, Detail = "等待正常 USB 重插与全新会话确认；仍保持维护" });
            _ownedTask = Task.Run(async () =>
            {
                try
                {
                    var normal = _binding is null
                        ? await _physical.ConfirmNormalWithoutWorkerAsync(CancellationToken.None, _retainedReceipt).ConfigureAwait(false)
                        : await _physical.ConfirmNormalAsync(_binding, CancellationToken.None).ConfigureAwait(false);
                    if (!normal) throw new InvalidOperationException("Fresh normal reconnect not observed");
                    await _maintenance.FinishAsync(current.JobId, true, true, CancellationToken.None).ConfigureAwait(false);
                    lock (_gate) { _leaseOwner = null; _interfacesReleased = false; Publish(_status! with { Phase = FirmwarePhase.Finished, Running = false, Detail = "维护已明确完成；新会话接管当前设置，不回放旧场景或配置" }); }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { Update(s => s with { Running = false, Detail = "完成维护失败，保持阻止接管。" + ex.Message }); }
            }, CancellationToken.None);
            return Task.FromResult(published);
        }
    }

    private FirmwareJobStatus Check(FirmwareRequest request, bool hardware, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = _status ?? throw new InvalidOperationException("No loaded firmware job");
        if (request.JobId != s.JobId || request.ReleaseId != s.Package.ReleaseId || request.BundleSha256 != s.Package.BundleSha256 || request.RequestId == Guid.Empty)
            throw new InvalidDataException("Exact job/release/bundle confirmation required");
        if (_requests.Contains(request.RequestId)) return s;
        if (request.ExpectedRevision != s.Revision) throw new InvalidOperationException("Stale click; read current status, never resend uncertain mutations");
        if (hardware && (!_options.HardwareEnabled || _catalog is null || _package is null || _featureError is not null)) throw new InvalidOperationException(_featureError ?? "本机未启用硬件流程或缺少独立审核目录；仅允许文件检查");
        return s;
    }
    private void Accept(FirmwareRequest request)
    {
        FirmwareFiles.CreateNew(Path.Combine(_status!.ReceiptDirectory, $"request-{request.RequestId:N}.json"), JsonSerializer.SerializeToUtf8Bytes(request));
        _requests.Add(request.RequestId);
    }
    private string NewOperationDirectory(string prefix)
    { var path = Path.Combine(_status!.ReceiptDirectory, prefix + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private void Update(Func<FirmwareJobStatus, FirmwareJobStatus> update) { lock (_gate) Publish(update(_status!)); }
    private FirmwareJobStatus Publish(FirmwareJobStatus value)
    {
        value = value with { Revision = value.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        var path = Path.Combine(value.ReceiptDirectory, $"status-{value.Revision:D8}.json");
        _status = value; _history[value.JobId] = value; // in-memory ownership/running is authoritative even if receipt storage fails
        try { FirmwareFiles.CreateNew(path + ".pending", JsonSerializer.SerializeToUtf8Bytes(value)); File.Move(path + ".pending", path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _featureError = "固件回执写入失败；保留任务所有权，禁止新操作：" + ex.GetType().Name;
            _status = value with { FeatureError = _featureError, Detail = value.Detail + "；" + _featureError };
            _history[value.JobId] = _status;
        }
        return _status;
    }
    public async ValueTask DisposeAsync()
    {
        Task? owned;
        lock (_gate) owned = _ownedTask;
        if (owned is not null) await owned.ConfigureAwait(false);
        _prepareCancellation?.Dispose(); // never cancel or kill an in-flight destructive operation
    }
}
