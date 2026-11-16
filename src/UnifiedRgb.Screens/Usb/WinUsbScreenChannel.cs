using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Screens.Usb;

/// <summary>Opens screens through WinUSB. Used only by a worker whose family output gate is open.</summary>
public sealed class WinUsbScreenOpener : IScreenUsbOpener
{
    public IScreenUsbChannel Open(ScreenDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return WinUsbScreenChannel.Open(device);
    }
}

internal unsafe interface IWinUsbChannelOps
{
    bool SetPipeTimeout(IntPtr usb, byte pipeId, uint timeoutMs, out int err);
    bool WritePipe(IntPtr usb, byte pipeId, ReadOnlySpan<byte> data, out uint transferred, out int err);
    bool ReadPipe(IntPtr usb, byte pipeId, Span<byte> buffer, out uint transferred, out int err);
    bool AbortPipe(IntPtr usb, byte pipeId);
    bool ResetPipe(IntPtr usb, byte pipeId);
    void Close(IntPtr usb, SafeFileHandle? file);
}

/// <summary>
/// One WinUSB screen handle. Interface 0, alternate 0: the first bulk OUT and bulk IN pipes, which must equal the family's
/// source-proven endpoints when it has them (TL/H2: 0x01/0x81). Every transfer is bounded by PIPE_TRANSFER_TIMEOUT; transfers are
/// synchronous on the worker thread and <see cref="Abort"/> (WinUsb_AbortPipe) cancels them from another thread.
/// </summary>
internal sealed unsafe class WinUsbScreenChannel : IScreenUsbChannel, IScreenUsbDiagnostics, IScreenUsbPipeReset
{
    private readonly object _gate = new();
    private readonly SafeFileHandle? _file;
    private readonly IntPtr _usb;
    private readonly IWinUsbChannelOps _ops;
    private uint _outTimeout;
    private uint _inTimeout;
    private bool _disposed;
    private readonly bool _drainReplies;
    private ScreenReadCounts _lastRead;
    public int? DeviceSpeedCode { get; private init; }
    public ScreenReadCounts LastRead => _lastRead;
    public int ReplyQuietMs { private get; set; } = ScreenReplyFlow.DrainTimeoutMs;
    public bool? CorrelatedReplies { private get; set; }
    public bool ExactReplyPair { private get; set; }
    private readonly ScreenReplyCapability _replyCapability = new();
    private bool _expectsReply;
    private byte _expectedCommand;
    private uint _expectedTimestamp;

    public void ExpectResponse(byte command, uint timestamp)
    { _expectsReply = true; _expectedCommand = command; _expectedTimestamp = timestamp; }

    private WinUsbScreenChannel(SafeFileHandle? file, IntPtr usb, byte outEp, byte inEp, ScreenFamily family, IWinUsbChannelOps ops)
    {
        _file = file;
        _usb = usb;
        _ops = ops;
        OutEndpoint = outEp;
        InEndpoint = inEp;
        _drainReplies = family is ScreenFamily.H2 or ScreenFamily.Turzx;
    }

    public byte OutEndpoint { get; }

    public byte InEndpoint { get; }

    public static WinUsbScreenChannel Open(ScreenDeviceInfo device)
    {
        var file = ScreenUsbNative.CreateFile(device.Path, ScreenUsbNative.GenericRead | ScreenUsbNative.GenericWrite,
            ScreenUsbNative.FileShareReadWrite, IntPtr.Zero, ScreenUsbNative.OpenExisting, ScreenUsbNative.FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            var err = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new ScreenTransferException(ScreenTransferException.Classify(err), $"CreateFile failed ({err})", err);
        }

        if (!ScreenUsbNative.WinUsb_Initialize(file, out var usb))
        {
            var err = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new ScreenTransferException(ScreenTransferException.Classify(err), $"WinUsb_Initialize failed ({err})", err);
        }

        try
        {
            if (!ScreenUsbNative.WinUsb_QueryInterfaceSettings(usb, 0, out var desc))
            {
                var err = Marshal.GetLastPInvokeError();
                throw new ScreenTransferException(ScreenTransferException.Classify(err), $"WinUsb_QueryInterfaceSettings failed ({err})", err);
            }

            byte? outEp = null;
            byte? inEp = null;
            for (byte i = 0; i < desc.NumEndpoints; i++)
            {
                if (!ScreenUsbNative.WinUsb_QueryPipe(usb, 0, i, out var pipe) || pipe.PipeType != ScreenUsbNative.PipeTypeBulk)
                {
                    continue;
                }

                if ((pipe.PipeId & 0x80) != 0)
                {
                    inEp ??= pipe.PipeId;
                }
                else
                {
                    outEp ??= pipe.PipeId;
                }
            }

            var f = device.Family;
            if (desc.InterfaceNumber != 0 || outEp is null || inEp is null
                || (f.ExpectedOutEndpoint is { } eo && eo != outEp) || (f.ExpectedInEndpoint is { } ei && ei != inEp))
            {
                throw new ScreenTransferException(ScreenTransferError.EndpointMismatch,
                    $"interface {desc.InterfaceNumber}, bulk OUT {Hex(outEp)} / IN {Hex(inEp)} differ from the source-proven " +
                    $"{Hex(f.ExpectedOutEndpoint)} / {Hex(f.ExpectedInEndpoint)}");
            }

            uint speedLength = 1; byte speed = 0;
            var knownSpeed = ScreenUsbNative.WinUsb_QueryDeviceInformation(usb, 1, ref speedLength, &speed) && speedLength == 1;
            var channel = new WinUsbScreenChannel(file, usb, outEp.Value, inEp.Value, f.Family, NativeWinUsbOps.Instance)
            {
                DeviceSpeedCode = knownSpeed ? speed : null,
            };
            DrainOnOpen(channel);
            return channel;
        }
        catch
        {
            ScreenUsbNative.WinUsb_Free(usb);
            file.Dispose();
            throw;
        }
    }

    internal static WinUsbScreenChannel OpenForTest(ScreenFamily family, byte outEp, byte inEp, IWinUsbChannelOps ops, IntPtr usb = default)
    {
        var channel = new WinUsbScreenChannel(null, usb, outEp, inEp, family, ops);
        try
        {
            DrainOnOpen(channel);
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    private static void DrainOnOpen(WinUsbScreenChannel channel)
    {
        // Recover stale IN packets/ZLPs from an earlier interrupted owner BEFORE sending a new command (including TL-LCD).
        // Normal frame Read() keeps _drainReplies = false for TL-LCD so expected command ACKs are never discarded.
        ScreenReplyFlow.Drain(channel.ReadRaw, ScreenReplyFlow.OpenDrainLimit, ScreenReplyFlow.DrainTimeoutMs);
        channel._lastRead = default;
    }

    public void Write(ReadOnlySpan<byte> data, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetTimeout(OutEndpoint, ref _outTimeout, timeoutMs);
        if (!_ops.WritePipe(_usb, OutEndpoint, data, out var n, out var err))
        {
            throw new ScreenTransferException(ScreenTransferException.Classify(err), $"WinUsb_WritePipe failed ({err})", err);
        }

        if (n != data.Length)
        {
            throw new ScreenTransferException(ScreenTransferError.ShortWrite, $"short write: {n} of {data.Length} bytes");
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        _lastRead = default;
        if (!_drainReplies) return ReadRaw(buffer, timeoutMs).Length;
        if (_expectsReply && (CorrelatedReplies ?? _replyCapability.Verified))
        {
            _lastRead = _lastRead with { Correlated = true };
            return ExactReplyPair ? ScreenReplyFlow.ReadExactPair(ReadRaw, buffer, timeoutMs)
                : ScreenReplyFlow.ReadCorrelated(ReadRaw, buffer, timeoutMs);
        }
        var result = ScreenReplyFlow.ReadAndDrain(ReadRaw, buffer, timeoutMs, ReplyQuietMs);
        _replyCapability.Observe(_expectsReply, _lastRead);
        return result;
    }

    private ScreenPipeRead ReadRaw(Span<byte> buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastRead = _lastRead with { Reads = _lastRead.Reads + 1 };
        var started = Stopwatch.GetTimestamp();
        SetTimeout(InEndpoint, ref _inTimeout, timeoutMs);
        if (_ops.ReadPipe(_usb, InEndpoint, buffer, out var n, out var err))
        {
            if (n == 0) _lastRead = _lastRead with { ZeroLength = _lastRead.ZeroLength + 1 };
            var matched = _expectsReply && ScreenReplyFlow.MatchesImageReply(buffer[..(int)n], _expectedCommand, _expectedTimestamp);
            if (matched) _lastRead = _lastRead with { Matched = _lastRead.Matched + 1 };
            RecordReadTime(started);
            return new ScreenPipeRead((int)n, TimedOut: false, MatchesRequest: matched);
        }

        if (err == ScreenUsbNative.ErrorSemTimeout) _lastRead = _lastRead with { Timeouts = _lastRead.Timeouts + 1 };
        RecordReadTime(started);
        return err == ScreenUsbNative.ErrorSemTimeout
            ? new ScreenPipeRead(0, TimedOut: true)
            : throw new ScreenTransferException(ScreenTransferException.Classify(err), $"WinUsb_ReadPipe failed ({err})", err);
    }

    private void RecordReadTime(long started)
    {
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _lastRead = _lastRead.Reads == 1 ? _lastRead with { FirstMs = ms } : _lastRead with { DrainMs = _lastRead.DrainMs + ms };
    }

    public bool TryResetPipes()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var inAborted = _ops.AbortPipe(_usb, InEndpoint);
            var inReset = _ops.ResetPipe(_usb, InEndpoint);
            var outAborted = _ops.AbortPipe(_usb, OutEndpoint);
            var outReset = _ops.ResetPipe(_usb, OutEndpoint);
            _inTimeout = 0;
            _outTimeout = 0;
            return inAborted && inReset && outAborted && outReset;
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _ops.AbortPipe(_usb, OutEndpoint);
                _ops.AbortPipe(_usb, InEndpoint);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ops.AbortPipe(_usb, OutEndpoint);
            _ops.AbortPipe(_usb, InEndpoint);
            _ops.Close(_usb, _file);
        }
    }

    private void SetTimeout(byte pipe, ref uint current, int timeoutMs)
    {
        var t = (uint)Math.Clamp(timeoutMs, 1, 10_000);
        if (t == current)
        {
            return;
        }

        if (_ops.SetPipeTimeout(_usb, pipe, t, out var err))
        {
            current = t;
            return;
        }

        throw new ScreenTransferException(ScreenTransferException.Classify(err), $"WinUsb_SetPipePolicy failed ({err})", err);
    }

    private static string Hex(byte? b) => b is { } v ? "0x" + v.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : "none";

    private sealed class NativeWinUsbOps : IWinUsbChannelOps
    {
        public static readonly NativeWinUsbOps Instance = new();

        public bool SetPipeTimeout(IntPtr usb, byte pipeId, uint timeoutMs, out int err)
        {
            if (ScreenUsbNative.WinUsb_SetPipePolicy(usb, pipeId, ScreenUsbNative.PipeTransferTimeout, sizeof(uint), &timeoutMs))
            {
                err = 0;
                return true;
            }

            err = Marshal.GetLastPInvokeError();
            return false;
        }

        public bool WritePipe(IntPtr usb, byte pipeId, ReadOnlySpan<byte> data, out uint transferred, out int err)
        {
            fixed (byte* p = data)
            {
                if (ScreenUsbNative.WinUsb_WritePipe(usb, pipeId, p, (uint)data.Length, out transferred, IntPtr.Zero))
                {
                    err = 0;
                    return true;
                }
            }

            err = Marshal.GetLastPInvokeError();
            return false;
        }

        public bool ReadPipe(IntPtr usb, byte pipeId, Span<byte> buffer, out uint transferred, out int err)
        {
            fixed (byte* p = buffer)
            {
                if (ScreenUsbNative.WinUsb_ReadPipe(usb, pipeId, p, (uint)buffer.Length, out transferred, IntPtr.Zero))
                {
                    err = 0;
                    return true;
                }
            }

            err = Marshal.GetLastPInvokeError();
            return false;
        }

        public bool AbortPipe(IntPtr usb, byte pipeId) => ScreenUsbNative.WinUsb_AbortPipe(usb, pipeId);

        public bool ResetPipe(IntPtr usb, byte pipeId) => ScreenUsbNative.WinUsb_ResetPipe(usb, pipeId);

        public void Close(IntPtr usb, SafeFileHandle? file)
        {
            ScreenUsbNative.WinUsb_Free(usb);
            file?.Dispose();
        }
    }
}
