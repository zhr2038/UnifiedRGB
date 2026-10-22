using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// A frozen manual clock drives one fake TL worker picture by picture, so the source frame selected for every submission and its
/// exact wire bytes are known in advance. This file uses only pre-diagnostics APIs: it was first run against the unmodified pinned
/// source, then unchanged against the diagnostics build, to show selection, pacing and wire bytes did not change.
/// </summary>
public sealed class SourceFrameWireTests
{
    [Fact]
    public async Task Local_epoch_pictures_and_wire_bytes_follow_the_frozen_clock_exactly()
    {
        await using var s = await SourceStepScenario.StartAsync(timeline: false);
        var shown = new List<int>();
        foreach (var (clock, _) in SourceStepScenario.LocalSteps) shown.AddRange(await s.StepAsync(clock));
        shown.Should().Equal(1, 2, 3, 3, 4, 5, 5, 7, 7, 1, 1, 2, 2, 2, 3, 3, 5, 5, 6, 7, 0, 0);
        shown.Should().Equal(SourceStepScenario.Expected(SourceStepScenario.LocalSteps));
        (await s.Rig.ScreenAsync(Devices.Tl1.Id)).Dropped.Should().Be(0);
    }

    [Fact]
    public async Task Shared_timeline_pictures_and_wire_bytes_follow_the_frozen_position_exactly()
    {
        await using var s = await SourceStepScenario.StartAsync(timeline: true);
        var shown = new List<int>();
        foreach (var (clock, position) in SourceStepScenario.TimelineSteps) shown.AddRange(await s.StepAsync(clock, position));
        shown.Should().Equal(1, 2, 1, 2, 6, 6, 7, 7, 0, 2, 2, 2, 7, 4);
        shown.Should().Equal(SourceStepScenario.Expected(SourceStepScenario.TimelineSteps));
        (await s.Rig.ScreenAsync(Devices.Tl1.Id)).Dropped.Should().Be(0);
    }
}

/// <summary>
/// One TL screen showing an 8-frame GIF with NONuniform delays (loop 1200 ms) at 10 FPS. The clock never moves on its own: each step
/// advances it a whole number of 100 ms periods; one period admits exactly one picture, more periods exactly two (the late picture,
/// then the existing pacing's newest-due-slot picture at the same instant). The fake encoder sends a 15-byte marker of the composed
/// canvas (behind JPEG magic), so each wire payload identifies its source frame exactly.
/// </summary>
internal sealed class SourceStepScenario : IAsyncDisposable
{
    public static readonly int[] DelaysMs = [100, 100, 200, 100, 300, 100, 100, 200];
    public const int LoopMs = 1200;
    public const int PeriodMs = 100;
    public const string TimelineId = "package:source-steps";

    /// <summary>Clock steps (ms) of the local-epoch run; the position is the clock.</summary>
    public static readonly (int Clock, double? Position)[] LocalSteps =
        [(100, null), (100, null), (200, null), (100, null), (300, null), (200, null), (300, null), (100, null), (1200, null),
         (200, null), (400, null), (100, null), (100, null), (200, null)];

    /// <summary>Clock steps with the shared timeline position each step publishes (backward, seek, pause, wrap, clock jump).</summary>
    public static readonly (int Clock, double? Position)[] TimelineSteps =
        [(100, 100), (100, 200), (100, 150), (100, 250), (100, 950), (100, 950), (100, 1050), (100, 1150), (100, 50), (200, 250),
         (100, 350), (100, 1150), (100, 600)];

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    private SourceStepScenario() => Rig = new Rig(null, Rig.AllOutput, _time, Devices.Tl1);

    public Rig Rig { get; }

    public StepTimeline Timeline { get; } = new();

    public ScreenAssignmentDto Assignment { get; private set; } = null!;

    public ManualTime Time => _time;

    public long LocalMs { get; private set; }

    public static async Task<SourceStepScenario> StartAsync(bool timeline)
    {
        var s = new SourceStepScenario();
        try
        {
            var frames = DelaysMs.Select((d, k) => new GifFrame(0, 0, 2, 2, [.. Enumerable.Range(0, 4).Select(q => (byte)((k >> (2 * q)) & 3))], d / 10)).ToArray();
            var asset = await s.Rig.Manager.UploadScreenAssetAsync(new("source steps", Convert.ToBase64String(Images.Gif(2, 2, frames))));
            asset.FrameCount.Should().Be(8); asset.DurationMs.Should().Be(LoopMs);
            s.Rig.Manager.EncoderForTest = static (f, canvas, r, q, i) => new EncodedFrame(ScreenProtocol.PushJpegCommand, Marker(canvas), canvas, "marker");
            if (timeline) s.Rig.Manager.Timeline = s.Timeline;
            s.Assignment = new ScreenAssignmentDto(Devices.Tl1.Id, asset.Id, Fps: 10, TimelineId: timeline ? TimelineId : null);
            await s.Rig.Manager.SetScreenSceneAsync(Images.Scene(s.Assignment));
            s.Rig.Manager.Start();
            try { await Wait.UntilAsync(() => s.Written >= 1, "first picture at the frozen instant"); }
            catch (TimeoutException)
            {
                var screen = await s.Rig.ScreenAsync(Devices.Tl1.Id);
                throw new TimeoutException($"no first picture: {screen.State} {screen.LastError} opens={s.Rig.Opener.Opens}");
            }
            await SettleAsync();
            s.Wire().Should().OnlyContain(i => i == 0, "nothing moves before the clock does");
            return s;
        }
        catch
        {
            await s.DisposeAsync();
            throw;
        }
    }

    public int Written => Rig.Opener.Channels.Sum(c => c.Writes.Count);

    /// <summary>Source index of each wire picture so far, after checking the exact framing and marker payload.</summary>
    public int[] Wire()
    {
        Rig.Opener.Channels.Should().HaveCount(1, "the fake transport never fails, so one channel carries every picture");
        return [.. Rig.Opener.Last.Writes.Select(IndexOf)];
    }

    /// <summary>Advances the clock (and the published position) and returns the source index of every picture this admitted.</summary>
    public async Task<int[]> StepAsync(int clockMs, double? positionMs = null)
    {
        clockMs.Should().BePositive(); (clockMs % PeriodMs).Should().Be(0);
        var before = Written;
        var pictures = clockMs == PeriodMs ? 1 : 2;
        if (positionMs is { } p) Timeline.Sample = new(p, "device-clock");
        LocalMs += clockMs;
        _time.Advance(TimeSpan.FromMilliseconds(clockMs));
        await Wait.UntilAsync(() => Written >= before + pictures, $"{pictures} picture(s) after +{clockMs} ms");
        await SettleAsync();
        Written.Should().Be(before + pictures, "a frozen clock admits no extra picture");
        return Wire()[before..];
    }

    /// <summary>Reference selection, independent of StoredAsset.FrameAt.</summary>
    public static int FrameAt(double positionMs)
    {
        var t = (long)Math.Floor(positionMs) % LoopMs;
        for (var k = 0; k < DelaysMs.Length; k++) { if (t < DelaysMs[k]) return k; t -= DelaysMs[k]; }
        throw new InvalidOperationException("unreachable");
    }

    public static int[] Expected((int Clock, double? Position)[] steps)
    {
        var list = new List<int>();
        long local = 0;
        foreach (var (clock, position) in steps)
        {
            local += clock;
            var index = FrameAt(position ?? local);
            list.Add(index);
            if (clock != PeriodMs) list.Add(index);
        }
        return [.. list];
    }

    /// <summary>JPEG magic (the packet builder checks it) + the RGB of the four quadrant centres of the composed canvas.</summary>
    public static byte[] Marker(PixelFrame canvas)
    {
        var marker = new byte[15];
        Jpeg.CopyTo(marker, 0);
        for (var q = 0; q < 4; q++)
        {
            var x = canvas.Width * ((q & 1) == 0 ? 1 : 3) / 4;
            var y = canvas.Height * ((q & 2) == 0 ? 1 : 3) / 4;
            var i = ((y * canvas.Width) + x) * 4;
            marker[3 + (3 * q)] = canvas.Pixels[i + 2]; marker[4 + (3 * q)] = canvas.Pixels[i + 1]; marker[5 + (3 * q)] = canvas.Pixels[i];
        }
        return marker;
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// The source index a wire picture carries, after checking framing and payload: quadrant colours are unambiguous (WIC decodes
    /// the GIF palette to near-saturated values) and every picture of one source index is byte-identical to its first one.
    /// </summary>
    public int IndexOf(byte[] packet)
    {
        var header = ScreenProtocol.DecryptHeader(ScreenFamily.TlLcd, packet.AsSpan(0, 512));
        header[0].Should().Be(ScreenProtocol.PushJpegCommand);
        var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        size.Should().Be(15);
        var marker = packet.AsSpan(512, size).ToArray();
        marker.AsSpan(0, 3).ToArray().Should().Equal(Jpeg);
        var index = 0;
        for (var q = 0; q < 4; q++)
        {
            var rgb = marker.AsSpan(3 + (3 * q), 3).ToArray();
            rgb.Should().OnlyContain(v => v < 8 || v > 247, $"quadrant colours are unambiguous: {Convert.ToHexString(marker)}");
            var digit = rgb[0] > 128 ? 1 : rgb[1] > 128 ? 2 : rgb[2] > 128 ? 3 : 0;
            rgb.Count(v => v > 128).Should().Be(digit == 0 ? 0 : 1);
            index |= digit << (2 * q);
        }
        _payloads.GetOrAdd(index, marker).Should().Equal(marker, "the same source frame always yields the same wire bytes");
        return index;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _payloads = new();

    /// <summary>The worker waits at most one 100 ms period per loop; with the clock frozen nothing else can be admitted.</summary>
    public static Task SettleAsync() => Task.Delay(250);

    public ValueTask DisposeAsync() => Rig.DisposeAsync();

    internal sealed class StepTimeline : IScreenTimeline
    {
        public volatile ScreenTimelineSample Sample = new(0, "device-clock");

        public ScreenTimelineSample Position(string timelineId) => Sample;
    }
}
