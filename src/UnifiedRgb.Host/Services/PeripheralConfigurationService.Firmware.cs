using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Firmware;

namespace UnifiedRgb.Host.Services;

// Root: copy beside PeripheralConfigurationService.cs AFTER applying host-wiring.patch.
public sealed partial class PeripheralConfigurationService : IFirmwareMaintenance
{
    // Called by HostComposition's EARLY gate factory, not the late service constructor.
    internal static PeripheralConfigurationGate CreateFirmwareAwareGate(UnifiedRgbOptions options)
    {
        var gate = new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load());
        if (FirmwareStartupLeaseGuard.BlocksAcquisition(FirmwareStartupLeaseGuard.Read(options.ResolvedDataDirectory)))
            gate.BlockRogAcquisition();
        return gate;
    }
    private FirmwareMaintenanceLease? _firmwareLease;
    private bool _leaseKnownInThisProcess;
    private void InitializeFirmwareLease(string dataDirectory)
    {
        _firmwareLease = FirmwareStartupLeaseGuard.Read(dataDirectory);
        if (_firmwareLease.RecoveryError is not null)
        {
            _gate.BlockRogAcquisition();
            Publish(new(true, "Error", _firmwareLease.RecoveryError, false, DateTimeOffset.UtcNow, _firmwareLease.RecoveryError,
                new PeripheralHandoffIntentDto(PeripheralHandoffOrigins.Unknown, LeaseState: PeripheralMaintenanceLeaseStates.Unknown,
                    Reason: "Malformed firmware lease retained; owner is unknown, no invented lease ID")));
        }
        else if (_firmwareLease.Owner is not null)
        {
            _gate.BlockRogAcquisition();
            Publish(new(true, "Error", "Firmware maintenance remains after restart; outcome is unknown and requires reviewed recovery.",
                false, DateTimeOffset.UtcNow, "Firmware owner outcome unknown", FirmwareIntent(_firmwareLease.Owner.Value, PeripheralMaintenanceLeaseStates.Unknown)));
        }
    }
    private static PeripheralHandoffIntentDto FirmwareIntent(Guid jobId, string state) => new(
        PeripheralHandoffOrigins.FirmwareMaintenance, jobId.ToString("D"), "FirmwareJobBroker", PeripheralRoles.Wired,
        null, state, Reason: "Explicit firmware maintenance; never auto-exit or replay prior configuration");

    public async Task AcquireAsync(Guid jobId, CancellationToken ct)
    {
        await _gate.RunExclusiveAsync(async _ =>
        {
            ct.ThrowIfCancellationRequested();
            var lease = _firmwareLease ?? throw new InvalidOperationException("Firmware lease not initialized");
            async Task Release()
            {
                _gate.BlockRogAcquisition();
                _resumeReadyToCommit = false;
                Publish(new(true, "Entering", "Firmware maintenance: persisting ownership and joining keyboard/mouse sessions.", false,
                    DateTimeOffset.UtcNow, null, FirmwareIntent(jobId, PeripheralMaintenanceLeaseStates.Active)));
                try
                {
                    _marker.Save(); // failure NEVER re-enables acquisition
                    await ObserveReleaseAsync().ConfigureAwait(false); // existing release joins both keyboard and mouse owners
                    if (KeyboardRecoveryJournals.Any(_journalPath)) throw new IOException("Keyboard recovery obligations remain; firmware handoff is not Ready.");
                    Publish(new(true, "Ready", "Owned firmware lease: all ROG sessions released and joined.", true,
                        DateTimeOffset.UtcNow, null, FirmwareIntent(jobId, PeripheralMaintenanceLeaseStates.Active)));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var notReady = ex is UnifiedRgb.Devices.AsusRog.RogKeyboardNotReadyException || ex is AggregateException aggregate &&
                        aggregate.Flatten().InnerExceptions.Any(inner => inner is UnifiedRgb.Devices.AsusRog.RogKeyboardNotReadyException);
                    var detail = notReady
                        ? "原有无线键盘控制尚未恢复。请临时切回 2.4G 并唤醒键盘，再点自动进入升级模式；控制释放后按页面提示切到 USB。未发送升级命令，也未刷写固件；恢复记录和维护任务已保留。"
                        : "Firmware release failed; blocked and exact owner retained.";
                    Publish(new(true, "Error", detail, false,
                        DateTimeOffset.UtcNow, ex.Message, FirmwareIntent(jobId, PeripheralMaintenanceLeaseStates.Active)));
                    if (notReady) throw new IOException(detail, ex);
                    throw;
                }
            }
            if (lease.Owner == jobId && _leaseKnownInThisProcess)
                await lease.RetryReleaseUnderHostGateAsync(jobId, Release).ConfigureAwait(false);
            else
            {
                if (Volatile.Read(ref _status).ConfigurationMode || !_gate.RogAcquisitionAllowed || _releaseInFlight is not null || _resumeInFlight is not null)
                    throw new InvalidOperationException("Manual/newer/unknown peripheral intent cannot be adopted by firmware");
                _leaseKnownInThisProcess = true;
                await lease.AcquireUnderHostGateAsync(jobId, Release).ConfigureAwait(false);
            }
            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task FinishAsync(Guid jobId, bool knownWireClosed, bool freshNormal, CancellationToken ct)
    {
        await _gate.RunExclusiveAsync(async _ =>
        {
            ct.ThrowIfCancellationRequested();
            var lease = _firmwareLease ?? throw new InvalidOperationException("Firmware lease not initialized");
            if (!_leaseKnownInThisProcess || Volatile.Read(ref _status) is not { ConfigurationMode: true, InterfacesReleased: true } ||
                _releaseInFlight is not null || KeyboardRecoveryJournals.Any(_journalPath))
                throw new InvalidOperationException("Release/session outcome unknown; scoped finish denied");
            await lease.FinishUnderHostGateAsync(jobId, knownWireClosed, freshNormal, async () =>
            {
                _gate.BlockRogAcquisition();
                if (!_resumeReadyToCommit)
                {
                    _gate.BeginFreshResume();
                    try
                    {
                        await ObserveResumeAsync().ConfigureAwait(false); // fresh current settings only
                        _resumeReadyToCommit = true;
                    }
                    finally { _gate.EndFreshResume(); }
                }
                _marker.Remove(); // no acquisition is allowed until lease file deletion also commits
            }).ConfigureAwait(false);
            _leaseKnownInThisProcess = false;
            _resumeReadyToCommit = false;
            _gate.AllowRogAcquisition(); // ONLY after both persisted markers were safely removed
            Publish(new(false, "Active", "Firmware maintenance explicitly finished; fresh sessions own current settings.", false,
                DateTimeOffset.UtcNow, null));
            return true;
        }, ct).ConfigureAwait(false);
    }
}
