using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class NativeVideoVariantTests
{
    [Fact]
    public void Legacy_wire_defaults_and_binary_constructors_keep_the_original_encoder()
    {
        var options = ContractsJson.Configure(new JsonSerializerOptions());
        var request = JsonSerializer.Deserialize<NativeVideoTrialRequestDto>("{\"screenId\":\"s\"}", options)!;
        request.SingleSlice.Should().BeFalse(); request.Quality.Should().BeNull(); request.SimpleOverlayPng.Should().BeFalse();
        (request.Quality ?? 95).Should().Be(95); request.DurationMs.Should().Be(10000); request.Fps.Should().Be(30);
        typeof(NativeVideoTrialRequestDto).GetConstructor([typeof(string), typeof(int), typeof(int), typeof(string)]).Should().NotBeNull();
        typeof(NativeVideoTrialDto).GetConstructor([typeof(string), typeof(string), typeof(int), typeof(int), typeof(string)]).Should().NotBeNull();
        var candidate = request with { SingleSlice = true, Quality = 100, SimpleOverlayPng = true };
        JsonSerializer.Deserialize<NativeVideoTrialRequestDto>(JsonSerializer.Serialize(candidate, options), options).Should().Be(candidate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(94)]
    [InlineData(101)]
    public async Task A_lower_quality_or_invalid_quality_is_rejected_before_any_IO(int quality)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id) { SingleSlice = true, Quality = quality }))
            .Should().ThrowAsync<ArgumentException>();
        rig.Opener.Opens.Should().Be(0);
        FluentActions.Invoking(() => new MfH264IntraEncoder(64, 64, true, true, quality)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true,95)]
    [InlineData(false,100)]
    public async Task New_variants_cannot_change_the_accepted_H2_path(bool singleSlice, int quality)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id) { SingleSlice = singleSlice, Quality = quality }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*TURZX-only*");
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public async Task A_png_overlay_experiment_cannot_be_requested_on_the_H2_protocol()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id) { SimpleOverlayPng = true }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*TURZX-only*");
        rig.Opener.Opens.Should().Be(0);
    }

    [MediaFoundationFact]
    public async Task Actual_high_quality_single_slice_output_is_independent_and_preserves_black_and_colours()
    {
        await Sta.RunAsync(() =>
        {
            using var encoder = new MfH264IntraEncoder(64, 64, true, true, 100);
            foreach (var (r,g,b) in new[] { (0,0,0), (252,25,20), (20,250,25) })
            {
                var bytes = encoder.Encode(PixelFrame.Solid(64,64,(byte)r,(byte)g,(byte)b));
                var kinds = H264AnnexB.Types(bytes);
                kinds.Count(k=>k==5).Should().Be(1); kinds.Should().Contain([7,8]); kinds.Should().NotContain(k=>k>=1 && k<=4);
                encoder.LastSliceCount.Should().Be(1);
                var actual = MfH264IntraEncoder.Decode(bytes,64,64,true).At(32,32);
                Math.Abs(actual.R-r).Should().BeLessThan(5); Math.Abs(actual.G-g).Should().BeLessThan(5); Math.Abs(actual.B-b).Should().BeLessThan(5);
            }
            return true;
        });
    }

    [MediaFoundationFact]
    public async Task Trial_preview_and_sent_bytes_use_the_same_variant_then_restore_legacy_without_persisting()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        rig.Opener.Configure = c => c.Reply = packet =>
        {
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0,512));
            var reply = new byte[512]; reply[0]=header[0]; reply[1]=200; header.AsSpan(4,4).CopyTo(reply.AsSpan(2)); return reply;
        };
        var asset = await rig.Manager.UploadScreenAssetAsync(new("variant fixture",Convert.ToBase64String(Images.Gif(2,2,
            GifFrame.Fill(0,0,2,2,1,delayCs:10),GifFrame.Fill(0,0,2,2,2,delayCs:10)))));
        var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay:"compact", Fps:15);
        var scene = new ScreenSceneDto([assignment]);
        await rig.Manager.SetScreenSceneAsync(scene); rig.Manager.Start();
        await Wait.UntilAsync(async()=> (await rig.ScreenAsync(Devices.Turzx.Id)).State==ScreenStates.Running,"initial image path");
        var path=Path.Combine(rig.DataDirectory,"screens","scene.json"); var original=File.ReadAllBytes(path);
        var trial=await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id,60000,30){SingleSlice=true,Quality=100,SimpleOverlayPng=true});
        trial.SingleSlice.Should().BeTrue(); trial.Quality.Should().Be(100); trial.SimpleOverlayPng.Should().BeTrue();
        await Wait.UntilAsync(async()=> (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo?.EncodingNote?.Contains("Q100, 1 slices",StringComparison.Ordinal)==true,"actual single-slice stream");
        var native=rig.Opener.Last.Writes.Where(p=>ScreenProtocol.DecryptHeader(ScreenFamily.Turzx,p.AsSpan(0,512))[0]==121).Last();
        H264AnnexB.Types(native.AsSpan(512)).Count(t=>t==5).Should().Be(1);
        (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo!.OverlayEncodingNote.Should().Contain("filter NONE");
        var preview=await rig.Manager.PreviewScreenAsync(assignment);
        preview.Note.Should().Contain("Q100, 1 slices").And.Contain("filter NONE").And.Contain("not sent");
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(async()=> (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo is null,"ordinary image restored");
        File.ReadAllBytes(path).Should().Equal(original); (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        var legacy=await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id,60000,30));
        await Wait.UntilAsync(async()=> (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo?.EncodingNote?.Contains("Q95",StringComparison.Ordinal)==true,"default encoder restored after a new trial");
        (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo!.OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
        await rig.Manager.EndNativeVideoTrialAsync(legacy.TrialId);
    }
}
