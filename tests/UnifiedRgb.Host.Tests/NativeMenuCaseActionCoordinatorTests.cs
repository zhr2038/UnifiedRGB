using System.Reflection;
using System.Text.Json;
using FluentAssertions.Equivalency;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class NativeMenuCaseActionCoordinatorTests
{
    // Deliberately unlike any id format: the coordinator only compares it ordinally.
    private const string Session = "opaque:7f/é==";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly ManualTimeProvider _time = new();

    [Theory]
    [InlineData(DesiredAction.EnsureOff)]
    [InlineData(DesiredAction.EnsureOn)]
    public async Task A_repeated_sequence_is_replayed_and_a_second_press_in_the_same_direction_writes_nothing(DesiredAction action)
    {
        var host = action == DesiredAction.EnsureOff ? new MenuHostFake(Running()) : new MenuHostFake(Black());
        host.Profiles[CasePowerCoordinator.BackupProfileName] = new(CasePowerCoordinator.BackupProfileName, Running());
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1).Applied.Should().BeTrue();

        var first = menu.Submit(Intent(2, action, 1));
        var repeat = menu.Submit(Intent(2, action, 1));
        var outcome = await first.Outcome!.WaitAsync(Patience);
        var second = await menu.Submit(Intent(4, action, 3)).Outcome!.WaitAsync(Patience);

        first.Accepted.Should().BeTrue();
        repeat.Rejection.Should().Be(NativeMenuRejection.Replayed);
        outcome.State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        outcome.Stale.Should().BeFalse();
        // Only an observation: the case screen still reports Running, so lights-off is pending, never claimed as done.
        outcome.Readback!.State.Should().Be(action == DesiredAction.EnsureOff ? CaseStatusState.Pending : CaseStatusState.Active);
        second.State.Should().Be(NativeMenuCaseOutcomeState.Unchanged, "the coordinator reads the state first and clamps up/up and down/down");
        menu.Submit(Intent(4, action, 3)).Rejection.Should().Be(NativeMenuRejection.Replayed);
        menu.Submit(Intent(3, action, 1)).Rejection.Should().Be(NativeMenuRejection.Replayed);
        host.Applied.Should().ContainSingle();
        CaseStatusSummary.IsBlackout(host.Scene).Should().Be(action == DesiredAction.EnsureOff);
    }

    [Fact]
    public async Task A_disconnected_context_suppresses_its_queue_and_feedback_and_its_session_never_returns()
    {
        var hold = new Hold();
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var running = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        var queued = new[] { menu.Submit(Intent(4, DesiredAction.EnsureOn, 3)), menu.Submit(Intent(6, DesiredAction.EnsureOff, 5)) };
        var callsAtDisconnect = host.Calls;

        var change = menu.Invalidate(Session, 1, disconnected: true);
        var inFlight = await running.Outcome!.WaitAsync(Patience);
        var dropped = await Task.WhenAll(queued.Select(q => q.Outcome!)).WaitAsync(Patience);

        change.Should().BeEquivalentTo(new { Applied = true, Suppressed = 2, InFlight = inFlight.Intent });
        inFlight.State.Should().Be(NativeMenuCaseOutcomeState.Unknown);
        inFlight.Message.Should().Contain("not resent");
        inFlight.Stale.Should().BeTrue();
        inFlight.Readback.Should().BeNull("feedback of a retired context is not refreshed");
        dropped.Should().OnlyContain(o => o.State == NativeMenuCaseOutcomeState.NotSent
            && o.NotSentReason == NativeMenuNotSentReason.ContextRetired && o.Stale && o.Readback == null);
        host.Calls.Should().Equal(callsAtDisconnect, "neither a queued intent nor a readback of the retired context may reach the Host");
        host.Applied.Should().BeEmpty("the held write observed the cancellation before it applied");
        menu.Submit(Intent(8, DesiredAction.EnsureOff, 7)).Rejection.Should().Be(NativeMenuRejection.StaleContext);
        menu.Begin(Session, 2, 1).Applied.Should().BeFalse("a disconnected session is never resumed");

        // The next connection's session works on the same Host, and old-session input stays out of scope.
        menu.Begin("next-session", 1, 1).Applied.Should().BeTrue();
        menu.Submit(Intent(10, DesiredAction.EnsureOff, 9)).Rejection.Should().Be(NativeMenuRejection.OtherSession);
        var next = await menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, session: "next-session")).Outcome!.WaitAsync(Patience);
        next.State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        host.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task A_new_context_with_the_same_profile_retires_the_old_one_and_starts_its_own_sequence()
    {
        var hold = new Hold();
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 7);
        var running = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, profile: 7));
        await hold.Entered;
        var queued = menu.Submit(Intent(4, DesiredAction.EnsureOn, 3, profile: 7));
        var callsAtBegin = host.Calls;

        var change = menu.Begin(Session, 2, 7);
        (await running.Outcome!.WaitAsync(Patience)).Should().Match<NativeMenuCaseOutcome>(o => o.State == NativeMenuCaseOutcomeState.Unknown && o.Stale);
        (await queued.Outcome!.WaitAsync(Patience)).NotSentReason.Should().Be(NativeMenuNotSentReason.ContextRetired);

        change.Should().BeEquivalentTo(new { Applied = true, Suppressed = 1 });
        menu.Begin(Session, 2, 7).Applied.Should().BeFalse();
        menu.Begin(Session, 1, 7).Applied.Should().BeFalse();
        menu.Submit(Intent(6, DesiredAction.EnsureOff, 5, context: 1, profile: 7)).Rejection.Should().Be(NativeMenuRejection.StaleContext);
        menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, context: 3, profile: 7)).Rejection.Should().Be(NativeMenuRejection.UnknownContext);
        menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, context: 2, profile: 8)).Rejection.Should().Be(NativeMenuRejection.ProfileMismatch);
        host.Calls.Should().Equal(callsAtBegin);
        // Out-of-scope input consumed nothing: the new context's own sequence 2 is still fresh.
        var fresh = await menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, context: 2, profile: 7)).Outcome!.WaitAsync(Patience);
        fresh.State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        fresh.Stale.Should().BeFalse();
        host.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task Session_ids_are_opaque_and_compared_ordinally()
    {
        var host = new MenuHostFake(Running());
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin("0x01", 1, 1);

        menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, session: "0x1")).Rejection.Should().Be(NativeMenuRejection.OtherSession);
        menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, session: "0X01")).Rejection.Should().Be(NativeMenuRejection.OtherSession);
        menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, session: "1")).Rejection.Should().Be(NativeMenuRejection.OtherSession);
        menu.Invalidate("1", 1, disconnected: false).Applied.Should().BeFalse("another id leaves the active context alone");
        host.Calls.Should().BeEmpty();
        (await menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, session: "0x01")).Outcome!.WaitAsync(Patience))
            .State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        FluentActions.Invoking(() => menu.Begin("", 1, 1)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => menu.Begin(new string('s', NativeMenuCaseActionCoordinator.MaxSessionIdLength + 1), 1, 1))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => menu.Begin("s", 0, 1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_press_without_a_fresh_neutral_arm_or_with_a_wild_sequence_is_rejected_before_any_backend_call()
    {
        var host = new MenuHostFake(Running());
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1, sequenceFloor: 10);

        var rejections = new[]
        {
            menu.Submit(Intent(9, DesiredAction.EnsureOff, 8)), // before this context
            menu.Submit(Intent(12, DesiredAction.EnsureOff, 9)), // neutral seen before the context began
            menu.Submit(Intent(14, DesiredAction.EnsureOff, 0)), // no neutral at all
            menu.Submit(Intent(16, DesiredAction.EnsureOff, 16)), // "neutral" not before the press
            menu.Submit(Intent(18, DesiredAction.EnsureOff, 15)), // neutral before the previous press: still held
            menu.Submit(Intent(18 + NativeMenuCaseActionCoordinator.MaxSequenceAdvance + 1, DesiredAction.EnsureOff, 19)),
        }.Select(r => r.Rejection);

        rejections.Should().Equal(NativeMenuRejection.Replayed, NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.NoNeutralArm,
            NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.SequenceOutOfRange);
        host.Calls.Should().BeEmpty();
        // The wild sequence consumed nothing, so a properly armed next press is accepted and writes once.
        (await menu.Submit(Intent(20, DesiredAction.EnsureOff, 19)).Outcome!.WaitAsync(Patience))
            .State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        host.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task Held_coalesced_and_ambiguous_input_follow_the_caller_contract()
    {
        var host = new MenuHostFake(Running());
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var start = _time.GetUtcNow();
        (await menu.Submit(Intent(2, DesiredAction.EnsureOff, 1)).Outcome!.WaitAsync(Patience)).State.Should().Be(NativeMenuCaseOutcomeState.Applied);

        var rejections = new List<NativeMenuRejection?>
        {
            // Held: auto-repeat, or rocking to the other side, without passing neutral.
            menu.Submit(Intent(3, DesiredAction.EnsureOff, 1)).Rejection,
            menu.Submit(Intent(4, DesiredAction.EnsureOn, 1)).Rejection,
            // Coalesced or unmapped values are not actions (middle has none at all).
            menu.Submit(Intent(6, default, 5)).Rejection,
            menu.Submit(Intent(8, DesiredAction.EnsureOn | DesiredAction.EnsureOff, 7)).Rejection,
            // The ambiguous press still used up its neutral.
            menu.Submit(Intent(9, DesiredAction.EnsureOn, 7)).Rejection,
            // Timestamps that contradict the clock or the sequence.
            menu.Submit(Intent(11, DesiredAction.EnsureOn, 10, at: start + TimeSpan.FromSeconds(1))).Rejection,
            menu.Submit(Intent(13, DesiredAction.EnsureOn, 12, at: start - TimeSpan.FromMilliseconds(1))).Rejection,
        };
        _time.Advance(TimeSpan.FromSeconds(3));
        rejections.Add(menu.Submit(Intent(15, DesiredAction.EnsureOn, 14, at: start + TimeSpan.FromMilliseconds(500))).Rejection);

        rejections.Should().Equal(NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.Ambiguous,
            NativeMenuRejection.Ambiguous, NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.Ambiguous, NativeMenuRejection.Ambiguous,
            NativeMenuRejection.Expired);
        host.Applied.Should().ContainSingle("only the first, armed press wrote");
        CaseStatusSummary.IsBlackout(host.Scene).Should().BeTrue();
        // A fresh neutral re-arms: the opposite press now restores the recovery point.
        (await menu.Submit(Intent(17, DesiredAction.EnsureOn, 16)).Outcome!.WaitAsync(Patience)).State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        host.Applied.Should().HaveCount(2);
        host.Scene.Assignments.Should().BeEquivalentTo(Running().Assignments, Exact);
    }

    [Fact]
    public async Task Overflow_is_rejected_and_disarms_while_every_acknowledged_request_still_runs_in_order()
    {
        var hold = new Hold();
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var admitted = new List<NativeMenuSubmitResult> { menu.Submit(Intent(2, DesiredAction.EnsureOff, 1)) };
        await hold.Entered;
        admitted.Add(menu.Submit(Intent(4, DesiredAction.EnsureOn, 3)));
        admitted.Add(menu.Submit(Intent(6, DesiredAction.EnsureOff, 5)));
        admitted.Add(menu.Submit(Intent(8, DesiredAction.EnsureOn, 7)));
        var calls = host.Calls;

        var overflow = menu.Submit(Intent(10, DesiredAction.EnsureOff, 9));
        var sameNeutral = menu.Submit(Intent(11, DesiredAction.EnsureOff, 9));

        admitted.Should().HaveCount(NativeMenuCaseActionCoordinator.MaxPending).And.OnlyContain(r => r.Accepted);
        overflow.Rejection.Should().Be(NativeMenuRejection.QueueFull);
        overflow.Message.Should().Contain("not queued");
        sameNeutral.Rejection.Should().Be(NativeMenuRejection.StaleNeutralArm, "the overflowing press disarmed until the next neutral");
        host.Calls.Should().Equal(calls);
        hold.Release();
        var outcomes = await Task.WhenAll(admitted.Select(a => a.Outcome!)).WaitAsync(Patience);
        outcomes.Select(o => o.Intent.Sequence).Should().Equal(2, 4, 6, 8);
        outcomes.Should().OnlyContain(o => o.State == NativeMenuCaseOutcomeState.Applied && !o.Stale);
        host.Applied.Should().HaveCount(4);
        host.MaxConcurrentCalls.Should().Be(1);
        (await menu.Submit(Intent(13, DesiredAction.EnsureOff, 12)).Outcome!.WaitAsync(Patience))
            .State.Should().Be(NativeMenuCaseOutcomeState.Applied, "a drained queue admits again");
    }

    [Fact]
    public async Task Consecutive_opposite_intents_run_in_admission_order_with_one_write_each()
    {
        var hold = new Hold();
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);

        var down = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        var callsWhileHeld = host.Calls;
        var up = menu.Submit(Intent(4, DesiredAction.EnsureOn, 3));
        host.Calls.Should().Equal(callsWhileHeld, "the later intent waits for the earlier one");
        hold.Release();
        var results = await Task.WhenAll(down.Outcome!, up.Outcome!).WaitAsync(Patience);

        // Had up read the scene before down's write, it would have found the lights on and changed nothing.
        results.Should().OnlyContain(o => o.State == NativeMenuCaseOutcomeState.Applied);
        var calls = host.Calls;
        Array.IndexOf(calls, "SetScene#1").Should().BeLessThan(Array.IndexOf(calls, "LoadProfile#1"));
        host.Applied.Should().HaveCount(2);
        CaseStatusSummary.IsBlackout(host.Applied[0]).Should().BeTrue();
        host.Scene.Assignments.Should().BeEquivalentTo(Running().Assignments, Exact);
        host.MaxConcurrentCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_write_whose_answer_never_arrives_is_unknown_read_back_and_not_resent()
    {
        var reply = new Hold();
        // The Host applied the scene; only its answer is lost until the deadline.
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1 applied" ? reply.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var off = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await reply.Entered;

        _time.Advance(NativeMenuCaseActionCoordinator.WriteTimeout);
        var outcome = await off.Outcome!.WaitAsync(Patience);

        outcome.State.Should().Be(NativeMenuCaseOutcomeState.Unknown);
        outcome.Message.Should().Contain("no answer within 10 s").And.Contain("not resent");
        outcome.Readback!.State.Should().Be(CaseStatusState.Pending, "the scene reads black but the case screen has not reported sleeping");
        host.Calls.SkipWhile(c => c != "SetScene#1").Skip(1).Should()
            .Equal(["GetScene#3", "ListControllers#2", "GetScreenScene#1", "ListScreens#1", "GetScene#4"], "only a readback follows");
        // A new explicit press is a new intent, not a retry: it reads first and finds the case already off.
        (await menu.Submit(Intent(4, DesiredAction.EnsureOff, 3)).Outcome!.WaitAsync(Patience))
            .State.Should().Be(NativeMenuCaseOutcomeState.Unchanged);
        host.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task A_write_ignoring_cancellation_holds_its_connection_until_it_ends_and_is_never_retried()
    {
        var stuck = new Hold(ignoresCancellation: true);
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? stuck.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var off = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await stuck.Entered;

        _time.Advance(NativeMenuCaseActionCoordinator.WriteTimeout);
        var outcome = await off.Outcome!.WaitAsync(Patience);
        var callsAfterReadback = host.Calls;
        var blocked = await menu.Submit(Intent(4, DesiredAction.EnsureOn, 3)).Outcome!.WaitAsync(Patience);

        outcome.State.Should().Be(NativeMenuCaseOutcomeState.Unknown);
        outcome.Readback!.State.Should().Be(CaseStatusState.Active, "the write has not landed yet, and the readback says so");
        blocked.State.Should().Be(NativeMenuCaseOutcomeState.NotSent);
        blocked.NotSentReason.Should().Be(NativeMenuNotSentReason.EarlierWriteUnanswered);
        host.Calls.Should().Equal(callsAfterReadback, "nothing is sent while the earlier write is unanswered");

        stuck.Release(); // The late write lands on its own.
        NativeMenuCaseOutcome next;
        var sequence = 4L;
        do
        {
            sequence += 2;
            next = await menu.Submit(Intent(sequence, DesiredAction.EnsureOff, sequence - 1)).Outcome!.WaitAsync(Patience);
            if (next.NotSentReason == NativeMenuNotSentReason.EarlierWriteUnanswered) await Task.Delay(10);
        }
        while (next.NotSentReason == NativeMenuNotSentReason.EarlierWriteUnanswered && sequence < 1000);
        next.State.Should().Be(NativeMenuCaseOutcomeState.Unchanged, "once the call ended, a new press reads the landed write and sends nothing");
        host.Applied.Should().ContainSingle();
        host.Calls.Count(c => c.StartsWith("SaveProfile", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task An_error_answer_is_failed_read_back_and_not_retried()
    {
        var host = new MenuHostFake(Running())
        {
            Intercept = (call, _) => call == "SetScene#1" ? Task.FromException(new ArgumentException("zone lianli:1/1 does not exist")) : Task.CompletedTask,
        };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);

        var outcome = await menu.Submit(Intent(2, DesiredAction.EnsureOff, 1)).Outcome!.WaitAsync(Patience);

        outcome.State.Should().Be(NativeMenuCaseOutcomeState.Failed);
        outcome.Message.Should().Contain("does not exist");
        outcome.Readback!.State.Should().Be(CaseStatusState.Active);
        host.Calls.Count(c => c.StartsWith("SetScene", StringComparison.Ordinal)).Should().Be(1);
        host.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reconnect_never_transfers_an_old_intent_to_the_new_host()
    {
        var hold = new Hold();
        var a = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        var b = new MenuHostFake(Running());
        MenuHostFake? current = a;
        await using var menu = new NativeMenuCaseActionCoordinator(() => current?.Connection ?? throw new InvalidOperationException("not connected"), _time);
        menu.Begin(Session, 1, 1);
        var first = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        var queued = menu.Submit(Intent(4, DesiredAction.EnsureOn, 3));

        current = b;
        hold.Release();
        var finished = await first.Outcome!.WaitAsync(Patience);
        var dropped = await queued.Outcome!.WaitAsync(Patience);
        var callsOnA = a.Calls;

        finished.State.Should().Be(NativeMenuCaseOutcomeState.Applied, "it was already running on its own connection");
        dropped.State.Should().Be(NativeMenuCaseOutcomeState.NotSent);
        dropped.NotSentReason.Should().Be(NativeMenuNotSentReason.HostChanged);
        b.Calls.Should().BeEmpty("an intent accepted on the old connection is never sent to the new Host");
        (await menu.Submit(Intent(6, DesiredAction.EnsureOff, 5)).Outcome!.WaitAsync(Patience)).State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        b.Applied.Should().ContainSingle();
        a.Calls.Should().Equal(callsOnA);
        a.Applied.Should().ContainSingle();

        current = null;
        menu.Submit(Intent(8, DesiredAction.EnsureOn, 7)).Rejection.Should().Be(NativeMenuRejection.NoHost);
        b.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task Stop_cancels_the_running_intent_suppresses_the_queue_and_refuses_new_input()
    {
        var hold = new Hold();
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var running = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        var queued = menu.Submit(Intent(4, DesiredAction.EnsureOn, 3));
        var calls = host.Calls;

        var stop = await menu.StopAsync().WaitAsync(Patience);

        stop.Should().BeEquivalentTo(new { Joined = true, UnansweredWrite = (bool?)false });
        (await running.Outcome!.WaitAsync(Patience)).Should().Match<NativeMenuCaseOutcome>(o =>
            o.State == NativeMenuCaseOutcomeState.Unknown && o.Stale && o.Readback == null);
        (await queued.Outcome!.WaitAsync(Patience)).NotSentReason.Should().Be(NativeMenuNotSentReason.Stopped);
        menu.Submit(Intent(6, DesiredAction.EnsureOff, 5)).Rejection.Should().Be(NativeMenuRejection.Stopped);
        menu.Begin("next-session", 1, 1).Applied.Should().BeFalse();
        host.Calls.Should().Equal(calls);
        host.Applied.Should().BeEmpty();
        await menu.DisposeAsync();
        (await menu.StopAsync().WaitAsync(Patience)).Joined.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_reports_a_backend_call_that_ignored_cancellation_and_nothing_queued_writes_after_it()
    {
        var stuck = new Hold(ignoresCancellation: true);
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? stuck.WaitAsync(ct) : Task.CompletedTask };
        await using var menu = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        menu.Begin(Session, 1, 1);
        var running = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await stuck.Entered;
        var queued = menu.Submit(Intent(4, DesiredAction.EnsureOn, 3));
        var calls = host.Calls;

        var stop = await menu.StopAsync().WaitAsync(Patience);

        stop.Joined.Should().BeTrue();
        stop.UnansweredWrite.Should().BeTrue();
        stop.Message.Should().Contain("unknown");
        (await running.Outcome!.WaitAsync(Patience)).State.Should().Be(NativeMenuCaseOutcomeState.Unknown);
        (await queued.Outcome!.WaitAsync(Patience)).NotSentReason.Should().Be(NativeMenuNotSentReason.Stopped);
        stuck.Release();
        await Eventually(async () => (await menu.StopAsync().WaitAsync(Patience)).UnansweredWrite == false);
        host.Applied.Should().ContainSingle("the late write landed by itself");
        host.Calls.Should().Equal(calls, "no readback, resend or queued intent followed it");
    }

    [Fact]
    public async Task A_join_the_caller_gives_up_on_is_reported_and_nothing_queued_is_sent_after_the_provider_returns()
    {
        var hold = new Hold();
        var host = new MenuHostFake(Running()) { Intercept = (call, ct) => call == "SetScene#1" ? hold.WaitAsync(ct) : Task.CompletedTask };
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var providerGate = new ManualResetEventSlim();
        var block = false;
        // A provider that blocks the consumer, ignoring every cancellation.
        NativeMenuCaseHost? Provider()
        {
            if (Volatile.Read(ref block))
            {
                providerEntered.TrySetResult();
                providerGate.Wait(Patience);
            }
            return host.Connection;
        }
        await using var menu = new NativeMenuCaseActionCoordinator(Provider, _time);
        menu.Begin(Session, 1, 1);
        var first = menu.Submit(Intent(2, DesiredAction.EnsureOff, 1));
        await hold.Entered;
        var second = menu.Submit(Intent(4, DesiredAction.EnsureOn, 3));
        Volatile.Write(ref block, true);
        hold.Release();
        await providerEntered.Task.WaitAsync(Patience);
        var calls = host.Calls;

        var stop = await menu.StopAsync(new CancellationToken(canceled: true)).WaitAsync(Patience);
        providerGate.Set();
        var after = await second.Outcome!.WaitAsync(Patience);

        stop.Should().BeEquivalentTo(new { Joined = false, UnansweredWrite = (bool?)null });
        stop.Message.Should().Contain("has not stopped");
        (await first.Outcome!.WaitAsync(Patience)).State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        after.State.Should().Be(NativeMenuCaseOutcomeState.NotSent);
        after.NotSentReason.Should().Be(NativeMenuNotSentReason.Stopped);
        host.Calls.Should().Equal(calls, "once the provider returned, the stopped coordinator sent nothing");
        (await menu.StopAsync().WaitAsync(Patience)).Joined.Should().BeTrue();
    }

    [Fact]
    public async Task No_rgb_or_screen_call_happens_without_an_explicitly_accepted_intent()
    {
        var host = new MenuHostFake(Running());
        var connected = true;
        var providerReads = 0;
        await using var menu = new NativeMenuCaseActionCoordinator(() =>
        {
            providerReads++;
            return connected ? host.Connection : null;
        }, _time);
        var now = _time.GetUtcNow();

        var rejections = new List<NativeMenuRejection?> { menu.Submit(Intent(2, DesiredAction.EnsureOff, 1)).Rejection };
        menu.Begin(Session, 1, 1);
        rejections.Add(menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, at: now - TimeSpan.FromSeconds(3))).Rejection);
        rejections.Add(menu.Submit(Intent(4, DesiredAction.EnsureOff, 3, session: "other")).Rejection);
        rejections.Add(menu.Submit(Intent(4, DesiredAction.EnsureOff, 3, context: 2)).Rejection);
        rejections.Add(menu.Submit(Intent(4, DesiredAction.EnsureOff, 3, profile: 2)).Rejection);
        rejections.Add(menu.Submit(Intent(0, DesiredAction.EnsureOff, 0)).Rejection);
        rejections.Add(menu.Submit(Intent(5000, DesiredAction.EnsureOff, 4999)).Rejection);
        rejections.Add(menu.Submit(Intent(4, (DesiredAction)99, 3)).Rejection);
        rejections.Add(menu.Submit(Intent(6, DesiredAction.EnsureOff, 0)).Rejection);
        rejections.Add(menu.Submit(Intent(8, DesiredAction.EnsureOff, 5)).Rejection);
        connected = false;
        rejections.Add(menu.Submit(Intent(10, DesiredAction.EnsureOff, 9)).Rejection);
        connected = true;
        menu.Invalidate(Session, 1, disconnected: false).Applied.Should().BeTrue();
        rejections.Add(menu.Submit(Intent(12, DesiredAction.EnsureOff, 11)).Rejection);
        menu.Begin(Session, 2, 1).Applied.Should().BeTrue();
        (await menu.StopAsync().WaitAsync(Patience)).Joined.Should().BeTrue();
        rejections.Add(menu.Submit(Intent(2, DesiredAction.EnsureOff, 1, context: 2)).Rejection);

        rejections.Should().Equal(NativeMenuRejection.NoContext, NativeMenuRejection.Expired, NativeMenuRejection.OtherSession,
            NativeMenuRejection.UnknownContext, NativeMenuRejection.ProfileMismatch, NativeMenuRejection.Replayed,
            NativeMenuRejection.SequenceOutOfRange, NativeMenuRejection.Ambiguous, NativeMenuRejection.NoNeutralArm,
            NativeMenuRejection.StaleNeutralArm, NativeMenuRejection.NoHost, NativeMenuRejection.StaleContext, NativeMenuRejection.Stopped);
        host.Calls.Should().BeEmpty();
        providerReads.Should().Be(rejections.Count, "the provider is only read at admission, which is not an RPC");

        // Control on the same fake: an accepted intent does reach it, and it only reads screens.
        await using var control = new NativeMenuCaseActionCoordinator(() => host.Connection, _time);
        control.Begin(Session, 1, 1);
        (await control.Submit(Intent(2, DesiredAction.EnsureOff, 1)).Outcome!.WaitAsync(Patience)).State.Should().Be(NativeMenuCaseOutcomeState.Applied);
        host.Calls.Should().Contain(["SaveProfile#1", "SetScene#1", "GetScreenScene#1", "ListScreens#1"]);
        host.Calls.Should().NotContain(c => c.StartsWith("UNEXPECTED", StringComparison.Ordinal));
        host.Applied.Should().ContainSingle();
    }

    private NativeMenuCaseIntent Intent(long sequence, DesiredAction action, long arm, long context = 1, long profile = 1,
        string session = Session, DateTimeOffset? at = null) =>
        new(session, context, profile, sequence, at ?? _time.GetUtcNow(), action, arm);

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(10);
        }
    }

    private static EquivalencyAssertionOptions<T> Exact<T>(EquivalencyAssertionOptions<T> options) =>
        options.WithStrictOrdering().ComparingRecordsByMembers();

    private static SceneDto Running() => new([new([new("lianli:1", 0), new("lianli:1", 1)], "Rainbow", ["#FF0000", "#00FF00"], Speed: 0.25f)]);

    // Lights-off as another client may write it.
    private static SceneDto Black() => new([new([new("lianli:1", 0), new("lianli:1", 1)], "StaticColor", ["#000000"])]);

    /// <summary>Holds one intercepted call until released; optionally ignores its token like a backend that never observes it.</summary>
    private sealed class Hold(bool ignoresCancellation = false)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task.WaitAsync(Patience);
        public void Release() => _released.TrySetResult();

        public Task WaitAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            return _released.Task.WaitAsync(Patience, ignoresCancellation ? CancellationToken.None : ct);
        }
    }
}

/// <summary>
/// One in-memory Host connection: an IRgbService and an IScreenService proxy sharing one call trace ("GetScene#1"). Every value
/// crosses as JSON, so each reply is a fresh copy; SetScene with Screens=null keeps the scene's screens. Intercept runs before each
/// call and again after a SetScene was applied ("SetScene#1 applied"); it may block, throw or cancel. Every other member, including
/// every screen write, is traced as UNEXPECTED and fails.
/// </summary>
internal sealed class MenuHostFake
{
    private readonly object _sync = new();
    private readonly List<string> _calls = [];
    private readonly List<SceneDto> _applied = [];
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _active;

    public MenuHostFake(SceneDto scene)
    {
        Scene = Copy(scene);
        var rgb = DispatchProxy.Create<IRgbService, NativeMenuRgbRpc>();
        ((NativeMenuRgbRpc)(object)rgb).Owner = this;
        var screens = DispatchProxy.Create<IScreenService, NativeMenuScreenRpc>();
        ((NativeMenuScreenRpc)(object)screens).Owner = this;
        Connection = new(rgb, screens);
    }

    public NativeMenuCaseHost Connection { get; }
    public SceneDto Scene { get; private set; }
    public Dictionary<string, ProfileDto> Profiles { get; } = new(StringComparer.Ordinal);
    public Func<string, CancellationToken, Task>? Intercept { get; init; }
    public int MaxConcurrentCalls { get; private set; }

    public IReadOnlyList<ControllerDto> Controllers { get; } =
        [new("lianli:1", "Lian Li", "lianli:1", "Fan", "Fan", "SN-1", [new(0, "Zone", "Linear", 16, 16, 1), new(1, "Zone", "Linear", 16, 16, 1)],
            30, ControllerStates.Active, null)];

    // One configured case screen that keeps reporting Running: nothing here pretends a screen followed the lights.
    public ScreenSceneDto ScreenScene { get; } = new([new("screen:h2-1")]);
    public IReadOnlyList<ScreenDeviceDto> Screens { get; } = [new("screen:h2-1", "H2", "H2", 480, 480, "Running", null, true, 0, 0, null, 5)];

    public string[] Calls
    {
        get { lock (_sync) return [.. _calls]; }
    }

    public SceneDto[] Applied
    {
        get { lock (_sync) return [.. _applied]; }
    }

    internal object Rgb(string method, object?[] args) => method switch
    {
        nameof(IRgbService.GetSceneAsync) => CallAsync("GetScene", args, () => Copy(Scene)),
        nameof(IRgbService.ListControllersAsync) => CallAsync("ListControllers", args, () => Controllers),
        nameof(IRgbService.SaveProfileAsync) => CallAsync<object?>("SaveProfile", args, () =>
        {
            var profile = (ProfileDto)args[0]!;
            Profiles[profile.Name] = Copy(profile);
            return null;
        }),
        nameof(IRgbService.LoadProfileAsync) => CallAsync("LoadProfile", args, () =>
            Profiles.TryGetValue((string)args[0]!, out var profile) ? Copy(profile) : throw new FileNotFoundException("No recovery point.")),
        nameof(IRgbService.SetSceneAsync) => CallAsync<object?>("SetScene", args, () =>
        {
            var scene = (SceneDto)args[0]!;
            lock (_sync) _applied.Add(Copy(scene));
            Scene = Copy(scene with { Screens = scene.Screens ?? Scene.Screens });
            return null;
        }),
        _ => Unexpected("rgb." + method),
    };

    internal object Screen(string method, object?[] args) => method switch
    {
        nameof(IScreenService.GetScreenSceneAsync) => CallAsync("GetScreenScene", args, () => ScreenScene),
        nameof(IScreenService.ListScreensAsync) => CallAsync("ListScreens", args, () => Screens),
        _ => Unexpected("screens." + method),
    };

    private async Task<T> CallAsync<T>(string method, object?[] args, Func<T> body)
    {
        var ct = args is [.., CancellationToken token] ? token : default;
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

    private object Unexpected(string method)
    {
        lock (_sync) _calls.Add("UNEXPECTED " + method);
        throw new InvalidOperationException(method + " must not be called by the native-menu case bridge.");
    }

    private static SceneDto Copy(SceneDto value) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(value, ContractsJsonContext.Default.SceneDto), ContractsJsonContext.Default.SceneDto)!;

    private static ProfileDto Copy(ProfileDto value) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(value, ContractsJsonContext.Default.ProfileDto), ContractsJsonContext.Default.ProfileDto)!;
}

public class NativeMenuRgbRpc : DispatchProxy
{
    internal MenuHostFake? Owner { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Owner!.Rgb(targetMethod!.Name, args ?? []);
}

public class NativeMenuScreenRpc : DispatchProxy
{
    internal MenuHostFake? Owner { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Owner!.Screen(targetMethod!.Name, args ?? []);
}
