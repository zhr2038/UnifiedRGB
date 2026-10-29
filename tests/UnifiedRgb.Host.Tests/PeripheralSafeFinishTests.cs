using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions.Execution;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Revision 4 safe finish. Ports the independent review probes (IndependentStockReviewTests, whose red TRX stays untouched in
/// the reviewer's folder) with the corrected expectations, plus regressions: no status read, refresh, connection, queued UI
/// update or completed lease sends an Exit or Enter; only the user's explicit finish does, exactly once. The Host has no
/// lease-scoped finish, so nothing falls back to an ordinary Exit. The real Host handoff service runs over a temp data
/// directory with fake release/resume; no Host process, pipe, PnP/HID device, browser or window.
/// </summary>
#pragma warning disable VSTHRD003 // Test-owned tasks script delayed and lost answers.
[Collection("Host")]
public sealed class PeripheralSafeFinishTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T07:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    // "自动" is only allowed negated (不/不会/未) or for the read-only status re-read; anything else would claim an automatic recovery.
    private static readonly Regex AutomaticClaim = new("(?<!不会|不|未)自动(?!重新读取状态)", RegexOptions.CultureInvariant);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "urgb-safe-finish-" + Guid.NewGuid().ToString("N"));

    private static PeripheralHandoffIntentDto Completed => new(PeripheralHandoffOrigins.FirmwareMaintenance,
        "independent-lease", "fake-updater", PeripheralRoles.Rf, PeripheralRoles.Rf,
        PeripheralMaintenanceLeaseStates.Completed, Now.AddMinutes(1), Now.AddSeconds(-1));

    // Review probe 1, corrected: the real service's ordinary Exit has no expected-lease argument, so the App never sends one by itself.
    [Fact]
    public async Task Probe1_completed_lease_never_clears_a_newer_manual_Gear_Link_request_without_a_new_explicit_action()
    {
        var options = new UnifiedRgbOptions { DataDirectory = _directory };
        var marker = PeripheralConfigurationMarker.For(options);
        marker.Save();
        var resources = new Resources();
        var real = new PeripheralConfigurationService(options, new PeripheralConfigurationGate(marker.Load()), resources);
        var host = new LeaseProxy(real) { Intent = Completed, ManualAfterRead = true };
        var vm = Vm(host, InlineDispatcher.Instance);

        await vm.RefreshAsync();

        using (new AssertionScope())
        {
            host.Intent!.Origin.Should().Be(PeripheralHandoffOrigins.Manual);
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired, "the read itself still showed the completed lease");
            host.ExitCalls.Should().Be(0, "a completed lease never sends an ordinary Exit by itself");
            host.EnterCalls.Should().Be(0);
            File.Exists(MarkerPath()).Should().BeTrue();
            resources.ResumeCalls.Should().Be(0);
        }

        // The next reads show the newer manual request; it stays untouched across reads and a reconnection.
        await vm.RefreshAsync();
        vm.SetConnected(false); vm.SetConnected(true);
        await vm.RefreshAsync();
        using (new AssertionScope())
        {
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualIntent);
            vm.ExitLabel.Should().Be("完成配置，恢复联动");
            vm.OpenGearLinkCommand.CanExecute(null).Should().BeTrue("the user's web configuration stays available");
            host.ExitCalls.Should().Be(0); host.EnterCalls.Should().Be(0);
            File.Exists(MarkerPath()).Should().BeTrue(); resources.ResumeCalls.Should().Be(0);
        }

        // Only a new explicit user action ends it: exactly one ordinary Exit through the real Host guards.
        await vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        using (new AssertionScope())
        {
            host.ExitCalls.Should().Be(1);
            vm.Status!.State.Should().Be("Active");
            File.Exists(MarkerPath()).Should().BeFalse();
            resources.ResumeCalls.Should().Be(1);
        }
    }

    // Review probe 2, corrected: the newest read is Manual but its UI update is still queued; nothing is sent meanwhile or after.
    [Fact]
    public async Task Probe2_queued_ui_update_never_exits_and_the_newer_manual_read_wins_once_applied()
    {
        var host = new CountingHost(Ready(Completed));
        var dispatcher = new QueuedDispatcher();
        var vm = Vm(host, dispatcher);
        await vm.RefreshAsync();
        dispatcher.Drain();
        vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);
        host.Status = Ready(Completed with { Origin = PeripheralHandoffOrigins.Manual });

        await vm.RefreshAsync();

        using (new AssertionScope())
        {
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired, "the newer read is queued, not yet applied");
            host.ExitCalls.Should().Be(0, "the latest read is Manual even before its posted UI update is processed");
            host.EnterCalls.Should().Be(0);
        }
        dispatcher.Drain();
        await vm.RefreshAsync(); dispatcher.Drain();
        vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualIntent);
        host.ExitCalls.Should().Be(0); host.EnterCalls.Should().Be(0);
    }

    // Review probes 3 and 4, corrected to the exact conservative verdicts, with neighbouring role words and one proven control.
    [Theory]
    [InlineData("", "", false, PeripheralFinishKind.RoleUnmatched)]
    [InlineData("Rf", "Rf", true, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData(" ", " ", false, PeripheralFinishKind.RoleUnmatched)]
    [InlineData("rf", "rf", false, PeripheralFinishKind.RoleUnmatched)]
    [InlineData("Usb", "Usb", false, PeripheralFinishKind.RoleUnmatched)]
    [InlineData(null, null, false, PeripheralFinishKind.RoleUnmatched)]
    [InlineData("Rf", "Wired", false, PeripheralFinishKind.RoleUnmatched)]
    [InlineData("", "", true, PeripheralFinishKind.OutcomeUnknown)]
    [InlineData("Rf", "Rf", false, PeripheralFinishKind.ManualFinishRequired)]
    public async Task Probe3_4_unknown_role_or_future_completion_is_never_ready_and_nothing_authorises_an_automatic_finish(
        string? leaseRole, string? currentRole, bool future, PeripheralFinishKind expected)
    {
        var intent = Completed with { LeaseRole = leaseRole, CurrentRole = currentRole, EndedAt = future ? Now.AddSeconds(30) : Completed.EndedAt };
        PeripheralMaintenanceFinish.Evaluate(Ready(intent), Now).Kind.Should().Be(expected).And.NotBe(PeripheralFinishKind.AutoFinish);

        var host = new CountingHost(Ready(intent));
        var vm = Vm(host, InlineDispatcher.Instance);
        for (var i = 0; i < 3; i++) await vm.RefreshAsync();
        using (new AssertionScope())
        {
            vm.Finish!.Kind.Should().Be(expected);
            host.ExitCalls.Should().Be(0); host.EnterCalls.Should().Be(0);
            vm.OpenGearLinkCommand.CanExecute(null).Should().BeFalse();
            vm.KeyboardIntegrationConfirmed.Should().BeFalse();
            vm.CanExit.Should().BeTrue("the user may still finish explicitly through the Host's own guards");
            if (expected != PeripheralFinishKind.ManualFinishRequired)
                vm.StateTitle.Should().NotContain("已就绪").And.NotContain("已完成").And.EndWith("不当作就绪");
        }
    }

    [Theory]
    [InlineData(null, "fake-updater", PeripheralMaintenanceLeaseStates.Completed)]
    [InlineData(" ", "fake-updater", PeripheralMaintenanceLeaseStates.Completed)]
    [InlineData("independent-lease", null, PeripheralMaintenanceLeaseStates.Completed)]
    [InlineData("independent-lease", "", PeripheralMaintenanceLeaseStates.Completed)]
    [InlineData("independent-lease", " ", PeripheralMaintenanceLeaseStates.Active)]
    [InlineData("independent-lease", null, PeripheralMaintenanceLeaseStates.Active)]
    public void Missing_lease_or_owner_evidence_is_an_unknown_outcome_never_running_or_completed(string? lease, string? owner, string state)
    {
        var intent = Completed with { LeaseId = lease, Owner = owner, LeaseState = state };
        var verdict = PeripheralMaintenanceFinish.Evaluate(Ready(intent), Now);
        verdict.Kind.Should().Be(PeripheralFinishKind.OutcomeUnknown, "an unowned lease neither holds the user's controls nor proves completion");
        verdict.FirmwareMaintenance.Should().BeTrue();
    }

    [Fact]
    public void Finish_kinds_keep_their_numeric_values_and_no_observation_yields_the_retired_automatic_finish()
    {
        var expected = new (PeripheralFinishKind Kind, int Value)[]
        {
            (PeripheralFinishKind.NoHandoff, 0), (PeripheralFinishKind.ManualIntent, 1), (PeripheralFinishKind.OriginUnknown, 2),
            (PeripheralFinishKind.MaintenanceRunning, 3), (PeripheralFinishKind.LeaseExpired, 4), (PeripheralFinishKind.MaintenanceFailed, 5),
            (PeripheralFinishKind.OutcomeUnknown, 6), (PeripheralFinishKind.RoleUnmatched, 7), (PeripheralFinishKind.RetryRequired, 8),
            (PeripheralFinishKind.Waiting, 9), (PeripheralFinishKind.AutoFinish, 10), (PeripheralFinishKind.ManualFinishRequired, 11),
        };
        expected.Select(e => (int)e.Kind).Should().Equal(expected.Select(e => e.Value));
        Enum.GetValues<PeripheralFinishKind>().Should().HaveCount(expected.Length);

        // Exhaustive sweep: AutoFinish is never returned, and the completed state needs every piece of evidence at once.
        string?[] origins = [null, PeripheralHandoffOrigins.Manual, PeripheralHandoffOrigins.FirmwareMaintenance, PeripheralHandoffOrigins.Unknown, "firmwaremaintenance"];
        string?[] texts = [null, "", " ", "x"];
        string[] states = [PeripheralMaintenanceLeaseStates.Active, PeripheralMaintenanceLeaseStates.Completed, PeripheralMaintenanceLeaseStates.Failed,
            PeripheralMaintenanceLeaseStates.Unknown, PeripheralMaintenanceLeaseStates.Expired, "completed"];
        string?[] roles = [null, "", PeripheralRoles.Wired, PeripheralRoles.Rf, "rf"];
        DateTimeOffset?[] ended = [null, Now.AddSeconds(-1), Now, Now.AddSeconds(30), Now.AddMinutes(2)];
        DateTimeOffset?[] expires = [null, Now.AddMinutes(1), Now.AddSeconds(-5)];
        PeripheralConfigurationStatusDto[] shapes =
        [
            new(true, "Ready", "s", true, Now, null), new(true, "Ready", "s", false, Now, null), new(true, "Error", "s", false, Now, "e"),
            new(true, "Error", "s", true, Now, "e"), new(true, "Entering", "s", false, Now, null), new(true, "Resuming", "s", false, Now, null),
            new(false, "Active", "s", false, Now, null), new(true, "Active", "s", false, Now, null), new(false, "Ready", "s", true, Now, null),
            new(true, "Weird", "s", true, Now, null),
        ];
        var violations = new List<string>();
        var completed = 0;
        foreach (var origin in origins)
        foreach (var lease in texts)
        foreach (var owner in texts)
        foreach (var state in states)
        foreach (var leaseRole in roles)
        foreach (var currentRole in roles)
        foreach (var end in ended)
        foreach (var expiry in expires)
        foreach (var shape in shapes)
        {
            var intent = origin is null ? null : new PeripheralHandoffIntentDto(origin, lease, owner, leaseRole, currentRole, state, expiry, end);
            var kind = PeripheralMaintenanceFinish.Evaluate(shape with { Intent = intent }, Now).Kind;
            if (kind == PeripheralFinishKind.AutoFinish) violations.Add($"AutoFinish: {intent} {shape}");
            if (kind != PeripheralFinishKind.ManualFinishRequired) continue;
            completed++;
            var proven = origin == PeripheralHandoffOrigins.FirmwareMaintenance && lease == "x" && owner == "x" && state == PeripheralMaintenanceLeaseStates.Completed
                && end is { } e && expiry is { } x && e <= x && e <= Now && leaseRole is PeripheralRoles.Wired or PeripheralRoles.Rf && leaseRole == currentRole
                && shape is { ConfigurationMode: true, State: "Ready", InterfacesReleased: true };
            if (!proven) violations.Add($"ManualFinishRequired without full evidence: {intent} {shape}");
        }
        violations.Should().BeEmpty();
        completed.Should().BeGreaterThan(0, "the proven combination is still recognised");
    }

    [Fact]
    public void The_Host_has_no_lease_scoped_finish_and_the_view_model_has_no_automatic_request_to_fall_back_from()
    {
        typeof(IPeripheralConfigurationService).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(
            new[] { "GetPeripheralConfigurationStatusAsync", "EnterPeripheralConfigurationAsync", "ExitPeripheralConfigurationAsync" },
            "PM-HOST-1 (an atomic lease-scoped finish) is not implemented; only the ordinary, unscoped Exit exists");
        var members = typeof(PeripheralConfigurationViewModel).GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Select(m => m.Name).ToArray();
        members.Should().NotContain(n => n.Contains("auto", StringComparison.OrdinalIgnoreCase),
            "no automatic-finish state or per-lease automatic bookkeeping remains");
    }

    // Every verdict, every automatic path (read, poll, read command, expiry, reconnection, queued UI updates): zero Exit and Enter.
    [Theory]
    [InlineData("no-handoff")]
    [InlineData("manual")]
    [InlineData("unknown-origin")]
    [InlineData("running")]
    [InlineData("expired")]
    [InlineData("failed")]
    [InlineData("outcome-unknown")]
    [InlineData("blank-roles")]
    [InlineData("future-completion")]
    [InlineData("no-owner")]
    [InlineData("role-changed")]
    [InlineData("retry")]
    [InlineData("waiting")]
    [InlineData("completed")]
    public async Task Every_automatic_path_sends_zero_exit_and_enter_and_only_an_explicit_finish_sends_one(string scenario)
    {
        var (status, kind) = Scenario(scenario);
        var host = new CountingHost(status);
        var clock = new ManualClock();
        var ui = new QueuedDispatcher();
        var vm = Vm(host, ui, clock.Now);

        await vm.RefreshAsync(); ui.Drain();
        clock.Advance(TimeSpan.FromSeconds(2)); await vm.RefreshAsync(); ui.Drain();
        await vm.ReadCommand.ExecuteAsync(); ui.Drain();
        clock.Advance(TimeSpan.FromSeconds(11)); vm.ExpireObservation(); ui.Drain();
        vm.Status.Should().BeNull("the observation expired");
        await vm.RefreshAsync(); ui.Drain();
        vm.SetConnected(false); vm.SetConnected(true); ui.Drain();
        await vm.RefreshAsync(); ui.Drain();

        using (new AssertionScope())
        {
            host.ReadCalls.Should().Be(5);
            host.ExitCalls.Should().Be(0); host.EnterCalls.Should().Be(0);
            vm.Finish!.Kind.Should().Be(kind);
            Texts(vm).Where(t => AutomaticClaim.IsMatch(t)).Should().BeEmpty("no text claims an automatic recovery");
        }

        // The explicit finish, where offered, sends exactly one ordinary Exit; where it is withheld, not even a programmatic call sends.
        var offered = vm.CanExit;
        offered.Should().Be(kind is not (PeripheralFinishKind.NoHandoff or PeripheralFinishKind.MaintenanceRunning or PeripheralFinishKind.RetryRequired));
        await vm.ExitCommand.ExecuteAsync().WaitAsync(Guard); ui.Drain();
        await vm.RefreshAsync(); ui.Drain();
        await vm.RefreshAsync(); ui.Drain();
        host.ExitCalls.Should().Be(offered ? 1 : 0);
        host.EnterCalls.Should().Be(0);
    }

    [Fact]
    public async Task Current_and_older_hosts_without_an_intent_or_without_the_handoff_send_nothing_by_themselves()
    {
        // The current Host's exact status shape (it never sends an intent): an unknown origin, kept like a manual request.
        const string current = """{"configurationMode":true,"state":"Ready","detail":"released","interfacesReleased":true,"updatedAt":null,"lastError":null}""";
        var parsed = JsonSerializer.Deserialize(current, ContractsJsonContext.Default.PeripheralConfigurationStatusDto)!;
        parsed.Intent.Should().BeNull();
        JsonSerializer.Serialize(parsed, ContractsJsonContext.Default.PeripheralConfigurationStatusDto).Should().NotContain("intent");
        var host = new CountingHost(parsed);
        var vm = Vm(host, InlineDispatcher.Instance);
        for (var i = 0; i < 3; i++) await vm.RefreshAsync();
        using (new AssertionScope())
        {
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.OriginUnknown);
            vm.OriginNote.Should().Contain("不会自动清除");
            host.ExitCalls.Should().Be(0); host.EnterCalls.Should().Be(0);
            vm.CanExit.Should().BeTrue();
        }
        await vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);
        host.ExitCalls.Should().Be(1);

        // An older Host without the handoff methods: a read-only notice, nothing sent, not probed again on the connection.
        var old = new CountingHost(parsed) { ReadFailure = MethodNotFound() };
        var oldVm = Vm(old, InlineDispatcher.Instance);
        await oldVm.RefreshAsync(); await oldVm.RefreshAsync();
        await oldVm.ExitAsync(); await oldVm.EnterAsync();
        using (new AssertionScope())
        {
            oldVm.IsUnsupported.Should().BeTrue();
            oldVm.Message.Should().Be(PeripheralConfigurationViewModel.UnsupportedMessage);
            old.ReadCalls.Should().Be(1);
            old.ExitCalls.Should().Be(0); old.EnterCalls.Should().Be(0);
            oldVm.CanExit.Should().BeFalse(); oldVm.Finish.Should().BeNull();
        }
    }

    [Fact]
    public async Task Lost_answer_to_an_explicit_finish_is_read_back_once_and_a_newer_manual_request_seen_by_the_readback_is_left_alone()
    {
        var host = new CountingHost(Ready(Completed));
        // The Host never answers and did not apply the Exit; meanwhile the user's Gear Link request arrived.
        host.ExitOverride = _ =>
        {
            host.Status = Ready(Completed with { Origin = PeripheralHandoffOrigins.Manual });
            return new TaskCompletionSource<PeripheralConfigurationStatusDto>().Task;
        };
        var vm = Vm(host, InlineDispatcher.Instance, timeout: TimeSpan.FromMilliseconds(250));
        await vm.RefreshAsync();
        host.ExitCalls.Should().Be(0);

        await vm.ExitCommand.ExecuteAsync().WaitAsync(Guard);

        using (new AssertionScope())
        {
            host.ExitCalls.Should().Be(1);
            host.ReadCalls.Should().Be(2, "one read before the click and one readback");
            vm.Message.Should().Be("恢复联动结果未确认：未收到后台答复，未自动重发；以上为回读状态。");
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualIntent, "the readback, not the lost answer, supplies the state");
        }
        for (var i = 0; i < 3; i++) await vm.RefreshAsync();
        vm.SetConnected(false); vm.SetConnected(true);
        await vm.RefreshAsync();
        host.ExitCalls.Should().Be(1, "neither reads nor a reconnection resend the unanswered request");
        host.EnterCalls.Should().Be(0);
        vm.CanExit.Should().BeTrue("a new explicit click remains the user's choice");
    }

    [Fact]
    public async Task Explicit_finish_sends_exactly_one_exit_even_when_clicked_again_while_it_is_in_flight()
    {
        var host = new CountingHost(Ready(Completed));
        var answer = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ExitOverride = _ => answer.Task;
        var vm = Vm(host, InlineDispatcher.Instance);
        await vm.RefreshAsync();
        vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualFinishRequired);

        var first = vm.ExitCommand.ExecuteAsync();
        await vm.ExitCommand.ExecuteAsync();
        await vm.ExitAsync();
        await vm.RefreshAsync(); // an in-flight request owns the feedback: no read either
        using (new AssertionScope())
        {
            host.ExitCalls.Should().Be(1);
            host.ReadCalls.Should().Be(1);
            vm.CanExit.Should().BeFalse(); vm.Status.Should().BeNull();
        }

        var resumed = new PeripheralConfigurationStatusDto(false, "Active", "fake resumed", false, Now, null);
        host.Status = resumed;
        answer.SetResult(resumed);
        await first.WaitAsync(Guard);
        await vm.RefreshAsync();
        using (new AssertionScope())
        {
            host.ExitCalls.Should().Be(1);
            vm.Status!.State.Should().Be("Active");
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.NoHandoff);
            vm.KeyboardIntegrationConfirmed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Delayed_reads_on_a_queued_dispatcher_apply_only_the_newest_read_and_send_nothing()
    {
        var host = new CountingHost(Ready(Completed)) { HoldReads = true };
        var ui = new QueuedDispatcher();
        var vm = Vm(host, ui);
        var older = vm.RefreshAsync();
        var newer = vm.RefreshAsync();
        host.PendingReads.Should().HaveCount(2);

        host.PendingReads[1].SetResult(Ready(Completed with { Origin = PeripheralHandoffOrigins.Manual }));
        await newer.WaitAsync(Guard);
        host.PendingReads[0].SetResult(Ready(Completed)); // the older read answers last, still showing the completed lease
        await older.WaitAsync(Guard);
        ui.Drain();

        using (new AssertionScope())
        {
            vm.Finish!.Kind.Should().Be(PeripheralFinishKind.ManualIntent, "the late completed read is older and is never applied");
            host.ExitCalls.Should().Be(0); host.EnterCalls.Should().Be(0);
        }
    }

    private static (PeripheralConfigurationStatusDto Status, PeripheralFinishKind Kind) Scenario(string name) => name switch
    {
        "no-handoff" => (new(false, "Active", "fake", false, Now, null), PeripheralFinishKind.NoHandoff),
        "manual" => (Ready(Completed with { Origin = PeripheralHandoffOrigins.Manual }), PeripheralFinishKind.ManualIntent),
        "unknown-origin" => (Ready(null), PeripheralFinishKind.OriginUnknown),
        "running" => (Ready(Completed with { LeaseState = PeripheralMaintenanceLeaseStates.Active, EndedAt = null }), PeripheralFinishKind.MaintenanceRunning),
        "expired" => (Ready(Completed with { LeaseState = PeripheralMaintenanceLeaseStates.Expired }), PeripheralFinishKind.LeaseExpired),
        "failed" => (Ready(Completed with { LeaseState = PeripheralMaintenanceLeaseStates.Failed }), PeripheralFinishKind.MaintenanceFailed),
        "outcome-unknown" => (Ready(Completed with { LeaseState = PeripheralMaintenanceLeaseStates.Unknown }), PeripheralFinishKind.OutcomeUnknown),
        "blank-roles" => (Ready(Completed with { LeaseRole = "", CurrentRole = "" }), PeripheralFinishKind.RoleUnmatched),
        "future-completion" => (Ready(Completed with { EndedAt = Now.AddSeconds(30) }), PeripheralFinishKind.OutcomeUnknown),
        "no-owner" => (Ready(Completed with { Owner = null }), PeripheralFinishKind.OutcomeUnknown),
        "role-changed" => (Ready(Completed with { CurrentRole = PeripheralRoles.Wired }), PeripheralFinishKind.RoleUnmatched),
        "retry" => (new(true, "Error", "fake", false, Now, "fake", Completed), PeripheralFinishKind.RetryRequired),
        "waiting" => (new(true, "Resuming", "fake", false, Now, null, Completed), PeripheralFinishKind.Waiting),
        "completed" => (Ready(Completed), PeripheralFinishKind.ManualFinishRequired),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static PeripheralConfigurationStatusDto Ready(PeripheralHandoffIntentDto? intent) => new(true, "Ready", "fake", true, Now, null, intent);

    private static IEnumerable<string> Texts(PeripheralConfigurationViewModel vm) =>
        [vm.StateTitle, vm.StatusLine, vm.OriginNote, vm.Guidance, vm.NextStep, vm.Message, vm.EnterLabel, vm.ExitLabel, vm.KeyboardHoldNote];

    private static PeripheralConfigurationViewModel Vm(IPeripheralConfigurationService host, IUiDispatcher ui, Func<DateTimeOffset>? now = null, TimeSpan? timeout = null) =>
        new(() => host, ui, ex => throw new InvalidOperationException("Unexpected VM error", ex), _ => throw new InvalidOperationException("No browser"),
            now ?? (() => Now), timeout ?? TimeSpan.FromSeconds(30));

    private static RemoteMethodNotFoundException MethodNotFound() =>
        (RemoteMethodNotFoundException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException));

    private string MarkerPath() => Path.Combine(_directory, "peripherals", "peripheral-configuration.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class ManualClock
    {
        private DateTimeOffset _value = PeripheralSafeFinishTests.Now;
        public DateTimeOffset Now() => _value;
        public void Advance(TimeSpan by) => _value += by;
    }

    private sealed class Resources : IPeripheralConfigurationResources
    {
        public int ResumeCalls { get; private set; }
        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct) { ResumeCalls++; return Task.CompletedTask; }
    }

    /// <summary>The reviewer's proxy over the real service: each read can flip the origin to a newer manual request right after it.</summary>
    private sealed class LeaseProxy(IPeripheralConfigurationService inner) : IPeripheralConfigurationService
    {
        public PeripheralHandoffIntentDto? Intent { get; set; }
        public bool ManualAfterRead { get; set; }
        public int ExitCalls { get; private set; }
        public int EnterCalls { get; private set; }

        public async Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            var snapshot = (await inner.GetPeripheralConfigurationStatusAsync(ct).ConfigureAwait(false)) with { Intent = Intent };
            if (ManualAfterRead) Intent = Intent! with { Origin = PeripheralHandoffOrigins.Manual };
            return snapshot;
        }

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default)
        {
            EnterCalls++;
            return inner.EnterPeripheralConfigurationAsync(ct);
        }

        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default)
        {
            ExitCalls++;
            return inner.ExitPeripheralConfigurationAsync(ct);
        }
    }

    /// <summary>Scripted Host: counts every call; reads can be held and answered out of order; Exit can be replaced (lost answer).</summary>
    private sealed class CountingHost(PeripheralConfigurationStatusDto status) : IPeripheralConfigurationService
    {
        public int ReadCalls;
        public int EnterCalls;
        public int ExitCalls;
        public PeripheralConfigurationStatusDto Status { get; set; } = status;
        public bool HoldReads { get; init; }
        public Exception? ReadFailure { get; init; }
        public List<TaskCompletionSource<PeripheralConfigurationStatusDto>> PendingReads { get; } = [];
        public Func<CancellationToken, Task<PeripheralConfigurationStatusDto>>? ExitOverride { get; set; }

        public Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ReadCalls);
            if (ReadFailure is not null) return Task.FromException<PeripheralConfigurationStatusDto>(ReadFailure);
            if (!HoldReads) return Task.FromResult(Status);
            var pending = new TaskCompletionSource<PeripheralConfigurationStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (PendingReads) PendingReads.Add(pending);
            return pending.Task;
        }

        public Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref EnterCalls);
            return Task.FromResult(Status);
        }

        public Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ExitCalls);
            return ExitOverride?.Invoke(ct) ?? Task.FromResult(Status = new(false, "Active", "fake resumed", false, Now, null));
        }
    }

    /// <summary>A UI thread that is busy: posted updates wait until the test drains them, in order.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _actions = new();
        public void Post(Action action) => _actions.Enqueue(action);
        public void Drain() { while (_actions.TryDequeue(out var action)) action(); }
    }
}
