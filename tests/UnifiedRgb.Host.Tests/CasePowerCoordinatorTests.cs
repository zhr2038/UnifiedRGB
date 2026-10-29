using System.Text.Json;
using FluentAssertions.Equivalency;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CasePowerCoordinatorTests
{
    private const string Backup = CasePowerCoordinator.BackupProfileName;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Blackout_saves_the_exact_rgb_scene_without_screens_then_blacks_out_every_active_zone()
    {
        var fake = new FakeRgbService(PackageScene(), Controllers());
        using var coordinator = new CasePowerCoordinator(fake);

        var result = await coordinator.ExecuteAsync(CasePowerIntent.Blackout);

        result.Changed.Should().BeTrue();
        var saved = fake.Saved.Should().ContainSingle().Subject;
        saved.Name.Should().Be("Before Lights Off");
        saved.SchemaVersion.Should().Be(ProfileDto.CurrentSchema);
        saved.Scene.Screens.Should().BeNull();
        saved.Scene.Assignments.Should().BeEquivalentTo(PackageScene().Assignments, Exact);
        saved.Scene.Package.Should().Be(PackageScene().Package);
        var sent = fake.Applied.Should().ContainSingle().Subject;
        sent.Screens.Should().BeNull();
        sent.Package.Should().BeNull();
        var black = sent.Assignments.Should().ContainSingle().Subject;
        black.Targets.Should().Equal(new ZoneRefDto("lianli:1", 0), new ZoneRefDto("lianli:1", 1));
        black.EffectId.Should().Be("StaticColor");
        black.Colors.Should().Equal("#000000");
        black.Brightness.Should().Be(0);
        black.Strategy.Should().Be("Vendor");
        CaseStatusSummary.IsBlackout(sent).Should().BeTrue();
        fake.Scene.Screens.Should().BeEquivalentTo(PackageScene().Screens, Exact);
    }

    [Fact]
    public async Task Restore_sends_exactly_the_saved_assignments_and_package_and_leaves_screens_alone()
    {
        var screens = new ScreenSceneDto([new("screen:h2-1", BackgroundColor: "#101010")]);
        var fake = new FakeRgbService(BlackScene(screens));
        fake.Profiles[Backup] = new(Backup, PackageScene()); // written by another tool, screen content included
        using var coordinator = new CasePowerCoordinator(fake);

        var result = await coordinator.ExecuteAsync(CasePowerIntent.Restore);

        result.Changed.Should().BeTrue();
        var sent = fake.Applied.Should().ContainSingle().Subject;
        sent.Screens.Should().BeNull();
        sent.Assignments.Should().BeEquivalentTo(PackageScene().Assignments, Exact);
        sent.Package.Should().Be(PackageScene().Package);
        fake.Saved.Should().BeEmpty();
        fake.Scene.Screens.Should().BeEquivalentTo(screens, Exact);
    }

    [Fact]
    public async Task Blackout_then_restore_returns_the_exact_scene()
    {
        var fake = new FakeRgbService(PackageScene(), Controllers());
        using var coordinator = new CasePowerCoordinator(fake);

        (await coordinator.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeTrue();
        (await coordinator.ExecuteAsync(CasePowerIntent.Restore)).Changed.Should().BeTrue();

        fake.Scene.Should().BeEquivalentTo(PackageScene(), Exact);
        fake.Applied.Should().HaveCount(2).And.OnlyContain(s => s.Screens == null);
    }

    [Fact]
    public async Task Repeated_blackout_keeps_the_first_recovery_point()
    {
        var fake = new FakeRgbService(PackageScene(), Controllers());
        using var coordinator = new CasePowerCoordinator(fake);
        await coordinator.ExecuteAsync(CasePowerIntent.Blackout);

        var again = await coordinator.ExecuteAsync(CasePowerIntent.Blackout);

        again.Changed.Should().BeFalse();
        fake.Saved.Should().ContainSingle();
        fake.Applied.Should().ContainSingle();
        (await coordinator.ExecuteAsync(CasePowerIntent.Restore)).Changed.Should().BeTrue();
        fake.Scene.Assignments.Should().BeEquivalentTo(PackageScene().Assignments, Exact);
        fake.Scene.Package.Should().Be(PackageScene().Package);
    }

    [Fact]
    public async Task Restore_keeps_a_newer_running_scene()
    {
        var fake = new FakeRgbService(NewerScene());
        fake.Profiles[Backup] = new(Backup, PackageScene());
        using var coordinator = new CasePowerCoordinator(fake);

        var result = await coordinator.ExecuteAsync(CasePowerIntent.Restore);

        result.Changed.Should().BeFalse();
        fake.Applied.Should().BeEmpty();
        fake.Calls.Should().NotContain(c => c.StartsWith("LoadProfile", StringComparison.Ordinal));
        fake.Scene.Should().BeEquivalentTo(NewerScene(), Exact);
    }

    [Fact]
    public async Task Restore_keeps_a_newer_scene_that_another_client_started_while_the_backup_loaded()
    {
        var fake = new FakeRgbService(BlackScene());
        fake.Profiles[Backup] = new(Backup, PackageScene());
        fake.Intercept = (call, _) =>
        {
            if (call == "LoadProfile#1") fake.Scene = NewerScene();
            return Task.CompletedTask;
        };
        using var coordinator = new CasePowerCoordinator(fake);

        var result = await coordinator.ExecuteAsync(CasePowerIntent.Restore);

        result.Changed.Should().BeFalse();
        fake.Applied.Should().BeEmpty();
        fake.Scene.Should().BeEquivalentTo(NewerScene(), Exact);
    }

    [Fact]
    public async Task Blackout_sends_nothing_when_another_client_changed_the_rgb_scene_meanwhile()
    {
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, _) =>
        {
            if (call == "SaveProfile#1") fake.Scene = NewerScene();
            return Task.CompletedTask;
        };
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Blackout))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no scene was sent*");

        fake.Applied.Should().BeEmpty();
        fake.Scene.Should().BeEquivalentTo(NewerScene(), Exact);
    }

    [Fact]
    public async Task Blackout_proceeds_when_only_screen_content_changed_meanwhile()
    {
        var otherScreens = new ScreenSceneDto([new("screen:h2-1", BackgroundColor: "#202020")]);
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, _) =>
        {
            if (call == "SaveProfile#1") fake.Scene = fake.Scene with { Screens = otherScreens };
            return Task.CompletedTask;
        };
        using var coordinator = new CasePowerCoordinator(fake);

        (await coordinator.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeTrue();

        fake.Scene.Screens.Should().BeEquivalentTo(otherScreens, Exact);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blackout_without_any_zone_fails_before_saving(bool controllerWithoutZones)
    {
        var fake = new FakeRgbService(PackageScene(), controllerWithoutZones ? [Controller("lianli:1", ControllerStates.Active)] : []);
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Blackout)).Should().ThrowAsync<InvalidOperationException>();

        fake.Saved.Should().BeEmpty();
        fake.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_without_a_recovery_point_propagates_the_load_failure_and_sends_nothing()
    {
        var fake = new FakeRgbService(BlackScene());
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Restore)).Should().ThrowAsync<FileNotFoundException>();

        fake.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_rejects_an_empty_load_reply_and_sends_nothing()
    {
        var fake = new FakeRgbService(BlackScene());
        fake.Profiles[Backup] = null!; // the reply carried no profile
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Restore))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Before Lights Off*");

        fake.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_failure_propagates_and_no_scene_is_sent()
    {
        var failure = new IOException("profiles folder is read-only");
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, _) => call == "SaveProfile#1" ? Task.FromException(failure) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);

        (await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Blackout)).Should().ThrowAsync<IOException>())
            .Which.Should().BeSameAs(failure);

        fake.Calls.Should().NotContain(c => c.StartsWith("SetScene", StringComparison.Ordinal));
        fake.Scene.Should().BeEquivalentTo(PackageScene(), Exact);
    }

    [Theory]
    [InlineData(CasePowerIntent.Blackout, "ListControllers#1")]
    [InlineData(CasePowerIntent.Blackout, "SaveProfile#1")]
    [InlineData(CasePowerIntent.Blackout, "GetScene#2")]
    [InlineData(CasePowerIntent.Restore, "LoadProfile#1")]
    [InlineData(CasePowerIntent.Restore, "GetScene#2")]
    public async Task Cancellation_before_the_scene_write_stops_the_intent(CasePowerIntent intent, string cancelledDuring)
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeRgbService(intent == CasePowerIntent.Blackout ? PackageScene() : BlackScene(), Controllers());
        fake.Profiles[Backup] = new(Backup, PackageScene());
        // The reply still arrives normally: a backend that ignores the token must not let the write through.
        fake.Intercept = (call, _) =>
        {
            if (call == cancelledDuring) cts.Cancel();
            return Task.CompletedTask;
        };
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync(intent, cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        fake.Calls[^1].Should().Be(cancelledDuring, "nothing further may be requested once cancellation is seen");
        fake.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_queued_intent_leaves_without_side_effects_and_without_freeing_the_running_slot()
    {
        var hold = new Hold();
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);
        using var cts = new CancellationTokenSource();

        var running = coordinator.ExecuteAsync(CasePowerIntent.Blackout);
        await hold.Entered;
        var callsWhileRunning = fake.Calls.Length;
        var cancelled = coordinator.ExecuteAsync(CasePowerIntent.Restore, cts.Token);
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => cancelled.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        var next = coordinator.ExecuteAsync(CasePowerIntent.Restore);

        fake.Calls.Should().HaveCount(callsWhileRunning, "neither queued intent may reach the backend while one is running");
        running.IsCompleted.Should().BeFalse();
        hold.Release();
        (await running.WaitAsync(Patience)).Changed.Should().BeTrue();
        (await next.WaitAsync(Patience)).Changed.Should().BeTrue();
        fake.MaxConcurrentCalls.Should().Be(1);
        fake.Scene.Assignments.Should().BeEquivalentTo(PackageScene().Assignments, Exact);
    }

    [Fact]
    public async Task Opposite_intents_run_one_at_a_time_and_the_later_one_reads_the_state_the_earlier_one_left()
    {
        var hold = new Hold();
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);

        var blackout = coordinator.ExecuteAsync(CasePowerIntent.Blackout);
        await hold.Entered;
        var callsBeforeRestore = fake.Calls;
        var restore = coordinator.ExecuteAsync(CasePowerIntent.Restore);
        fake.Calls.Should().Equal(callsBeforeRestore);
        hold.Release();
        var results = await Task.WhenAll(blackout, restore).WaitAsync(Patience);

        // Had Restore read the scene before the black one was applied, it would have kept that scene and reported no change.
        results.Should().OnlyContain(r => r.Changed);
        fake.MaxConcurrentCalls.Should().Be(1);
        fake.Scene.Should().BeEquivalentTo(PackageScene(), Exact);
    }

    [Fact]
    public async Task Dispose_during_a_running_intent_lets_it_finish_and_rejects_queued_and_new_ones()
    {
        var hold = new Hold();
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask;
        var coordinator = new CasePowerCoordinator(fake);

        var running = coordinator.ExecuteAsync(CasePowerIntent.Blackout);
        await hold.Entered;
        var queued = coordinator.ExecuteAsync(CasePowerIntent.Restore);
        coordinator.Dispose();
        var callsAtDispose = fake.Calls.Length;

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Restore)).Should().ThrowAsync<ObjectDisposedException>();
        hold.Release();
        (await running.WaitAsync(Patience)).Changed.Should().BeTrue();
        await FluentActions.Awaiting(() => queued.WaitAsync(Patience)).Should().ThrowAsync<ObjectDisposedException>();
        fake.Calls.Should().HaveCount(callsAtDispose, "nothing may start after Dispose");
        coordinator.Invoking(c => c.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task Requests_beyond_four_pending_are_rejected_without_side_effects()
    {
        var hold = new Hold();
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);

        var admitted = new List<Task<CasePowerIntentResult>> { coordinator.ExecuteAsync(CasePowerIntent.Blackout) };
        await hold.Entered;
        admitted.Add(coordinator.ExecuteAsync(CasePowerIntent.Restore));
        admitted.Add(coordinator.ExecuteAsync(CasePowerIntent.Blackout));
        admitted.Add(coordinator.ExecuteAsync(CasePowerIntent.Restore));
        var calls = fake.Calls.Length;

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Blackout)).Should().ThrowAsync<InvalidOperationException>();

        fake.Calls.Should().HaveCount(calls);
        hold.Release();
        (await Task.WhenAll(admitted).WaitAsync(Patience)).Should().OnlyContain(r => r.Changed);
        (await coordinator.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeTrue("a drained queue admits again");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task Unknown_intent_is_rejected_before_any_backend_call(int value)
    {
        var fake = new FakeRgbService(PackageScene(), Controllers());
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync((CasePowerIntent)value)).Should().ThrowAsync<ArgumentOutOfRangeException>();

        fake.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CasePowerIntent.Blackout, "timeout")]
    [InlineData(CasePowerIntent.Blackout, "cancelled")]
    [InlineData(CasePowerIntent.Blackout, "connection lost")]
    [InlineData(CasePowerIntent.Restore, "timeout")]
    [InlineData(CasePowerIntent.Restore, "cancelled")]
    [InlineData(CasePowerIntent.Restore, "connection lost")]
    public async Task A_scene_write_without_a_reply_is_neither_retried_nor_rolled_back(CasePowerIntent intent, string loss)
    {
        Exception lost = loss switch
        {
            "timeout" => new TimeoutException("no reply"),
            "cancelled" => new OperationCanceledException("caller stopped waiting"),
            _ => new IOException("pipe closed"),
        };
        var fake = new FakeRgbService(intent == CasePowerIntent.Blackout ? PackageScene() : BlackScene(), Controllers());
        fake.Profiles[Backup] = new(Backup, PackageScene());
        // The Host applied the write; only its reply never arrived.
        fake.Intercept = (call, _) => call == "SetScene#1 applied" ? Task.FromException(lost) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);

        (await coordinator.Awaiting(c => c.ExecuteAsync(intent)).Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(lost);

        fake.Applied.Should().ContainSingle();
        fake.Calls[^1].Should().Be("SetScene#1", "no read-back, retry or rollback may follow an unanswered write");
        // Repeating the intent after reading the state back finds it done and writes nothing.
        (await coordinator.ExecuteAsync(intent)).Changed.Should().BeFalse();
        fake.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task A_rejected_scene_write_propagates_unchanged_and_is_not_retried()
    {
        var rejection = new ArgumentException("zone ene:dram/2 does not exist");
        var fake = new FakeRgbService(PackageScene(), Controllers());
        fake.Intercept = (call, _) => call == "SetScene#1" ? Task.FromException(rejection) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);

        (await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Blackout)).Should().ThrowAsync<ArgumentException>())
            .Which.Should().BeSameAs(rejection);

        fake.Calls[^1].Should().Be("SetScene#1");
        fake.Scene.Should().BeEquivalentTo(PackageScene(), Exact);
    }

    [Fact]
    public async Task Blackout_leaves_absent_blocked_contended_and_zoneless_controllers_out_and_blacks_out_only_the_active_zones()
    {
        var fake = new FakeRgbService(MixedScene(), MixedControllers()) { RenderZones = RenderLoopZones };
        using var coordinator = new CasePowerCoordinator(fake);

        var result = await coordinator.ExecuteAsync(CasePowerIntent.Blackout);

        result.Changed.Should().BeTrue();
        fake.Calls.Should().Equal("GetScene#1", "ListControllers#1", "SaveProfile#1", "GetScene#2", "SetScene#1");
        var saved = fake.Saved.Should().ContainSingle().Subject;
        saved.Scene.Assignments.Should().BeEquivalentTo(MixedScene().Assignments, Exact, "the recovery point keeps the absent mouse's part");
        saved.Scene.Package.Should().Be(MixedScene().Package);
        saved.Scene.Screens.Should().BeNull();
        var black = fake.Applied.Should().ContainSingle().Subject.Assignments.Should().ContainSingle().Subject;
        black.Targets.Should().Equal(ActiveZones);
        black.EffectId.Should().Be("StaticColor");
        black.Colors.Should().Equal("#000000");
        black.Brightness.Should().Be(0);
        CaseStatusSummary.IsBlackout(fake.Scene).Should().BeTrue();
        fake.Scene.Screens.Should().BeEquivalentTo(MixedScene().Screens, Exact);
    }

    [Fact]
    public async Task Repeated_blackout_with_an_absent_mouse_writes_once_and_keeps_the_first_recovery_point()
    {
        var fake = new FakeRgbService(MixedScene(), MixedControllers()) { RenderZones = RenderLoopZones };
        using var coordinator = new CasePowerCoordinator(fake);
        (await coordinator.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeTrue();

        (await coordinator.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeFalse();

        fake.Saved.Should().ContainSingle();
        fake.Applied.Should().ContainSingle();
        fake.Profiles[Backup].Scene.Assignments.Should().BeEquivalentTo(MixedScene().Assignments, Exact);
    }

    [Fact]
    public async Task Restore_queued_behind_a_blackout_while_the_mouse_is_away_is_refused_unretried_and_succeeds_once_it_is_back()
    {
        var hold = new Hold();
        var fake = new FakeRgbService(MixedScene(), MixedControllers()) { RenderZones = RenderLoopZones };
        fake.Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask;
        using var coordinator = new CasePowerCoordinator(fake);

        var blackout = coordinator.ExecuteAsync(CasePowerIntent.Blackout);
        await hold.Entered;
        var restore = coordinator.ExecuteAsync(CasePowerIntent.Restore);
        hold.Release();
        (await blackout.WaitAsync(Patience)).Changed.Should().BeTrue();

        // The exact recovery point names the absent mouse, which the Host's strict SetScene still refuses (unchanged here).
        await FluentActions.Awaiting(() => restore.WaitAsync(Patience)).Should().ThrowAsync<ArgumentException>().WithMessage("Unknown zone rog:mouse/0.");
        fake.Calls.Should().Equal("GetScene#1", "ListControllers#1", "SaveProfile#1", "GetScene#2", "SetScene#1",
            "GetScene#3", "LoadProfile#1", "GetScene#4", "SetScene#2");
        fake.Applied.Should().ContainSingle();
        CaseStatusSummary.IsBlackout(fake.Scene).Should().BeTrue();
        fake.MaxConcurrentCalls.Should().Be(1);

        fake.RenderZones = [.. RenderLoopZones, .. MouseZones];
        (await coordinator.ExecuteAsync(CasePowerIntent.Restore)).Changed.Should().BeTrue();

        fake.Saved.Should().ContainSingle();
        fake.Scene.Should().BeEquivalentTo(MixedScene(), Exact);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blackout_without_any_active_zone_fails_before_saving_and_keeps_an_earlier_recovery_point(bool earlierRecoveryPoint)
    {
        var fake = new FakeRgbService(MixedScene(), [.. MixedControllers().Where(c => c.Id != "lianli:1")]) { RenderZones = [new("msi:gpu", 0)] };
        if (earlierRecoveryPoint) fake.Profiles[Backup] = new(Backup, PackageScene());
        using var coordinator = new CasePowerCoordinator(fake);

        await coordinator.Awaiting(c => c.ExecuteAsync(CasePowerIntent.Blackout))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*nothing was saved or sent*");

        fake.Calls.Should().Equal("GetScene#1", "ListControllers#1");
        fake.Saved.Should().BeEmpty();
        fake.Applied.Should().BeEmpty();
        fake.Scene.Should().BeEquivalentTo(MixedScene(), Exact);
        if (earlierRecoveryPoint) fake.Profiles[Backup].Scene.Should().BeEquivalentTo(PackageScene(), Exact);
        else fake.Profiles.Should().BeEmpty();
    }

    // An asleep mouse keeps its part of the running scene. The Host's render loop holds only the active controllers (a contended
    // one too, its writer paused), and its strict SetScene refuses every other zone.
    private static ControllerDto[] MixedControllers() =>
    [
        Controller("lianli:1", ControllerStates.Active, 0, 1),
        Controller("rog:mouse", ControllerStates.Offline, 0, 1),
        Controller("rog:keyboard", ControllerStates.Blocked, 0),
        Controller("msi:gpu", ControllerStates.Contended, 0),
        Controller("asus:board", ControllerStates.BringUp, 0),
        Controller("lianli:port", ControllerStates.Active),
    ];

    private static readonly ZoneRefDto[] ActiveZones = [new("lianli:1", 0), new("lianli:1", 1)];
    private static readonly ZoneRefDto[] RenderLoopZones = [.. ActiveZones, new("msi:gpu", 0)];
    private static readonly ZoneRefDto[] MouseZones = [new("rog:mouse", 0), new("rog:mouse", 1)];

    private static SceneDto MixedScene() =>
        PackageScene() with { Assignments = [PackageScene().Assignments[0], new(MouseZones, "Breathing", ["#00FFFF"], Brightness: 0.6f)] };

    private static EquivalencyAssertionOptions<T> Exact<T>(EquivalencyAssertionOptions<T> options) =>
        options.WithStrictOrdering().ComparingRecordsByMembers();

    // Offline and blocked controllers are not in the Host's render loop and are left out of the lights-off, like the GUI button.
    private static ControllerDto[] Controllers() =>
    [
        Controller("lianli:1", ControllerStates.Active, 0, 1),
        Controller("msi:gpu", ControllerStates.Offline, 0),
        Controller("ene:dram", ControllerStates.Blocked, 0, 1, 2),
    ];

    private static ControllerDto Controller(string id, string state, params int[] zones) =>
        new(id, "Vendor", id, "Class", "Fan", "SN-" + id, [.. zones.Select(z => new ZoneDto(z, "Zone", "Linear", 16, 16, 1))], 30, state, null);

    // Every optional assignment field set, a package pin, and screen content that must never be touched.
    private static SceneDto PackageScene() => new(
        [
            new([new("lianli:1", 0), new("lianli:1", 1)], "Rainbow", ["#FF0000", "#00ff00"], Speed: 0.25f, Brightness: 0.8f,
                Reverse: true, Seed: 7, GroupName: "Front", LoopPolicy: "Generic", Strategy: "Program",
                SpatialPath: new([new(12, [new(new("lianli:1", 0)), new(new("lianli:1", 1), Reverse: true)])]),
                LoopReturn: new(5000, 300, 32, 0.4)),
            new([new("msi:gpu", 0)], "Frames", [], Strategy: "Frames", FrameSetId: "frames-42"),
        ],
        Screens: new([new("screen:h2-1", BackgroundAssetId: "asset-7", Rotation: 90, TimelineId: "package:c0ffee:b1nd")]),
        Package: new("c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00", "b1nd-0001", "rgb24-hold-physical-ms-v1"));

    private static SceneDto NewerScene() => new([new([new("ene:dram", 2)], "Breathing", ["#123456"])]);

    // Lights-off as another client may write it: black colours at full brightness count as blackout too.
    private static SceneDto BlackScene(ScreenSceneDto? screens = null) =>
        new([new([new("lianli:1", 0)], "StaticColor", ["#000000"])], Screens: screens);

    /// <summary>Holds one intercepted call until released, or until its token is cancelled like a pending RPC.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task.WaitAsync(Patience);
        public void Release() => _released.TrySetResult();

        public Task WaitAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            return _released.Task.WaitAsync(Patience, ct);
        }
    }

    /// <summary>
    /// The pipe and the Host in memory. Every value crosses as JSON, so each reply is a fresh copy; SetScene with Screens=null
    /// keeps the screens. Intercept runs before each call ("SetScene#1") and again after a SetScene was applied
    /// ("SetScene#1 applied"); it may block, throw, cancel or change the scene. Every other member fails the test.
    /// </summary>
    private sealed class FakeRgbService(SceneDto initial, IReadOnlyList<ControllerDto>? controllers = null) : IRgbService
    {
        private readonly object _sync = new();
        private readonly List<string> _calls = [];
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private int _active;

        public SceneDto Scene { get; set; } = Copy(initial);
        public IReadOnlyList<ControllerDto> Controllers { get; } = controllers ?? [];
        public Dictionary<string, ProfileDto> Profiles { get; } = new(StringComparer.Ordinal);
        public List<ProfileDto> Saved { get; } = [];
        public List<SceneDto> Applied { get; } = [];
        public int MaxConcurrentCalls { get; private set; }
        public Func<string, CancellationToken, Task>? Intercept { get; set; }

        /// <summary>When set, SetScene rejects a target outside these zones before applying anything, like the Host's strict
        /// SetScene: its render loop has only the active controllers ("Unknown zone").</summary>
        public IReadOnlyCollection<ZoneRefDto>? RenderZones { get; set; }

        public string[] Calls
        {
            get { lock (_sync) return [.. _calls]; }
        }

        public Task<SceneDto> GetSceneAsync(CancellationToken ct = default) => CallAsync("GetScene", () => Copy(Scene), ct);

        public Task<IReadOnlyList<ControllerDto>> ListControllersAsync(CancellationToken ct = default) =>
            CallAsync("ListControllers", () => Controllers, ct);

        public Task SaveProfileAsync(ProfileDto profile, CancellationToken ct = default) => CallAsync("SaveProfile", () =>
        {
            Saved.Add(Copy(profile));
            return Profiles[profile.Name] = Copy(profile);
        }, ct);

        public Task<ProfileDto> LoadProfileAsync(string name, CancellationToken ct = default) => CallAsync("LoadProfile", () =>
            Profiles.TryGetValue(name, out var profile) ? Copy(profile) : throw new FileNotFoundException($"Profile '{name}' not found."), ct);

        public Task SetSceneAsync(SceneDto scene, CancellationToken ct = default) => CallAsync("SetScene", () =>
        {
            if (RenderZones is { } live && scene.Assignments.SelectMany(a => a.Targets).FirstOrDefault(t => !live.Contains(t)) is { } unknown)
                throw new ArgumentException($"Unknown zone {unknown.ControllerId}/{unknown.ZoneId}.");
            Applied.Add(Copy(scene));
            return Scene = Copy(scene with { Screens = scene.Screens ?? Scene.Screens });
        }, ct);

        private async Task<T> CallAsync<T>(string method, Func<T> body, CancellationToken ct)
        {
            string call;
            lock (_sync)
            {
                _counts[method] = _counts.GetValueOrDefault(method) + 1;
                call = method + "#" + _counts[method];
                _calls.Add(call);
                MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, ++_active);
            }
            try
            {
                if (Intercept is { } before) await before(call, ct);
                var value = body();
                if (method == "SetScene" && Intercept is { } after) await after(call + " applied", ct);
                return value;
            }
            finally
            {
                lock (_sync) _active--;
            }
        }

        private static SceneDto Copy(SceneDto value) => JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(value, ContractsJsonContext.Default.SceneDto), ContractsJsonContext.Default.SceneDto)!;

        private static ProfileDto Copy(ProfileDto value) => JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(value, ContractsJsonContext.Default.ProfileDto), ContractsJsonContext.Default.ProfileDto)!;

        private Task<T> Unexpected<T>(string method)
        {
            lock (_sync) _calls.Add(method);
            return Task.FromException<T>(new NotSupportedException(method + " must not be called by the case-power coordinator."));
        }

        public event EventHandler<IReadOnlyList<ControllerDto>>? ControllersChanged { add { } remove { } }
        public event EventHandler<StatusDto>? StatusChanged { add { } remove { } }
        public event EventHandler<IReadOnlyList<TelemetryDto>>? TelemetryTick { add { } remove { } }
        public Task<IReadOnlyList<EffectDto>> ListEffectsAsync(CancellationToken ct = default) => Unexpected<IReadOnlyList<EffectDto>>("ListEffects");
        public Task SetAssignmentAsync(AssignmentDto assignment, CancellationToken ct = default) => Unexpected<bool>("SetAssignment");
        public Task<IReadOnlyList<ProfileInfoDto>> ListProfilesAsync(CancellationToken ct = default) => Unexpected<IReadOnlyList<ProfileInfoDto>>("ListProfiles");
        public Task ApplyProfileAsync(string name, CancellationToken ct = default) => Unexpected<bool>("ApplyProfile");
        public Task<StatusDto> GetStatusAsync(bool diagnostics = false, CancellationToken ct = default) => Unexpected<StatusDto>("GetStatus");
        public Task<IReadOnlyList<TelemetryDto>> GetTelemetryAsync(CancellationToken ct = default) => Unexpected<IReadOnlyList<TelemetryDto>>("GetTelemetry");
        public Task<TakeoverDto> SetTakeoverAsync(string deviceClass, bool enabled, CancellationToken ct = default) => Unexpected<TakeoverDto>("SetTakeover");
        public Task<IReadOnlyList<ControllerDto>> RescanAsync(CancellationToken ct = default) => Unexpected<IReadOnlyList<ControllerDto>>("Rescan");
        public Task<int> SaveLianLiConfigAsync(CancellationToken ct = default) => Unexpected<int>("SaveLianLiConfig");
        public Task<int> ResyncLianLiAsync(CancellationToken ct = default) => Unexpected<int>("ResyncLianLi");
        public Task<IReadOnlyList<MountingDto>> ListMountingsAsync(CancellationToken ct = default) => Unexpected<IReadOnlyList<MountingDto>>("ListMountings");
        public Task<MountingDto> SetMountingAsync(MountingDto mounting, CancellationToken ct = default) => Unexpected<MountingDto>("SetMounting");
        public Task<MountingDto> ResetMountingAsync(string controllerId, CancellationToken ct = default) => Unexpected<MountingDto>("ResetMounting");
        public Task<MountingDto> BeginRingCalibrationAsync(RingCalibrationDto calibration, CancellationToken ct = default) => Unexpected<MountingDto>("BeginRingCalibration");
        public Task<MountingDto> EndRingCalibrationAsync(string controllerId, bool apply, CancellationToken ct = default) => Unexpected<MountingDto>("EndRingCalibration");
        public Task<BringupSessionDto> BeginBringupAsync(string controllerId, int leaseSeconds, CancellationToken ct = default) => Unexpected<BringupSessionDto>("BeginBringup");
        public Task<ProbeResultDto> RunNativeProbeAsync(string controllerId, string probe, CancellationToken ct = default) => Unexpected<ProbeResultDto>("RunNativeProbe");
        public Task<ProbeResultDto> BeginMarkerAsync(MarkerDto marker, CancellationToken ct = default) => Unexpected<ProbeResultDto>("BeginMarker");
        public Task<ProbeResultDto> EndMarkerAsync(string controllerId, CancellationToken ct = default) => Unexpected<ProbeResultDto>("EndMarker");
        public Task<BringupSessionDto> RenewBringupAsync(string controllerId, CancellationToken ct = default) => Unexpected<BringupSessionDto>("RenewBringup");
        public Task<BringupEndDto> EndBringupAsync(string controllerId, CancellationToken ct = default) => Unexpected<BringupEndDto>("EndBringup");
        public Task<PlaybackCalibrationDto> GetPlaybackCalibrationAsync(CancellationToken ct = default) => Unexpected<PlaybackCalibrationDto>("GetPlaybackCalibration");
        public Task<PlaybackCalibrationDto> SetPlaybackCalibrationAsync(PlaybackCalibrationDto calibration, CancellationToken ct = default) => Unexpected<PlaybackCalibrationDto>("SetPlaybackCalibration");
    }
}
