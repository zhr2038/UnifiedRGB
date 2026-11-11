using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.App.Workbench;

/// <summary>All reads are passive. Mutations are single-send; uncertainty is read back, never resent.
/// Losing a UI connection is not cancellation and cannot stop the Host-owned worker.</summary>
public sealed class FirmwareUpdateViewModel : ObservableObject
{
    private readonly Func<IFirmwareUpdateService> _service;
    private readonly IUiDispatcher _ui;
    private readonly Func<string, bool> _confirm;
    private readonly Func<string?> _pickPackage;
    private readonly Action<FirmwareJobStatus> _export;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _timeout;
    private FirmwareJobStatus? _status;
    private DateTimeOffset? _observed;
    private DateTimeOffset _clockHighWater;
    private bool _connected = true, _busy, _uncertain, _unsupported;
    private long _epoch, _readVersion;
    private string _message = "请选择准备好的固件包。加载只检查文件，不会开始刷写。";

    public FirmwareUpdateViewModel(Func<IFirmwareUpdateService> service, IUiDispatcher ui,
        Func<string?> pickPackage, Func<string, bool> confirm, Action<FirmwareJobStatus> export,
        Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        _service = service; _ui = ui; _pickPackage = pickPackage; _confirm = confirm; _export = export;
        _now = now ?? (() => DateTimeOffset.UtcNow); _timeout = timeout ?? TimeSpan.FromSeconds(5);
        LoadCommand = new AsyncCommand(_ => LoadAsync(), Report, _ => CanLoad);
        ReadCommand = new AsyncCommand(_ => RefreshAsync(), Report);
        PrepareCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.PrepareFirmwareAsync(r), "请关闭其他刷机工具并连接 USB 线，先保持键盘当前连接模式，等待程序释放原有控制。页面提示控制已释放后，再将键盘切到 USB 有线模式，程序会自动进入升级模式。此步骤不会刷写固件。", confirmOtherWriters: true, automaticEntry: true), Report, _ => CanPrepare);
        ManualPrepareCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.PrepareFirmwareAsync(r), "请关闭其他刷机工具，再按提示手动进入升级模式。", confirmOtherWriters: true), Report, _ => CanPrepare);
        ReuseRetainedCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.UseRetainedFirmwareBaselineAsync(r), "沿用已确认的 0.25 开发版作为安装参考？写入前仍会自动保存当前固件。"), Report, _ => CanReuseRetained);
        BackupCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.BackupFirmwareAsync(r), null), Report, _ => CanBackup);
        InstallCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.InstallFirmwareAsync(r), "开始安装所选固件？程序会先保存当前固件，再写入并完整校验。擦写开始后不能取消，请保持 USB 连接。"), Report, _ => CanInstall);
        RestoreCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.RestoreFirmwareAsync(r), "恢复本次确切安装前的不可变备份（不是出厂恢复）。将擦写键盘固件，擦除后不能取消。"), Report, _ => CanRestore);
        CancelCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.CancelFirmwareIfSafeAsync(r), null), Report, _ => Fresh is { Running: true, CanCancel: true } && Idle);
        FinishCommand = new AsyncCommand(_ => MutateAsync((s, r) => s.FinishFirmwareAsync(r), null), Report, _ => CanFinish);
        ExportCommand = new RelayCommand(_ => { if (_status is not null) _export(_status); }, _ => _status is not null);
    }
    public AsyncCommand LoadCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand PrepareCommand { get; }
    public AsyncCommand ManualPrepareCommand { get; }
    public AsyncCommand ReuseRetainedCommand { get; }
    public AsyncCommand BackupCommand { get; }
    public AsyncCommand InstallCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand FinishCommand { get; }
    public RelayCommand ExportCommand { get; }
    public FirmwareJobStatus? Status => _status;
    public bool HasTask => _status is not null;
    public bool CanLoad => Idle && (_status is null || Fresh is { Running: false, Phase: FirmwarePhase.Loaded or FirmwarePhase.Finished });
    private FirmwareJobStatus? Fresh => _connected && !_unsupported && !_uncertain && _observed is { } at &&
        _now() >= _clockHighWater && _now() - at >= TimeSpan.Zero && _now() - at <= TimeSpan.FromSeconds(10) && _status?.UpdatedAt <= _now() ? _status : null;
    private bool Idle => _connected && !_busy && !_unsupported && !_uncertain;
    private bool HardwareReady => Idle && Fresh is { Package.Trusted: true, Package.HardwareEnabled: true, FeatureError: null };
    public bool CanPrepare => HardwareReady && Fresh is { Running: false, WireClosed: true, Phase: FirmwarePhase.Loaded or FirmwarePhase.Prepared or FirmwarePhase.BackupComplete or FirmwarePhase.AwaitingNormalReplug or FirmwarePhase.Failed or FirmwarePhase.Cancelled };
    public bool CanReuseRetained => Idle && Fresh is { Running: false, Package.Trusted: true, Package.RetainedBaselineAvailable: true, InstallRunId: null, Phase: FirmwarePhase.Loaded or FirmwarePhase.Prepared or FirmwarePhase.BackupComplete };
    public bool CanBackup => HardwareReady && Fresh is { Running: false, Phase: FirmwarePhase.Prepared };
    public bool CanInstall => HardwareReady && Fresh is { Running: false, Phase: FirmwarePhase.BackupComplete or FirmwarePhase.Prepared, AdmissionReferencePath: not null, InstallRunId: null };
    public bool CanRestore => HardwareReady && Fresh is { Running: false, Phase: FirmwarePhase.BackupComplete, InstallRunId: not null };
    public bool CanFinish => HardwareReady && Fresh is { Running: false, WireClosed: true, Phase: FirmwarePhase.AwaitingNormalReplug or FirmwarePhase.BackupComplete or FirmwarePhase.Cancelled or FirmwarePhase.Failed };
    public string AdmissionReferenceText => _status?.AdmissionReferenceOrigin switch
    {
        "OwnerConfirmedExistingReceipt" => "已沿用确认过的开发版参考；安装时会另行保存当前固件。",
        "ActualTwoPassRead" => "已使用本次读取的完整备份作为安装参考。",
        _ => "可沿用已确认的开发版参考，也可单独备份当前固件。",
    };
    public string Message => _message;
    public string Title => _status is null ? "尚未加载固件包" : $"{_status.Package.Title} · {_status.Package.Version}";
    public string Notes => _status?.Package.Notes ?? "选择已准备好的固件包，查看版本与更新内容。";
    public string Device => $"连接方式：USB · 当前固件：{_status?.CurrentFirmware ?? "等待准备"}";
    public string Trust => _status is null ? "等待选择固件" : !_status.Package.Trusted ? "该固件尚未列入本机审核目录，不能安装。" : !_status.Package.HardwareEnabled ? "固件已核验，升级模块尚未启用。" : "固件来源与文件已核验";
    public string Detail => _status?.Detail ?? "加载文件后，可自动切换键盘升级接口。";
    public string PhaseTitle => _status?.Phase switch
    {
        FirmwarePhase.Loaded => "文件检查完成（尚未刷写）", FirmwarePhase.Preparing => "准备维护",
        FirmwarePhase.AwaitingBoot => "等待升级接口连接", FirmwarePhase.EnteringUpgrade => "正在自动进入升级模式", FirmwarePhase.Prepared => "升级接口已就绪",
        FirmwarePhase.BackingUp => "只读双遍备份中（不擦除）",
        FirmwarePhase.BackupComplete when _status?.InstallRunId is not null => "原始备份与新的物理会话已就绪（可明确恢复）",
        FirmwarePhase.BackupComplete => "备份完成，尚未安装",
        FirmwarePhase.Installing => "安装预检 / 写入", FirmwarePhase.Restoring => "恢复刷机前固件",
        FirmwarePhase.Verifying => "最终完整镜像核验", FirmwarePhase.AwaitingNormalReplug => "核验完成，等待正常 USB 重插",
        FirmwarePhase.NeedsReviewedRecovery => "结果未知，需要审核恢复", FirmwarePhase.Finished => "维护已完成",
        FirmwarePhase.Cancelled => "已确认在擦除前取消", FirmwarePhase.Failed => "操作失败，维护仍保留", _ => "等待后台状态",
    };
    public bool Indeterminate => _status?.Running == true && _status.VerifiedPages == 0;
    public int VerifiedPages => _status?.VerifiedPages ?? 0;
    public string ProgressText => $"已校验 {VerifiedPages} / 55 页";
    public string BackupPath => _status?.BackupPath ?? "尚无实际读取的刷机前备份（保留参考不计为新备份）";
    public string Diagnostics => _status is null ? "无任务" : $"任务 {_status.JobId}\n版本 {_status.Revision}\n包 SHA-256 {_status.Package.BundleSha256}\n目标 SHA-256 {_status.Package.CandidateSha256}\n回退 SHA-256 {_status.Package.RollbackSha256}\n回执目录 {_status.ReceiptDirectory}\n父安装 {_status.InstallRunId}\n设备通道已关闭：{_status.WireClosed}\n安装参考来源：{_status.AdmissionReferenceOrigin}\n安装参考：{_status.AdmissionReferencePath}\n实际操作前镜像：{_status.BackupPath}\n实际操作前镜像凭证：{_status.BeforeImageProvenancePath}\n观察/日志诊断（与固件核验结论分开）：{_status.ObserverDiagnostic ?? "无"}";

    public void SetConnected(bool connected)
    {
        _epoch++; _connected = connected; _observed = null; _busy = false; _unsupported = false;
        _message = connected ? "已重连，请读取后台持久任务；不会重新发送上次操作。" : "后台已断开；固件任务继续，不表示取消。重连后读取状态。";
        Changed();
    }
    public async Task RefreshAsync()
    {
        if (!_connected) return;
        var epoch = _epoch; var version = ++_readVersion;
        try
        {
            var service = _service();
            var status = await service.GetFirmwareJobAsync().WaitAsync(_timeout).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (epoch != _epoch || version != _readVersion || _busy) return;
                _status = status; ObserveClock(); _uncertain = false;
                _message = "状态来自后台；读取不会开始、重试或完成任务。"; Changed();
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PostError(ex, epoch, mutation: false); }
    }
    public async Task LoadAsync()
    {
        if (!Idle) return;
        var path = _pickPackage(); if (path is null) return;
        var epoch = _epoch; _busy = true; ++_readVersion; Changed();
        try
        {
            var service = _service();
            var result = await service.LoadFirmwarePackageAsync(path).WaitAsync(_timeout).ConfigureAwait(false);
            Apply(result, epoch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PostError(ex, epoch, mutation: true); }
    }
    private async Task MutateAsync(Func<IFirmwareUpdateService, FirmwareRequest, Task<FirmwareJobStatus>> send, string? confirmation, bool confirmOtherWriters = false, bool automaticEntry = false)
    {
        var current = Fresh;
        if (!Idle || current is null) return;
        if (confirmation is not null && !_confirm($"{confirmation}\n\n{current.Package.Title} {current.Package.Version}\n任务：{current.JobId}\n包：{current.Package.BundleSha256}")) return;
        var epoch = _epoch;
        var request = new FirmwareRequest(Guid.NewGuid(), current.JobId, current.Revision, current.Package.ReleaseId, current.Package.BundleSha256, current.InstallRunId, confirmOtherWriters, automaticEntry);
        _busy = true; ++_readVersion; _observed = null; Changed();
        try
        {
            var service = _service();
            var result = await send(service, request).WaitAsync(_timeout).ConfigureAwait(false);
            Apply(result, epoch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PostError(ex, epoch, mutation: true); }
    }
    private void Apply(FirmwareJobStatus status, long epoch) => _ui.Post(() =>
    {
        if (epoch != _epoch) return;
        _status = status; ObserveClock(); _busy = false; _uncertain = false; _message = "请求已接收，请以阶段回读为准。关闭界面不会停止任务。"; Changed();
    });
    private void ObserveClock() { _observed = _now(); if (_observed > _clockHighWater) _clockHighWater = _observed.Value; }
    private void PostError(Exception ex, long epoch, bool mutation) => _ui.Post(() =>
    {
        if (epoch != _epoch) return;
        _busy = false; _observed = null;
        if (ex is RemoteMethodNotFoundException or NotSupportedException)
        { _unsupported = true; _message = "当前后台不支持固件更新；未启动任何固件操作。请升级后台后重新连接。"; }
        else
        { _uncertain |= mutation; _message = mutation ? "请求结果未确认；不自动重发，请读取后台任务。" : "状态读取失败，不使用旧状态授权操作。"; }
        Changed();
    });
    private void Report(Exception ex) { _message = "操作未完成：" + ex.Message; Changed(); }
    private void Changed()
    {
        foreach (var name in new[] { nameof(Status), nameof(HasTask), nameof(CanLoad), nameof(Message), nameof(Title), nameof(Notes), nameof(Device), nameof(Trust), nameof(Detail), nameof(PhaseTitle), nameof(Indeterminate), nameof(VerifiedPages), nameof(ProgressText), nameof(BackupPath), nameof(Diagnostics), nameof(CanPrepare), nameof(CanBackup), nameof(CanInstall), nameof(CanRestore), nameof(CanFinish), nameof(CanReuseRetained), nameof(AdmissionReferenceText) }) OnPropertyChanged(name);
        foreach (var command in new[] { LoadCommand, ReadCommand, PrepareCommand, ManualPrepareCommand, BackupCommand, InstallCommand, RestoreCommand, CancelCommand, FinishCommand, ReuseRetainedCommand }) command.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }
}
