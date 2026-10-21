using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>Compositor previews, rotation, fit, brightness, overlay text and per-family encoding limits (no USB).</summary>
public sealed class CompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly (byte B, byte G, byte R, byte A) Red = (0, 0, 255, 255);
    private static readonly (byte B, byte G, byte R, byte A) Blue = (255, 0, 0, 255);

    [Theory]
    [InlineData("screen:tl-lcd:a", 400, 400, "JPEG q")]
    [InlineData("screen:h2:a", 480, 480, "JPEG q")]
    [InlineData("screen:turzx:a", 1920, 480, "PNG 480x1920 rot90 full colour")]
    public async Task Preview_has_the_family_canvas_size_brightness_and_a_note(string id, int w, int h, string note)
    {
        await using var rig = new Rig();

        var p = await rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(id, BackgroundColor: "#FF8000", Brightness: 0.5));

        (p.Width, p.Height).Should().Be((w, h));
        p.Note.Should().StartWith(note).And.EndWith("; device not present; output disabled");
        var shown = await Sta.RunAsync(() => WpfCodec.DecodePng(Convert.FromBase64String(p.PngBase64), 1920 * 480));
        (shown.Width, shown.Height).Should().Be((w, h));
        shown.At(w / 2, h / 2).Should().Be(((byte)0, (byte)64, (byte)128, (byte)255));
    }

    [Fact]
    public async Task Preview_of_a_present_enabled_screen_has_no_caveats()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);

        var p = await rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(Devices.H2.Id));

        p.Note.Should().MatchRegex(@"^JPEG q90 \d+ B <= 2097152$");
        rig.Opener.Opens.Should().Be(0, "a preview never opens the device");
    }

    [Fact]
    public async Task Invalid_assignments_are_refused_by_preview()
    {
        await using var rig = new Rig();

        foreach (var bad in new[]
                 {
                     new ScreenAssignmentDto("screen:h2:a", Rotation: 45),
                     new ScreenAssignmentDto("screen:h2:a", Brightness: 1.5),
                     new ScreenAssignmentDto("screen:h2:a", Fps: 61),
                     new ScreenAssignmentDto("screen:h2:a", BackgroundColor: "red"),
                     new ScreenAssignmentDto("screen:h2:a", Overlay: "big"),
                     new ScreenAssignmentDto("screen:h2:a", Fit: "stretch"),
                     new ScreenAssignmentDto("screen:h2:a", BackgroundAssetId: "0123456789abcdef"),
                     new ScreenAssignmentDto("screen:a065:a"),
                 })
        {
            await FluentActions.Awaiting(() => rig.Manager.PreviewScreenAsync(bad)).Should().ThrowAsync<ArgumentException>(bad.ToString());
        }
    }

    [Fact]
    public async Task Rotation_90_turns_the_left_half_to_the_top()
    {
        using var store = await TempStore.WithAsync(Images.Split(64, 32));

        var canvas = await Sta.RunAsync(() => new ScreenCompositor(store.Assets, 1 << 24)
            .Compose(ScreenFamilyInfo.TlLcd, new ScreenAssignmentDto("screen:tl-lcd:a", store.Ids[0], Rotation: 90), 0, null, Now).Canvas);

        canvas.At(200, 10).Should().Be(Red);
        canvas.At(200, 390).Should().Be(Blue);
        canvas.At(10, 200).Should().NotBe(Red);
    }

    [Fact]
    public async Task Cover_fills_and_contain_letterboxes_on_the_background_colour()
    {
        using var store = await TempStore.WithAsync(Images.Solid(100, 50, 255, 0, 0));
        var asset = store.Ids[0];

        var (cover, contain) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store.Assets, 1 << 24);
            return (c.Compose(ScreenFamilyInfo.TlLcd, new ScreenAssignmentDto("screen:tl-lcd:a", asset, "#0000FF"), 0, null, Now).Canvas,
                c.Compose(ScreenFamilyInfo.TlLcd, new ScreenAssignmentDto("screen:tl-lcd:a", asset, "#0000FF", Fit: "contain"), 0, null, Now).Canvas);
        });

        cover.At(200, 5).Should().Be(Red);
        cover.At(200, 200).Should().Be(Red);
        contain.At(200, 5).Should().Be(Blue);
        contain.At(200, 395).Should().Be(Blue);
        contain.At(200, 200).Should().Be(Red);
        contain.At(5, 200).Should().Be(Red);
    }

    [Fact]
    public async Task A_missing_asset_composes_the_background_with_a_warning()
    {
        using var store = await TempStore.WithAsync();

        var r = await Sta.RunAsync(() => new ScreenCompositor(store.Assets, 0)
            .Compose(ScreenFamilyInfo.H2, new ScreenAssignmentDto("screen:h2:a", "0123456789abcdef", "#00FF00"), 0, null, Now));

        r.Warning.Should().Contain("missing");
        r.Canvas.At(1, 1).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
    }

    [Fact]
    public async Task Animated_frames_follow_elapsed_time_and_mark_the_assignment_dynamic()
    {
        var gif = Images.Gif(2, 2, GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10), GifFrame.Fill(0, 0, 2, 2, 3, delayCs: 10));
        using var store = await TempStore.WithRawAsync(gif, await Images.PngAsync(Images.Solid(2, 2, 1, 1, 1)));

        var (dynamicGif, dynamicStill, dynamicOverlay, f0, f1, f2) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store.Assets, 1 << 24);
            var a = new ScreenAssignmentDto("screen:tl-lcd:a", store.Ids[0]);
            return (c.IsDynamic(a), c.IsDynamic(a with { BackgroundAssetId = store.Ids[1] }), c.IsDynamic(a with { BackgroundAssetId = null, Overlay = "compact" }),
                c.Compose(ScreenFamilyInfo.TlLcd, a, 0, null, Now).Canvas.At(5, 5),
                c.Compose(ScreenFamilyInfo.TlLcd, a, 150, null, Now).Canvas.At(5, 5),
                c.Compose(ScreenFamilyInfo.TlLcd, a, 200, null, Now).Canvas.At(5, 5));
        });

        (dynamicGif, dynamicStill, dynamicOverlay).Should().Be((true, false, true));
        f0.Should().Be(Red);
        f1.Should().Be(Blue);
        f2.Should().Be(Red, "the animation loops");
    }

    [Fact]
    public async Task Tl_jpeg_is_baseline_and_within_the_packet()
    {
        var canvas = Gradient(400, 400);

        var e = await Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.TlLcd, canvas, 90));

        e.Command.Should().Be(101);
        e.Image.Length.Should().BeLessThanOrEqualTo(101888);
        e.Image[..2].Should().Equal(0xFF, 0xD8);
        var markers = JpegMarkers(e.Image);
        markers.Should().Contain(0xC0, "baseline SOF0").And.NotContain(0xC2, "never progressive");
        e.Shown.Should().BeSameAs(canvas);
    }

    [Fact]
    public async Task Wrong_canvas_sizes_are_programming_errors()
    {
        var act = () => Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.TlLcd, Images.Solid(480, 480, 0, 0, 0), 90));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(90, 479, 0)]
    [InlineData(270, 0, 1919)]
    public async Task Turzx_png_is_the_rotated_480x1920_rgba_raster(int rotation, int nativeX, int nativeY)
    {
        var canvas = Images.Solid(1920, 480, 0, 0, 0);
        canvas.Pixels[2] = 255; // canvas (0,0) red

        var (e, native) = await Sta.RunAsync(() =>
        {
            var enc = ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, rotation);
            return (enc, WpfCodec.DecodePng(enc.Image, 1920 * 480));
        });

        e.Command.Should().Be(102);
        e.Image.Length.Should().BeLessThanOrEqualTo(524288);
        BinaryPrimitives.ReadInt32BigEndian(e.Image.AsSpan(16)).Should().Be(480);
        BinaryPrimitives.ReadInt32BigEndian(e.Image.AsSpan(20)).Should().Be(1920);
        e.Image[24].Should().Be(8, "8 bits per channel");
        e.Image[25].Should().Be(6, "RGBA");
        native.At(nativeX, nativeY).Should().Be(Red);
        native.At(240, 960).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        e.Shown.At(0, 0).Should().Be(Red, "the preview stays in authoring orientation");
        e.Note.Should().Be($"PNG 480x1920 rot{rotation} full colour {e.Image.Length} B <= 524288");
    }

    [Fact]
    public async Task Turzx_steps_down_colour_depth_to_fit_and_the_preview_shows_it()
    {
        var rng = new Random(42);
        var canvas = Images.Solid(1920, 480, 0, 0, 0);
        for (var i = 0; i < canvas.Pixels.Length; i += 4)
        {
            canvas.Pixels[i] = (byte)(128 + rng.Next(4));
            canvas.Pixels[i + 1] = (byte)(128 + rng.Next(4));
            canvas.Pixels[i + 2] = (byte)(128 + rng.Next(4));
        }

        var e = await Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, 90));

        e.Note.Should().StartWith("PNG 480x1920 rot90 6-bit posterized");
        e.Image.Length.Should().BeLessThanOrEqualTo(524288);
        e.Shown.Should().NotBeSameAs(canvas);
        e.Shown.At(7, 7).Should().Be(((byte)130, (byte)130, (byte)130, (byte)255));
    }

    [Fact]
    public async Task Turzx_content_that_cannot_fit_fails_clearly_instead_of_truncating()
    {
        var rng = new Random(7);
        var canvas = Images.Solid(1920, 480, 0, 0, 0);
        rng.NextBytes(canvas.Pixels);
        for (var i = 3; i < canvas.Pixels.Length; i += 4)
        {
            canvas.Pixels[i] = 255;
        }

        var act = () => Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, 90));

        await act.Should().ThrowAsync<ScreenEncodeException>().WithMessage("*even at 5 bits per channel; the limit is 524288*");
    }

    [Fact]
    public async Task Turzx_steps_down_to_5_bits_when_6_bits_is_still_too_large()
    {
        var canvas = Noise(128, 8, 11); // two 6-bit levels per channel (random), one 5-bit level

        var e = await Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, 90));

        e.Note.Should().StartWith("PNG 480x1920 rot90 5-bit posterized");
        e.Image.Length.Should().BeLessThanOrEqualTo(524288);
        e.Shown.At(7, 7).Should().Be(((byte)132, (byte)132, (byte)132, (byte)255), "the preview shows the 5-bit raster that is sent");
    }

    [Fact]
    public async Task Turzx_has_no_4_bit_step_content_only_4_bits_could_fit_fails()
    {
        var canvas = Noise(128, 16, 13); // two 5-bit levels per channel (random), one 4-bit level

        var (fourBitBytes, act) = await Sta.RunAsync(() =>
        {
            var four = canvas.Clone();
            PixelOps.Posterize(four, 4);
            return (WpfCodec.EncodePng(PixelOps.Rotate(four, 90)).Length,
                FluentActions.Invoking(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, canvas, 90)).Should().Throw<ScreenEncodeException>().Which);
        });

        fourBitBytes.Should().BeLessThanOrEqualTo(524288, "a 4-bit step would have fitted this content");
        act.Message.Should().Match("The TURZX PNG is * bytes even at 5 bits per channel; the limit is 524288. Simplify the content.",
            "the approved ladder is 8, 6, 5 bits; there is no silent 4-bit colour");
    }

    [Fact]
    public async Task Turzx_landscape_rotation_must_be_a_quarter_turn()
    {
        var act = () => Sta.RunAsync(() => ScreenFrameEncoder.Encode(ScreenFamilyInfo.Turzx, Images.Solid(1920, 480, 0, 0, 0), 180));

        await act.Should().ThrowAsync<ScreenEncodeException>();
    }

    [Fact]
    public void Overlay_values_never_fabricate_zero()
    {
        var full = new PcTelemetryDto(Now, 25.04, 12L << 30, 32L << 30, 42, 61.4, 8L << 30, 32L << 30, "s", null);

        OverlayRenderer.Values(full, Now).Should().Equal(("CPU", "25%"), ("GPU", "42% 61°C"), ("RAM", "12.0/32 GB"), ("VRAM", "8.0/32 GB"));
        OverlayRenderer.Values(null, Now).Select(v => v.Value).Should().OnlyContain(v => v == "—");
        OverlayRenderer.Values(full, Now.AddSeconds(6)).Select(v => v.Value).Should().OnlyContain(v => v == "—", "stale telemetry is not shown");
        OverlayRenderer.Values(full, Now.AddSeconds(-6)).Select(v => v.Value).Should().OnlyContain(v => v == "—", "future-dated telemetry is not shown");
        var partial = new PcTelemetryDto(Now, null, 1, null, null, 70, null, null, "s", "no GPU");
        OverlayRenderer.Values(partial, Now).Should().Equal(("CPU", "—"), ("GPU", "— 70°C"), ("RAM", "—"), ("VRAM", "—"));
        OverlayRenderer.Values(full with { CpuUsagePercent = double.NaN }, Now)[0].Value.Should().Be("—");
    }

    [Theory]
    [InlineData("compact", 0, 0)]
    [InlineData("dashboard", 0, 0)]
    [InlineData("compact", 90, 1)]
    [InlineData("dashboard", 0, 1)]
    public async Task Overlays_draw_on_every_family_canvas(string overlay, int rotation, int family)
    {
        var info = new[] { ScreenFamilyInfo.TlLcd, ScreenFamilyInfo.Turzx }[family];
        using var store = await TempStore.WithAsync();
        var telemetry = new PcTelemetryDto(Now, 25, 12L << 30, 32L << 30, null, null, null, null, "s", null);

        var canvas = await Sta.RunAsync(() => new ScreenCompositor(store.Assets, 0)
            .Compose(info, new ScreenAssignmentDto("screen:x:a", Overlay: overlay, Rotation: rotation), 0, telemetry, Now).Canvas);

        (canvas.Width, canvas.Height).Should().Be((info.Width, info.Height));
        var lit = 0;
        for (var i = 0; i < canvas.Pixels.Length; i += 4)
        {
            lit += canvas.Pixels[i] + canvas.Pixels[i + 1] + canvas.Pixels[i + 2] > 0 ? 1 : 0;
        }

        lit.Should().BeGreaterThan(100, "overlay text and accent are drawn");
        canvas.Pixels.Where((_, i) => i % 4 == 3).Should().OnlyContain(a => a == 255, "the canvas is opaque");
    }

    /// <summary>A 1920x480 canvas whose RGB channels are independent random values in base..base+span-1.</summary>
    private static PixelFrame Noise(int @base, int span, int seed)
    {
        var rng = new Random(seed);
        var canvas = Images.Solid(1920, 480, 0, 0, 0);
        for (var i = 0; i < canvas.Pixels.Length; i += 4)
        {
            canvas.Pixels[i] = (byte)(@base + rng.Next(span));
            canvas.Pixels[i + 1] = (byte)(@base + rng.Next(span));
            canvas.Pixels[i + 2] = (byte)(@base + rng.Next(span));
        }

        return canvas;
    }

    private static PixelFrame Gradient(int w, int h)
    {
        var f = new PixelFrame(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = ((y * w) + x) * 4;
                f.Pixels[i] = (byte)x;
                f.Pixels[i + 1] = (byte)y;
                f.Pixels[i + 2] = (byte)(x ^ y);
                f.Pixels[i + 3] = 255;
            }
        }

        return f;
    }

    private static List<byte> JpegMarkers(byte[] jpeg)
    {
        var markers = new List<byte>();
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var m = jpeg[i + 1];
            markers.Add(m);
            if (m == 0xDA)
            {
                break;
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(i + 2));
        }

        return markers;
    }

    /// <summary>A throwaway asset store holding the given images (imported like an upload).</summary>
    private sealed class TempStore : IDisposable
    {
        private TempStore(string root, AssetStore assets, IReadOnlyList<string> ids)
        {
            Root = root;
            Assets = assets;
            Ids = ids;
        }

        public string Root { get; }

        public AssetStore Assets { get; }

        public IReadOnlyList<string> Ids { get; }

        public static async Task<TempStore> WithAsync(params PixelFrame[] frames)
        {
            var raw = new List<byte[]>();
            foreach (var f in frames)
            {
                raw.Add(await Images.PngAsync(f));
            }

            return await WithRawAsync([.. raw]);
        }

        public static async Task<TempStore> WithRawAsync(params byte[][] uploads)
        {
            var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
            var store = new AssetStore(root, 1 << 26, 16, NullLogger.Instance);
            var ids = new List<string>();
            foreach (var data in uploads)
            {
                var import = await Sta.RunAsync(() => ImageImporter.Import(data, ImportLimits.Default));
                ids.Add(store.Commit(AssetStore.IdOf(data), "t", import).Id);
            }

            return new TempStore(root, store, ids);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
