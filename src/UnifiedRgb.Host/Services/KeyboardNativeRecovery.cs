using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Services;

internal sealed class KeyboardRecoveryIntentChangedException()
    : IOException("Keyboard intent changed during read-only recovery.");

public sealed partial class KeyboardDisplayService
{
    private static bool ReadOnlyRecoveryUnavailable(Exception error) =>
        error is RogOledTimeoutException or RogOledEnvelopeRefusedException or RogOledUnexpectedReplyException;

    /// <summary>
    /// A native page can start a fresh ARM epoch without stock preset/brightness writes.
    /// Keep the original recovery record when fresh state proves it is still the same
    /// profile and its persistent settings match. Final shutdown still owes that record.
    /// </summary>
    private async Task<bool> TryResumeNativeRecoveryAsync(RogWiredOledSession session,
        IRogOledConnection connection, RecoveryRecord recorded, byte[] recordedBytes, bool ownsLighting, CancellationToken ct)
    {
        if (_nativeMenu?.Requested != true || !NativePresentationDeclared(connection)
            || _configurationGate?.FreshResumeAllowed == true || ownsLighting != (recorded.Lighting is not null)) return false;
        if (session.MutationAdmitted) throw new InvalidOperationException("Native recovery reconciliation must precede mutations.");

        var intent = Interlocked.Read(ref _intentRevision);
        var nativeIntent = _nativeMenu.RequestRevision;
        void RequireCurrentIntent()
        {
            ct.ThrowIfCancellationRequested();
            if (intent != Interlocked.Read(ref _intentRevision) || nativeIntent != _nativeMenu.RequestRevision)
                throw new KeyboardRecoveryIntentChangedException();
        }
        try
        {
            var before = await ReadNativeRecoveryCapabilityAsync(connection, ct).ConfigureAwait(false);
            RequireCurrentIntent();
            if (_configurationGate?.RogAcquisitionAllowed == false || _configurationGate?.FreshResumeAllowed == true
                || _guard.Check(DeviceClass.AsusRog).State != GuardState.Clear)
                throw new InvalidOperationException("Keyboard ownership guard changed during recovery.");
            if (!_nativeMenu.PageRequested(before) || !RogNativeMenuCodec.SupportsDedicatedPage(before)) return false;
            RequireNativeRecoveryProfile(session, before);
            if (ownsLighting) await session.SnapshotLightingAsync(ct).ConfigureAwait(false);
            var after = await ReadNativeRecoveryCapabilityAsync(connection, ct).ConfigureAwait(false);
            RequireCurrentIntent();
            RequireNativeRecoveryProfile(session, after);
            if (before.Mode != after.Mode || before.Profile != after.Profile || before.Transport != after.Transport
                || before.Epoch != after.Epoch || before.Cookie != after.Cookie)
                throw new KeyboardPresentationNotReadyException(new IOException("Native scope changed during read-only reconciliation."));

            if (!_nativeMenu.PageRequested(after)) throw new KeyboardRecoveryIntentChangedException();
            if (_configurationGate?.RogAcquisitionAllowed == false || _configurationGate?.FreshResumeAllowed == true
                || _guard.Check(DeviceClass.AsusRog).State != GuardState.Clear)
                throw new InvalidOperationException("Keyboard ownership guard changed during recovery.");
            if (!connection.SameDevice()) throw new InvalidDataException("Keyboard physical scope changed during recovery.");
            if (!File.Exists(_journalPath) || !ReadBytes(_journalPath).AsSpan().SequenceEqual(recordedBytes))
                throw new RecoveryScopeException("Recovery record changed during read-only reconciliation.",
                    "恢复记录在核对期间发生变化；已停止接管");

            var goal = ResolveNativeGoal(session, connection, recorded.Brightness, allowFreshNative: false, ct);
            var transport = RogPerKeyLighting.TransportFor(connection.ConnectionKind);
            var perKeyReady = ownsLighting && transport is not null
                && RogPerKeyPlanner.Plan(_options.AsusRog.KeyboardPerKey, [transport], transport)?.Ready == true;
            var adopted = session.TryAdoptNativeRecoveryBaseline(recorded.Brightness, goal,
                recorded.Lighting, recorded.StaticBank, allowActiveSoftwareControl: perKeyReady);
            if (adopted)
                Serilog.Log.Information("Keyboard native recovery reconciled without settings writes: transport={Transport}, profileToken={Profile}, nativeProfile={NativeProfile}",
                    connection.ConnectionKind, session.VerifiedProfileToken, after.Profile);
            return adopted;
        }
        catch (Exception ex) when (!session.MutationAdmitted && ReadOnlyRecoveryUnavailable(ex))
        {
            RequireCurrentIntent();
            throw new KeyboardPresentationNotReadyException(ex);
        }
    }

    private static async Task<RogNativeCapability> ReadNativeRecoveryCapabilityAsync(IRogOledConnection connection, CancellationToken ct)
    {
        var reply = await connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false);
        var capability = RogNativeMenuCodec.ParseCapability(reply, 0x12, connection.ConnectionKind == "wired" ? (byte)1 : (byte)2);
        return capability;
    }

    private static void RequireNativeRecoveryProfile(RogWiredOledSession session, RogNativeCapability capability)
    {
        // The stock GET12 handler encodes raw profile0 as6 and leaves1..5 unchanged.
        // GYM1 exposes raw0..5. The actual25/D50 producer bytes establish this mapping.
        if (capability.Profile != session.VerifiedProfileToken % 6)
            throw new RecoveryScopeException("Native capability and the verified keyboard profile differ.",
                "原生页面与键盘配置档不一致；保留恢复记录并停止接管");
    }
}
