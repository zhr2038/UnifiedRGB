using System.Text.Json;

namespace UnifiedRgb.Firmware;

public sealed record NormalObservation(string Model, string FirmwareHeader, string ContainerId,
    string PhysicalLocation, DateTimeOffset ObservedAtUtc, string EvidencePath, string HeaderOrigin = "ActualNormalGetter");
public sealed record NormalBindingEvidence(NormalObservation Observation, JsonElement SourceEvidence);
public sealed record BootObservation(string HidPath, string HidInstanceId, string UsbAncestor,
    string ContainerId, string PhysicalLocation, string CapsSha256, string ExpectedRawVersion,
    DateTimeOffset ObservedAtUtc, byte[] InventoryJson, bool NoBootReportsSentByObserver, bool ContinuousWatch);
public sealed record PhysicalPreparationAuthorization(Guid MaintenanceLeaseId, bool InterfacesReleasedAndJoined,
    bool OperatorConfirmedOtherWritersAbsent);
public sealed record PhysicalBinding(Guid SessionId, DateTimeOffset ObservedAtUtc, string DeviceKey,
    string PinPath, string PinSha256, string NormalEvidencePath, string NormalEvidenceSha256,
    string NormalFirmwareHeader, Guid? MaintenanceLeaseId = null, bool InterfacesReleasedAndJoined = false,
    bool OperatorConfirmedOtherWritersAbsent = false, string NormalHeaderOrigin = "ActualNormalGetter");

/// <summary>Installed Host adapter must use the proven normal getter and passive metadata watcher.
/// No implementation is selected by bundles. Automatic entry requires the separate explicit intent interface.</summary>
public interface IPhysicalObservationSource
{
    Task<NormalObservation> ReadCurrentNormalUsbAsync(CancellationToken ct);
    Task<NormalObservation> ObserveCurrentNormalUsbAsync(RetainedBaselineReceipt? retained, CancellationToken ct);
    Task<BootObservation> WatchNormalRemovalAndMatchingBootAsync(NormalObservation normal, CancellationToken ct);
    Task<bool> ObserveFreshNormalReplugAsync(PhysicalBinding binding, CancellationToken ct);
}

public interface IAutomaticPhysicalObservationSource
{
    Task<BootObservation> EnterUpgradeAsync(NormalObservation normal, string directory,
        PhysicalPreparationAuthorization authorization, Action writeAdmitted, CancellationToken ct);
}

public sealed class TransitionUncertainException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class PhysicalWorkflow(IPhysicalObservationSource source)
{
    public async Task<PhysicalBinding> PrepareAsync(string directory, Action awaitingBoot, CancellationToken ct,
        PhysicalPreparationAuthorization? authorization = null, RetainedBaselineReceipt? retained = null,
        bool automaticallyEnterUpgrade = false, Action? writeAdmitted = null)
    {
        if (authorization is not { MaintenanceLeaseId: var leaseId, InterfacesReleasedAndJoined: true, OperatorConfirmedOtherWritersAbsent: true } || leaseId == Guid.Empty)
            throw new InvalidDataException("Owned joined maintenance lease AND explicit operator confirmation of other-writer absence required");
        var normal = await source.ObserveCurrentNormalUsbAsync(retained, ct).ConfigureAwait(false);
        if (normal.Model != "1C24" || normal.FirmwareHeader != "001200000025000100" ||
            !Guid.TryParse(normal.ContainerId, out _) || !normal.PhysicalLocation.Contains("#USB(", StringComparison.Ordinal) ||
            DateTimeOffset.UtcNow - normal.ObservedAtUtc < TimeSpan.Zero || DateTimeOffset.UtcNow - normal.ObservedAtUtc > TimeSpan.FromSeconds(30))
            throw new InvalidDataException("Reconnect normal USB first; exact fresh current normal header/identity required");
        if (retained is not null && (normal.HeaderOrigin != RetainedBaselinePolicy.Origin || normal.ContainerId != retained.ContainerId || normal.PhysicalLocation != retained.PhysicalLocation))
            throw new InvalidDataException("Retained baseline does not identify the currently observed physical USB device");
        FirmwareFiles.NoReparse(normal.EvidencePath);
        if (new FileInfo(normal.EvidencePath).Length > 1024 * 1024) throw new InvalidDataException("Normal evidence bound");
        var normalBytes = File.ReadAllBytes(normal.EvidencePath);
        var boundNormalBytes = JsonSerializer.SerializeToUtf8Bytes(new NormalBindingEvidence(normal,
            JsonSerializer.Deserialize<JsonElement>(normalBytes)));
        FirmwareFiles.CreateNew(Path.Combine(directory, "normal-evidence.json"), boundNormalBytes);
        awaitingBoot();
        var boot = automaticallyEnterUpgrade
            ? await (source as IAutomaticPhysicalObservationSource ?? throw new InvalidOperationException("Automatic entry unavailable; use explicit manual preparation before any attempt"))
                .EnterUpgradeAsync(normal, directory, authorization, writeAdmitted ?? (() => { }), ct).ConfigureAwait(false)
            : await source.WatchNormalRemovalAndMatchingBootAsync(normal, ct).ConfigureAwait(false);
        if (!Guid.TryParse(boot.ContainerId, out var bootContainer) || bootContainer == Guid.Empty || boot.PhysicalLocation != normal.PhysicalLocation ||
            boot.ExpectedRawVersion != "02000200" || boot.ObservedAtUtc <= normal.ObservedAtUtc || boot.ObservedAtUtc > DateTimeOffset.UtcNow ||
            DateTimeOffset.UtcNow - boot.ObservedAtUtc > TimeSpan.FromMinutes(15) || !boot.NoBootReportsSentByObserver || !boot.ContinuousWatch ||
            !boot.HidPath.StartsWith("\\\\?\\hid#", StringComparison.OrdinalIgnoreCase) ||
            !boot.HidPath.Contains("vid_0b05&pid_1c26", StringComparison.OrdinalIgnoreCase) ||
            !boot.HidInstanceId.StartsWith("HID\\VID_0B05&PID_1C26", StringComparison.OrdinalIgnoreCase) ||
            !boot.UsbAncestor.StartsWith("USB\\VID_0B05&PID_1C26\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Fresh continuous same-physical normal-to-boot observation required; no identity aliases");
        FirmwareFiles.Hash(boot.CapsSha256);
        if (boot.InventoryJson.Length > 4 * 1024 * 1024) throw new InvalidDataException("Inventory bound");
        var inventory = Path.Combine(directory, "inventory.json"); FirmwareFiles.CreateNew(inventory, boot.InventoryJson);
        var session = Guid.NewGuid();
        var pinPath = Path.Combine(directory, "physical-pin.json");
        FirmwareFiles.CreateNew(pinPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            boot.HidPath, boot.HidInstanceId, boot.UsbAncestor, boot.PhysicalLocation, boot.ContainerId, boot.CapsSha256,
            InventoryFile = inventory, InventorySha256 = FirmwareFiles.Sha(boot.InventoryJson), FreshPhysicalSessionId = session,
            BootObservedAtUtc = boot.ObservedAtUtc, RootObservedFreshPhysicalBootEntry = true,
            RootConfirmedExclusiveNoPriorBootReports = authorization.InterfacesReleasedAndJoined && authorization.OperatorConfirmedOtherWritersAbsent && boot.NoBootReportsSentByObserver,
            RootConfirmedContinuousWatchedPhysicalSession = boot.ContinuousWatch,
            ExpectedRawVersionHex = boot.ExpectedRawVersion,
        }));
        FirmwareFiles.CreateNew(Path.Combine(directory, "operator-and-owned-lease.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { authorization, normal.HeaderOrigin, BootVersionEvidence = "ExpectedOnly_Actual82ValidatedByR6", boot.NoBootReportsSentByObserver }));
        var key = FirmwareFiles.Sha(System.Text.Encoding.UTF8.GetBytes(string.Join('|', boot.HidInstanceId, boot.UsbAncestor, boot.ContainerId, boot.PhysicalLocation)));
        return new(session, boot.ObservedAtUtc, key, pinPath, FirmwareFiles.HashFile(pinPath),
            Path.Combine(directory, "normal-evidence.json"), FirmwareFiles.Sha(boundNormalBytes), normal.FirmwareHeader,
            leaseId, true, true, normal.HeaderOrigin);
    }
    public Task<bool> ConfirmNormalAsync(PhysicalBinding binding, CancellationToken ct) => source.ObserveFreshNormalReplugAsync(binding, ct);
    public async Task<bool> ConfirmNormalWithoutWorkerAsync(CancellationToken ct, RetainedBaselineReceipt? retained = null)
    {
        var normal = await source.ObserveCurrentNormalUsbAsync(retained, ct).ConfigureAwait(false);
        return normal.Model == "1C24" && normal.FirmwareHeader == "001200000025000100" &&
            (retained is null || normal.ContainerId == retained.ContainerId && normal.PhysicalLocation == retained.PhysicalLocation) &&
            normal.ObservedAtUtc <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - normal.ObservedAtUtc < TimeSpan.FromSeconds(30);
    }
    public static void RequireFresh(PhysicalBinding binding)
    {
        var age = DateTimeOffset.UtcNow - binding.ObservedAtUtc;
        if (binding.SessionId == Guid.Empty || age < TimeSpan.Zero || age > TimeSpan.FromMinutes(15) ||
            FirmwareFiles.HashFile(binding.PinPath) != binding.PinSha256 || FirmwareFiles.HashFile(binding.NormalEvidencePath) != binding.NormalEvidenceSha256)
            throw new InvalidDataException("Expired/changed physical binding; reconnect normal and observe a new physical boot transition. Archived backup pin is never renewed.");
    }
}

public interface IFirmwareMaintenance
{
    Task AcquireAsync(Guid jobId, CancellationToken ct);
    Task FinishAsync(Guid jobId, bool knownWireClosed, bool freshNormal, CancellationToken ct);
}

/// <summary>Same serialization lock must surround manual Enter/Exit. Persist ownership BEFORE release;
/// after restart an owner is unknown, never automatically cleared.</summary>
public sealed class FirmwareMaintenanceLease
{
    private readonly string _path;
    private Guid? _owner;
    public Guid? Owner => _owner;
    public string? RecoveryError { get; private set; }
    public FirmwareMaintenanceLease(string path)
    {
        _path = path;
        try
        {
            FirmwareFiles.NoReparse(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            // Unlike File.Exists, GetAttributes does not hide access-denied/IO failures as 'no lease'.
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Maintenance lease is not a regular file");
            _owner = FirmwareFiles.Parse<LeaseRecord>(FirmwareFiles.ReadBounded(path, 4096)).JobId;
            if (_owner == Guid.Empty) throw new InvalidDataException("Empty maintenance owner");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { _owner = null; RecoveryError = "Malformed/unknown firmware lease retained: " + ex.GetType().Name; }
    }
    public void RejectManualChange() { if (_owner is not null || RecoveryError is not null) throw new InvalidOperationException("Firmware owns/has unknown peripheral maintenance; scoped safe finish required"); }
    public async Task AcquireUnderHostGateAsync(Guid jobId, Func<Task> blockPersistReleaseAndJoin)
    {
        if (jobId == Guid.Empty || _owner is not null || RecoveryError is not null) throw new InvalidOperationException("Maintenance lease already owned/unknown");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        FirmwareFiles.CreateNew(_path, JsonSerializer.SerializeToUtf8Bytes(new LeaseRecord(jobId)));
        _owner = jobId;
        await blockPersistReleaseAndJoin().ConfigureAwait(false); // failure deliberately retains ownership/block
    }
    public async Task FinishUnderHostGateAsync(Guid jobId, bool wireClosed, bool freshNormal, Func<Task> proveFreshResume)
    {
        if (_owner != jobId || RecoveryError is not null || !wireClosed || !freshNormal) throw new InvalidOperationException("Exact owned lease, joined closed wire and fresh normal required");
        await proveFreshResume().ConfigureAwait(false);
        File.Delete(_path); _owner = null;
    }
    public Task RetryReleaseUnderHostGateAsync(Guid jobId, Func<Task> releaseAndJoin)
    {
        if (_owner != jobId) throw new InvalidOperationException("Exact existing owned lease required for explicit release retry");
        return releaseAndJoin();
    }
    private sealed record LeaseRecord(Guid JobId);
}

/// <summary>Local persisted state only. Must run in the early gate factory, BEFORE any device owner is resolved.</summary>
public static class FirmwareStartupLeaseGuard
{
    public static FirmwareMaintenanceLease Read(string dataDirectory) => new(Path.Combine(dataDirectory, "firmware", "maintenance-lease.json"));
    public static bool BlocksAcquisition(FirmwareMaintenanceLease lease) => lease.Owner is not null || lease.RecoveryError is not null;
    public static FirmwareInstalledOptions Apply(FirmwareInstalledOptions options, FirmwareMaintenanceLease lease)
        => BlocksAcquisition(lease) ? options with { HardwareEnabled = false,
            FeatureError = lease.RecoveryError ?? "Firmware maintenance pending after restart; outcome unknown, reviewed recovery required" } : options;
}
