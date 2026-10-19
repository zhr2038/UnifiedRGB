using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Transports.WinUsb;

/// <summary>The measured RECEIVER, not the sender relay. Its sole OUT message is literal GetDev(16,1).
/// No caller packets, control transfers or recovery commands. Bounded I/O; interface0/bulk01/81/64-byte pins.</summary>
public static unsafe class RfReceiverQuery
{
    public static bool Supports(UsbDeviceInfo device) => device.Vid == 0x0416 && device.Pid == 0x8041
        && device.Path.StartsWith(@"\\?\usb#vid_0416&pid_8041#", StringComparison.OrdinalIgnoreCase);

    public static byte[] ReadList(UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Supports(device)) { throw new ArgumentException("Only the measured RF receiver is supported.", nameof(device)); }
        using var file = WinUsbNative.CreateFile(device.Path, WinUsbNative.GenericRead | WinUsbNative.GenericWrite,
            WinUsbNative.FileShareReadWrite, IntPtr.Zero, WinUsbNative.OpenExisting, WinUsbNative.FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "RF receiver open failed"); }
        if (!WinUsbNative.WinUsb_Initialize(file, out var usb)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        try
        {
            if (!WinUsbNative.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor) || descriptor.InterfaceNumber != 0)
            { throw new InvalidDataException("RF receiver interface pin failed."); }
            bool input = false, output = false;
            for (byte i = 0; i < descriptor.NumEndpoints; i++)
            {
                if (WinUsbNative.WinUsb_QueryPipe(usb, 0, i, out var p) && p.PipeType == UsbdPipeType.Bulk && p.MaximumPacketSize == 64)
                { input |= p.PipeId == 0x81; output |= p.PipeId == 1; }
            }
            if (!input || !output) { throw new InvalidDataException("RF receiver endpoint pin failed."); }
            var packet = new byte[64];
            SetTimeout(usb, 0x81, 10);
            for (var i = 0; i < 8; i++)
            {
                if (Read(usb, packet) == 0) { break; }
                if (i == 7) { throw new InvalidDataException("RF receiver drain did not settle."); }
            }
            SetTimeout(usb, 1, 500);
            SetTimeout(usb, 0x81, 50);
            var query = new byte[64]; query[0] = 16; query[1] = 1;
            fixed (byte* p = query)
            {
                if (!WinUsbNative.WinUsb_WritePipe(usb, 1, p, 64, out var sent, IntPtr.Zero) || sent != 64)
                { throw new IOException($"RF receiver query failed ({Marshal.GetLastPInvokeError()})."); }
            }
            var response = new List<byte>(448);
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 750 && response.Count < 512)
            {
                var n = Read(usb, packet);
                if (n == 0) { break; }
                response.AddRange(packet.AsSpan(0, n).ToArray());
                if (response.Count >= 4 && response[0] == 16 && response[1] <= 10 && response.Count >= 4 + response[1] * 42)
                { break; }
            }
            return response.ToArray();
        }
        finally { WinUsbNative.WinUsb_Free(usb); }
    }

    private static int Read(IntPtr usb, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            if (WinUsbNative.WinUsb_ReadPipe(usb, 0x81, p, (uint)buffer.Length, out var n, IntPtr.Zero)) { return (int)n; }
            var error = Marshal.GetLastPInvokeError();
            return error == WinUsbNative.ErrorSemTimeout ? 0 : throw new IOException($"RF receiver read failed ({error}).");
        }
    }

    private static void SetTimeout(IntPtr usb, byte pipe, uint milliseconds)
    {
        if (!WinUsbNative.WinUsb_SetPipePolicy(usb, pipe, WinUsbNative.PipeTransferTimeout, sizeof(uint), &milliseconds))
        { throw new IOException("Cannot bound RF receiver I/O."); }
    }
}
