using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class JpegDetailPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detail_policy_uses_q100_when_it_fits_and_legacy_output_remains_exact(bool h2)
    {
        var family=h2?ScreenFamilyInfo.H2:ScreenFamilyInfo.TlLcd;
        await Sta.RunAsync(() =>
        {
            var canvas=Pattern(family.Width,family.Height);
            var legacy=ScreenFrameEncoder.Encode(family,canvas,90);
            var detail=ScreenFrameEncoder.Encode(family,canvas,90,ScreenEncodingPolicies.JpegDetailV1);
            legacy.Image.Should().Equal(WpfCodec.EncodeJpeg(canvas,90));
            detail.Image.Should().Equal(WpfCodec.EncodeJpeg(canvas,100));
            detail.Note.Should().Contain("q100");detail.Command.Should().Be(ScreenProtocol.PushJpegCommand);
            detail.Image.Length.Should().BeLessThanOrEqualTo(ScreenProtocol.MaxImageBytes(family.Family));
            return 0;
        });
    }

    [Fact]
    public async Task Detail_policy_falls_back_within_the_unchanged_TL_wire_budget()
    {
        await Sta.RunAsync(() =>
        {
            var canvas=new PixelFrame(400,400);var random=new Random(4816);random.NextBytes(canvas.Pixels);
            for(var i=3;i<canvas.Pixels.Length;i+=4)canvas.Pixels[i]=255;
            WpfCodec.EncodeJpeg(canvas,100).Length.Should().BeGreaterThan(ScreenProtocol.TlMaxJpegBytes);
            var encoded=ScreenFrameEncoder.Encode(ScreenFamilyInfo.TlLcd,canvas,90,ScreenEncodingPolicies.JpegDetailV1);
            encoded.Note.Should().NotContain("q100");
            encoded.Image.Length.Should().BeLessThanOrEqualTo(101888);
            ScreenProtocol.BuildImagePacket(ScreenFamily.TlLcd,encoded.Command,encoded.Image,1).Length.Should().Be(102400);
            return 0;
        });
    }

    [Fact]
    public async Task PNG_policy_is_not_silently_replaced_and_unconfigured_JPEG_keeps_legacy()
    {
        await using var rig=new Rig(null,Devices.Tl1,Devices.Turzx);
        (await rig.Manager.GetScreenDeviceSettingsAsync(Devices.Tl1.Id)).EncodingQuality.Should().Be("legacy");
        (await rig.Manager.GetScreenDeviceSettingsAsync(Devices.Tl1.Id)).JpegDetailSupported.Should().BeTrue();
        (await rig.Manager.GetScreenDeviceSettingsAsync(Devices.Turzx.Id)).JpegDetailSupported.Should().BeFalse();
        await FluentActions.Awaiting(()=>rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Turzx.Id,0,ScreenEncodingPolicies.JpegDetailV1)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*only available for TL/H2*");
        await FluentActions.Awaiting(()=>Sta.RunAsync(()=>ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx,new PixelFrame(1920,480),90,ScreenEncodingPolicies.JpegDetailV1)))
            .Should().ThrowAsync<ScreenEncodeException>();
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Explicit_detail_save_survives_restart_without_changing_content_or_rotation()
    {
        await using var rig=new Rig(null,Devices.Tl1);
        await rig.Manager.SetScreenSceneAsync(new([new(Devices.Tl1.Id,BackgroundColor:"#123456",Rotation:90)]));
        var scene=await rig.Manager.GetScreenSceneAsync();
        await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Tl1.Id,90,ScreenEncodingPolicies.JpegDetailV1));
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        await rig.Manager.DisposeAsync();
        await using var restarted=new ScreenManager(rig.Options,rig.DataDirectory,rig.Platform);
        var settings=await restarted.GetScreenDeviceSettingsAsync(Devices.Tl1.Id);
        settings.EncodingQuality.Should().Be(ScreenEncodingPolicies.JpegDetailV1);settings.Rotation.Should().Be(90);
        (await restarted.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Detail_preview_decodes_the_actual_JPEG_bytes_without_hardware_output()
    {
        await using var rig=new Rig(null,Devices.Tl1);
        await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Tl1.Id,0,ScreenEncodingPolicies.JpegDetailV1));
        var request=new ScreenAssignmentDto(Devices.Tl1.Id,BackgroundColor:"#345678",Overlay:"none");
        var preview=await rig.Manager.PreviewScreenAsync(request);preview.Note.Should().Contain("q100");
        await Sta.RunAsync(()=>
        {
            var canvas=PixelFrame.Solid(400,400,0x34,0x56,0x78);var encoded=ScreenFrameEncoder.Encode(ScreenFamilyInfo.TlLcd,canvas,90,ScreenEncodingPolicies.JpegDetailV1);
            using var stream=new MemoryStream(encoded.Image,false);var expected=WpfCodec.ToPixels(WpfCodec.Decoder("jpeg",stream).Frames[0]);
            WpfCodec.DecodePng(Convert.FromBase64String(preview.PngBase64),160000).Pixels.Should().Equal(expected.Pixels);return 0;
        });
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public void Capability_is_optional_in_older_RPC_shapes_and_is_never_a_saved_device_setting()
    {
        var options=ContractsJson.Configure(new JsonSerializerOptions());
        var old=JsonSerializer.Deserialize<ScreenDeviceSettingsDto>("""{"screenId":"screen:tl-lcd:test","rotation":90,"encodingQuality":"legacy","indexed8Verified":false}""",options)!;
        old.JpegDetailSupported.Should().BeFalse();
        var bytes=ScreenDeviceSettingsStore.Serialize(new Dictionary<string,ScreenDeviceSetting>{{"screen:tl-lcd:test",new(90,ScreenEncodingPolicies.JpegDetailV1)}});
        System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("Supported").And.NotContain("supported");
    }

    private static PixelFrame Pattern(int w,int h)
    {
        var result=new PixelFrame(w,h);
        for(var y=0;y<h;y++)for(var x=0;x<w;x++){var i=(y*w+x)*4;result.Pixels[i]=(byte)(x%256);result.Pixels[i+1]=(byte)(y%256);result.Pixels[i+2]=(byte)((x+y)%256);result.Pixels[i+3]=255;}
        return result;
    }
}
