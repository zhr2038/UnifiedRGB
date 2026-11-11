namespace UnifiedRgb.Contracts;

public enum FirmwarePhase
{
    Loaded, Preparing, AwaitingBoot, Prepared, BackingUp, BackupComplete, Installing,
    Restoring, Verifying, AwaitingNormalReplug, Finished, Cancelled, Failed, NeedsReviewedRecovery, EnteringUpgrade,
}

public sealed record FirmwarePackageInfo(string ReleaseId, string Title, string Version, string Notes,
    string BundleSha256, string CandidateSha256, string RollbackSha256, bool Trusted, string TrustDetail, bool HardwareEnabled = false,
    bool RetainedBaselineAvailable = false);
public sealed record FirmwareJobStatus(Guid JobId, long Revision, FirmwarePackageInfo Package,
    FirmwarePhase Phase, string Detail, bool Running, bool CanCancel, bool WireClosed,
    int VerifiedPages, string CurrentFirmware, string ReceiptDirectory, string? BackupPath,
    Guid? InstallRunId, DateTimeOffset UpdatedAt, string? AdmissionReferencePath = null,
    string AdmissionReferenceOrigin = "None", string? AdmissionReceiptPath = null,
    string? BeforeImageProvenancePath = null, string? FeatureError = null, string? ObserverDiagnostic = null);
public sealed record FirmwareRequest(Guid RequestId, Guid JobId, long ExpectedRevision,
    string ReleaseId, string BundleSha256, Guid? ParentInstallRunId = null,
    bool OperatorConfirmedOtherWritersAbsent = false, bool AutomaticallyEnterUpgrade = false);

/// <summary>Mutations acknowledge promptly. Status reads never start, retry, finish or cancel work.</summary>
public interface IFirmwareUpdateService
{
    Task<FirmwareJobStatus> LoadFirmwarePackageAsync(string path, CancellationToken ct = default);
    Task<FirmwareJobStatus?> GetFirmwareJobAsync(Guid? jobId = null, CancellationToken ct = default);
    Task<FirmwareJobStatus> PrepareFirmwareAsync(FirmwareRequest request, CancellationToken ct = default);
    Task<FirmwareJobStatus> UseRetainedFirmwareBaselineAsync(FirmwareRequest request, CancellationToken ct = default);
    Task<FirmwareJobStatus> BackupFirmwareAsync(FirmwareRequest request, CancellationToken ct = default);
    Task<FirmwareJobStatus> InstallFirmwareAsync(FirmwareRequest request, CancellationToken ct = default);
    Task<FirmwareJobStatus> RestoreFirmwareAsync(FirmwareRequest request, CancellationToken ct = default);
    Task<FirmwareJobStatus> CancelFirmwareIfSafeAsync(FirmwareRequest request, CancellationToken ct = default);
    Task<FirmwareJobStatus> FinishFirmwareAsync(FirmwareRequest request, CancellationToken ct = default);
}
