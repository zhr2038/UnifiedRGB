using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

[Collection("Codex")]
public sealed class KeyboardCodexPresentationTests
{
    [Fact]
    public void Entry_hold_tracks_actual_page_not_ARM_recovery_poll_or_acknowledgement()
    {
        using var rig = new Rig(); var hold = new CodexEntryHold(rig.Time); var quota = Fill(0x77);
        KeyboardNativePresentation Presenter() => new(rig.Animation, rig.Logo, rig.Time, idleFrame: () => quota, codexEntry: hold);
        var presenter = Presenter(); var cap = new RogNativeCapability(0, 10, 0, 1, 0x70, 1, 0, 0);
        presenter.NativePage(cap, rig.Status, true).Should().BeNull();
        presenter.Acknowledge().Should().BeFalse(); presenter.Shown.Should().NotBe(KeyboardNativeArtwork.Logo);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1199));
        presenter.NativePage(cap, rig.Status, true).Should().BeNull();
        presenter.Stepped(false); presenter = Presenter(); // same page, replacement owner shares physical history
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        presenter.NativePage(cap, rig.Status, true).Should().BeSameAs(quota);
        for (var i = 0; i < 3; i++) presenter.NativePage(cap, rig.Status, true).Should().BeSameAs(quota);
        hold.Observe(3); // verified stock-page exit has no artwork callback
        rig.Time.Advance(TimeSpan.FromSeconds(2)); hold.Observe(10);
        presenter.NativePage(cap, rig.Status).Should().BeNull();
        rig.Time.Advance(TimeSpan.FromMilliseconds(1200)); presenter.NativePage(cap, rig.Status).Should().BeSameAs(quota);
    }

    [Fact]
    public async Task Dedicated_page_ignores_logo_popup_overlay_and_blackout_then_restores_current_phase_Guangyu()
    {
        using var rig = new Rig(); await rig.Prepare();
        var quota = Fill(0x77); byte[]? cached = quota; var blackout = true; var overlays = 0;
        var presenter = new KeyboardNativePresentation(rig.Animation, rig.Logo, rig.Time,
            overlay: pixels => { overlays++; return pixels; }, caseBlackout: () => blackout, idleFrame: () => cached);
        var page = new RogNativeCapability(0, 10, 0, 1, 0x70, 1, 0, 0);
        presenter.InteractionBegan(true);
        presenter.NativePage(page, rig.Status).Should().BeNull("firmware owns the entry asset; no duplicate Host logo");
        rig.Time.Advance(TimeSpan.FromMilliseconds(1200));
        presenter.NativePage(page, rig.Status).Should().BeSameAs(quota);
        presenter.Acknowledge().Should().BeFalse();
        presenter.Shown.Should().Be(KeyboardNativeArtwork.CodexQuota);
        presenter.IdleChoice.Should().Be(KeyboardNativeArtwork.CodexQuota);
        rig.Animation.Status(true, false).Acknowledged.Should().Be(0);
        rig.Timeline.Reads.Should().Be(0, "quota never samples the animation or acknowledges an animation frame");
        presenter.InteractionBegan(false); // even a delayed notification cannot put a Guangyu popup in mode10
        for (var i = 0; i < 2; i++)
        {
            cached = Fill((byte)(0x80 + i));
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            presenter.NativePage(page, rig.Status).Should().BeSameAs(cached);
        }
        overlays.Should().Be(0);
        presenter.NativePage(page with { Flags = 0x30 }, rig.Status).Should().BeNull("mode10 needs the descriptor, not a build tag");
        page = page with { Mode = 9 };
        presenter.NativePage(page, rig.Status).Should().BeSameAs(rig.Logo);
        presenter.Acknowledge(); rig.Time.Advance(TimeSpan.FromMilliseconds(1200));
        presenter.NativePage(page, rig.Status).Should().BeNull("mode9 retains confirmed blackout policy, even with quota enabled");
        presenter.IdleChoice.Should().Be(KeyboardNativeArtwork.DeviceAnimation);
        blackout = false;
        rig.Timeline.At(2500);
        presenter.NativePage(page, rig.Status).Should().Equal(Fill(0x33));
        presenter.Acknowledge().Should().BeTrue();
        rig.Animation.Status(true, false).Acknowledged.Should().Be(1);
        presenter.IdleChoice.Should().Be(KeyboardNativeArtwork.CustomAnimation);
    }

    [Fact]
    public async Task Ordinary_and_mode9_never_select_quota_even_when_preference_is_enabled()
    {
        using var rig = new Rig(); await rig.Prepare(); byte[]? cached = null;
        var presenter = new KeyboardNativePresentation(rig.Animation, null, rig.Time, idleFrame: () => cached);
        presenter.Latest(rig.Status, true).Should().Equal(Fill(0x11));
        presenter.Acknowledge().Should().BeTrue();
        cached = Fill(0x77);
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        presenter.Latest(rig.Status, true).Should().Equal(Fill(0x11));
        presenter.Acknowledge().Should().BeFalse();
        rig.Animation.Status(true, false).Acknowledged.Should().Be(1);
        rig.Timeline.At(2500); rig.Time.Advance(TimeSpan.FromSeconds(2));
        presenter.Native(rig.Status).Should().Equal(Fill(0x33));
        presenter.Acknowledge().Should().BeTrue();
        rig.Animation.Status(true, false).Acknowledged.Should().Be(2);
    }

    [Fact]
    public void Ordinary_page_off_never_selects_quota_and_bad_callback_keeps_existing_status()
    {
        using var rig = new Rig();
        var presenter = new KeyboardNativePresentation(rig.Animation, null, rig.Time, idleFrame: () => Fill(0x88));
        presenter.Latest(rig.Status, false).Should().BeSameAs(rig.Status);
        var invalid = new KeyboardNativePresentation(rig.Animation, null, rig.Time, idleFrame: () => new byte[2]);
        invalid.Latest(rig.Status, true).Should().BeSameAs(rig.Status);
        var throwing = new KeyboardNativePresentation(rig.Animation, null, rig.Time, idleFrame: () => throw new IOException("fake"));
        throwing.Latest(rig.Status, true).Should().BeSameAs(rig.Status);
    }

    [Fact]
    public void Actual_Wpf_packing_boundary_places_even_pixel_in_low_nibble()
    {
        var bgra = new byte[208 * 64 * 4];
        bgra[0] = bgra[1] = bgra[2] = 17;
        bgra[4] = bgra[5] = bgra[6] = 238;
        var packed = KeyboardStatusArtwork.PackBgra(bgra);
        packed.Length.Should().Be(6656); packed[0].Should().Be(0xE1); packed[1].Should().Be(0);
    }

    [Fact]
    public void Single_window_shows_percentage_in_bar_and_reset_time_without_an_extra_heading()
    {
        var status = KeyboardCodexProviderTests.Ready(new ManualTimeProvider());
        var packed = KeyboardCodexArtwork.Render(status);
        var levels = new byte[KeyboardOledFormat.Pixels]; KeyboardOledFormat.Unpack(packed, levels);
        levels[10 * 208 + 10].Should().Be(15, "the remaining portion fills the progress bar");
        levels[10 * 208 + 200].Should().BeInRange((byte)1, (byte)14);
        levels.Skip(8 * 208).Take(20 * 208).Should().Contain(p => p == 0, "the percentage remains readable against the filled bar");
        levels.Skip(43 * 208).Should().Contain(p => p != 0, "the reset time appears below the bar");
        levels.Take(4 * 208).Should().OnlyContain(p => p == 0, "no heading or success timestamp is added above the bar");
        KeyboardCodexArtwork.Render(status with { Primary = null, Secondary = status.Primary }).Should().Equal(packed,
            "a single secondary window gets the same readable layout as a single primary window");
    }

    [Fact]
    public void Off_clock_artwork_is_gray4_stable_without_sample_time_or_second_countdown()
    {
        var status = KeyboardCodexProviderTests.Ready(new ManualTimeProvider());
        var first = KeyboardCodexArtwork.Render(status);
        first.Should().HaveCount(6656); first.Should().Contain(b => b != 0);
        KeyboardCodexArtwork.Render(status with { LastSuccess = DateTimeOffset.UtcNow }).Should().Equal(first);
        KeyboardCodexArtwork.Render(status with { State = "Stale" }).Should().NotEqual(first);
        KeyboardCodexArtwork.Render(status with { Primary = status.Primary! with { Label = "5小时", DurationMinutes = 300 } }).Should().Equal(first,
            "the requested single-window display contains only percentage/bar and reset time");
        var levels = new byte[KeyboardOledFormat.Pixels]; KeyboardOledFormat.Unpack(first, levels);
        for (var i = 0; i < first.Length; i++)
        {
            levels[2 * i].Should().Be((byte)(first[i] & 15));
            levels[2 * i + 1].Should().Be((byte)(first[i] >> 4));
        }
    }

    [Fact]
    public async Task Service_endpoints_forward_only_the_sanitized_cache_and_preference_not_hardware()
    {
        var data = CodexTestData.Directory(); var calls = new List<bool>();
        var status = KeyboardCodexProviderTests.Ready(new ManualTimeProvider());
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
        using var service = new KeyboardDisplayService(new() { DataDirectory = data, KeyboardDisplay = new() { HostArtworkEnabled = false } },
            rgb, KeyboardAnimationScreens.Create(new()), new NoFactory(), new NoGuard(),
            idleFrame: () => Fill(0xAA), codexStatus: () => status,
            setCodexEnabled: (enabled, _) => { calls.Add(enabled); return status with { Enabled = enabled }; });
        (await service.GetKeyboardCodexUsageAsync()).Should().Be(status with { IndependentPageRouting = true });
        (await service.SetKeyboardCodexUsageEnabledAsync(false)).Enabled.Should().BeFalse();
        calls.Should().Equal(false);
        File.Exists(Path.Combine(data, "peripherals", "keyboard-display.json")).Should().BeFalse();
        (await service.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse("quota preference cannot enable a device or bypass emergency artwork policy");
    }

    private static byte[] Fill(byte value) => Enumerable.Repeat(value, 6656).ToArray();
    private sealed class NoFactory : IRogWiredOledFactory
    { public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("No device operations"); }
    private sealed class NoGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => throw new InvalidOperationException("No hardware guards");
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => throw new InvalidOperationException("No takeover");
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover");
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException("No restore");
    }

    private sealed class Rig : IDisposable
    {
        public ManualTimeProvider Time { get; } = new();
        public FakeSharedTimeline Timeline { get; } = new();
        public byte[] Logo { get; } = Fill(0xF0);
        public byte[] Status { get; } = Fill(0x0F);
        public KeyboardDisplayAnimation Animation { get; }
        private readonly IScreenService _screens;
        public Rig()
        {
            var media = new FakeKeyboardOledMedia { Scene = new([new("screen:h2:test", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]) };
            _screens = KeyboardAnimationScreens.Create(media);
            Animation = new(new() { AnimationAdmitted = true }, CodexTestData.Directory(), media, Timeline, Time);
        }
        public async Task Prepare()
        {
            await Animation.SaveAsync(new(true, KeyboardDisplayServiceTests.AssetA, KeyboardDisplayServiceTests.SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 2000),
                _screens, true, false, default);
            await CodexTestData.Until(() => Animation.Status(true, false).CachedFrames == 3);
        }
        public void Dispose() => Animation.Dispose();
    }
}
