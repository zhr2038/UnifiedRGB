using System.Diagnostics;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>One worker's existing channel, one access unit in flight. No independent file player or local playback clock.</summary>
internal sealed class TurzxVideoSession(IScreenUsbChannel channel, ScreenTimestamp stamp, Func<bool> stopped,
    bool allowCorrelatedReplies = true) : INativeVideoSession
{
    private bool _entered;
    private bool _faulted;
    private uint? _lastStamp;
    private readonly ScreenReplyCapability _dataReplyCapability = new();
    private NativeVideoWork _work;
    private readonly byte[] _reply = new byte[512];
    public int LastQueueDepth { get; private set; }
    public long AccessUnits { get; private set; }
    public long OverlayWrites { get; private set; }
    public long BytesWritten { get; private set; }
    public double PacketMs { get; private set; }
    public double WriteMs { get; private set; }
    public double ReadMs { get; private set; }
    public NativeVideoWork Work => _work;

    public void Begin(byte[] transparentPng)
    {
        if (_faulted) throw new InvalidOperationException("The failed native channel must be reopened.");
        if (_entered) return;
        _dataReplyCapability.Observe(false, default);
        _entered = true; // cleanup is required even if a later initialization command fails
        Control(TurzxVideoProtocol.StopPlayback);
        Control(TurzxVideoProtocol.Prepare);
        Control(TurzxVideoProtocol.Start);
        SendOverlay(transparentPng);
        Control(TurzxVideoProtocol.Cadence);
    }

    public void SendFrame(byte[] accessUnit)
    {
        if (!_entered || _faulted) throw new InvalidOperationException("Video session is not initialized or its channel must be reopened.");
        var types = H264AnnexB.Types(accessUnit);
        if (accessUnit.Length is 0 or > MfH264IntraEncoder.MaxEncodedBytes || !types.Contains(7) || !types.Contains(8)
            || !types.Contains(5) || types.Any(t => t is >= 1 and <= 4)) throw new InvalidDataException("Only independent SPS/PPS/IDR pictures may be streamed.");
        for (var offset = 0; offset < accessUnit.Length; offset += TurzxVideoProtocol.MaxChunkBytes)
        {
            if (stopped()) throw new ScreenTransferException(ScreenTransferError.Aborted, "Native video was stopped.");
            var timestamp = NextStamp();
            var packetStart = Stopwatch.GetTimestamp();
            var packet = TurzxVideoProtocol.Data(accessUnit.AsSpan(offset, Math.Min(TurzxVideoProtocol.MaxChunkBytes, accessUnit.Length - offset)), timestamp);
            PacketMs += Stopwatch.GetElapsedTime(packetStart).TotalMilliseconds;
            var response = Send(packet, TurzxVideoProtocol.DataCommand, timestamp);
            if (response < 9) { _faulted = true; throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "Video reply lacks its buffer status."); }
            LastQueueDepth = _reply[8];
            // Actual .NET sender waits when depth >3, until <=2. The Python example has the opposite comparison.
            var busy = LastQueueDepth > 3;
            for (var i = 0; busy; i++)
            {
                if (i >= 8 || stopped()) throw new ScreenTransferException(ScreenTransferError.ReplyBacklog, "Native video buffer did not drain within its bounded wait.");
                var sleepStart = Stopwatch.GetTimestamp();
                Thread.Sleep(25);
                _work = _work with { QueueSleep = _work.QueueSleep + Stopwatch.GetElapsedTime(sleepStart).TotalMilliseconds };
                var count = Control(TurzxVideoProtocol.QueueStatus);
                if (count < 9) { _faulted = true; throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "Video status reply is too short."); }
                LastQueueDepth = _reply[8];
                busy = LastQueueDepth > 2;
            }
        }
        AccessUnits++;
    }

    public void SendOverlay(byte[] png)
    {
        var timestamp = NextStamp();
        var start = Stopwatch.GetTimestamp();
        var packet = ScreenProtocol.BuildImagePacket(ScreenFamily.Turzx, ScreenProtocol.PushPngCommand, png, timestamp);
        PacketMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Send(packet, ScreenProtocol.PushPngCommand, timestamp);
        OverlayWrites++;
    }

    /// <summary>
    /// One explicit strict queue-status query (122) through the existing control path, for the optional D1 lab measurement after a
    /// worker-issued overlay PNG. Returns the device's REPORTED buffer-status byte (not a picture, decode or presentation fact) and
    /// never assigns <see cref="LastQueueDepth"/>; its cost is control work. A short status faults the session like any other
    /// unverified reply, so no cleanup or later command follows on that channel.
    /// </summary>
    public int QueryQueueStatus()
    {
        if (!_entered || _faulted) throw new InvalidOperationException("Video session is not initialized or its channel must be reopened.");
        var count = Control(TurzxVideoProtocol.QueueStatus);
        if (count < 9) { _faulted = true; throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "Video status reply is too short."); }
        return _reply[8];
    }

    private int Control(Func<uint, byte[]> packet)
    {
        var timestamp = NextStamp(); var start = Stopwatch.GetTimestamp(); var bytes = packet(timestamp);
        PacketMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var command = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, bytes)[0];
        return Send(bytes, command, timestamp);
    }

    private int Send(byte[] packet, byte command, uint timestamp)
    {
        if (_faulted) throw new IOException("Native video channel must be reopened before another command.");
        var diagnostics = channel as IScreenUsbDiagnostics;
        var data = command == TurzxVideoProtocol.DataCommand;
        var fast = data && allowCorrelatedReplies && _dataReplyCapability.Verified && diagnostics is not null;
        if (diagnostics is not null)
        {
            diagnostics.ReplyQuietMs = 2;
            diagnostics.CorrelatedReplies = fast; // controls/overlay never inherit image or data capabilities
            diagnostics.ExactReplyPair = fast;
            diagnostics.ExpectResponse(command, timestamp);
        }
        try
        {
            var start = Stopwatch.GetTimestamp(); channel.Write(packet, 2000);
            var write = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            WriteMs += write; BytesWritten += packet.Length;
            start = Stopwatch.GetTimestamp(); var count = channel.Read(_reply, 2000);
            var read = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            ReadMs += read;
            if (!ScreenReplyFlow.MatchesImageReply(_reply.AsSpan(0, count), command, timestamp))
                throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, $"Native command {command} did not return this request's acknowledgement.");
            var facts = diagnostics?.LastRead ?? default;
            if (fast && (count != 512 || facts is not { Reads: 2, Matched: 1, ZeroLength: 1, Timeouts: 0 }))
                throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "Native data reply shape changed after negotiation; reopen conservatively.");
            if (data && !fast) _dataReplyCapability.Observe(count == 512, facts);
            _work = _work with
            {
                DataWrite = _work.DataWrite + (data ? write : 0), DataRead = _work.DataRead + (data ? read : 0),
                OverlayWrite = _work.OverlayWrite + (command == ScreenProtocol.PushPngCommand ? write : 0),
                OverlayRead = _work.OverlayRead + (command == ScreenProtocol.PushPngCommand ? read : 0),
                ControlWrite = _work.ControlWrite + (!data && command != ScreenProtocol.PushPngCommand ? write : 0),
                ControlRead = _work.ControlRead + (!data && command != ScreenProtocol.PushPngCommand ? read : 0),
                Reads = _work.Reads + facts.Reads, Timeouts = _work.Timeouts + facts.Timeouts,
                Zlp = _work.Zlp + facts.ZeroLength, Matched = _work.Matched + facts.Matched,
                Correlated = _work.Correlated + (fast ? 1 : 0), FirstRead = _work.FirstRead + facts.FirstMs,
                Drain = _work.Drain + facts.DrainMs,
            };
            return count;
        }
        catch (IOException ex)
        {
            _faulted = true; // no stop/resend on a channel with an unverified reply suffix
            if (ex is ScreenTransferException) throw;
            throw new ScreenTransferException("Native video I/O failed; reopen the channel before another command.", ex);
        }
    }

    private uint NextStamp()
    {
        var value = stamp.Next();
        if (_lastStamp is { } last && unchecked((int)(value - last)) <= 0) value = unchecked(last + 1);
        _lastStamp = value;
        return value; // request correlation token only; the shared animation clock is untouched
    }

    /// <summary>Stop a trusted session; an uncertain reply requires closing and reopening, without any further write.</summary>
    public void Stop()
    {
        if (!_entered) return;
        _entered = false;
        if (_faulted) return;
        Exception? error = null;
        foreach (var control in new Func<uint, byte[]>[] { TurzxVideoProtocol.EndStream, TurzxVideoProtocol.StopPlayback, TurzxVideoProtocol.StopImagePlayback })
        {
            try { Control(control); }
            catch (IOException ex) { error ??= ex; }
        }
        if (error is not null) throw new IOException("Native video stop acknowledgement failed; reopen the normal image channel.", error);
    }
}
