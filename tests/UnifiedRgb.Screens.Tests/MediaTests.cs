using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>Upload decoding bounds, GIF timing/disposal, normalization and the bounded asset store.</summary>
public sealed class MediaTests
{
    [Fact]
    public async Task Png_and_jpeg_import_as_single_images()
    {
        var png = await Images.PngAsync(Images.Split(64, 32));
        var jpeg = await Images.JpegAsync(Images.Solid(40, 30, 0, 255, 0));

        var a = await Sta.RunAsync(() => ImageImporter.Import(png, ImportLimits.Default));
        var b = await Sta.RunAsync(() => ImageImporter.Import(jpeg, ImportLimits.Default));

        a.Format.Should().Be("png");
        (a.Width, a.Height, a.FramePngs.Count, a.DurationMs, a.Animated).Should().Be((64, 32, 1, 0, false));
        b.Format.Should().Be("jpeg");
        (b.Width, b.Height).Should().Be((40, 30));
    }

    [Fact]
    public async Task Large_images_are_fitted_into_the_normalization_box_without_upscaling()
    {
        var png = await Images.PngAsync(Images.Solid(3840, 1000, 10, 20, 30));
        var r = await Sta.RunAsync(() => ImageImporter.Import(png, ImportLimits.Default));

        (r.Width, r.Height).Should().Be((1920, 500));
        var small = await Sta.RunAsync(() => ImageImporter.Import(png, ImportLimits.Default with { NormalizeBox = 5000 }));
        (small.Width, small.Height).Should().Be((3840, 1000));
    }

    [Fact]
    public async Task Gif_delays_follow_the_short_delay_rule()
    {
        var gif = Images.Gif(2, 2,
            GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 2),
            GifFrame.Fill(0, 0, 2, 2, 2, delayCs: 1),
            GifFrame.Fill(0, 0, 2, 2, 3, delayCs: 0),
            GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 50));

        var r = await Sta.RunAsync(() => ImageImporter.Import(gif, ImportLimits.Default));

        r.Format.Should().Be("gif");
        r.DelaysMs.Should().Equal(20, 100, 100, 500);
        r.DurationMs.Should().Be(720);
        r.Animated.Should().BeTrue();
        var asset = new StoredAsset("0123456789abcdef", "g", "gif", 2, 2, r.DelaysMs, 1);
        asset.Kind.Should().Be("animation");
        new[] { 0, 19, 20, 119, 120, 219, 220, 719, 720, 740 }.Select(t => asset.FrameAt(t)).Should().Equal(0, 0, 1, 1, 2, 2, 3, 3, 0, 1);
    }

    [Fact]
    public async Task Gif_disposal_keep_clear_and_restore_are_composited()
    {
        // 4x4 red; then green 2x2 at (1,1) disposed to background; then blue 1x1 at (0,0)
        var clear = Images.Gif(4, 4,
            GifFrame.Fill(0, 0, 4, 4, 1, disposal: 1),
            GifFrame.Fill(1, 1, 2, 2, 2, disposal: 2),
            GifFrame.Fill(0, 0, 1, 1, 3, disposal: 0));
        var restore = Images.Gif(4, 4,
            GifFrame.Fill(0, 0, 4, 4, 1, disposal: 1),
            GifFrame.Fill(1, 1, 2, 2, 2, disposal: 3),
            GifFrame.Fill(0, 0, 1, 1, 3, disposal: 0));

        var frames = await Sta.RunAsync(() =>
        {
            var c = ImageImporter.Import(clear, ImportLimits.Default).FramePngs.Select(p => WpfCodec.DecodePng(p, 100)).ToArray();
            var r = ImageImporter.Import(restore, ImportLimits.Default).FramePngs.Select(p => WpfCodec.DecodePng(p, 100)).ToArray();
            return (c, r);
        });

        frames.c[0].At(1, 1).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        frames.c[1].At(1, 1).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        frames.c[1].At(3, 3).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        frames.c[2].At(1, 1).A.Should().Be(0, "disposal 2 clears the rectangle to transparent");
        frames.c[2].At(0, 0).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        frames.c[2].At(3, 3).R.Should().Be(255);
        frames.r[2].At(1, 1).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255), "disposal 3 restores the previous canvas");
    }

    [Fact]
    public void Non_images_and_oversized_uploads_are_refused_before_decoding()
    {
        FluentActions.Invoking(() => ImageImporter.Import([], ImportLimits.Default)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ImageImporter.Import("BM not a bitmap we accept"u8.ToArray(), ImportLimits.Default))
            .Should().Throw<ArgumentException>().WithMessage("*JPEG, PNG and GIF*");
        FluentActions.Invoking(() => ImageImporter.Import(new byte[(12 * 1024 * 1024) + 1], ImportLimits.Default))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_png_header_claiming_more_than_40_megapixels_is_refused_from_its_header()
    {
        var png = await Images.PngAsync(Images.Solid(2, 2, 1, 2, 3));
        SetPngSize(png, 10_000, 5_000);

        var act = () => Sta.RunAsync(() => ImageImporter.Import(png, ImportLimits.Default));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*raster*");
    }

    [Fact]
    public async Task Gif_frame_count_duration_and_total_pixel_budgets_are_enforced()
    {
        var many = Images.Gif(1, 1, Enumerable.Range(0, 301).Select(_ => GifFrame.Fill(0, 0, 1, 1, 1)).ToArray());
        var longGif = Images.Gif(1, 1, GifFrame.Fill(0, 0, 1, 1, 1, delayCs: 2000), GifFrame.Fill(0, 0, 1, 1, 2, delayCs: 2000));
        var big = Images.Gif(10, 10, GifFrame.Fill(0, 0, 10, 10, 1), GifFrame.Fill(0, 0, 10, 10, 2));

        await FluentActions.Awaiting(() => Sta.RunAsync(() => ImageImporter.Import(many, ImportLimits.Default)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*frames*");
        await FluentActions.Awaiting(() => Sta.RunAsync(() => ImageImporter.Import(longGif, ImportLimits.Default)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*30000 ms*");
        await FluentActions.Awaiting(() => Sta.RunAsync(() => ImageImporter.Import(big, ImportLimits.Default with { MaxTotalPixels = 150 })))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*decode budget*");
    }

    [Fact]
    public async Task Asset_store_commits_lists_reloads_and_deletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var data = await Images.PngAsync(Images.Solid(8, 8, 9, 9, 9));
            var import = await Sta.RunAsync(() => ImageImporter.Import(data, ImportLimits.Default));
            var id = AssetStore.IdOf(data);
            var store = new AssetStore(root, 1 << 20, 4, NullLogger.Instance);

            var a = store.Commit(id, "grey", import);
            store.Commit(id, "other name", import).Should().BeSameAs(a, "identical content maps to the existing asset");
            a.ToDto().Should().Be(new ScreenAssetDto(id, "grey", 8, 8, 1, 0, "image"));
            Directory.CreateDirectory(Path.Combine(root, "~tmp-interrupted"));

            var reloaded = new AssetStore(root, 1 << 20, 4, NullLogger.Instance);
            reloaded.List().Should().ContainSingle().Which.Should().BeEquivalentTo(a);
            Directory.Exists(Path.Combine(root, "~tmp-interrupted")).Should().BeFalse("debris is removed on load");
            reloaded.ReadFrame(reloaded.Find(id)!, 0).Should().Equal(import.FramePngs[0]);

            reloaded.Delete(id).Should().BeTrue();
            reloaded.Delete(id).Should().BeFalse();
            reloaded.List().Should().BeEmpty();
            Directory.EnumerateDirectories(root).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Asset_store_count_and_byte_bounds_hold()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imports = new List<(string Id, ImportResult R)>();
            for (byte i = 0; i < 3; i++)
            {
                var data = await Images.PngAsync(Images.Solid(4, 4, i, 0, 0));
                imports.Add((AssetStore.IdOf(data), await Sta.RunAsync(() => ImageImporter.Import(data, ImportLimits.Default))));
            }

            var byCount = new AssetStore(root, 1 << 20, 2, NullLogger.Instance);
            byCount.Commit(imports[0].Id, "a", imports[0].R);
            byCount.Commit(imports[1].Id, "b", imports[1].R);
            FluentActions.Invoking(() => byCount.Commit(imports[2].Id, "c", imports[2].R)).Should().Throw<InvalidOperationException>();
            FluentActions.Invoking(byCount.RequireRoomForOne).Should().Throw<InvalidOperationException>();

            var tight = new AssetStore(root + "-b", imports[0].R.FramePngs[0].Length, 10, NullLogger.Instance);
            tight.Commit(imports[0].Id, "a", imports[0].R);
            FluentActions.Invoking(() => tight.Commit(imports[1].Id, "b", imports[1].R)).Should().Throw<InvalidOperationException>().WithMessage("*exceed*");
            Directory.Delete(root + "-b", true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Upload_via_the_service_is_bounded_and_deduplicated()
    {
        await using var rig = new Rig();
        var png = await Images.PngAsync(Images.Solid(16, 16, 1, 2, 3));

        var a = await rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("logo", Convert.ToBase64String(png)));
        var b = await rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("logo again", Convert.ToBase64String(png)));

        b.Should().Be(a);
        (await rig.Manager.ListScreenAssetsAsync()).Should().ContainSingle();
        await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("", Convert.ToBase64String(png))))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("x\n", Convert.ToBase64String(png))))
            .Should().NotThrowAsync("surrounding whitespace is trimmed");
        await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("bad\u0001name", Convert.ToBase64String(png))))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("x", "not base64!")))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("x", new string('A', 17_000_000))))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("x", Convert.ToBase64String("plain text"u8.ToArray()))))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_asset_in_the_current_scene_cannot_be_deleted()
    {
        await using var rig = new Rig();
        var png = await Images.PngAsync(Images.Solid(16, 16, 1, 2, 3));
        var a = await rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("logo", Convert.ToBase64String(png)));
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto("screen:h2:x", a.Id)));

        await FluentActions.Awaiting(() => rig.Manager.DeleteScreenAssetAsync(a.Id)).Should().ThrowAsync<InvalidOperationException>();
        await rig.Manager.SetScreenSceneAsync(Images.Scene());
        await rig.Manager.DeleteScreenAssetAsync(a.Id);
        (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty();
        await FluentActions.Awaiting(() => rig.Manager.DeleteScreenAssetAsync(a.Id)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.DeleteScreenAssetAsync("../../x")).Should().ThrowAsync<ArgumentException>();
    }

    private static void SetPngSize(byte[] png, int width, int height)
    {
        // IHDR data starts at 16; CRC covers type + data (12..29) and sits at 29
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
