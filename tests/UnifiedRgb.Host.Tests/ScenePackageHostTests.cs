using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// OPUS-GENERIC-SCENE-PACKAGES end to end in a real Host (full DI) with fake hardware only: a Virtual-class uploaded-loop adapter
/// with a wire-budget probe and a fake screen platform whose USB open is counted and refused. Nothing reaches real devices.
/// </summary>
[Collection("Host")]
public sealed partial class ScenePackageHostTests
{
    private const string Loop = "pkg:1";

    private sealed record Rig(TestHost Host, PackageLoopAdapter Adapter, FakeScreens Screens) : IAsyncDisposable
    {
        public IScenePackageService Packages => Host.Get<IScenePackageService>();

        public IRgbService Rgb => Host.Get<IRgbService>();

        public LightingEngine Engine => Host.Engine;

        public ValueTask DisposeAsync() => Host.DisposeAsync();

        /// <summary>Stops the Host but keeps its data directory (restart tests).</summary>
        public async Task StopKeepingDataAsync()
        {
            await Host.App.StopAsync();
            Host.App.Dispose();
        }
    }

    private static Task<Rig> StartAsync(string? dataDirectory = null, params IDeviceAdapter[] extra) => StartAsync(false, dataDirectory, extra);

    /// <summary>
    /// <paramref name="screenOutput"/>: screen output enabled onto the fake in-memory sink, with the TL/H2 owner ControlVerified flag
    /// (a package with screens needs both). <paramref name="screens"/>: a shared fake platform (restart tests).
    /// </summary>
    private static async Task<Rig> StartAsync(bool screenOutput, string? dataDirectory = null, params IDeviceAdapter[] extra) =>
        await StartAsync(screenOutput, dataDirectory, null, extra);

    private static async Task<Rig> StartAsync(bool screenOutput, string? dataDirectory, FakeScreens? screens, params IDeviceAdapter[] extra)
    {
        var adapter = new PackageLoopAdapter();
        screens ??= new FakeScreens { AcceptOpens = screenOutput };
        var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(adapter));
            foreach (var a in extra)
            {
                s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(a));
            }

            s.AddSingleton(screens.Platform);
        }, o =>
        {
            if (dataDirectory is not null)
            {
                o.DataDirectory = dataDirectory;
            }

            if (screenOutput)
            {
                o.Screens.Enabled = true;
                o.Screens.TlLcd.Output = true;
                o.Screens.H2.Output = true;
                o.Screens.TlLcd.ControlVerified = true;
                o.Screens.H2.ControlVerified = true;
                if (screens.CasePresent)
                {
                    o.Screens.Turzx.Output = true;
                    o.Screens.Turzx.ControlVerified = true;
                }
            }
        });
        await UntilAsync(() => host.Engine.Adapters.Controllers.Any(c => c.Descriptor.Id.Value == Loop && c.IsActive), "loop controller active");
        return new Rig(host, adapter, screens);
    }

    private static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits until the adapter saw no new upload for a while (startup scene settled).</summary>
    private static async Task<int> SettledAsync(PackageLoopAdapter adapter)
    {
        var count = -1;
        while (count != adapter.Uploaded.Count)
        {
            count = adapter.Uploaded.Count;
            await Task.Delay(300);
        }

        return count;
    }

    private static Task<ScenePackageDetailsDto> ImportAsync(IScenePackageService svc, byte[] zip) =>
        svc.ImportScenePackageAsync(new ScenePackageUploadDto("upload", Convert.ToBase64String(zip)));

    private static ScenePackageBindingsDto Bind(string contentId, params ScenePackageBindingDto[] b) => new(contentId, b);

    private static ScenePackageBindingDto Zone(string role, string controller = Loop, int zone = 0) => new(role, new ZoneRefDto(controller, zone));

    private static async Task<(string ContentId, string BindingId, string Timeline)> ImportBoundAsync(Rig rig, byte[] zip)
    {
        var details = await ImportAsync(rig.Packages, zip);
        var state = await rig.Packages.SaveScenePackageBindingsAsync(Bind(details.Info.ContentId, Zone("strip")));
        state.Problems.Should().BeEmpty();
        state.Ready.Should().BeTrue();
        return (details.Info.ContentId, state.BindingId!, ScenePackageIds.TimelineIdOf(details.Info.ContentId, state.BindingId!));
    }

    private static LoopFrames LastStrict(PackageLoopAdapter adapter) => adapter.For(0).Last(l => l.Strict);

    [Fact]
    public async Task Import_preview_and_binding_saves_never_upload_measure_open_or_change_the_scene()
    {
        await using var rig = await StartAsync();
        var uploads = await SettledAsync(rig.Adapter);
        var scene = rig.Engine.Loop.Scene;

        var details = await ImportAsync(rig.Packages, Pkg.Lighting());
        details.Info.PlaybackContract.Should().Be(Pkg.Contract);
        details.Info.DurationMs.Should().Be(12000);
        details.Roles.Should().ContainSingle().Which.Should().Match<ScenePackageRoleDto>(r =>
            r.Kind == "lighting" && r.LedCount == 8 && r.Frames == 200 && r.Width == 0 && r.PreviewColors.Count == 8 && r.PreviewColors[0] == "#FFFFFF");
        (await rig.Packages.ListScenePackagesAsync()).Should().ContainSingle();
        (await rig.Packages.GetScenePackageAsync(details.Info.ContentId)).Info.Should().Be(details.Info);
        var none = await rig.Packages.GetScenePackageBindingsAsync(details.Info.ContentId);
        none.BindingId.Should().BeNull();
        none.Ready.Should().BeFalse();
        (await rig.Packages.SaveScenePackageBindingsAsync(Bind(details.Info.ContentId, Zone("strip")))).Ready.Should().BeTrue();
        (await ImportAsync(rig.Packages, Pkg.Lighting())).Info.Should().Be(details.Info, "identical content is idempotent");

        await Task.Delay(400);
        rig.Adapter.Uploaded.Count.Should().Be(uploads);
        rig.Adapter.Measures.Should().Be(0);
        rig.Screens.Opens.Should().Be(0);
        rig.Engine.Loop.Scene.Should().BeSameAs(scene);
    }

    [Fact]
    public async Task Apply_uploads_the_exact_immutable_bytes_at_96_raw_ticks_with_one_shared_12s_timeline()
    {
        await using var rig = await StartAsync();
        await SettledAsync(rig.Adapter);
        var (cid, bid, timeline) = await ImportBoundAsync(rig, Pkg.Lighting());

        var applied = await rig.Packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeTrue(applied.Detail);
        applied.BindingId.Should().Be(bid);
        rig.Adapter.Measures.Should().BeGreaterThan(0, "the budget is measured through the adapter before commit");

        await UntilAsync(() => rig.Adapter.For(0).Any(l => l.Strict), "strict upload");
        var loop = LastStrict(rig.Adapter);
        loop.Frames.Should().Be(200);
        loop.LedCount.Should().Be(8);
        loop.IntervalMs.Should().Be(96, "60 physical ms = 96 raw ticks at 1600/s");
        loop.Rgb.ToArray().Should().Equal(Pkg.Rgb(200, 8), "authored bytes are uploaded unchanged (no regeneration, no rescale)");

        rig.Engine.Loop.Scene.Package.Should().Be(new ScenePackageProvenance(cid, bid, Pkg.Contract));
        rig.Engine.BuildStatus(false).BlockedContent.Should().BeNull();

        var rgb = rig.Engine.Loop.Timeline(timeline);
        rgb.Should().NotBeNull();
        rgb!.PeriodMs.Should().Be(12000, "200 frames × 60 ms");
        rgb.IsDeviceClock.Should().BeFalse("the fake has no device clock: the source is labelled");
        rgb.Source.Should().Match(s => s.StartsWith(TimelineSources.UploadAnchor, StringComparison.Ordinal) || s == TimelineSources.LocalEpoch);

        var screen = rig.Host.Get<ScenePackageTimeline>().Position(timeline);
        screen.Should().NotBeNull();
        screen!.Source.Should().Be(rig.Engine.Loop.Timeline(timeline)!.Source, "screens read the RGB's own timeline");
        screen.Blocked.Should().BeNull();

        // index alignment: one position source, so RGB frame = floor(position / 60) is also the screen's authored instant
        foreach (var t in new[] { 10.0, 10.059, 10.06, 21.999, 22.0, 33.5 })
        {
            var a = rig.Engine.Loop.TimelineAt(timeline, t)!;
            var b = rig.Engine.Loop.TimelineAt(timeline, t + 0.06)!;
            var step = ((b.PositionMs - a.PositionMs) % 12000 + 12000) % 12000;
            step.Should().BeApproximately(60, 1e-6, "physical ms advance 1:1 (exactly one 60 ms frame per 60 ms)");
            var index = (int)Math.Floor(a.PositionMs / 60);
            index.Should().BeInRange(0, 199);
            ((int)Math.Floor(b.PositionMs / 60)).Should().Be((index + 1) % 200);
        }
    }

    [Fact]
    public async Task A_loop_that_does_not_fit_the_budget_is_refused_whole_and_the_previous_scene_stays()
    {
        await using var rig = await StartAsync();
        var uploads = await SettledAsync(rig.Adapter);
        var scene = rig.Engine.Loop.Scene;
        var (cid, _, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        rig.Adapter.Fits = false;

        var applied = await rig.Packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeFalse();
        applied.Detail.Should().StartWith("Not applied").And.Contain("does not fit").And.Contain("never decimated");
        rig.Engine.Loop.Scene.Should().BeSameAs(scene);
        await Task.Delay(300);
        rig.Adapter.Uploaded.Count.Should().Be(uploads);
    }

    [Fact]
    public async Task Invalid_bindings_are_refused_and_the_previous_snapshot_stays_current()
    {
        var blocked = new PackageLoopAdapter("pkg:2", new DeviceBlockedException("offline in this test"));
        await using var rig = await StartAsync(null, blocked);
        var details = await ImportAsync(rig.Packages, Pkg.Lighting());
        var cid = details.Info.ContentId;
        var good = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip")));
        good.BindingId.Should().NotBeNull();

        async Task RefusedAsync(string because, params ScenePackageBindingDto[] b)
        {
            var r = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, b));
            r.BindingId.Should().BeNull(because);
            r.Ready.Should().BeFalse();
            r.Problems.Should().NotBeEmpty(because);
            (await rig.Packages.GetScenePackageBindingsAsync(cid)).BindingId.Should().Be(good.BindingId, "a refused save keeps the previous snapshot");
        }

        await RefusedAsync("missing role");
        await RefusedAsync("duplicate role", Zone("strip"), Zone("strip"));
        await RefusedAsync("unknown role", Zone("strip"), Zone("nope"));
        await RefusedAsync("wrong LED count (16, never resampled)", Zone("strip", Loop, 1));
        await RefusedAsync("unknown controller", Zone("strip", "pkg:absent"));
        await RefusedAsync("unknown zone", Zone("strip", Loop, 7));
        await RefusedAsync("a lighting role on a screen", new ScenePackageBindingDto("strip", ScreenId: FakeScreens.Tl.Id));

        var inactive = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip", "pkg:2")));
        inactive.BindingId.Should().NotBeNull("discovered but inactive targets may be bound");
        inactive.Ready.Should().BeFalse();
        inactive.Problems.Should().NotBeEmpty();

        var scene = rig.Engine.Loop.Scene;
        var applied = await rig.Packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeFalse("an inactive target is never partially applied");
        rig.Engine.Loop.Scene.Should().BeSameAs(scene);
    }

    [Fact]
    public async Task Package_zones_are_never_partially_overridden_but_other_zones_still_change()
    {
        await using var rig = await StartAsync();
        var (cid, _, timeline) = await ImportBoundAsync(rig, Pkg.Lighting());
        (await rig.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
        var scene = rig.Engine.Loop.Scene;

        var steal = () => rig.Rgb.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto(Loop, 0)], "StaticColor", ["#FF0000"]));
        await steal.Should().ThrowAsync<ArgumentException>();
        rig.Engine.Loop.Scene.Should().BeSameAs(scene);

        await rig.Rgb.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto(Loop, 1)], "StaticColor", ["#00FF00"]));
        var merged = rig.Engine.Loop.Scene;
        merged.Package.Should().Be(scene.Package);
        merged.Assignments.Should().Contain(a => a.FrameSetId == timeline);
        rig.Engine.BuildStatus(false).BlockedContent.Should().BeNull();
    }

    [Fact]
    public async Task A_restart_replays_the_package_and_a_missing_package_is_blocked_dark_never_a_default_show()
    {
        var data = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        var zip = Pkg.Lighting();
        try
        {
            string cid;
            var first = await StartAsync(data);
            (cid, _, _) = await ImportBoundAsync(first, zip);
            (await first.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
            await first.StopKeepingDataAsync();

            var second = await StartAsync(data);
            await UntilAsync(() => second.Adapter.For(0).Any(l => l.Strict), "strict upload after restart");
            LastStrict(second.Adapter).Rgb.ToArray().Should().Equal(Pkg.Rgb(200, 8));
            second.Engine.Loop.Scene.Package!.ContentId.Should().Be(cid);
            await second.StopKeepingDataAsync();

            Directory.Delete(Path.Combine(data, ScenePackageStore.DirectoryName, cid), recursive: true);
            await using var third = await StartAsync(data);
            await SettledAsync(third.Adapter);
            var status = third.Engine.BuildStatus(false);
            status.BlockedContent.Should().NotBeNull();
            status.BlockedContent!.Should().Contain(b => b.Contains("BLOCKED", StringComparison.Ordinal) && b.Contains(cid, StringComparison.Ordinal));
            third.Engine.Loop.Scene.Assignments.Should().ContainSingle("no default/Rainbow show replaces missing content")
                .Which.FrameSetId.Should().StartWith(ScenePackageIds.Prefix + cid, "the blocked package assignment itself stays (dark)");
            third.Engine.Loop.Scene.Package!.ContentId.Should().Be(cid, "provenance is kept for diagnostics");
            third.Adapter.Uploaded.Should().OnlyContain(u => u.Loop.Rgb.ToArray().All(b => b == 0), "only black ever reaches the zones");

            // re-importing the identical content repairs it; applying clears the startup block
            await ImportAsync(third.Packages, zip);
            (await third.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
            third.Engine.BuildStatus(false).BlockedContent.Should().BeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // best effort (the last Host deletes it on dispose)
            }
        }
    }

    [Fact]
    public async Task Full_RPC_import_bind_apply_profile_round_trip_with_a_pinned_screen_asset()
    {
        await using var rig = await StartAsync(screenOutput: true);
        await using var pipe = new NamedPipeClientStream(".", rig.Host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var packages = rpc.Attach<IScenePackageService>();
        var rgb = rpc.Attach<IRgbService>();
        var screens = rpc.Attach<IScreenService>();
        rpc.StartListening();

        var details = await ImportAsync(packages, Pkg.GenericFixture());
        var cid = details.Info.ContentId;
        cid.Should().Be("95d4603bf1dc513d727bcf603eea27f61836979668d845aac67a5cb4ef7d405b");
        details.Roles.Select(r => (r.Role, r.Kind)).Should().Equal(("strip", "lighting"), ("panel", "screen"));
        var panel = details.Roles[1];
        (panel.Width, panel.Height, panel.Frames, panel.LedCount, panel.DurationMs).Should().Be((64, 32, 1, 0, 12000.0));
        panel.PreviewPngBase64.Should().NotBeNullOrEmpty();

        var state = await packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip"), new ScenePackageBindingDto("panel", ScreenId: FakeScreens.Tl.Id)));
        state.Ready.Should().BeTrue(string.Join("; ", state.Problems));
        var timeline = ScenePackageIds.TimelineIdOf(cid, state.BindingId!);

        var applied = await packages.ApplyScenePackageCheckedAsync(new(cid, state.BindingId!));
        applied.Applied.Should().BeTrue(applied.Detail);
        var scene = await rgb.GetSceneAsync();
        scene.Package.Should().Be(new ScenePackageRefDto(cid, state.BindingId!, Pkg.Contract));
        scene.Assignments.Should().ContainSingle(a => a.FrameSetId == timeline);
        var screen = scene.Screens!.Assignments.Single(a => a.ScreenId == FakeScreens.Tl.Id);
        screen.TimelineId.Should().Be(timeline);
        var asset = screen.BackgroundAssetId!;

        var delete = () => screens.DeleteScreenAssetAsync(asset);
        await delete.Should().ThrowAsync<RemoteInvocationException>("package media is protected from generic deletion");
        (await screens.ListScreenAssetsAsync()).Should().Contain(a => a.Id == asset);

        await rgb.SaveProfileAsync(new ProfileDto("package profile", scene));
        await rgb.SetSceneAsync(new SceneDto([new AssignmentDto([new ZoneRefDto(Loop, 0)], "StaticColor", ["#FF0000"])]));
        (await rgb.GetSceneAsync()).Package.Should().BeNull();

        await rgb.ApplyProfileAsync("package profile");
        var restored = await rgb.GetSceneAsync();
        restored.Package.Should().Be(scene.Package);
        restored.Assignments.Should().ContainSingle(a => a.FrameSetId == timeline);
        (await rgb.LoadProfileAsync("package profile")).Scene.Package.Should().Be(scene.Package);
        (await screens.ListScreensAsync()).Single(d => d.Id == FakeScreens.Tl.Id).State.Should().BeOneOf("Idle", "Running");
    }

    [Fact]
    public async Task A_screen_only_package_keeps_the_RGB_scene_and_uses_a_labelled_apply_epoch()
    {
        await using var rig = await StartAsync(screenOutput: true);
        var before = rig.Engine.Loop.Scene.Assignments;
        var details = await ImportAsync(rig.Packages, Pkg.ScreenOnly(Pkg.Png()));
        var cid = details.Info.ContentId;
        var state = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, new ScenePackageBindingDto("panel", ScreenId: FakeScreens.H2.Id)));
        state.Ready.Should().BeTrue(string.Join("; ", state.Problems));

        var applied = await rig.Packages.ApplyScenePackageAsync(cid);
        applied.Applied.Should().BeTrue(applied.Detail);
        applied.Detail.Should().Contain(TimelineSources.ApplyEpoch);
        rig.Engine.Loop.Scene.Assignments.Should().Equal(before);
        rig.Engine.Loop.Scene.Package!.ContentId.Should().Be(cid);
        var position = rig.Host.Get<ScenePackageTimeline>().Position(ScenePackageIds.TimelineIdOf(cid, state.BindingId!))!;
        position.Source.Should().Be(TimelineSources.ApplyEpoch);
        position.PositionMs.Should().BeInRange(0, 12000);
    }

    [Fact]
    public async Task Draft_unknown_and_tampered_uploads_are_rejected_and_install_nothing()
    {
        await using var rig = await StartAsync();
        foreach (var zip in new[]
        {
            Pkg.Lighting(edit: m => m["format"] = "UnifiedRgb.ScenePackage.Draft"),
            Pkg.Lighting(edit: m => m["playbackContract"] = "rgb24-hold-physical-ms-v1-draft"),
            Pkg.Lighting(edit: m => m["schemaVersion"] = 4), // 1/2/3 are read; unknown versions still fail closed
            Pkg.Zip(Pkg.Entries(Pkg.Lighting()).Select(e => e.Name == "data/strip.rgb24" ? (e.Name, new byte[e.Data.Length]) : (e.Name, e.Data))),
        })
        {
            var import = () => ImportAsync(rig.Packages, zip);
            await import.Should().ThrowAsync<ArgumentException>().WithMessage("Scene package rejected*");
        }

        var bad = () => rig.Packages.ImportScenePackageAsync(new ScenePackageUploadDto("x", "not base64!"));
        await bad.Should().ThrowAsync<ArgumentException>();
        (await rig.Packages.ListScenePackagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_canonical_fixture_imports_with_its_nine_lighting_and_five_screen_roles_unchanged()
    {
        await using var rig = await StartAsync();
        var uploads = await SettledAsync(rig.Adapter);
        var zip = Pkg.FullCaseFixture();
        Pkg.Sha(zip).Should().Be("34e5cb4a359c3f12564caa9e455e0c455200f164a5c28fafa4693a2f2eb69e43");

        var details = await ImportAsync(rig.Packages, zip);
        details.Info.ContentId.Should().Be("638040f39a763d745ed76bed6e01fb00f31b327d0132fef4c40bcc067edb24c9");
        details.Info.PlaybackContract.Should().Be(Pkg.Contract);
        (details.Info.LightingRoles, details.Info.ScreenRoles, details.Info.DurationMs).Should().Be((9, 5, 12000.0));
        details.Roles.Where(r => r.Kind == "lighting").Should().OnlyContain(r => r.Frames == 200 && r.Width == 0 && r.PreviewColors.Count == r.LedCount);
        details.Roles.Where(r => r.Kind == "screen").Should().OnlyContain(r => r.Frames > 1 && r.DurationMs == 12000 && r.PreviewPngBase64 != null);

        rig.Host.Get<ScenePackageStore>().TryGet(details.Info.ContentId, out var installed, out _).Should().BeTrue();
        foreach (var (path, data) in Pkg.Entries(zip).Where(e => e.Name != "scene.json" && e.Name != "bindings.template.json"))
        {
            installed.ReadFile(path).Should().Equal(data, $"{path} is stored byte for byte");
        }

        rig.Adapter.Uploaded.Count.Should().Be(uploads);
        rig.Screens.Opens.Should().Be(0);
    }
}
