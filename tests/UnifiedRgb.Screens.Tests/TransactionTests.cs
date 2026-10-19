using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The screen mutation gate: scene transactions and asset deletes are serialized, in both race orders, and a failed, disposed or
/// cancelled transaction changes nothing and frees the gate. Ordering uses explicit barriers (task completion sources and the
/// manager's waiter count), never sleeps.
/// </summary>
public sealed class TransactionTests : IDisposable
{
    // bounds every gate operation: a leaked gate fails the test (OperationCanceledException) instead of hanging the run
    private readonly CancellationTokenSource _guard = new(TimeSpan.FromSeconds(30));

    private static readonly ScreenSceneDto Keep = Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#111111"));

    [Fact]
    public async Task A_delete_waits_for_a_transaction_that_validated_the_asset_and_is_then_refused()
    {
        await using var rig = new Rig();
        var asset = await UploadAsync(rig);
        var uses = Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, asset.Id));

        using var transaction = await rig.Manager.BeginSceneAsync(uses, Guard);
        rig.Manager.MutationHeld.Should().BeTrue();
        var delete = rig.Manager.DeleteScreenAssetAsync(asset.Id, Guard);
        await Wait.UntilAsync(() => rig.Manager.MutationWaiters == 1, "the delete to queue behind the transaction");
        delete.IsCompleted.Should().BeFalse("the transaction still holds the gate");

        transaction.Commit();

        await FluentActions.Awaiting(Wait.Outcome(delete)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*used by the current screen scene*");
        (await rig.Manager.ListScreenAssetsAsync()).Select(a => a.Id).Should().Equal(asset.Id);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(uses);
        rig.Manager.MutationHeld.Should().BeFalse();
        await rig.Manager.PreviewScreenAsync(uses.Assignments[0]); // the media is still readable
    }

    [Fact]
    public async Task A_transaction_waits_for_a_delete_in_progress_and_then_fails_validation()
    {
        await using var rig = new Rig();
        await rig.Manager.SetScreenSceneAsync(Keep, Guard);
        var asset = await UploadAsync(rig);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manager.BeforeAssetDeleteForTest = async ct =>
        {
            entered.TrySetResult();
            await proceed.Task.WaitAsync(ct);
        };

        var delete = rig.Manager.DeleteScreenAssetAsync(asset.Id, Guard);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var set = rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, asset.Id)), Guard);
        await Wait.UntilAsync(() => rig.Manager.MutationWaiters == 1, "the scene change to queue behind the delete");
        set.IsCompleted.Should().BeFalse("it cannot validate the asset while the delete holds the gate");

        proceed.SetResult();

        await delete.WaitAsync(Guard);
        await FluentActions.Awaiting(Wait.Outcome(set)).Should().ThrowAsync<ArgumentException>().WithMessage($"*{asset.Id}*");
        (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty();
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(Keep);
        File.ReadAllText(SceneFile(rig)).Should().NotContain(asset.Id);
        rig.Manager.MutationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_validation_releases_the_gate()
    {
        await using var rig = new Rig();
        await rig.Manager.SetScreenSceneAsync(Keep, Guard);
        var asset = await UploadAsync(rig);

        await FluentActions.Awaiting(() => rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, Rotation: 45)), Guard))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, "0123456789abcdef")), Guard))
            .Should().ThrowAsync<ArgumentException>();

        rig.Manager.MutationHeld.Should().BeFalse();
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(Keep);
        await rig.Manager.DeleteScreenAssetAsync(asset.Id, Guard); // not blocked
    }

    [Fact]
    public async Task Disposing_an_uncommitted_transaction_changes_nothing_and_releases_once()
    {
        await using var rig = new Rig();
        await rig.Manager.SetScreenSceneAsync(Keep, Guard);
        var saved = File.ReadAllBytes(SceneFile(rig));

        var transaction = await rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Tl1.Id, BackgroundColor: "#FF0000")), Guard);
        transaction.Dispose();
        transaction.Dispose(); // no second release (the gate's maximum count is one: an extra release would throw)

        transaction.Invoking(t => t.Commit()).Should().Throw<InvalidOperationException>();
        rig.Manager.MutationHeld.Should().BeFalse();
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(Keep);
        File.ReadAllBytes(SceneFile(rig)).Should().Equal(saved);

        // the gate is a single slot again: one holder, one waiter
        using var first = await rig.Manager.BeginSceneAsync(Keep, Guard);
        var second = rig.Manager.BeginSceneAsync(Keep, Guard);
        await Wait.UntilAsync(() => rig.Manager.MutationWaiters == 1, "the second transaction to wait");
        second.IsCompleted.Should().BeFalse();
        first.Commit();
        using var next = await second.WaitAsync(Guard);
        next.Commit();
        rig.Manager.MutationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task A_transaction_commits_once()
    {
        await using var rig = new Rig();
        var scene = Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#00FF00"));

        var transaction = await rig.Manager.BeginSceneAsync(scene, Guard);
        transaction.Commit();

        transaction.Invoking(t => t.Commit()).Should().Throw<InvalidOperationException>();
        transaction.Dispose(); // after a commit: nothing to release
        rig.Manager.MutationHeld.Should().BeFalse();
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        File.ReadAllText(SceneFile(rig)).Should().Contain("#00FF00");
    }

    [Fact]
    public async Task Cancelled_waiters_change_nothing()
    {
        await using var rig = new Rig();
        await rig.Manager.SetScreenSceneAsync(Keep, Guard);
        var asset = await UploadAsync(rig);
        using var cancelSet = new CancellationTokenSource();
        using var cancelDelete = new CancellationTokenSource();

        var holder = await rig.Manager.BeginSceneAsync(Keep, Guard);
        var set = rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, asset.Id)), cancelSet.Token);
        var delete = rig.Manager.DeleteScreenAssetAsync(asset.Id, cancelDelete.Token);
        await Wait.UntilAsync(() => rig.Manager.MutationWaiters == 2, "both to wait");

        await cancelSet.CancelAsync();
        await cancelDelete.CancelAsync();

        await FluentActions.Awaiting(Wait.Outcome(set)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(Wait.Outcome(delete)).Should().ThrowAsync<OperationCanceledException>();
        rig.Manager.MutationWaiters.Should().Be(0);
        rig.Manager.MutationHeld.Should().BeTrue("only the holder has the gate");
        holder.Dispose();
        rig.Manager.MutationHeld.Should().BeFalse();
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(Keep);
        (await rig.Manager.ListScreenAssetsAsync()).Select(a => a.Id).Should().Equal(asset.Id);
    }

    [Fact]
    public async Task A_delete_cancelled_before_its_files_go_keeps_the_asset_and_frees_the_gate()
    {
        await using var rig = new Rig();
        var asset = await UploadAsync(rig);
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manager.BeforeAssetDeleteForTest = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var delete = rig.Manager.DeleteScreenAssetAsync(asset.Id, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Manager.MutationHeld.Should().BeTrue();
        await cancel.CancelAsync();

        await FluentActions.Awaiting(Wait.Outcome(delete)).Should().ThrowAsync<OperationCanceledException>();
        rig.Manager.MutationHeld.Should().BeFalse();
        (await rig.Manager.ListScreenAssetsAsync()).Select(a => a.Id).Should().Equal(asset.Id);
        rig.Manager.BeforeAssetDeleteForTest = null;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, asset.Id)), Guard); // still valid
    }

    [Fact]
    public async Task Committing_after_the_manager_is_disposed_throws_and_releases()
    {
        await using var rig = new Rig(); // its own dispose repeats the manager's (idempotent)
        await rig.Manager.SetScreenSceneAsync(Keep, Guard);
        var saved = File.ReadAllBytes(SceneFile(rig));
        var transaction = await rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#ABCDEF")), Guard);

        await rig.Manager.DisposeAsync();

        transaction.Invoking(t => t.Commit()).Should().Throw<ObjectDisposedException>();
        transaction.Dispose();
        rig.Manager.MutationHeld.Should().BeFalse();
        File.ReadAllBytes(SceneFile(rig)).Should().Equal(saved);
        await FluentActions.Awaiting(() => rig.Manager.BeginSceneAsync(Keep, Guard)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => rig.Manager.DeleteScreenAssetAsync("0123456789abcdef", Guard)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_delete_whose_rename_fails_keeps_the_asset_listed_and_readable()
    {
        await using var rig = new Rig();
        var asset = await UploadAsync(rig);
        var frame = Path.Combine(rig.DataDirectory, "screens", "assets", asset.Id, "f000.png");

        using (new FileStream(frame, FileMode.Open, FileAccess.Read, FileShare.Read)) // a reader has the frame open
        {
            var failure = await FluentActions.Awaiting(() => rig.Manager.DeleteScreenAssetAsync(asset.Id, Guard)).Should().ThrowAsync<Exception>();
            failure.Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
            rig.Manager.MutationHeld.Should().BeFalse();
            (await rig.Manager.ListScreenAssetsAsync()).Select(a => a.Id).Should().Equal(asset.Id);
            await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, asset.Id)), Guard);
            await rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(Devices.H2.Id, asset.Id));
            await rig.Manager.SetScreenSceneAsync(Keep, Guard);
        }

        await rig.Manager.DeleteScreenAssetAsync(asset.Id, Guard);
        (await rig.Manager.ListScreenAssetsAsync()).Should().BeEmpty();
        Directory.GetDirectories(Path.Combine(rig.DataDirectory, "screens", "assets")).Should().BeEmpty("no renamed leftovers");
    }

    [Fact]
    public async Task The_store_renames_before_it_forgets()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssetStore(root, 1 << 26, 16, NullLogger.Instance);
            var data = await Images.PngAsync(Images.Solid(8, 8, 1, 2, 3));
            var import = await Sta.RunAsync(() => ImageImporter.Import(data, ImportLimits.Default));
            var asset = store.Commit(AssetStore.IdOf(data), "t", import);

            using (new FileStream(Path.Combine(root, asset.Id, "f000.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                store.Invoking(s => s.Delete(asset.Id)).Should().Throw<Exception>().Which.Should()
                    .Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
                store.Find(asset.Id).Should().NotBeNull();
                store.ReadFrame(asset, 0).Should().Equal(File.ReadAllBytes(Path.Combine(root, asset.Id, "f000.png")));
            }

            store.Delete(asset.Id).Should().BeTrue();
            store.Find(asset.Id).Should().BeNull();
            store.Delete(asset.Id).Should().BeFalse();
            Directory.GetDirectories(root).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    public void Dispose() => _guard.Dispose();

    private CancellationToken Guard => _guard.Token;

    private static string SceneFile(Rig rig) => Path.Combine(rig.DataDirectory, "screens", "scene.json");

    private static async Task<ScreenAssetDto> UploadAsync(Rig rig)
    {
        var png = await Images.PngAsync(Images.Solid(24, 24, 10, 200, 30));
        return await rig.Manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("logo", Convert.ToBase64String(png)));
    }
}
