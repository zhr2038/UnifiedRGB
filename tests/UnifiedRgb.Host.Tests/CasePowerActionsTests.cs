using System.Reflection;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Controlled latch tasks exercise simultaneous application requests.
public sealed class CasePowerActionsTests : IDisposable
{
    private readonly List<CasePowerActions> _owned = [];
    private static readonly ScenePackageRefDto Package = new("content", "binding", "v1");
    private static SceneDto Running => new([new([new("case:one", 0)], "Breathing", ["#FFCC00"])],
        new([new("screen:case", BackgroundAssetId: "asset", Rotation: 90, Brightness: .3)]), Package);

    private (CasePowerActions Actions, CasePowerRpc Rpc) Create()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var rpc = (CasePowerRpc)(object)proxy;
        rpc.Scene = Running;
        rpc.Controllers = [new("case:one", "test", "case", "Virtual", "Linear", "",
            [new(0, "zone", "Linear", 4, 0, 0)], 60, "Active", null, true)];
        var actions = new CasePowerActions(() => proxy);
        _owned.Add(actions);
        return (actions, rpc);
    }

    // An asleep mouse keeps its part of the running scene, but the Host's render loop holds only the active controllers.
    private static readonly ControllerDto[] Mixed =
    [
        new("case:one", "test", "case", "Virtual", "Linear", "", [new(0, "zone", "Linear", 4, 0, 0), new(1, "zone", "Linear", 4, 0, 0)], 60, ControllerStates.Active, null, true),
        new("rog:mouse", "test", "mouse", "Mouse", "Point", "", [new(0, "logo", "Point", 1, 0, 0), new(1, "wheel", "Point", 1, 0, 0)], 30, ControllerStates.Offline, null, true),
        new("rog:keyboard", "test", "keyboard", "Keyboard", "Matrix", "", [new(0, "keys", "Matrix", 96, 24, 4)], 30, ControllerStates.Blocked, "Armoury Crate", true),
        new("lianli:port", "test", "port", "Virtual", "Linear", "", [], 60, ControllerStates.Active, null, true),
    ];
    private static readonly ZoneRefDto[] ActiveZones = [new("case:one", 0), new("case:one", 1)];
    private static readonly ZoneRefDto[] MouseZones = [new("rog:mouse", 0), new("rog:mouse", 1)];
    private static SceneDto MixedRunning => new([new(ActiveZones, "Breathing", ["#FFCC00"]), new(MouseZones, "Rainbow", ["#FF0000"])],
        new([new("screen:case", BackgroundAssetId: "asset", Rotation: 90, Brightness: .3)]), Package);

    private (CasePowerActions Actions, CasePowerRpc Rpc) CreateMixed()
    {
        var (actions, rpc) = Create();
        rpc.Scene = MixedRunning; rpc.Controllers = Mixed; rpc.RenderZones = ActiveZones;
        return (actions, rpc);
    }

    [Fact]
    public async Task Off_with_an_absent_mouse_and_a_blocked_keyboard_blacks_out_only_the_active_case_zones()
    {
        var (actions, rpc) = CreateMixed();
        var result = await actions.OffAsync();
        result.Changed.Should().BeTrue();
        rpc.Writes.Should().Equal("save", "set");
        var black = rpc.Submitted.Should().ContainSingle().Subject;
        black.Screens.Should().BeNull(); black.Package.Should().BeNull();
        var off = black.Assignments.Should().ContainSingle().Subject;
        off.Targets.Should().Equal(ActiveZones);
        off.EffectId.Should().Be("StaticColor"); off.Colors.Should().Equal("#000000"); off.Brightness.Should().Be(0);
        rpc.Backup!.Name.Should().Be(CasePowerActions.BackupProfileName);
        rpc.Backup.Scene.Assignments.Should().BeEquivalentTo(MixedRunning.Assignments, o => o.WithStrictOrdering(), "the recovery point keeps the absent mouse's part");
        rpc.Backup.Scene.Package.Should().Be(Package); rpc.Backup.Scene.Screens.Should().BeNull();
        actions.Status.State.Should().Be(CasePowerRequestState.OffRequested);
    }

    [Fact]
    public async Task Duplicate_off_with_an_absent_mouse_writes_once_and_keeps_the_first_recovery_point()
    {
        var (actions, rpc) = CreateMixed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var first = actions.OffAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = actions.OffAsync(); release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        (await first).Changed.Should().BeTrue(); (await second).Changed.Should().BeFalse();
        var backup = rpc.Backup;
        (await actions.OffAsync()).Changed.Should().BeFalse();
        rpc.Backup.Should().BeSameAs(backup); rpc.Writes.Should().Equal("save", "set");
        rpc.Submitted.Single().Assignments.Single().Targets.Should().Equal(ActiveZones);
    }

    [Fact]
    public async Task On_after_off_while_the_mouse_is_away_fails_truthfully_keeps_the_recovery_point_and_restores_once_it_is_back()
    {
        var (actions, rpc) = CreateMixed();
        var screens = rpc.Scene.Screens;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var off = actions.OffAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var on = actions.EnsureOnAsync(); release.SetResult();
        (await off.WaitAsync(TimeSpan.FromSeconds(5))).Changed.Should().BeTrue();
        // The exact recovery point names the absent mouse, which the Host's strict SetScene still refuses (unchanged here).
        await FluentActions.Awaiting(() => on.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<ArgumentException>().WithMessage("Unknown zone rog:mouse/0.");
        rpc.Writes.Should().Equal("save", "set", "rejected"); rpc.Loads.Should().Be(1, "On ran after Off and found its blackout");
        CasePowerActions.IsBlackout(rpc.Scene).Should().BeTrue(); actions.Status.State.Should().Be(CasePowerRequestState.Failed);
        var backup = rpc.Backup;
        rpc.Controllers = [.. Mixed.Select(c => c.Id == "rog:mouse" ? c with { State = ControllerStates.Active } : c)];
        rpc.RenderZones = [.. ActiveZones, .. MouseZones];
        (await actions.EnsureOnAsync()).Changed.Should().BeTrue();
        rpc.Backup.Should().BeSameAs(backup); rpc.Writes.Should().Equal("save", "set", "rejected", "set");
        rpc.Scene.Assignments.Should().BeEquivalentTo(MixedRunning.Assignments, o => o.WithStrictOrdering());
        rpc.Scene.Package.Should().Be(Package); rpc.Scene.Screens.Should().BeSameAs(screens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Off_without_any_active_zone_fails_truthfully_and_leaves_the_recovery_point_alone(bool earlierRecoveryPoint)
    {
        var (actions, rpc) = CreateMixed();
        rpc.Controllers = [.. Mixed.Where(c => c.Id != "case:one")]; rpc.RenderZones = [];
        var earlier = earlierRecoveryPoint ? new ProfileDto(CasePowerActions.BackupProfileName, new(Running.Assignments)) : null;
        rpc.Backup = earlier;
        var running = rpc.Scene;
        await actions.Awaiting(x => x.OffAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("没有可以熄灭的灯光分区。");
        rpc.Writes.Should().BeEmpty(); rpc.Backup.Should().BeSameAs(earlier); rpc.Scene.Should().BeSameAs(running);
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Off_preserves_package_backup_and_never_writes_screen_content()
    {
        var (actions, rpc) = Create();
        var screens = rpc.Scene.Screens;
        var result = await actions.OffAsync();
        result.Changed.Should().BeTrue();
        rpc.Backup!.Scene.Package.Should().Be(Package);
        rpc.Backup.Scene.Screens.Should().BeNull();
        rpc.Scene.Screens.Should().BeSameAs(screens);
        rpc.Submitted.Should().OnlyContain(x => x.Screens == null);
        CasePowerActions.IsBlackout(rpc.Scene).Should().BeTrue();
        actions.Status.State.Should().Be(CasePowerRequestState.OffRequested, "a request is not physical confirmation");
    }

    [Fact]
    public async Task Repeated_off_does_not_overwrite_the_recovery_point()
    {
        var (actions, rpc) = Create();
        await actions.OffAsync(); var backup = rpc.Backup;
        var result = await actions.OffAsync();
        result.Changed.Should().BeFalse(); rpc.Backup.Should().BeSameAs(backup);
        rpc.Writes.Should().Equal("save", "set");
    }

    [Fact]
    public async Task Ensure_on_preserves_an_already_running_new_scene()
    {
        var (actions, rpc) = Create();
        var scene = rpc.Scene;
        (await actions.EnsureOnAsync()).Changed.Should().BeFalse();
        rpc.Scene.Should().BeSameAs(scene); rpc.Loads.Should().Be(0); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_on_restores_rgb_and_package_but_preserves_new_screen_edits()
    {
        var (actions, rpc) = Create();
        await actions.OffAsync();
        var newer = new ScreenSceneDto([new("screen:case", BackgroundColor: "#123456", Rotation: 270)]);
        rpc.Scene = rpc.Scene with { Screens = newer };
        (await actions.EnsureOnAsync()).Changed.Should().BeTrue();
        rpc.Scene.Assignments.Should().BeEquivalentTo(Running.Assignments, options => options.WithStrictOrdering());
        rpc.Scene.Package.Should().Be(Package); rpc.Scene.Screens.Should().BeSameAs(newer);
    }

    [Fact]
    public async Task Explicit_GUI_restore_keeps_its_recovery_point_semantics_even_when_lit()
    {
        var (actions, rpc) = Create();
        rpc.Backup = new(CasePowerActions.BackupProfileName, new([Running.Assignments[0] with { Brightness = .5f }]));
        (await actions.RestoreBackupAsync()).Changed.Should().BeTrue();
        rpc.Scene.Assignments.Single().Brightness.Should().Be(.5f);
    }

    [Fact]
    public async Task Concurrent_off_requests_share_one_snapshot_and_one_scene_write()
    {
        var (actions, rpc) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var first = actions.OffAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = actions.OffAsync();
        rpc.Writes.Should().Equal("save");
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        rpc.Writes.Should().Equal("save", "set");
        (await first).Changed.Should().BeTrue(); (await second).Changed.Should().BeFalse();
    }

    [Fact]
    public async Task Restore_queued_after_off_observes_the_committed_blackout()
    {
        var (actions, rpc) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var off = actions.OffAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var on = actions.EnsureOnAsync(); release.SetResult();
        await Task.WhenAll(off, on).WaitAsync(TimeSpan.FromSeconds(5));
        rpc.Writes.Should().Equal("save", "set", "set"); rpc.Loads.Should().Be(1);
        rpc.Scene.Package.Should().Be(Package);
    }

    [Fact]
    public async Task No_targets_fails_before_overwriting_a_recovery_point()
    {
        var (actions, rpc) = Create(); rpc.Controllers = [];
        await actions.Awaiting(x => x.OffAsync()).Should().ThrowAsync<InvalidOperationException>();
        rpc.Writes.Should().BeEmpty(); actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Save_failure_does_not_send_blackout_or_report_success()
    {
        var (actions, rpc) = Create(); rpc.BeforeSave = () => throw new IOException("save failed");
        var original = rpc.Scene;
        await actions.Awaiting(x => x.OffAsync()).Should().ThrowAsync<IOException>();
        rpc.Scene.Should().BeSameAs(original); rpc.Submitted.Should().BeEmpty();
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Set_failure_retains_snapshot_and_never_claims_completion()
    {
        var (actions, rpc) = Create(); rpc.FailSet = true;
        await actions.Awaiting(x => x.OffAsync()).Should().ThrowAsync<IOException>();
        rpc.Backup.Should().NotBeNull(); rpc.Scene.Package.Should().Be(Package);
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Pre_cancelled_request_has_no_rpc_or_backup_side_effects()
    {
        var (actions, rpc) = Create(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await actions.Awaiting(x => x.OffAsync(stop.Token)).Should().ThrowAsync<OperationCanceledException>();
        rpc.Reads.Should().Be(0); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_recovery_point_does_not_send_a_scene_or_stale_success()
    {
        var (actions, rpc) = Create();
        await actions.Awaiting(x => x.RestoreBackupAsync()).Should().ThrowAsync<FileNotFoundException>();
        rpc.Writes.Should().BeEmpty(); actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Disposing_waits_for_owned_operations_without_admitting_new_requests()
    {
        var (actions, rpc) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var running = actions.OffAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        actions.Dispose();
        await actions.Awaiting(x => x.EnsureOnAsync()).Should().ThrowAsync<ObjectDisposedException>();
        release.SetResult(); await running.WaitAsync(TimeSpan.FromSeconds(5));
        rpc.Writes.Should().Equal("save", "set");
        actions.Dispose();
    }

    public void Dispose() { foreach (var actions in _owned) actions.Dispose(); }
}

public class CasePowerRpc : DispatchProxy
{
    public SceneDto Scene { get; set; } = new([]);
    public IReadOnlyList<ControllerDto> Controllers { get; set; } = [];
    public ProfileDto? Backup { get; set; }
    public List<string> Writes { get; } = [];
    public List<SceneDto> Submitted { get; } = [];
    public Func<Task>? BeforeSave { get; set; }
    public bool FailSet { get; set; }
    public int Reads { get; private set; }
    public int Loads { get; private set; }
    public Func<Task<SceneDto>>? ReadOverride { get; set; }

    /// <summary>When set, SetScene rejects a target outside these zones before applying anything, like the Host's strict
    /// SetScene: its render loop has only the active controllers ("Unknown zone").</summary>
    public IReadOnlyCollection<ZoneRefDto>? RenderZones { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IRgbService.GetSceneAsync): Reads++; return ReadOverride?.Invoke() ?? Task.FromResult(Scene);
            case nameof(IRgbService.ListControllersAsync): return Task.FromResult(Controllers);
            case nameof(IRgbService.SaveProfileAsync): return SaveAsync((ProfileDto)args![0]!);
            case nameof(IRgbService.LoadProfileAsync): Loads++; return Task.FromResult(Backup ?? throw new FileNotFoundException());
            case nameof(IRgbService.SetSceneAsync):
                if (FailSet) return Task.FromException(new IOException("set failed"));
                var next = (SceneDto)args![0]!;
                if (RenderZones is { } live && next.Assignments.SelectMany(a => a.Targets).FirstOrDefault(t => !live.Contains(t)) is { } unknown)
                {
                    Writes.Add("rejected");
                    return Task.FromException(new ArgumentException($"Unknown zone {unknown.ControllerId}/{unknown.ZoneId}."));
                }
                Submitted.Add(next); Writes.Add("set");
                Scene = next.Screens is null ? next with { Screens = Scene.Screens } : next;
                return Task.CompletedTask;
            default: throw new InvalidOperationException("Unexpected fake RPC: " + targetMethod?.Name);
        }
    }
    private async Task SaveAsync(ProfileDto profile)
    {
        Writes.Add("save");
        if (BeforeSave is { } before) await before();
        Backup = profile;
    }
}
