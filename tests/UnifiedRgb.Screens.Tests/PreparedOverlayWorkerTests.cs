using System.Diagnostics;
using System.Globalization;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using Xunit.Abstractions;
using static UnifiedRgb.Screens.Tests.PreparedOverlayKit;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The wired PreparedOverlay lab path on one frozen-clock TURZX worker: exact inline bytes, requests only on a byte-key change,
/// structural generation and mode flips, disable/clear, failure and no-progress fallback (sticky per trial), one preparer thread over
/// sessions, stop/expiry/blackout/exit ordering without joins on the sender path, schema4 clip boundaries, raster ownership and
/// steady-state worker allocation. Fake channels and test hosts only.
/// </summary>
public sealed class PreparedOverlayWorkerTests(ITestOutputHelper output)
{
    private const int LayerBytes = 1920 * 480 * 4;

    [MediaFoundationFact]
    public async Task Default_trials_create_no_preparer_send_no_query_and_report_no_lab_facts()
    {
        using var h = await NativeWorkerHarness.StartAsync(NativeWorkerHarness.NewTrial("default"));
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        h.SetTelemetry(20);
        await PictureAsync(h);
        await h.PostAsync(h.Assignment with { Overlay = "none" });
        await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
        h.Worker.OverlayPreparerForTest.Should().BeNull("a default trial never creates the preparer object or its thread");
        h.Count(122).Should().Be(0, "no queue query without MeasureQueueAfterOverlay");
        WorkerPngs(h.Writes).Should().HaveCount(4, "inline: the first overlay, the telemetry change, the clear and the re-enabled overlay");
        var facts = Facts(h);
        (facts.PreparedOverlay, facts.LastOverlayQueue).Should().Be((null, null));
        facts.OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
    }

    /// <summary>
    /// The same frozen-clock scenario inline and prepared: identical overlay PNGs (in order), identical data access units and selected
    /// source indices; a static layer makes no request; the worker never encodes an overlay itself in prepared mode.
    /// </summary>
    [MediaFoundationFact]
    public async Task Prepared_overlays_are_the_inline_bytes_and_a_static_layer_makes_no_request()
    {
        var inline = await ScenarioAsync(prepared: false);
        var prepared = await ScenarioAsync(prepared: true);
        prepared.Pngs.Select(Sha).Should().Equal(inline.Pngs.Select(Sha));
        prepared.Pngs.Should().HaveCount(3);
        prepared.Data.Should().Equal(inline.Data, "data pictures, pacing, cache and encoder are untouched");
        prepared.Indices.Should().Equal(inline.Indices);
        prepared.Note.Should().Be(inline.Note);
        inline.WorkerOverlayEncodeMax.Should().BeGreaterThan(0);
        prepared.WorkerOverlayEncodeMax.Should().Be(0, "the worker sent prepared bytes; it encoded no overlay itself");
    }

    private sealed record ScenarioRun(List<byte[]> Pngs, string[] Data, int[] Indices, string? Note, double WorkerOverlayEncodeMax);

    private static async Task<ScenarioRun> ScenarioAsync(bool prepared)
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(prepared ? Prepared("scenario") : NativeWorkerHarness.NewTrial("scenario"), prepared ? encoder : null);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        foreach (var cpu in new[] { 20, 30 })
        {
            if (prepared) await IdleAsync(h);
            await PictureAsync(h);
            h.SetTelemetry(cpu);
            await PictureAsync(h);
        }

        if (prepared) await IdleAsync(h);
        await PictureAsync(h);
        NativeVideoPreparedOverlayDto? before = prepared ? PreparedFacts(h) : null;
        var preparerRequests = prepared ? Preparer(h).Snapshot().Requests : 0;
        var pngs = WorkerPngs(h.Writes).Count;
        for (var i = 0; i < 6; i++) await PictureAsync(h);
        WorkerPngs(h.Writes).Should().HaveCount(pngs, "a static overlay is not sent again");
        if (prepared)
        {
            var after = PreparedFacts(h);
            var s = Preparer(h).Snapshot();
            after.Requests.Should().Be(before!.Requests, "no request without a byte-key change");
            s.Requests.Should().Be(preparerRequests);
            (after.Requests, s.Queued, s.Encodes, s.Completed, s.Handed, s.Failed).Should().Be((3L, 3L, 3L, 3L, 3L, 0L));
            (after.LastQueuedStamp, after.LastSentStamp, after.PreparedSends).Should().Be((s.Queued, s.Queued, 3L));
            (after.Mode, after.Outstanding, after.ThreadsCreated, after.Thread).Should().Be(("prepared", false, 1L, "Running"));
            encoder.Calls.Should().Be(3);
        }

        await Task.Delay(250); // the sample and row of the last picture are recorded right after its facts
        var perf = h.Worker.Performance;
        return new(WorkerPngs(h.Writes), [.. h.Writes.Where(p => NativeWorkerHarness.Command(p) == TurzxVideoProtocol.DataCommand).Select(NativeWorkerHarness.Line)],
            [.. perf.SourceFrames!.Select(r => r.SourceIndex)], perf.NativeVideo!.OverlayEncodingNote, perf.Stages["videoOverlayEncode"].MaxMs);
    }

    /// <summary>
    /// The 36 representative overlays (six layouts by six telemetry states, the S1a benchmark definitions) reach the fake wire
    /// byte-identical through the prepared path and the inline path, in WIC and in filter-NONE mode. Bounded: 36 overlays per run.
    /// Desktop host timings only; nothing here measures a device.
    /// </summary>
    [MediaFoundationFact]
    public async Task The_36_representative_overlays_reach_the_wire_byte_identical_through_the_preparer_in_wic_and_none()
    {
        var summary = new List<string>();
        foreach (var simple in new[] { false, true })
        {
            var inline = await OverlaysAsync(prepared: false, simple);
            var prepared = await OverlaysAsync(prepared: true, simple);
            inline.Pngs.Should().HaveCount(36);
            prepared.Pngs.Select(Sha).Should().Equal(inline.Pngs.Select(Sha), simple ? "NONE" : "WIC");
            prepared.Notes.Should().Equal(inline.Notes);
            prepared.Data.Should().Equal(inline.Data);
            inline.Notes.Should().OnlyContain(n => n == (simple ? "RGBA8 filter NONE PNG; exact lossless repack" : "RGBA8 WIC PNG"));
            var s = prepared.Snapshot!.Value;
            (s.Queued, s.Encodes, s.Completed, s.Handed, s.Failed, s.StaleResults, s.ThreadsCreated).Should().Be((36L, 36L, 36L, 36L, 0L, 0L, 1L));
            inline.WorkerOverlayEncodeMs.Count(v => v > 0).Should().Be(36, "one inline encode per sent overlay");
            prepared.WorkerOverlayEncodeMs.Should().OnlyContain(v => v == 0, "the worker encoded no overlay itself");
            var mode = simple ? "none" : "wic";
            summary.Add(string.Create(CultureInfo.InvariantCulture,
                $"{mode}: overlays={inline.Pngs.Count} identical_sha256={prepared.Pngs.Zip(inline.Pngs).Count(p => p.First.AsSpan().SequenceEqual(p.Second))}/{inline.Pngs.Count} png_bytes median={Median(inline.Pngs.Select(p => (double)p.Length))} max={inline.Pngs.Max(p => p.Length)}; worker_inline_overlay_encode_ms median={Median(inline.WorkerOverlayEncodeMs.Where(v => v > 0)):F2} max={inline.WorkerOverlayEncodeMs.Max():F2}; worker_prepared_overlay_encode_ms max={prepared.WorkerOverlayEncodeMs.Max():F2} (pictures={prepared.WorkerOverlayEncodeMs.Count}); preparer_encode_ms max={s.MaxEncodeMs:F2} last={s.LastEncodeMs:F2}; preparer_managed_alloc_per_encode max={s.MaxAllocatedBytes} B"));
        }

        foreach (var line in summary) output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("S1B_CAPTURE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "s1b-36-overlay-wire-benchmark.txt"), summary);
        }
    }

    private sealed record OverlayRun(List<byte[]> Pngs, List<string?> Notes, string[] Data, List<double> WorkerOverlayEncodeMs,
        OverlayPreparerSnapshot? Snapshot);

    private sealed class TelemetryBox
    {
        private PcTelemetryDto? _value;

        public PcTelemetryDto? Value { get => Volatile.Read(ref _value); set => Volatile.Write(ref _value, value); }
    }

    private static readonly ScreenOverlayLayoutDto ExampleRegion = new(0.5, 0.2, 0.47, 0.66, 2, 0);

    private static ScreenAssignmentDto Example(ScreenAssignmentDto a) =>
        a with { Overlay = "dashboard", AccentColor = "#FAD61D", Fit = "contain", OverlayLayout = ExampleRegion, Rotation = 0, Brightness = 1 };

    private static readonly (string Name, Func<ScreenAssignmentDto, ScreenAssignmentDto> Layout)[] Layouts =
    [
        ("example-region", Example),
        ("dashboard", a => a with { Overlay = "dashboard", AccentColor = "#5DDAC8", Fit = "cover", OverlayLayout = null, Rotation = 0, Brightness = 1 }),
        ("compact", a => a with { Overlay = "compact", AccentColor = "#5DDAC8", Fit = "cover", OverlayLayout = null, Rotation = 0, Brightness = 1 }),
        ("region-backed", a => Example(a) with { OverlayLayout = new(0.70, 0.0, 0.30, 1.0, 1, 0.6) }),
        ("region-full-4col", a => Example(a) with { OverlayLayout = new(0.0, 0.0, 1.0, 1.0, 4, 0.0) }),
        ("example-rotated-dimmed", a => Example(a) with { Rotation = 180, Brightness = 0.6 }),
    ];

    private static readonly PcTelemetryDto?[] States =
    [
        HeadlessProbe.Telemetry, RoundExamples.Long, RoundExamples.NoGpu, null,
        new(HeadlessProbe.Now, 0, 4L << 30, 32L << 30, 0, 30, 1L << 30, 8L << 30, "bench", null),
        new(HeadlessProbe.Now, 57, (long)(21.3 * (1L << 30)), 64L << 30, 88, 74, (long)(11.2 * (1L << 30)), 24L << 30, "bench", null),
    ];

    private static async Task<OverlayRun> OverlaysAsync(bool prepared, bool simple)
    {
        var telemetry = new TelemetryBox();
        using var encoder = new OverlayEncoder();
        var trial = NativeWorkerHarness.NewTrial("bench-" + simple, t => t with { PreparedOverlay = prepared, SimpleOverlayPng = simple });
        using var h = await StartAsync(trial, prepared ? encoder : null, overlay: "none", customize: c => c with { Telemetry = () => telemetry.Value });
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        var notes = new List<string?>();
        foreach (var (_, layout) in Layouts)
        {
            for (var k = 0; k < States.Length; k++)
            {
                telemetry.Value = States[k] is { } t ? t with { Timestamp = h.Time.GetUtcNow() } : null;
                if (k == 0) await h.PostAsync(layout(h.Assignment));
                else await PictureAsync(h);
                if (prepared) await IdleAsync(h);
                await PictureAsync(h);
                notes.Add(Facts(h).OverlayEncodingNote);
            }
        }

        await Task.Delay(250);
        var rows = h.Worker.Performance.SourceFrames!; // at most 73 rows of this trial: one per picture
        return new(WorkerPngs(h.Writes), notes,
            [.. h.Writes.Where(p => NativeWorkerHarness.Command(p) == TurzxVideoProtocol.DataCommand).Select(NativeWorkerHarness.Line)],
            [.. rows.Select(r => r.OverlayEncodeMs)], prepared ? Preparer(h).Snapshot() : null);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    [MediaFoundationFact]
    public async Task Structural_changes_and_mode_flips_send_a_fresh_exact_overlay_and_an_equal_assignment_does_not()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("flip"), encoder);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        WorkerPngs(h.Writes).Should().BeEmpty("prepared mode sends the first overlay once it is ready, never inline");
        await IdleAsync(h);
        await PictureAsync(h);
        var pngs = WorkerPngs(h.Writes);
        pngs.Should().ContainSingle().Which.Should().Equal(await ExactAsync(encoder.Last));
        var g1 = PreparedFacts(h).Generation;

        await h.PostAsync(h.Assignment with { Rotation = 180 });
        PreparedFacts(h).Generation.Should().BeGreaterThan(g1, "a new assignment is a structural change");
        await IdleAsync(h);
        encoder.Last.Assignment.Rotation.Should().Be(180);
        await PictureAsync(h);
        pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(2);
        pngs[1].Should().Equal(await ExactAsync(encoder.Last)).And.NotEqual(pngs[0]);
        var g2 = PreparedFacts(h).Generation;
        var requests = PreparedFacts(h).Requests;

        await h.PostAsync(h.Assignment with { });
        (PreparedFacts(h).Generation, PreparedFacts(h).Requests).Should().Be((g2, requests), "an equal assignment is not structural");
        WorkerPngs(h.Writes).Should().HaveCount(2);

        h.Trial = NativeWorkerHarness.NewTrial("flip"); // the same trial id, inline
        await PictureAsync(h);
        pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(3, "the flip to inline re-sends the current overlay in the same picture");
        pngs[2].Should().Equal(pngs[1]);
        Facts(h).PreparedOverlay.Should().BeNull("an inline trial reports no prepared-overlay facts");

        h.Trial = Prepared("flip"); // and back
        await PictureAsync(h);
        await IdleAsync(h);
        await PictureAsync(h);
        pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(4, "a mode flip forces a fresh overlay although the layer did not change");
        pngs[3].Should().Equal(pngs[1]);
        PreparedFacts(h).Generation.Should().BeGreaterThan(g2);
        PreparedFacts(h).Mode.Should().Be("prepared");
        encoder.Calls.Should().Be(3);
        Preparer(h).Snapshot().ThreadsCreated.Should().Be(1);
    }

    [MediaFoundationFact]
    public async Task Disabling_the_overlay_clears_at_once_and_an_older_pending_overlay_is_never_sent()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("clear"), encoder);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        WorkerPngs(h.Writes).Should().HaveCount(1);

        encoder.Gated = true;
        h.SetTelemetry(55);
        await PictureAsync(h);
        await encoder.WaitCallsAsync(2);
        await h.PostAsync(h.Assignment with { Overlay = "none" });
        var pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(2, "the disabled overlay clears in that same picture, not after the pending encode");
        pngs[1].Should().Equal(BeginPng(h.Writes), "the clear is the session's transparent PNG");
        (PreparedFacts(h).ClearSends, PreparedFacts(h).Outstanding).Should().Be((1, false));

        encoder.Release();
        await Wait.UntilAsync(() => Preparer(h).Snapshot() is { InFlight: false, Poisoned: >= 1 }, "the older encode returns and is poisoned");
        for (var i = 0; i < 3; i++) await PictureAsync(h);
        WorkerPngs(h.Writes).Should().HaveCount(2, "nothing prepared before the clear is ever sent");
        Preparer(h).Snapshot().Handed.Should().Be(1);

        encoder.Gated = false;
        await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
        await IdleAsync(h);
        await PictureAsync(h);
        pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(3);
        pngs[2].Should().Equal(await ExactAsync(encoder.Last));
    }

    [MediaFoundationFact]
    public async Task A_failing_encode_switches_this_trial_to_inline_in_the_same_picture_without_ending_it()
    {
        var log = new RecordingLogger();
        using var encoder = new OverlayEncoder { Fail = true };
        using var h = await StartAsync(Prepared("fail"), encoder, customize: c => c with { Log = log });
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await Wait.UntilAsync(() => Preparer(h).Snapshot().Failed == 1, "the failed encode");
        await PictureAsync(h);
        var pngs = WorkerPngs(h.Writes);
        pngs.Should().ContainSingle("the current overlay is sent inline in the picture that observed the failure");
        pngs[0].Should().Equal(await ExactAsync(encoder.Last));
        var facts = PreparedFacts(h);
        (facts.Mode, facts.TrialId).Should().Be(("inline-fallback", "fail"));
        facts.FallbackReason.Should().StartWith("encode failed: InvalidOperationException");
        (h.Aborts, h.Trial is not null).Should().Be((0, true), "the worker never ends the trial for this");
        log.Messages.Should().ContainSingle(m => m.Contains("inline overlay for the rest of this trial", StringComparison.Ordinal));

        h.SetTelemetry(66);
        await PictureAsync(h);
        WorkerPngs(h.Writes).Should().HaveCount(2, "inline, in the same picture as the change");
        (encoder.Calls, PreparedFacts(h).Requests, PreparedFacts(h).Mode).Should().Be((1, 1L, "inline-fallback"), "sticky for the trial");
        Facts(h).OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
    }

    [MediaFoundationFact]
    public async Task An_encode_that_never_returns_falls_back_after_two_seconds_a_static_overlay_never_does_and_a_new_trial_clears_it()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("watch-1"), encoder);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        for (var i = 0; i < 25; i++) await PictureAsync(h); // 2.5 s with nothing outstanding
        (PreparedFacts(h).Mode, PreparedFacts(h).Outstanding, PreparedFacts(h).Requests).Should().Be(("prepared", false, 1L));
        WorkerPngs(h.Writes).Should().HaveCount(1);

        encoder.Gated = true;
        h.SetTelemetry(70);
        await PictureAsync(h); // the request that never returns
        await encoder.WaitCallsAsync(2);
        for (var i = 0; i < 20; i++) await PictureAsync(h);
        var waiting = PreparedFacts(h);
        (waiting.Mode, waiting.Outstanding, waiting.OutstandingMs).Should().Be(("prepared", true, 2000.0), "exactly 2 s is not yet over the bound");
        WorkerPngs(h.Writes).Should().HaveCount(1);
        await PictureAsync(h);
        var pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(2, "after 2.1 s the current overlay goes inline in that picture");
        pngs[1].Should().Equal(await ExactAsync(encoder.Last));
        var fallback = PreparedFacts(h);
        (fallback.Mode, fallback.FallbackReason).Should().Be(("inline-fallback", "no prepared overlay within 2 s"));
        var s = Preparer(h).Snapshot();
        (s.ThreadsCreated, s.JoinTimeouts, s.Thread, s.InFlight).Should().Be((1L, 0L, OverlayPreparerThreadState.Running, true),
            "no stop, join, restart or replacement thread; the stuck encode simply keeps its one thread");
        h.Aborts.Should().Be(0);

        encoder.Gated = false;
        encoder.Release();
        await Wait.UntilAsync(() => Preparer(h).Snapshot() is { InFlight: false }, "the stuck encode returns");
        await SwitchTrialAsync(h, Prepared("watch-2"));
        await IdleAsync(h);
        await PictureAsync(h);
        pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(3);
        pngs[2].Should().Equal(await ExactAsync(encoder.Last));
        var fresh = PreparedFacts(h);
        (fresh.TrialId, fresh.Mode, fresh.FallbackReason, fresh.PreparedSends, fresh.ThreadsCreated).Should().Be(("watch-2", "prepared", null, 1L, 1L));
        Preparer(h).Snapshot().Handed.Should().Be(2, "the stuck result was poisoned, never handed");
    }

    [MediaFoundationFact]
    public async Task One_preparer_thread_serves_three_sessions_and_ending_a_session_never_joins_it()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("s1"), encoder);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        var preparer = Preparer(h);

        encoder.Gated = true; // session 1 ends by expiry while an encode is in flight
        h.SetTelemetry(41);
        await PictureAsync(h);
        await encoder.WaitCallsAsync(2);
        (await EndTrialAsync(h)).Should().Equal(123, 111, 114, ScreenProtocol.PushPngCommand);
        var s = preparer.Snapshot();
        (s.Thread, s.ThreadAlive, s.InFlight, s.JoinTimeouts).Should().Be((OverlayPreparerThreadState.Running, true, true, 0L),
            "the session stopped at once; the preparer was invalidated, not joined");
        encoder.Gated = false;
        encoder.Release();
        await Wait.UntilAsync(() => preparer.Snapshot() is { InFlight: false, Poisoned: >= 1 }, "the in-flight encode is poisoned");

        foreach (var (id, post) in new[] { ("s2", true), ("s3", false) })
        {
            await SwitchTrialAsync(h, Prepared(id));
            await IdleAsync(h);
            await PictureAsync(h);
            PreparedFacts(h).TrialId.Should().Be(id);
            (await EndTrialAsync(h, post)).Should().Equal(123, 111, 114, ScreenProtocol.PushPngCommand);
            h.Worker.OverlayPreparerForTest.Should().BeSameAs(preparer);
        }

        Commands(h.Writes).Count(c => c == 41).Should().Be(3, "three native sessions");
        WorkerPngs(h.Writes).Should().HaveCount(3, "one overlay per session; the poisoned one never");
        s = preparer.Snapshot();
        (s.ThreadsCreated, s.Thread, s.JoinTimeouts, s.Handed).Should().Be((1L, OverlayPreparerThreadState.Running, 0L, 3L), "the idle thread is kept across sessions");
        h.Worker.Dispose();
        s = preparer.Snapshot();
        (s.Thread, s.ThreadAlive, s.JoinTimeouts).Should().Be((OverlayPreparerThreadState.Disposed, false, 0L), "worker exit joins the idle thread");
    }

    [MediaFoundationFact]
    public async Task Blackout_turns_the_backlight_off_first_without_joining_the_preparer_and_sleeping_keeps_only_active_trial_rows()
    {
        var dark = 0;
        var directory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var encoder = new OverlayEncoder();
            using var h = await StartAsync(Prepared("dark"), encoder,
                customize: c => c with { WholeCaseBlackout = () => Volatile.Read(ref dark) != 0, BacklightStateDirectory = directory });
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
            await IdleAsync(h);
            await PictureAsync(h);
            encoder.Gated = true;
            h.SetTelemetry(33);
            await PictureAsync(h);
            await encoder.WaitCallsAsync(2);
            var before = h.Writes.Length;
            Volatile.Write(ref dark, 1);
            await Wait.UntilAsync(() => h.Worker.Snapshot().State == ScreenStates.Sleeping, "sleeping");
            var after = h.Writes.Skip(before).ToArray();
            Commands(after).Should().Equal(14, 123, 111, 114);
            var s = Preparer(h).Snapshot();
            (s.Thread, s.InFlight, s.JoinTimeouts, s.ThreadsCreated).Should().Be((OverlayPreparerThreadState.Running, true, 0L, 1L));

            var sleeping = h.Worker.Performance;
            sleeping.NativeVideo.Should().BeNull("the session is closed");
            sleeping.SourceFrames.Should().NotBeNullOrEmpty("the trial is still the active one");
            sleeping.TrialId.Should().Be("dark");
            h.Trial = null; // ends while asleep: its rows disappear at once, without another sample
            h.Worker.Performance.SourceFrames.Should().BeNull();
            h.Worker.Performance.TrialId.Should().Be("dark", "the cached last-sampled trial id is unchanged");

            encoder.Gated = false;
            encoder.Release();
            await Wait.UntilAsync(() => Preparer(h).Snapshot() is { InFlight: false, Poisoned: >= 1 }, "the in-flight encode is poisoned");
            var writes = h.Writes.Length;
            await Task.Delay(300);
            h.Writes.Length.Should().Be(writes, "nothing is sent while sleeping");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Worker_exit_waits_at_most_the_bounded_join_and_reports_an_abandoned_encode()
    {
        var log = new RecordingLogger();
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("exit"), encoder, customize: c => c with { Log = log });
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        encoder.Gated = true;
        h.SetTelemetry(44);
        await PictureAsync(h);
        await encoder.WaitCallsAsync(2);
        var preparer = Preparer(h);
        var watch = Stopwatch.StartNew();
        h.Worker.Dispose();
        watch.Stop();
        var s = preparer.Snapshot();
        (s.Thread, s.ThreadAlive, s.JoinTimeouts, s.ThreadsCreated).Should().Be((OverlayPreparerThreadState.Disposed, true, 1L, 1L));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "one bounded 500 ms join, well inside the worker's own 10 s bound");
        log.Messages.Should().ContainSingle(m => m.Contains("abandoned", StringComparison.Ordinal));
        encoder.Release();
        await Wait.UntilAsync(() => !preparer.Snapshot().ThreadAlive, "the abandoned thread exits once its encode returns");
    }

    /// <summary>
    /// A schema4 sequence asset (two 2000 ms clips of the h264-30 fixture): crossing the clip boundary is not a new assignment, so a
    /// fixed telemetry overlay stays continuous (same generation, no request, no PNG) while the data pictures move to clip two.
    /// </summary>
    [MediaFoundationFact]
    public async Task A_schema4_clip_boundary_keeps_a_fixed_overlay_continuous()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("clips"), encoder);
        var scratch = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-30.mp4"));
            var asset = await Sta.RunAsync(() =>
            {
                var probe = SegmentedVideo.Probe(bytes, scratch, CancellationToken.None);
                return h.Assets.CommitSequence("clips", [new(bytes, 2000), new(bytes, 2000)], [probe, probe]);
            });
            (asset.FrameCount, asset.DurationMs).Should().Be((120, 4000));
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
            await h.PostAsync(h.Assignment with { BackgroundAssetId = asset.Id });
            await IdleAsync(h);
            await PictureAsync(h);
            var start = PreparedFacts(h);
            var pngs = WorkerPngs(h.Writes).Count;
            var overlayWrites = Facts(h).OverlayWrites;
            for (var i = 0; i < 25; i++) await PictureAsync(h); // 2.6 s after the post: across the 2000 ms boundary
            await Task.Delay(250);
            var rows = h.Worker.Performance.SourceFrames!.Where(r => r.SourceCount == 120).ToArray();
            rows.Should().HaveCount(27, "the post picture and 26 more of the sequence");
            rows.Should().Contain(r => r.SourceIndex < 60).And.Contain(r => r.SourceIndex >= 60, "the pictures crossed into clip two");
            var end = PreparedFacts(h);
            (end.Generation, end.Requests, end.PreparedSends, end.Outstanding).Should().Be((start.Generation, start.Requests, start.PreparedSends, false));
            WorkerPngs(h.Writes).Should().HaveCount(pngs);
            Facts(h).OverlayWrites.Should().Be(overlayWrites);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
    }

    /// <summary>
    /// Every layer handed to the preparer is a renderer-owned 1920x480 RGBA raster (3,686,400 B) that nobody writes afterwards; the
    /// preparer references at most two of them at any time, and each prepared send is the exact encoding of its layer.
    /// </summary>
    [MediaFoundationFact]
    public async Task Prepared_layers_are_immutable_renderer_rasters_and_at_most_two_are_referenced()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("raster"), encoder);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        for (var cpu = 21; cpu <= 26; cpu++)
        {
            h.SetTelemetry(cpu);
            await PictureAsync(h);
            var s = Preparer(h).Snapshot();
            s.RetainedLayers.Should().BeInRange(1, 2);
            s.RetainedLayerBytes.Should().Be(s.RetainedLayers * (long)LayerBytes);
            await IdleAsync(h);
            await PictureAsync(h);
        }

        var seen = encoder.Seen.ToArray();
        seen.Should().HaveCount(7);
        seen.Select(x => x.Request.Layer).Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(7, "a changed text is a new raster, never an overwritten one");
        seen.Should().OnlyContain(x => x.Request.Layer.ByteCount == LayerBytes && x.Request.Layer.Width == 1920 && x.Request.Layer.Height == 480);
        seen.Should().OnlyContain(x => Sha(x.Request.Layer.Pixels) == x.LayerSha, "no raster was written after it was handed over");
        var pngs = WorkerPngs(h.Writes);
        pngs.Should().HaveCount(7);
        for (var i = 0; i < 7; i++) pngs[i].Should().Equal(await ExactAsync(seen[i].Request));
    }

    /// <summary>
    /// Worker-thread managed allocation over 12 steady pictures (static overlay, warm caches), measured by a probe on the worker's own
    /// thread: the prepared path adds no per-picture allocation over the default path (no request, take or publish without a change).
    /// </summary>
    [MediaFoundationFact]
    public async Task Steady_prepared_pictures_allocate_no_more_on_the_worker_than_default_pictures()
    {
        var inline = await SteadyAllocationAsync(prepared: false);
        var prepared = await SteadyAllocationAsync(prepared: true);
        output.WriteLine(FormattableString.Invariant($"worker-thread managed bytes over 12 steady pictures: default={inline} prepared={prepared}"));
        (prepared - inline).Should().BeLessThan(12 * 256, "no per-picture prepared-overlay allocation beyond measurement noise");
    }

    private sealed class AllocationProbe
    {
        private long _last = -1;

        public long Last => Volatile.Read(ref _last);

        public void Mark()
        {
            if (Thread.CurrentThread.Name?.StartsWith("screen ", StringComparison.Ordinal) == true)
                Volatile.Write(ref _last, GC.GetAllocatedBytesForCurrentThread());
        }
    }

    private static async Task<long> SteadyAllocationAsync(bool prepared)
    {
        var probe = new AllocationProbe();
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(prepared ? Prepared("steady") : NativeWorkerHarness.NewTrial("steady"), prepared ? encoder : null,
            customize: c => c with { VideoTrialOf = id => { probe.Mark(); return c.VideoTrialOf(id); } });
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        if (prepared) await IdleAsync(h);
        for (var i = 0; i < 8; i++) await PictureAsync(h); // two loops of the 4-frame source: warm caches
        await Task.Delay(150);
        var requests = prepared ? Preparer(h).Snapshot().Requests : 0;
        var start = probe.Last;
        for (var i = 0; i < 12; i++) await PictureAsync(h);
        var end = probe.Last;
        if (prepared) Preparer(h).Snapshot().Requests.Should().Be(requests);
        start.Should().BePositive();
        return end - start;
    }
}
