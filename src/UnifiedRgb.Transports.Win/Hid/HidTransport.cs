using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Transports.Hid;

[StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct HiddAttributes
{
    public uint Size;
    public ushort VendorId;
    public ushort ProductId;
    public ushort VersionNumber;
}

internal static partial class HidNative
{
    [LibraryImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);
}

/// <summary>
/// HID output/input reports (Lian Li HIDUsb path). Byte 0 of every frame is the report id; the opcode firewall is checked
/// inside <see cref="SendAsync"/> with the HID layout (opcodes shifted by one).
/// </summary>
public sealed class HidTransport : ITransport, IDisposable
{
    private readonly FileStream _stream;
    private readonly OpcodeLayout _layout;
    private readonly RelaySequence _sequence = new();

    private HidTransport(FileStream stream, OpcodeLayout layout)
    {
        _stream = stream;
        _layout = layout;
    }

    public static Guid InterfaceClass
    {
        get
        {
            HidNative.HidD_GetHidGuid(out var g);
            return g;
        }
    }

    public static HidTransport Open(UsbDeviceInfo device, OpcodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layout);
        var handle = WinUsbNative.CreateFile(device.Path, WinUsbNative.GenericRead | WinUsbNative.GenericWrite, WinUsbNative.FileShareReadWrite,
            IntPtr.Zero, WinUsbNative.OpenExisting, WinUsbNative.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateFile({device.Path}) failed");
        }

        var attr = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
        if (!HidNative.HidD_GetAttributes(handle, ref attr) || attr.VendorId != device.Vid || attr.ProductId != device.Pid)
        {
            handle.Dispose();
            throw new InvalidOperationException($"{device.Path} is not the expected HID device {device.Vid:X4}:{device.Pid:X4}");
        }

        return new HidTransport(new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true), layout);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        _sequence.Check(frame.Span, _layout);
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _stream.ReadAsync(buffer, ct);

    public void Dispose() => _stream.Dispose();
}
