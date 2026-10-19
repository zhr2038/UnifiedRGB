using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Transports.WinUsb;

/// <summary>
/// WinUSB bulk/interrupt transport for Lian Li receivers and wired controllers. ARCHITECTURE §3: the opcode firewall is
/// checked inside <see cref="SendAsync"/> on every frame, in addition to the OpcodeFirewall decorator in the chain, so
/// even a mis-built chain cannot put a denied opcode on the wire. Sends and reads are synchronous on the caller's thread
/// (the DeviceWriter thread), with a pipe timeout so a stalled device cannot hang the writer.
/// </summary>
public sealed unsafe class WinUsbTransport : ITransport, IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly IntPtr _usb;
    private readonly byte _outPipe;
    private readonly byte _inPipe;
    private readonly OpcodeLayout _layout;
    private readonly RelaySequence _sequence = new();
    private readonly object _sendGate = new();
    private bool _disposed;

    private WinUsbTransport(SafeFileHandle file, IntPtr usb, byte outPipe, byte inPipe, OpcodeLayout layout)
    {
        _file = file;
        _usb = usb;
        _outPipe = outPipe;
        _inPipe = inPipe;
        _layout = layout;
    }

    public int OutPacketSize { get; private init; }

    public static WinUsbTransport Open(string devicePath, OpcodeLayout layout, uint timeoutMs = 500) => Open(devicePath, layout, 0, 0, timeoutMs);

    /// <summary>Opens with pinned endpoints: the device must expose exactly these OUT/IN pipe ids (0 = first found).</summary>
    public static WinUsbTransport Open(string devicePath, OpcodeLayout layout, byte requiredOutPipe, byte requiredInPipe, uint timeoutMs = 500)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var file = WinUsbNative.CreateFile(devicePath, WinUsbNative.GenericRead | WinUsbNative.GenericWrite, WinUsbNative.FileShareReadWrite,
            IntPtr.Zero, WinUsbNative.OpenExisting, WinUsbNative.FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateFile({devicePath}) failed");
        }

        if (!WinUsbNative.WinUsb_Initialize(file, out var usb))
        {
            var err = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new Win32Exception(err, "WinUsb_Initialize failed");
        }

        try
        {
            if (!WinUsbNative.WinUsb_QueryInterfaceSettings(usb, 0, out var desc))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "WinUsb_QueryInterfaceSettings failed");
            }

            byte outPipe = 0, inPipe = 0;
            ushort outSize = 0;
            for (byte i = 0; i < desc.NumEndpoints; i++)
            {
                if (!WinUsbNative.WinUsb_QueryPipe(usb, 0, i, out var pipe)
                    || pipe.PipeType is not (UsbdPipeType.Bulk or UsbdPipeType.Interrupt))
                {
                    continue;
                }

                if ((pipe.PipeId & 0x80) != 0)
                {
                    if (requiredInPipe == 0 ? inPipe == 0 : pipe.PipeId == requiredInPipe)
                    {
                        inPipe = pipe.PipeId;
                    }
                }
                else if (requiredOutPipe == 0 ? outPipe == 0 : pipe.PipeId == requiredOutPipe)
                {
                    outPipe = pipe.PipeId;
                    outSize = pipe.MaximumPacketSize;
                }
            }

            if (outPipe == 0)
            {
                throw new InvalidOperationException($"{devicePath} has no bulk/interrupt OUT pipe");
            }

            if (requiredInPipe != 0 && inPipe == 0)
            {
                throw new InvalidOperationException($"{devicePath} has no IN pipe 0x{requiredInPipe:X2}");
            }

            foreach (var p in new[] { outPipe, inPipe })
            {
                if (p != 0)
                {
                    var t = timeoutMs;
                    WinUsbNative.WinUsb_SetPipePolicy(usb, p, WinUsbNative.PipeTransferTimeout, sizeof(uint), &t);
                }
            }

            return new WinUsbTransport(file, usb, outPipe, inPipe, layout) { OutPacketSize = outSize };
        }
        catch
        {
            WinUsbNative.WinUsb_Free(usb);
            file.Dispose();
            throw;
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        _sequence.Check(frame.Span, _layout);
        ct.ThrowIfCancellationRequested();
        lock (_sendGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fixed (byte* p = frame.Span)
            {
                if (!WinUsbNative.WinUsb_WritePipe(_usb, _outPipe, p, (uint)frame.Length, out var n, IntPtr.Zero) || n != frame.Length)
                {
                    throw new IOException($"WinUsb_WritePipe failed ({Marshal.GetLastPInvokeError()})");
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_inPipe == 0)
        {
            return ValueTask.FromResult(0);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        fixed (byte* p = buffer.Span)
        {
            if (!WinUsbNative.WinUsb_ReadPipe(_usb, _inPipe, p, (uint)buffer.Length, out var n, IntPtr.Zero))
            {
                var err = Marshal.GetLastPInvokeError();
                return err == WinUsbNative.ErrorSemTimeout ? ValueTask.FromResult(0) : throw new IOException($"WinUsb_ReadPipe failed ({err})");
            }

            return ValueTask.FromResult((int)n);
        }
    }

    public void Dispose()
    {
        lock (_sendGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WinUsbNative.WinUsb_Free(_usb);
            _file.Dispose();
        }
    }
}
