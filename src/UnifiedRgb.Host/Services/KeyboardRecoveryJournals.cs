using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Services;

/// <summary>Fixed local recovery paths; deferred routes remain pending during configuration and firmware handoff.</summary>
internal static class KeyboardRecoveryJournals
{
    internal static string[] Paths(string active) => [active, active + ".deferred-wired", active + ".deferred-wireless"];
    internal static bool Any(string active) => Paths(active).Any(Path.Exists);
}

public sealed partial class KeyboardDisplayService
{
    private string DeferredPath(string kind) => _journalPath + ".deferred-" + kind;

    private void ValidateDeferredRecovery()
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in KeyboardRecoveryJournals.Paths(_journalPath))
        {
            if (!Path.Exists(path)) continue;
            var record = ReadRecoveryForRoute(path, path != _journalPath);
            var kind = RecoveryKind(record);
            if (!kinds.Add(kind)) throw new InvalidDataException("Duplicate keyboard recovery routes; records retained, no acquisition or replay.");
        }
    }

    private RecoveryRecord ReadRecoveryForRoute(string path, bool deferred)
    {
        RequireOrdinaryRecoveryFile(path);
        var record = Read<RecoveryRecord>(path);
        if (record.Schema is not (1 or 2) || deferred && record.Schema != 2 || record.Schema == 2 && record.ProfileToken is < 1 or > 6
            || record.Brightness > 100 || (record.Lighting is null) != (record.StaticBank is null)) throw MissingRecoveryScope();
        if (record.Lighting is not null && record.StaticBank is not null)
        {
            if (record.StaticBank.EffectType != 0 || record.Lighting.SoftwareControl is > 1 || record.StaticBank.SoftwareControl is > 1
                || record.Lighting.Payloads is null || record.StaticBank.Payloads is null
                || record.Lighting.Payloads.Any(payload => payload is null) || record.StaticBank.Payloads.Any(payload => payload is null))
                throw new InvalidDataException("Recovery lighting record is incomplete; no acquisition or transfer.");
            _ = RogProtocol.KeyboardNativeReports(2, record.Lighting);
            _ = RogProtocol.KeyboardNativeReports(2, record.StaticBank);
        }
        var kind = RecoveryKind(record); // validate exact configured device plus transport before moving anything
        if (deferred && path != DeferredPath(kind)) throw new InvalidDataException("Deferred recovery path and recorded transport differ; records retained.");
        return record;
    }

    private string RecoveryKind(RecoveryRecord record)
    {
        var native = _nativeMenu?.QualifiedRoutes.FirstOrDefault(route => record.DeviceKey == route.DeviceKey
            && SameProtocol(record.FirmwareHeader, route.RouteAnchor));
        if (native is not null) return native.ConnectionKind;
        if (HasWiredPins(_options.KeyboardDisplay) && record.DeviceKey == _options.KeyboardDisplay.DeviceKey
            && SameProtocol(record.FirmwareHeader, WiredLabel)) return "wired";
        if (HasWirelessPins(_options.KeyboardDisplay) && record.DeviceKey == _options.KeyboardDisplay.WirelessReceiverKey
            && SameProtocol(record.FirmwareHeader, WirelessLabel)) return "wireless";
        throw new RecoveryScopeException("Recovery journal does not match a verified configured transport; journal retained.",
            "恢复记录与已验证的有线或无线传输不匹配；已保留，未替换或猜测设备");
    }

    private static bool SameRecoveryRoute(RecoveryRecord record, IRogOledConnection connection) =>
        record.DeviceKey == connection.DeviceKey && SameProtocol(record.FirmwareHeader, ConnectionLabel(connection));

    private bool IsKnownAvailableWiredRoute(IRogOledConnection connection) =>
        connection.ConnectionKind == "wired" && connection.PixelPayloadCapacity == 60
        && ((_nativeMenu?.QualifiedRoutes.Any(route => route.ConnectionKind == "wired"
                && route.MatchesScope(connection.DeviceKey, connection.FirmwareHeader, connection.ConnectionKind)) ?? false)
            || HasWiredPins(_options.KeyboardDisplay) && connection.DeviceKey == _options.KeyboardDisplay.DeviceKey
                && SameProtocol(ConnectionLabel(connection), WiredLabel))
        && connection.SameDevice();

    private void ActivateDeferredRecovery(IRogOledConnection connection)
    {
        var path = DeferredPath(connection.ConnectionKind);
        if (!Path.Exists(path)) return;
        var record = ReadRecoveryForRoute(path, deferred: true);
        if (!SameRecoveryRoute(record, connection)) throw MissingRecoveryScope();
        TransferRecovery(path, _journalPath);
    }

    // Preserve the complete record durably before removing its old name. A collision or interrupted transfer is
    // intentionally visible as a duplicate on the next pass; it never authorizes silently dropping an obligation.
    private static void TransferRecovery(string source, string destination)
    {
        RequireOrdinaryRecoveryFile(source);
        if (Path.Exists(destination)) throw new IOException("Recovery destination already exists; records retained.");
        var bytes = ReadBytes(source);
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        RequireOrdinaryRecoveryFile(source);
        RequireOrdinaryRecoveryFile(destination);
        if (!ReadBytes(source).AsSpan().SequenceEqual(bytes) || !ReadBytes(destination).AsSpan().SequenceEqual(bytes))
            throw new IOException("Recovery record changed during preservation; both records retained.");
        File.Delete(source);
    }

    private static void RequireOrdinaryRecoveryFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Keyboard recovery path is not an ordinary file; no acquisition or replay.");
    }
}
