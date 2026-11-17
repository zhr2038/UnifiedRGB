using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Review round 2 (CODEX-BACKEND-REVIEW-R1): engine-level writer readiness for every strict path, cache-only screen timeline with
/// startup hydration before screen output, and the StopScreens-pause / restart-hold / presence / control distinction of package
/// screens. Fake hardware only.
/// </summary>
public sealed partial class ScenePackageHostTests
{
    /// <summary>ScreenCompositor.BlockedPrefix (internal to the screen library).</summary>
    private const string ScreenCompositorBlocked = "BLOCKED: ";

    private static SceneDto PackageScene(string contentId, string bindingId) =>
        new([new AssignmentDto([new ZoneRefDto(Loop, 0)], "StaticColor", [], Strategy: "Frames", FrameSetId: ScenePackageIds.TimelineIdOf(contentId, bindingId))],
            Package: new ScenePackageRefDto(contentId, bindingId, Pkg.Contract));

    private static async Task<(string ContentId, string BindingId, string Timeline)> ImportCombinedAsync(Rig rig, string screenId)
    {
        var details = await ImportAsync(rig.Packages, Pkg.GenericFixture());
        var cid = details.Info.ContentId;
        var state = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip"), new ScenePackageBindingDto("panel", ScreenId: screenId)));
        state.Problems.Should().BeEmpty();
        return (cid, state.BindingId!, ScenePackageIds.TimelineIdOf(cid, state.BindingId!));
    }

    private static async Task<ScreenDeviceDto> ScreenAsync(Rig rig, string id) =>
        (await rig.Host.Get<IScreenService>().ListScreensAsync()).Single(s => s.Id == id);

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort (the last Host deletes it on dispose)
        }
    }

    [Fact]
    public async Task Strict_readiness_names_every_unavailable_writer_state()
    {
        var adapter = new PackageLoopAdapter("pkg:9");
        var descriptor = (await adapter.DiscoverAsync(CancellationToken.None)).Single();
        LightingEngine.StrictUnavailable(null).Should().Contain("not discovered");
        LightingEngine.StrictUnavailable(new ControllerEntry(descriptor, adapter)).Should().Contain("not active");
        LightingEngine.StrictUnavailable(new ControllerEntry(descriptor, adapter) { BringupLease = "session-1" }).Should().Contain("leased").And.Contain("session-1");
        LightingEngine.StrictUnavailable(new ControllerEntry(descriptor, adapter) { Verdict = new GuardVerdict(GuardState.Contended, "OtherApp") })
            .Should().Contain("contended").And.Contain("OtherApp");
        LightingEngine.StrictUnavailable(new ControllerEntry(descriptor, adapter) { Verdict = new GuardVerdict(GuardState.Blocked, "Vendor") })
            .Should().Contain("blocked").And.Contain("Vendor");

        await using var rig = await StartAsync();
        var entry = rig.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id.Value == Loop);
        LightingEngine.StrictUnavailable(entry).Should().BeNull("an open, unpaused, unleased, clear writer is ready");
        entry.Writer!.Paused = true;
        try
        {
            LightingEngine.StrictUnavailable(entry).Should().Contain("paused");
        }
        finally
        {
            entry.Writer.Paused = false;
        }
    }

    [Fact]
    public async Task Raw_SetScene_ApplyProfile_and_re_committing_SetAssignment_refuse_a_leased_package_writer_but_legacy_scenes_keep_their_semantics()
    {
        await using var rig = await StartAsync();
        var (cid, bid, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        var scene = PackageScene(cid, bid);
        await rig.Rgb.SaveProfileAsync(new ProfileDto("lease review", scene));
        (await rig.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
        var active = rig.Engine.Loop.Scene;
        var entry = rig.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id.Value == Loop);

        entry.BringupLease = "bring-up session 7";
        try
        {
            (await rig.Packages.GetScenePackageBindingsAsync(cid)).Ready.Should().BeFalse("Ready uses the same readiness rule");
            (await rig.Packages.ApplyScenePackageAsync(cid)).Detail.Should().Contain("leased");
            await FluentActions.Awaiting(() => rig.Rgb.SetSceneAsync(scene)).Should().ThrowAsync<ArgumentException>().WithMessage("*leased*");
            await FluentActions.Awaiting(() => rig.Rgb.ApplyProfileAsync("lease review")).Should().ThrowAsync<ArgumentException>().WithMessage("*leased*");
            await FluentActions.Awaiting(() => rig.Rgb.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto(Loop, 1)], "StaticColor", ["#00FF00"])))
                .Should().ThrowAsync<ArgumentException>("the merged scene re-commits the package, so it is re-admitted: all targets or nothing");
            rig.Engine.Loop.Scene.Should().BeSameAs(active);
        }
        finally
        {
            entry.BringupLease = null;
        }

        // a historical (non-package) scene keeps its pause semantics: it is accepted while the writer is paused
        entry.Writer!.Paused = true;
        try
        {
            await rig.Rgb.SetSceneAsync(new SceneDto([new AssignmentDto([new ZoneRefDto(Loop, 1)], "StaticColor", ["#0000FF"])]));
            rig.Engine.Loop.Scene.Package.Should().BeNull();
        }
        finally
        {
            entry.Writer.Paused = false;
        }

        await rig.Rgb.ApplyProfileAsync("lease review");
        rig.Engine.Loop.Scene.Package!.ContentId.Should().Be(cid, "ready again once the lease is gone");
    }

    [Fact]
    public async Task The_screen_timeline_is_cache_only_for_resolved_missing_and_unknown_ids()
    {
        var a = new string('a', 64);
        var screenOnly = ScenePackageIds.TimelineIdOf(a, new string('1', 64));
        var combined = ScenePackageIds.TimelineIdOf(a, new string('2', 64));
        var missing = ScenePackageIds.TimelineIdOf(new string('b', 64), new string('3', 64));
        var facts = new Dictionary<string, ScenePackageTimeline.TimelineFacts>
        {
            [screenOnly] = new(12000, false, null),
            [combined] = new(12000, true, null),
            [missing] = new(0, false, "scene package bbb is not installed"),
        };
        var calls = new Dictionary<string, int>();
        var loop = new Dictionary<string, TimelinePosition>();
        var t = new ScenePackageTimeline(id =>
        {
            lock (calls)
            {
                calls[id] = calls.GetValueOrDefault(id) + 1;
            }

            return facts[id];
        }, id => loop.GetValueOrDefault(id));

        int Calls(string id)
        {
            lock (calls)
            {
                return calls.GetValueOrDefault(id);
            }
        }

        // unknown: blocked "being prepared" with ONE background resolve; then cached (a missing package included)
        t.Position(missing)!.Blocked.Should().Be(ScenePackageTimeline.Preparing);
        await UntilAsync(() => t.Position(missing)!.Blocked == facts[missing].Problem, "the background warm");
        for (var i = 0; i < 10_000; i++)
        {
            t.Position(missing)!.Source.Should().Be(TimelineSources.Blocked);
        }

        Calls(missing).Should().Be(1, "a missing/corrupt id is never re-resolved by a rendering read");

        t.Warm(screenOnly).Should().BeNull();
        t.Warm(combined).Should().BeNull();
        for (var i = 0; i < 10_000; i++)
        {
            t.Position(screenOnly)!.Source.Should().Be(TimelineSources.ApplyEpoch, "a screen-only package keeps its shared monotonic apply epoch");
            t.Position(combined)!.Blocked.Should().Be(ScenePackageTimeline.RgbNotInPlan, "never an independent clock for a package with RGB");
        }

        loop[combined] = new TimelinePosition(1234, 12000, TimelineSources.DeviceClock);
        t.Position(combined).Should().Be(new ScreenTimelineSample(1234, TimelineSources.DeviceClock));
        (Calls(screenOnly), Calls(combined)).Should().Be((1, 1), "only the explicit warms resolved");

        // an explicit block is pinned: Retry (after an import) leaves it, only Warm (apply / completed hydration) lifts it
        t.Block(screenOnly, "startup: media could not be re-installed");
        t.Retry(a);
        t.Position(screenOnly)!.Blocked.Should().Be("startup: media could not be re-installed");
        Calls(screenOnly).Should().Be(1);
        t.Warm(screenOnly).Should().BeNull();
        t.Position(screenOnly)!.Blocked.Should().BeNull();

        // Retry re-resolves cached resolution failures of that content only; successes are untouched (no interruption)
        facts[missing] = new(12000, false, null);
        t.Retry(a);
        Calls(missing).Should().Be(1, "another content id");
        t.Retry(new string('b', 64));
        Calls(missing).Should().Be(2);
        t.Position(missing)!.Source.Should().Be(TimelineSources.ApplyEpoch);
        (Calls(screenOnly), Calls(combined)).Should().Be((2, 1));
    }

    [Fact]
    public async Task A_restart_rebuilds_a_deleted_or_incomplete_screen_cache_from_the_immutable_original_before_screens_start()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = await StartAsync(true, data, (FakeScreens?)null);
            var (cid, _, timeline) = await ImportCombinedAsync(first, FakeScreens.Tl.Id);
            (await first.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
            var asset = (await first.Rgb.GetSceneAsync()).Screens!.Assignments.Single().BackgroundAssetId!;
            await first.StopKeepingDataAsync();
            var cache = Path.Combine(data, "screens", "assets", asset);
            Directory.Exists(cache).Should().BeTrue();

            Directory.Delete(cache, recursive: true);
            var second = await StartAsync(true, data, (FakeScreens?)null);
            Directory.Exists(cache).Should().BeTrue("re-normalized from the immutable original during startup, before screens start");
            second.Host.Get<ScenePackageService>().Hydration.IsCompletedSuccessfully.Should().BeTrue();
            (await second.Host.Get<IScreenService>().ListScreenAssetsAsync()).Should().Contain(x => x.Id == asset);
            second.Host.Get<ScenePackageTimeline>().Position(timeline)!.Source.Should().NotBe(TimelineSources.Blocked);
            await UntilAsync(() => second.Screens.SinkWrites > 0, "package screen output after the restart");
            ((await ScreenAsync(second, FakeScreens.Tl.Id)).LastError ?? string.Empty).Should().NotStartWith(ScreenCompositorBlocked);
            await second.StopKeepingDataAsync();

            File.Delete(Path.Combine(cache, "f000.png"));
            await using var third = await StartAsync(true, data, (FakeScreens?)null);
            File.Exists(Path.Combine(cache, "f000.png")).Should().BeTrue("an incomplete normalized cache is replaced, not reused");
            third.Host.Get<ScenePackageTimeline>().Position(timeline)!.Source.Should().NotBe(TimelineSources.Blocked);
        }
        finally
        {
            TryDelete(data);
        }
    }

    [Fact]
    public async Task A_restart_with_a_corrupt_original_blocks_RGB_and_screens_labelled_and_never_substitutes()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = await StartAsync(true, data, (FakeScreens?)null);
            var (cid, _, timeline) = await ImportCombinedAsync(first, FakeScreens.Tl.Id);
            (await first.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
            var asset = (await first.Rgb.GetSceneAsync()).Screens!.Assignments.Single().BackgroundAssetId!;
            await first.StopKeepingDataAsync();

            var original = Path.Combine(data, ScenePackageStore.DirectoryName, cid, ScenePackageStore.FilesDir, "media", "panel.png");
            var bytes = await File.ReadAllBytesAsync(original);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(original, bytes);
            Directory.Delete(Path.Combine(data, "screens", "assets", asset), recursive: true);

            await using var second = await StartAsync(true, data, (FakeScreens?)null);
            var status = second.Engine.BuildStatus(false);
            status.BlockedContent.Should().NotBeNull();
            status.BlockedContent!.Should().Contain(b => b.Contains("BLOCKED", StringComparison.Ordinal) && b.Contains(cid, StringComparison.Ordinal));
            var position = second.Host.Get<ScenePackageTimeline>().Position(timeline)!;
            position.Source.Should().Be(TimelineSources.Blocked);
            position.Blocked.Should().StartWith("startup:").And.Contain("corrupt");
            (await second.Host.Get<IScreenService>().ListScreenAssetsAsync()).Should().NotContain(x => x.Id == asset, "nothing is installed from a corrupt original");
            await UntilAsync(() => ScreenAsync(second, FakeScreens.Tl.Id).GetAwaiter().GetResult().LastError?.StartsWith("BLOCKED: startup:", StringComparison.Ordinal) == true,
                "the package screen sends black, labelled BLOCKED");
            await SettledAsync(second.Adapter);
            second.Adapter.Uploaded.Should().OnlyContain(u => u.Loop.Rgb.ToArray().All(b => b == 0), "only black reaches the zones");

            // a corrupt original is resolved once outside rendering; repeated reads do no store I/O
            var store = new ScenePackageStore(data);
            var source = new ScenePackageFrameSource(store, new ScenePackageBindingStore(store));
            var resolves = 0;
            var t = new ScenePackageTimeline(id =>
            {
                Interlocked.Increment(ref resolves);
                return ScenePackageTimeline.FactsOf(source, id);
            }, _ => null);
            t.Warm(timeline).Should().Contain("corrupt");
            for (var i = 0; i < 1000; i++)
            {
                t.Position(timeline)!.Blocked.Should().Contain("corrupt");
            }

            resolves.Should().Be(1);
        }
        finally
        {
            TryDelete(data);
        }
    }

    [Fact]
    public async Task An_explicit_package_apply_resumes_a_StopScreens_pause_but_refuses_disconnected_or_unverified_screens_and_changes_no_gate()
    {
        await using var rig = await StartAsync(screenOutput: true);
        var screens = rig.Host.Get<IScreenService>();
        var (cid, _, timeline) = await ImportCombinedAsync(rig, FakeScreens.Tl.Id);
        await screens.SetScreenSceneAsync(new ScreenSceneDto([new ScreenAssignmentDto(FakeScreens.Tl.Id, BackgroundColor: "#102030", Fps: 2)]));
        await UntilAsync(() => ScreenAsync(rig, FakeScreens.Tl.Id).GetAwaiter().GetResult().State == ScreenStates.Running, "screen running");

        await screens.StopScreensAsync();
        await UntilAsync(() => ScreenAsync(rig, FakeScreens.Tl.Id).GetAwaiter().GetResult().State == ScreenStates.Stopped, "StopScreens pause");
        (await rig.Packages.GetScenePackageBindingsAsync(cid)).Ready.Should().BeTrue("a user pause is resumable by an explicit apply");
        var applied = await rig.Packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeTrue(applied.Detail);
        await UntilAsync(() => ScreenAsync(rig, FakeScreens.Tl.Id).GetAwaiter().GetResult().State == ScreenStates.Running, "resumed like SetScreenScene");
        (await screens.GetScreenSceneAsync()).Assignments.Single().TimelineId.Should().Be(timeline);

        // control not verified: refused whole, the flag is never turned on here
        var options = rig.Host.Options.Screens;
        options.TlLcd.ControlVerified = false;
        try
        {
            var scene = rig.Engine.Loop.Scene;
            var refused = await rig.Packages.ApplyScenePackageAsync(cid);
            refused.Applied.Should().BeFalse();
            refused.Detail.Should().Contain("control-verified");
            rig.Engine.Loop.Scene.Should().BeSameAs(scene);
            (options.TlLcd.ControlVerified, options.TlLcd.Output).Should().Be((false, true));
        }
        finally
        {
            options.TlLcd.ControlVerified = true;
        }

        // unplugged: the persisted scene still names it, so it is listed Disconnected and refused
        rig.Screens.TlPresent = false;
        try
        {
            await UntilAsync(() => ScreenAsync(rig, FakeScreens.Tl.Id).GetAwaiter().GetResult().State == ScreenStates.Disconnected, "disconnected");
            var scene = rig.Engine.Loop.Scene;
            var refused = await rig.Packages.ApplyScenePackageAsync(cid);
            refused.Applied.Should().BeFalse();
            refused.Detail.Should().Contain("Disconnected");
            rig.Engine.Loop.Scene.Should().BeSameAs(scene);
        }
        finally
        {
            rig.Screens.TlPresent = true;
        }
    }

    [Fact]
    public async Task Wireless_keyboard_and_mouse_sleep_and_wake_never_reupload_case_loops_or_block_screens_while_guard_and_lease_refuse()
    {
        var adapter = new PackageLoopAdapter();
        var kb = new SleepablePeripheralAdapter("kb:1", DeviceType.Keyboard, 81, throwBlockedOnSleep: true);
        var mouse = new SleepablePeripheralAdapter("ms:1", DeviceType.Mouse, 1, throwBlockedOnSleep: false);
        var guard = new FakeGuard().Enable(DeviceClass.Virtual, DeviceClass.AsusRog);
        var screens = new FakeScreens { AcceptOpens = true };

        await using var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IWriterGuard>(guard);
            s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(adapter));
            s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(kb));
            s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(mouse));
            s.AddSingleton(screens.Platform);
        }, o =>
        {
            o.Screens.Enabled = true;
            o.Screens.TlLcd.Output = true;
            o.Screens.H2.Output = true;
            o.Screens.TlLcd.ControlVerified = true;
            o.Screens.H2.ControlVerified = true;
        });

        await UntilAsync(() =>
            host.Engine.Adapters.Controllers.Any(c => c.Descriptor.Id.Value == Loop && c.IsActive)
            && host.Engine.Adapters.Controllers.Any(c => c.Descriptor.Id == kb.Id && c.IsActive)
            && host.Engine.Adapters.Controllers.Any(c => c.Descriptor.Id == mouse.Id && c.IsActive), "all controllers active");

        var packages = host.Get<IScenePackageService>();
        var rgb = host.Get<IRgbService>();
        var screenSvc = host.Get<IScreenService>();
        var timelineSvc = host.Get<ScenePackageTimeline>();

        var files = new Dictionary<string, byte[]>
        {
            ["data/strip.rgb24"] = Pkg.Rgb(200, 8),
            ["data/keyboard.rgb24"] = Pkg.Rgb(200, 81),
            ["data/mouse.rgb24"] = Pkg.Rgb(200, 1),
            ["media/panel.png"] = Pkg.Png(),
        };
        var manifest = Pkg.Manifest(files,
            [
                Pkg.LightingTrack("strip", "data/strip.rgb24", 8, 200, 60),
                Pkg.LightingTrack("keyboard", "data/keyboard.rgb24", 81, 200, 60),
                Pkg.LightingTrack("mouse", "data/mouse.rgb24", 1, 200, 60),
            ],
            [Pkg.ScreenTrack("panel", "media/panel.png")]);
        var zip = Pkg.Zip([("scene.json", Pkg.Utf8(manifest)), .. files.Select(f => (f.Key, f.Value))]);

        var details = await ImportAsync(packages, zip);
        var cid = details.Info.ContentId;
        var bindings = await packages.SaveScenePackageBindingsAsync(Bind(cid,
            Zone("strip"),
            Zone("keyboard", kb.Id.Value, 0),
            Zone("mouse", mouse.Id.Value, 0),
            new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Tl.Id)));
        bindings.Ready.Should().BeTrue(string.Join("; ", bindings.Problems));
        var timeline = ScenePackageIds.TimelineIdOf(cid, bindings.BindingId!);

        var applied = await packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeTrue(applied.Detail);
        var uploads = await SettledAsync(adapter);
        var zone0Uploads = adapter.For(0).Count;

        ControllerEntry Entry(ControllerId id) => host.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id == id);
        async Task<ScreenDeviceDto> PanelAsync() => (await screenSvc.ListScreensAsync()).Single(s => s.Id == FakeScreens.Tl.Id);

        // 1. Keyboard sleeps via DeviceBlockedException ("Keyboard USB owner is connecting."), leaving Verdict = Blocked while Guard is Clear.
        kb.SetAsleep(true);
        await host.Engine.RescanAsync(CancellationToken.None);
        Entry(kb.Id).IsActive.Should().BeFalse();
        Entry(kb.Id).Verdict.State.Should().Be(GuardState.Blocked);

        await Task.Delay(250);
        adapter.Uploaded.Count.Should().Be(uploads, "keyboard standby must never blackout or re-upload Lian Li loops");
        host.Engine.BuildStatus(false).BlockedContent.Should().BeNull();
        timelineSvc.Position(timeline)!.Blocked.Should().BeNull();
        ((await PanelAsync()).LastError ?? string.Empty).Should().NotStartWith(ScreenCompositorBlocked);

        // Applying and re-saving scene must succeed with 0 loop uploads while keyboard is asleep;
        // editing unrelated loop zone Loop/1 uploads only Zone 1 and leaves strict package Zone 0 untouched.
        (await packages.GetScenePackageBindingsAsync(cid)).Ready.Should().BeTrue();
        (await packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
        await rgb.SetSceneAsync(await rgb.GetSceneAsync());
        await Task.Delay(200);
        adapter.Uploaded.Count.Should().Be(uploads, "no case loop re-upload should occur on re-apply/resave while keyboard sleeps");
        await rgb.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto(Loop, 1)], "StaticColor", ["#00FF00"]));
        uploads = await SettledAsync(adapter);
        adapter.For(0).Count.Should().Be(zone0Uploads, "editing unrelated zone 1 must never re-upload strict package zone 0");

        // 2. Mouse also sleeps via IOException ("Offline"): case lights & screens still uninterrupted.
        mouse.SetAsleep(true);
        await host.Engine.RescanAsync(CancellationToken.None);
        Entry(mouse.Id).IsActive.Should().BeFalse();

        await Task.Delay(250);
        adapter.Uploaded.Count.Should().Be(uploads, "mouse standby must never blackout or re-upload Lian Li loops");
        host.Engine.BuildStatus(false).BlockedContent.Should().BeNull();
        timelineSvc.Position(timeline)!.Blocked.Should().BeNull();

        // 3. Both wake back up: zero Lian Li re-uploads and peripherals become active again.
        kb.SetAsleep(false);
        mouse.SetAsleep(false);
        await host.Engine.RescanAsync(CancellationToken.None);
        Entry(kb.Id).IsActive.Should().BeTrue();
        Entry(mouse.Id).IsActive.Should().BeTrue();
        await Task.Delay(250);
        adapter.Uploaded.Count.Should().Be(uploads, "peripheral wake must never disturb case loops");

        // 4. WriterGuard contention/block and BringupLease STILL refuse package activation while keyboard sleeps.
        kb.SetAsleep(true);
        await host.Engine.RescanAsync(CancellationToken.None);
        guard.Verdicts[DeviceClass.AsusRog] = GuardVerdict.Blocked("ArmouryCrate");
        try
        {
            (await packages.GetScenePackageBindingsAsync(cid)).Ready.Should().BeFalse();
            (await packages.ApplyScenePackageAsync(cid)).Applied.Should().BeFalse();
        }
        finally
        {
            guard.Verdicts.TryRemove(DeviceClass.AsusRog, out _);
        }

        Entry(kb.Id).BringupLease = "lease-1";
        try
        {
            (await packages.GetScenePackageBindingsAsync(cid)).Ready.Should().BeFalse();
            (await packages.ApplyScenePackageAsync(cid)).Applied.Should().BeFalse();
        }
        finally
        {
            Entry(kb.Id).BringupLease = null;
        }
    }

    private sealed class SleepablePeripheralAdapter(string id, DeviceType deviceType, int leds, bool throwBlockedOnSleep) : IDeviceAdapter
    {
        private readonly ControllerDescriptor _descriptor = new(new ControllerId(id), "ROG", id, deviceType, DeviceClass.AsusRog, id,
            [new ZoneDescriptor(new ZoneId(0), "zone", ZoneLayout.Linear(leds))], Capabilities.DirectOnly(60));
        private int _asleep;

        public ControllerId Id => _descriptor.Id;
        public string Vendor => "ROG";
        public DeviceClass Class => DeviceClass.AsusRog;
        public event Action<HotplugEvent>? Hotplug;

        public void SetAsleep(bool asleep)
        {
            Volatile.Write(ref _asleep, asleep ? 1 : 0);
            Hotplug?.Invoke(new HotplugEvent(Id, asleep ? HotplugKind.Removed : HotplugKind.Arrived));
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_descriptor]);

        public Task OpenAsync(ControllerId controllerId, CancellationToken ct) =>
            Volatile.Read(ref _asleep) != 0
                ? Task.FromException(throwBlockedOnSleep ? new DeviceBlockedException("Keyboard USB owner is connecting.") : new IOException("mouse asleep"))
                : Task.CompletedTask;

        public void WriteFrame(ControllerId controllerId, ZoneId zone, ReadOnlySpan<Rgb> colors) { }
        public Task SetFirmwareModeAsync(ControllerId controllerId, FirmwareMode mode, CancellationToken ct) => Task.CompletedTask;
        public Telemetry? ReadTelemetry(ControllerId controllerId) => null;
        public Task CloseAsync(ControllerId controllerId) => Task.CompletedTask;
    }
}
