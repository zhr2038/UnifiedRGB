using System.IO.Pipes;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

public sealed partial class ScenePackageHostTests
{
    private static ISceneLibraryService Library(Rig rig) => rig.Host.Get<ISceneLibraryService>();

    [Fact]
    public async Task Unreferenced_removal_frees_capacity_keeps_binding_history_and_all_other_package_bytes()
    {
        await using var rig = await StartAsync();
        var (cid, bid, timeline) = await ImportBoundAsync(rig, Pkg.Lighting());
        var other = (await ImportAsync(rig.Packages, Pkg.Lighting(edit: m => m["revision"] = 2))).Info.ContentId;
        var before = rig.Engine.Loop.Scene;
        var store = rig.Host.Get<ScenePackageStore>();
        var original = File.ReadAllBytes(Path.Combine(store.Root, other, "package.zip"));
        rig.Host.Get<ScenePackageFrameSource>().TryGet(timeline, out _).Should().BeTrue();
        var usage = await Library(rig).GetSceneLibraryUsageAsync();
        usage.Packages.Should().Be(2);
        (await Library(rig).InspectScenePackageRemovalAsync(cid)).References.Should().BeEmpty();
        var removed = await Library(rig).RemoveScenePackageAsync(new(cid));
        removed.Removed.Should().BeTrue(); removed.CleanupPending.Should().BeFalse(); removed.ReclaimedBytes.Should().BeGreaterThan(0);
        (await Library(rig).GetSceneLibraryUsageAsync()).Packages.Should().Be(1);
        File.ReadAllBytes(Path.Combine(store.Root, other, "package.zip")).Should().Equal(original);
        rig.Engine.Loop.Scene.Should().BeSameAs(before); rig.Screens.Opens.Should().Be(0);
        rig.Host.Get<ScenePackageFrameSource>().TryGet(timeline, out _).Should().BeFalse("a cached frame set cannot resurrect a removed package");
        (await ImportAsync(rig.Packages, Pkg.Lighting())).Info.ContentId.Should().Be(cid);
        (await rig.Packages.GetScenePackageBindingsAsync(cid)).BindingId.Should().Be(bid);
    }

    [Fact]
    public async Task Current_playback_and_restart_state_block_removal_without_any_write()
    {
        await using var rig = await StartAsync();
        var (cid, _, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        (await rig.Packages.ApplyScenePackageAsync(cid)).Applied.Should().BeTrue();
        var hashes = PackageStoreHashes(rig);
        var plan = await Library(rig).InspectScenePackageRemovalAsync(cid);
        plan.References.Should().Contain("当前灯光播放").And.Contain("重启恢复场景");
        var result = await Library(rig).RemoveScenePackageAsync(new(cid, true));
        result.Removed.Should().BeFalse(); result.ReclaimedBytes.Should().Be(0);
        PackageStoreHashes(rig).Should().BeEquivalentTo(hashes);
    }

    [Fact]
    public async Task Saved_scene_reference_requires_explicit_profile_removal_and_recovery_alias_is_protected()
    {
        await using var rig = await StartAsync();
        var (cid, bid, timeline) = await ImportBoundAsync(rig, Pkg.Lighting());
        var packageScene = new SceneDto([new([new(Loop, 0)], "StaticColor", ["#FFFFFF"], FrameSetId: timeline)], Package: new(cid, bid, Pkg.Contract));
        var profiles = rig.Host.Get<ProfileStore>();
        profiles.Save(new("Keep", packageScene)); profiles.Save(new("Before Lights Off", RedLoop));
        var plan = await Library(rig).InspectScenePackageRemovalAsync(cid);
        plan.References.Should().Contain(r => r.Contains("Keep", StringComparison.Ordinal));
        (await Library(rig).RemoveScenePackageAsync(new(cid))).Removed.Should().BeFalse();
        (await Library(rig).RemoveSavedSceneAsync("Before Lights Off")).Removed.Should().BeFalse();
        (await Library(rig).RemoveSavedSceneAsync("Keep")).Removed.Should().BeTrue();
        (await Library(rig).RemoveScenePackageAsync(new(cid))).Removed.Should().BeTrue();
        await FluentActions.Awaiting(() => Library(rig).RemoveSavedSceneAsync("../outside")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Stale_prepared_profile_or_scene_cannot_be_committed_after_removal()
    {
        await using var rig = await StartAsync();
        var (cid, bid, timeline) = await ImportBoundAsync(rig, Pkg.Lighting());
        var stale = new SceneDto([new([new(Loop, 0)], "StaticColor", ["#FFFFFF"], FrameSetId: timeline)], Package: new(cid, bid, Pkg.Contract));
        (await Library(rig).RemoveScenePackageAsync(new(cid))).Removed.Should().BeTrue();
        var profiles = rig.Host.Get<ProfileStore>();
        FluentActions.Invoking(() => profiles.Save(new("Late", stale))).Should().Throw<ArgumentException>();
        var before = rig.Engine.Loop.Scene;
        await FluentActions.Awaiting(() => rig.Rgb.SetSceneAsync(stale)).Should().ThrowAsync<ArgumentException>();
        rig.Engine.Loop.Scene.Should().BeSameAs(before);
        File.Exists(Path.Combine(profiles.Directory, "Late.json")).Should().BeFalse();
    }

    [Fact]
    public async Task A_profile_save_queued_at_remove_commit_is_revalidated_after_the_detach()
    {
        await using var rig = await StartAsync();
        var (cid, bid, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        var stale = new ProfileDto("Late", new([], Package: new(cid, bid, Pkg.Contract)));
        using var atCommit = new ManualResetEventSlim(); using var proceed = new ManualResetEventSlim();
        rig.Host.Get<ScenePackageStore>().BeforeRemoveCommit = () => { atCommit.Set(); proceed.Wait(TimeSpan.FromSeconds(8)).Should().BeTrue(); };
        var remove = Library(rig).RemoveScenePackageAsync(new(cid));
        (await Task.Run(() => atCommit.Wait(TimeSpan.FromSeconds(8)))).Should().BeTrue();
        var save = Task.Run(() => rig.Host.Get<ProfileStore>().Save(stale));
        proceed.Set();
        (await remove).Removed.Should().BeTrue();
#pragma warning disable VSTHRD003 // A deliberately queued save must finish after the tested removal boundary.
        await FluentActions.Awaiting(async () => await save).Should().ThrowAsync<ArgumentException>();
#pragma warning restore VSTHRD003
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("authored")]
    [InlineData("zip")]
    public async Task Unknown_or_mutated_package_files_refuse_removal_before_detach(string mutation)
    {
        await using var rig = await StartAsync();
        var (cid, _, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        var dir = Path.Combine(rig.Host.Get<ScenePackageStore>().Root, cid);
        var file = mutation switch { "unknown" => "owner-note.txt", "authored" => "files/data/strip.rgb24", _ => "package.zip" };
        File.WriteAllText(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar)), "owner data or corruption");
        if (mutation == "unknown")
            await FluentActions.Awaiting(() => Library(rig).RemoveScenePackageAsync(new(cid))).Should().ThrowAsync<IOException>();
        else
            await FluentActions.Awaiting(() => Library(rig).RemoveScenePackageAsync(new(cid))).Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(dir).Should().BeTrue(); File.ReadAllText(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar))).Should().Be("owner data or corruption");
    }

    [Fact]
    public async Task Unreadable_saved_profile_refuses_inspection_and_does_not_remove_package()
    {
        await using var rig = await StartAsync();
        var (cid, _, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        var profiles = rig.Host.Get<ProfileStore>(); Directory.CreateDirectory(profiles.Directory);
        File.WriteAllText(Path.Combine(profiles.Directory, "Damaged.json"), "{broken");
        await FluentActions.Awaiting(() => Library(rig).RemoveScenePackageAsync(new(cid))).Should().ThrowAsync<InvalidDataException>();
        (await rig.Packages.ListScenePackagesAsync()).Should().Contain(p => p.ContentId == cid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Related_media_is_only_reclaimed_when_explicitly_requested(bool clean)
    {
        await using var rig = await StartAsync();
        var cid = (await ImportAsync(rig.Packages, Pkg.ScreenOnly(Pkg.Png()))).Info.ContentId;
        await rig.Host.Get<IScreenPackageMedia>().InstallPackageAssetAsync("test", Pkg.Png());
        var screens = rig.Host.Get<IScreenService>();
        var asset = (await screens.ListScreenAssetsAsync()).Single().Id;
        var result = await Library(rig).RemoveScenePackageAsync(new(cid, clean));
        result.Removed.Should().BeTrue();
        (await screens.ListScreenAssetsAsync()).Any(a => a.Id == asset).Should().Be(!clean);
        if (clean) result.RemovedAssetIds.Should().Equal(asset); else result.KeptAssetIds.Should().Equal(asset);
        rig.Screens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("profile")]
    [InlineData("active")]
    [InlineData("persisted-screen")]
    public async Task Shared_or_persisted_media_references_protect_normalized_assets(string reference)
    {
        await using var rig = await StartAsync();
        var cid = (await ImportAsync(rig.Packages, Pkg.ScreenOnly(Pkg.Png()))).Info.ContentId;
        await rig.Host.Get<IScreenPackageMedia>().InstallPackageAssetAsync("test", Pkg.Png());
        var screens = rig.Host.Get<IScreenService>(); var asset = (await screens.ListScreenAssetsAsync()).Single().Id;
        var scene = new SceneDto([], Screens: new([new(FakeScreens.H2.Id, asset)]));
        var profiles = rig.Host.Get<ProfileStore>();
        switch (reference)
        {
            case "package": await ImportAsync(rig.Packages, Pkg.Mixed(Pkg.Png())); break;
            case "profile": profiles.Save(new("Saved media", scene)); break;
            case "active": profiles.SaveActive(new("active", scene)); break;
            default:
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(scene.Screens, ContractsJson.Configure(new()));
                SceneStateFiles.Commit(rig.Host.Options.ResolvedDataDirectory, [new("screens/scene.json", bytes)]); break;
        }
        var result = await Library(rig).RemoveScenePackageAsync(new(cid, true));
        result.Removed.Should().BeTrue(); result.KeptAssetIds.Should().Contain(asset);
        (await screens.ListScreenAssetsAsync()).Should().Contain(a => a.Id == asset);
        if (reference is "profile" or "active" or "persisted-screen")
            await FluentActions.Awaiting(() => screens.DeleteScreenAssetAsync(asset)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Library_RPC_round_trip_is_additive_and_read_only_until_explicit_remove()
    {
        await using var rig = await StartAsync();
        var cid = (await ImportAsync(rig.Packages, Pkg.Lighting())).Info.ContentId;
        using var pipe = new NamedPipeClientStream(".", rig.Host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var library = rpc.Attach<ISceneLibraryService>(); rpc.StartListening();
        (await library.GetSceneLibraryUsageAsync()).Packages.Should().Be(1);
        (await library.InspectScenePackageRemovalAsync(cid)).References.Should().BeEmpty();
        (await library.RemoveScenePackageAsync(new(cid))).Removed.Should().BeTrue();
        (await library.GetSceneLibraryUsageAsync()).Packages.Should().Be(0);
    }

    [Fact]
    public async Task A_physical_cleanup_failure_reports_logical_removal_and_can_be_retried_after_restart()
    {
        await using var rig = await StartAsync();
        var cid = (await ImportAsync(rig.Packages, Pkg.Lighting())).Info.ContentId;
        var store = rig.Host.Get<ScenePackageStore>();
        var file = Path.Combine(store.Root, cid, "package.zip");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        try
        {
            var result = await Library(rig).RemoveScenePackageAsync(new(cid));
            result.Removed.Should().BeTrue(); result.CleanupPending.Should().BeTrue(); result.ReclaimedBytes.Should().Be(0);
            var trash = Directory.GetDirectories(store.Root, "~remove-*").Single();
            (await Library(rig).GetSceneLibraryUsageAsync()).PendingCleanupBytes.Should().BeGreaterThan(0);
            file = Path.Combine(trash, "package.zip"); File.SetAttributes(file, FileAttributes.Normal);
            new ScenePackageStore(rig.Host.Options.ResolvedDataDirectory).TryGet(cid, out _, out _).Should().BeFalse();
            Directory.Exists(trash).Should().BeFalse("a later process retries only the known owned trash");
        }
        finally { if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal); }
    }

    [Fact]
    public async Task A_cancelled_removal_never_detaches_or_reclaims_content()
    {
        await using var rig = await StartAsync(); var (cid, _, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        var hashes = PackageStoreHashes(rig);
        await FluentActions.Awaiting(() => Library(rig).RemoveScenePackageAsync(new(cid), cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        PackageStoreHashes(rig).Should().BeEquivalentTo(hashes);
    }

    [Fact]
    public async Task A_full_package_library_can_accept_a_new_package_after_explicit_unreferenced_removal()
    {
        await using var rig = await StartAsync();
        string? first = null;
        for (var i = 1; i <= ScenePackageStore.MaxPackages; i++)
        {
            var revision = i;
            var installed = await ImportAsync(rig.Packages, Pkg.Lighting(edit: m => m["revision"] = revision));
            first ??= installed.Info.ContentId;
        }
        var extra = Pkg.Lighting(edit: m => m["revision"] = 100);
        await FluentActions.Awaiting(() => ImportAsync(rig.Packages, extra)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*full*");
        (await Library(rig).RemoveScenePackageAsync(new(first!))).Removed.Should().BeTrue();
        await ImportAsync(rig.Packages, extra);
        (await Library(rig).GetSceneLibraryUsageAsync()).Packages.Should().Be(ScenePackageStore.MaxPackages);
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("null-list")]
    [InlineData("null-item")]
    public async Task Cache_cleanup_failure_after_detach_returns_an_honest_partial_result(string malformed)
    {
        await using var rig = await StartAsync();
        var cid = (await ImportAsync(rig.Packages, Pkg.ScreenOnly(Pkg.Png()))).Info.ContentId;
        var media = rig.Host.Get<IScreenPackageMedia>();
        var asset = await media.InstallPackageAssetAsync("test", Pkg.Png());
        rig.Host.Get<ScenePackageStore>().BeforeRemoveCommit = () =>
        {
            var path = rig.Host.Get<ProfileStore>().Directory; Directory.CreateDirectory(path);
            var damaged = malformed == "syntax" ? "{broken" : "{\"name\":\"Corrupted concurrently\",\"schemaVersion\":1,\"scene\":{\"assignments\":[],\"screens\":{\"assignments\":"
                + (malformed == "null-list" ? "null" : "[null]") + "}}}";
            File.WriteAllText(Path.Combine(path, "Corrupted concurrently.json"), damaged);
        };
        var result = await Library(rig).RemoveScenePackageAsync(new(cid, true));
        result.Removed.Should().BeTrue(); result.CleanupPending.Should().BeTrue(); result.KeptAssetIds.Should().Contain(asset);
        media.ContainsAsset(asset).Should().BeTrue();
        (await rig.Packages.ListScenePackagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Excessive_profile_names_fail_closed_before_loading_profile_contents()
    {
        await using var rig = await StartAsync(); var (cid, _, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        var profiles = rig.Host.Get<ProfileStore>(); Directory.CreateDirectory(profiles.Directory);
        for (var i = 0; i < 1026; i++) File.WriteAllText(Path.Combine(profiles.Directory, "Entry" + i + ".json"), "{unreadable");
        await FluentActions.Awaiting(() => Library(rig).RemoveScenePackageAsync(new(cid))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*1024*");
        (await rig.Packages.ListScenePackagesAsync()).Should().Contain(p => p.ContentId == cid);
    }
}
