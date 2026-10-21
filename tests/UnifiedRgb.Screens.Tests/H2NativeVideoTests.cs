using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class H2NativeVideoTests
{
    [Fact]
    public void H2_native_write_failure_is_not_replayed_after_partial_transport_acceptance()
    {
        using var channel=new FakeChannel { Reply=Reply, FailWrite=n=>n==3?new IOException("simulated accepted packet timeout"):null };
        var session=new H2VideoSession(channel,new ScreenTimestamp(ScreenFamily.H2,()=>DateTime.UtcNow,()=>DateTime.Now),()=>false,30);
        session.Begin([]);
        var picture=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","h264-unspecified-colour.h264"));
        FluentActions.Invoking(()=>session.SendFrame(picture)).Should().Throw<IOException>();
        channel.WriteAttempts.Should().Be(3,"an uncertain native packet must never be resent");
        session.AccessUnits.Should().Be(0);
        session.Stop();channel.WriteAttempts.Should().Be(4,"a bounded StopPlay is attempted separately");
    }
    private static byte[] Reply(byte[] packet)
    {
        var h=ScreenProtocol.DecryptHeader(ScreenFamily.H2,packet.AsSpan(0,512));
        var r=new byte[512];r[0]=h[0];r[1]=200;h.AsSpan(4,4).CopyTo(r.AsSpan(2));return r;
    }
    private static int Command(byte[] p)=>ScreenProtocol.DecryptHeader(ScreenFamily.H2,p.AsSpan(0,512))[0];
    private static byte[][] Data(Rig rig)=>rig.Opener.Last.Writes.Where(p=>Command(p)==121).Select(p=>p.AsSpan(512).ToArray()).ToArray();

    [Fact]
    public void H2_native_header_has_exact_live_stream_fields_and_does_not_expand_the_image_API()
    {
        const uint stamp=0x01020304;
        var data=H2VideoProtocol.Data([1,2,3,4],stamp);
        var h=ScreenProtocol.DecryptHeader(ScreenFamily.H2,data.AsSpan(0,512));
        h.Take(14).Should().Equal(121,0,0x1a,0x6d,4,3,2,1,0,0,0,4,0,0);
        data.AsSpan(512).ToArray().Should().Equal(1,2,3,4);
        Command(H2VideoProtocol.Stop(stamp)).Should().Be(123);
        Command(H2VideoProtocol.QueueStatus(stamp)).Should().Be(122);
        var rate=ScreenProtocol.DecryptHeader(ScreenFamily.H2,H2VideoProtocol.FrameRate(stamp,30));
        (rate[0],rate[8]).Should().Be(((byte)15,(byte)30));
        ScreenProtocol.AllowedCommands(ScreenFamily.H2).Should().Equal(101,102);
        FluentActions.Invoking(()=>H2VideoProtocol.Data([],stamp)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(()=>H2VideoProtocol.Data(new byte[202753],stamp)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(()=>H2VideoProtocol.FrameRate(stamp,61)).Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class Clock : IScreenTimeline
    {
        public volatile ScreenTimelineSample Next=new(0,"device-clock");
        public ScreenTimelineSample Position(string timelineId)=>Next;
    }

    [MediaFoundationFact]
    public async Task H2_bounded_native_trial_follows_shared_clock_and_restores_JPEG_without_settings_writes()
    {
        await using var rig=new Rig(Rig.AllOutput,Devices.H2);rig.Opener.Configure=c=>c.Reply=Reply;
        var clock=new Clock();rig.Manager.Timeline=clock;
        var gif=Images.Gif(2,2,GifFrame.Fill(0,0,2,2,1,delayCs:10),GifFrame.Fill(0,0,2,2,2,delayCs:10));
        var asset=await rig.Manager.UploadScreenAssetAsync(new("generic H2 clock",Convert.ToBase64String(gif)));
        var scene=new ScreenSceneDto([new(Devices.H2.Id,asset.Id,Overlay:"none",Fps:30,TimelineId:"same-shared-clock")]);
        await rig.Manager.SetScreenSceneAsync(scene);rig.Manager.Start();
        await Wait.UntilAsync(async()=>(await rig.ScreenAsync(Devices.H2.Id)).State==ScreenStates.Running,"H2 normal transaction committed");
        var scenePath=Path.Combine(rig.DataDirectory,"screens","scene.json");var saved=File.ReadAllBytes(scenePath);
        var first=rig.Opener.Last.Writes.Count;
        var trial=await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id,60000,30));
        await Wait.UntilAsync(()=>Data(rig).Length>0,"H2 first native picture");var red=Data(rig)[^1];
        clock.Next=new(150,"device-clock");await Wait.UntilAsync(()=>!Data(rig)[^1].SequenceEqual(red),"H2 green from shared clock");
        clock.Next=new(0,"device-clock");await Wait.UntilAsync(()=>Data(rig)[^1].SequenceEqual(red),"H2 loop reuses independent red IDR");
        var facts=(await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo!;
        facts.SourceClock.Should().Be("device-clock");facts.OverlayWrites.Should().Be(0);
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(()=>Command(rig.Opener.Last.Writes.Last())==101,"H2 JPEG restored");
        File.ReadAllBytes(scenePath).Should().Equal(saved);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        rig.Opener.Last.Writes.Skip(first).Select(Command).Should().NotContain([11,13,14,40,41,42,43,103,125,150,247,250,251,252,253]);
    }

    [MediaFoundationFact]
    public async Task H2_native_with_live_telemetry_reports_the_actual_uncached_source_frame()
    {
        await using var rig=new Rig(Rig.AllOutput,Devices.H2);rig.Opener.Configure=c=>c.Reply=Reply;
        var clock=new Clock();rig.Manager.Timeline=clock;
        var gif=Images.Gif(2,2,GifFrame.Fill(0,0,2,2,1,delayCs:10),GifFrame.Fill(0,0,2,2,2,delayCs:10));
        var asset=await rig.Manager.UploadScreenAssetAsync(new("generic H2 telemetry clock",Convert.ToBase64String(gif)));
        await rig.Manager.SetScreenSceneAsync(new([new(Devices.H2.Id,asset.Id,Overlay:"compact",Fps:30,TimelineId:"same-shared-clock")]));
        rig.Manager.Start();
        await Wait.UntilAsync(async()=>(await rig.ScreenAsync(Devices.H2.Id)).State==ScreenStates.Running,"initial H2 telemetry JPEG committed");
        var trial=await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id,60000,30));
        await Wait.UntilAsync(()=>Data(rig).Length>0,"H2 telemetry native picture");
        clock.Next=new(150,"device-clock");
        await Wait.UntilAsync(async()=>(await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo?.SourceFrameIndex==1,
            "uncached H2 native diagnostics follows the actual source frame");
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
    }

    [MediaFoundationFact]
    public async Task H2_preview_decodes_the_whole_native_picture_including_telemetry()
    {
        await using var rig=new Rig(o=>{Rig.AllOutput(o);o.NativeH264VerifiedScreenIds=[Devices.H2.Id];},Devices.H2);
        await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.H2.Id,0,ScreenEncodingPolicies.NativeH264IntraV1));
        var preview=await rig.Manager.PreviewScreenAsync(new(Devices.H2.Id,Overlay:"compact",BackgroundColor:"#182838"));
        preview.Note.Should().Contain("complete picture including telemetry").And.NotContain("lossless live PNG");
        var decoded=await Sta.RunAsync(()=>WpfCodec.DecodePng(Convert.FromBase64String(preview.PngBase64),480L*480));
        decoded.Pixels.Where((_,i)=>i%4!=3).Should().Contain(v=>v>180,"live overlay text must be encoded into the native image");
        rig.Opener.Opens.Should().Be(0,"preview never opens hardware");
    }

    [MediaFoundationFact]
    public async Task H2_trial_expiry_returns_to_current_JPEG_and_never_rewrites_persistence()
    {
        await using var rig=new Rig(Rig.AllOutput,Devices.H2);rig.Opener.Configure=c=>c.Reply=Reply;
        var scene=new ScreenSceneDto([new(Devices.H2.Id,BackgroundColor:"#164A72",Overlay:"none",Fps:30)]);
        await rig.Manager.SetScreenSceneAsync(scene);rig.Manager.Start();
        await Wait.UntilAsync(async()=>(await rig.ScreenAsync(Devices.H2.Id)).State==ScreenStates.Running,"initial H2 JPEG committed");
        var file=Path.Combine(rig.DataDirectory,"screens","scene.json");var bytes=File.ReadAllBytes(file);
        await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id,1000,30));
        await Wait.UntilAsync(()=>Data(rig).Length>0,"H2 native started");
        await Wait.UntilAsync(async()=> (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo is null
            && Command(rig.Opener.Last.Writes.Last())==101,"monotonic expiry restored JPEG",5000);
        File.ReadAllBytes(file).Should().Equal(bytes);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
    }
}
