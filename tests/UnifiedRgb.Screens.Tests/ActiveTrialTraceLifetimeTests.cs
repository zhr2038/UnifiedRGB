using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using static UnifiedRgb.Screens.Tests.PreparedOverlayKit;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Active-trial detail lifetime: the per-submission SourceFrames rows are read only while the sampled trial is the screen's ACTIVE
/// one (native video first, then rate), so an ended, expired or replaced trial's rows disappear at once, before any further sample.
/// The one-argument Snapshot test seam, the cached TrialId, the selection summary and the timing window are unchanged; reading
/// clears nothing. Paused screens keep an unexpired rate trial active (StopScreensAsync clears only the native video trial).
/// </summary>
public sealed class ActiveTrialTraceLifetimeTests
{
    private static readonly ScreenDeviceDto Device = new("s", "test", "Turzx", 480, 1920, "Running", null, false, 1000, 0, null, 54);
    private static readonly StoredAsset Source = new("a", "a", "gif", 2, 2, [100, 100, 100, 100], 0);
    private static readonly ScreenFrameWork Work = new(1, 2, 3, 4, 5, 6, 7, 8, 100, 612, 1, 0, 0);

    private static void Add(ScreenPerformance perf, string? trial, int k) =>
        perf.Add(Work, 60, trial, null, 0, new ScreenSourceSample(Source, k % 4, k * 100L, 5_000_000 + (k * 100_000L), 1_000_000, false, false));

    [Fact]
    public void The_production_read_shows_rows_only_for_the_active_trial_and_the_seam_and_counters_are_unchanged()
    {
        var perf = new ScreenPerformance();
        for (var k = 0; k < 3; k++) Add(perf, "t1", k);
        var seam = perf.Snapshot(Device);
        var active = perf.Snapshot(Device, "t1");
        active.SourceFrames.Should().HaveCount(3);
        active.SourceFrames.Should().BeEquivalentTo(seam.SourceFrames, o => o.WithStrictOrdering());
        foreach (var other in new[] { perf.Snapshot(Device, "t2"), perf.Snapshot(Device, null) })
        {
            other.SourceFrames.Should().BeNull("t1 is no longer the active trial");
            (other.TrialId, other.Samples, other.SourceSelection).Should().Be((active.TrialId, active.Samples, active.SourceSelection));
            other.Stages.Should().BeEquivalentTo(active.Stages);
        }

        perf.Snapshot(Device, "t1").SourceFrames.Should().HaveCount(3, "a gated read cleared nothing");
        perf.Snapshot(Device).SourceFrames.Should().HaveCount(3, "the one-argument seam shows the last sampled trial's rows regardless");
        Add(perf, "t1", 3);
        perf.Snapshot(Device, "t1").SourceFrames!.Select(r => r.SourceIndex).Should().Equal(0, 1, 2, 3);

        Add(perf, "t2", 4); // a new trial restarts the window
        perf.Snapshot(Device, "t2").SourceFrames!.Should().ContainSingle().Which.SourceIndex.Should().Be(0);
        perf.Snapshot(Device, "t1").SourceFrames.Should().BeNull();
        Add(perf, null, 5); // outside any trial: never rows, on either read
        (perf.Snapshot(Device, null).SourceFrames, perf.Snapshot(Device).SourceFrames).Should().Be((null, null));
        perf.Snapshot(Device, null).SourceSelection!.Submissions.Should().Be(6, "the cumulative summary counts every submission");
    }

    /// <summary>The worker's precedence: the native video trial, then the rate trial; ending one hides its rows before another sample.</summary>
    [MediaFoundationFact]
    public async Task The_worker_read_follows_video_then_rate_precedence_and_hides_rows_before_the_next_sample()
    {
        using var h = await NativeWorkerHarness.StartAsync(NativeWorkerHarness.NewTrial("video"));
        h.RateTrial = new ScreenRateTrial(new("rate", [Devices.Turzx.Id], 10, 60000, false, 50), 0, long.MaxValue);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await PictureAsync(h);
        await Task.Delay(250);
        var video = h.Worker.Performance;
        video.TrialId.Should().Be("video", "the native video trial takes precedence");
        video.SourceFrames.Should().HaveCount(2);

        var gate = new ManualResetEventSlim(false);
        try
        {
            h.Reply = (command, stamp, _) =>
            {
                if (command == ScreenProtocol.PushPngCommand) gate.Wait(TimeSpan.FromSeconds(30)); // hold the normal picture's reply
                return null;
            };
            h.Trial = null; // the video trial ends: the rate trial is now the active one, nothing of it sampled yet
            await Wait.UntilAsync(() => h.Count(114) >= 1 && h.Writes.Length > 0 && NativeWorkerHarness.Command(h.Writes[^1]) == ScreenProtocol.PushPngCommand,
                "the session stopped and the normal picture is held mid-transfer");
            var between = h.Worker.Performance;
            between.SourceFrames.Should().BeNull("the video trial's rows disappear at once, before any rate-trial sample");
            (between.TrialId, between.SourceSelection).Should().Be(("video", video.SourceSelection), "the cached id and summary are unchanged");
            between.NativeVideo.Should().BeNull();
        }
        finally { gate.Set(); }

        await Wait.UntilAsync(() => h.Worker.Performance.TrialId == "rate", "the rate trial's first sample");
        var rate = h.Worker.Performance;
        rate.SourceFrames.Should().ContainSingle("a new trial restarts the rows").Which.Selection.Should().Be("start");
        h.RateTrial = null;
        h.Worker.Performance.SourceFrames.Should().BeNull("the rate trial ended; no further sample is needed");
        h.Worker.Performance.TrialId.Should().Be("rate");
        gate.Dispose();
    }

    /// <summary>A new native trial (another id) whose first picture has not completed: the old trial's rows and lab facts are hidden.</summary>
    [MediaFoundationFact]
    public async Task A_new_trial_before_its_first_sample_shows_neither_the_old_rows_nor_the_old_lab_facts()
    {
        using var h = await NativeWorkerHarness.StartAsync(Prepared("old", measure: true), reply: (command, stamp, _) =>
            command == 122 ? NativeWorkerHarness.Ack(command, stamp, 2) : null);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        await Task.Delay(250);
        var old = h.Worker.Performance;
        old.TrialId.Should().Be("old");
        old.SourceFrames.Should().HaveCount(2);
        old.NativeVideo!.PreparedOverlay!.TrialId.Should().Be("old");
        old.NativeVideo.LastOverlayQueue!.Prepared.Should().BeTrue();

        var gate = new ManualResetEventSlim(false);
        try
        {
            var data = h.DataWrites;
            h.Reply = (command, stamp, _) =>
            {
                if (command == TurzxVideoProtocol.DataCommand) gate.Wait(TimeSpan.FromSeconds(30)); // hold the new trial's first picture
                return null;
            };
            h.Trial = NativeWorkerHarness.NewTrial("new");
            await Wait.UntilAsync(() => h.DataWrites > data, "the new trial's first access unit is in flight");
            var between = h.Worker.Performance;
            between.SourceFrames.Should().BeNull("'old' is no longer the active trial");
            between.TrialId.Should().Be("old", "the cached last-sampled id");
            between.NativeVideo.Should().NotBeNull("the session continues");
            (between.NativeVideo!.PreparedOverlay, between.NativeVideo.LastOverlayQueue).Should().Be((null, null), "the old trial's lab facts are hidden");
            between.SourceSelection.Should().Be(old.SourceSelection);
        }
        finally { gate.Set(); }

        await Wait.UntilAsync(() => h.Worker.Performance.TrialId == "new", "the new trial's first sample");
        var fresh = h.Worker.Performance;
        fresh.SourceFrames.Should().ContainSingle().Which.Generation.Should().BeGreaterThan(old.SourceFrames![^1].Generation);
        (fresh.NativeVideo!.PreparedOverlay, fresh.NativeVideo.LastOverlayQueue).Should().Be((null, null), "a default trial has no lab facts");
        gate.Dispose();
    }

    /// <summary>
    /// Through the manager (TL rate trial, frozen clock): pausing keeps the unexpired rate trial ACTIVE, so its rows stay readable;
    /// expiry while paused hides them without any new sample; End hides them. The cached TrialId and the summary are unchanged.
    /// </summary>
    [Fact]
    public async Task Manager_reads_keep_rows_while_paused_and_hide_them_at_expiry()
    {
        await using var s = await SourceStepScenario.StartAsync(timeline: false);
        var trial = await s.Rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], 10, 2000));
        await Wait.UntilAsync(async () => (await PerfAsync(s)).TrialId == trial.TrialId, "the trial's first picture");
        await s.StepAsync(SourceStepScenario.PeriodMs);
        var running = await PerfAsync(s);
        running.SourceFrames.Should().HaveCount(2);

        await s.Rig.Manager.StopScreensAsync();
        await SourceStepScenario.SettleAsync();
        var paused = await PerfAsync(s);
        paused.SourceFrames.Should().NotBeNull("pausing does not end or expire the rate trial");
        paused.TrialId.Should().Be(trial.TrialId);

        s.Time.Advance(TimeSpan.FromMilliseconds(2000)); // past the deadline while paused
        await SourceStepScenario.SettleAsync();
        var expired = await PerfAsync(s);
        expired.SourceFrames.Should().BeNull("an expired trial is no longer the active one");
        (expired.TrialId, expired.SourceSelection).Should().Be((trial.TrialId, paused.SourceSelection), "nothing was sampled or cleared");
        (await PerfAsync(s)).SourceFrames.Should().BeNull();
    }

    [Fact]
    public async Task Manager_reads_hide_rows_once_the_rate_trial_is_ended()
    {
        await using var s = await SourceStepScenario.StartAsync(timeline: false);
        var trial = await s.Rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], 10, 60000));
        await Wait.UntilAsync(async () => (await PerfAsync(s)).TrialId == trial.TrialId, "the trial's first picture");
        await s.StepAsync(SourceStepScenario.PeriodMs);
        (await PerfAsync(s)).SourceFrames.Should().HaveCount(2);
        await s.Rig.Manager.EndScreenRateTrialAsync(trial.TrialId);
        (await PerfAsync(s)).SourceFrames.Should().BeNull("hidden from the first read after End");
        await SourceStepScenario.SettleAsync();
        (await PerfAsync(s)).SourceFrames.Should().BeNull();
    }

    private static async Task<ScreenPerformanceDto> PerfAsync(SourceStepScenario s) =>
        (await s.Rig.Manager.GetScreenPerformanceAsync()).Single();
}
