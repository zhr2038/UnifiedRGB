using UnifiedRgb.Contracts;
using System.Text.Json;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CenterToggleCoordinatorTests
{
    private static SceneDto Running(string color = "#FF0000") => new([new([new("lianli:1", 0)], "StaticColor", [color])]);
    private static SceneDto Black() => new([new([new("lianli:1", 0)], "StaticColor", ["#000000"], Brightness: 0)]);
    private static NativeMenuCaseIntent Toggle(ManualTimeProvider time, long sequence = 2, long neutral = 1) => new("center", 1, 0, sequence, time.GetUtcNow(), DesiredAction.Toggle, neutral);

    [Fact]
    public async Task Center_off_to_on_then_next_press_off_uses_existing_backup_and_preserves_screens()
    {
        var host = new MenuHostFake(Black()); host.Profiles[CasePowerCoordinator.BackupProfileName] = new(CasePowerCoordinator.BackupProfileName, Running());
        using var c = new CasePowerCoordinator(host.Connection.Rgb);
        var on = await c.ExecuteAsync(CasePowerIntent.Toggle); var off = await c.ExecuteAsync(CasePowerIntent.Toggle);
        on.ResolvedIntent.Should().Be(CasePowerIntent.Restore); off.ResolvedIntent.Should().Be(CasePowerIntent.Blackout);
        host.Applied.Should().HaveCount(2).And.OnlyContain(s => s.Screens == null);
        CaseStatusSummary.IsBlackout(host.Scene).Should().BeTrue();
        host.Profiles[CasePowerCoordinator.BackupProfileName].Scene.Assignments.Should().BeEquivalentTo(Running().Assignments);
    }

    [Fact]
    public async Task Center_reads_external_UI_state_each_time_instead_of_flipping_a_cached_boolean()
    {
        var host = new MenuHostFake(Running()); using var c = new CasePowerCoordinator(host.Connection.Rgb);
        (await c.ExecuteAsync(CasePowerIntent.Toggle)).ResolvedIntent.Should().Be(CasePowerIntent.Blackout);
        await host.Connection.Rgb.SetSceneAsync(Running("#0000FF")); // Another client deliberately changes state.
        (await c.ExecuteAsync(CasePowerIntent.Toggle)).ResolvedIntent.Should().Be(CasePowerIntent.Blackout);
        host.Profiles[CasePowerCoordinator.BackupProfileName].Scene.Assignments.Should().BeEquivalentTo(Running("#0000FF").Assignments);
    }

    [Fact]
    public async Task Center_toggle_reuses_package_backup_and_preserves_screen_content_exactly()
    {
        var package = new ScenePackageRefDto(new string('a', 64), new string('b', 64), "scene-package-playback-v1");
        var screens = new ScreenSceneDto([new("screen:h2-1", BackgroundColor: "#101010")]);
        var host = new MenuHostFake(Running() with { Package = package, Screens = screens });
        using var c = new CasePowerCoordinator(host.Connection.Rgb);
        await c.ExecuteAsync(CasePowerIntent.Toggle); await c.ExecuteAsync(CasePowerIntent.Toggle);
        host.Applied.Should().HaveCount(2).And.OnlyContain(s => s.Screens == null);
        host.Profiles[CasePowerCoordinator.BackupProfileName].Scene.Package.Should().Be(package);
        host.Profiles[CasePowerCoordinator.BackupProfileName].Scene.Screens.Should().BeNull();
        host.Scene.Package.Should().Be(package); host.Scene.Screens.Should().BeEquivalentTo(screens);
    }

    [Fact]
    public async Task Center_portable_profile_fixtures_parse_and_classify_as_ordinary_data_without_theme_rules()
    {
        using var activeJson = typeof(CenterToggleCoordinatorTests).Assembly.GetManifestResourceStream("CasePower.active.json")!;
        using var backupJson = typeof(CenterToggleCoordinatorTests).Assembly.GetManifestResourceStream("CasePower.before-lights-off.json")!;
        var active = JsonSerializer.Deserialize(activeJson, ContractsJsonContext.Default.ProfileDto)!;
        var backup = JsonSerializer.Deserialize(backupJson, ContractsJsonContext.Default.ProfileDto)!;
        CaseStatusSummary.IsBlackout(active.Scene).Should().BeTrue(); CaseStatusSummary.IsBlackout(backup.Scene).Should().BeFalse();
        var host = new MenuHostFake(active.Scene); host.Profiles[CasePowerCoordinator.BackupProfileName] = backup;
        using var c = new CasePowerCoordinator(host.Connection.Rgb);
        (await c.ExecuteAsync(CasePowerIntent.Toggle)).ResolvedIntent.Should().Be(CasePowerIntent.Restore);
        host.Applied.Should().ContainSingle().Which.Assignments.Should().BeEquivalentTo(backup.Scene.Assignments);
        host.Scene.Package.Should().Be(backup.Scene.Package);
    }

    [Fact]
    public async Task Center_two_queued_distinct_presses_are_not_coalesced_and_read_after_the_previous_write()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new MenuHostFake(Running()) { Intercept = async (call, ct) => { if (call == "SetScene#1") { entered.TrySetResult(); await release.Task.WaitAsync(ct); } } };
        using var c = new CasePowerCoordinator(host.Connection.Rgb);
        var first = c.ExecuteAsync(CasePowerIntent.Toggle); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var calls = host.Calls; var second = c.ExecuteAsync(CasePowerIntent.Toggle); host.Calls.Should().Equal(calls);
        release.SetResult(); var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        results.Select(x => x.ResolvedIntent).Should().Equal(CasePowerIntent.Blackout, CasePowerIntent.Restore);
        host.Applied.Should().HaveCount(2); host.MaxConcurrentCalls.Should().Be(1); CaseStatusSummary.IsBlackout(host.Scene).Should().BeFalse();
    }

    [Fact]
    public async Task Center_two_qualified_presses_queued_during_slow_first_run_both_resolve_after_logical_time_advances()
    {
        var time = new ManualTimeProvider(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new MenuHostFake(Running()) { Intercept = async (call, ct) => { if (call == "SetScene#1") { entered.TrySetResult(); await release.Task.WaitAsync(ct); } } };
        await using var c = new NativeMenuCaseActionCoordinator(() => host.Connection, time); c.Begin("center", 1, 0);
        var first = c.Submit(Toggle(time)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = c.Submit(Toggle(time, 4, 3)); first.Accepted.Should().BeTrue(); second.Accepted.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(3)); // Admission was qualified; do not invent another expiry while it waits.
        release.SetResult();
        var answers = await Task.WhenAll(first.Outcome!, second.Outcome!).WaitAsync(TimeSpan.FromSeconds(5));
        answers.Select(x => x.ResolvedAction).Should().Equal(DesiredAction.EnsureOff, DesiredAction.EnsureOn);
        host.Applied.Should().HaveCount(2); host.MaxConcurrentCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("read-failed")] [InlineData("empty")] [InlineData("malformed")]
    public async Task Center_unknown_or_malformed_current_scene_never_guesses_or_writes(string fault)
    {
        var scene = fault == "empty" ? new SceneDto([]) : fault == "malformed" ? new([new([new("lianli:1", 0)], "", ["#FF0000"])]) : Running();
        var host = new MenuHostFake(scene) { Intercept = (call, _) => fault == "read-failed" && call == "GetScene#1" ? Task.FromException(new IOException("Unreadable")) : Task.CompletedTask };
        using var c = new CasePowerCoordinator(host.Connection.Rgb);
        await ((Func<Task>)(async () => await c.ExecuteAsync(CasePowerIntent.Toggle))).Should().ThrowAsync<Exception>();
        host.Applied.Should().BeEmpty(); host.Profiles.Should().BeEmpty(); host.Calls.Should().NotContain(x => x.StartsWith("SetScene", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GetScene#1")] [InlineData("ListControllers#1")] [InlineData("SaveProfile#1")] [InlineData("GetScene#3")]
    public async Task Center_cancellation_before_write_is_not_permission_for_a_scene_mutation(string at)
    {
        using var stop = new CancellationTokenSource();
        var host = new MenuHostFake(Running()) { Intercept = (call, _) => { if (call == at) stop.Cancel(); return Task.CompletedTask; } };
        using var c = new CasePowerCoordinator(host.Connection.Rgb);
        await ((Func<Task>)(async () => await c.ExecuteAsync(CasePowerIntent.Toggle, stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        host.Applied.Should().BeEmpty(); host.Calls.Should().NotContain(x => x.StartsWith("SetScene", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Center_unknown_write_is_reported_unresolved_never_retried_and_next_explicit_press_rereads()
    {
        var time = new ManualTimeProvider(); var host = new MenuHostFake(Running())
        { Intercept = (call, _) => call == "SetScene#1" ? Task.FromException(new TimeoutException("Unknown scene answer")) : Task.CompletedTask };
        await using var c = new NativeMenuCaseActionCoordinator(() => host.Connection, time); c.Begin("center", 1, 0);
        var first = await c.Submit(Toggle(time)).Outcome!.WaitAsync(TimeSpan.FromSeconds(5));
        first.State.Should().Be(NativeMenuCaseOutcomeState.Unknown); first.ResolvedAction.Should().BeNull(); host.Applied.Should().BeEmpty();
        host.Calls.Count(x => x == "SetScene#1").Should().Be(1);
        await host.Connection.Rgb.SetSceneAsync(Black()); // Only this separate caller changed state after the unknown answer.
        var second = await c.Submit(Toggle(time, 4, 3)).Outcome!.WaitAsync(TimeSpan.FromSeconds(5));
        second.ResolvedAction.Should().Be(DesiredAction.EnsureOn); second.State.Should().Be(NativeMenuCaseOutcomeState.Applied);
    }

    [Fact]
    public void Center_appends_internal_values_without_changing_existing_numbers_or_RPC_DTOs()
    {
        ((int)DesiredAction.EnsureOn, (int)DesiredAction.EnsureOff, (int)DesiredAction.Toggle).Should().Be((1, 2, 4));
        ((int)CasePowerIntent.Blackout, (int)CasePowerIntent.Restore, (int)CasePowerIntent.Toggle).Should().Be((0, 1, 2));
        ((int)RogNativeDecision.Ignore, (int)RogNativeDecision.EnsureOn, (int)RogNativeDecision.EnsureOff, (int)RogNativeDecision.Disarmed, (int)RogNativeDecision.Toggle).Should().Be((0, 1, 2, 3, 4));
        typeof(NativeMenuStatusDto).GetProperties().Should().NotContain(p => p.PropertyType == typeof(DesiredAction));
    }
}

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Center_only_neutral_separated_presses_emit_two_toggles_held_and_up_down_emit_none(byte transport)
    {
        await using var rig = new Rig(transport); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        byte[] masks = [0, 2, 2, 2, 0, 2, 0, 1, 0, 4];
        for (ushort n = 0; n < masks.Length; n++) rig.Input.Send(Notice((ushort)(n + 1), masks[n], n == 0 ? (byte)3 : (byte)1, transport));
        await rig.Input.WaitReads(masks.Length); await rig.Actor.StepAsync(Pixels, default);
        var presses = rig.Events.OfType<NativeMenuPress>().Select(x => x.Intent).ToArray();
        presses.Select(x => x.Action).Should().Equal(DesiredAction.Toggle, DesiredAction.Toggle);
        presses.Select(x => x.Sequence).Should().Equal(2, 6); presses.Select(x => x.NeutralArmSequence).Should().Equal(1, 5);
        rig.Io.Arms.Should().Be(1); rig.Actor.Armed.Should().BeTrue();
    }
}
