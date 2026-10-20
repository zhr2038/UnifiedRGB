using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class DiscoveryGenerationTests
{
    [Fact]
    public async Task A_cached_snapshot_cannot_recreate_a_worker_after_a_newer_unplug()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1, Devices.Tl2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new(Devices.Tl1.Id), new(Devices.Tl2.Id)));
        var oldSnapshot = rig.Manager.Present(TimeSpan.Zero);
        rig.Manager.Reconcile(oldSnapshot);
        await Wait.UntilAsync(() => rig.Opener.Opens == 2, "both workers open");
        var detached = rig.Opener.For(Devices.Tl1.Id).Single();
        var retained = rig.Opener.For(Devices.Tl2.Id).Single();
        rig.Enumerator.Set(Devices.Tl2);
        rig.Manager.Reconcile(rig.Manager.Present(TimeSpan.Zero));
        detached.Disposed.Should().BeTrue();
        rig.Manager.Reconcile(oldSnapshot); // delayed list RPC arriving after the fresh detach
        rig.Manager.WorkerIds.Should().NotContain(Devices.Tl1.Id, "stale discovery must not recreate even an initializing worker");
        rig.Opener.For(Devices.Tl1.Id).Should().ContainSingle();
        rig.Opener.For(Devices.Tl2.Id).Single().Should().BeSameAs(retained);
        (await rig.ScreenAsync(Devices.Tl1.Id)).State.Should().Be(ScreenStates.Disconnected);
        retained.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task A_late_old_enumeration_cannot_overwrite_the_newer_published_snapshot()
    {
        await using var rig = new Rig(null, Devices.Tl1);
        using var captured = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        rig.Enumerator.AfterSnapshot = call => { if (call == 1) { captured.Set(); release.Wait(TimeSpan.FromSeconds(8)).Should().BeTrue(); } };
        var old = Task.Run(() => rig.Manager.Present(TimeSpan.Zero));
        try
        {
            (await Task.Run(() => captured.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();
            rig.Enumerator.Set(Devices.Tl2);
            var newer = rig.Manager.Present(TimeSpan.Zero);
            newer.Single().Id.Should().Be(Devices.Tl2.Id);
            release.Set();
            var late = await old;
            late.Single().Id.Should().Be(Devices.Tl2.Id);
            rig.Manager.Present(TimeSpan.FromSeconds(1)).Single().Id.Should().Be(Devices.Tl2.Id);
            rig.Opener.Opens.Should().Be(0);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task A_replug_waits_for_the_previous_worker_to_release_its_channel()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1, Devices.Tl2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new(Devices.Tl1.Id), new(Devices.Tl2.Id)));
        rig.Manager.Reconcile(rig.Manager.Present(TimeSpan.Zero));
        await Wait.UntilAsync(() => rig.Opener.Opens == 2, "initial channels");
        var first = rig.Opener.For(Devices.Tl1.Id).Single();
        using var retiring = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        first.BeforeDispose = () => { retiring.Set(); release.Wait(TimeSpan.FromSeconds(8)).Should().BeTrue(); };
        rig.Enumerator.Set(Devices.Tl2);
        var unplugged = rig.Manager.Present(TimeSpan.Zero);
        var detach = Task.Run(() => rig.Manager.Reconcile(unplugged));
        try
        {
            (await Task.Run(() => retiring.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();
            rig.Enumerator.Set(Devices.Tl1, Devices.Tl2);
            var replugged = rig.Manager.Present(TimeSpan.Zero);
            rig.Manager.Reconcile(replugged);
            rig.Manager.WorkerIds.Should().NotContain(Devices.Tl1.Id, "a replacement must not even initialize while the old channel is closing");
            rig.Opener.For(Devices.Tl1.Id).Should().ContainSingle("the previous worker still owns the channel");
            release.Set(); await detach;
            rig.Manager.Reconcile(replugged);
            await Wait.UntilAsync(() => rig.Opener.For(Devices.Tl1.Id).Length == 2, "replacement after old close");
            first.Disposed.Should().BeTrue();
            rig.Opener.For(Devices.Tl2.Id).Should().ContainSingle().Which.Disposed.Should().BeFalse();
        }
        finally { release.Set(); }
    }
}
