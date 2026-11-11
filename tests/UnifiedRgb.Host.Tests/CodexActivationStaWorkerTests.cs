using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexActivationStaWorkerTests
{
    [Fact]
    public async Task Mta_caller_executes_callback_on_sta_and_returned_task_joins_thread_exit()
    {
        Thread? worker = null;
        var apartment = ApartmentState.Unknown;
        var foreground = false;
        var result = await Task.Run(async () =>
        {
            Thread.CurrentThread.GetApartmentState().Should().Be(ApartmentState.MTA);
            return await CodexStaActivationWorker.RunAsync(() =>
            {
                worker = Thread.CurrentThread; apartment = worker.GetApartmentState();
                foreground = !worker.IsBackground;
                return new("Requested", 0);
            });
        });
        result.Status.Should().Be("Requested"); apartment.Should().Be(ApartmentState.STA);
        worker.Should().NotBeNull(); worker!.IsAlive.Should().BeFalse(); foreground.Should().BeTrue();
    }

    [Fact]
    public async Task Already_sta_request_runs_inline_without_spawning_a_second_worker()
    {
        var inline = false;
        var result = await CodexStaActivationWorker.RunAsync(() =>
        {
            var original = Environment.CurrentManagedThreadId;
            var nested = CodexStaActivationWorker.RunAsync(() =>
            { inline = Environment.CurrentManagedThreadId == original; return new("Requested", 0); });
            if (!nested.IsCompleted) throw new InvalidOperationException("STA callback was not inline");
            return nested.GetAwaiter().GetResult();
        });
        result.Status.Should().Be("Requested"); inline.Should().BeTrue();
    }

    [Fact]
    public async Task Owned_sta_call_and_coordinator_stop_remain_pending_until_callback_and_thread_exit()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Thread? worker = null;
        var calls = 0;
        var fake = new CodexLaunchTests.FakeActivator { Next = () => CodexStaActivationWorker.RunAsync(() =>
        {
            worker = Thread.CurrentThread; Interlocked.Increment(ref calls); entered.SetResult();
            release.Wait(); return new("Requested", 0);
        }) };
        using var coordinator = new CodexLaunchCoordinator(fake, time);
        try
        {
            coordinator.Submit(new("sta-lifetime-fake", 2, 123, 0, 2, 1, time.GetUtcNow(), 10, 0x70), () => true).Should().BeTrue();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopping = coordinator.StopAsync(CancellationToken.None);
            stopping.IsCompleted.Should().BeFalse(); coordinator.Busy.Should().BeTrue();
            coordinator.Status.Should().Be("Unknown"); worker!.IsAlive.Should().BeTrue();
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            worker.IsAlive.Should().BeFalse(); coordinator.Busy.Should().BeFalse(); calls.Should().Be(1);
        }
        finally
        {
            release.Set();
            await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
