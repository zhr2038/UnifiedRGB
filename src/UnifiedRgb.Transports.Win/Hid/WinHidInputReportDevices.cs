using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Transports.Hid;

internal static unsafe partial class HidInputCapsNative
{
    /// <summary>HIDP_REPORT_TYPE HidP_Input.</summary>
    public const int HidpInput = 0;

    /// <summary>sizeof(HIDP_BUTTON_CAPS) == sizeof(HIDP_VALUE_CAPS) == 72 on x64 (hidpi.h); ReportID is the byte at offset 2 of both.</summary>
    public const int CapsRecordSize = 72;
    public const int ReportIdOffset = 2;

    /// <summary>Upper bound for input button/value cap records of one vendor collection; more is treated as drift.</summary>
    public const int MaximumCapsRecords = 64;

    [LibraryImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int HidP_GetButtonCaps(int reportType, byte* buttonCaps, ref ushort buttonCapsLength, IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int HidP_GetValueCaps(int reportType, byte* valueCaps, ref ushort valueCapsLength, IntPtr preparsed);

    /// <summary>Report ids of <paramref name="count"/> returned cap records (pure; synthetic records in tests).</summary>
    public static void AddReportIds(ReadOnlySpan<byte> records, int count, ISet<byte> ids)
    {
        if (count < 0 || count > MaximumCapsRecords || records.Length < count * CapsRecordSize)
            throw new InvalidDataException("HID input-cap array has an unexpected size.");
        for (var i = 0; i < count; i++) ids.Add(records[i * CapsRecordSize + ReportIdOffset]);
    }
}

/// <summary>
/// Win32 vendor HID input collections. Enumeration is the existing access-0 caps scan of <see cref="WinHidReportDevices"/>.
/// <see cref="OpenReadOnly"/> refuses MI_00 and non-vendor usage pages before any handle exists, then opens the path with
/// <c>GENERIC_READ</c> only, <c>FILE_SHARE_READ | FILE_SHARE_WRITE</c> (other readers and the OS stack keep working) and
/// overlapped I/O; the stream is <see cref="FileAccess.Read"/>. No output, feature or HID driver setter is called.
/// </summary>
public sealed unsafe class WinHidInputReportDevices : IHidInputReportDevices
{
    /// <summary>GENERIC_READ only; never GENERIC_WRITE.</summary>
    internal const uint DesiredAccess = WinUsbNative.GenericRead;
    internal const uint ShareMode = WinUsbNative.FileShareReadWrite;
    internal const uint Flags = WinUsbNative.FileFlagOverlapped;

    private readonly WinHidReportDevices _metadata;
    private readonly Func<string, uint, uint, uint, SafeFileHandle> _createFile;

    public WinHidInputReportDevices(IUsbEnumerator? enumerator = null)
        : this(enumerator, null)
    {
    }

    /// <summary>Test seam: <paramref name="createFile"/> receives (path, access, share, flags) instead of kernel32 CreateFileW.</summary>
    internal WinHidInputReportDevices(IUsbEnumerator? enumerator, Func<string, uint, uint, uint, SafeFileHandle>? createFile)
    {
        _metadata = new WinHidReportDevices(enumerator);
        _createFile = createFile ?? ((path, access, share, flags) =>
            WinUsbNative.CreateFile(path, access, share, IntPtr.Zero, WinUsbNative.OpenExisting, flags, IntPtr.Zero));
    }

    public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => _metadata.Enumerate(vid, pid);

    public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.Interface is null or 0 || collection.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MI_00 or an unnumbered interface is never opened for input.");
        if (collection.Error is not null || !HidVendorInputRule.IsVendorUsagePage(collection.UsagePage))
            throw new InvalidOperationException("Only vendor-defined HID collections are opened for input; ordinary keyboard, mouse and consumer collections never are.");

        var handle = _createFile(collection.Path, DesiredAccess, ShareMode, Flags);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Read-only CreateFile of the vendor HID input collection failed.");
        }

        try
        {
            return new Handle(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Attributes, caps and input report ids of an opened handle; nothing is read from or sent to the device.</summary>
    internal static HidInputDescriptor Describe(SafeFileHandle handle)
    {
        var attributes = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
        if (!HidNative.HidD_GetAttributes(handle, ref attributes))
            throw new InvalidDataException("HID attributes are unavailable on the opened input handle.");
        if (!HidCapsNative.HidD_GetPreparsedData(handle, out var preparsed))
            throw new InvalidDataException("HID preparsed data is unavailable on the opened input handle.");
        try
        {
            var caps = stackalloc ushort[HidCapsNative.CapsSize / 2];
            new Span<ushort>(caps, HidCapsNative.CapsSize / 2).Clear();
            if (HidCapsNative.HidP_GetCaps(preparsed, caps) != HidCapsNative.HidpStatusSuccess)
                throw new InvalidDataException("HID caps are unavailable on the opened input handle.");
            var ids = new SortedSet<byte>();
            // HIDP_CAPS USHORT index 23 = NumberInputButtonCaps, 24 = NumberInputValueCaps.
            ReadIds(preparsed, caps[23], buttons: true, ids);
            ReadIds(preparsed, caps[24], buttons: false, ids);
            return new HidInputDescriptor(attributes.VendorId, attributes.ProductId, caps[1], caps[0], caps[2], caps[3], caps[4], [.. ids]);
        }
        finally
        {
            HidCapsNative.HidD_FreePreparsedData(preparsed);
        }
    }

    private static void ReadIds(IntPtr preparsed, ushort advertised, bool buttons, SortedSet<byte> ids)
    {
        if (advertised == 0) return;
        if (advertised > HidInputCapsNative.MaximumCapsRecords) throw new InvalidDataException("HID input-cap array is larger than any verified vendor collection.");
        var records = new byte[advertised * HidInputCapsNative.CapsRecordSize];
        var count = advertised;
        int status;
        fixed (byte* p = records)
        {
            status = buttons
                ? HidInputCapsNative.HidP_GetButtonCaps(HidInputCapsNative.HidpInput, p, ref count, preparsed)
                : HidInputCapsNative.HidP_GetValueCaps(HidInputCapsNative.HidpInput, p, ref count, preparsed);
        }

        if (status != HidCapsNative.HidpStatusSuccess || count > advertised) throw new InvalidDataException("HID input-cap array changed while it was read.");
        HidInputCapsNative.AddReportIds(records, count, ids);
    }

    internal sealed class Handle : IHidInputReportHandle
    {
        private readonly SafeFileHandle _handle;
        private readonly FileStream _stream;

        public Handle(SafeFileHandle handle)
        {
            _handle = handle;
            // Unbuffered: every read is one complete input report. FileAccess.Read: this stream cannot write.
            _stream = new FileStream(handle, FileAccess.Read, 0, isAsync: true);
        }

        internal bool CanWrite => _stream.CanWrite;

        public HidInputDescriptor Describe() => WinHidInputReportDevices.Describe(_handle);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _stream.ReadAsync(buffer, ct);

        public void Dispose()
        {
            _stream.Dispose();
            _handle.Dispose();
        }
    }
}
