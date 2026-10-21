using System.Diagnostics;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>One worker's existing channel, one access unit in flight. No independent file player or local playback clock.</summary>
internal sealed class TurzxVideoSession(IScreenUsbChannel channel, ScreenTimestamp stamp, Func<bool> stopped)
{
    private bool _entered;
    private readonly byte[] _reply = new byte[512];
    public int LastQueueDepth { get; private set; }
    public long AccessUnits { get; private set; }
    public long OverlayWrites { get; private set; }
    public long BytesWritten { get; private set; }
    public double PacketMs { get; private set; }
    public double WriteMs { get; private set; }
    public double ReadMs { get; private set; }

    public void Begin(byte[] transparentPng)
    {
        if (_entered) return;
        _entered = true; // cleanup is required even if a later initialization command fails
        Control(TurzxVideoProtocol.StopPlayback);
        Control(TurzxVideoProtocol.Prepare);
        Control(TurzxVideoProtocol.Start);
        SendOverlay(transparentPng);
        Control(TurzxVideoProtocol.Cadence);
    }

    public void SendFrame(byte[] accessUnit)
    {
        if (!_entered) throw new InvalidOperationException("Video session is not initialized.");
        var types = H264AnnexB.Types(accessUnit);
        if (accessUnit.Length is 0 or > MfH264IntraEncoder.MaxEncodedBytes || !types.Contains(7) || !types.Contains(8)
            || !types.Contains(5) || types.Any(t => t is >= 1 and <= 4)) throw new InvalidDataException("Only independent SPS/PPS/IDR pictures may be streamed.");
        for (var offset = 0; offset < accessUnit.Length; offset += TurzxVideoProtocol.MaxChunkBytes)
        {
            if (stopped()) throw new ScreenTransferException(ScreenTransferError.Aborted, "Native video was stopped.");
            var timestamp = stamp.Next();
            var packetStart = Stopwatch.GetTimestamp();
            var packet = TurzxVideoProtocol.Data(accessUnit.AsSpan(offset, Math.Min(TurzxVideoProtocol.MaxChunkBytes, accessUnit.Length - offset)), timestamp);
            PacketMs += Stopwatch.GetElapsedTime(packetStart).TotalMilliseconds;
            var response = Send(packet, TurzxVideoProtocol.DataCommand, timestamp);
            if (response < 9) throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "Video reply lacks its buffer status.");
            LastQueueDepth = _reply[8];
            // Actual .NET sender waits when depth >3, until <=2. The Python example has the opposite comparison.
            var busy = LastQueueDepth > 3;
            for (var i = 0; busy; i++)
            {
                if (i >= 8 || stopped()) throw new ScreenTransferException(ScreenTransferError.ReplyBacklog, "Native video buffer did not drain within its bounded wait.");
                Thread.Sleep(25);
                var count = Control(TurzxVideoProtocol.QueueStatus);
                if (count < 9) throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "Video status reply is too short.");
                LastQueueDepth = _reply[8];
                busy = LastQueueDepth > 2;
            }
        }
        AccessUnits++;
    }

    public void SendOverlay(byte[] png)
    {
        var timestamp = stamp.Next();
        var start = Stopwatch.GetTimestamp();
        var packet = ScreenProtocol.BuildImagePacket(ScreenFamily.Turzx, ScreenProtocol.PushPngCommand, png, timestamp);
        PacketMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Send(packet, ScreenProtocol.PushPngCommand, timestamp);
        OverlayWrites++;
    }

    private int Control(Func<uint, byte[]> packet)
    {
        var timestamp = stamp.Next(); var start = Stopwatch.GetTimestamp(); var bytes = packet(timestamp);
        PacketMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var command = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, bytes)[0];
        return Send(bytes, command, timestamp);
    }

    private int Send(byte[] packet, byte command, uint timestamp)
    {
        if (channel is IScreenUsbDiagnostics diagnostics)
        {
            diagnostics.ReplyQuietMs = 2;
            diagnostics.CorrelatedReplies = false; // native controls are never auto-negotiated from image acknowledgements
            diagnostics.ExpectResponse(command, timestamp);
        }
        var start = Stopwatch.GetTimestamp(); channel.Write(packet, 2000);
        WriteMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds; BytesWritten += packet.Length;
        start = Stopwatch.GetTimestamp(); var count = channel.Read(_reply, 2000);
        ReadMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!ScreenReplyFlow.MatchesImageReply(_reply.AsSpan(0, count), command, timestamp))
            throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, $"Native command {command} did not return this request's acknowledgement.");
        return count;
    }

    /// <summary>Try every volatile stop even when an earlier one fails; the normal image path reopens and restores CURRENT content.</summary>
    public void Stop()
    {
        if (!_entered) return;
        _entered = false;
        Exception? error = null;
        foreach (var control in new Func<uint, byte[]>[] { TurzxVideoProtocol.EndStream, TurzxVideoProtocol.StopPlayback, TurzxVideoProtocol.StopImagePlayback })
        {
            try { Control(control); }
            catch (IOException ex) { error ??= ex; }
        }
        if (error is not null) throw new IOException("Native video stop acknowledgement failed; reopen the normal image channel.", error);
    }
}
