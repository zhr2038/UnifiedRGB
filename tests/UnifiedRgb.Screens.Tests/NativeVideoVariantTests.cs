using System.Text.Json;
using Microsoft.Extensions.Configuration;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host;
using UnifiedRgb.Screens.Composition;
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
    [InlineData(34)]
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
    [InlineData(false,80)]
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

    [MediaFoundationFact]
    public async Task Actual_lower_quality_single_slice_output_shrinks_access_unit_monotonically_and_decodes_cleanly()
    {
        var sizes = await Sta.RunAsync(() =>
        {
            var pixels = new byte[480 * 1920 * 4];
            for (var y = 0; y < 1920; y++)
            for (var x = 0; x < 480; x++)
            {
                var offset = (y * 480 + x) * 4;
                pixels[offset] = (byte)(((x * 3 + y * 5) / 4 + ((x ^ y) & 31)) & 0xFF);
                pixels[offset + 1] = (byte)(((x * 7 + y * 2) / 4 + ((x * y) & 15)) & 0xFF);
                pixels[offset + 2] = (byte)(((x * 2 + y * 9) / 4) & 0xFF);
                pixels[offset + 3] = 255;
            }
            var frame = new PixelFrame(480, 1920, pixels);
            var lengths = new List<int>(4);
            foreach (var q in new[] { 95, 85, 75, 60 })
            {
                using var encoder = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: true, quality: q);
                var bytes = encoder.Encode(frame);
                H264AnnexB.ValidateIdrAccessUnit(bytes, out var sliceCount).Should().BeTrue();
                sliceCount.Should().Be(1);
                var decoded = MfH264IntraEncoder.Decode(bytes, 480, 1920, fullRange: true);
                decoded.Width.Should().Be(480);
                decoded.Height.Should().Be(1920);
                lengths.Add(bytes.Length);
            }
            return lengths;
        });

        sizes[3].Should().BeLessThan(sizes[2]);
        sizes[2].Should().BeLessThan(sizes[1]);
        sizes[1].Should().BeLessThan(sizes[0]);
    }

    [Fact]
    public async Task TurzxVideoQuality_option_defaults_to_95_is_absent_from_shipped_settings_and_obeys_trial_precedence()
    {
        new ScreensOptions().TurzxVideoQuality.Should().Be(95);
        var shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.ReadAllText(shipped).Should().NotContain(nameof(ScreensOptions.TurzxVideoQuality), "no source appsettings change");
        Bind(new ConfigurationBuilder().AddJsonFile(shipped)).TurzxVideoQuality.Should().Be(95);
        Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UnifiedRgb:Screens:Enabled"] = "true" }))
            .TurzxVideoQuality.Should().Be(95, "absent means default 95");
        Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UnifiedRgb:Screens:TurzxVideoQuality"] = "75" }))
            .TurzxVideoQuality.Should().Be(75);

        ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 75 }, ScreenFamily.Turzx, null, verified: true).Should().Be(75);
        ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 75 }, ScreenFamily.Turzx, null, verified: false).Should().Be(95);
        ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 75 }, ScreenFamily.H2, null, verified: true).Should().Be(95);
        ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 75 }, ScreenFamily.TlLcd, null, verified: true).Should().Be(95);
        ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 75 }, ScreenFamily.Turzx,
            new NativeVideoTrialDto("t", Devices.Turzx.Id, 10000, 60, null), verified: true).Should().Be(95);
        ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 75 }, ScreenFamily.Turzx,
            new NativeVideoTrialDto("t", Devices.Turzx.Id, 10000, 60, null) { Quality = 65 }, verified: true).Should().Be(65);
        FluentActions.Invoking(() => ScreenWorker.QualityOf(new ScreensOptions { TurzxVideoQuality = 34 }, ScreenFamily.Turzx, null, verified: true))
            .Should().Throw<ScreenEncodeException>().WithMessage("TurzxVideoQuality must be in 35..100*");

        foreach (var badQuality in new[] { 34, 101 })
        {
            await using var rig = new Rig(o =>
            {
                Rig.AllOutput(o);
                o.NativeH264VerifiedScreenIds = [Devices.Turzx.Id];
                o.TurzxVideoQuality = badQuality;
            }, Devices.Turzx);
            await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Devices.Turzx.Id, 0, ScreenEncodingPolicies.NativeH264IntraV1));
            var asset = await rig.Manager.UploadScreenAssetAsync(new("gif fixture", Convert.ToBase64String(Images.Gif(2, 2, GifFrame.Fill(0, 0, 2, 2, 1)))));
            await FluentActions.Awaiting(() => rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id)))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("NOT SENDABLE: TurzxVideoQuality must be in 35..100*");
            rig.Opener.Opens.Should().Be(0);
        }

        static ScreensOptions Bind(IConfigurationBuilder builder) =>
            builder.Build().GetSection(UnifiedRgbOptions.Section).Get<UnifiedRgbOptions>()!.Screens;
    }
}
