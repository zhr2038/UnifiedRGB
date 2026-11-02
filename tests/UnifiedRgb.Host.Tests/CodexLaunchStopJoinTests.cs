using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexLaunchStopJoinTests
{
    private static CodexLaunchIntent Intent(TimeProvider time) => new("stop-join-fake", 2, 123, 0, 2, 1,
        time.GetUtcNow(), 10, 0x70);

    [Fact]
    public async Task Stop_joins_cancelled_queued_scheduler_before_claiming_completion()
    {
        var time = new ManualTimeProvider();
        var fake = new CodexLaunchTests.FakeActivator();
        Func<Task>? queued = null;
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work =>
        { queued = work; return scheduled.Task; });
        try
        {
            coordinator.Submit(Intent(time), () => true).Should().BeTrue();
            var stop = coordinator.StopAsync(CancellationToken.None);
            coordinator.Busy.Should().BeFalse(); // queued activation was cancelled, but the scheduler is still owned
            stop.IsCompleted.Should().BeFalse();
            coordinator.SchedulerCompletion.IsCompleted.Should().BeFalse();
            await queued!();
            fake.Calls.Should().Be(0);
            stop.IsCompleted.Should().BeFalse();
            scheduled.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.SchedulerCompletion.IsCompleted.Should().BeTrue();
            coordinator.Status.Should().Be("Cancelled");
        }
        finally
        {
            scheduled.TrySetResult();
            if (queued is not null) await queued();
            await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Cancelled_stop_wait_does_not_release_pending_scheduler_or_allow_a_new_submit()
    {
        var time = new ManualTimeProvider();
        var fake = new CodexLaunchTests.FakeActivator();
        Func<Task>? queued = null;
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CodexLaunchCoordinator(fake, time, schedule: work =>
        { queued = work; return scheduled.Task; });
        try
        {
            coordinator.Submit(Intent(time), () => true).Should().BeTrue();
            using var cancelled = new CancellationTokenSource();
            var stop = coordinator.StopAsync(cancelled.Token);
            stop.IsCompleted.Should().BeFalse();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            coordinator.SchedulerCompletion.IsCompleted.Should().BeFalse();
            time.Advance(TimeSpan.FromSeconds(3));
            coordinator.Submit(Intent(time) with { Sequence = 4, NeutralSequence = 3 }, () => true).Should().BeFalse();
            await queued!();
            fake.Calls.Should().Be(0);
            scheduled.SetResult();
            await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.SchedulerCompletion.IsCompleted.Should().BeTrue();
        }
        finally
        {
            scheduled.TrySetResult();
            if (queued is not null) await queued();
            await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
