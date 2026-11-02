using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexLaunchTests
{
    internal sealed class FakeActivator : ICodexWindowActivator
    {
        public int Calls;
        public Func<Task<CodexActivationResult>>? Next;
        public Task<CodexActivationResult> RequestAsync()
        { Interlocked.Increment(ref Calls); return Next?.Invoke() ?? Task.FromResult(new CodexActivationResult("Requested")); }
    }

    private static CodexLaunchIntent Intent(TimeProvider time) => new("fake", 2, 123, 0, 2, 1, time.GetUtcNow(), 10, 0x70);

    [Theory]
    [InlineData("page")] [InlineData("capability")] [InlineData("cookie")] [InlineData("neutral")]
    [InlineData("sequence")] [InlineData("stale")] [InlineData("future")] [InlineData("admission")]
    public void Invalid_intents_never_schedule_activation(string fault)
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator(); var scheduled = 0;
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: _ => { scheduled++; return Task.CompletedTask; });
        var intent = Intent(time);
        intent = fault switch
        {
            "page" => intent with { Page = 9 }, "capability" => intent with { Flags = 0x30 },
            "cookie" => intent with { Cookie = 0 }, "neutral" => intent with { NeutralSequence = 0 },
            "sequence" => intent with { Sequence = 3 }, "stale" => intent with { ReceivedAt = time.GetUtcNow().AddSeconds(-1) },
            "future" => intent with { ReceivedAt = time.GetUtcNow().AddSeconds(1) }, _ => intent,
        };
        coordinator.Submit(intent, () => fault != "admission").Should().BeFalse();
        scheduled.Should().Be(0); fake.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("exit")] [InlineData("maintenance")] [InlineData("stop")] [InlineData("stale")]
    [InlineData("later-intent")] [InlineData("disable")]
    public async Task Queued_work_rechecks_current_admission_and_invalidation_at_start(string reason)
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator(); Func<Task>? queued = null;
        using var gate = new PeripheralConfigurationGate(new(false, null));
        using var coordinator = new CodexLaunchCoordinator(fake, time, gate, work => { queued = work; return Task.CompletedTask; });
        var current = true;
        coordinator.Submit(Intent(time), () => current).Should().BeTrue();
        switch (reason)
        {
            case "maintenance": gate.BlockRogAcquisition(); gate.AllowRogAcquisition(); break;
            case "stop": await coordinator.StopAsync(default); break;
            case "stale": time.Advance(TimeSpan.FromSeconds(1)); break;
            case "later-intent": case "disable": current = false; break;
            default: coordinator.Invalidate(); break;
        }
        await queued!(); fake.Calls.Should().Be(0); coordinator.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task Uninterruptible_activation_keeps_single_slot_unknown_is_not_retried_and_replays_are_rejected()
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator();
        var answer = new TaskCompletionSource<CodexActivationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Next = () => answer.Task;
        Func<Task>? queued = null;
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work => { queued = work; return Task.CompletedTask; });
        var first = Intent(time);
        coordinator.Submit(first, () => true).Should().BeTrue();
        var running = queued!(); fake.Calls.Should().Be(1);
        coordinator.Invalidate(); time.Advance(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 100; i++) coordinator.Submit(first with { ReceivedAt = time.GetUtcNow(), Sequence = 4, NeutralSequence = 3 }, () => true).Should().BeFalse();
        answer.SetResult(new("Unknown")); await running;
        coordinator.Status.Should().Be("Unknown"); fake.Calls.Should().Be(1);
        coordinator.Submit(first with { ReceivedAt = time.GetUtcNow() }, () => true).Should().BeFalse("no replay of an unknown outcome");
        fake.Next = null;
        coordinator.Submit(first with { ReceivedAt = time.GetUtcNow(), Sequence = 4, NeutralSequence = 3 }, () => true).Should().BeTrue();
        await queued!(); fake.Calls.Should().Be(2);
        coordinator.Submit(first with { Sequence = 6, NeutralSequence = 5, ReceivedAt = time.GetUtcNow() }, () => true).Should().BeFalse("bounded launch rate");
        await coordinator.StopAsync(default); time.Advance(TimeSpan.FromSeconds(3));
        coordinator.Submit(first with { Sequence = 8, NeutralSequence = 7, ReceivedAt = time.GetUtcNow() }, () => true).Should().BeFalse();
    }

    [Fact]
    public async Task Stop_joins_running_call_and_cancelled_stop_reports_unknown_busy_until_actual_completion()
    {
        var time = new ManualTimeProvider(); var answer = new TaskCompletionSource<CodexActivationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeActivator { Next = () => answer.Task }; Func<Task>? queued = null;
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work => { queued = work; return Task.CompletedTask; });
        coordinator.Submit(Intent(time), () => true).Should().BeTrue();
        var running = queued!(); var owned = coordinator.Completion;
        var stopping = coordinator.StopAsync(default);
        stopping.IsCompleted.Should().BeFalse(); owned.IsCompleted.Should().BeFalse();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await ((Func<Task>)(() => coordinator.StopAsync(cancel.Token))).Should().ThrowAsync<OperationCanceledException>();
        coordinator.Status.Should().Be("Unknown"); coordinator.Busy.Should().BeTrue();
        coordinator.Submit(Intent(time), () => true).Should().BeFalse();
        answer.SetResult(new("Requested")); await running; await stopping; await owned;
        coordinator.Busy.Should().BeFalse(); coordinator.Status.Should().Be("Requested"); fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_joins_the_owned_worker_without_holding_the_status_lock()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<CodexActivationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeActivator { Next = () => { entered.SetResult(); return answer.Task; } };
        var coordinator = new CodexLaunchCoordinator(fake, time);
        coordinator.Submit(Intent(time), () => true).Should().BeTrue(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = Task.Run(coordinator.Dispose);
        try
        {
            disposing.IsCompleted.Should().BeFalse(); coordinator.Busy.Should().BeTrue(); coordinator.Status.Should().Be("Unknown");
        }
        finally { answer.TrySetResult(new("Requested")); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(5)); await coordinator.SchedulerCompletion;
        coordinator.Busy.Should().BeFalse(); fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Queued_stop_joins_scheduler_and_late_stale_delegate_cannot_invoke()
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator(); Func<Task>? queued = null;
        var scheduler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work => { queued = work; return scheduler.Task; });
        try
        {
            coordinator.Submit(Intent(time), () => true).Should().BeTrue(); var owned = coordinator.Completion;
            var stopping = coordinator.StopAsync(default);
            owned.IsCompleted.Should().BeTrue(); coordinator.Busy.Should().BeFalse(); stopping.IsCompleted.Should().BeFalse();
            await queued!(); fake.Calls.Should().Be(0); coordinator.Status.Should().Be("Cancelled");
            scheduler.SetException(new IOException("late scheduler fault"));
            await stopping.WaitAsync(TimeSpan.FromSeconds(2)); await coordinator.SchedulerCompletion;
            coordinator.Status.Should().Be("Cancelled");
        }
        finally
        {
            scheduler.TrySetResult();
            if (queued is not null) await queued();
            await coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scheduler_throw_or_fault_is_observed_releases_slot_and_does_not_activate(bool asynchronous)
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator(); Func<Task>? stale = null;
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work =>
        { stale = work; return asynchronous ? Task.FromException(new IOException("scheduler")) : throw new IOException("scheduler"); });
        coordinator.Submit(Intent(time), () => true).Should().BeTrue();
        await coordinator.SchedulerCompletion; await coordinator.Completion;
        coordinator.Status.Should().Be("Failed"); coordinator.Busy.Should().BeFalse(); fake.Calls.Should().Be(0);
        await stale!(); fake.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Invalidated_queued_work_completes_but_does_not_spawn_replacement_schedulers_until_original_is_joined()
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator(); Func<Task>? queued = null;
        var scheduler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var schedules = 0;
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work => { schedules++; queued = work; return scheduler.Task; });
        coordinator.Submit(Intent(time), () => true).Should().BeTrue(); var owned = coordinator.Completion;
        coordinator.Invalidate(); await owned; coordinator.Busy.Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(3));
        coordinator.Submit(Intent(time) with { Sequence = 4, NeutralSequence = 3 }, () => true).Should().BeFalse();
        schedules.Should().Be(1); await queued!(); fake.Calls.Should().Be(0);
        scheduler.SetResult(); await coordinator.SchedulerCompletion;
        coordinator.Submit(Intent(time) with { Sequence = 4, NeutralSequence = 3 }, () => true).Should().BeTrue();
        coordinator.Invalidate(); await queued!(); await coordinator.SchedulerCompletion;
    }

    [Fact]
    public async Task Scheduler_fault_during_committed_activation_cannot_release_the_running_slot()
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator(); Func<Task>? queued = null;
        var scheduler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<CodexActivationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Next = () => answer.Task;
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work => { queued = work; return scheduler.Task; });
        coordinator.Submit(Intent(time), () => true).Should().BeTrue(); var running = queued!();
        scheduler.SetException(new IOException("scheduler fault after invoke")); await coordinator.SchedulerCompletion;
        coordinator.Busy.Should().BeTrue(); coordinator.Status.Should().Be("Unknown");
        var duplicate = queued!(); await duplicate; coordinator.Busy.Should().BeTrue(); fake.Calls.Should().Be(1);
        answer.SetResult(new("Unknown")); await running; coordinator.Busy.Should().BeFalse();
    }

    [Fact]
    public async Task Admission_callback_runs_outside_coordinator_lock_and_revision_invalidates_its_snapshot()
    {
        var time = new ManualTimeProvider(); var fake = new FakeActivator();
        using var coordinator = new CodexLaunchCoordinator(fake, time);
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Submit(Intent(time), () =>
        {
            Task.Run(() => { coordinator.Invalidate(); invalidated.SetResult(); }).GetAwaiter().GetResult();
            return true;
        }).Should().BeFalse();
        await invalidated.Task; fake.Calls.Should().Be(0);
    }
}
