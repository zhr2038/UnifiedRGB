using System.Globalization;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Transports.Hid;

[StructLayout(LayoutKind.Sequential)]
internal struct DevPropKey
{
    public Guid FormatId;
    public uint PropertyId;

    public DevPropKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>The four Configuration Manager calls the resolver uses (cfgmgr32 or a fake in tests). Returns CONFIGRET.</summary>
internal interface IDevicePropertyNative
{
    uint GetInterfaceProperty(string interfacePath, DevPropKey key, out uint propertyType, Span<byte> buffer, out uint size);

    uint LocateDevNode(string instanceId, out uint devInst);

    uint GetDevNodeStatus(uint devInst, out uint status, out uint problem);

    uint GetDevNodeProperty(uint devInst, DevPropKey key, out uint propertyType, Span<byte> buffer, out uint size);
}

internal static unsafe partial class CfgMgrNative
{
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_Device_Interface_Property(string deviceInterface, in DevPropKey key, out uint propertyType,
        byte* buffer, ref uint bufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_DevNode_Property(uint devInst, in DevPropKey key, out uint propertyType,
        byte* buffer, ref uint bufferSize, uint flags);

    public sealed class Platform : IDevicePropertyNative
    {
        public static readonly Platform Instance = new();

        public uint GetInterfaceProperty(string interfacePath, DevPropKey key, out uint propertyType, Span<byte> buffer, out uint size)
        {
            size = (uint)buffer.Length;
            fixed (byte* p = buffer) return CM_Get_Device_Interface_Property(interfacePath, in key, out propertyType, p, ref size, 0);
        }

        /// <summary>CM_LOCATE_DEVNODE_NORMAL (0): only a device node that is present right now.</summary>
        public uint LocateDevNode(string instanceId, out uint devInst) => CM_Locate_DevNode(out devInst, instanceId, 0);

        public uint GetDevNodeStatus(uint devInst, out uint status, out uint problem) => CM_Get_DevNode_Status(out status, out problem, devInst, 0);

        public uint GetDevNodeProperty(uint devInst, DevPropKey key, out uint propertyType, Span<byte> buffer, out uint size)
        {
            size = (uint)buffer.Length;
            fixed (byte* p = buffer) return CM_Get_DevNode_Property(devInst, in key, out propertyType, p, ref size, 0);
        }
    }
}

/// <summary>
/// Physical container of one enumerated HID interface through the Windows Configuration Manager only (cfgmgr32; no
/// PowerShell/WMI process, no admin, no device handle): interface → <c>DEVPKEY_Device_InstanceId</c> (a terminated string
/// whose segments must be exactly the interface path's), the PRESENT device node, which must be started without a problem,
/// → <c>DEVPKEY_Device_ContainerId</c> (exactly a 16-byte GUID, not empty and not the local-machine container). Property
/// types and lengths are checked, buffers have fixed maxima and are never grown. Failure texts carry no path, instance id,
/// serial or GUID.
/// </summary>
public sealed class WinHidContainerResolver : IHidContainerResolver
{
    internal const uint CrSuccess = 0;
    internal const uint DevPropTypeGuid = 0x0000000D;
    internal const uint DevPropTypeString = 0x00000012;
    internal const uint DnStarted = 0x00000008;
    internal const uint DnHasProblem = 0x00000400;

    /// <summary>MAX_DEVICE_ID_LEN (200) UTF-16 characters including the terminator.</summary>
    internal const int MaximumInstanceIdBytes = 200 * sizeof(char);

    internal static readonly DevPropKey InstanceIdKey = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    internal static readonly DevPropKey ContainerIdKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    private readonly IDevicePropertyNative _native;
    private readonly TimeProvider _time;

    public WinHidContainerResolver(TimeProvider? time = null)
        : this(CfgMgrNative.Platform.Instance, time)
    {
    }

    internal WinHidContainerResolver(IDevicePropertyNative native, TimeProvider? time)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _time = time ?? TimeProvider.System;
    }

    public HidContainerResolution Resolve(HidCollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var pathParts = collection.Path.Split('#');
        if (collection.Interface is null or 0 || pathParts.Length != 4
            || !pathParts[0].EndsWith("hid", StringComparison.OrdinalIgnoreCase) || !pathParts[0].StartsWith(@"\\?\", StringComparison.Ordinal))
            return HidContainerResolution.Unavailable("not an enumerated HID interface path");

        Span<byte> text = stackalloc byte[MaximumInstanceIdBytes];
        text.Clear();
        var result = _native.GetInterfaceProperty(collection.Path, InstanceIdKey, out var type, text, out var size);
        if (result != CrSuccess) return Failed("interface instance id is unavailable", result);
        if (type != DevPropTypeString || size < 2 * sizeof(char) || size > text.Length || size % sizeof(char) != 0)
            return HidContainerResolution.Unavailable("interface instance id has an unexpected property type or length");
        var chars = MemoryMarshal.Cast<byte, char>(text[..(int)size]);
        if (chars[^1] != '\0' || chars[..^1].Contains('\0'))
            return HidContainerResolution.Unavailable("interface instance id is not exactly one terminated string");
        var instanceId = new string(chars[..^1]);
        if (!BelongsTo(instanceId, pathParts))
            return HidContainerResolution.Unavailable("instance id does not belong to the requested HID interface");

        result = _native.LocateDevNode(instanceId, out var devInst);
        if (result != CrSuccess) return Failed("device node is not present", result);
        result = _native.GetDevNodeStatus(devInst, out var status, out var problem);
        if (result != CrSuccess) return Failed("device node status is unavailable", result);
        if ((status & DnStarted) == 0 || (status & DnHasProblem) != 0 || problem != 0)
            return HidContainerResolution.Unavailable("device node is not started without a problem");

        Span<byte> guid = stackalloc byte[16];
        guid.Clear();
        result = _native.GetDevNodeProperty(devInst, ContainerIdKey, out type, guid, out size);
        if (result != CrSuccess) return Failed("container id is unavailable", result);
        if (type != DevPropTypeGuid || size != 16) return HidContainerResolution.Unavailable("container id has an unexpected property type or length");
        var container = new Guid(guid);
        if (!HidPhysicalContainer.IsSpecific(container))
            return HidContainerResolution.Unavailable("container id is empty or the local-machine container");
        return HidContainerResolution.Resolved(HidPhysicalContainer.FromPlatformContainerId(container, _time.GetTimestamp()));
    }

    /// <summary>
    /// A HID interface symbolic link is <c>\\?\HID#&lt;hardware&gt;#&lt;instance&gt;#{class}</c> for the device instance
    /// <c>HID\&lt;hardware&gt;\&lt;instance&gt;</c>. Comparing segments binds the property to the requested interface; the
    /// container itself still comes only from the platform property.
    /// </summary>
    private static bool BelongsTo(string instanceId, string[] pathParts)
    {
        var parts = instanceId.Split('\\');
        return parts.Length == 3 && parts[0].Equals("HID", StringComparison.OrdinalIgnoreCase)
            && parts[1].Length > 0 && parts[2].Length > 0
            && parts[1].Equals(pathParts[1], StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals(pathParts[2], StringComparison.OrdinalIgnoreCase);
    }

    private static HidContainerResolution Failed(string what, uint result) =>
        HidContainerResolution.Unavailable(string.Create(CultureInfo.InvariantCulture, $"{what} (CONFIGRET 0x{result:X})"));
}
