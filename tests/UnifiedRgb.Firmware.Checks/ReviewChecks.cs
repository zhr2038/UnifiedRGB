using System.Text.Json;
using AppOnlyWriter;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Firmware.Worker;

namespace UnifiedRgb.Firmware.Checks;

internal static class ReviewChecks
{
    public static async Task RunAsync(string root, string bundlePath, ReleasePackage package, byte[] original, byte[] candidate, Action<bool, string> check)
    {
        string Dir(string label) { var directory = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
        FirmwareRequest Click(FirmwareJobStatus status) => new(Guid.NewGuid(), status.JobId, status.Revision, status.Package.ReleaseId, status.Package.BundleSha256, status.InstallRunId, true);
        async Task<FirmwareJobStatus> Wait(FirmwareJobBroker broker, FirmwarePhase? phase = null)
        {
            for (var i = 0; i < 4000; i++)
            {
                var status = (await broker.GetFirmwareJobAsync().ConfigureAwait(false))!;
                if (phase is null ? !status.Running : status.Phase == phase) { await Task.Delay(10).ConfigureAwait(false); return status; }
                await Task.Delay(5).ConfigureAwait(false);
            }
            throw new TimeoutException("Focused review fake did not reach expected state");
        }
        var options = Fixtures.Trust(Dir("review-trust"), package);
        var mutablePicker = Path.Combine(Dir("snapshot-picker"), "picker.urgbfw"); File.Copy(bundlePath, mutablePicker);
        var immutablePackage = ReleasePackage.Load(mutablePicker);
        using (var expanded = new FileStream(mutablePicker, FileMode.Open, FileAccess.Write)) expanded.SetLength(32 * 1024 * 1024);
        var materialized = Path.Combine(Dir("snapshot-output"), "exact.urgbfw"); immutablePackage.MaterializeSnapshot(materialized);
        check(FirmwareFiles.HashFile(materialized) == package.BundleSha256 && new FileInfo(materialized).Length < 8 * 1024 * 1024,
            "materialization uses bounded locked ZIP snapshot despite picker expanding/swapping afterward");
        foreach (var property in new[] { "Candidate", "Rollback", "Title", "Notes", "ImageManifest" })
        {
            var data = Fixtures.Files(original, candidate); var node = System.Text.Json.Nodes.JsonNode.Parse(data["release.json"])!; node[property] = null;
            data["release.json"] = JsonSerializer.SerializeToUtf8Bytes(node);
            var rejected = false;
            try { ReleasePackage.Load(Fixtures.Zip(root, data)); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected = true; }
            check(rejected, "explicit null release field meaningfully rejected: " + property);
        }
        var catalog = FirmwareFiles.Parse<TrustedCatalog>(File.ReadAllBytes(options.CatalogPath!));
        var retainedImage = Path.Combine(options.PrivateDirectory, "retained-baseline.bin"); File.WriteAllBytes(retainedImage, original);
        var retainedPath = Path.Combine(options.PrivateDirectory, "retained-owner-receipt.json");
        var retained = new RetainedBaselineReceipt(1, "UNIFIEDRGB_OWNER_CONFIRMED_RETAINED_BASELINE_V1", package.Release.Rollback.Sha256,
            "1C24", "001200000025000100", FakePhysical.Container, FakePhysical.Location, DateTimeOffset.UtcNow.AddDays(-4), "Fake trusted retained 2DCB baseline; not a fresh read");
        File.WriteAllBytes(retainedPath, JsonSerializer.SerializeToUtf8Bytes(retained));
        var receiptHash = FirmwareFiles.HashFile(retainedPath);
        catalog = catalog with { Releases = [catalog.Releases[0] with { RetainedBaseline = new("2DCB / 0.25 (fake image)",
            new("retained-baseline.bin", package.Release.Rollback.Sha256), new("retained-owner-receipt.json", receiptHash)) }] };
        File.WriteAllBytes(options.CatalogPath!, JsonSerializer.SerializeToUtf8Bytes(catalog));
        var source = new FakePhysical(root); var memory = new MemoryRunner(original); var maintenance = new FakeMaintenance();
        FirmwareJobStatus completed;
        await using (var broker = new FirmwareJobBroker(options, maintenance, new(source), memory, Dir("retained-jobs")))
        {
            var status = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
            status = await broker.UseRetainedFirmwareBaselineAsync(Click(status)).ConfigureAwait(false);
            check(status.BackupPath is null && status.AdmissionReferenceOrigin == RetainedBaselinePolicy.Origin && memory.Runs == 0 && source.Calls == 0,
                "explicit retained selection is local existing-receipt reference, never fake fresh backup");
            await broker.PrepareFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.Prepared && source.NormalGetterCalls == 0 && source.PassiveNormalCalls == 1,
                "retained workflow prepares actual normal-to-boot metadata without GET12 or separate M backup");
            await broker.InstallFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.AwaitingNormalReplug && memory.Runs == 1 && memory.Erases == 55 && memory.Flash.AsSpan().SequenceEqual(candidate),
                "explicit retained baseline install still runs unchanged actual double-preimage and all55 verification");
            check(status.BackupPath is not null && status.BackupPath != status.AdmissionReferencePath && status.BeforeImageProvenancePath is not null &&
                File.ReadAllBytes(status.BackupPath).AsSpan().SequenceEqual(original), "successful install exposes ACTUAL immutable B before-image distinct from retained reference");
            var actual = FirmwareFiles.Parse<FirmwareBackupProvenance>(File.ReadAllBytes(status.BeforeImageProvenancePath!));
            check(actual.TwoPassEqual && actual.ImagePath == status.BackupPath && FirmwareFiles.HashFile(retainedPath) == receiptHash,
                "actual transaction provenance is new read; retained owner receipt and timestamp unchanged");
            await broker.PrepareFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            await broker.RestoreFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.AwaitingNormalReplug && memory.Flash.AsSpan().SequenceEqual(original) &&
                File.ReadAllBytes(status.BackupPath!).AsSpan().SequenceEqual(candidate), "restore exposes its ACTUAL pre-restore image, not reference or old M/B");
            await broker.FinishFirmwareAsync(Click(status)).ConfigureAwait(false); completed = await Wait(broker).ConfigureAwait(false);
        }
        var cancelSource = new FakePhysical(root) { HoldBootEntry = true }; var cancelMemory = new MemoryRunner(original); var cancelMaintenance = new FakeMaintenance();
        await using (var broker = new FirmwareJobBroker(options, cancelMaintenance, new(cancelSource), cancelMemory, Dir("cancel-prepare")))
        {
            var status = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
            await broker.PrepareFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker, FirmwarePhase.AwaitingBoot).ConfigureAwait(false);
            await broker.CancelFirmwareIfSafeAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.Cancelled && cancelMemory.Runs == 0 && cancelMemory.Erases == 0, "cancel AwaitingBoot joins without binding, worker or erase");
            cancelSource.HoldBootEntry = false;
            await broker.PrepareFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.Prepared && cancelMaintenance.Acquires == 1, "explicit reprepare after no-binding cancel retains exact existing owned lease");
            // A second cancellation before binding also has a separate explicit safe finish path.
            cancelSource.HoldBootEntry = true;
            await broker.PrepareFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker, FirmwarePhase.AwaitingBoot).ConfigureAwait(false);
            await broker.CancelFirmwareIfSafeAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            await broker.FinishFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.Finished && cancelMaintenance.Finishes == 1 && cancelMemory.Runs == 0, "explicit finish after joined cancelled preparation never invents a new boot binding");
        }
        // No-binding cancellation -> finish directly (not merely reprepare then finish).
        cancelSource = new FakePhysical(root) { HoldBootEntry = true }; cancelMaintenance = new FakeMaintenance();
        await using (var broker = new FirmwareJobBroker(options, cancelMaintenance, new(cancelSource), new MemoryRunner(original), Dir("cancel-direct-finish")))
        {
            var status = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
            await broker.PrepareFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker, FirmwarePhase.AwaitingBoot).ConfigureAwait(false);
            await broker.CancelFirmwareIfSafeAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            await broker.FinishFirmwareAsync(Click(status)).ConfigureAwait(false); status = await Wait(broker).ConfigureAwait(false);
            check(status.Phase == FirmwarePhase.Finished && cancelMaintenance.Finishes == 1, "no-binding preparation cancellation can explicitly prove normal target and finish");
        }
        var job = Guid.NewGuid(); var physical = new PhysicalWorkflow(new FakePhysical(root));
        var binding = await physical.PrepareAsync(Dir("cancel-read-pin"), () => { }, CancellationToken.None, new(job, true, true)).ConfigureAwait(false);
        var cancelFlag = false; var fake = new FakeBoot(original); var readBoundaries = 0;
        fake.BeforeSetAddress = () => { if (++readBoundaries == 50) cancelFlag = true; };
        var backupRequest = new FirmwareWorkerRequest(job, Guid.NewGuid(), FirmwareOperation.Backup, bundlePath, package.BundleSha256,
            package.Release.ReleaseId, Dir("cancel-actual-read"), binding, null, null, null, null, null, null);
        var cancelled = FunctionalTransaction.Run(package, backupRequest, _ => fake, _ => { }, () => cancelFlag);
        check(cancelled.Phase == FirmwarePhase.Cancelled && fake.EraseRequests == 0 && fake.ProgramRequests == 0 && readBoundaries <= 50,
            "pending actual backup cancellation joins promptly at safe read boundary with zero erase");
        check(!File.Exists(Path.Combine(backupRequest.Directory, "preimage-provenance.json")), "cancelled incomplete read never claims TwoPassEqual or BackupComplete");

        var child = new PendingChild(); var factory = new InjectedProcessFactory(child); var disk = new FailingOutputStorage();
        var runner = new FirmwareWorkerProcess(options, factory, disk);
        var pendingMaintenance = new FakeMaintenance();
        var pendingBroker = new FirmwareJobBroker(options, pendingMaintenance, physical, new ThrowingProgressRunner(runner), Dir("pending-child-job"));
        var pendingStatus = await pendingBroker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
        await pendingBroker.PrepareFirmwareAsync(Click(pendingStatus)).ConfigureAwait(false); pendingStatus = await Wait(pendingBroker).ConfigureAwait(false);
        await pendingBroker.BackupFirmwareAsync(Click(pendingStatus)).ConfigureAwait(false);
        string? requestPath = null;
        for (var i = 0; i < 2000 && requestPath is null; i++)
        { requestPath = Directory.EnumerateFiles(pendingStatus.ReceiptDirectory, "request.json", SearchOption.AllDirectories).FirstOrDefault(); if (requestPath is null) await Task.Delay(5).ConfigureAwait(false); }
        check(requestPath is not null, "injected process started without launching any executable");
        await Task.Delay(250).ConfigureAwait(false);
        check((await pendingBroker.GetFirmwareJobAsync().ConfigureAwait(false))!.Running && pendingMaintenance.Finishes == 0 && child.Disposals == 0,
            "throwing progress callback and disk-full output leave broker Running/owned while child pending");
        var dispose = pendingBroker.DisposeAsync().AsTask();
        await Task.Delay(30).ConfigureAwait(false);
        check(!dispose.IsCompleted && !child.DisposedBeforeExit, "broker Dispose awaits owned child; never kills or releases it for UI/observer failure");
        check(((CountingReader)child.StandardOutput).ReadCount == 100000 && ((CountingReader)child.StandardError).ReadCount == 100000 && disk.Attempts == 2,
            "log disk full still drains and discards both child pipes beyond retention cap");
        var pendingRequest = FirmwareFiles.Parse<FirmwareWorkerRequest>(File.ReadAllBytes(requestPath!));
        FirmwareFiles.CreateNew(Path.Combine(pendingRequest.Directory, "result.json"), JsonSerializer.SerializeToUtf8Bytes(
            new FirmwareWorkerReceipt(pendingRequest.JobId, pendingRequest.RunId, FirmwarePhase.Cancelled, "fake child closed", true, false, 0, null, Path.Combine(pendingRequest.Directory, "ledger.jsonl"))));
        child.Complete(); await dispose.ConfigureAwait(false);
        check(child.Waits > 0 && child.Disposals == 1 && !child.DisposedBeforeExit && pendingMaintenance.Finishes == 0,
            "all child/drain joins complete before idle/disposal; maintenance never auto-released");
        check((await pendingBroker.GetFirmwareJobAsync().ConfigureAwait(false))?.ObserverDiagnostic is { } observerDiagnostic &&
            observerDiagnostic.Contains("progress observer", StringComparison.Ordinal) && observerDiagnostic.Contains("output retention open", StringComparison.Ordinal),
            "observer and retention failures survive separately in durable job status, not as fake firmware failure/success");
        check(factory.StartInfo is { UseShellExecute: false, CreateNoWindow: true } && factory.StartInfo.ArgumentList.Count == 2,
            "production process factory uses fixed arguments and no shell even under injection");

        var progressChild = new PendingChild(); var progressFactory = new InjectedProcessFactory(progressChild);
        var progressRequest = backupRequest with { Operation = FirmwareOperation.Install, RunId = Guid.NewGuid(), Directory = Dir("ledger-live-progress") };
        var pages = new List<int>();
        var progressTask = new FirmwareWorkerProcess(options, progressFactory).RunAsync(catalog.Releases[0], progressRequest,
            progress => { if (progress.Destructive) { check(progress.Phase == FirmwarePhase.Installing, "page progress alone is never success/final verification"); pages.Add(progress.VerifiedPages); } });
        var ledger = new DurableLedger(Path.Combine(progressRequest.Directory, "ledger.jsonl"), new(binding.DeviceKey, package.Release.Rollback.Sha256, package.Release.Candidate.Sha256, progressRequest.RunId));
        var flash = new FakeBoot(original); var before = Path.Combine(progressRequest.Directory, "before-image.bin");
        FirmwareFiles.CreateNew(Path.Combine(progressRequest.Directory, "destructive-barrier"), "fake"u8);
        new AppTransaction(flash, ledger, before, pageLimit: 1).Run(candidate, false, original, CancellationToken.None, true);
        await Task.Delay(300).ConfigureAwait(false);
        new AppTransaction(new FakeBoot(flash.Flash), ledger, before, pageLimit: 2).Run(candidate, false, null, CancellationToken.None, true);
        await Task.Delay(300).ConfigureAwait(false);
        check(pages.Contains(1) && pages.Contains(2) && !progressTask.IsCompleted, "live bounded observer reports confirmed ProgramReadback 1 then2 while child still runs");
        FirmwareFiles.CreateNew(Path.Combine(progressRequest.Directory, "result.json"), JsonSerializer.SerializeToUtf8Bytes(
            new FirmwareWorkerReceipt(job, progressRequest.RunId, FirmwarePhase.Failed, "fake resolved partial", true, true, 2, before, ledger.FilePath)));
        progressChild.Complete(); await progressTask.ConfigureAwait(false);

        var badConfigDirectory = Dir("bad-config"); var badConfig = Path.Combine(badConfigDirectory, "firmware-installed.json"); File.WriteAllText(badConfig, "{broken");
        var config = InstalledWorkerConfiguration.Read(badConfigDirectory);
        check(!config.HardwareEnabled && config.FeatureError is not null && File.ReadAllText(badConfig) == "{broken", "malformed installed config disables only firmware feature and preserves file");
        var badJobs = Dir("bad-job"); var badJobDir = Path.Combine(badJobs, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(badJobDir);
        File.WriteAllText(Path.Combine(badJobDir, "status-00000001.json"), "null");
        await using (var unavailable = new FirmwareJobBroker(options, new FakeMaintenance(), physical, new MemoryRunner(original), badJobs))
        { var status = await unavailable.GetFirmwareJobAsync().ConfigureAwait(false); check(status is { FeatureError: not null, Phase: FirmwarePhase.NeedsReviewedRecovery }, "malformed job receipt returns firmware recovery error instead of crashing Host construction"); }
        var badLease = Path.Combine(Dir("bad-lease"), "lease.json"); File.WriteAllText(badLease, "null"); var lease = new FirmwareMaintenanceLease(badLease);
        check(lease.RecoveryError is not null && File.ReadAllText(badLease) == "null", "malformed lease preserves unknown intent without startup crash");
        var countBoundJobs = Dir("global-receipt-bound");
        for (var part = 0; part < 2; part++)
        {
            var sub = Path.Combine(countBoundJobs, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(sub);
            // All files are tiny malformed fixtures; bound triggers before inspecting the second job.
            for (var index = 0; index < 4097; index++) File.WriteAllText(Path.Combine(sub, $"status-{index:D8}.json"), "null");
            File.WriteAllBytes(Path.Combine(sub, "status-00004096.json"), JsonSerializer.SerializeToUtf8Bytes(completed with
            { JobId = Guid.ParseExact(Path.GetFileName(sub), "N"), Revision = 4097, ReceiptDirectory = sub }));
        }
        await using (var bounded = new FirmwareJobBroker(options, new FakeMaintenance(), physical, new MemoryRunner(original), countBoundJobs))
        { check((await bounded.GetFirmwareJobAsync().ConfigureAwait(false))?.FeatureError is not null, "global receipt enumeration remains feature-closed under excessive archived receipts"); }
        var baselineClock = DateTimeOffset.UtcNow.AddSeconds(5); var clock = baselineClock;
        var service = new UiService(completed with { Phase = FirmwarePhase.Loaded, Package = completed.Package with { Trusted = true, HardwareEnabled = true }, UpdatedAt = baselineClock.AddSeconds(-1) });
        var vm = new FirmwareUpdateViewModel(() => service, InlineDispatcher.Instance, () => null, _ => true, _ => { }, () => clock);
        await vm.RefreshAsync().ConfigureAwait(false); check(vm.CanPrepare, "fresh UI observation enabled only with trusted enabled source");
        clock = baselineClock.AddMinutes(-1); await vm.RefreshAsync().ConfigureAwait(false); check(!vm.CanPrepare, "UI clock rollback rejects even a new read from lower clock");
        clock = baselineClock.AddSeconds(1); service.Status = service.Status with { UpdatedAt = clock.AddMinutes(1) };
        await vm.RefreshAsync().ConfigureAwait(false); check(!vm.CanPrepare, "future Host observation cannot authorize UI actions");
        service.Unsupported = true; await vm.RefreshAsync().ConfigureAwait(false); vm.SetConnected(false); service.Unsupported = false;
        service.Status = service.Status with { UpdatedAt = baselineClock }; vm.SetConnected(true); await vm.RefreshAsync().ConfigureAwait(false);
        check(vm.CanPrepare && service.Writes == 0, "reconnect resets unsupported state for upgraded Host without writes");
        service.Status = service.Status with { Package = service.Status.Package with { HardwareEnabled = false } };
        await vm.RefreshAsync().ConfigureAwait(false); check(!vm.CanPrepare && !vm.CanBackup && !vm.CanInstall && !vm.CanRestore && !vm.CanFinish,
            "default disabled source gates every actionable hardware button");
    }
}
