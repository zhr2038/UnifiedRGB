using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

// The keyboard animation and a case screen on ONE real ScenePackageTimeline (real store, binding snapshot with speed 2 + reverse,
// real frame source) whose only time input is a fake render-loop position. The screen side is the real ScreenManager/ScreenWorker
// over the in-memory FakeScreens sink; its per-submission source rows are read through an explicit lab trial. Not covered here:
// a screen-only package, whose apply epoch is a process Stopwatch inside ScenePackageTimeline with no TimeProvider seam.
public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public async Task The_keyboard_reads_the_real_package_timeline_position_cycle_block_and_removal_exactly_as_the_screen_worker()
    {
        var zip = Pkg.Mixed(Pkg.Png());
        var store = new ScenePackageStore(_directory);
        var content = ScenePackageReader.Read(zip);
        // The import pipeline's probe record for the one still screen track (metadata only; the timeline never reads it).
        store.Install(content, zip, [(new ScenePackageMediaRecord("panel", "png", 64, 32, 1, 0), Pkg.Png())]).Created.Should().BeTrue();
        var bindings = new ScenePackageBindingStore(store);
        var snapshot = bindings.Save(content.ContentId,
            [new("strip", BindingEntry.Lighting, "pkg:1", 0, null), new("panel", BindingEntry.Screen, null, null, FakeScreens.Tl.Id)],
            new ScenePackagePlaybackDto(Speed: 2, Reverse: true));
        var id = ScenePackageIds.TimelineIdOf(content.ContentId, snapshot.BindingId);
        var loop = new LoopClock();
        var timeline = new ScenePackageTimeline(new ScenePackageFrameSource(store, bindings), loop.Position);
        timeline.Warm(id).Should().BeNull();
        var frames = new KeyboardOledFrames(AssetA, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 3000, 3, [0, 1000, 2000], FakeKeyboardOledMedia.Bytes(AssetA));

        var screenOptions = new ScreensOptions { Enabled = true, NvmlTelemetry = false, DiscoveryIntervalMs = 250, BackoffInitialMs = 20, BackoffMaxMs = 80 };
        screenOptions.TlLcd.Output = true;
        screenOptions.TlLcd.ControlVerified = true;
        await using var screens = new ScreenManager(screenOptions, Path.Combine(_directory, "screen-host"), new FakeScreens { AcceptOpens = true }.Platform)
        {
            Timeline = timeline,
        };
        screens.Start();
        await screens.SetScreenSceneAsync(new([new(FakeScreens.Tl.Id, BackgroundColor: "#102030", TimelineId: id)]));
        await UntilAsync(() => Screen(screens).State == ScreenStates.Running, "running fake TL screen");
        await screens.BeginScreenRateTrialAsync(new([FakeScreens.Tl.Id], 1, 120_000));

        var media = new FakeKeyboardOledMedia { Scene = new([new("screen:h2:test", AssetA, TimelineId: id)]) };
        var factory = new TapFactory();
        var time = new ManualTimeProvider();
        factory.EntryAcknowledged = () => time.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        var rgb = DispatchProxy.Create<IRgbService, ReadProxy>();
        _rgbProxy = (ReadProxy)rgb;
        var keyboardScreens = KeyboardAnimationScreens.Create(media);
        using var service = new KeyboardDisplayService(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = Admitted() }, rgb,
            keyboardScreens, factory, new ClearGuard(), keyboardMedia: media, sharedTimeline: timeline, time: time);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            await UntilAsync(() => factory.Count >= 1, "post-entry status content");
            factory.EntryLogos.Should().Be(1);
            var status = service.ArtworkPixels.ToArray();
            factory.Image(0).Should().Equal(status);

            // P0: the package has RGB but the render loop has no position for it: both sides are blocked with the same reason.
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA) with { TimelineId = id });
            await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Fallback, "keyboard blocked fallback");
            Animation(service).Detail.Should().Contain(ScenePackageTimeline.RgbNotInPlan);
            (await ScreenAfterAsync(screens)).Error.Should().Be(ScreenBlocked + ScenePackageTimeline.RgbNotInPlan);
            factory.Count.Should().Be(1, "no animation frame without a shared position");
            loop.LastId.Should().Be(id);

            // P1: the render loop is at 1600 ms; speed 2 + reverse map it to 12000 - 3200 = 8799.999999 on both paths.
            loop.At(new(1600, 12000, TimelineSources.DeviceClock));
            await UntilAsync(() => factory.Count >= 2, "first package-timed keyboard frame");
            var p1 = await ScreenAfterAsync(screens);
            p1.Error.Should().BeNull();
            p1.Positions.Should().OnlyContain(p => p == 8799);
            factory.Image(1).Should().Equal(frames.FrameAt(frames.IndexAt(p1.Positions[0])), "the frame for the screen worker's sampled position");
            factory.Image(1).Should().Equal(Fill(0x33));
            frames.IndexAt(1600).Should().NotBe(frames.IndexAt(8799), "the raw render position would have selected another frame");
            await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Playing, "playing on the package timeline");

            // P2: the render loop is frozen while the keyboard gate clock and wall time move: neither side advances on its own.
            var selected = Animation(service).Selected;
            time.Advance(TimeSpan.FromSeconds(10));
            (await ScreenAfterAsync(screens)).Positions.Should().OnlyContain(p => p == 8799);
            factory.Count.Should().Be(2);
            Animation(service).Selected.Should().Be(selected, "no additional clock or guessed phase");

            // P3: a new cycle: 6900 * 2 = 13800 wraps to 1800, reversed to 10199.999999.
            loop.At(new(6900, 12000, TimelineSources.DeviceClock));
            await UntilAsync(() => factory.Count >= 3, "second package-timed keyboard frame");
            var p3 = await ScreenAfterAsync(screens);
            p3.Positions.Should().OnlyContain(p => p == 10199);
            factory.Image(2).Should().Equal(frames.FrameAt(frames.IndexAt(p3.Positions[0])));
            factory.Image(2).Should().Equal(Fill(0x22));
            frames.IndexAt(6900).Should().NotBe(frames.IndexAt(10199));

            // P4: an explicit block reaches both sides with the same reason; the keyboard shows status, not a held frame.
            const string Why = "测试：媒体无法重新安装";
            timeline.Block(id, Why);
            await UntilAsync(() => factory.Count >= 4, "keyboard status page while blocked");
            factory.Image(3).Should().Equal(service.ArtworkPixels.ToArray());
            Animation(service).Detail.Should().Contain("共享时间轴不可用（" + Why + "）");
            (await ScreenAfterAsync(screens)).Error.Should().Be(ScreenBlocked + Why);

            // P5: Warm lifts the block: the screen resumes at once at the same position; the keyboard waits only for its gate.
            timeline.Warm(id).Should().BeNull();
            (await ScreenAfterAsync(screens)).Positions.Should().OnlyContain(p => p == 10199);
            factory.Count.Should().Be(4, "the cadence gate is still closed");
            time.Advance(TimeSpan.FromSeconds(2));
            await UntilAsync(() => factory.Count >= 5, "keyboard frame after the block was lifted");
            factory.Image(4).Should().Equal(Fill(0x22), "the frame for the current shared position, not a guessed later one");

            // P6: removal of the package content reaches both sides.
            timeline.Forget(content.ContentId);
            await UntilAsync(() => factory.Count >= 6, "keyboard status page after removal");
            factory.Image(5).Should().Equal(service.ArtworkPixels.ToArray());
            Animation(service).Detail.Should().Contain("the scene package was removed");
            (await ScreenAfterAsync(screens)).Error.Should().Be(ScreenBlocked + "the scene package was removed");
            (factory.Opens, media.Prepares).Should().Be((1, 1));
        }
        finally { await service.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        ((KeyboardAnimationScreens)(object)keyboardScreens).Mutations.Should().Be(0);
    }

    // ---- helpers ----

    /// <summary>The screen worker's label for blocked strict content (ScreenCompositor.BlockedPrefix, internal to Screens).</summary>
    private const string ScreenBlocked = "BLOCKED: ";

    /// <summary>The render loop's published package position (null: the package RGB is not in the plan).</summary>
    private sealed class LoopClock
    {
        private TimelinePosition? _at;
        private string? _lastId;
        public string? LastId => Volatile.Read(ref _lastId);
        public void At(TimelinePosition? at) => Volatile.Write(ref _at, at);
        public TimelinePosition? Position(string timelineId)
        {
            Volatile.Write(ref _lastId, timelineId);
            return Volatile.Read(ref _at);
        }
    }

    private static ScreenDeviceDto Screen(ScreenManager screens) =>
        screens.ListScreensAsync().GetAwaiter().GetResult().Single(s => s.Id == FakeScreens.Tl.Id);

    private static ScreenSourceFrameDto[] ScreenRows(ScreenManager screens) =>
        [.. screens.GetScreenPerformanceAsync().GetAwaiter().GetResult().Single(p => p.ScreenId == FakeScreens.Tl.Id).SourceFrames ?? []];

    /// <summary>Positions and the label of screen submissions composed entirely after the call (the one in flight is skipped).</summary>
    private static async Task<(long[] Positions, string? Error)> ScreenAfterAsync(ScreenManager screens)
    {
        var mark = ScreenRows(screens).Select(r => r.Sequence).DefaultIfEmpty(0).Max();
        await UntilAsync(() => ScreenRows(screens).Any(r => r.Sequence > mark + 1), "screen submissions after the change");
        var error = Screen(screens).LastError;
        return ([.. ScreenRows(screens).Where(r => r.Sequence > mark + 1).Select(r => r.SourcePositionMs)], error);
    }
}
