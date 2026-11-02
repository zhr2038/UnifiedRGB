using System.Text.Json;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Firmware.Worker;

public sealed record NormalEntryTarget(HidCollectionInfo Collection, string ContainerId, string PhysicalLocation,
    string HidInstanceId, string UsbAncestor);
public sealed record TransitionIntent(int Schema, string Classification, Guid JobId, Guid RunId,
    NormalEntryTarget Target, string NormalHeaderOrigin, string PayloadSha256, DateTimeOffset AttemptedAtUtc);

/// <summary>Only compiled policy, never package/RPC bytes. The write deadline requests native cancellation;
/// safety join is unconditional even if a defective channel takes longer to settle. No detached I/O.</summary>
public sealed class AutomaticBootEntry(
    Func<NormalEntryTarget> captureCurrent,
    Func<NormalEntryTarget, IHidReportChannel> open,
    Func<NormalObservation, CancellationToken, Task<BootObservation>> watch)
{
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private int _attempted;
    public static byte[] Frame()
    {
        var frame = new byte[65];
        ReadOnlySpan<byte> magic = [0, 0x7B, 0xAA, 0x41, 0x53, 0x55, 0x53, 0xAA];
        magic.CopyTo(frame); return frame;
    }
    public async Task<BootObservation> ExecuteAsync(NormalObservation normal, NormalEntryTarget target, string directory,
        PhysicalPreparationAuthorization authorization, Action writeAdmitted, CancellationToken ct)
    {
        if (authorization is not { InterfacesReleasedAndJoined: true, OperatorConfirmedOtherWritersAbsent: true } ||
            authorization.MaintenanceLeaseId == Guid.Empty || !RogWiredOledProtocol.Matches(target.Collection) ||
            target.Collection.Error is not null || target.ContainerId != normal.ContainerId || target.PhysicalLocation != normal.PhysicalLocation ||
            !Guid.TryParse(target.ContainerId, out var container) || container == Guid.Empty ||
            !target.PhysicalLocation.Contains("#USB(", StringComparison.Ordinal) ||
            !target.Collection.Path.StartsWith("\\\\?\\hid#vid_0b05&pid_1c24", StringComparison.OrdinalIgnoreCase) ||
            HidCollectionInfo.InterfaceOf(target.Collection.Path) != 1 ||
            !target.HidInstanceId.StartsWith("HID\\VID_0B05&PID_1C24", StringComparison.OrdinalIgnoreCase) ||
            !target.UsbAncestor.StartsWith("USB\\VID_0B05&PID_1C24\\", StringComparison.OrdinalIgnoreCase) ||
            normal.Model != "1C24" || normal.FirmwareHeader != "001200000025000100" ||
            normal.ObservedAtUtc > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - normal.ObservedAtUtc > TimeSpan.FromSeconds(30))
            throw new InvalidDataException("Exact released owned lease and fresh normal collection/physical binding required");
        ct.ThrowIfCancellationRequested();
        if (!await Writer.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("Global firmware entry writer already owned");
        var admitted = false;
        Task<BootObservation>? observation = null;
        using var observing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observing.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (Volatile.Read(ref _attempted) != 0) throw new InvalidOperationException("Consumed normal context; never repeat an uncertain command");
            if (captureCurrent() != target) throw new InvalidDataException("Normal metadata/caps changed before open");
            var frame = Frame();
            FirmwareFiles.CreateNew(Path.Combine(directory, "transition-intent.json"), JsonSerializer.SerializeToUtf8Bytes(new TransitionIntent(
                1, "UNIFIEDRGB_NORMAL_TO_BOOT_INTENT_V1", authorization.MaintenanceLeaseId, Guid.NewGuid(), target,
                normal.HeaderOrigin, FirmwareFiles.Sha(frame), DateTimeOffset.UtcNow)));
            // Intent consumes this context even if opening or subsequent revalidation fails. Never retry it.
            Interlocked.Exchange(ref _attempted, 1);
            using (var channel = open(target))
            {
                if (captureCurrent() != target) throw new InvalidDataException("Normal binding changed after open, before write");
                ct.ThrowIfCancellationRequested();
                observation = watch(normal, observing.Token); // armed BEFORE write; metadata only, no boot report owner
                if (observation.IsCompleted)
                {
                    await observation.ConfigureAwait(false);
                    throw new InvalidDataException("Normal role transitioned before write admission; do not send");
                }
                ct.ThrowIfCancellationRequested();
                writeAdmitted();
                admitted = true;
                // Persisting/announcing admission can block. Recheck its observation and target at the LAST boundary;
                // a watcher that failed or completed during that callback must never be followed by a write.
                ct.ThrowIfCancellationRequested();
                if (captureCurrent() != target) throw new InvalidDataException("Normal binding changed at write boundary");
                if (observation.IsCompleted)
                {
                    await observation.ConfigureAwait(false);
                    throw new InvalidDataException("Normal role transitioned during write admission; do not send");
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                string outcome;
                try
                {
                    await channel.WriteAsync(frame, deadline.Token).ConfigureAwait(false); // exactly once, zero normal reads
                    outcome = deadline.IsCancellationRequested ? "CompletionAfterCancellation_Unknown" : "WriteCompleted_NotBootProof";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { outcome = "Unknown_NoResend_" + ex.GetType().Name; }
                FirmwareFiles.CreateNew(Path.Combine(directory, "transition-write-outcome.json"), JsonSerializer.SerializeToUtf8Bytes(new { outcome, at = DateTimeOffset.UtcNow }));
            } // actual write joined and old handle CLOSED before returning any new boot binding
            var boot = await observation.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); // after admission cancellation is always uncertain, never ordinary Cancelled
            return boot;
        }
        catch (Exception ex) when (admitted && ex is not OutOfMemoryException)
        { throw new TransitionUncertainException("进入升级结果未知；已尝试一次，禁止重发或旧正常 HID。保留维护与 intent，须正常重连/审核恢复。", ex); }
        finally
        {
            observing.Cancel();
            if (observation is not null)
            {
                try { await observation.ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* joined metadata observer; original outcome retained */ }
            }
            Writer.Release();
        }
    }
}

public sealed record EntryMetadataSnapshot(NormalEntryTarget[] Normals, BootObservation[] Boots);

/// <summary>Bounded passive role correlation. Cross-PID containers/ancestors are deliberately NOT aliased.</summary>
public static class BootTransitionWatcher
{
    public static async Task<BootObservation> WatchAsync(NormalEntryTarget target,
        Func<EntryMetadataSnapshot> capture, Func<CancellationToken, Task> delay, CancellationToken ct)
    {
        var removed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; attempt < 30 && clock.Elapsed < TimeSpan.FromSeconds(30); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = capture();
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(30)) throw new TimeoutException("Metadata observation exceeded transition deadline");
            if (attempt == 0 && (snapshot.Normals.Length != 1 || snapshot.Normals[0] != target || snapshot.Boots.Length != 0))
                throw new InvalidDataException("Observer must start with the exact normal target present and no boot role");
            if (snapshot.Normals.Length == 0) removed = true;
            else if (removed || snapshot.Normals.Length != 1 || snapshot.Normals[0] != target)
                throw new InvalidDataException("Normal changed/replugged within watched transition");
            if (snapshot.Boots.Length != 0)
            {
                if (!removed || snapshot.Normals.Length != 0 || snapshot.Boots.Length != 1 ||
                    snapshot.Boots[0].PhysicalLocation != target.PhysicalLocation)
                    throw new InvalidDataException("Unwitnessed, wrong-port or ambiguous boot appearance");
                return snapshot.Boots[0];
            }
            await delay(ct).ConfigureAwait(false);
        }
        throw new TimeoutException("No unique same-port boot within 30 passive observations; never resend");
    }
}
