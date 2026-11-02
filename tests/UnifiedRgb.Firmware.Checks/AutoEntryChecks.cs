using System.Text.Json;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Firmware.Worker;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Firmware.Checks;

internal static class AutoEntryChecks
{
    internal static NormalEntryTarget Target => new(new("\\\\?\\hid#vid_0b05&pid_1c24&mi_01#fake", 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0),
        FakePhysical.Container, FakePhysical.Location, "HID\\VID_0B05&PID_1C24&MI_01\\normal", "USB\\VID_0B05&PID_1C24\\T4FAKE");
    internal static BootObservation Boot => new("\\\\?\\hid#vid_0b05&pid_1c26&mi_01#fake", "HID\\VID_0B05&PID_1C26&MI_01\\boot",
        "USB\\VID_0B05&PID_1C26\\8&FAKE", "29498fe8-cf5f-11f1-95fe-60ff9efff29b", FakePhysical.Location,
        new string('a', 64), "02000200", DateTimeOffset.UtcNow, [], true, true);

    public static async Task RunAsync(string root, string bundlePath, ReleasePackage package, byte[] original, Action<bool, string> check)
    {
        string Dir() { var p = Path.Combine(root, "auto-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        async Task Deny(Func<Task> action, string label)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
            { check(true, label); return; }
            throw new InvalidOperationException("Expected auto-entry denial: " + label);
        }
        var normal = new NormalObservation("1C24", "001200000025000100", Target.ContainerId, Target.PhysicalLocation, DateTimeOffset.UtcNow, "fake",
            RetainedBaselinePolicy.Origin);
        var authorization = new PhysicalPreparationAuthorization(Guid.NewGuid(), true, true);
        var channel = new EntryChannel(); var path = Dir();
        static async Task<BootObservation> AfterWrite(EntryChannel ch)
        { await ch.Started.Task.ConfigureAwait(false); return Boot; }
        var captureCount = 0;
        var entry = new AutomaticBootEntry(() => { captureCount++; return Target; }, _ =>
        {
            check(File.Exists(Path.Combine(path, "transition-intent.json")), "intent persisted BEFORE normal channel open"); return channel;
        }, (_, _) => AfterWrite(channel));
        var result = await entry.ExecuteAsync(normal, Target, path, authorization, () => { }, CancellationToken.None).ConfigureAwait(false);
        var frame = channel.Frame!;
        check(frame.Length == 65 && Convert.ToHexString(frame.AsSpan(0, 8)) == "007BAA41535553AA" && frame.AsSpan(8).IndexOfAnyExcept((byte)0) < 0 &&
            channel.Writes == 1 && channel.Reads == 0 && channel.Disposals == 1 && captureCount == 3, "compiled exact65 single interrupt write, zero reads, revalidate through final boundary and close");
        var intent = FirmwareFiles.Parse<TransitionIntent>(File.ReadAllBytes(Path.Combine(path, "transition-intent.json")));
        check(intent.JobId == authorization.MaintenanceLeaseId && intent.RunId != Guid.Empty && intent.PayloadSha256 == FirmwareFiles.Sha(frame) &&
            intent.Target == Target && intent.NormalHeaderOrigin == RetainedBaselinePolicy.Origin, "immutable intent actual normal identity/run/hash/time and honest retained origin");
        check(result.ContainerId != normal.ContainerId && result.UsbAncestor != Target.UsbAncestor, "different actual GUIDs and ancestors preserved, never aliased");
        await Deny(() => entry.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None), "same consumed normal context cannot resend").ConfigureAwait(false);
        check(channel.Writes == 1, "no repeat after consumed intent");

        foreach (var bad in new[] { Target with { Collection = Target.Collection with { Pid = 0x1C26 } }, Target with { Collection = Target.Collection with { Interface = 0 } },
            Target with { Collection = Target.Collection with { FeatureReportLength = 65 } }, Target with { Collection = Target.Collection with { OutputReportLength = 64 } },
            Target with { Collection = Target.Collection with { Usage = 2 } }, Target with { ContainerId = Guid.NewGuid().ToString("D") } })
        {
            var rejectedChannel = new EntryChannel();
            var denied = new AutomaticBootEntry(() => bad, _ => rejectedChannel, (_, _) => Task.FromResult(Boot));
            await Deny(() => denied.ExecuteAsync(normal, bad, Dir(), authorization, () => { }, CancellationToken.None), "wrong device/caps/binding zero-send").ConfigureAwait(false);
            check(rejectedChannel.Writes == 0, "rejected normal target sends zero reports");
        }
        foreach (var badLease in new[] { authorization with { MaintenanceLeaseId = Guid.Empty }, authorization with { InterfacesReleasedAndJoined = false }, authorization with { OperatorConfirmedOtherWritersAbsent = false } })
        {
            var denied = new AutomaticBootEntry(() => Target, _ => throw new InvalidOperationException("Must not open"), (_, _) => Task.FromResult(Boot));
            await Deny(() => denied.ExecuteAsync(normal, Target, Dir(), badLease, () => { }, CancellationToken.None), "invalid lease/intent zero-open").ConfigureAwait(false);
        }
        var stale = new AutomaticBootEntry(() => Target, _ => throw new InvalidOperationException("Must not open"), (_, _) => Task.FromResult(Boot));
        await Deny(() => stale.ExecuteAsync(normal with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, Target, Dir(), authorization, () => { }, CancellationToken.None), "stale normal binding zero-send").ConfigureAwait(false);
        var changed = new AutomaticBootEntry(() => Target with { UsbAncestor = "changed" }, _ => throw new InvalidOperationException("Must not open"), (_, _) => Task.FromResult(Boot));
        await Deny(() => changed.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None), "metadata swap before open denied").ConfigureAwait(false);
        var postOpen = new EntryChannel(); var validations = 0;
        var changedAfterOpen = new AutomaticBootEntry(() => ++validations == 1 ? Target : Target with { Collection = Target.Collection with { InputReportLength = 64 } },
            _ => postOpen, (_, _) => Task.FromResult(Boot));
        await Deny(() => changedAfterOpen.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None), "caps swap after open before write denied").ConfigureAwait(false);
        check(postOpen.Writes == 0 && postOpen.Disposals == 1, "prewrite revalidation failure closes channel with zero sends");
        using (var beforeAdmission = new CancellationTokenSource())
        {
            beforeAdmission.Cancel(); var noOpen = 0;
            var preCancelled = new AutomaticBootEntry(() => Target, _ => { noOpen++; return new EntryChannel(); }, (_, _) => Task.FromResult(Boot));
            await Deny(() => preCancelled.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, beforeAdmission.Token), "cancel before admission sends nothing").ConfigureAwait(false);
            check(noOpen == 0, "pre-admission cancellation zero-open");
        }
        foreach (var json in new[] { "broken", "{}", "null", "{\"JobId\":\"00000000-0000-0000-0000-000000000000\"}",
            "{\"JobId\":\"not-a-guid\"}", new string('x', 4097) })
        {
            var data = Dir(); var leaseDirectory = Path.Combine(data, "firmware"); Directory.CreateDirectory(leaseDirectory);
            File.WriteAllText(Path.Combine(leaseDirectory, "maintenance-lease.json"), json);
            var lease = FirmwareStartupLeaseGuard.Read(data);
            check(lease.Owner is null && lease.RecoveryError is not null && FirmwareStartupLeaseGuard.BlocksAcquisition(lease),
                "malformed/empty/oversized local lease fails closed without invented owner");
        }

        // An observed boot cannot escape while the OLD write is still pending, even past its cancellation deadline.
        var pending = new EntryChannel { Pending = true }; var pendingEntry = new AutomaticBootEntry(() => Target, _ => pending, (_, _) => AfterWrite(pending));
        var pendingTask = pendingEntry.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None);
        await pending.Deadline.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(!pendingTask.IsCompleted && pending.Disposals == 0 && pending.Writes == 1, "timeout requests cancellation but JOINS pending write; no boot82/owner before old wire settles");
        var concurrent = new AutomaticBootEntry(() => Target, _ => throw new InvalidOperationException("Must not open"), (_, _) => Task.FromResult(Boot));
        await Deny(() => concurrent.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None), "global entry writer excludes independent context").ConfigureAwait(false);
        pending.Complete.TrySetResult();
        await pendingTask.ConfigureAwait(false);
        check(pending.Disposals == 1 && pending.Writes == 1, "positive passive boot may resolve unknown write only AFTER join and close");

        var lost = new EntryChannel { Disconnect = true };
        var unknown = new AutomaticBootEntry(() => Target, _ => lost, async (_, _) => { await lost.Started.Task.ConfigureAwait(false); throw new TimeoutException("no boot"); });
        await Deny(() => unknown.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None), "disconnect plus absent boot freezes uncertain context").ConfigureAwait(false);
        await Deny(() => unknown.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, CancellationToken.None), "unknown disconnect never resends").ConfigureAwait(false);
        check(lost.Writes == 1 && lost.Reads == 0 && lost.Disposals == 1, "unknown closes old handle and never reads normal ACK");

        var cancelledChannel = new EntryChannel { Pending = true }; using var cancel = new CancellationTokenSource();
        var cancelled = new AutomaticBootEntry(() => Target, _ => cancelledChannel, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); return Boot; });
        var cancellationTask = cancelled.ExecuteAsync(normal, Target, Dir(), authorization, () => { }, cancel.Token);
        cancel.Cancel();
        check(!cancellationTask.IsCompleted && cancelledChannel.Disposals == 0, "cancel after write admission does not detach or claim nothing happened");
        cancelledChannel.Complete.TrySetResult();
        await Deny(() => cancellationTask, "cancel after admission is uncertain, not ordinary Cancelled").ConfigureAwait(false);
        check(cancellationTask.Exception?.InnerException is TransitionUncertainException && cancelledChannel.Disposals == 1, "cancellation joined and closed, uncertain outcome preserved");

        async Task<BootObservation> Watch(params EntryMetadataSnapshot[] snapshots)
        {
            var index = 0;
            return await BootTransitionWatcher.WatchAsync(Target, () => snapshots[Math.Min(index++, snapshots.Length - 1)], _ => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
        }
        var mapped = await Watch(new([Target], []), new([], []), new([], [Boot])).ConfigureAwait(false);
        check(mapped.ContainerId == Boot.ContainerId, "witnessed removal then unique same-port DIFFERENT-GUID boot accepted");
        await Deny(() => Watch(new EntryMetadataSnapshot([], [Boot with { PhysicalLocation = "PCIROOT(0)#USBROOT(0)#USB(99)" }])), "wrong-port boot rejected").ConfigureAwait(false);
        await Deny(() => Watch(new EntryMetadataSnapshot([], [Boot, Boot with { ContainerId = Guid.NewGuid().ToString("D") }])), "ambiguous boot rejected").ConfigureAwait(false);
        await Deny(() => Watch(new EntryMetadataSnapshot([Target], [Boot])), "boot without normal removal rejected").ConfigureAwait(false);
        await Deny(() => Watch(new([], []), new([Target], [])), "normal replug inside uncertain transition rejected").ConfigureAwait(false);
        await Deny(() => Watch(new EntryMetadataSnapshot([Target with { ContainerId = Guid.NewGuid().ToString("D") }], [])), "stale within-role normal identity rejected").ConfigureAwait(false);
        var samples = 0;
        await Deny(() => BootTransitionWatcher.WatchAsync(Target, () => ++samples == 1 ? new([Target], []) : new([], []), _ => Task.CompletedTask, CancellationToken.None), "bounded passive wait exhaustion unknown").ConfigureAwait(false);
        check(samples == 30, "passive polling never exceeds30 samples");

        var physical = new AutomaticFakePhysical(Dir()); var maintenance = new FakeMaintenance(); var runner = new MemoryRunner(original);
        await using (var broker = new FirmwareJobBroker(Fixtures.Trust(Dir(), package), maintenance, new(physical), runner, Dir()))
        {
            var loaded = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
            await broker.GetFirmwareJobAsync().ConfigureAwait(false);
            check(physical.Channel.Writes == 0, "source load/getter no automatic entry");
            var request = new FirmwareRequest(Guid.NewGuid(), loaded.JobId, loaded.Revision, loaded.Package.ReleaseId, loaded.Package.BundleSha256,
                OperatorConfirmedOtherWritersAbsent: true, AutomaticallyEnterUpgrade: true);
            await broker.PrepareFirmwareAsync(request).ConfigureAwait(false);
            FirmwareJobStatus status;
            do { await Task.Delay(5).ConfigureAwait(false); status = (await broker.GetFirmwareJobAsync().ConfigureAwait(false))!; } while (status.Running);
            check(status.Phase == FirmwarePhase.Prepared && status.WireClosed && maintenance.Acquires == 1 && physical.Channel.Writes == 1 && runner.Runs == 0,
                "explicit auto prepare acquires maintenance and maps boot without starting backup/flash");
            await broker.PrepareFirmwareAsync(request).ConfigureAwait(false);
            check(physical.Channel.Writes == 1, "source user request durable dedup prevents second entry command");
            runner.BeforeOpen = () => check(physical.Channel.Disposals == 1, "first actual fake boot owner only AFTER old normal handle close");
            runner.WrongBootVersion = true;
            await broker.BackupFirmwareAsync(request with { RequestId = Guid.NewGuid(), ExpectedRevision = status.Revision }).ConfigureAwait(false);
            do { await Task.Delay(5).ConfigureAwait(false); status = (await broker.GetFirmwareJobAsync().ConfigureAwait(false))!; } while (status.Running);
            check(status.Phase == FirmwarePhase.Failed && runner.LastBoot!.Trace.Contains("82", StringComparer.Ordinal) && runner.Erases == 0,
                "unchanged R6 rejects ACTUAL wrong82 despite metadata expected02000200, zero erase");
            runner.BeforeOpen = null; runner.WrongBootVersion = false;
        }
        var defaultSource = new AutomaticFakePhysical(Dir());
        await using (var broker = new FirmwareJobBroker(new(Dir()), new FakeMaintenance(), new(defaultSource), runner, Dir()))
        {
            var s = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
            await Deny(() => broker.PrepareFirmwareAsync(new(Guid.NewGuid(), s.JobId, s.Revision, s.Package.ReleaseId, s.Package.BundleSha256,
                OperatorConfirmedOtherWritersAbsent: true, AutomaticallyEnterUpgrade: true)), "default gate denies auto entry").ConfigureAwait(false);
            check(defaultSource.Channel.Writes == 0, "default-disabled sends zero entry commands");
        }
        var cancellingSource = new AutomaticFakePhysical(Dir()) { HoldBoot = true };
        var cancellingRunner = new MemoryRunner(original);
        cancellingSource.Channel.Pending = true;
        await using (var broker = new FirmwareJobBroker(Fixtures.Trust(Dir(), package), new FakeMaintenance(), new(cancellingSource), cancellingRunner, Dir()))
        {
            var s = await broker.LoadFirmwarePackageAsync(bundlePath).ConfigureAwait(false);
            var request = new FirmwareRequest(Guid.NewGuid(), s.JobId, s.Revision, s.Package.ReleaseId, s.Package.BundleSha256,
                OperatorConfirmedOtherWritersAbsent: true, AutomaticallyEnterUpgrade: true);
            await broker.PrepareFirmwareAsync(request).ConfigureAwait(false);
            await cancellingSource.Channel.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            s = (await broker.GetFirmwareJobAsync().ConfigureAwait(false))!;
            await broker.CancelFirmwareIfSafeAsync(request with { RequestId = Guid.NewGuid(), ExpectedRevision = s.Revision }).ConfigureAwait(false);
            check((await broker.GetFirmwareJobAsync().ConfigureAwait(false))!.Running && cancellingSource.Channel.Disposals == 0,
                "source cancellation keeps Host task owned until old write joins");
            cancellingSource.Channel.Complete.TrySetResult();
            do { await Task.Delay(5).ConfigureAwait(false); s = (await broker.GetFirmwareJobAsync().ConfigureAwait(false))!; } while (s.Running);
            check(s.Phase == FirmwarePhase.NeedsReviewedRecovery && !s.WireClosed && cancellingSource.Channel.Disposals == 1 && cancellingRunner.Runs == 0,
                "source after-admission cancellation freezes unknown instead of claiming no transition");
            var retry = request with { RequestId = Guid.NewGuid(), ExpectedRevision = s.Revision, AutomaticallyEnterUpgrade = false };
            await Deny(() => broker.PrepareFirmwareAsync(retry), "manual prepare cannot bypass uncertain automatic context").ConfigureAwait(false);
            await Deny(() => broker.FinishFirmwareAsync(retry), "unknown context cannot finish maintenance").ConfigureAwait(false);
            await Deny(() => broker.LoadFirmwarePackageAsync(bundlePath), "unknown transition cannot be replaced by package load").ConfigureAwait(false);
            check(cancellingSource.Channel.Writes == 1, "source unknown-context denial never resends");
        }
        var uiStatus = new FirmwareJobStatus(Guid.NewGuid(), 1, new("fake", "fake", "1", "", "hash", "hash", "hash", true, "reviewed", true),
            FirmwarePhase.Loaded, "fake", false, false, true, 0, "unknown", root, null, null, DateTimeOffset.UtcNow);
        var uiService = new UiService(uiStatus);
        var vm = new FirmwareUpdateViewModel(() => uiService, InlineDispatcher.Instance, () => null, _ => true, _ => { });
        await vm.RefreshAsync().ConfigureAwait(false);
        check(uiService.Writes == 0, "UI refresh never auto enters");
        await vm.PrepareCommand.ExecuteAsync().ConfigureAwait(false);
        check(uiService.LastRequest is { AutomaticallyEnterUpgrade: true, OperatorConfirmedOtherWritersAbsent: true }, "UI explicit auto action carries only typed intent, not payload");
        await vm.ManualPrepareCommand.ExecuteAsync().ConfigureAwait(false);
        check(uiService.LastRequest is { AutomaticallyEnterUpgrade: false }, "UI manual fallback remains explicit without automatic command");
    }
}

internal sealed class EntryChannel : IHidReportChannel
{
    public int Writes, Reads, Disposals;
    public byte[]? Frame;
    public bool Pending, Disconnect;
    public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Deadline { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
    {
        Writes++; Frame = report.ToArray(); Started.TrySetResult();
        using var registration = ct.Register(() => Deadline.TrySetResult());
        if (Pending) await Complete.Task.ConfigureAwait(false); // deliberately uncooperative pending native fake
        if (Disconnect || ct.IsCancellationRequested) throw new IOException("Fake disconnect/unknown completion");
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { Reads++; throw new InvalidOperationException("Normal reads forbidden"); }
    public void Dispose() => Disposals++;
}

internal sealed class AutomaticFakePhysical(string directory) : IPhysicalObservationSource, IAutomaticPhysicalObservationSource
{
    private readonly FakePhysical _physical = new(directory);
    public EntryChannel Channel { get; } = new();
    public bool HoldBoot { set => _physical.HoldBootEntry = value; }
    public Task<NormalObservation> ReadCurrentNormalUsbAsync(CancellationToken ct) => _physical.ReadCurrentNormalUsbAsync(ct);
    public Task<NormalObservation> ObserveCurrentNormalUsbAsync(RetainedBaselineReceipt? retained, CancellationToken ct) => _physical.ObserveCurrentNormalUsbAsync(retained, ct);
    public Task<BootObservation> WatchNormalRemovalAndMatchingBootAsync(NormalObservation normal, CancellationToken ct) => _physical.WatchNormalRemovalAndMatchingBootAsync(normal, ct);
    public Task<bool> ObserveFreshNormalReplugAsync(PhysicalBinding binding, CancellationToken ct) => _physical.ObserveFreshNormalReplugAsync(binding, ct);
    public Task<BootObservation> EnterUpgradeAsync(NormalObservation normal, string dir, PhysicalPreparationAuthorization authorization, Action admitted, CancellationToken ct)
        => new AutomaticBootEntry(() => AutoEntryChecks.Target, _ => Channel, async (n, token) =>
            { await Channel.Started.Task.ConfigureAwait(false); return await WatchNormalRemovalAndMatchingBootAsync(n, token).ConfigureAwait(false); })
            .ExecuteAsync(normal, AutoEntryChecks.Target, dir, authorization, admitted, ct);
}
