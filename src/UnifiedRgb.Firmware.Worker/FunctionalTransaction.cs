using AppOnlyWriter;
using BootReadOnly;
using UnifiedRgb.Contracts;
using UnifiedRgb.Firmware;
using System.Text.Json;

namespace UnifiedRgb.Firmware.Worker;

public static class FunctionalTransaction
{
    public static FirmwareWorkerReceipt Run(ReleasePackage package, FirmwareWorkerRequest request,
        Func<Pin, IBootProtocol> open, Action<FirmwareWorkerReceipt> progress, Func<bool> cancelRequested,
        CatalogEntry? reviewedRelease = null, Func<Pin, Func<bool>, IBootProtocol>? openWithSafeCancellation = null)
    {
        PhysicalWorkflow.RequireFresh(request.Binding);
        // Unchanged R6 Pin.Load(now) runs before inventory/open. No archived timestamp is refreshed.
        var pin = Pin.Load(request.Binding.PinPath, DateTimeOffset.UtcNow);
        var key = FirmwareFiles.Sha(System.Text.Encoding.UTF8.GetBytes(string.Join('|', pin.HidInstanceId, pin.UsbAncestor, pin.ContainerId, pin.PhysicalLocation)));
        if (pin.FreshPhysicalSessionId != request.Binding.SessionId || pin.BootObservedAtUtc != request.Binding.ObservedAtUtc || key != request.Binding.DeviceKey ||
            request.BundleSha256 != package.BundleSha256 || request.ReleaseId != package.Release.ReleaseId || request.RunId == Guid.Empty || request.JobId == Guid.Empty ||
            request.Binding.MaintenanceLeaseId != request.JobId || !request.Binding.InterfacesReleasedAndJoined || !request.Binding.OperatorConfirmedOtherWritersAbsent)
            throw new InvalidDataException("Operation/physical/release binding mismatch");
        var restore = request.Operation == FirmwareOperation.Restore;
        var rollback = package.Read(package.Release.Rollback); var candidate = package.Read(package.Release.Candidate);
        var target = restore ? rollback : candidate;
        var ledgerPath = Path.Combine(request.Directory, "ledger.jsonl");
        if (File.Exists(ledgerPath) || File.Exists(ledgerPath + ".identity.json") || File.Exists(ledgerPath + ".lineage.json")) throw new InvalidDataException("Never reuse operation ledger");
        var ledger = new DurableLedger(ledgerPath, new(key, package.Release.Rollback.Sha256, FirmwareFiles.Sha(target), request.RunId));
        var before = Path.Combine(request.Directory, "before-image.bin");
        RestoreSource? source = null;
        if (restore)
        {
            if (request.ParentRunId is null || request.ParentLedgerPath is null || request.ParentBeforeImagePath is null ||
                FirmwareFiles.HashFile(request.ParentLedgerPath) != request.ParentLedgerSha256 ||
                FirmwareFiles.HashFile(request.ParentLedgerPath + ".lineage.json") != request.ParentLineageSha256)
                throw new InvalidDataException("Explicit exact parent install run/ledger/lineage required");
            var parent = new DurableLedger(request.ParentLedgerPath, new(key, package.Release.Rollback.Sha256, package.Release.Candidate.Sha256, request.ParentRunId.Value));
            parent.AssertResolved(); parent.VerifyStoredSnapshot();
            source = new(parent, Path.GetFullPath(request.ParentBeforeImagePath), candidate);
        }
        else if (request.Operation == FirmwareOperation.Install)
        {
            if (request.RetainedBaseline is { } retained)
            {
                var receipt = RetainedBaselinePolicy.Validate(retained, reviewedRelease ?? throw new InvalidDataException("Installed retained review required"), package);
                var normal = FirmwareFiles.Parse<NormalBindingEvidence>(FirmwareFiles.ReadBounded(request.Binding.NormalEvidencePath, 2 * 1024 * 1024)).Observation;
                if (receipt.ContainerId != normal.ContainerId || receipt.PhysicalLocation != normal.PhysicalLocation ||
                    normal.PhysicalLocation != pin.PhysicalLocation || normal.Model != "1C24" ||
                    normal.FirmwareHeader != receipt.NormalFirmwareHeader || normal.HeaderOrigin != RetainedBaselinePolicy.Origin ||
                    request.Binding.NormalHeaderOrigin != RetainedBaselinePolicy.Origin)
                    throw new InvalidDataException("Owner-confirmed reference/current actual physical boot mismatch");
            }
            else
            {
            if (request.BackupPath is null || FirmwareFiles.HashFile(request.BackupPath) != package.Release.Rollback.Sha256)
                throw new InvalidDataException("Completed immutable owned backup required");
            var provenancePath = Path.Combine(Path.GetDirectoryName(request.BackupPath)!, "preimage-provenance.json");
            if (new FileInfo(provenancePath).Length > 32768) throw new InvalidDataException("Backup provenance bound");
            var provenance = FirmwareFiles.Parse<FirmwareBackupProvenance>(File.ReadAllBytes(provenancePath));
            if (provenance.Schema != 1 || provenance.Classification != "UNIFIEDRGB_ACTUAL_TWO_PASS_BACKUP_V1" || provenance.JobId != request.JobId ||
                provenance.BundleSha256 != request.BundleSha256 || provenance.Binding.DeviceKey != key || !provenance.TwoPassEqual ||
                provenance.CompletedAtUtc > DateTimeOffset.UtcNow || provenance.CompletedAtUtc < provenance.Binding.ObservedAtUtc ||
                DateTimeOffset.UtcNow - provenance.CompletedAtUtc > TimeSpan.FromHours(24) || provenance.ImageSha256 != package.Release.Rollback.Sha256 ||
                Path.GetFullPath(provenance.ImagePath) != Path.GetFullPath(request.BackupPath) ||
                FirmwareFiles.HashFile(provenance.Binding.PinPath) != provenance.Binding.PinSha256 ||
                FirmwareFiles.HashFile(provenance.Binding.NormalEvidencePath) != provenance.Binding.NormalEvidenceSha256)
                throw new InvalidDataException("Owned actual immutable backup provenance mismatch; old pins are archived only");
            ValidateHistoricalBackupPin(provenance, pin);
            // Do NOT call Pin.Load with a retimed 'now' for archived M. Current B was loaded above.
            }
        }
        if (cancelRequested()) return new(request.JobId, request.RunId, FirmwarePhase.Cancelled, "已在设备打开前取消", true, false, 0, null, ledgerPath);
        var destructive = false;
        var pages = 0;
        IBootProtocol? io = null;
        var provenancePathForRun = Path.Combine(request.Directory, "preimage-provenance.json");
        FirmwareWorkerReceipt Receipt(FirmwarePhase phase, string detail, bool closed, string? backup = null) =>
            new(request.JobId, request.RunId, phase, detail, closed, destructive, pages,
                backup ?? (File.Exists(provenancePathForRun) ? before : null), ledgerPath,
                File.Exists(provenancePathForRun) ? provenancePathForRun : null);
        try
        {
            bool ShouldCancel() => !destructive && cancelRequested();
            io = openWithSafeCancellation is not null ? openWithSafeCancellation(pin, ShouldCancel) :
                new SafePreEraseProtocol(open(pin), ShouldCancel);
            var tx = new AppTransaction(io, ledger, before, restoreSource: source, beforeDestructive: () =>
            {
                FirmwareFiles.CreateNew(Path.Combine(request.Directory, "preimage-provenance.json"), JsonSerializer.SerializeToUtf8Bytes(
                    new FirmwareBackupProvenance(1, "UNIFIEDRGB_ACTUAL_TWO_PASS_BACKUP_V1", request.JobId, request.RunId, request.Binding,
                        DateTimeOffset.UtcNow, true, FirmwareFiles.HashFile(before), before, ledgerPath, request.BundleSha256)));
                if (cancelRequested()) throw new OperationCanceledException("Cancelled before destructive barrier");
                if (request.Operation == FirmwareOperation.Backup) throw new BackupCompleteException();
                // From this durable barrier onward cancellation is ignored, including tokens passed into the core.
                FirmwareFiles.CreateNew(Path.Combine(request.Directory, "destructive-barrier"), "explicit-authorized-operation"u8);
                destructive = true;
                progress(Receipt(restore ? FirmwarePhase.Restoring : FirmwarePhase.Installing, "双遍当前镜像与不可变备份校验通过；正在写入全部 55 页，请勿断开 USB", false));
            });
            tx.Run(target, restore, restore ? null : rollback, CancellationToken.None, officialPages: true);
            pages = ledger.Records.Count(r => r.Kind == "ProgramReadback");
            if (ledger.Records.Count == 0 || ledger.Records[^1].Kind != (restore ? "RestoreVerified" : "InstallVerified")) throw new InvalidDataException("Final full-image verification proof absent");
            pages = 55; // final two complete image passes verified every page, including unchanged restore pages
            progress(Receipt(FirmwarePhase.Verifying, "全部页面及最终双遍完整镜像、校验和已核验", false));
            io.Dispose(); io = null;
            return Receipt(FirmwarePhase.AwaitingNormalReplug, "固件核验完成；请手动正常重插 USB，再明确完成维护", true);
        }
        catch (BackupCompleteException)
        {
            io?.Dispose(); io = null;
            return Receipt(FirmwarePhase.BackupComplete, "225280 字节双遍备份及校验和完成；擦除次数为零，尚未安装", true, before);
        }
        catch (OperationCanceledException) when (!destructive)
        { io?.Dispose(); io = null; return Receipt(FirmwarePhase.Cancelled, "已在擦除前取消；维护仍保持，需明确完成", true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var unknown = false;
            try { ledger.AssertResolved(); } catch (InvalidDataException) { unknown = true; }
            io?.Dispose(); io = null;
            return Receipt(unknown ? FirmwarePhase.NeedsReviewedRecovery : FirmwarePhase.Failed,
                (unknown ? "写入结果未知：禁止任何后续 I/O、重试或复位；保留账本，须独立审核恢复。 " : "操作失败，未宣称安装成功。 ") + ex.Message, true);
        }
        finally { io?.Dispose(); }
    }
    private static void ValidateHistoricalBackupPin(FirmwareBackupProvenance backup, Pin current)
    {
        // Historical M is inspected as evidence at its recorded completion time, NEVER admitted by
        // Pin.Load(fabricatedNow). The currently opening B/C always uses unchanged Pin.Load(realNow).
        var binding = backup.Binding;
        if (new FileInfo(binding.PinPath).Length > 1024 * 1024) throw new InvalidDataException("Archived pin bound");
        var archived = FirmwareFiles.Parse<Pin>(File.ReadAllBytes(binding.PinPath));
        if (archived.FreshPhysicalSessionId != binding.SessionId || archived.BootObservedAtUtc != binding.ObservedAtUtc ||
            !archived.RootObservedFreshPhysicalBootEntry || !archived.RootConfirmedExclusiveNoPriorBootReports || !archived.RootConfirmedContinuousWatchedPhysicalSession ||
            archived.ExpectedRawVersionHex != "02000200" || archived.HidPath != current.HidPath || archived.HidInstanceId != current.HidInstanceId ||
            archived.UsbAncestor != current.UsbAncestor || archived.ContainerId != current.ContainerId || archived.PhysicalLocation != current.PhysicalLocation ||
            archived.CapsSha256 != current.CapsSha256 || archived.BootObservedAtUtc > backup.CompletedAtUtc ||
            !Path.IsPathFullyQualified(archived.InventoryFile) || new FileInfo(archived.InventoryFile).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Archived original/current exact physical tuple/caps/session differs");
        FirmwareFiles.NoReparse(archived.InventoryFile);
        var raw = File.ReadAllBytes(archived.InventoryFile);
        if (FirmwareFiles.Sha(raw) != archived.InventorySha256) throw new InvalidDataException("Archived inventory hash differs");
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        if (!doc.RootElement.TryGetProperty("at", out var stamp) || !stamp.TryGetDateTimeOffset(out var captured) || captured.Offset != TimeSpan.Zero ||
            captured < archived.BootObservedAtUtc || captured > backup.CompletedAtUtc)
            throw new InvalidDataException("Archived inventory timestamp outside real capture/completion");
        var values = new HashSet<string>(StringComparer.Ordinal);
        void Gather(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) values.Add(e.GetString()!);
            else if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Gather(p.Value);
            else if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) Gather(v);
        }
        Gather(doc.RootElement);
        foreach (var value in new[] { archived.HidPath, archived.HidInstanceId, archived.UsbAncestor, archived.ContainerId, archived.PhysicalLocation, archived.CapsSha256 })
            if (!values.Contains(value)) throw new InvalidDataException("Archived exact physical field absent from inventory");
    }
    private sealed class BackupCompleteException : Exception;
}
