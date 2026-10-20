using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class ScreenAnimationCacheTests
{
    [Fact]
    public void Oversized_cycles_keep_a_bounded_useful_prefix_and_clear_releases_it()
    {
        var cache = new ScreenAnimationCache(7, 2);
        var first = new ScreenImage(101, [1, 2, 3], "first");
        cache.Add("a", 0, first);
        cache.Add("a", 0, new(101, [9], "duplicate"));
        cache.Add("a", 1, new(101, [4, 5, 6, 7], "second"));
        for (var i = 2; i < 1000; i++) cache.Add("a", i, new(101, [8], "too much"));
        cache.Find("a", 0).Should().BeSameAs(first);
        cache.Find("other", 0).Should().BeNull();
        (cache.Bytes, cache.Count).Should().Be((7, 2));
        cache.Clear();
        (cache.Bytes, cache.Count).Should().Be((0, 0));
        cache.Find("a", 0).Should().BeNull();
        cache.Add("b", 0, first);
        cache.Find("b", 0).Should().BeSameAs(first);
        var entries = new ScreenAnimationCache(100, 1);
        entries.Add("a", 0, first); entries.Add("a", 1, first);
        entries.Count.Should().Be(1);
    }

    private sealed class Timeline : IScreenTimeline
    {
        public volatile ScreenTimelineSample Sample = new(0, "device-clock");
        public ScreenTimelineSample Position(string id) => Sample;
    }

    [Fact]
    public async Task Cached_wire_images_follow_shared_time_and_never_hold_over_a_block_or_assignment_change()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var timeline = new Timeline();
        rig.Manager.Timeline = timeline;
        var gif = Images.Gif(2, 2, GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10), GifFrame.Fill(0, 0, 2, 2, 2, delayCs: 10));
        var asset = await rig.Manager.UploadScreenAssetAsync(new("two colours", Convert.ToBase64String(gif)));
        var encodes = 0;
        rig.Manager.EncoderForTest = (f, c, r, q, i) =>
        {
            Interlocked.Increment(ref encodes);
            return ScreenFrameEncoder.Encode(f, c, r, q, i);
        };
        var a = new ScreenAssignmentDto(Devices.Tl1.Id, asset.Id, Fps: 15, TimelineId: "package:test");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(a));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Channels.Any(c => c.Writes.Count >= 4), "repeated cached frame");
        Volatile.Read(ref encodes).Should().Be(1);
        var first = Image(rig.Opener.Last.Writes.Last());
        timeline.Sample = new(150, "device-clock");
        await Wait.UntilAsync(() => Volatile.Read(ref encodes) >= 2, "second source frame");
        await Wait.UntilAsync(() => !Image(rig.Opener.Last.Writes.Last()).SequenceEqual(first), "green wire frame");
        timeline.Sample = new(0, "blocked", "source unavailable");
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Tl1.Id)).LastError == ScreenCompositor.BlockedPrefix + "source unavailable", "blocked black");
        var black = Image(rig.Opener.Last.Writes.Last());
        black.Should().NotEqual(first);
        var before = Volatile.Read(ref encodes);
        timeline.Sample = new(0, "device-clock");
        await Wait.UntilAsync(() => Image(rig.Opener.Last.Writes.Last()).SequenceEqual(first), "original cached red resumes");
        Volatile.Read(ref encodes).Should().Be(before, "a blocked timeline invalidates eligibility, not the immutable content");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(a with { Brightness = 0.25 }));
        await Wait.UntilAsync(() => Volatile.Read(ref encodes) > before, "brightness re-encoded");
        await Wait.UntilAsync(() => !Image(rig.Opener.Last.Writes.Last()).SequenceEqual(first), "new brightness wire bytes");
        var dark = Image(rig.Opener.Last.Writes.Last());
        before = Volatile.Read(ref encodes);
        await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Tl1.Id, 0, "lossless-first-v1"));
        await Wait.UntilAsync(() => Volatile.Read(ref encodes) > before, "a changed local policy invalidates cached bytes");
        Image(rig.Opener.Last.Writes.Last()).Should().Equal(dark, "JPEG's existing byte output is unchanged by the PNG policy");
        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Tl1.Id)).State == ScreenStates.Stopped, "pause still closes the transport");
        var stoppedWrites = rig.Opener.Channels.Sum(c => c.Writes.Count);
        await Task.Delay(150);
        rig.Opener.Channels.Sum(c => c.Writes.Count).Should().Be(stoppedWrites);
    }

    [Fact]
    public async Task A_live_overlay_bypasses_the_animation_cache()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var gif = Images.Gif(2, 2, GifFrame.Fill(0, 0, 2, 2, 1), GifFrame.Fill(0, 0, 2, 2, 2));
        var asset = await rig.Manager.UploadScreenAssetAsync(new("overlay", Convert.ToBase64String(gif)));
        var encodes = 0;
        rig.Manager.EncoderForTest = (f, c, r, q, i) =>
        {
            Interlocked.Increment(ref encodes);
            return ScreenFrameEncoder.Encode(f, c, r, q, i);
        };
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Tl1.Id, asset.Id, Overlay: "compact", Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref encodes) >= 4, "each live telemetry frame is re-encoded");
    }

    private static byte[] Image(byte[] packet)
    {
        var size = BinaryPrimitives.ReadInt32BigEndian(ScreenProtocol.DecryptHeader(ScreenFamily.TlLcd, packet.AsSpan(0, 512)).AsSpan(8));
        return packet.AsSpan(512, size).ToArray();
    }
}
