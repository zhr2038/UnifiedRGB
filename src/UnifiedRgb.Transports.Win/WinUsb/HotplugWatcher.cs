using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Transports.WinUsb;

public enum DeviceInterfaceChange
{
    Arrived,
    Removed,
}

public interface IHotplugSource : IDisposable
{
    event Action<DeviceInterfaceChange, string>? Changed;
}

/// <summary>
/// Device-interface arrival/removal for one interface class via <c>CM_Register_Notification</c> (cfgmgr32). The callback
/// runs on a system thread pool thread; subscribers must be quick and thread-safe.
/// </summary>
public sealed unsafe partial class HotplugWatcher : IHotplugSource
{
    private const int FilterTypeDeviceInterface = 0;
    private const int ActionInterfaceArrival = 0;
    private const int ActionInterfaceRemoval = 1;
    private const int FilterSize = 416; // CM_NOTIFY_FILTER: 16-byte header + 400-byte union

    private GCHandle _self;
    private IntPtr _registration;

    public HotplugWatcher(Guid interfaceClass)
    {
        _self = GCHandle.Alloc(this);
        var filter = stackalloc byte[FilterSize];
        new Span<byte>(filter, FilterSize).Clear();
        *(uint*)filter = FilterSize;
        *(int*)(filter + 8) = FilterTypeDeviceInterface;
        *(Guid*)(filter + 16) = interfaceClass;
        var rc = CM_Register_Notification(filter, GCHandle.ToIntPtr(_self), &Callback, out _registration);
        if (rc != 0)
        {
            _self.Free();
            throw new InvalidOperationException($"CM_Register_Notification failed (CONFIGRET {rc})");
        }
    }

    public event Action<DeviceInterfaceChange, string>? Changed;

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = CM_Unregister_Notification(_registration); // best effort on dispose
            _registration = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Callback(IntPtr notify, IntPtr context, int action, byte* eventData, uint size)
    {
        try
        {
            if (context == IntPtr.Zero || eventData == null || size < 26 || *(int*)eventData != FilterTypeDeviceInterface
                || GCHandle.FromIntPtr(context).Target is not HotplugWatcher w)
            {
                return 0;
            }

            var link = new string((char*)(eventData + 24)); // FilterType, Reserved, ClassGuid, then SymbolicLink
            var change = action switch
            {
                ActionInterfaceArrival => DeviceInterfaceChange.Arrived,
                ActionInterfaceRemoval => DeviceInterfaceChange.Removed,
                _ => (DeviceInterfaceChange?)null,
            };
            if (change is { } c)
            {
                w.Changed?.Invoke(c, link);
            }
        }
#pragma warning disable CA1031 // exceptions must not cross the native callback boundary
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return 0;
    }

    [LibraryImport("cfgmgr32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint CM_Register_Notification(
        byte* filter, IntPtr context, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, byte*, uint, uint> callback, out IntPtr registration);

    [LibraryImport("cfgmgr32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint CM_Unregister_Notification(IntPtr registration);
}
