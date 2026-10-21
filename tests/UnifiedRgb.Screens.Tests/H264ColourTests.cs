using System.Text.RegularExpressions;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class H264ColourTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Colour_description_changes_only_SPS_and_is_idempotent(bool fullRange)
    {
        var source=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","h264-unspecified-colour.h264"));
        var changed=H264ColourMetadata.SpecifyCanvasColour(source,fullRange);
        changed.Should().NotEqual(source,"the inbox SPS omitted its canvas colour interpretation");
        H264ColourMetadata.SpecifyCanvasColour(changed,fullRange).Should().Equal(changed);
        OtherNals(changed).Should().Equal(OtherNals(source),"PPS, image slices and other NAL payloads must be byte-exact");
        H264AnnexB.Types(changed).Should().Equal(H264AnnexB.Types(source));
    }

    // Independent byte-oriented boundary check; this does not use the SPS bit parser under test.
    private static IEnumerable<string> OtherNals(byte[] bytes) => Regex.Split(Convert.ToHexString(bytes),"00000001|000001")
        .Where(s=>s.Length>=2 && (Convert.ToByte(s[..2],16)&31)!=7);

    [Theory]
    [InlineData("")]
    [InlineData("00000167")]
    [InlineData("000001674200")]
    [InlineData("0000016764001F80")]
    [InlineData("0000016580")]
    public void Invalid_or_uncontrolled_SPS_is_refused_before_any_device_send(string hex) =>
        FluentActions.Invoking(()=>H264ColourMetadata.SpecifyCanvasColour(Convert.FromHexString(hex))).Should().Throw<InvalidDataException>();

    [MediaFoundationFact]
    public async Task Encoded_black_grey_white_levels_and_actual_preview_stay_at_the_canvas_brightness()
    {
        await Sta.RunAsync(()=>
        {
            foreach(var fullRange in new[]{false,true})
            {
             using var encoder=new MfH264IntraEncoder(128,64,fullRange);
             foreach(var level in new byte[]{0,1,16,32,64,128,192,235,255})
             {
                var source=PixelFrame.Solid(128,64,level,level,level);
                var bytes=encoder.Encode(source);
                H264ColourMetadata.SpecifyCanvasColour(bytes,fullRange).Should().Equal(bytes);
                var pixels=MfH264IntraEncoder.Decode(bytes,128,64,fullRange).Pixels;
                for(var i=0;i<pixels.Length;i+=4)
                {
                    Math.Abs(pixels[i]-level).Should().BeLessThanOrEqualTo(2);
                    Math.Abs(pixels[i+1]-level).Should().BeLessThanOrEqualTo(2);
                    Math.Abs(pixels[i+2]-level).Should().BeLessThanOrEqualTo(2);
                }
             }
            }
            return true;
        });
    }

    [Theory]
    [InlineData(false,16,235)]
    [InlineData(true,0,255)]
    public void Actual_luma_samples_use_the_declared_range(bool fullRange,byte black,byte white)
    {
        MfH264IntraEncoder.ToNv12(PixelFrame.Solid(2,2,0,0,0),fullRange).Take(4).Should().OnlyContain(v=>v==black);
        MfH264IntraEncoder.ToNv12(PixelFrame.Solid(2,2,255,255,255),fullRange).Take(4).Should().OnlyContain(v=>v==white);
    }
}
