using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppOnlyWriter;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Firmware;
using UnifiedRgb.Firmware.Checks;
using UnifiedRgb.Firmware.Worker;

var directory = Path.Combine(Path.GetTempPath(), "UnifiedRgb-Firmware-Fakes-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); Interlocked.Increment(ref checks); Console.WriteLine("PASS " + name); }
void Deny(Action action, string name) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or JsonException or ArgumentException) { Check(true, name); return; } throw new InvalidOperationException("Expected denial: " + name); }
async Task DenyAsync(Func<Task> action, string name) { try { await action().ConfigureAwait(false); } catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException) { Check(true, name); return; } throw new InvalidOperationException("Expected denial: " + name); }
string Dir(string name) { var path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
try
{
    var original = Fixtures.Image(139); var candidate = Fixtures.Image(140);
    var files = Fixtures.Files(original, candidate);
    var bundlePath = Fixtures.Zip(directory, files); var package = ReleasePackage.Load(bundlePath);
    Check(package.Release.ReleaseId == "FAKE-ONLY-1", "valid bounded functional bundle");
    Check(new InstalledTrust(new(directory)).Review(package) is null, "absent local catalog is untrusted");
    Check(!InstalledWorkerConfiguration.Read(directory).HardwareEnabled, "missing installed configuration defaults disabled");
    foreach (var path in new[] { "../evil", "/absolute", "a/../x", "a//x", "a/./x", "a\\x", "C:/x", "data/CON.bin", "a./x", "a/%2e/x" })
    {
        var copy = new Dictionary<string, byte[]>(files) { [path] = [1] };
        Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, copy)), "zip path rejection " + path);
    }
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, files, zip => { using var stream = zip.CreateEntry("RELEASE.JSON").Open(); stream.WriteByte(1); })), "case alias rejection");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, files, zip => { var entry = zip.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); using var stream = entry.Open(); stream.WriteByte(1); })), "symlink rejection");
    foreach (var path in new[] { "reviewed-catalog.json", "worker.exe", "approval.json", "device-pin.json" })
    { var copy = new Dictionary<string, byte[]>(files) { [path] = [1] }; Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, copy)), "foreign self-authorization/runner rejected " + path); }
    var wrongHash = new Dictionary<string, byte[]>(files) { ["data/candidate.bin"] = original };
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, wrongHash)), "candidate hash mismatch");
    foreach (var property in new[] { "Schema", "AppStart", "NormalFirmwareHeader", "PagePolicy" })
    {
        var copy = new Dictionary<string, byte[]>(files); var node = JsonNode.Parse(copy["release.json"])!;
        node[property] = property is "Schema" or "AppStart" ? JsonValue.Create(0) : JsonValue.Create("WRONG");
        copy["release.json"] = JsonSerializer.SerializeToUtf8Bytes(node);
        Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, copy)), "schema/target mismatch " + property);
    }
    var badImage = original.ToArray(); badImage[500] ^= 1;
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, Fixtures.Files(badImage, candidate))), "hash-matching invalid checkword rejected");
    var missing = new Dictionary<string, byte[]>(files); var missingNode = JsonNode.Parse(missing["release.json"])!.AsObject(); missingNode.Remove("Schema");
    missing["release.json"] = JsonSerializer.SerializeToUtf8Bytes(missingNode);
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, missing)), "missing required constructor field rejected");
    var duplicate = System.Text.Encoding.UTF8.GetBytes("{\"Schema\":1,\"Schema\":1}");
    Deny(() => FirmwareFiles.Parse<FirmwareRelease>(duplicate), "duplicate JSON field rejected");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, Fixtures.Files(original, candidate, [new(114688, 339964)]))), "reviewed manifest changed-byte range enforced");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, Fixtures.Files(original, candidate, [new(114688, 339969)]))), "manifest outside APP range rejected");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, Fixtures.Files(original, candidate, pageCount: 54))), "wrong wire page count rejected even with matching hashes");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, files, zip => { using var stream = zip.CreateEntry("huge.bin", CompressionLevel.NoCompression).Open(); stream.Write(new byte[1024 * 1024 + 1]); })), "per-entry expansion bound");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, files, zip => { using var stream = zip.CreateEntry("ratio.bin", CompressionLevel.SmallestSize).Open(); stream.Write(new byte[900000]); })), "compression ratio bound");
    Deny(() => ReleasePackage.Load(Fixtures.Zip(directory, files, zip => { for (var i = 0; i < 11; i++) { using var stream = zip.CreateEntry($"extra{i}.bin").Open(); stream.WriteByte(1); } })), "zip entry count bound");
    var options = Fixtures.Trust(directory, package); var trust = new InstalledTrust(options);
    Check(trust.Review(package) is not null, "exact independent installed catalog match");
    var executable = Path.Combine(options.PrivateDirectory, "UnifiedRgb.Firmware.Worker.exe");
    File.AppendAllText(executable, "changed"); Deny(() => trust.Review(package), "foreign runner hash rejected before execution");
    File.WriteAllText(executable, "FAKE-NOT-EXECUTABLE-UnifiedRgb.Firmware.Worker.exe");
    Check(trust.Review(package) is not null, "fake reviewed worker bytes restored without executing");

    var defaultPhysical = new FakePhysical(directory); var defaultMaintenance = new FakeMaintenance(); var defaultRunner = new MemoryRunner(original);
    await using (var broker = new FirmwareJobBroker(new(directory), defaultMaintenance, new(defaultPhysical), defaultRunner, Dir("default-jobs")))
    {
        Check(await broker.GetFirmwareJobAsync().ConfigureAwait(false) is null, "default getter has no device or worker action");
        var loaded = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
        var request = new FirmwareRequest(Guid.NewGuid(), loaded.JobId, loaded.Revision, loaded.Package.ReleaseId, loaded.Package.BundleSha256);
        await DenyAsync(() => broker.PrepareFirmwareAsync(request), "default prepare blocked").ConfigureAwait(false);
        Check(defaultPhysical.Calls == 0 && defaultMaintenance.Acquires == 0 && defaultRunner.Runs == 0, "constructor/getter/load/default no IO");
    }
    var leasePath = Path.Combine(directory, "lease.json"); var lease = new FirmwareMaintenanceLease(leasePath); var jobId = Guid.NewGuid();
    await DenyAsync(() => lease.AcquireUnderHostGateAsync(jobId, () => throw new IOException("failed release")), "release failure retained").ConfigureAwait(false);
    Check(lease.Owner == jobId, "failed release retains exact lease owner");
    Deny(lease.RejectManualChange, "manual exit blocked while owned");
    await DenyAsync(() => lease.AcquireUnderHostGateAsync(Guid.NewGuid(), () => Task.CompletedTask), "second lease cannot race owner").ConfigureAwait(false);
    await DenyAsync(() => lease.FinishUnderHostGateAsync(Guid.NewGuid(), true, true, () => Task.CompletedTask), "foreign finish rejected").ConfigureAwait(false);
    await DenyAsync(() => lease.FinishUnderHostGateAsync(jobId, false, true, () => Task.CompletedTask), "unknown wire finish rejected").ConfigureAwait(false);
    await DenyAsync(() => lease.FinishUnderHostGateAsync(jobId, true, false, () => Task.CompletedTask), "boot completion cannot resume").ConfigureAwait(false);
    Check(new FirmwareMaintenanceLease(leasePath).Owner == jobId, "lease survives service reconnect");
    await DenyAsync(() => lease.FinishUnderHostGateAsync(jobId, true, true, () => throw new IOException("resume failed")), "failed fresh resume remains blocked").ConfigureAwait(false);
    Check(lease.Owner == jobId, "failed finish retains owner");
    await lease.FinishUnderHostGateAsync(jobId, true, true, () => Task.CompletedTask).ConfigureAwait(false);
    Check(lease.Owner is null && !File.Exists(leasePath), "explicit exact scoped safe finish");

    var source = new FakePhysical(directory); var physical = new PhysicalWorkflow(source);
    var authorization = new PhysicalPreparationAuthorization(jobId, true, true);
    var binding = await physical.PrepareAsync(Dir("physical-M"), () => { }, CancellationToken.None, authorization).ConfigureAwait(false);
    var immutablePinHash = FirmwareFiles.HashFile(binding.PinPath);
    Check(binding.SessionId != Guid.Empty && source.Calls == 2, "fresh actual fake normal-to-boot binding");
    source.WrongHeader = true;
    await DenyAsync(() => physical.PrepareAsync(Dir("wrong-header"), () => { }, CancellationToken.None, authorization), "normal exact header mismatch").ConfigureAwait(false); source.WrongHeader = false;
    source.WrongBoot = true;
    await DenyAsync(() => physical.PrepareAsync(Dir("wrong-boot"), () => { }, CancellationToken.None, authorization), "foreign boot transition rejected").ConfigureAwait(false); source.WrongBoot = false;
    source.AlreadyBoot = true;
    await DenyAsync(() => physical.PrepareAsync(Dir("already-boot"), () => { }, CancellationToken.None, authorization), "already boot requires normal first").ConfigureAwait(false); source.AlreadyBoot = false;
    Deny(() => PhysicalWorkflow.RequireFresh(binding with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-16) }), "old pin cannot be retimed or renewed");
    ImageAdmission.AdmitOnce(package);
    Deny(() => ImageAdmission.AdmitOnce(package), "one-release immutable admission context");
    FirmwareWorkerRequest Request(FirmwareOperation operation, PhysicalBinding pin, Guid job, string? backup = null) =>
        new(job, Guid.NewGuid(), operation, bundlePath, package.BundleSha256, package.Release.ReleaseId, Dir(operation.ToString()), pin, backup, null, null, null, null, null);
    var backupRequest = Request(FirmwareOperation.Backup, binding, jobId);
    var backupIo = new FakeBoot(original);
    var backup = FunctionalTransaction.Run(package, backupRequest, _ => backupIo, _ => { }, () => false);
    Check(backup.Phase == FirmwarePhase.BackupComplete && backupIo.EraseRequests == 0 && backupIo.ProgramRequests == 0, "read-only actual R6 double backup zero erase");
    Check(File.ReadAllBytes(backup.BackupPath!).AsSpan().SequenceEqual(original), "immutable complete backup bytes");
    var provenance = FirmwareFiles.Parse<FirmwareBackupProvenance>(File.ReadAllBytes(Path.Combine(backupRequest.Directory, "preimage-provenance.json")));
    Check(provenance.TwoPassEqual && provenance.Binding.SessionId == binding.SessionId && provenance.ImageSha256 == package.Release.Rollback.Sha256, "backup actual lineage/provenance retained");
    var installBinding = await physical.PrepareAsync(Dir("physical-B"), () => { }, CancellationToken.None, authorization).ConfigureAwait(false);
    var installRequest = Request(FirmwareOperation.Install, installBinding, jobId, backup.BackupPath);
    var installIo = new FakeBoot(original);
    var cancellation = false;
    var install = FunctionalTransaction.Run(package, installRequest, _ => installIo, p => { if (p.Destructive) cancellation = true; }, () => cancellation);
    Check(install.Phase == FirmwarePhase.AwaitingNormalReplug && install.VerifiedPages == 55 && install.WireClosed, "full fake install final full-image/checkword proof");
    Check(installIo.EraseRequests == 55 && installIo.ProgramRequests == 55 && installIo.DestructiveRetries == 0, "exact 55 official pages no destructive retries");
    Check(installIo.Flash.AsSpan().SequenceEqual(candidate), "installed bytes exact candidate");
    Check(cancellation && install.Phase != FirmwarePhase.Cancelled, "cancellation after destructive barrier ignored safely");
    Check(FirmwareFiles.HashFile(binding.PinPath) == immutablePinHash, "backup M pin not rewritten by fresh B");
    var restoreBinding = await physical.PrepareAsync(Dir("physical-C"), () => { }, CancellationToken.None, authorization).ConfigureAwait(false);
    var restoreRequest = Request(FirmwareOperation.Restore, restoreBinding, jobId) with
    {
        ParentLedgerPath = install.LedgerPath, ParentRunId = installRequest.RunId,
        ParentLedgerSha256 = FirmwareFiles.HashFile(install.LedgerPath), ParentLineageSha256 = FirmwareFiles.HashFile(install.LedgerPath + ".lineage.json"),
        ParentBeforeImagePath = Path.Combine(installRequest.Directory, "before-image.bin"),
    };
    var restoreIo = new FakeBoot(candidate);
    var restore = FunctionalTransaction.Run(package, restoreRequest, _ => restoreIo, _ => { }, () => false);
    Check(restore.Phase == FirmwarePhase.AwaitingNormalReplug && restoreIo.Flash.AsSpan().SequenceEqual(original), "fresh C exact-parent original restore");
    var opens = 0;
    Deny(() => FunctionalTransaction.Run(package, restoreRequest with { Directory = Dir("foreign-parent"), ParentRunId = Guid.NewGuid() }, _ => { opens++; return new FakeBoot(candidate); }, _ => { }, () => false), "foreign parent before open");
    Check(opens == 0, "foreign parent zero open");
    Deny(() => FunctionalTransaction.Run(package, Request(FirmwareOperation.Backup, binding with { ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) }, jobId), _ => { opens++; return new FakeBoot(original); }, _ => { }, () => false), "stale B before inventory/open");
    Check(opens == 0, "stale pin zero open");
    var cancelled = FunctionalTransaction.Run(package, Request(FirmwareOperation.Backup, binding, jobId), _ => { opens++; return new FakeBoot(original); }, _ => { }, () => true);
    Check(cancelled.Phase == FirmwarePhase.Cancelled && opens == 0, "cancel before open zero erase");
    var wrongIo = new FakeBoot(candidate);
    var wrongPreimage = FunctionalTransaction.Run(package, Request(FirmwareOperation.Backup, binding, jobId), _ => wrongIo, _ => { }, () => false);
    Check(wrongPreimage.Phase == FirmwarePhase.Failed && wrongIo.EraseRequests == 0, "wrong whole preimage zero erase");
    var secondIo = new FakeBoot(original); var starts = 0;
    secondIo.BeforeSetAddressValue = address => { if (address == BootWriteWire.Start && ++starts == 4) secondIo.Flash[100] ^= 1; };
    var secondPass = FunctionalTransaction.Run(package, Request(FirmwareOperation.Backup, binding, jobId), _ => secondIo, _ => { }, () => false);
    Check(secondPass.Phase == FirmwarePhase.Failed && secondIo.EraseRequests == 0, "second complete pass disagreement zero erase");
    var unknownDir = Dir("unknown"); var unknownIo = new FakeBoot(original) { DropNextDestructiveOut = true };
    var unknownLedger = new DurableLedger(Path.Combine(unknownDir, "ledger.jsonl"), new(binding.DeviceKey, package.Release.Rollback.Sha256, package.Release.Candidate.Sha256, Guid.NewGuid()));
    var unknownTx = new AppTransaction(unknownIo, unknownLedger, Path.Combine(unknownDir, "before.bin"));
    Deny(() => unknownTx.Run(candidate, false, original, CancellationToken.None, true), "unknown destructive outcome retained");
    var traces = unknownIo.Trace.Count;
    Deny(() => unknownTx.ReadApp(CancellationToken.None), "unknown write freezes all subsequent read IO");
    Deny(() => unknownTx.Run(candidate, false, original, CancellationToken.None, true), "unknown write cannot retry");
    Check(unknownIo.Trace.Count == traces, "unknown freeze zero further wire calls");
    var unknownRestoreRequest = Request(FirmwareOperation.Restore, restoreBinding, jobId) with
    { ParentLedgerPath = unknownLedger.FilePath, ParentRunId = unknownLedger.Identity.RunId, ParentLedgerSha256 = FirmwareFiles.HashFile(unknownLedger.FilePath),
      ParentLineageSha256 = FirmwareFiles.HashFile(unknownLedger.FilePath + ".lineage.json"), ParentBeforeImagePath = Path.Combine(unknownDir, "before.bin") };
    Deny(() => FunctionalTransaction.Run(package, unknownRestoreRequest, _ => { opens++; return new FakeBoot(original); }, _ => { }, () => false), "unknown parent restore blocked before device open");
    Check(opens == 0, "unknown parent zero worker device open");

    var partialDirectory = Dir("known-partial"); var partialRun = Guid.NewGuid(); var partialIo = new FakeBoot(original);
    var partialLedger = new DurableLedger(Path.Combine(partialDirectory, "ledger.jsonl"), new(binding.DeviceKey, package.Release.Rollback.Sha256, package.Release.Candidate.Sha256, partialRun));
    var partialBefore = Path.Combine(partialDirectory, "before.bin");
    new AppTransaction(partialIo, partialLedger, partialBefore, pageLimit: 1).Run(candidate, false, original, CancellationToken.None, true);
    Check(partialIo.EraseRequests == 1 && partialLedger.Records.All(r => r.Kind != "InstallVerified"), "resolved partial page does not counterfeit install completion");
    var partialRestoreRequest = Request(FirmwareOperation.Restore, restoreBinding, jobId) with
    { ParentLedgerPath = partialLedger.FilePath, ParentRunId = partialRun, ParentLedgerSha256 = FirmwareFiles.HashFile(partialLedger.FilePath),
      ParentLineageSha256 = FirmwareFiles.HashFile(partialLedger.FilePath + ".lineage.json"), ParentBeforeImagePath = partialBefore };
    var partialRestoreIo = new FakeBoot(partialIo.Flash);
    var partialRestore = FunctionalTransaction.Run(package, partialRestoreRequest, _ => partialRestoreIo, _ => { }, () => false);
    Check(partialRestore.Phase == FirmwarePhase.AwaitingNormalReplug && partialRestoreIo.Flash.AsSpan().SequenceEqual(original), "resolved partial exact-parent restore through unchanged lineage replay");
    Check(partialRestoreIo.EraseRequests == 1, "known-partial restore does not erase already-original pages");

    var cancelAtBarrierIo = new FakeBoot(original);
    var cancelBarrierRequest = Request(FirmwareOperation.Install, installBinding, jobId, backup.BackupPath);
    var cancelledAtBarrier = FunctionalTransaction.Run(package, cancelBarrierRequest,
        _ => cancelAtBarrierIo, _ => { }, () => File.Exists(Path.Combine(cancelBarrierRequest.Directory, "preimage-provenance.json")));
    Check(cancelledAtBarrier.Phase == FirmwarePhase.Cancelled && cancelAtBarrierIo.EraseRequests == 0 &&
        cancelAtBarrierIo.ProgramRequests == 0, "cancel after double preimage but before destructive intent zero erase");

    var unboundBackup = Path.Combine(Dir("foreign-backup"), "original.bin"); File.WriteAllBytes(unboundBackup, original);
    Deny(() => FunctionalTransaction.Run(package, Request(FirmwareOperation.Install, installBinding, jobId, unboundBackup), _ =>
        { opens++; return new FakeBoot(original); }, _ => { }, () => false), "matching arbitrary BIN without owned provenance rejected");
    Check(opens == 0, "foreign backup rejection before open");

    var archivePath = Path.Combine(backupRequest.Directory, "preimage-provenance.json");
    var actualBackupReceipt = File.ReadAllBytes(archivePath);
    File.WriteAllBytes(archivePath, JsonSerializer.SerializeToUtf8Bytes(provenance with { JobId = Guid.NewGuid() }));
    Deny(() => FunctionalTransaction.Run(package, Request(FirmwareOperation.Install, installBinding, jobId, backup.BackupPath), _ =>
        { opens++; return new FakeBoot(original); }, _ => { }, () => false), "foreign backup job provenance cannot authorize install");
    File.WriteAllBytes(archivePath, actualBackupReceipt);
    Check(FirmwareFiles.HashFile(binding.PinPath) == immutablePinHash, "negative provenance test never refreshed archived pin");

    var brokerJobs = Dir("broker-jobs"); var memory = new MemoryRunner(original); var maintenance = new FakeMaintenance();
    await using (var broker = new FirmwareJobBroker(options, maintenance, physical, memory, brokerJobs))
    {
        var state = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
        FirmwareRequest Click(FirmwareJobStatus s) => new(Guid.NewGuid(), s.JobId, s.Revision, s.Package.ReleaseId, s.Package.BundleSha256, s.InstallRunId, true);
        async Task<FirmwareJobStatus> Wait()
        {
            for (var i = 0; i < 2000; i++)
            { var s = (await broker.GetFirmwareJobAsync().ConfigureAwait(false))!; if (!s.Running) return s; await Task.Delay(5).ConfigureAwait(false); }
            throw new TimeoutException("fake broker wait");
        }
        var prepareClick = Click(state);
        var ack = await broker.PrepareFirmwareAsync(prepareClick).ConfigureAwait(false);
        Check(ack.Running, "prepare acknowledges a job promptly"); state = await Wait().ConfigureAwait(false);
        Check(state.Phase == FirmwarePhase.Prepared && maintenance.Acquires == 1, "owned fake prepare");
        await broker.PrepareFirmwareAsync(prepareClick).ConfigureAwait(false);
        Check(maintenance.Acquires == 1, "replayed RPC click deduplicated");
        await DenyAsync(() => broker.BackupFirmwareAsync(Click(state) with { ExpectedRevision = 0 }), "stale RPC click rejected").ConfigureAwait(false);
        var backupClick = Click(state); await broker.BackupFirmwareAsync(backupClick).ConfigureAwait(false); state = await Wait().ConfigureAwait(false);
        Check(state.Phase == FirmwarePhase.BackupComplete && memory.Erases == 0 && memory.Runs == 1, "broker explicit fake backup no erase");
        await broker.BackupFirmwareAsync(backupClick).ConfigureAwait(false); Check(memory.Runs == 1, "backup timeout replay cannot start duplicate worker");
        await broker.InstallFirmwareAsync(Click(state)).ConfigureAwait(false); state = await Wait().ConfigureAwait(false);
        Check(state.Phase == FirmwarePhase.AwaitingNormalReplug && memory.Erases == 55 && state.InstallRunId is not null, "broker fake install verify durable status");
        var reconnected = await broker.GetFirmwareJobAsync(state.JobId).ConfigureAwait(false);
        Check(reconnected == state, "UI reconnect reads same durable job without execution");
        await broker.PrepareFirmwareAsync(Click(state)).ConfigureAwait(false); state = await Wait().ConfigureAwait(false);
        await broker.RestoreFirmwareAsync(Click(state)).ConfigureAwait(false); state = await Wait().ConfigureAwait(false);
        Check(state.Phase == FirmwarePhase.AwaitingNormalReplug && memory.Flash.AsSpan().SequenceEqual(original), "broker explicit restore after fresh transition");
        // Allow the owned task to return before a new user action; Finish independently checks joined state.
        await Task.Delay(10).ConfigureAwait(false);
        await broker.FinishFirmwareAsync(Click(state)).ConfigureAwait(false); state = await Wait().ConfigureAwait(false);
        Check(state.Phase == FirmwarePhase.Finished && maintenance.Finishes == 1, "explicit safe finish after normal replug");
        var ui = new UiService(state with { Phase = FirmwarePhase.Loaded, Package = state.Package with { Trusted = true } });
        var vm = new FirmwareUpdateViewModel(() => ui, InlineDispatcher.Instance, () => null, _ => true, _ => { }, timeout: TimeSpan.FromMilliseconds(15));
        await vm.RefreshAsync().ConfigureAwait(false);
        _ = vm.CanPrepare; _ = vm.Diagnostics; _ = vm.Detail;
        Check(ui.Writes == 0 && ui.Reads == 1 && vm.CanPrepare, "UI refresh/getters no writes");
        ui.TimeoutMutation = true;
        await vm.PrepareCommand.ExecuteAsync().ConfigureAwait(false);
        Check(ui.Writes == 1 && !vm.CanPrepare && vm.Message.Contains("不自动重发", StringComparison.Ordinal), "UI timeout mutation unknown no resend");
        await vm.PrepareCommand.ExecuteAsync().ConfigureAwait(false); Check(ui.Writes == 1, "UI uncertain intent cannot resend");
        await vm.RefreshAsync().ConfigureAwait(false); Check(ui.Writes == 1 && vm.CanPrepare, "UI explicit status reconciles timeout without resend");
        vm.SetConnected(false); Check(!vm.CanPrepare && vm.Message.Contains("继续", StringComparison.Ordinal), "UI disconnect never claims cancelled");
        vm.SetConnected(true); await vm.RefreshAsync().ConfigureAwait(false);
        ui.Status = state with { Phase = FirmwarePhase.Installing, Running = true, VerifiedPages = 17, CanCancel = false };
        await vm.RefreshAsync().ConfigureAwait(false);
        Check(vm.VerifiedPages == 17 && !vm.CanInstall && !vm.CancelCommand.CanExecute(null), "UI actual phase/page progress and destructive cancel gate");
        ui.Unsupported = true; await vm.RefreshAsync().ConfigureAwait(false);
        Check(!vm.CanPrepare && vm.Message.Contains("不支持", StringComparison.Ordinal), "old Host gracefully unsupported no write");
    }
    await using (var restarted = new FirmwareJobBroker(options, new FakeMaintenance(), physical, new MemoryRunner(original), brokerJobs))
    { var state = await restarted.GetFirmwareJobAsync().ConfigureAwait(false); Check(state?.Phase == FirmwarePhase.Finished, "finished job durable across broker restart"); }
    await ReviewChecks.RunAsync(directory, bundlePath, package, original, candidate, Check).ConfigureAwait(false);
    await AutoEntryChecks.RunAsync(directory, bundlePath, package, original, Check).ConfigureAwait(false);
    await FinalEntryBoundaryChecks.RunAsync(directory, Check).ConfigureAwait(false);
    await AutoReviewChecks.RunAsync(directory, Check).ConfigureAwait(false);
    await NormalUsbReadinessChecks.RunAsync(Check).ConfigureAwait(false);
    Console.WriteLine($"PASS {checks} focused fake checks; hardware/process starts=0");
    return 0;
}
finally
{
    // Exact owned unique normal temp directory, no environment-specific /tmp assumptions.
    Directory.Delete(directory, recursive: true);
}
