using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The keyboard OLED gray encoder and bounded frame preparation (Stage0). Expected levels are independent hand-computed vectors
/// of the documented formulas, not outputs of the code under test. No device is opened.
/// </summary>
public sealed class KeyboardOledEncoderTests
{
    private const string Round = KeyboardDisplayAnimationEncodings.LumaRound;
    private const string Bayer = KeyboardDisplayAnimationEncodings.LumaBayer4;
    private const int W = KeyboardOledFormat.Width;
    private const int H = KeyboardOledFormat.Height;

    [Fact]
    public void Packing_puts_the_even_pixel_in_the_low_nibble_and_round_trips()
    {
        var levels = new byte[KeyboardOledFormat.Pixels];
        levels[0] = 1;
        levels[1] = 2;
        levels[W] = 15; // first pixel of row 1 (even index 208)
        var packed = new byte[KeyboardOledFormat.PackedBytes];
        KeyboardOledFormat.Pack(levels, packed);
        packed[0].Should().Be(0x21);
        packed[W / 2].Should().Be(0x0F);
        var back = new byte[KeyboardOledFormat.Pixels];
        KeyboardOledFormat.Unpack(packed, back);
        back.Should().Equal(levels);
        levels[3] = 16;
        ((Action)(() => KeyboardOledFormat.Pack(levels, packed))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => KeyboardOledFormat.Unpack(new byte[10], back))).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(255, 255, 255, 15)]
    [InlineData(128, 128, 128, 8)]
    [InlineData(255, 0, 0, 3)]
    [InlineData(0, 255, 0, 11)]
    [InlineData(0, 0, 255, 1)]
    public void Round_encoding_uses_luma_not_the_status_pages_blue_channel(byte r, byte g, byte b, int expected)
    {
        var packed = KeyboardOledEncoder.Encode(Solid(r, g, b), Round);
        packed.Should().HaveCount(KeyboardOledFormat.PackedBytes).And.OnlyContain(v => v == (byte)(expected | (expected << 4)));
        // The unchanged status page packs blue/17 (red 0, green 0, blue 15, gray 7): the animation encoding is a separate, explicit choice.
        var legacy = b / 17;
        if (r != g || g != b) legacy.Should().NotBe(expected);
    }

    [Fact]
    public void Bayer_encoding_is_a_fixed_ordered_threshold()
    {
        Levels(KeyboardOledEncoder.Encode(Solid(136, 136, 136), Bayer)).Should().OnlyContain(v => v == 8);
        var field = Levels(KeyboardOledEncoder.Encode(Solid(128, 128, 128), Bayer));
        field.Should().OnlyContain(v => v == 7 || v == 8);
        field.Sum(v => (long)v).Should().Be(KeyboardOledFormat.Pixels * 15L / 2, "half of every 4x4 cell rounds up at luma 128");
        // Matrix row 0 is 0, 8, 2, 10: thresholds 0..7 give 7, 8..15 give 8 at luma 128.
        field.Take(4).Should().Equal(7, 8, 7, 8);
        Levels(KeyboardOledEncoder.Encode(Solid(0, 0, 0), Bayer)).Should().OnlyContain(v => v == 0);
        Levels(KeyboardOledEncoder.Encode(Solid(255, 255, 255), Bayer)).Should().OnlyContain(v => v == 15);
        Levels(KeyboardOledEncoder.Encode(Solid(128, 128, 128), Bayer)).Should().Equal(field, "the encoder is deterministic");
    }

    [Theory]
    [InlineData(Round)]
    [InlineData(Bayer)]
    public void A_one_pixel_move_changes_only_the_bytes_it_touches(string encoding)
    {
        var a = KeyboardOledEncoder.Encode(GradientWithSquare(40), encoding);
        var b = KeyboardOledEncoder.Encode(GradientWithSquare(41), encoding);
        // 8 rows x (the column the square left + the column it entered), in 16 different bytes.
        a.Zip(b).Count(p => p.First != p.Second).Should().Be(16);
    }

    [Fact]
    public void Encoder_rejects_wrong_sizes_and_unknown_encodings()
    {
        ((Action)(() => KeyboardOledEncoder.Encode(new byte[16], Round))).Should().Throw<ArgumentException>();
        ((Action)(() => KeyboardOledEncoder.Encode(Solid(1, 2, 3), "luma-legacy"))).Should().Throw<ArgumentException>();
        ((Action)(() => KeyboardOledEncoder.Encode(Solid(1, 2, 3), Round, new byte[100]))).Should().Throw<ArgumentException>();
        KeyboardDisplayAnimationEncodings.IsKnown(Round).Should().BeTrue();
        KeyboardDisplayAnimationEncodings.IsKnown(null).Should().BeFalse();
    }

    [Fact]
    public void Timeline_positions_map_like_the_case_screens_including_wrap_seek_and_invalid_positions()
    {
        var frames = Frames(600, [0, 100, 300]);
        frames.IndexAt(0).Should().Be(0);
        frames.IndexAt(99.9).Should().Be(0);
        frames.IndexAt(100).Should().Be(1);
        frames.IndexAt(299).Should().Be(1);
        frames.IndexAt(300).Should().Be(2);
        frames.IndexAt(599.99).Should().Be(2);
        frames.IndexAt(600).Should().Be(0, "the loop wraps by the asset duration");
        frames.IndexAt(6_000_150).Should().Be(1, "a seek lands directly on its segment");
        frames.IndexAt(-5).Should().Be(0);
        frames.IndexAt(double.NaN).Should().Be(0);
        frames.IndexAt(double.PositiveInfinity).Should().Be(0);
        Frames(0, [0]).IndexAt(12345).Should().Be(0, "a still image has one frame");

        // Without merging, every position selects the frame the case screens select (StoredAsset.FrameAt of the floored position).
        var asset = new StoredAsset("0123456789abcdef", "t", "gif", 2, 2, [100, 200, 300], 0);
        var exact = Frames(asset.DurationMs, [.. KeyboardOledFramePreparer.SegmentStarts(asset.DelaysMs, 20)]);
        for (var position = -50.0; position < 2000; position += 12.5)
            exact.IndexAt(position).Should().Be(asset.FrameAt(position > 0 ? (long)Math.Floor(position) : 0), $"position {position}");
    }

    [Fact]
    public void Segment_grid_merges_source_frames_shorter_than_the_floor()
    {
        KeyboardOledFramePreparer.SegmentStarts([30, 30, 30, 100, 50], 50).Should().Equal(0, 60, 190);
        KeyboardOledFramePreparer.SegmentStarts([100, 100, 100], 100).Should().Equal(0, 100, 200);
        KeyboardOledFramePreparer.SegmentStarts([500], 100).Should().Equal(0);
        KeyboardOledFramePreparer.SegmentStarts([10, 10, 10, 10], 1000).Should().Equal(0);
    }

    [Fact]
    public async Task Preparation_composes_every_segment_with_the_existing_fit_and_merges_unchanged_frames()
    {
        using var store = await Store.WithAsync(Images.Gif(2, 2, Fill(1), Fill(1), Fill(2)));
        var limits = new KeyboardOledLimits(1 << 20, 100, 30_000);
        var contain = await Sta.RunAsync(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round, limits, default));
        contain.Count.Should().Be(2, "the second red frame is identical to the first");
        (contain.DurationMs, contain.SourceFrames, contain.StartMs(1), contain.Bytes).Should().Be((300, 3, 200, 2L * KeyboardOledFormat.PackedBytes));
        contain.IndexAt(150).Should().Be(0);
        contain.IndexAt(250).Should().Be(1);
        var red = Levels(contain.FrameAt(0));
        (red[Pixel(104, 32)], red[Pixel(0, 0)]).Should().Be(((byte)3, (byte)0), "contain centres the square on black");
        Levels(contain.FrameAt(1))[Pixel(104, 32)].Should().Be(11);

        var cover = await Sta.RunAsync(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "cover", Round, limits, default));
        Levels(cover.FrameAt(0)).Should().OnlyContain(v => v == 3, "cover fills the whole display");
    }

    [Theory]
    [InlineData(Round)]
    [InlineData(Bayer)]
    public async Task Preview_samples_are_exactly_the_prepared_frames_at_their_positions(string encoding)
    {
        using var store = await Store.WithAsync(Images.Gif(2, 2, Fill(1), Fill(2), Fill(3), Fill(0), Fill(1, 20)));
        var frames = await Sta.RunAsync(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "cover", encoding,
            new KeyboardOledLimits(1 << 20, 100, 30_000), default));
        var samples = await Sta.RunAsync(() => KeyboardOledFramePreparer.Sample(store.Assets, store.Id, "cover", encoding, 100, 4, 30_000, default));
        samples.Select(s => s.PositionMs).Should().Equal(0, 100, 200, 300);
        foreach (var sample in samples) sample.Packed.Should().Equal(frames.FrameAt(frames.IndexAt(sample.PositionMs)));
        var one = await Sta.RunAsync(() => KeyboardOledFramePreparer.Sample(store.Assets, store.Id, "cover", encoding, 100, 8, 30_000, default));
        one.Should().HaveCount(5, "never more samples than segments");
    }

    [Fact]
    public async Task Byte_budget_is_refused_before_any_decode_and_nothing_is_returned()
    {
        using var store = await Store.WithAsync(Images.Gif(2, 2, Fill(1), Fill(2), Fill(3)));
        var clock = 0;
        var refused = await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round,
            new KeyboardOledLimits(2 * KeyboardOledFormat.PackedBytes, 100, 30_000), default, () => { clock++; return TimeSpan.Zero; })));
        refused.Should().BeOfType<KeyboardOledPrepareException>().Which.Message.Should().Contain("19968 B").And.Contain("未缓存任何帧");
        clock.Should().Be(0, "the budget is checked before the first frame is composed");
        (await Sta.RunAsync(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round,
            new KeyboardOledLimits(3 * KeyboardOledFormat.PackedBytes, 100, 30_000), default).Count)).Should().Be(3);
        ((Action)(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round, new KeyboardOledLimits(1, 100, 30_000), default)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Time_budget_and_cancellation_stop_between_frames_without_a_partial_result()
    {
        using var store = await Store.WithAsync(Images.Gif(2, 2, Fill(1), Fill(2), Fill(3)));
        var limits = new KeyboardOledLimits(1 << 20, 100, 1000);
        var calls = 0;
        var late = await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round, limits, default,
            () => TimeSpan.FromMilliseconds(calls++ == 0 ? 0 : 1001))));
        late.Should().BeOfType<KeyboardOledPrepareException>().Which.Message.Should().Contain("1000 ms");
        calls.Should().Be(2, "the second frame was refused before it was composed");

        using var cts = new CancellationTokenSource();
        var checks = 0;
        var cancelled = await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round, limits, cts.Token,
            () => { if (++checks == 2) cts.Cancel(); return TimeSpan.Zero; })));
        cancelled.Should().BeAssignableTo<OperationCanceledException>();
        checks.Should().Be(2);
    }

    [Fact]
    public async Task Missing_assets_and_invalid_arguments_are_refused()
    {
        using var store = await Store.WithAsync(Images.Gif(2, 2, Fill(1), Fill(2)));
        var limits = new KeyboardOledLimits(1 << 20, 100, 30_000);
        (await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Prepare(store.Assets, "ffffffffffffffff", "contain", Round, limits, default))))
            .Should().BeOfType<KeyboardOledPrepareException>().Which.Message.Should().Be("所选素材不在素材库中");
        (await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Prepare(store.Assets, "../escape", "contain", Round, limits, default))))
            .Should().BeOfType<ArgumentException>();
        (await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "stretch", Round, limits, default))))
            .Should().BeOfType<ArgumentException>();
        (await Sta.RunAsync(() => Capture(() => KeyboardOledFramePreparer.Sample(store.Assets, store.Id, "contain", Round, 100, 9, 30_000, default))))
            .Should().BeOfType<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_screen_manager_prepares_on_its_own_sta_without_touching_scenes_or_devices()
    {
        var rig = new Rig();
        var media = rig.Manager; // the IKeyboardOledMedia implementation the Host resolves
        var asset = await rig.Manager.UploadScreenAssetAsync(new("two colours", Convert.ToBase64String(Images.Gif(2, 2, Fill(1), Fill(2)))));
        media.CurrentReferences()?.Assignments.Should().BeNullOrEmpty();
        var frames = await media.PrepareKeyboardOledAsync(asset.Id, "cover", Round, new(1 << 20, 100, 30_000), default);
        frames.Count.Should().Be(2);
        var samples = await media.SampleKeyboardOledAsync(asset.Id, "cover", Round, 100, 2, 30_000, default);
        samples.Select(s => s.Packed).Should().BeEquivalentTo([frames.FrameAt(0), frames.FrameAt(1)], o => o.WithStrictOrdering());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ((Func<Task>)(() => media.PrepareKeyboardOledAsync(asset.Id, "cover", Round, new(1 << 20, 100, 30_000), cancelled.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        rig.Opener.Channels.Should().BeEmpty();
        await rig.DisposeAsync();
        await ((Func<Task>)(() => media.PrepareKeyboardOledAsync(asset.Id, "cover", Round, new(1 << 20, 100, 30_000), default)))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    private static GifFrame Fill(byte index, int delayCs = 10) => GifFrame.Fill(0, 0, 2, 2, index, delayCs);

    private static int Pixel(int x, int y) => (y * W) + x;

    private static byte[] Solid(byte r, byte g, byte b)
    {
        var bgra = new byte[KeyboardOledFormat.Pixels * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }

        return bgra;
    }

    private static byte[] GradientWithSquare(int left)
    {
        var bgra = new byte[KeyboardOledFormat.Pixels * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var v = (byte)(x >= left && x < left + 8 && y >= 28 && y < 36 ? 255 : x * 255 / (W - 1));
                var i = Pixel(x, y) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        }

        return bgra;
    }

    private static byte[] Levels(byte[] packed)
    {
        var levels = new byte[KeyboardOledFormat.Pixels];
        KeyboardOledFormat.Unpack(packed, levels);
        return levels;
    }

    private static KeyboardOledFrames Frames(int durationMs, int[] starts) =>
        new("0123456789abcdef", "contain", Round, durationMs, starts.Length, starts, [.. starts.Select(_ => new byte[KeyboardOledFormat.PackedBytes])]);

    private static Exception? Capture(Action work)
    {
        try
        {
            work();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>A throwaway asset store holding one GIF, imported like an upload.</summary>
    private sealed class Store : IDisposable
    {
        private Store(string root, AssetStore assets, string id)
        {
            Root = root;
            Assets = assets;
            Id = id;
        }

        public string Root { get; }

        public AssetStore Assets { get; }

        public string Id { get; }

        public static async Task<Store> WithAsync(byte[] data)
        {
            var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
            var store = new AssetStore(root, 1 << 26, 16, NullLogger.Instance);
            var import = await Sta.RunAsync(() => ImageImporter.Import(data, ImportLimits.Default));
            return new Store(root, store, store.Commit(AssetStore.IdOf(data), "t", import).Id);
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
