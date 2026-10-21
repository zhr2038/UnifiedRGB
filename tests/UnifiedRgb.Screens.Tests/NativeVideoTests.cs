using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

internal sealed class MediaFoundationFactAttribute : FactAttribute
{
    public MediaFoundationFactAttribute()
    {
        using var encoder = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{6ca50344-051a-4ded-9779-a43305165e35}\InprocServer32");
        using var decoder = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{62ce7e72-4c71-4d20-b15d-452831a87d9d}\InprocServer32");
        if (encoder is null || decoder is null || !File.Exists(Path.Combine(Environment.SystemDirectory, "mfreadwrite.dll")))
            Skip = "Windows Media Foundation H.264 components are not installed on this host.";
    }
}

public sealed class NativeVideoTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-30.mp4");

    [MediaFoundationFact]
    public async Task Mp4_rejects_unhandled_content_orientation_and_non_square_pixels_before_publishing()
    {
        foreach (var (file, reason) in new[] { ("h264-rotated.mp4", "*rotation metadata*"), ("h264-anamorphic.mp4", "*square pixels*") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
            await using var rig = new Rig();
            await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new("Unsupported geometry", Convert.ToBase64String(bytes))))
                .Should().ThrowAsync<ArgumentException>("file {0}", file).WithMessage(reason);
            (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty();
        }
    }

    [MediaFoundationFact]
    public async Task Mp4_import_preserves_sixty_frames_two_seconds_colours_and_survives_store_reload_and_cleanup()
    {
        var bytes = File.ReadAllBytes(Fixture);
        var result = await Sta.RunAsync(() => Mp4Importer.Import(bytes, ImportLimits.Default));
        (result.Format, result.Width, result.Height, result.FramePngs.Count, result.DurationMs).Should().Be(("mp4", 64, 64, 60, 2000));
        result.DelaysMs.Should().OnlyContain(d => d == 33 || d == 34, "MP4 uses presentation times, never GIF's short-delay=100ms rule");
        await using var rig = new Rig();
        var asset = await rig.Manager.UploadScreenAssetAsync(new("generic MP4", Convert.ToBase64String(bytes)));
        asset.Kind.Should().Be("video"); asset.FrameCount.Should().Be(60); asset.DurationMs.Should().Be(2000);
        (await rig.Manager.UploadScreenAssetAsync(new("idempotent", Convert.ToBase64String(bytes)))).Id.Should().Be(asset.Id);
        var store = new AssetStore(Path.Combine(rig.DataDirectory,"screens","assets"), 1L<<30,256,NullLogger.Instance);
        var restored = store.Find(asset.Id)!;
        restored.Format.Should().Be("mp4"); restored.FrameAt(1999).Should().Be(59); restored.FrameAt(2000).Should().Be(0);
        store.ReadFrame(restored,59).Should().Equal(result.FramePngs[59]);
        await rig.Manager.DeleteScreenAssetAsync(asset.Id);
        Directory.Exists(Path.Combine(store.Root,asset.Id)).Should().BeFalse();
    }

    [MediaFoundationFact]
    public async Task Mp4_work_and_output_budgets_and_cancellation_fail_without_publishing_an_asset()
    {
        var bytes = File.ReadAllBytes(Fixture);
        await FluentActions.Awaiting(() => Sta.RunAsync(() => Mp4Importer.Import(bytes, ImportLimits.Default with { MaxNormalizedBytes=100 })))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*budget*");
        await FluentActions.Awaiting(() => Sta.RunAsync(() => Mp4Importer.Import(bytes, ImportLimits.Default with { MaxDurationMs=100 })))
            .Should().ThrowAsync<ArgumentException>();
        using var cts = new CancellationTokenSource(); await cts.CancelAsync();
        await FluentActions.Awaiting(() => Sta.RunAsync(() => Mp4Importer.Import(bytes, ImportLimits.Default,cts.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Movie_staging_cleanup_does_not_take_ownership_of_undeclared_four_digit_frames()
    {
        var root=Path.Combine(Path.GetTempPath(),"urgb-screens-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store=new AssetStore(root,1L<<30,10,NullLogger.Instance);
            var dir=Path.Combine(root,"~tmp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir,"f1000.png"),"unowned");
            store.List();
            File.Exists(Path.Combine(dir,"f1000.png")).Should().BeTrue("without video metadata, old staging owns at most the legacy 300 frames");
        }
        finally{Directory.Delete(root,recursive:true);}
    }

    [MediaFoundationFact]
    public async Task Every_native_access_unit_is_independent_IDR_and_preview_decodes_the_actual_bytes()
    {
        await Sta.RunAsync(() =>
        {
            using var encoder=new MfH264IntraEncoder(64,64);
            foreach(var colour in new[]{(R:250,G:25,B:20),(R:20,G:250,B:25),(R:25,G:20,B:250)})
            {
                var frame=PixelFrame.Solid(64,64,(byte)colour.R,(byte)colour.G,(byte)colour.B);
                var bytes=encoder.Encode(frame);var types=H264AnnexB.Types(bytes);
                types.Should().Contain([7,8,5]);types.Should().NotContain(t=>t>=1 && t<=4);
                var p=MfH264IntraEncoder.Decode(bytes,64,64).At(32,32);
                Math.Abs(p.R-colour.R).Should().BeLessThan(5);Math.Abs(p.G-colour.G).Should().BeLessThan(5);Math.Abs(p.B-colour.B).Should().BeLessThan(5);
            }
            return true;
        });
    }

    [Fact]
    public void Typed_video_protocol_preserves_image_whitelist_and_refuses_storage_or_arbitrary_data_sizes()
    {
        ScreenProtocol.AllowedCommands(ScreenFamily.Turzx).Should().Equal(102);
        var packets=new[]{TurzxVideoProtocol.StopPlayback(8),TurzxVideoProtocol.Prepare(8),TurzxVideoProtocol.Start(8),TurzxVideoProtocol.Cadence(8),TurzxVideoProtocol.QueueStatus(8),TurzxVideoProtocol.EndStream(8),TurzxVideoProtocol.StopImagePlayback(8)};
        packets.Select(p=>ScreenProtocol.DecryptHeader(ScreenFamily.Turzx,p)[0]).Should().Equal(111,112,41,15,122,123,114);
        ScreenProtocol.DecryptHeader(ScreenFamily.Turzx,packets[3])[8].Should().Be(60);
        FluentActions.Invoking(()=>TurzxVideoProtocol.Data(new byte[202753],0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(()=>TurzxVideoProtocol.Data([],0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [MediaFoundationFact]
    public async Task Native_encoder_output_has_one_owner_across_repeated_encoding_and_forced_finalization()
    {
        await Sta.RunAsync(() =>
        {
            using var encoder=new MfH264IntraEncoder(64,64);
            for(var i=0;i<240;i++)
            {
                var bytes=encoder.Encode(PixelFrame.Solid(64,64,(byte)i,25,30));
                H264AnnexB.Types(bytes).Should().Contain(5);
                if(i%20==0){GC.Collect();GC.WaitForPendingFinalizers();}
            }
            return true;
        });
    }

    private sealed class Timeline : IScreenTimeline
    {
        public volatile ScreenTimelineSample Next=new(0,"device-clock");
        public ScreenTimelineSample Position(string timelineId)=>Next;
    }

    private static byte[] Reply(byte[] packet)
    {
        var header=ScreenProtocol.DecryptHeader(ScreenFamily.Turzx,packet.AsSpan(0,512));
        var reply=new byte[512];reply[0]=header[0];reply[1]=200;header.AsSpan(4,4).CopyTo(reply.AsSpan(2));return reply;
    }

    [MediaFoundationFact]
    public async Task Native_trial_uses_the_shared_clock_keeps_overlay_and_restores_current_image_path_without_persistence()
    {
        await using var rig=new Rig(Rig.AllOutput,Devices.Turzx);
        rig.Opener.Configure=c=>c.Reply=Reply;
        var timeline=new Timeline();rig.Manager.Timeline=timeline;
        var gif=Images.Gif(2,2,GifFrame.Fill(0,0,2,2,1,delayCs:10),GifFrame.Fill(0,0,2,2,2,delayCs:10));
        var asset=await rig.Manager.UploadScreenAssetAsync(new("native clock fixture",Convert.ToBase64String(gif)));
        var scene=new ScreenSceneDto([new(Devices.Turzx.Id,asset.Id,Overlay:"dashboard",Fps:15,TimelineId:"shared-clock-test")]);
        await rig.Manager.SetScreenSceneAsync(scene);rig.Manager.Start();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Turzx.Id)).State == ScreenStates.Running,
            "normal image transaction committed before starting a native trial");
        var scenePath=Path.Combine(rig.DataDirectory,"screens","scene.json");var saved=File.ReadAllBytes(scenePath);
        var trial=await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id,60000,30));
        byte[]? Red=null;
        await Wait.UntilAsync(()=>Data(rig).Length>0,"native IDR");
        Red=Data(rig)[^1];
        timeline.Next=new(150,"device-clock");
        await Wait.UntilAsync(()=>!Data(rig)[^1].SequenceEqual(Red),"shared clock chooses green");
        timeline.Next=new(0,"device-clock");
        await Wait.UntilAsync(()=>Data(rig)[^1].SequenceEqual(Red),"loop picks the cached red IDR without an independent video epoch");
        rig.Opener.Last.Writes.Count(p=>Command(p)==102).Should().BeGreaterThan(1,"PNG overlay coexists with native frames");
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(()=>Command(rig.Opener.Last.Writes.Last())==102,"normal opaque image path restored");
        File.ReadAllBytes(scenePath).Should().Equal(saved);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        rig.Opener.Last.Writes.Select(Command).Should().NotContain([13,14,38,39,125,251,252,253]);
    }

    private static int Command(byte[] packet)=>ScreenProtocol.DecryptHeader(ScreenFamily.Turzx,packet.AsSpan(0,512))[0];
    private static byte[][] Data(Rig rig)=>rig.Opener.Last.Writes.Where(p=>Command(p)==121).Select(p=>p.AsSpan(512).ToArray()).ToArray();

    [Fact]
    public async Task Unverified_native_policy_and_trials_on_unopened_or_non_TURZX_screens_are_refused_without_IO()
    {
        await using var rig=new Rig(Rig.AllOutput,Devices.Turzx);
        await FluentActions.Awaiting(()=>rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Turzx.Id,0,ScreenEncodingPolicies.NativeH264IntraV1)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*verified*");
        await FluentActions.Awaiting(()=>rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Tl1.Id))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(()=>rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(()=>rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id))).Should().ThrowAsync<InvalidOperationException>();
        rig.Opener.Opens.Should().Be(0);
    }
}
