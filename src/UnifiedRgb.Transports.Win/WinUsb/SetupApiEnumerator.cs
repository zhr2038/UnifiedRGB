using System.Globalization;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Transports.WinUsb;

/// <summary>A present device interface: its symbolic link and the VID/PID/serial parsed from it.</summary>
public sealed record UsbDeviceInfo(string Path, ushort Vid, ushort Pid, string InstanceSuffix)
{
    /// <summary>Parses "\\?\usb#vid_0cf2&amp;pid_a200&amp;mi_01#6&amp;123abc&amp;0&amp;0001#{guid}" (case-insensitive). Null when not a USB/HID path.</summary>
    public static UsbDeviceInfo? FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var lower = path.ToLowerInvariant();
        var v = lower.IndexOf("vid_", StringComparison.Ordinal);
        var p = lower.IndexOf("pid_", StringComparison.Ordinal);
        if (v < 0 || p < 0 || v + 8 > lower.Length || p + 8 > lower.Length
            || !ushort.TryParse(lower.AsSpan(v + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            || !ushort.TryParse(lower.AsSpan(p + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
        {
            return null;
        }

        var parts = path.Split('#');
        return new UsbDeviceInfo(path, vid, pid, parts.Length >= 3 ? parts[2] : string.Empty);
    }
}

public interface IUsbEnumerator
{
    IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass);
}

/// <summary>SetupAPI enumeration of present device interfaces of one class (read-only).</summary>
public sealed unsafe class SetupApiEnumerator : IUsbEnumerator
{
    public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass)
    {
        var set = WinUsbNative.SetupDiGetClassDevs(interfaceClass, null, IntPtr.Zero, WinUsbNative.DigcfPresent | WinUsbNative.DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            throw new InvalidOperationException($"SetupDiGetClassDevs failed ({Marshal.GetLastPInvokeError()})");
        }

        var list = new List<UsbDeviceInfo>();
        var buf = new byte[1024];
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { Size = (uint)sizeof(SpDeviceInterfaceData) };
                if (!WinUsbNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, interfaceClass, i, ref data))
                {
                    break; // ERROR_NO_MORE_ITEMS
                }

                WinUsbNative.SetupDiGetDeviceInterfaceDetail(set, ref data, null, 0, out var required, IntPtr.Zero);
                if (required < 8 || required > 64 * 1024)
                {
                    continue;
                }

                if (buf.Length < required)
                {
                    buf = new byte[required];
                }

                string path;
                fixed (byte* b = buf)
                {
                    *(uint*)b = 8; // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) on x64
                    if (!WinUsbNative.SetupDiGetDeviceInterfaceDetail(set, ref data, b, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    path = new string((char*)(b + 4));
                }

                if (UsbDeviceInfo.FromPath(path) is { } info)
                {
                    list.Add(info);
                }
            }
        }
        finally
        {
            WinUsbNative.SetupDiDestroyDeviceInfoList(set);
        }

        return list;
    }
}
