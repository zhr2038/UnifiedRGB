using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// OPUS-GENERIC-SCENE-PACKAGES: screens with a TimelineId sample the shared package timeline (fake USB only), and a 200 × 60 ms
/// animation's frame index at any position equals the RGB frame index floor(position / 60). Strict content that cannot play (a
/// blocked/unknown timeline or missing package media) is sent as an opaque black frame without overlay, labelled BLOCKED.
/// </summary>
public sealed class ScreenTimelineTests
{
    private static readonly string Tl = Devices.Tl1.Id;
    private const string Id = "package:aa:bb";

    private sealed class FakeTimeline : IScreenTimeline
    {
        public volatile ScreenTimelineSample? Next;

        public int Calls;

        public ScreenTimelineSample? Position(string timelineId)
        {
            timelineId.Should().Be(Id);
            Interlocked.Increment(ref Calls);
            return Next;
        }
    }

    [Fact]
    public void A_200_by_60ms_asset_shows_the_same_frame_index_as_the_RGB_at_every_position()
    {
        var asset = new StoredAsset("a", "n", "gif", 4, 4, Enumerable.Repeat(60, 200).ToArray(), 0);
        asset.DurationMs.Should().Be(12000);
        for (var ms = 0; ms < 24000; ms++)
        {
            asset.FrameAt(ms).Should().Be(ms % 12000 / 60);
        }
    }

    private sealed class HoldingRecovery : IFanLcdGroupRecovery
    {
        public Func<Task>? During { get; set; }

        public void Validate(string controllerId, int lcdFans)
        {
        }

        public async Task<FanLcdGroupResetResult> ResetAsync(string controllerId, int lcdFans, Action beforeFirstSend, CancellationToken ct)
        {
            beforeFirstSend();
            if (During is { } during)
            {
                await during();
            }

            return new FanLcdGroupResetResult(true, 1, "acknowledged");
        }
    }

    /// <summary>The image of the newest packet the screen's newest channel received, decoded.</summary>
    private static async Task<PixelFrame> LastSentAsync(Rig rig, string id, ScreenFamily family)
    {
        var packet = rig.Opener.For(id)[^1].Writes.Last();
        var length = BinaryPrimitives.ReadInt32BigEndian(ScreenProtocol.DecryptHeader(family, packet.AsSpan(0, 512)).AsSpan(8));
        return await Sta.RunAsync(() => HeadlessProbe.Decode(packet.AsSpan(512, length).ToArray()));
    }

    private static int Brightest(PixelFrame f)
    {
        var max = 0;
        for (var y = 0; y < f.Height; y += 4)
        {
            for (var x = 0; x < f.Width; x += 4)
            {
                var (b, g, r, _) = f.At(x, y);
                max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
            }
        }

        return max;
    }

    [Fact]
    public async Task A_worker_uses_the_shared_timeline_label_and_sends_black_BLOCKED_frames_without_overlay_when_blocked_or_unknown()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var timeline = new FakeTimeline { Next = new ScreenTimelineSample(1234, "upload-anchor: test") };
        rig.Manager.Timeline = timeline;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, BackgroundColor: "#FFFFFF", Overlay: "compact", Fps: 10, TimelineId: Id)));
        rig.Manager.Start();

        await Wait.UntilAsync(() => rig.Manager.TimelineSourceOf(Tl) == "upload-anchor: test" && timeline.Calls > 0, "timeline label");
        await Wait.UntilAsync(() => rig.Opener.For(Tl) is [.., { Writes.IsEmpty: false }], "a playing frame");
        Brightest(await LastSentAsync(rig, Tl, ScreenFamily.TlLcd)).Should().BeGreaterThan(200, "the playing white background reaches the device");

        timeline.Next = new ScreenTimelineSample(0, "blocked", "package missing");
        await Wait.UntilAsync(() => rig.Manager.TimelineSourceOf(Tl) == "blocked: package missing", "blocked label");
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).LastError == ScreenCompositor.BlockedPrefix + "package missing", "BLOCKED note");
        Brightest(await LastSentAsync(rig, Tl, ScreenFamily.TlLcd)).Should().BeLessThan(8,
            "blocked strict content is black: no held content frame, no background colour, no telemetry overlay");

        timeline.Next = null;
        await Wait.UntilAsync(() => rig.Manager.TimelineSourceOf(Tl) == "unavailable", "unknown timeline label");
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).LastError?.StartsWith(ScreenCompositor.BlockedPrefix + "shared timeline", StringComparison.Ordinal) == true,
            "BLOCKED note for an unknown timeline");
        Brightest(await LastSentAsync(rig, Tl, ScreenFamily.TlLcd)).Should().BeLessThan(8, "an unknown timeline never falls back to a local start");

        timeline.Next = new ScreenTimelineSample(10, "device-clock");
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).LastError?.StartsWith(ScreenCompositor.BlockedPrefix, StringComparison.Ordinal) != true,
            "playing again once the timeline resolves");
        await Wait.UntilAsync(async () => Brightest(await LastSentAsync(rig, Tl, ScreenFamily.TlLcd)) > 200, "white content again");

        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, Overlay: "compact", Fps: 10)));
        await Wait.UntilAsync(() => rig.Manager.TimelineSourceOf(Tl) is null, "no timeline id: historical local start");
        var calls = timeline.Calls;
        await Task.Delay(400);
        timeline.Calls.Should().Be(calls, "an assignment without a TimelineId never reads the shared timeline");
    }

    [Fact]
    public async Task Strict_content_with_missing_media_is_black_and_BLOCKED_while_historical_content_keeps_its_background()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        var store = new AssetStore(root, 1 << 26, 16, NullLogger.Instance);
        var historical = new ScreenAssignmentDto("screen:h2:a", "0123456789abcdef", "#00FF00");
        var strict = historical with { Overlay = "compact", TimelineId = Id };
        var (old, blocked) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store, 0);
            return (c.Compose(ScreenFamilyInfo.H2, historical, 0, null, DateTimeOffset.UtcNow), c.Compose(ScreenFamilyInfo.H2, strict, 0, null, DateTimeOffset.UtcNow));
        });

        old.Warning.Should().Contain("missing; background colour only", "historical behaviour is unchanged");
        old.Canvas.At(1, 1).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        blocked.Warning.Should().StartWith(ScreenCompositor.BlockedPrefix).And.Contain("0123456789abcdef");
        (blocked.Canvas.Width, blocked.Canvas.Height).Should().Be((480, 480));
        Brightest(blocked.Canvas).Should().Be(0, "no background substitute and no overlay for strict content");
        blocked.Canvas.At(240, 240).A.Should().Be(255, "opaque");

        foreach (var family in new[] { ScreenFamilyInfo.TlLcd, ScreenFamilyInfo.H2, ScreenFamilyInfo.Turzx })
        {
            var r = ScreenCompositor.ComposeBlocked(family, "why");
            (r.Canvas.Width, r.Canvas.Height, r.Warning).Should().Be((family.Width, family.Height, "BLOCKED: why"));
        }
    }

    [Fact]
    public async Task A_fan_LCD_restart_hold_is_distinguished_from_a_StopScreens_pause()
    {
        var recovery = new HoldingRecovery();
        await using var rig = new Rig(recovery, o =>
        {
            Rig.AllOutput(o);
            o.FanLcdRestart = new FanLcdRestartOptions { Enabled = true, ControllerId = "lianli:rf:02-00-00-00-00-04", ScreenIds = [Tl] };
        }, Devices.Tl1);
        rig.Manager.RestartSettle = TimeSpan.FromMilliseconds(20);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, Fps: 5)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.For(Tl) is [{ Writes.IsEmpty: false }], "sending");

        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Stopped, "paused");
        rig.Manager.RestartHolds(Tl).Should().BeFalse("a StopScreens pause is not a restart hold");

        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, Fps: 5)));
        bool? heldDuring = null;
        string? stateDuring = null;
        recovery.During = async () =>
        {
            heldDuring = rig.Manager.RestartHolds(Tl);
            stateDuring = (await rig.ScreenAsync(Tl)).State;
        };
        (await rig.Manager.RestartFanLcdAsync()).CommandAccepted.Should().BeTrue();
        (heldDuring, stateDuring).Should().Be((true, ScreenStates.Stopped));
        rig.Manager.RestartHolds(Tl).Should().BeFalse("released after the restart");
    }

    [Fact]
    public async Task An_incomplete_normalized_cache_is_skipped_at_load_and_replaced_by_a_fresh_normalization()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var png = await Images.PngAsync(Images.Solid(8, 8, 1, 2, 3));
            var id = AssetStore.IdOf(png);
            var import = await Sta.RunAsync(() => ImageImporter.Import(png, ImportLimits.Default));
            new AssetStore(root, 1 << 26, 16, NullLogger.Instance).Commit(id, "p", import);
            File.Delete(Path.Combine(root, id, "f000.png"));

            var reloaded = new AssetStore(root, 1 << 26, 16, NullLogger.Instance);
            reloaded.Find(id).Should().BeNull("an incomplete normalized cache is never indexed");
            var rebuilt = reloaded.Commit(id, "p", import);
            reloaded.ReadFrame(rebuilt, 0).Should().Equal(import.FramePngs[0]);
            new AssetStore(root, 1 << 26, 16, NullLogger.Instance).Find(id).Should().NotBeNull("the rebuilt cache loads after a restart");
            Directory.GetDirectories(root).Select(Path.GetFileName).Should().Equal(id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Pinned_package_assets_cannot_be_deleted_generically()
    {
        await using var rig = new Rig(null);
        var png = await Images.PngAsync(Images.Solid(8, 8, 1, 2, 3));
        var asset = await rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("p", Convert.ToBase64String(png)));
        rig.Manager.AssetPinned = id => id == asset.Id;
        var delete = () => rig.Manager.DeleteScreenAssetAsync(asset.Id);
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*scene package*");
        (await rig.Manager.ListScreenAssetsAsync()).Should().ContainSingle(a => a.Id == asset.Id);

        rig.Manager.AssetPinned = null;
        await rig.Manager.DeleteScreenAssetAsync(asset.Id);
        (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Package_media_probe_and_install_open_no_device_and_install_is_idempotent()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var png = await Images.PngAsync(Images.Solid(64, 32, 9, 8, 7));
        var info = await rig.Manager.ProbeMediaAsync(png);
        (info.Width, info.Height, info.Frames, info.DurationMs).Should().Be((64, 32, 1, 0));
        info.ThumbnailPng.Should().NotBeEmpty();
        (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty("probing stores nothing");

        var a = await rig.Manager.InstallPackageAssetAsync("pkg panel", png);
        (await rig.Manager.InstallPackageAssetAsync("pkg panel", png)).Should().Be(a);
        (await rig.Manager.ListScreenAssetsAsync()).Should().ContainSingle();
        rig.Opener.Opens.Should().Be(0);
        var bad = () => rig.Manager.ProbeMediaAsync([1, 2, 3]);
        await bad.Should().ThrowAsync<ArgumentException>();
    }
}
