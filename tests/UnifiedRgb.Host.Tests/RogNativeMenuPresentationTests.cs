using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

// The FUTURE declared native presentation on the actual keyboard owner with the existing native-menu fakes. The capability is a
// fake input only: production registers none and no current firmware is known to support it. Owner/animation time is virtual
// (ManualTimeProvider); the shared source timeline is the test's FakeSharedTimeline. Mock success here is not physical behavior.
public sealed partial class RogNativeMenuSourceTests
{
    private const string SuppressedDetail = "已安装的原生光域菜单拥有键盘屏幕输出：不发送动画画面，原生状态画面与拨杆不受影响；绑定与开启偏好已保留";

    [Fact]
    public void Scene_caption_revision_bypasses_animation_frame_interval()
    {
        long revision = 0;
        var calls = 0;
        using var state = new PresentationState(false, selected =>
        {
            calls++;
            selected[0] = (byte)revision;
            return selected;
        }, () => revision);
        var first = state.Presentation.Latest(state.Status, true);
        state.Presentation.Stepped(true);
        state.Presentation.Latest(state.Status, true).Should().BeSameAs(first);
        calls.Should().Be(1);
        revision++;
        var next = state.Presentation.Latest(state.Status, true);
        next[0].Should().Be(1);
        calls.Should().Be(2, "a scene name changes immediately without advancing the background clock");
        state.Status[0].Should().Be(0x0F, "the overlay must not rewrite the source frame");
        state.Presentation.Latest(state.Status, true).Should().BeSameAs(next);
    }

    [Fact]
    public async Task Device_owned_entry_logo_is_not_uploaded_again_by_the_host_and_native_input_keeps_working()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available),
            mode: 7, animation: false, deviceShowsLogo: true);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative(); await rig.Declared();
        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        rig.Shows(rig.Logo).Should().BeFalse("the physical entry already displayed the device's Logo");
        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1));
        await Until(() => rig.Events.OfType<NativeMenuPress>().Count() == 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        await rig.NextTick();
        rig.Control.ImageBegins.Should().Be(1, "no deferred second Logo is queued");
        rig.Control.Arms.Should().Be(1);
    }

    [Fact]
    public async Task Device_owned_entry_logo_option_does_not_remove_the_ordinary_host_entry_logo()
    {
        await using var rig = new PresentationRig(null, mode: 7, policy: false, animation: false, deviceShowsLogo: true);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
    }

    [Fact]
    public async Task Declared_presentation_shows_the_entry_logo_then_the_shared_timeline_frame_at_nonzero_elapsed_time_while_the_lever_keeps_working()
    {
        var capability = new DeclaredPresentation(KeyboardNativePresentationSupport.Available);
        await using var rig = new PresentationRig(capability, mode: 7);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative();
        await rig.Prepared();
        await rig.Declared();
        capability.Scopes.Should().ContainSingle().Which.Should().Be(
            new KeyboardNativePresentationScope(rig.Control.DeviceKey, rig.Control.FirmwareHeader, "wired"));

        // Ordinary mode 7 under installed ownership: unchanged (no image, no frame selection).
        var ordinary = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        ordinary.State.Should().Be(KeyboardDisplayAnimationStates.Ready);
        ordinary.Detail.Should().Contain("固件已声明原生光域画面支持");
        rig.Control.ImageBegins.Should().Be(0); rig.Timeline.Reads.Should().Be(0);

        rig.Timeline.At(1500); // the shared source keeps its own position while the overlay is shown
        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
        rig.Control.Arms.Should().Be(1);
        // The lever keeps working during the overlay; its notices neither restart nor extend it.
        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1));
        await Until(() => rig.Events.OfType<NativeMenuPress>().Count() == 1);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        await Task.Delay(400);
        rig.Control.ImageBegins.Should().Be(1, "repeated mode9 ticks, lease wakes and the press keep the one bounded overlay");
        rig.Shows(rig.Logo).Should().BeTrue();
        rig.Timeline.Reads.Should().Be(0, "the overlay selects no frame and never touches the shared timeline");

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Control.ImageBegins == 2 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x22)));
        await rig.NextTick();
        rig.Shows(KeyboardDisplayServiceTests.Fill(0x11)).Should().BeFalse("expiry resumes at the shared position, not frame zero");
        rig.Control.Arms.Should().Be(1, "protocol2 replaces artwork inside the same epoch and reader");
        rig.Control.Disarms.Should().Be(0); rig.Control.RejectedImages.Should().Be(0);
        var playing = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        playing.State.Should().Be(KeyboardDisplayAnimationStates.Playing);
        playing.Acknowledged.Should().Be(1);
        playing.Detail.Should().Contain("拨杆仍由原生菜单控制");

        // The same native context keeps sequence/neutral and the lever across replacement artwork.
        rig.Input.Send(ScopedNotice(3, 0, 1, rig.Control.Epoch, Cookie)); rig.Input.Send(ScopedNotice(4, 2, 1, rig.Control.Epoch, Cookie));
        await Until(() => rig.Events.OfType<NativeMenuPress>().Count() == 2);
        rig.Control.ImageBegins.Should().Be(2);

        // The same shared timeline continues through the existing cadence gate.
        rig.Timeline.At(2500);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(2000));
        await Until(() => rig.Control.ImageBegins == 3 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x33)));
        rig.Events.OfType<NativeMenuContextEnded>().Should().NotContain(e => e.Disconnected);
    }

    [Fact]
    public async Task Without_authored_content_the_logo_expires_into_the_existing_status_artwork()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available),
            mode: RogNativeMenuCodec.NativeMode, animation: false);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
        await rig.NextTick();
        var restores = rig.Control.NativeRestores;
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Control.ImageBegins == 2 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        rig.Timeline.Reads.Should().Be(0);
        (await rig.Owner.GetKeyboardDisplayAnimationAsync()).State.Should().Be(KeyboardDisplayAnimationStates.Off);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(2, "the status artwork stays; no logo comes back without a new entry");
        rig.Control.Disarms.Should().Be(0);
        rig.Control.NativeRestores.Should().Be(restores, "selecting the base presentation is not a native restore");
    }

    [Fact]
    public async Task Mode_exit_ends_the_entry_and_a_real_re_entry_gets_one_fresh_bounded_logo_then_the_current_frame()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available), mode: 7);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative(); await rig.Prepared(); await rig.Declared();
        rig.Timeline.At(1500); rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Shows(rig.Logo)); await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Shows(KeyboardDisplayServiceTests.Fill(0x22))); await rig.NextTick();

        var restores = rig.Control.NativeRestores;
        rig.Control.Mode = 7;
        await Until(() => rig.Control.NativeRestores > restores);
        rig.Clock.Advance(TimeSpan.FromSeconds(3)); // outside mode9 nothing is drawn; the shared source moves on
        rig.Timeline.At(2500);
        var images = rig.Control.ImageBegins;
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(images);

        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == images + 1 && rig.Shows(rig.Logo));
        await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(images + 1);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Control.ImageBegins == images + 2 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x33)));
    }

    [Fact]
    public async Task Disable_cancels_the_overlay_and_re_enable_is_a_fresh_bounded_entry_without_releasing_native_control()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available), mode: 7);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative(); await rig.Prepared(); await rig.Declared();
        rig.Timeline.At(1500); rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo)); await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(800));

        rig.Source!.RequestEnabled(false);
        await Until(() => rig.Control.Disarms == 1);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(800)); // past the cancelled entry's bound
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(1, "a disabled native owner draws nothing and never falls back to legacy output");
        rig.Timeline.Reads.Should().Be(0);

        rig.Source.RequestEnabled(true);
        await Until(() => rig.Control.ImageBegins == 2 && rig.Shows(rig.Logo));
        await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        await Task.Delay(300);
        rig.Control.ImageBegins.Should().Be(2);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Control.ImageBegins == 3 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x22)));
        rig.Control.RejectedImages.Should().Be(0);
    }

    [Fact]
    public async Task Reconnect_is_a_new_session_that_re_evaluates_the_capability_and_shows_one_fresh_logo_at_the_current_position()
    {
        var capability = new DeclaredPresentation(KeyboardNativePresentationSupport.Available);
        await using var rig = new PresentationRig(capability, mode: 7);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative(); await rig.Prepared(); await rig.Declared();
        rig.Timeline.At(1500); rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Shows(rig.Logo)); await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Shows(KeyboardDisplayServiceTests.Fill(0x22))); await rig.NextTick();
        var images = rig.Control.ImageBegins;

        rig.Timeline.At(2500);
        await rig.Owner.ResumeAfterPeripheralConfigurationAsync(default).WaitAsync(Patience);
        rig.Factory.Opens.Should().Be(2);
        await Until(() => rig.Control.ImageBegins == images + 1 && rig.Shows(rig.Logo));
        capability.Calls.Should().Be(2, "each owner session asks for its own scope");
        await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Control.ImageBegins == images + 2 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x33)));
        rig.Control.Disarms.Should().Be(1, "only the old owner's existing stop path disarmed");
    }

    [Fact]
    public async Task An_unknown_logo_upload_outcome_faults_the_owner_without_retry_disarm_or_guessed_presentation()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available), mode: 7);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative(); await rig.Prepared(); await rig.Declared();
        rig.Control.FailStage = "begin";
        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
        var writes = rig.Control.Writes.Count;
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(400);
        rig.Control.Writes.Should().HaveCount(writes, "an unknown outcome is never retried");
        rig.Control.ImageBegins.Should().Be(1); rig.Control.Arms.Should().Be(1); rig.Control.Disarms.Should().Be(0);
        rig.Factory.Opens.Should().Be(1); rig.Timeline.Reads.Should().Be(0);
        var status = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        status.State.Should().Be(KeyboardDisplayAnimationStates.SuppressedByNativeMenu, "no owner session, no declared presentation");
        status.Detail.Should().Be(SuppressedDetail);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("unavailable")]
    [InlineData("unrecognized")]
    [InlineData("throws")]
    public async Task Without_an_explicit_Available_capability_installed_native9_keeps_its_exact_animation_suppression(string answer)
    {
        DeclaredPresentation? capability = answer switch
        {
            "absent" => null,
            "unavailable" => new DeclaredPresentation(KeyboardNativePresentationSupport.Unavailable),
            "unrecognized" => new DeclaredPresentation((KeyboardNativePresentationSupport)2),
            _ => new DeclaredPresentation(KeyboardNativePresentationSupport.Available, throws: true),
        };
        await using var rig = new PresentationRig(capability, mode: 7);
        await rig.Owner.StartAsync(default);
        await rig.RunningNative(); await rig.Prepared();
        if (capability is not null) await Until(() => capability.Calls == 1);
        rig.Timeline.At(1500);
        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == 1 && rig.Control.Rebuilt.Length == 6656);
        rig.Control.Rebuilt.Should().Equal(rig.Owner.ArtworkPixels.ToArray());
        rig.Clock.Advance(TimeSpan.FromSeconds(5)); rig.Timeline.At(2500);
        await Task.Delay(400);
        rig.Control.ImageBegins.Should().Be(1); rig.Control.Arms.Should().Be(1); rig.Control.RejectedImages.Should().Be(0);
        rig.Control.Rebuilt.Should().Equal(rig.Owner.ArtworkPixels.ToArray());
        rig.Timeline.Reads.Should().Be(0, "a suppressed animation does not even select a frame");
        var status = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        status.State.Should().Be(KeyboardDisplayAnimationStates.SuppressedByNativeMenu);
        status.Detail.Should().Be(SuppressedDetail);
        status.Selected.Should().Be(0); status.Acknowledged.Should().Be(0);
        File.ReadAllBytes(rig.AnimationPreference).Should().Equal(rig.AnimationBefore);
    }

    [Fact]
    public async Task Ordinary_owner_without_installed_policy_never_consults_the_capability_and_plays_on_the_legacy_page()
    {
        var capability = new DeclaredPresentation(KeyboardNativePresentationSupport.Available);
        await using var rig = new PresentationRig(capability, mode: 7, policy: false);
        rig.Timeline.At(1500);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins >= 1);
        await rig.Prepared();
        await Until(() => rig.Shows(KeyboardDisplayServiceTests.Fill(0x22)));
        var status = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        status.State.Should().Be(KeyboardDisplayAnimationStates.Playing);
        status.Detail.Should().NotContain("固件已声明");
        capability.Calls.Should().Be(0);
        rig.Control.Writes.ToArray().Should().NotContain("12/7D");
        rig.Control.Arms.Should().Be(0);
    }

    [Fact]
    public async Task Production_composition_registers_one_pure_default_off_presentation_capability()
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNativePresentationDi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection().AddLogging().AddUnifiedRgb(new UnifiedRgbOptions { UseVirtualAdapter = false, DataDirectory = data }, enableIpc: false);
            services.Should().ContainSingle(d => d.ServiceType == typeof(IKeyboardNativePresentationCapability));
            services.AddSingleton<IWriterGuard>(new MutationClearGuard());
            services.AddSingleton<IRogWiredOledFactory>(new NoOled());
            services.AddSingleton<IRogWirelessOledFactory>(new NoWirelessOled());
            await using (var provider = services.BuildServiceProvider())
            {
                var capability = provider.GetRequiredService<IKeyboardNativePresentationCapability>();
                capability.Should().BeOfType<RogNative49PresentationCapability>();
                capability.Evaluate(new(new string('A', 64), "001200000025000100", "wired")).Should().Be(KeyboardNativePresentationSupport.Unavailable);
                var keyboard = provider.GetRequiredService<KeyboardDisplayService>();
                typeof(KeyboardDisplayService).GetField("_nativePresentation", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(keyboard).Should().BeSameAs(capability);
            }
            default(KeyboardNativePresentationSupport).Should().Be(KeyboardNativePresentationSupport.Unavailable);
            var implementations = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name is { } n && n.StartsWith("UnifiedRgb.", StringComparison.Ordinal) && !n.EndsWith(".Tests", StringComparison.Ordinal))
                .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>().ToArray(); } })
                .Where(t => !t.IsInterface && typeof(IKeyboardNativePresentationCapability).IsAssignableFrom(t));
            implementations.Should().ContainSingle().Which.Should().Be<RogNative49PresentationCapability>();
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    // ---- pure presentation state (owner actor calls in order; virtual time) ----

    [Fact]
    public void Repeated_armed_ticks_never_extend_the_bound_and_an_unacknowledged_entry_never_starts_it()
    {
        using var state = new PresentationState(logo: true);
        var p = state.Presentation;
        p.Latest(state.Status, true).Should().BeSameAs(state.Logo);
        p.Stepped(false).Should().BeFalse(); // the logo upload did not finish armed: no bound, entry ended
        p.Entered.Should().BeFalse();
        state.Clock.Advance(TimeSpan.FromSeconds(30));
        p.Latest(state.Status, true).Should().BeSameAs(state.Logo, "a new entry still owes its whole overlay");
        p.Stepped(true).Should().BeTrue(); // acknowledged: the 1200 ms bound starts now
        for (var i = 0; i < 11; i++)
        {
            state.Clock.Advance(TimeSpan.FromMilliseconds(109));
            p.Latest(state.Status, true).Should().BeSameAs(state.Logo);
            p.Stepped(true).Should().BeTrue(); // repeated mode9 ticks/re-arms with the same bytes
        }
        state.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 11 * 109 - 1));
        p.Latest(state.Status, true).Should().BeSameAs(state.Logo);
        state.Clock.Advance(TimeSpan.FromMilliseconds(1));
        p.Latest(state.Status, true).Should().BeSameAs(state.Status, "the bound is exact; nothing authored, so the status artwork");
        p.Stepped(true).Should().BeTrue();
        state.Clock.Advance(TimeSpan.FromHours(1));
        p.Latest(state.Status, true).Should().BeSameAs(state.Status, "no logo without a new entry");
        p.Stepped(false); p.Latest(state.Status, true).Should().BeSameAs(state.Logo, "a real re-entry");
    }

    [Fact]
    public void Without_a_rendered_logo_the_overlay_is_skipped_and_content_is_presented_at_once()
    {
        using var state = new PresentationState(logo: false);
        state.Presentation.Latest(state.Status, true).Should().BeSameAs(state.Status);
        state.Presentation.Entered.Should().BeTrue();
        state.Presentation.Stepped(true).Should().BeTrue();
    }

    private static byte[] ScopedNotice(ushort sequence, byte mask, byte flags, uint epoch, ulong cookie)
    {
        var p = Notice(sequence, mask, flags);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), epoch); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), cookie);
        return p;
    }

    private sealed class DeclaredPresentation(KeyboardNativePresentationSupport answer, bool throws = false) : IKeyboardNativePresentationCapability
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<KeyboardNativePresentationScope> Scopes { get; } = new();
        public KeyboardNativePresentationSupport Evaluate(KeyboardNativePresentationScope scope)
        {
            Interlocked.Increment(ref _calls); Scopes.Enqueue(scope);
            return throws ? throw new InvalidOperationException("Injected capability fault.") : answer;
        }
    }

    private sealed class NoWirelessOled : IRogWirelessOledFactory
    {
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader) => throw new InvalidOperationException("No keyboard opens.");
    }

    private sealed class PresentationState : IDisposable
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "SolNativePresentationState-" + Guid.NewGuid().ToString("N"));
        private readonly KeyboardDisplayAnimation _animation;
        public ManualTimeProvider Clock { get; } = new();
        public byte[] Status { get; } = KeyboardDisplayServiceTests.Fill(0x0F);
        public byte[] Logo { get; } = KeyboardDisplayServiceTests.Fill(0xF0);
        public KeyboardNativePresentation Presentation { get; }
        public PresentationState(bool logo, Func<byte[], byte[]>? overlay = null, Func<long>? revision = null)
        {
            // Not admitted: the selector answers with the status artwork, which is the original base presentation.
            _animation = new KeyboardDisplayAnimation(new KeyboardDisplayOptions(), _data, null, null, Clock);
            Presentation = new(_animation, logo ? Logo : null, Clock, overlay, overlayRevision: revision);
        }
        public void Dispose() { _animation.Dispose(); if (Directory.Exists(_data)) Directory.Delete(_data, true); }
    }

    private sealed class PresentationRig : IAsyncDisposable
    {
        private readonly IDisposable? _registration;
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "SolNativePresentation-" + Guid.NewGuid().ToString("N"));
        public string AnimationPreference => Path.Combine(Data, "peripherals", KeyboardDisplayAnimation.FileName);
        public byte[] AnimationBefore { get; }
        public Input Input { get; }
        public Connection Control { get; }
        public LegacyPageFactory Factory { get; }
        public RogNativeMenuSource? Source { get; }
        public ConcurrentQueue<NativeMenuSourceEvent> Events { get; } = new();
        public RogWiredLightingBridge Bridge { get; } = new();
        public FakeKeyboardOledMedia Media { get; } = new()
        {
            Scene = new([new("screen:h2:test", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]),
        };
        public FakeSharedTimeline Timeline { get; } = new();
        public ManualTimeProvider Clock { get; } = new();
        public KeyboardDisplayService Owner { get; }
        public byte[] Logo => Owner.EntryLogoPixels.ToArray();

        public PresentationRig(IKeyboardNativePresentationCapability? capability, byte mode, bool policy = true, bool animation = true,
            bool deviceShowsLogo = false)
        {
            Directory.CreateDirectory(Path.Combine(Data, "peripherals"));
            File.WriteAllBytes(Path.Combine(Data, "peripherals", "keyboard-display.json"), Encoding.UTF8.GetBytes("{\"Schema\": 1, \"Enabled\": true}"));
            AnimationBefore = KeyboardDisplayServiceTests.AnimationFile(enabled: animation);
            File.WriteAllBytes(AnimationPreference, AnimationBefore);
            var time = new SourceClock(); Input = new(time, 1);
            Control = new(time, 1) { Input = Input, Mode = mode, EnforceNativeImageGate = policy };
            Factory = new(Control, false);
            if (policy)
            {
                ulong cookie = Cookie; Source = new(Policy(), Input, Input, time, () => cookie++);
                _registration = Source.Connect(Events.Enqueue); Source.RequestEnabled(true);
            }
            var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
            Owner = new(new()
            {
                DataDirectory = Data,
                KeyboardDisplay = new() { Enabled = false, ControlVerified = true, LightingVerified = true,
                    DeviceKey = Control.DeviceKey, FirmwareHeader = Control.FirmwareHeader, AnimationAdmitted = true,
                    DeviceShowsNativeEntryLogo = deviceShowsLogo },
            }, rgb, KeyboardAnimationScreens.Create(Media), Factory, new MutationClearGuard(), Bridge,
                nativeMenu: Source, keyboardMedia: Media, sharedTimeline: Timeline, time: Clock, nativePresentation: capability);
        }

        public bool Shows(byte[] pixels) => Control.Rebuilt.AsSpan().SequenceEqual(pixels);

        public Task RunningNative() => Until(() => Bridge.Available
            && Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().Detail.Contains("原生光域菜单", StringComparison.Ordinal));

        public Task Declared() => Until(() => Owner.GetKeyboardDisplayAnimationAsync().GetAwaiter().GetResult().Detail
            .Contains("固件已声明原生光域画面支持", StringComparison.Ordinal));

        public Task Prepared() => Until(() => Owner.GetKeyboardDisplayAnimationAsync().GetAwaiter().GetResult().CachedFrames == 3);

        /// <summary>Any later control exchange is the next owner turn: the previous native tick (and its bookkeeping) completed.</summary>
        public async Task NextTick()
        {
            var writes = Control.Writes.Count;
            await Until(() => Control.Writes.Count > writes);
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.StopAsync(default); Owner.Dispose(); _registration?.Dispose(); Input.Dispose(); Control.Dispose();
            if (Directory.Exists(Data)) Directory.Delete(Data, true);
        }
    }
}
