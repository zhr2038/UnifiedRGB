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
    private readonly Stream _stream;
    private readonly OpcodeLayout _layout;
    private readonly RelaySequence _sequence = new();
    private readonly TimeSpan _timeout;
    private int _faulted;

    internal HidTransport(Stream stream, OpcodeLayout layout, TimeSpan? timeout = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _timeout = timeout ?? TimeSpan.FromSeconds(1);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _faulted) != 0, this);
        _sequence.Check(frame.Span, _layout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        try { await _stream.WriteAsync(frame, bounded.Token).AsTask().WaitAsync(_timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            Fault();
            throw new TimeoutException($"HID output did not complete within {_timeout.TotalMilliseconds:0} ms; the uncertain transfer is not resent.", ex);
        }
        catch (OperationCanceledException) { Fault(); throw; }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _faulted) != 0, this);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        try { return await _stream.ReadAsync(buffer, bounded.Token).AsTask().WaitAsync(_timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { Fault(); throw new TimeoutException($"HID input did not complete within {_timeout.TotalMilliseconds:0} ms.", ex); }
        catch (OperationCanceledException) { Fault(); throw; }
    }

    private void Fault()
    {
        Interlocked.Exchange(ref _faulted, 1);
        try { _stream.Dispose(); }
        catch (IOException) { /* the original transfer failure stays authoritative */ }
    }

    public void Dispose() { Interlocked.Exchange(ref _faulted, 1); _stream.Dispose(); }
}
