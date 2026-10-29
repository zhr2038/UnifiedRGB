using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Transports.Hid;

/// <summary>
/// One HID top-level collection: its interface path, ids, the USB interface number parsed from <c>MI_xx</c> (null when the
/// device has a single interface) and the caps of its report descriptor. <paramref name="Error"/> says why the caps could
/// not be read; the lengths are then zero.
/// </summary>
public sealed record HidCollectionInfo(
    string Path,
    ushort Vid,
    ushort Pid,
    int? Interface,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    string? Error = null)
{
    /// <summary>The <c>MI_xx</c> interface number of a device path (case-insensitive), or null.</summary>
    public static int? InterfaceOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var i = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 5 <= path.Length
            && int.TryParse(path.AsSpan(i + 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mi) ? mi : null;
    }

    public override string ToString() => Error is not null
        ? $"{Vid:X4}:{Pid:X4} MI_{Interface?.ToString("X2", CultureInfo.InvariantCulture) ?? "--"} caps unavailable ({Error})"
        : string.Create(CultureInfo.InvariantCulture,
            $"{Vid:X4}:{Pid:X4} MI_{Interface?.ToString("X2", CultureInfo.InvariantCulture) ?? "--"} UP:{UsagePage:X4} U:{Usage:X4} in {InputReportLength} out {OutputReportLength} feature {FeatureReportLength}");
}

/// <summary>
/// Which collection a native adapter may open: VID/PID, the vendor usage page/usage and the exact input/output report
/// length. Interface 0 (MI_00) is never selected: on the ASUS board it is the WinUSB "AURA LED Controller", not the lighting
/// collection (NATIVE design §4.1).
/// </summary>
public sealed record HidCollectionRule(ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, int ReportLength)
{
    /// <summary>Null when <paramref name="c"/> is the collection this rule selects, else why not.</summary>
    public string? Reject(HidCollectionInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Vid != Vid || c.Pid != Pid)
        {
            return $"device {c.Vid:X4}:{c.Pid:X4} is not {Vid:X4}:{Pid:X4}";
        }

        if (c.Interface == 0 || c.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase))
        {
            return "MI_00 is never opened";
        }

        if (c.Error is not null)
        {
            return $"caps unavailable: {c.Error}";
        }

        if (c.UsagePage != UsagePage || c.Usage != Usage)
        {
            return $"usage {c.UsagePage:X4}/{c.Usage:X4} is not {UsagePage:X4}/{Usage:X4}";
        }

        if (c.OutputReportLength != ReportLength || c.InputReportLength != ReportLength)
        {
            return $"report lengths in {c.InputReportLength} / out {c.OutputReportLength} are not {ReportLength}";
        }

        return null;
    }

    /// <summary>The collections this rule selects, in enumeration order; <paramref name="rejected"/> lists the others with the reason.</summary>
    public IReadOnlyList<HidCollectionInfo> Select(IEnumerable<HidCollectionInfo> collections, out IReadOnlyList<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(collections);
        var selected = new List<HidCollectionInfo>();
        var why = new List<string>();
        foreach (var c in collections)
        {
            if (Reject(c) is { } reason)
            {
                why.Add($"{c}: {reason}");
            }
            else
            {
                selected.Add(c);
            }
        }

        rejected = why;
        return selected;
    }
}

/// <summary>An open HID collection: output reports written, input reports read (overlapped I/O).</summary>
public interface IHidReportChannel : IDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>HID collections of the system (Win32) or a fake (tests).</summary>
public interface IHidReportDevices
{
    /// <summary>Collections of <paramref name="vid"/>:<paramref name="pid"/> with their caps. Read-only: caps come from access-0 handles; no report is sent or read.</summary>
    IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid);

    /// <summary>Opens <paramref name="collection"/> for reports (read/write, shared). Implementations refuse MI_00.</summary>
    IHidReportChannel Open(HidCollectionInfo collection);
}

/// <summary>
/// Generic HID report transport (NATIVE design §4.2), the innermost link of a native HID chain such as Aura's
/// <c>AuraReportFirewall → FrameValidator{65,65} → FrameBudget</c>. Only a collection the <see cref="HidCollectionRule"/>
/// admits is opened; every output report must have the collection's output length; every write and every read is bounded by
/// the timeout (<see cref="TimeoutException"/>, row status ERROR_TIMEOUT), and a read returns the size the device actually
/// delivered (the caller validates it). Lian Li's <see cref="HidTransport"/> is separate.
/// </summary>
public sealed class HidReportTransport : ITransport, IDisposable
{
    private readonly IHidReportChannel _channel;
    private readonly int _timeoutMs;

    private HidReportTransport(IHidReportChannel channel, HidCollectionInfo collection, int timeoutMs)
    {
        _channel = channel;
        Collection = collection;
        _timeoutMs = timeoutMs;
    }

    public HidCollectionInfo Collection { get; }

    /// <summary>Opens <paramref name="collection"/> after <paramref name="rule"/> admitted it; otherwise nothing is opened.</summary>
    public static HidReportTransport Open(IHidReportDevices devices, HidCollectionInfo collection, HidCollectionRule rule, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, 1);
        if (rule.Reject(collection) is { } why)
        {
            throw new InvalidOperationException($"HID collection {collection} refused: {why}");
        }

        return new HidReportTransport(devices.Open(collection), collection, timeoutMs);
    }

    /// <summary>One output report; <see cref="TimeoutException"/> when the write has not completed within the bound.</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (frame.Length != Collection.OutputReportLength)
        {
            throw new InvalidOperationException($"HID output report of {frame.Length} bytes; the collection takes {Collection.OutputReportLength}");
        }

        // a stalled write must not hold a leased step (or the writer) forever: same bound as reads
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeoutMs);
        try
        {
            await _channel.WriteAsync(frame, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"HID output report not written within {_timeoutMs} ms");
        }
    }

    /// <summary>One input report into a zeroed <paramref name="buffer"/>; <see cref="TimeoutException"/> after the bounded wait.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length < Collection.InputReportLength)
        {
            throw new ArgumentException($"read buffer of {buffer.Length} bytes; input reports are {Collection.InputReportLength}", nameof(buffer));
        }

        buffer.Span.Clear();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeoutMs);
        try
        {
            return await _channel.ReadAsync(buffer[..Collection.InputReportLength], bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no HID input report within {_timeoutMs} ms");
        }
    }

    public void Dispose() => _channel.Dispose();
}

internal static unsafe partial class HidCapsNative
{
    /// <summary>HIDP_STATUS_SUCCESS.</summary>
    public const int HidpStatusSuccess = 0x00110000;

    /// <summary>sizeof(HIDP_CAPS): 32 USHORTs (Usage, UsagePage, Input/Output/FeatureReportByteLength, Reserved[17], 10 counts).</summary>
    public const int CapsSize = 64;

    [LibraryImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FreePreparsedData(IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int HidP_GetCaps(IntPtr preparsed, ushort* caps);
}

/// <summary>
/// Win32 HID collections: SetupAPI enumeration of the HID interface class, caps through access-0 handles
/// (<c>HidD_GetPreparsedData</c>/<c>HidP_GetCaps</c>; no report traffic), and read/write overlapped handles for
/// <see cref="Open"/>. MI_00 is never opened.
/// </summary>
public sealed unsafe class WinHidReportDevices : IHidReportDevices
{
    private readonly IUsbEnumerator _enumerator;

    public WinHidReportDevices(IUsbEnumerator? enumerator = null) => _enumerator = enumerator ?? new SetupApiEnumerator();

    public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid)
    {
        var result = new List<HidCollectionInfo>();
        foreach (var d in _enumerator.Enumerate(HidTransport.InterfaceClass))
        {
            if (d.Vid == vid && d.Pid == pid)
            {
                result.Add(Caps(d));
            }
        }

        return result;
    }

    public IHidReportChannel Open(HidCollectionInfo collection)
        => OpenCore(collection, exclusiveWriter: false);

    /// <summary>Owns the vendor collection's write access. Other readers are allowed; another writer fails before I/O.</summary>
    public static IHidReportChannel OpenExclusiveWriter(HidCollectionInfo collection)
        => OpenCore(collection, exclusiveWriter: true);

    private static Channel OpenCore(HidCollectionInfo collection, bool exclusiveWriter)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.Interface == 0 || collection.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MI_00 is never opened");
        }

        var handle = WinUsbNative.CreateFile(collection.Path, WinUsbNative.GenericRead | WinUsbNative.GenericWrite, exclusiveWriter ? 1u : WinUsbNative.FileShareReadWrite,
            IntPtr.Zero, WinUsbNative.OpenExisting, WinUsbNative.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateFile({collection.Path}) failed");
        }

        var attr = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
        if (!HidNative.HidD_GetAttributes(handle, ref attr) || attr.VendorId != collection.Vid || attr.ProductId != collection.Pid)
        {
            handle.Dispose();
            throw new InvalidOperationException($"{collection.Path} is not the expected HID device {collection.Vid:X4}:{collection.Pid:X4}");
        }

        return new Channel(new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true));
    }

    private static HidCollectionInfo Caps(UsbDeviceInfo d)
    {
        var mi = HidCollectionInfo.InterfaceOf(d.Path);
        HidCollectionInfo Failed(string why) => new(d.Path, d.Vid, d.Pid, mi, 0, 0, 0, 0, 0, why);

        // access 0: attributes and caps only, the device is not opened for reports
        using var handle = WinUsbNative.CreateFile(d.Path, 0, WinUsbNative.FileShareReadWrite, IntPtr.Zero, WinUsbNative.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"CreateFile error {Marshal.GetLastPInvokeError()}"));
        }

        if (!HidCapsNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"HidD_GetPreparsedData error {Marshal.GetLastPInvokeError()}"));
        }

        try
        {
            var raw = stackalloc ushort[HidCapsNative.CapsSize / 2];
            new Span<ushort>(raw, HidCapsNative.CapsSize / 2).Clear();
            var status = HidCapsNative.HidP_GetCaps(preparsed, raw);
            if (status != HidCapsNative.HidpStatusSuccess)
            {
                return Failed(string.Create(CultureInfo.InvariantCulture, $"HidP_GetCaps NTSTATUS 0x{status:X8}"));
            }

            return new HidCollectionInfo(d.Path, d.Vid, d.Pid, mi, raw[1], raw[0], raw[2], raw[3], raw[4]);
        }
        finally
        {
            HidCapsNative.HidD_FreePreparsedData(preparsed);
        }
    }

    private sealed class Channel(FileStream stream) : IHidReportChannel
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) => stream.WriteAsync(report, ct);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => stream.ReadAsync(buffer, ct);

        public void Dispose() => stream.Dispose();
    }
}
