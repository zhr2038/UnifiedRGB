using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

public sealed class PerformanceTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void Absolute_schedule_does_not_accumulate_late_wakeups(int fps)
    {
        var p = new ScreenPacing();
        const long freq = 1_000_000;
        long now = 0;
        p.Remaining(now, freq, 1000.0 / fps).Should().Be(0);
        for (var i = 0; i < fps * 5; i++)
        {
            p.Completed(now + 1000, freq);
            now = (long)Math.Round((i + 1) * (double)freq / fps) + 8000; // recurring 8ms OS oversleep
            p.Remaining(now, freq, 1000.0 / fps).Should().Be(0);
        }
        now.Should().BeLessThanOrEqualTo(5 * freq + 8001);
        p.Completed(now, freq);
        p.Remaining(now, freq, 1000.0 / fps).Should().BeInRange(1, (int)Math.Ceiling(1000.0 / fps));
    }

    [Fact]
    public void Slow_transport_uses_latest_due_slot_without_halving_throughput_or_catching_up_old_slots()
    {
        var p = new ScreenPacing();
        p.Remaining(0, 1000, 50).Should().Be(0);
        p.Completed(120, 1000).Should().Be(1);
        p.Remaining(120, 1000, 50).Should().Be(0);
        p.Completed(121, 1000).Should().Be(0);
        p.Remaining(121, 1000, 50).Should().Be(29); // exactly one latest slot, then the original 150ms deadline
        p.Reset();
        p.Remaining(9000, 1000, 50).Should().Be(0);
    }

    [Fact]
    public void Diagnostic_storage_is_bounded_and_excludes_bootstrap_and_old_samples()
    {
        var stats = new ScreenPerformance();
        var device = new ScreenDeviceDto("s", "test", "TlLcd", 400, 400, "Running", null, false, 1000, 0, null, 15);
        for (var i = 0; i < 1000; i++) stats.Add(new(i, 2, 3, 4, 5, 6, 66, 20, 10, 100, 1, 1, 0), 15, null, 3, 1);
        var s = stats.Snapshot(device);
        s.Samples.Should().Be(256);
        s.Stages["compose"].MaxMs.Should().Be(999);
        s.Stages["compose"].P50Ms.Should().Be(871);
        s.PacketBytes.Should().Be(25600);
        s.SkippedScheduleSlots.Should().Be(1000);
    }

    [Fact]
    public async Task Trial_expires_without_persisting_and_keeps_the_current_scene_content()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var scene = new ScreenSceneDto([new(Devices.Tl1.Id, Overlay: "compact", Fps: 15, Rotation: 180, Brightness: .4)]);
        await rig.Manager.SetScreenSceneAsync(scene);
        await UntilAsync(async () => (await rig.ScreenAsync(Devices.Tl1.Id)).FramesWritten > 0);
        var path = Path.Combine(rig.DataDirectory, "screens", "scene.json");
        var before = File.ReadAllBytes(path);
        var trial = await rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], 30, 1000, TlReadTimeoutMs: 2));
        await UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().TrialId == trial.TrialId);
        await UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().TrialId is null);
        (await rig.Manager.GetScreenPerformanceAsync()).Single().TargetFps.Should().Be(15);
        File.ReadAllBytes(path).Should().Equal(before);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        await rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], 20, 1000));
        await rig.Manager.SetScreenSceneAsync(scene);
        await UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().TrialId is null);
    }

    [Theory]
    [InlineData(0, 1000, 50)]
    [InlineData(61, 1000, 50)]
    [InlineData(30, 120001, 50)]
    [InlineData(30, 999, 50)]
    [InlineData(30, 1000, 0)]
    [InlineData(30, 1000, 51)]
    public async Task Invalid_lab_rates_are_refused_before_any_device_opens(int fps, int duration, int readTimeout)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var act = () => rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], fps, duration, TlReadTimeoutMs: readTimeout));
        await act.Should().ThrowAsync<ArgumentException>();
        rig.Opener.Opens.Should().Be(0);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++) { if (await condition()) return; await Task.Delay(20); }
        (await condition()).Should().BeTrue("the fake worker should converge within four seconds");
    }

    [Fact]
    public async Task High_resolution_deadline_wait_is_interruptible_and_consumes_only_its_own_signal()
    {
        using var box = new LatestWins<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = Task.Run(() => { ready.SetResult(); return box.Wait(1000); });
        await ready.Task;
        box.Post("new content");
        (await wait.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        box.TryTake().Should().Be("new content");
        box.Wait(1).Should().BeFalse("the native wait must also consume the managed semaphore count");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public void Shorter_quiet_window_still_drains_ack_and_ZLP_until_a_real_timeout(int quietMs)
    {
        var timeouts = new List<int>();
        var results = new Queue<ScreenPipeRead>([new(512, false), new(0, false), new(0, true)]);
        ScreenPipeRead Read(Span<byte> _, int timeout) { timeouts.Add(timeout); return results.Dequeue(); }
        ScreenReplyFlow.ReadAndDrain(Read, new byte[512], 2000, quietMs).Should().Be(512);
        timeouts.Should().Equal(2000, quietMs, quietMs);
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Invalid_quiet_window_is_refused_before_reading(int quietMs)
    {
        var reads = 0;
        ScreenPipeRead Read(Span<byte> _, int timeout) { reads++; return new(0, true); }
        var act = () => ScreenReplyFlow.ReadAndDrain(Read, new byte[512], 2000, quietMs);
        act.Should().Throw<ArgumentOutOfRangeException>();
        reads.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Invalid_quiet_trial_is_refused_without_opening_any_device(int quietMs)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var act = () => rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], 30, 1000, ReplyQuietMs: quietMs));
        await act.Should().ThrowAsync<ArgumentException>();
        rig.Opener.Opens.Should().Be(0);
    }
}
