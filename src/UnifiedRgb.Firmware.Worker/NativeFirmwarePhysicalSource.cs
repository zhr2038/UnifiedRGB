using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BootReadOnly;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Firmware.Worker;

/// <summary>Explicit preparation only. Normal identity uses the existing proven wired BasicInfo getter.
/// Boot observation is metadata-only; the unchanged R6 transaction is the FIRST boot report owner and
/// validates the actual 02000200 reply. Entry is compiled, explicit, single-write only; no RF/reset/exit route.</summary>
public sealed class NativeFirmwarePhysicalSource(string evidenceDirectory) : IPhysicalObservationSource, IAutomaticPhysicalObservationSource
{
    private string? _normalPath;
    private NormalMetadata? _normalMetadata;
    private RetainedBaselineReceipt? _retained;
    private NormalEntryTarget? _entryTarget;
    private AutomaticBootEntry? _entry;
    private static HidCollectionInfo[] NormalCollections() => new WinHidReportDevices().Enumerate(0x0B05, 0x1C24).Where(RogWiredOledProtocol.Matches).ToArray();
    public async Task<NormalObservation> ReadCurrentNormalUsbAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Inventory.Enumerate().Any()) throw new InvalidDataException("Already in boot mode: reconnect normal USB before preparation");
        var normal = NormalCollections();
        if (normal.Length != 1) throw new InvalidDataException("Exactly one normal USB 1C24 control interface required");
        var c = normal[0]; var metadata = NormalMetadata.Read(c.Path);
        using var connection = new RogWiredOledFactory().TryOpen(RogWiredOledProtocol.DeviceKey(c), "001200000025000100")
            ?? throw new IOException("Normal USB exclusive getter unavailable");
        var reply = await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, ct).ConfigureAwait(false);
        connection.RequireFirmwareIdentity(reply, "001200000025000100");
        var header = Convert.ToHexString(reply.AsSpan(0, 9));
        if (header != "001200000025000100" || !connection.SameDevice() || NormalMetadata.Read(c.Path) != metadata)
            throw new InvalidDataException("Exact actual normal USB header/physical identity changed");
        var at = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(evidenceDirectory); FirmwareFiles.NoReparse(evidenceDirectory);
        var path = Path.Combine(evidenceDirectory, "normal-" + Guid.NewGuid().ToString("N") + ".json");
        FirmwareFiles.CreateNew(path, JsonSerializer.SerializeToUtf8Bytes(new { at, c.Path, metadata, RawReply = Convert.ToHexString(reply), Header = header }));
        _normalPath = c.Path;
        _normalMetadata = metadata;
        BindEntry(c, metadata);
        return new("1C24", header, metadata.ContainerId, metadata.PhysicalLocation, at, path);
    }
    public async Task<NormalObservation> ObserveCurrentNormalUsbAsync(RetainedBaselineReceipt? retained, CancellationToken ct)
    {
        _retained = retained;
        await NormalUsbReadiness.WaitAsync(() => (NormalCollections().Length, Inventory.Enumerate().Count),
            (duration, token) => Task.Delay(duration, token), ct).ConfigureAwait(false);
        if (retained is null) return await ReadCurrentNormalUsbAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Inventory.Enumerate().Any()) throw new InvalidDataException("Reconnect normal USB first; retained data cannot mint a boot transition");
        var normal = NormalCollections();
        if (normal.Length != 1) throw new InvalidDataException("Exactly one current normal USB interface required");
        var metadata = NormalMetadata.Read(normal[0].Path);
        if (metadata.ContainerId != retained.ContainerId || metadata.PhysicalLocation != retained.PhysicalLocation)
            throw new InvalidDataException("Owner-confirmed baseline/current physical USB mismatch");
        var at = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(evidenceDirectory); FirmwareFiles.NoReparse(evidenceDirectory);
        var path = Path.Combine(evidenceDirectory, "normal-metadata-" + Guid.NewGuid().ToString("N") + ".json");
        FirmwareFiles.CreateNew(path, JsonSerializer.SerializeToUtf8Bytes(new
        { at, normal[0].Path, metadata, HeaderOrigin = RetainedBaselinePolicy.Origin, RetainedOwnerConfirmedAtUtc = retained.OwnerConfirmedAtUtc, retained.ImageSha256,
            Note = "Fresh physical metadata only; no GET12, no fresh firmware read/header claim" }));
        _normalPath = normal[0].Path; _normalMetadata = metadata;
        BindEntry(normal[0], metadata);
        return new NormalObservation("1C24", retained.NormalFirmwareHeader, metadata.ContainerId, metadata.PhysicalLocation, at, path, RetainedBaselinePolicy.Origin);
    }
    private void BindEntry(HidCollectionInfo collection, NormalMetadata metadata)
    {
        _entryTarget = new(collection, metadata.ContainerId, metadata.PhysicalLocation, metadata.HidInstanceId, metadata.UsbAncestor);
        _entry = new(CaptureNormal, target => WinHidReportDevices.OpenExclusiveWriter(target.Collection), WatchNormalRemovalAndMatchingBootAsync);
    }
    private static NormalEntryTarget CaptureNormal()
    {
        var normals = NormalCollections();
        if (normals.Length != 1) throw new InvalidDataException("Exactly one current normal collection required");
        var metadata = NormalMetadata.Read(normals[0].Path);
        return new(normals[0], metadata.ContainerId, metadata.PhysicalLocation, metadata.HidInstanceId, metadata.UsbAncestor);
    }
    public Task<BootObservation> EnterUpgradeAsync(NormalObservation normal, string directory,
        PhysicalPreparationAuthorization authorization, Action writeAdmitted, CancellationToken ct)
        => (_entry ?? throw new InvalidOperationException("No current normal entry context"))
            .ExecuteAsync(normal, _entryTarget!, directory, authorization, writeAdmitted, ct);

    public Task<BootObservation> WatchNormalRemovalAndMatchingBootAsync(NormalObservation normal, CancellationToken ct)
    {
        if (_normalPath is null || _entryTarget is null || _normalMetadata is null ||
            normal.ContainerId != _normalMetadata.ContainerId || normal.PhysicalLocation != _normalMetadata.PhysicalLocation)
            throw new InvalidOperationException("No exact current normal binding");
        EntryMetadataSnapshot Capture()
        {
            var normals = NormalCollections();
            var targets = normals.Select(c =>
            {
                var m = NormalMetadata.Read(c.Path);
                return new NormalEntryTarget(c, m.ContainerId, m.PhysicalLocation, m.HidInstanceId, m.UsbAncestor);
            }).ToArray();
            var rows = Inventory.Enumerate(); // access0 caps/metadata only; NEVER boot82
            var boot = BootEntryMetadataSelector.Select(rows, normal.PhysicalLocation, DateTimeOffset.UtcNow);
            return new(targets, boot is null ? [] : [boot]);
        }
        return BootTransitionWatcher.WatchAsync(_entryTarget, Capture, token => Task.Delay(1000, token), ct);
    }
    public async Task<bool> ObserveFreshNormalReplugAsync(PhysicalBinding binding, CancellationToken ct)
    {
        // Compare against the actual NORMAL identity, not the different boot container.
        var normalContainer = _normalMetadata?.ContainerId;
        var normalLocation = _normalMetadata?.PhysicalLocation;
        var observation = await ObserveCurrentNormalUsbAsync(_retained, ct).ConfigureAwait(false);
        return observation.ObservedAtUtc > binding.ObservedAtUtc && observation.ContainerId == normalContainer && observation.PhysicalLocation == normalLocation;
    }
}

internal sealed record NormalMetadata(string ContainerId, string PhysicalLocation, string HidInstanceId, string UsbAncestor)
{
    public static NormalMetadata Read(string interfacePath)
    {
        var buffer = new byte[8192]; uint size = (uint)buffer.Length;
        var instanceKey = new MetadataKey(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);
        var result = MetadataNative.InterfaceProperty(interfacePath, in instanceKey, out var type, buffer, ref size, 0);
        if (result != 0 || type != 18 || size < 4 || size > buffer.Length || size % 2 != 0)
            throw new IOException($"读取键盘 USB 身份失败（CONFIGRET 0x{result:X}，类型 0x{type:X}，长度 {size}）。");
        var instanceChars = Encoding.Unicode.GetString(buffer, 0, checked((int)size));
        if (instanceChars[^1] != '\0' || instanceChars.AsSpan(0, instanceChars.Length - 1).Contains('\0'))
            throw new InvalidDataException("Normal interface instance ID is not exactly one terminated string");
        var instance = instanceChars[..^1];
        if (!instance.StartsWith("HID\\VID_0B05&PID_1C24", StringComparison.OrdinalIgnoreCase) || MetadataNative.Locate(out var node, instance, 0) != 0)
            throw new InvalidDataException("Normal present HID instance mismatch");
        for (var depth = 0; depth < 12; depth++)
        {
            var id = new char[1024];
            if (MetadataNative.DeviceId(node, id, (uint)id.Length, 0) != 0) throw new IOException("Normal ancestor metadata unavailable");
            if (new string(id).TrimEnd('\0').StartsWith("USB\\VID_0B05&PID_1C24\\", StringComparison.OrdinalIgnoreCase))
            {
                var containerKey = new MetadataKey(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
                size = (uint)buffer.Length;
                if (MetadataNative.NodeProperty(node, in containerKey, out type, buffer, ref size, 0) != 0 || type != 13 || size != 16) throw new IOException("Normal container unavailable");
                var container = new Guid(buffer.AsSpan(0, 16));
                if (container == Guid.Empty || container == new Guid("00000000-0000-0000-ffff-ffffffffffff")) throw new InvalidDataException("Nonspecific normal container");
                var locationKey = new MetadataKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 37);
                size = (uint)buffer.Length;
                if (MetadataNative.NodeProperty(node, in locationKey, out type, buffer, ref size, 0) != 0 || type != 0x2012 || size < 4 || size > buffer.Length) throw new IOException("Normal location unavailable");
                var locations = Encoding.Unicode.GetString(buffer, 0, checked((int)size)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var usbLocations = locations.Where(location => location.Contains("#USB(", StringComparison.Ordinal)).ToArray();
                if (usbLocations.Length != 1) throw new InvalidDataException("Exactly one normal USB port path required");
                return new(container.ToString("D"), usbLocations[0], instance, new string(id).TrimEnd('\0'));
            }
            if (MetadataNative.Parent(out var parent, node, 0) != 0) break;
            node = parent;
        }
        throw new InvalidDataException("No exact normal USB physical ancestor");
    }
}
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MetadataKey(Guid format, uint property)
{
    public readonly Guid Format = format;
    public readonly uint Property = property;
}
internal static class MetadataNative
{
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", CharSet = CharSet.Unicode)]
    internal static extern uint InterfaceProperty(string path, in MetadataKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    internal static extern uint Locate(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    internal static extern uint Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    internal static extern uint DeviceId(uint node, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
    internal static extern uint NodeProperty(uint node, in MetadataKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
