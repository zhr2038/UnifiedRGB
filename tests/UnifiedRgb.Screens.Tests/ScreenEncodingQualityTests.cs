using System.Buffers.Binary;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class ScreenEncodingQualityTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task A_full_colour_png_that_already_fits_is_byte_exact_and_cached(int rotation)
    {
        var canvas = Images.Solid(1920, 480, 41, 82, 123);
        var (old, first, repeat) = await Sta.RunAsync(() => (
            ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, rotation),
            ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, rotation, ScreenEncodingPolicies.LosslessFirstV1),
            ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, rotation, ScreenEncodingPolicies.LosslessFirstV1)));
        first.Image.Should().Equal(old.Image);
        repeat.Image.Should().BeSameAs(first.Image, "the bounded cache retains encoded bytes, not full rasters");
        first.Shown.Pixels.Should().Equal(canvas.Pixels);
        first.Note.Should().Contain("original WIC");
    }

    [Fact]
    public async Task The_optimizer_is_lossless_rgba8_and_preserves_size_orientation_and_pixels()
    {
        var canvas = Images.Solid(480, 1920, 0, 0, 0);
        for (var y = 0; y < canvas.Height; y++)
            for (var x = 0; x < canvas.Width; x++)
            {
                var p = (y * canvas.Width + x) * 4;
                canvas.Pixels[p] = (byte)x; canvas.Pixels[p + 1] = (byte)(y / 7); canvas.Pixels[p + 2] = (byte)(x ^ y);
            }
        var (bytes, decoded) = await Sta.RunAsync(() =>
        {
            var reference = WpfCodec.EncodePng(canvas);
            var png = BoundedPngEncoder.Optimize(canvas, 524288, reference);
            foreach (var tag in new[] { "sRGB", "gAMA", "pHYs" })
                ChunkOf(png, tag).Should().Equal(ChunkOf(reference, tag), "the same pixels retain the WIC colour interpretation and density");
            return (png, WpfCodec.DecodePng(png, 1920L * 480));
        });
        bytes[25].Should().Be(6, "no unverified Indexed8 colour type reaches production");
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)).Should().Be(480);
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)).Should().Be(1920);
        decoded.Pixels.Should().Equal(canvas.Pixels);
    }

    [Fact]
    public async Task Unsendable_noise_is_an_explicit_error_in_both_policies()
    {
        var canvas = Images.Solid(1920, 480, 0, 0, 0); var random = new Random(917);
        random.NextBytes(canvas.Pixels);
        for (var i = 3; i < canvas.ByteCount; i += 4) canvas.Pixels[i] = 255;
        foreach (var quality in new[] { ScreenEncodingPolicies.Legacy, ScreenEncodingPolicies.LosslessFirstV1 })
            await FluentActions.Awaiting(() => Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, 90, quality)))
                .Should().ThrowAsync<ScreenEncodeException>().WithMessage("*limit is 524288*");
    }

    [Fact]
    public void A_palette_flag_is_not_evidence_of_support_and_never_enables_the_research_path()
    {
        FluentActions.Invoking(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, Images.Solid(1920, 480, 1, 2, 3), 90,
            ScreenEncodingPolicies.PaletteFirstV1, indexed8Verified: true)).Should().Throw<ScreenEncodeException>().WithMessage("*research-only*");
        ScreenEncodingPolicies.IsKnown(ScreenEncodingPolicies.PaletteFirstV1).Should().BeFalse();
        FluentActions.Invoking(() => BoundedPngEncoder.Optimize(new PixelFrame(1921, 480), 524288)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Jpeg_families_keep_their_existing_bytes_under_the_png_policy()
    {
        var canvas = Images.Solid(480, 480, 7, 60, 150);
        var (old, current) = await Sta.RunAsync(() => (
            ScreenFrameEncoder.Encode(ScreenFamilyInfo.H2, canvas, 90),
            ScreenFrameEncoder.Encode(ScreenFamilyInfo.H2, canvas, 90, ScreenEncodingPolicies.LosslessFirstV1)));
        current.Image.Should().Equal(old.Image); current.Note.Should().Be(old.Note);
    }

    [Fact]
    public void Cache_is_bounded_and_keys_follow_pixels_mapping_and_policy()
    {
        var cache = new PngFrameCache(10, 2);
        cache.Add("a", new(new byte[4], 8, "a")); cache.Add("b", new(new byte[4], 8, "b"));
        cache.Find("a").Should().NotBeNull(); cache.Add("c", new(new byte[4], 8, "c"));
        cache.Find("b").Should().BeNull(); cache.Size.Should().Be((8, 2));
        cache.Add("d", new(new byte[9], 8, "d")); cache.Size.Should().Be((9, 1));
        cache.Add("oversized", new(new byte[11], 8, "x")); cache.Size.Should().Be((9, 1));
        var canvas = Images.Solid(1, 1, 1, 2, 3); var first = PngFrameCache.Key(canvas, 90, "legacy");
        PngFrameCache.Key(canvas, 270, "legacy").Should().NotBe(first);
        PngFrameCache.Key(canvas, 90, "lossless-first-v1").Should().NotBe(first);
        canvas.Pixels[0]++; PngFrameCache.Key(canvas, 90, "legacy").Should().NotBe(first);
    }

    private static byte[] ChunkOf(byte[] png, string name)
    {
        for (var offset = 8; offset < png.Length;)
        {
            var n = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == name) return png.AsSpan(offset, n + 12).ToArray();
            offset += n + 12;
        }
        throw new InvalidDataException("Missing " + name);
    }
}
