using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

// Native idle presentation prototype: control armed without Host artwork, device or custom idle, and the transient popup of
// valid lever actions only. Fakes only (no device, GUI window, RPC or media decode); owner time is virtual (ManualTimeProvider).
// Mock success here is not physical behaviour: the visible stored-animation restoration is Root's physical validation.
public sealed partial class RogNativeMenuSourceTests
{
    private static readonly TimeSpan IdleHold = TimeSpan.FromMilliseconds(KeyboardNativePresentation.InteractionHoldMs);

    // ---- actor: control without artwork ----

    [Fact]
    public async Task NativeIdle_actor_arms_control_without_artwork_and_keeps_genuine_input()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(() => null, default)).Should().BeFalse("no Host artwork is active");
        rig.Io.Arms.Should().Be(1); rig.Actor.ControlArmed.Should().BeTrue(); rig.Actor.ArtworkActive.Should().BeFalse();
        rig.Source.IsPresent.Should().BeTrue("IsPresent reports control availability, not artwork");
        rig.Io.Packets.Should().NotContain(p => p[1] == 0x67, "no 6656-byte upload precedes keeping input");
        rig.Input.Opens.Should().Be(1); rig.Events.OfType<NativeMenuContextBegun>().Should().ContainSingle();

        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1)); await rig.Input.WaitReads(2);
        (await rig.Actor.StepAsync(() => null, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Action.Should().Be(DesiredAction.Toggle);
        rig.Io.Arms.Should().Be(1); rig.Io.Disarms.Should().Be(0); rig.Io.Packets.Should().NotContain(p => p[1] == 0x67);
    }

    [Fact]
    public async Task NativeIdle_actor_ending_host_artwork_disarms_joins_and_rearms_a_fresh_context_without_artwork()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        Func<byte[]?> none = () => null;
        (await rig.Actor.StepAsync(none, default)).Should().BeFalse();
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue("popup artwork is in-epoch artwork of the live context");
        rig.Io.Arms.Should().Be(1); rig.Actor.ArtworkActive.Should().BeTrue(); rig.Io.Rebuilt.Should().Equal(Pixels());

        (await rig.Actor.StepAsync(none, default)).Should().BeFalse();
        rig.Io.Disarms.Should().Be(1); rig.Actor.ControlArmed.Should().BeFalse(); rig.Actor.RearmingForDeviceIdle.Should().BeTrue();
        rig.Input.LiveHandles.Should().Be(0, "the reader joined before the DISARM"); rig.Input.Pending.Should().Be(0);
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => !e.Disconnected && e.Reason.Contains("device idle", StringComparison.Ordinal));
        var images = rig.Io.ImageBegins;

        (await rig.Actor.StepAsync(none, default)).Should().BeFalse();
        rig.Io.Arms.Should().Be(2); rig.Actor.ControlArmed.Should().BeTrue(); rig.Actor.RearmingForDeviceIdle.Should().BeFalse();
        rig.Io.ImageBegins.Should().Be(images, "the fresh context carries no Host artwork: no held, black or status frame");
        rig.Io.ArmWhileInputReading.Should().BeFalse(); rig.Io.MaximumControl.Should().Be(1);
        rig.Events.OfType<NativeMenuContextBegun>().Should().HaveCount(2);

        // A press seen before a fresh genuine neutral in the new context is not an action; after the neutral one is.
        rig.Input.Send(ScopedNotice(1, 2, 1, rig.Io.Epoch, Cookie + 1)); await rig.Input.WaitReads(1);
        (await rig.Actor.StepAsync(none, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Input.Send(ScopedNotice(2, 0, 3, rig.Io.Epoch, Cookie + 1)); rig.Input.Send(ScopedNotice(3, 2, 1, rig.Io.Epoch, Cookie + 1));
        await rig.Input.WaitReads(3);
        (await rig.Actor.StepAsync(none, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle();
        rig.Io.Arms.Should().Be(2); rig.Io.Disarms.Should().Be(1);
    }

    // ---- pure presentation (owner actor calls in order; virtual time) ----

    [Fact]
    public void NativeIdle_presentation_shows_the_logo_once_then_no_host_artwork_and_the_rearm_gap_is_not_a_new_entry()
    {
        using var state = new PresentationState(logo: true);
        var p = state.Presentation;
        p.Native(state.Status, newEntry: true).Should().BeSameAs(state.Logo);
        p.SteppedNative(controlArmed: true, artworkActive: true).Should().BeTrue();
        state.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        p.Native(state.Status).Should().BeSameAs(state.Logo);
        state.Clock.Advance(TimeSpan.FromMilliseconds(1));
        p.Native(state.Status).Should().BeNull("nothing authored: the keyboard's stored animation, never the status page");
        p.SteppedNative(controlArmed: true, artworkActive: false).Should().BeFalse(); // the DISARM tick; re-arming follows
        p.Entered.Should().BeTrue(); p.Shown.Should().Be(KeyboardNativeArtwork.DeviceAnimation);
        state.Clock.Advance(TimeSpan.FromMinutes(5));
        p.Native(state.Status, newEntry: false).Should().BeNull("the fresh ARM without artwork continues the entry: no second logo");
        p.SteppedNative(controlArmed: true, artworkActive: false).Should().BeFalse();
        p.IdleChoice.Should().Be(KeyboardNativeArtwork.DeviceAnimation);
        p.SteppedNative(controlArmed: false, artworkActive: false).Should().BeFalse(); // native mode exit
        p.Native(state.Status, newEntry: true).Should().BeSameAs(state.Logo, "a real re-entry shows the logo once again");
    }

    [Fact]
    public void NativeIdle_presentation_pops_up_only_for_valid_interactions_for_the_action_plus_a_bounded_hold()
    {
        long revision = 0;
        using var state = new PresentationState(logo: false, frame => frame, () => revision);
        var p = state.Presentation;
        var status = state.Status;
        p.Native(status, newEntry: true).Should().BeNull();
        p.SteppedNative(true, false);

        // Background refreshes (new status bytes), a caption alone and an unmatched completion never open it.
        p.Native(KeyboardDisplayServiceTests.Fill(0x0E)).Should().BeNull();
        revision = 7;
        p.Native(status).Should().BeNull("a caption without a valid action never opens the popup");
        revision = 0;
        p.InteractionCompleted();
        p.Native(status).Should().BeNull();

        // A valid centre action: open while its outcome is pending, then exactly the hold after it.
        p.InteractionBegan(awaitsCompletion: true);
        p.Native(status).Should().Equal(status);
        p.SteppedNative(true, true).Should().BeTrue();
        p.Shown.Should().Be(KeyboardNativeArtwork.InteractionStatus);
        state.Clock.Advance(TimeSpan.FromSeconds(10));
        p.Native(status).Should().Equal(status, "the outcome is still pending");
        p.InteractionCompleted();
        state.Clock.Advance(IdleHold - TimeSpan.FromMilliseconds(1));
        var refreshed = KeyboardDisplayServiceTests.Fill(0x0D);
        p.Native(refreshed).Should().Equal(refreshed, "new status content is shown, but it does not prolong the popup");
        state.Clock.Advance(TimeSpan.FromMilliseconds(1));
        p.Native(refreshed).Should().BeNull("the hold ended: back to the stored animation");

        // A browse step: the hold, kept while its caption is shown, never beyond the bound.
        p.InteractionBegan(awaitsCompletion: false);
        revision = 3;
        state.Clock.Advance(IdleHold);
        p.Native(status).Should().NotBeNull("the caption of that step keeps the popup");
        state.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.InteractionMaximumMs) - IdleHold);
        p.Native(status).Should().BeNull("bounded even while a caption stays");
        revision = 0;

        // An outcome that never arrives is bounded too, and a late one cannot reopen it.
        p.InteractionBegan(awaitsCompletion: true);
        state.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.InteractionMaximumMs - 1));
        p.Native(status).Should().NotBeNull();
        state.Clock.Advance(TimeSpan.FromMilliseconds(1));
        p.Native(status).Should().BeNull();
        p.InteractionCompleted();
        p.Native(status).Should().BeNull();
    }

    [Fact]
    public async Task NativeIdle_custom_animation_resumes_at_the_current_shared_position_after_a_popup_and_falls_back_to_the_device()
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNativeIdleCustom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "peripherals"));
        File.WriteAllBytes(Path.Combine(data, "peripherals", KeyboardDisplayAnimation.FileName), KeyboardDisplayServiceTests.AnimationFile(enabled: true));
        var clock = new ManualTimeProvider();
        var media = new FakeKeyboardOledMedia
        {
            Scene = new([new("screen:h2:test", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]),
        };
        var timeline = new FakeSharedTimeline();
        // A 10 s cadence gate, longer than the popup: resuming must neither wait for it nor show the frame held from before.
        using var animation = new KeyboardDisplayAnimation(new KeyboardDisplayOptions { AnimationAdmitted = true, AnimationMinimumIntervalMs = 10_000 },
            data, media, timeline, clock);
        try
        {
            await animation.RefreshAsync(KeyboardAnimationScreens.Create(media), default);
            await Until(() => animation.Status(true, false).CachedFrames == 3);
            var status = KeyboardDisplayServiceTests.Fill(0x0F);
            var p = new KeyboardNativePresentation(animation, null, clock);
            timeline.At(1500);
            p.Native(status, newEntry: true).Should().Equal(KeyboardDisplayServiceTests.Fill(0x22));
            p.SteppedNative(true, true).Should().BeTrue();
            p.IdleChoice.Should().Be(KeyboardNativeArtwork.CustomAnimation);
            animation.Status(true, false).Acknowledged.Should().Be(1);

            p.InteractionBegan(awaitsCompletion: true);
            var reads = timeline.Reads;
            p.Native(status).Should().BeSameAs(status);
            p.SteppedNative(true, true);
            timeline.At(2500);
            clock.Advance(TimeSpan.FromSeconds(1));
            p.Native(status).Should().BeSameAs(status);
            timeline.Reads.Should().Be(reads, "the popup never reads the shared timeline, which keeps its own position");
            p.InteractionCompleted();
            clock.Advance(IdleHold);
            p.Native(status).Should().Equal(KeyboardDisplayServiceTests.Fill(0x33), "the current shared position, not the held 0x22 frame or frame zero");
            p.SteppedNative(true, true);
            animation.Status(true, false).Acknowledged.Should().Be(2, "the popup upload is not counted as an animation frame");

            timeline.Block("paused for the test");
            clock.Advance(TimeSpan.FromSeconds(10));
            p.Native(status).Should().BeNull("an unplayable custom animation falls back to the stored animation, never the status page");
            p.IdleChoice.Should().Be(KeyboardNativeArtwork.DeviceAnimation);
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    // ---- actual keyboard owner with fakes ----

    [Fact]
    public async Task NativeIdle_owner_host_logo_then_device_idle_disarms_restores_and_rearms_without_host_artwork()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available),
            mode: RogNativeMenuCodec.NativeMode, animation: false);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
        await rig.NextTick();
        rig.Control.Arms.Should().Be(1);
        var restores = rig.Control.NativeRestores;
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Control.Disarms == 1 && rig.Control.Arms == 2 && rig.Control.NativeRestores > restores);
        await Task.Delay(400);
        rig.Control.ImageBegins.Should().Be(1, "no status page, held logo or black frame stands in for the stored animation");
        rig.Control.Arms.Should().Be(2, "one fresh context, no ARM/DISARM loop"); rig.Control.Disarms.Should().Be(1);
        rig.Control.RejectedImages.Should().Be(0); rig.Source!.IsPresent.Should().BeTrue();
        rig.Events.OfType<NativeMenuContextBegun>().Should().HaveCount(2);
        rig.Timeline.Reads.Should().Be(0);
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("待机：键盘动画偏好为关，请求键盘自带原动画")
            .And.Contain("主机不能确认其已重新加载或可见").And.NotContain("原动画已恢复");
        (await rig.Owner.GetKeyboardDisplayAnimationAsync()).Detail.Should().StartWith("自定义键盘动画未开启");

        // The display seam alone (as the native-menu owner calls it) pops up the status in the live context, then re-arms.
        rig.Owner.NativeInteractionBegan(awaitsCompletion: true);
        await Until(() => rig.Control.ImageBegins > 1 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        rig.Control.Arms.Should().Be(2, "the popup is in-epoch artwork of the live context");
        rig.Owner.NativeInteractionCompleted();
        rig.Clock.Advance(IdleHold);
        await Until(() => rig.Control.Disarms == 2 && rig.Control.Arms == 3);
        var images = rig.Control.ImageBegins;
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(images);
    }

    [Fact]
    public async Task NativeIdle_valid_centre_press_pops_up_status_then_device_idle_rearms_without_host_artwork()
    {
        await using var rig = new NativeIdleMenuRig(sceneCycle: false);
        await rig.StartAsync();
        await rig.NextTick(); await rig.NextTick();
        rig.Control.ImageBegins.Should().Be(0, "device Logo and device idle: control is armed without any Host upload");
        rig.Source.IsPresent.Should().BeTrue();
        var restores = rig.Control.NativeRestores;

        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1));
        await Until(() => rig.Menu.Trace.Contains("press #2 Toggle accepted"));
        await Until(() => rig.Menu.Trace.Any(e => e.StartsWith("artwork #2", StringComparison.Ordinal)));
        await Until(() => rig.Control.ImageBegins >= 1 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        rig.Control.Arms.Should().Be(1, "the popup is in-epoch artwork of the live context"); rig.Control.Disarms.Should().Be(0);

        rig.Clock.Advance(IdleHold - TimeSpan.FromMilliseconds(1));
        await rig.NextTick(); await rig.NextTick();
        rig.Control.Disarms.Should().Be(0, "the brief hold after the outcome is not over");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Control.Disarms == 1 && rig.Control.Arms == 2 && rig.Control.NativeRestores > restores);
        var images = rig.Control.ImageBegins;
        await Task.Delay(400);
        rig.Control.ImageBegins.Should().Be(images, "background status refreshes never reopen the popup");
        rig.Control.Arms.Should().Be(2); rig.Control.RejectedImages.Should().Be(0);

        // The fresh context keeps genuine input: a new neutral, then a press, pops up again.
        rig.Input.Send(ScopedNotice(1, 0, 3, rig.Control.Epoch, Cookie + 1)); rig.Input.Send(ScopedNotice(2, 2, 1, rig.Control.Epoch, Cookie + 1));
        await Until(() => rig.Menu.Trace.Count(e => e == "press #2 Toggle accepted") == 2);
        await Until(() => rig.Control.ImageBegins > images);
    }

    [Fact]
    public async Task NativeIdle_valid_browse_step_pops_up_briefly_then_device_idle_rearms()
    {
        await using var rig = new NativeIdleMenuRig(sceneCycle: true);
        await rig.StartAsync();
        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 1, 1));
        await Until(() => rig.Menu.Trace.Contains("browse #2 Previous accepted"));
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        rig.Control.Arms.Should().Be(1); rig.Control.Disarms.Should().Be(0);
        rig.Clock.Advance(IdleHold);
        await Until(() => rig.Control.Disarms == 1 && rig.Control.Arms == 2);
        var images = rig.Control.ImageBegins;
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(images);
        rig.Menu.Trace.Should().NotContain(e => e.StartsWith("press", StringComparison.Ordinal), "a browse step is never a case action");
    }

    private sealed class NativeIdleMenuRig : IAsyncDisposable
    {
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "SolNativeIdleMenu-" + Guid.NewGuid().ToString("N"));
        public SourceClock Time { get; } = new();
        public ManualTimeProvider Clock { get; } = new();
        public Input Input { get; }
        public Connection Control { get; }
        public RogNativeMenuSource Source { get; }
        public MenuHostFake Host { get; } = new(new([new([new("lianli:1", 0), new("lianli:1", 1)], "Rainbow", ["#FF0000", "#00FF00"])]));
        public KeyboardSceneCycleOverlay Overlay { get; } = new();
        public KeyboardSceneCycleBridge? Scenes { get; }
        public KeyboardDisplayService Owner { get; }
        public NativeMenuService Menu { get; }

        public NativeIdleMenuRig(bool sceneCycle)
        {
            Directory.CreateDirectory(Path.Combine(Data, "peripherals"));
            Input = new(Time, 1);
            Control = new(Time, 1) { Input = Input, Mode = RogNativeMenuCodec.NativeMode };
            ulong cookie = Cookie;
            Source = new(Policy(), Input, Input, Time, () => cookie++);
            var options = new UnifiedRgbOptions
            {
                DataDirectory = Data,
                KeyboardDisplay = new() { Enabled = false, ControlVerified = true, LightingVerified = true,
                    DeviceKey = Control.DeviceKey, FirmwareHeader = Control.FirmwareHeader, DeviceShowsNativeEntryLogo = true },
            };
            Owner = new(options, Host.Connection.Rgb, Host.Connection.Screens!, new LegacyPageFactory(Control, false), new MutationClearGuard(),
                new RogWiredLightingBridge(), nativeMenu: Source, time: Clock,
                nativePresentation: new DeclaredPresentation(KeyboardNativePresentationSupport.Available),
                nativeFrameOverlay: Overlay.Apply, nativeFrameAcknowledged: Overlay.Acknowledge, nativeFrameOverlayRevision: () => Overlay.Revision);
            // Its own never-advanced clock: the blocked list read never times out into a rendered caption.
            if (sceneCycle) Scenes = new(options, () => Host.Connection.Rgb, NativeIdlePackages.Pending, Overlay, new ManualTimeProvider());
            Menu = new(options, Host.Connection.Rgb, Host.Connection.Screens, Source, Owner, time: Time, sceneCycle: Scenes);
        }

        public bool Shows(byte[] pixels) => Control.Rebuilt.AsSpan().SequenceEqual(pixels);

        public async Task StartAsync()
        {
            await Menu.StartAsync(default);
            await Menu.SetNativeMenuEnabledAsync(true);
            await Owner.StartAsync(default);
            await Until(() => Control.Arms == 1 && Menu.Trace.Any(e => e.StartsWith("armed context", StringComparison.Ordinal)));
        }

        public async Task NextTick()
        {
            var writes = Control.Writes.Count;
            await Until(() => Control.Writes.Count > writes);
        }

        public async ValueTask DisposeAsync()
        {
            await Menu.DisposeAsync();
            if (Scenes is not null) await Scenes.DisposeAsync();
            await Owner.StopAsync(default); Owner.Dispose(); Input.Dispose(); Control.Dispose();
            if (Directory.Exists(Data)) Directory.Delete(Data, true);
        }
    }
}

/// <summary>Scene packages whose list never answers until the bridge stops: a queued browse step stays in its list read.</summary>
public class NativeIdlePackages : DispatchProxy
{
    internal static IScenePackageService Pending() => DispatchProxy.Create<IScenePackageService, NativeIdlePackages>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(IScenePackageService.ListScenePackagesAsync) && args is [CancellationToken ct]
            ? PendingAsync(ct)
            : throw new InvalidOperationException("No other scene-package call is expected: " + targetMethod?.Name);

    private static async Task<IReadOnlyList<ScenePackageInfoDto>> PendingAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return [];
    }
}
