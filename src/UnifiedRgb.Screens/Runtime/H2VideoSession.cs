using System.Diagnostics;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>One existing worker/channel, one all-IDR picture at a time. No autonomous loop, USB reset or partial-write resend.</summary>
internal sealed class H2VideoSession(IScreenUsbChannel channel, ScreenTimestamp stamp, Func<bool> stopped, int fps) : INativeVideoSession
{
    private bool _entered;
    private readonly byte[] _reply=new byte[512];
    public long AccessUnits { get; private set; }
    public long OverlayWrites => 0; // H2 telemetry is composited into the picture; no unverified independent overlay plane.
    public int LastQueueDepth { get; private set; }
    public long BytesWritten { get; private set; }
    public double PacketMs { get; private set; }
    public double WriteMs { get; private set; }
    public double ReadMs { get; private set; }
    public void Begin(byte[] transparentPng)
    {
        if(_entered)return;_entered=true;
        Control(H2VideoProtocol.Stop);
        Thread.Sleep(20); // original H2 controller lets StopPlay settle before changing stream cadence
        Control(t=>H2VideoProtocol.FrameRate(t,fps));
    }
    public void SendFrame(byte[] accessUnit)
    {
        if(!_entered)throw new InvalidOperationException("H2 video is not initialized.");
        var types=H264AnnexB.Types(accessUnit);
        if(accessUnit.Length is 0 or > MfH264IntraEncoder.MaxEncodedBytes||!types.Contains(7)||!types.Contains(8)||!types.Contains(5)||types.Any(t=>t is >=1 and <=4))
            throw new InvalidDataException("H2 live video needs independent SPS/PPS/IDR pictures.");
        for(var offset=0;offset<accessUnit.Length;offset+=H2VideoProtocol.MaxChunkBytes)
        {
            if(stopped())throw new ScreenTransferException(ScreenTransferError.Aborted,"H2 video was stopped.");
            var timestamp=stamp.Next();var start=Stopwatch.GetTimestamp();
            var packet=H2VideoProtocol.Data(accessUnit.AsSpan(offset,Math.Min(H2VideoProtocol.MaxChunkBytes,accessUnit.Length-offset)),timestamp);
            PacketMs+=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var count=Send(packet,H2VideoProtocol.DataCommand,timestamp);
            if(count<9)throw new ScreenTransferException(ScreenTransferError.ResponseMismatch,"H2 video reply lacks buffer feedback.");
            LastQueueDepth=_reply[8];var busy=LastQueueDepth>3;
            for(var i=0;busy;i++)
            {
                if(i>=8||stopped())throw new ScreenTransferException(ScreenTransferError.ReplyBacklog,"H2 video buffer exceeded the bounded drain wait.");
                Thread.Sleep(50);count=Control(H2VideoProtocol.QueueStatus);
                if(count<9)throw new ScreenTransferException(ScreenTransferError.ResponseMismatch,"H2 buffer reply is truncated.");
                LastQueueDepth=_reply[8];busy=LastQueueDepth>2;
            }
        }
        AccessUnits++;
    }
    public void SendOverlay(byte[] png) => throw new InvalidOperationException("H2 live information must be composited before encoding.");
    private int Control(Func<uint,byte[]> build)
    {
        var timestamp=stamp.Next();var start=Stopwatch.GetTimestamp();var packet=build(timestamp);PacketMs+=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var command=ScreenProtocol.DecryptHeader(ScreenFamily.H2,packet)[0];return Send(packet,command,timestamp);
    }
    private int Send(byte[] packet,byte command,uint timestamp)
    {
        if(channel is IScreenUsbDiagnostics diagnostics){diagnostics.ReplyQuietMs=2;diagnostics.CorrelatedReplies=false;diagnostics.ExpectResponse(command,timestamp);}
        var start=Stopwatch.GetTimestamp();channel.Write(packet,2000);WriteMs+=Stopwatch.GetElapsedTime(start).TotalMilliseconds;BytesWritten+=packet.Length;
        start=Stopwatch.GetTimestamp();var count=channel.Read(_reply,2000);ReadMs+=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if(!ScreenReplyFlow.MatchesImageReply(_reply.AsSpan(0,count),command,timestamp))
            throw new ScreenTransferException(ScreenTransferError.ResponseMismatch,$"H2 native command {command} reply mismatch ({Convert.ToHexString(_reply.AsSpan(0,Math.Min(count,16)))}).");
        return count;
    }
    public void Stop(){if(!_entered)return;_entered=false;Control(H2VideoProtocol.Stop);Thread.Sleep(20);}
}
