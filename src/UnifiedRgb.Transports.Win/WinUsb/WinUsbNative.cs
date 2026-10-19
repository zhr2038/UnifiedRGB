using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Transports.WinUsb;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UsbInterfaceDescriptor
{
    public byte Length;
    public byte DescriptorType;
    public byte InterfaceNumber;
    public byte AlternateSetting;
    public byte NumEndpoints;
    public byte InterfaceClass;
    public byte InterfaceSubClass;
    public byte InterfaceProtocol;
    public byte Interface;
}

internal enum UsbdPipeType
{
    Control = 0,
    Isochronous = 1,
    Bulk = 2,
    Interrupt = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinUsbPipeInformation
{
    public UsbdPipeType PipeType;
    public byte PipeId;
    public ushort MaximumPacketSize;
    public byte Interval;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDeviceInterfaceData
{
    public uint Size;
    public Guid InterfaceClassGuid;
    public uint Flags;
    public IntPtr Reserved;
}

/// <summary>winusb.dll, setupapi.dll and kernel32 CreateFile. Read/write/query only: no control transfers, no reset, no power policy.</summary>
internal static unsafe partial class WinUsbNative
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareReadWrite = 0x3;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;
    public const uint DigcfPresent = 0x2;
    public const uint DigcfDeviceInterface = 0x10;
    public const uint PipeTransferTimeout = 0x03;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorSemTimeout = 121;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Initialize(SafeFileHandle device, out IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Free(IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternate, out UsbInterfaceDescriptor descriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternate, byte pipeIndex, out WinUsbPipeInformation info);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policy, uint length, uint* value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId, byte* buffer, uint length, out uint transferred, IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, byte* buffer, uint length, out uint transferred, IntPtr overlapped);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr SetupDiGetClassDevs(in Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, in Guid classGuid, uint index, ref SpDeviceInterfaceData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, byte* detail, uint size, out uint required, IntPtr deviceInfo);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
