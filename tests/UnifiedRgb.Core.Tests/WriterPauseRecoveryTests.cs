using System.Reflection;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>Pause/resume against fake uploads only; barriers select the consume-before-pause race without sleeps.</summary>
public sealed class WriterPauseRecoveryTests
{
    private static readonly ControllerDescriptor Chain = new(new("test:paused-loop"), "Test", "chain", DeviceType.Ledstrip,
        DeviceClass.Virtual, "", [new(new(0), "one", ZoneLayout.Linear(1)), new(new(1), "two", ZoneLayout.Linear(1))],
        Capabilities.UploadedLoopOnly(200, 20, 12288));

    private static LoopFrames Frame(uint index, byte red) => new(1, 1, 20, index, new byte[] { red, 0, 0 });

    [Fact]
    public void Paused_drains_keep_only_the_latest_loop_for_resume()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Chain);
        var adapter = new RecordingLoopAdapter();
        using var writer = new DeviceWriter(box, adapter);
        bus.PublishLoop(Chain.Id, new(0), Frame(10, 255));
        writer.DrainLoopOnce().Should().BeTrue(); // A is already playing

        writer.Paused = true;
        bus.PublishLoop(Chain.Id, new(0), Frame(20, 0)); // black B
        writer.DrainLoopOnce().Should().BeFalse("a pause must not consume the pending upload");
        bus.PublishLoop(Chain.Id, new(0), Frame(3, 17)); // newer C, even when its generation number is smaller
        writer.DrainLoopOnce().Should().BeFalse();
        adapter.Indices().Should().Equal(10u);

        writer.Paused = false;
        writer.DrainLoopOnce().Should().BeTrue();
        writer.DrainLoopOnce().Should().BeFalse();
        adapter.Indices().Should().Equal(10u, 3u);
        adapter.Uploads[^1].Loop.Rgb.ToArray().Should().Equal(17, 0, 0);
        writer.GetStats().Errors.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_racing_consumption_retains_the_loop_and_a_newer_loop_still_wins(bool supersede)
    {
        using var bus = new FrameBus();
        var box = bus.Register(Chain);
        var adapter = new RecordingLoopAdapter();
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new DeviceWriter(box, adapter, onLoopUploaded: (_, _, _) => uploaded.TrySetResult());
        bus.PublishLoop(Chain.Id, new(0), Frame(20, 0));

        // Hold the existing I/O boundary while a drain takes B from the lock-free mailbox.
        // Reflection is only a scheduling barrier: assertions concern uploads and data, not private storage.
        var io = typeof(DeviceWriter).GetField("_io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
        Task<bool> drain;
        Monitor.Enter(io);
        try
        {
            // This deliberately blocks on a Monitor held by the test. Give it a dedicated thread so a
            // small CI thread pool cannot starve the barrier while the test is itself waiting synchronously.
            drain = Task.Factory.StartNew(writer.DrainLoopOnce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            SpinWait.SpinUntil(() => box.LoopsConsumed == 1, TimeSpan.FromSeconds(5)).Should().BeTrue("B reached the I/O boundary");
            writer.Paused = true;
            if (supersede) bus.PublishLoop(Chain.Id, new(0), Frame(3, 17));
        }
        finally
        {
            Monitor.Exit(io);
        }

        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        writer.DrainLoopOnce().Should().BeFalse();
        adapter.Indices().Should().BeEmpty();

        // No further publish: a retained, consumed loop must also wake the running writer after resume.
        writer.Start();
        try
        {
            writer.Paused = false;
            await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            writer.Stop();
        }

        adapter.Indices().Should().Equal(supersede ? 3u : 20u);
        adapter.Uploads.Single().Loop.Rgb.ToArray().Should().Equal(supersede ? (byte)17 : (byte)0, 0, 0);
        writer.GetStats().Errors.Should().Be(0);
    }

    [Fact]
    public void Pause_keeps_the_latest_loop_of_every_zone()
    {
        using var bus = new FrameBus();
        var adapter = new RecordingLoopAdapter();
        using var writer = new DeviceWriter(bus.Register(Chain), adapter) { Paused = true };
        bus.PublishLoop(Chain.Id, new(0), Frame(10, 0));
        bus.PublishLoop(Chain.Id, new(1), Frame(11, 9));
        writer.DrainLoopOnce().Should().BeFalse();
        bus.PublishLoop(Chain.Id, new(0), Frame(12, 7));
        writer.Paused = false;

        writer.DrainLoopOnce().Should().BeTrue();
        writer.DrainLoopOnce().Should().BeTrue();
        writer.DrainLoopOnce().Should().BeFalse();
        adapter.Uploads.Select(u => (u.Zone, u.Loop.EffectIndex)).Should().Equal((new ZoneId(0), 12u), (new ZoneId(1), 11u));
    }

    [Fact]
    public async Task Running_paused_writer_resumes_latest_content_without_a_new_publish()
    {
        using var bus = new FrameBus();
        var adapter = new RecordingLoopAdapter();
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new DeviceWriter(bus.Register(Chain), adapter, onLoopUploaded: (_, _, _) => uploaded.TrySetResult()) { Paused = true };
        writer.Start();
        try
        {
            bus.PublishLoop(Chain.Id, new(0), Frame(20, 0));
            bus.PublishLoop(Chain.Id, new(0), Frame(3, 17));
            writer.Paused = false;
            await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            writer.Stop();
        }

        adapter.Indices().Should().Equal(3u);
        adapter.RecoveryAllowed.Should().BeFalse("Stop must still revoke autonomous recovery");
    }
}
