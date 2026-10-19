using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Screens.Usb;

public enum ScreenTransferError
{
    /// <summary>The transfer did not complete within its bound (WinUSB PIPE_TRANSFER_TIMEOUT).</summary>
    Timeout,

    /// <summary>The device accepted fewer bytes than the packet holds.</summary>
    ShortWrite,

    /// <summary>The device is gone (unplugged, re-enumerated).</summary>
    Disconnected,

    /// <summary>Another program holds the device (access denied / sharing violation): treated as Blocked, never forced.</summary>
    AccessDenied,

    /// <summary>The pipe was aborted (stop/cancel).</summary>
    Aborted,

    /// <summary>Interface/endpoint identity differs from the primary source: the device is never written.</summary>
    EndpointMismatch,

    /// <summary>Reply draining hit its finite bound; the channel must close before another write.</summary>
    ReplyBacklog,

    Failed,

    /// <summary>A correlated image reply was incomplete or stale. Disable the fast path and reopen with conservative draining.</summary>
    ResponseMismatch,
}

public sealed class ScreenTransferException : IOException
{
    public ScreenTransferException()
    {
    }

    public ScreenTransferException(string message)
        : base(message)
    {
    }

    public ScreenTransferException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ScreenTransferException(ScreenTransferError kind, string message, int win32Error = 0)
        : base(message)
    {
        Kind = kind;
        Win32Error = win32Error;
    }

    public ScreenTransferError Kind { get; } = ScreenTransferError.Failed;

    public int Win32Error { get; }

    /// <summary>Maps a WinUSB/kernel32 error code to a transfer error kind.</summary>
    public static ScreenTransferError Classify(int win32Error) => win32Error switch
    {
        ScreenUsbNative.ErrorSemTimeout => ScreenTransferError.Timeout,
        ScreenUsbNative.ErrorOperationAborted => ScreenTransferError.Aborted,
        ScreenUsbNative.ErrorAccessDenied or ScreenUsbNative.ErrorSharingViolation => ScreenTransferError.AccessDenied,
        ScreenUsbNative.ErrorFileNotFound or ScreenUsbNative.ErrorPathNotFound or ScreenUsbNative.ErrorBadCommand
            or ScreenUsbNative.ErrorDeviceNotConnected or ScreenUsbNative.ErrorNoSuchDevice or ScreenUsbNative.ErrorGenFailure
            => ScreenTransferError.Disconnected,
        _ => ScreenTransferError.Failed,
    };
}

/// <summary>An open screen: one bulk OUT and one bulk IN pipe. Only the owning worker transfers; <see cref="Abort"/> may come from any thread.</summary>
public interface IScreenUsbChannel : IDisposable
{
    byte OutEndpoint { get; }

    byte InEndpoint { get; }

    /// <summary>Writes every byte within <paramref name="timeoutMs"/> or throws <see cref="ScreenTransferException"/>.</summary>
    void Write(ReadOnlySpan<byte> data, int timeoutMs);

    /// <summary>Reads at most one response; 0 when nothing arrived within <paramref name="timeoutMs"/>.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Aborts any pending transfer (cancellation/stop). Idempotent; a no-op after dispose.</summary>
    void Abort();
}

internal readonly record struct ScreenReadCounts(int Reads, int Timeouts, int ZeroLength, double FirstMs = 0, double DrainMs = 0,
    int Matched = 0, bool Correlated = false);

internal interface IScreenUsbDiagnostics
{
    int? DeviceSpeedCode { get; }
    ScreenReadCounts LastRead { get; }
    int ReplyQuietMs { set; }
    bool? CorrelatedReplies { set; }
    void ExpectResponse(byte command, uint timestamp);
}

public interface IScreenUsbOpener
{
    /// <summary>Opens the device; throws <see cref="ScreenTransferException"/> (AccessDenied, Disconnected, EndpointMismatch, ...).</summary>
    IScreenUsbChannel Open(ScreenDeviceInfo device);
}

/// <summary>A present, strictly matched screen interface.</summary>
public sealed record ScreenDeviceInfo(string Id, ScreenFamilyInfo Family, string Path, string Instance)
{
    /// <summary>
    /// Strict parse of a WinUSB interface path "\\?\usb#vid_1cbe&amp;pid_XXXX#&lt;instance&gt;#{guid}": exact VID 1CBE and one of
    /// the three PIDs, a whole (non-composite, no "&amp;mi_") device and a plain instance id. Anything else → null.
    /// </summary>
    public static ScreenDeviceInfo? FromPath(string? path)
    {
        if (path is null || path.Length > 512)
        {
            return null;
        }

        var m = PathPattern.Match(path.ToLowerInvariant());
        if (!m.Success
            || !ushort.TryParse(m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            || !ushort.TryParse(m.Groups[2].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid)
            || ScreenFamilyInfo.FromUsb(vid, pid) is not { } family)
        {
            return null;
        }

        var instance = m.Groups[3].Value;
        return ScreenIds.TryParse(string.Create(CultureInfo.InvariantCulture, $"screen:{family.Token}:{instance}"), out _, out _)
            ? new ScreenDeviceInfo(ScreenIds.Create(family, instance), family, path, instance)
            : null;
    }

    private static readonly Regex PathPattern = new(
        "^\\\\\\\\\\?\\\\usb#vid_([0-9a-f]{4})&pid_([0-9a-f]{4})#([^#\\\\/]{1,64})#\\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\}$",
        RegexOptions.CultureInvariant);
}

public interface IScreenDeviceEnumerator
{
    IReadOnlyList<ScreenDeviceInfo> Enumerate();
}

/// <summary>
/// Read-only SetupAPI enumeration of present interfaces of the screens' WinUSB interface class (found in the registry for all
/// five screens on 2026-10-18), filtered by <see cref="ScreenDeviceInfo.FromPath"/>. No handle is opened.
/// </summary>
public sealed unsafe class SetupApiScreenEnumerator : IScreenDeviceEnumerator
{
    public static readonly Guid ScreenInterfaceClass = new("88BAE032-5A81-49f0-BC3D-A4FF138216D6");

    public IReadOnlyList<ScreenDeviceInfo> Enumerate()
    {
        var cls = ScreenInterfaceClass;
        var set = ScreenUsbNative.SetupDiGetClassDevs(cls, null, IntPtr.Zero, ScreenUsbNative.DigcfPresent | ScreenUsbNative.DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            throw new ScreenTransferException(ScreenTransferError.Failed, $"SetupDiGetClassDevs failed ({Marshal.GetLastPInvokeError()})");
        }

        var list = new List<ScreenDeviceInfo>();
        var buf = new byte[1024];
        try
        {
            for (uint i = 0; i < 256; i++)
            {
                var data = new ScreenSpDeviceInterfaceData { Size = (uint)sizeof(ScreenSpDeviceInterfaceData) };
                if (!ScreenUsbNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, cls, i, ref data))
                {
                    break; // ERROR_NO_MORE_ITEMS
                }

                ScreenUsbNative.SetupDiGetDeviceInterfaceDetail(set, ref data, null, 0, out var required, IntPtr.Zero);
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
                    if (!ScreenUsbNative.SetupDiGetDeviceInterfaceDetail(set, ref data, b, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    path = new string((char*)(b + 4));
                }

                if (ScreenDeviceInfo.FromPath(path) is { } info && list.TrueForAll(d => d.Id != info.Id))
                {
                    list.Add(info);
                }
            }
        }
        finally
        {
            ScreenUsbNative.SetupDiDestroyDeviceInfoList(set);
        }

        return list;
    }
}
