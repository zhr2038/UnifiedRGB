using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>HOST source-frame selection diagnostics: exact skip/repeat/discontinuity semantics, bounds, allocation and JSON compatibility.</summary>
public sealed class SourceFrameDiagnosticsTests
{
    private static readonly ScreenDeviceDto Device = new("s", "test", "Turzx", 480, 1920, "Running", null, false, 1000, 0, null, 54);

    /// <summary>A 60 FPS MP4-like source: 960 frames of 17/17/16 ms, exactly 16 s per loop (indices 0..959).</summary>
    private static readonly StoredAsset Source60 = Asset("source60", [.. Enumerable.Range(0, 960).Select(i => i % 3 == 2 ? 16 : 17)]);

    private static StoredAsset Asset(string id, int[] delays) => new(id, id, "mp4", 2, 2, delays, 0);

    private static long Start(StoredAsset a, int index) => a.DelaysMs.Take(index).Sum();

    private static long Mid(StoredAsset a, int index) => Start(a, index) + (a.DelaysMs[index] / 2);

    /// <summary>Feeds samples exactly as the worker does: index = FrameAt(position) and a monotonic host timestamp (µs ticks).</summary>
    private sealed class Feed(ScreenPerformance perf, string? trial = "lab")
    {
        public const long Frequency = 1_000_000;
        private long _ticks = 5_000_000;

        public string? Trial { get; set; } = trial;

        public void At(StoredAsset? asset, long positionMs, double hostStepMs, bool newGeneration = false, bool cacheHit = false)
        {
            _ticks += (long)Math.Round(hostStepMs * 1000);
            perf.Add(Work, 60, Trial, null, 0,
                new ScreenSourceSample(asset, asset?.FrameAt(positionMs) ?? 0, positionMs, _ticks, Frequency, cacheHit, newGeneration));
        }
    }

    private static readonly ScreenFrameWork Work = new(1, 2, 3, 4, 5, 6, 7, 8, 100, 612, 1, 0, 0,
        Native: new NativeVideoWork(OverlayWrite: 0.5, OverlayRead: 0.25, QueueSleep: 3), OverlayCompose: 0.75, OverlayEncode: 1.5);

    private static (ScreenSourceSelectionDto Summary, IReadOnlyList<ScreenSourceFrameDto> Rows) Read(ScreenPerformance perf)
    {
        var dto = perf.Snapshot(Device);
        return (dto.SourceSelection!, dto.SourceFrames!);
    }

    private static string Kind(ScreenSourceFrameDto r) => r.Discontinuity is { } why ? r.Selection + ":" + why : r.LoopWrap ? r.Selection + "+wrap" : r.Selection;

    [Fact]
    public void Source60_skip_repeat_and_same_loop_wrap_are_exact_and_independent_of_dropped()
    {
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        feed.At(Source60, Mid(Source60, 47), 16.7);
        feed.At(Source60, Mid(Source60, 48), Mid(Source60, 48) - Mid(Source60, 47));
        feed.At(Source60, Mid(Source60, 48) + 1, 1); // the same picture selected again
        feed.At(Source60, Mid(Source60, 50), Mid(Source60, 50) - Mid(Source60, 48) - 1); // 48 -> 50: exactly one skipped index
        feed.At(Source60, Mid(Source60, 958), 16.7, newGeneration: true);
        feed.At(Source60, Mid(Source60, 959), Mid(Source60, 959) - Mid(Source60, 958));
        feed.At(Source60, 16_000 + Mid(Source60, 0), 16_000 + Mid(Source60, 0) - Mid(Source60, 959)); // local epoch: monotonic
        feed.At(Source60, 16_000 + Mid(Source60, 1), Mid(Source60, 1) - Mid(Source60, 0));
        feed.At(Source60, Mid(Source60, 959), 16.7, newGeneration: true);
        feed.At(Source60, Mid(Source60, 0), Mid(Source60, 0) + 16_000 - Mid(Source60, 959)); // shared loop position wraps 959 -> 0

        var (summary, rows) = Read(perf);
        rows.Select(Kind).Should().Equal("start", "advance", "repeat", "skip", "start", "advance", "advance+wrap", "advance", "start", "advance+wrap");
        rows.Select(r => r.SourceIndex).Should().Equal(47, 48, 48, 50, 958, 959, 0, 1, 959, 0);
        rows.Single(r => r.Selection == "skip").SkippedIndices.Should().Be(1);
        summary.Should().Be(new ScreenSourceSelectionDto(Generation: 3, Submissions: 10, NoSource: 0, Starts: 3, Advances: 5, Repeats: 1,
            Skips: 1, SkippedIndices: 1, Discontinuities: 0, LoopWraps: 2));
        perf.Snapshot(Device).Dropped.Should().Be(0, "transport Dropped is the device's own counter and is not touched");
        rows.Should().OnlyContain(r => r.SourceCount == 960 && r.SourceLoopMs == 16_000 && r.SourceFrameMs == Source60.DelaysMs[r.SourceIndex]);
    }

    [Fact]
    public void Backward_seek_clock_jump_pause_and_whole_loop_gaps_never_count_missing_frames()
    {
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        feed.At(Source60, Mid(Source60, 500), 16.7);
        feed.At(Source60, Mid(Source60, 10), 16.7);                       // backward seek: not 469 skipped, not 959 missing
        feed.At(Source60, Mid(Source60, 400), 16.7);                      // forward seek: 390 steps in 16.7 ms of host time
        feed.At(Source60, Mid(Source60, 400), 16.7);                      // paused source
        feed.At(Source60, Mid(Source60, 400), 16.7);
        feed.At(Source60, Mid(Source60, 401), Mid(Source60, 401) - Mid(Source60, 400)); // resumes
        feed.At(Source60, Mid(Source60, 404), 16.7);                      // a 50 ms clock jump in 16.7 ms
        feed.At(Source60, Mid(Source60, 404) + 16_000, 16_000);           // a stall of a whole loop: which indices is unknown
        feed.At(Source60, Mid(Source60, 404) + 16_000 + 17, 17);          // continues from there
        feed.At(Source60, Mid(Source60, 900), 1000);                      // backward although the host waited a second
        var (summary, rows) = Read(perf);
        rows.Select(Kind).Should().Equal("start", "discontinuity:backward", "discontinuity:jump", "repeat", "repeat", "advance",
            "discontinuity:jump", "discontinuity:loop-gap", "advance", "discontinuity:backward");
        summary.SkippedIndices.Should().Be(0);
        summary.Skips.Should().Be(0);
        summary.Discontinuities.Should().Be(5);
        rows.Should().OnlyContain(r => r.SkippedIndices == 0);
    }

    private static string Classified(int previousIndex, long previousPosition, int index, long position, double hostMs, int count = 960, int loopMs = 16_000)
    {
        var kind = ScreenSourceTrace.Classify(count, loopMs, previousIndex, previousPosition, index, position, hostMs, out var skipped, out var wrap, out var gap);
        return $"{ScreenSourceTrace.Name(kind)}:{ScreenSourceTrace.Name(gap) ?? "-"}:{skipped}:{wrap}";
    }

    [Fact]
    public void A_shared_position_modulo_the_loop_never_claims_indices_across_a_possible_hidden_loop()
    {
        const string LoopGap = "discontinuity:loop-gap:0:False";
        // Root review (CODEX-classification-review.json), exact inputs: a whole loop of host time may hide whole loops.
        Classified(0, 0, 1, 17, 16_017).Should().Be(LoopGap, "17 ms or 16017 ms of a modulo source: not a claimed consecutive advance");
        Classified(0, 0, 0, 0, 16_000).Should().Be(LoopGap, "the same modulo position after exactly one loop of host time is not a repeat");
        Classified(0, 0, 1, 17, 32_017).Should().Be(LoopGap);
        Classified(0, 0, 0, 2, 15_995).Should().Be(LoopGap, "2 ms plus one hidden loop matches the host time within tolerance");
        Classified(0, 8, 0, 16_008, 16_000).Should().Be(LoopGap, "unwrapped local time: the source itself advanced a loop");

        // Unchanged neighbours: exact counts are still claimed when no whole loop can hide.
        Classified(959, 15_992, 0, 8, 16).Should().Be("advance:-:0:True", "the legitimate same-loop boundary of a modulo position");
        Classified(0, 8, Source60.FrameAt(13_008), 13_008, 13_000).Should().Be("skip:-:779:False", "a consistent stall shorter than a loop");
        Classified(48, Mid(Source60, 48), 48, Mid(Source60, 48), 0).Should().Be("repeat:-:0:False", "pacing catch-up at the same instant");
        Classified(400, Mid(Source60, 400), 400, Mid(Source60, 400), 5000).Should().Be("repeat:-:0:False", "a pause shorter than a loop");
        Classified(500, Mid(Source60, 500), 10, Mid(Source60, 10), 16.7).Should().Be("discontinuity:backward:0:False");
        Classified(401, Mid(Source60, 401), 404, Mid(Source60, 404), 16.7).Should().Be("discontinuity:jump:0:False");

        // Invalid inputs (the worker clamps positions and its host clock is monotonic) claim nothing.
        const string Unknown = "discontinuity:unknown:0:False";
        Classified(0, 8, 0, -8, 16).Should().Be(Unknown);
        Classified(0, -8, 0, 8, 16).Should().Be(Unknown);
        Classified(0, 8, 1, 25, -1).Should().Be(Unknown);
        Classified(0, 8, 1, 25, double.NaN).Should().Be(Unknown);
        Classified(0, 8, 1, 25, double.PositiveInfinity).Should().Be(Unknown);
        Classified(0, 8, 960, 25, 16).Should().Be(Unknown);
        Classified(-1, 8, 1, 25, 16).Should().Be(Unknown);
        Classified(0, 8, 1, 25, 16, count: 1).Should().Be(Unknown);
        Classified(0, 8, 1, 25, 16, loopMs: 0).Should().Be(Unknown);
    }

    [Fact]
    public void Recorded_modulo_rows_after_whole_loop_host_gaps_add_no_skips_and_playback_continues_exactly()
    {
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        feed.At(Source60, Mid(Source60, 0), 16.7);
        feed.At(Source60, 17, 16_017);                       // Root's case 1 through the recorder
        feed.At(Source60, 34, 17);                           // continues from the new position
        feed.At(Source60, 34, 16_000);                       // Root's case 2 through the recorder
        feed.At(Source60, 51, 17);                           // index 3
        feed.At(Source60, 68, -5);                           // index 4, but the host clock went backwards
        feed.At(Source60, 85, 17);                           // index 5
        var (summary, rows) = Read(perf);
        rows.Select(Kind).Should().Equal("start", "discontinuity:loop-gap", "advance", "discontinuity:loop-gap", "advance",
            "discontinuity:unknown", "advance");
        rows.Select(r => r.HostIntervalMs).Should().Equal(0, 16_017, 17, 16_000, 17, -5, 17);
        summary.Should().Be(new ScreenSourceSelectionDto(Generation: 1, Submissions: 7, NoSource: 0, Starts: 1, Advances: 3, Repeats: 0,
            Skips: 0, SkippedIndices: 0, Discontinuities: 3, LoopWraps: 0));
    }

    [Fact]
    public void Generation_asset_and_trial_changes_start_fresh_and_never_alias_old_rows()
    {
        var perf = new ScreenPerformance();
        var feed = new Feed(perf, "trial-a");
        var other = Asset("source30", [.. Enumerable.Range(0, 30).Select(i => i % 3 == 2 ? 34 : 33)]);
        feed.At(Source60, Mid(Source60, 958), 16.7);
        feed.At(Source60, Mid(Source60, 959), 16.7);
        feed.At(other, Mid(other, 0), 16.7);                               // asset change: no 959 -> 0 wrap or skip
        feed.At(other, Mid(other, 1), Mid(other, 1) - Mid(other, 0));
        feed.At(Asset("source30", [.. other.DelaysMs]), Mid(other, 2), Mid(other, 2) - Mid(other, 1)); // the same content id reloaded
        feed.At(other, Mid(other, 3), Mid(other, 3) - Mid(other, 2), newGeneration: true); // command/channel/session/failed attempt
        var (_, rows) = Read(perf);
        rows.Select(Kind).Should().Equal("start", "advance", "start", "advance", "advance", "start");
        rows.Select(r => r.Generation).Should().Equal(1, 1, 2, 2, 2, 3);

        feed.Trial = "trial-b";
        feed.At(other, Mid(other, 4), Mid(other, 4) - Mid(other, 3));
        feed.At(other, Mid(other, 5), Mid(other, 5) - Mid(other, 4));
        var (summary, trialRows) = Read(perf);
        trialRows.Select(r => r.Sequence).Should().Equal(7, 8);
        trialRows.Select(Kind).Should().Equal("start", "advance");
        trialRows[0].Generation.Should().Be(4, "a trial change is a new continuity generation even without the worker's flag");
        summary.Submissions.Should().Be(8);
        summary.Starts.Should().Be(4);

        feed.Trial = null;
        feed.At(other, Mid(other, 6), Mid(other, 6) - Mid(other, 5));
        var outside = perf.Snapshot(Device);
        outside.SourceFrames.Should().BeNull("the large per-submission trace is only exposed during an explicit lab trial");
        outside.SourceSelection!.Submissions.Should().Be(9);
        outside.SourceSelection.Generation.Should().Be(5);
    }

    /// <summary>The same playback fed as unwrapped local time and as a shared position modulo the loop must be labelled identically.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonuniform_durations_match_a_brute_force_reference_exactly(bool moduloPosition)
    {
        var random = new Random(20261022);
        var delays = Enumerable.Range(0, 37).Select(_ => random.Next(5, 121)).ToArray();
        var asset = Asset("nonuniform", delays);
        var loop = delays.Sum();
        long Global(long position) => (position / loop * delays.Length) + asset.FrameAt(position);
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        long position = random.Next(0, loop);
        feed.At(asset, position, 16.7);
        var expected = new List<string> { "start" };
        long skippedTotal = 0, steps = 0;
        for (var i = 0; i < 250; i++)
        {
            var step = random.Next(6) switch { 0 => 0, 1 => random.Next(1, 10), 2 => random.Next(10, 40), 3 => random.Next(40, 400), 4 => random.Next(400, loop), _ => loop + random.Next(0, 50) };
            var next = position + step;
            var k = Global(next) - Global(position);
            var wrap = next / loop > position / loop;
            expected.Add(step >= loop ? "discontinuity:loop-gap" : k == 0 ? "repeat" : (k == 1 ? "advance" : "skip") + (wrap ? "+wrap" : ""));
            if (step < loop && k >= 2) skippedTotal += k - 1;
            if (step < loop) steps += k;
            feed.At(asset, moduloPosition ? next % loop : next, step); // host time advanced exactly as far as the source position
            position = next;
        }

        var (summary, rows) = Read(perf);
        rows.Select(Kind).Should().Equal(expected);
        rows.Where(r => r.Discontinuity is not null).Should().OnlyContain(r => r.Discontinuity == "loop-gap" && r.SkippedIndices == 0);
        summary.SkippedIndices.Should().Be(skippedTotal);
        (summary.Advances + summary.Skips + summary.SkippedIndices).Should().Be(steps, "every crossed index is either selected or counted as skipped");
        rows.Should().OnlyContain(r => r.SourceFrameMs == delays[r.SourceIndex] && r.SourceIndex == asset.FrameAt(r.SourcePositionMs));
    }

    [Fact]
    public void Net_source_skips_reconcile_index_advance_with_completed_submissions()
    {
        // Root's lab finding in miniature: a 60 FPS source sampled at ~54/s advances more indices than pictures completed.
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        var random = new Random(54);
        long position = 0;
        feed.At(Source60, position, 0);
        for (var i = 0; i < 1799; i++)
        {
            var step = random.Next(16, 22); // 16..21 ms per completed submission
            position += step;
            feed.At(Source60, position, step);
        }

        var (summary, rows) = Read(perf);
        var advanced = (position / 16_000 * 960) + Source60.FrameAt(position) - Source60.FrameAt(0);
        summary.Discontinuities.Should().Be(0);
        (summary.Advances + summary.Skips + summary.SkippedIndices).Should().Be(advanced);
        summary.SkippedIndices.Should().Be(advanced - (summary.Submissions - 1 - summary.Repeats), "net skips = indices advanced - index-changing submissions");
        summary.SkippedIndices.Should().BePositive();
        rows.Should().HaveCount(256);
        perf.Snapshot(Device).Dropped.Should().Be(0);
    }

    [Fact]
    public void Source30_sent_at_target60_repeats_legitimately_without_any_skip()
    {
        var source30 = Asset("source30", [.. Enumerable.Range(0, 30).Select(i => i % 3 == 2 ? 34 : 33)]);
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        long previous = 0;
        for (var k = 0; k < 600; k++)
        {
            var position = (long)Math.Floor(k * 1000 / 60.0);
            feed.At(source30, position, k == 0 ? 0 : 1000 / 60.0);
            previous = position;
        }

        var (summary, _) = Read(perf);
        previous.Should().Be(9983);
        summary.Skips.Should().Be(0);
        summary.Discontinuities.Should().Be(0);
        summary.Repeats.Should().BeInRange(295, 305);
        (summary.Advances + summary.Repeats).Should().Be(599);
        summary.LoopWraps.Should().Be(9);
    }

    [Fact]
    public void Still_images_missing_assets_and_static_pictures_are_none_and_break_continuity()
    {
        var still = new StoredAsset("still", "still", "png", 2, 2, [0], 0);
        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        feed.At(still, 0, 16.7);
        feed.At(null, 0, 1000);
        feed.At(Source60, Mid(Source60, 10), 16.7);
        feed.At(null, 0, 16.7);                         // e.g. a blocked shared timeline: black, no source
        feed.At(Source60, Mid(Source60, 12), 16.7);     // NOT a skip of index 11 across the black picture
        var (summary, rows) = Read(perf);
        rows.Select(Kind).Should().Equal("none", "none", "start", "none", "start");
        rows.Select(r => (r.SourceIndex, r.SourceCount)).Should().Equal((0, 1), (-1, 0), (10, 960), (-1, 0), (12, 960));
        summary.NoSource.Should().Be(3);
        summary.SkippedIndices.Should().Be(0);
        rows.Select(r => r.Generation).Should().Equal(1, 1, 2, 3, 4);
    }

    [Fact]
    public void Ten_thousand_records_stay_bounded_ordered_after_wrap_and_trial_scoped()
    {
        var perf = new ScreenPerformance();
        var feed = new Feed(perf, "long-trial");
        for (var i = 0; i < 300; i++) feed.At(Source60, i * 17L, 17);
        var (_, early) = Read(perf);
        early.Select(r => r.Sequence).Should().Equal(Enumerable.Range(45, 256).Select(v => (long)v), "oldest first after the ring wrapped");
        for (var i = 300; i < 10_000; i++) feed.At(Source60, i * 17L, 17);
        var dto = perf.Snapshot(Device);
        dto.Samples.Should().Be(256);
        dto.SourceFrames.Should().HaveCount(ScreenSourceTrace.Capacity);
        dto.SourceFrames!.Select(r => r.Sequence).Should().Equal(Enumerable.Range(9745, 256).Select(v => (long)v));
        dto.SourceSelection!.Submissions.Should().Be(10_000);
        dto.SourceSelection.Discontinuities.Should().Be(0);

        feed.Trial = "next-trial";
        feed.At(Source60, 10_000 * 17L, 17);
        perf.Snapshot(Device).SourceFrames!.Select(r => (r.Sequence, r.Selection)).Should().Equal((10_001L, "start"));
        feed.Trial = null;
        feed.At(Source60, 10_001 * 17L, 17);
        perf.Snapshot(Device).SourceFrames.Should().BeNull();
    }

    [Fact]
    public void Recording_a_submission_allocates_nothing()
    {
        var perf = new ScreenPerformance();
        var still = new StoredAsset("still", "still", "png", 2, 2, [0], 0);
        const int N = 10_000;
        var assets = new StoredAsset?[N];
        var indices = new int[N];
        var positions = new long[N];
        for (var i = 0; i < N; i++)
        {
            assets[i] = (i % 97) switch { 0 => null, 1 => still, _ => Source60 };
            positions[i] = i * 19L;
            indices[i] = assets[i]?.FrameAt(positions[i]) ?? 0;
        }

        const string trial = "lab";
        for (var i = 0; i < 1000; i++)
            perf.Add(Work, 60, trial, 4, 0, new ScreenSourceSample(assets[i], indices[i], positions[i], i * 19_000L, 1_000_000, false, i % 500 == 0));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++)
            perf.Add(Work, 60, trial, 4, 0, new ScreenSourceSample(assets[i], indices[i], positions[i], (1000 + i) * 19_000L, 1_000_000, i % 3 == 0, i % 500 == 0));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().Be(0, "recording copies one fixed-size struct into a ring allocated once; metadata is cached per asset identity");
        var probe = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[1]);
        (GC.GetAllocatedBytesForCurrentThread() - probe).Should().BePositive("control: the same probe sees one tiny allocation");
        perf.Snapshot(Device).SourceSelection!.Submissions.Should().Be(N + 1000);
    }

    [Fact]
    public void Existing_constructors_stay_exact_and_new_properties_are_optional_in_source_generated_json()
    {
        typeof(ScreenPerformanceDto).GetConstructors().Select(c => string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)))
            .Should().Equal("String ScreenId,String State,Int64 FramesWritten,Int64 Dropped,Double EffectiveFps,Int32 TargetFps,String TrialId,"
                + "Nullable`1 DeviceSpeedCode,Int32 Samples,Int64 SkippedScheduleSlots,IReadOnlyDictionary`2 Stages,Int64 ImageBytes,Int64 PacketBytes,"
                + "Int64 RawReads,Int64 ReadTimeouts,Int64 ZeroLengthReplies,Int64 MatchedReplies,Int64 CorrelatedTransactions,NativeVideoPerformanceDto NativeVideo");
        typeof(NativeVideoPerformanceDto).GetConstructors().Select(c => string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)))
            .Should().Equal("Int64 AccessUnits,Int64 OverlayWrites,Int32 DeviceBufferStatus,Int64 SourcePositionMs,Int32 SourceFrameIndex,String SourceClock,Int64 LoopWraps");
        ContractsJsonContext.Default.ScreenSourceFrameDto.Should().NotBeNull("the new DTOs are source-generated with the existing context");
        ContractsJsonContext.Default.ScreenSourceSelectionDto.Should().NotBeNull();

        var stages = new Dictionary<string, ScreenTimingDto> { ["compose"] = new(1, 1, 1, 1) };
        var legacy = new ScreenPerformanceDto("s", "Running", 10, 2, 54.5, 60, null, 4, 10, 1, stages, 100, 200, 3, 0, 0, 5, 6);
        using (var legacyJson = JsonDocument.Parse(JsonSerializer.Serialize(legacy, ContractsJsonContext.Default.ScreenPerformanceDto)))
        {
            legacyJson.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("screenId", "state", "framesWritten", "dropped",
                "effectiveFps", "targetFps", "trialId", "deviceSpeedCode", "samples", "skippedScheduleSlots", "stages", "imageBytes", "packetBytes",
                "rawReads", "readTimeouts", "zeroLengthReplies", "matchedReplies", "correlatedTransactions");
        }
        var oldPayload = """{"screenId":"s","state":"Running","framesWritten":10,"dropped":2,"effectiveFps":54.5,"targetFps":60,"samples":10,"skippedScheduleSlots":1,"stages":{},"imageBytes":100,"packetBytes":200,"rawReads":3,"readTimeouts":0,"zeroLengthReplies":0}""";
        var fromOld = JsonSerializer.Deserialize(oldPayload, ContractsJsonContext.Default.ScreenPerformanceDto)!;
        fromOld.SourceSelection.Should().BeNull();
        fromOld.SourceFrames.Should().BeNull();
        fromOld.NativeVideo.Should().BeNull();
        (fromOld.FramesWritten, fromOld.MatchedReplies, fromOld.CorrelatedTransactions).Should().Be((10L, 0L, 0L));

        var perf = new ScreenPerformance();
        var feed = new Feed(perf);
        feed.At(Source60, Mid(Source60, 5), 16.7);
        feed.At(Source60, Mid(Source60, 7), Mid(Source60, 7) - Mid(Source60, 5));
        feed.At(Source60, Mid(Source60, 2), 16.7);
        var current = perf.Snapshot(Device);
        var json = JsonSerializer.Serialize(current, ContractsJsonContext.Default.ScreenPerformanceDto);
        json.Should().Contain("\"sourceSelection\"").And.Contain("\"sourceFrames\"").And.Contain("\"discontinuity\": \"backward\"");
        var back = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.ScreenPerformanceDto)!;
        back.SourceSelection.Should().Be(current.SourceSelection);
        back.SourceFrames.Should().Equal(current.SourceFrames!);
        back.SourceFrames![0].Discontinuity.Should().BeNull();
        using (var document = JsonDocument.Parse(json))
            document.RootElement.GetProperty("sourceFrames")[0].TryGetProperty("discontinuity", out _).Should().BeFalse("null labels are omitted");

        var viaOptions = JsonSerializer.Deserialize<ScreenPerformanceDto>(json, ContractsJson.Configure(new JsonSerializerOptions()))!;
        viaOptions.SourceFrames.Should().Equal(current.SourceFrames!);
        var oldClient = JsonSerializer.Deserialize(json, LegacyPerformanceJson.Default.LegacyScreenPerformanceDto)!;
        oldClient.Should().BeEquivalentTo(current, o => o.ComparingByMembers<ScreenPerformanceDto>().ComparingByMembers<LegacyScreenPerformanceDto>()
            .Excluding(d => d.SourceSelection).Excluding(d => d.SourceFrames), "an old client ignores the unknown properties");
    }

    [Fact]
    public async Task Worker_rows_match_each_wire_picture_and_dropped_stays_zero_while_indices_are_skipped()
    {
        await using var s = await SourceStepScenario.StartAsync(timeline: false);
        var (trial, begin) = await BeginTrialAsync(s);
        var shown = new List<int>();
        foreach (var (clock, _) in SourceStepScenario.LocalSteps) shown.AddRange(await s.StepAsync(clock));
        shown.Should().Equal(SourceStepScenario.Expected(SourceStepScenario.LocalSteps), "diagnostics do not change what is selected or sent");

        var perf = (await s.Rig.Manager.GetScreenPerformanceAsync()).Single();
        perf.TrialId.Should().Be(trial);
        var rows = TrialRows(perf, begin);
        AssertCoherent(s, rows);
        rows.Select(Kind).Should().Equal("start", "advance", "advance", "advance", "repeat", "advance", "advance", "repeat", "skip", "repeat",
            "skip+wrap", "repeat", "advance", "discontinuity:loop-gap", "repeat", "advance", "repeat", "skip", "repeat", "advance", "advance",
            "advance+wrap", "repeat");
        rows.Select(r => r.HostIntervalMs).Should().Equal(0, 100, 100, 200, 0, 100, 300, 0, 200, 0, 300, 0, 100, 1200, 0, 200, 0, 400, 0, 100, 100, 200, 0);
        rows.Select(r => r.SourcePositionMs).Should().Equal(0, 100, 200, 400, 400, 500, 800, 800, 1000, 1000, 1300, 1300, 1400, 2600, 2600, 2800,
            2800, 3200, 3200, 3300, 3400, 3600, 3600);
        rows.Where(r => r.Selection == "repeat").Should().OnlyContain(r => r.CacheHit, "a repeated picture reuses the existing cache entry");
        perf.Dropped.Should().Be(0);
        perf.SourceSelection!.SkippedIndices.Should().Be(3);
        perf.SourceSelection.Submissions.Should().Be(perf.FramesWritten, "one record per successful submission, after it succeeded");
    }

    [Fact]
    public async Task Worker_shared_timeline_rows_label_backward_seek_pause_jump_and_wrap()
    {
        await using var s = await SourceStepScenario.StartAsync(timeline: true);
        var (_, begin) = await BeginTrialAsync(s);
        var shown = new List<int>();
        foreach (var (clock, position) in SourceStepScenario.TimelineSteps) shown.AddRange(await s.StepAsync(clock, position));
        shown.Should().Equal(SourceStepScenario.Expected(SourceStepScenario.TimelineSteps));

        var perf = (await s.Rig.Manager.GetScreenPerformanceAsync()).Single();
        var rows = TrialRows(perf, begin);
        AssertCoherent(s, rows);
        rows.Select(Kind).Should().Equal("start", "advance", "advance", "discontinuity:backward", "advance", "discontinuity:jump", "repeat",
            "advance", "repeat", "advance+wrap", "skip", "repeat", "repeat", "discontinuity:jump", "discontinuity:backward");
        perf.SourceSelection!.SkippedIndices.Should().Be(1);
        perf.Dropped.Should().Be(0);
    }

    [Fact]
    public async Task Worker_shared_timeline_after_a_whole_loop_of_host_time_reports_loop_gaps_not_repeat_or_advance()
    {
        (int, double?)[] steps = [(100, 100), (1200, 100), (1300, 200), (100, 300), (100, 400)];
        await using var s = await SourceStepScenario.StartAsync(timeline: true);
        var (_, begin) = await BeginTrialAsync(s);
        var shown = new List<int>();
        foreach (var (clock, position) in steps) shown.AddRange(await s.StepAsync(clock, position));
        shown.Should().Equal(1, 1, 1, 2, 2, 2, 3);
        shown.Should().Equal(SourceStepScenario.Expected(steps), "selection and wire bytes are unchanged");

        var perf = (await s.Rig.Manager.GetScreenPerformanceAsync()).Single();
        var rows = TrialRows(perf, begin);
        AssertCoherent(s, rows);
        rows.Select(Kind).Should().Equal("start", "advance", "discontinuity:loop-gap", "repeat", "discontinuity:loop-gap", "repeat",
            "repeat", "advance");
        rows.Select(r => r.HostIntervalMs).Should().Equal(0, 100, 1200, 0, 1300, 0, 100, 100);
        rows.Select(r => r.SourcePositionMs).Should().Equal(0, 100, 100, 100, 200, 200, 300, 400);
        perf.SourceSelection!.SkippedIndices.Should().Be(0);
        perf.SourceSelection.Discontinuities.Should().Be(2);
        perf.Dropped.Should().Be(0);
    }

    [MediaFoundationFact]
    public async Task Native_trial_rows_are_the_same_pictures_as_the_native_facts()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        rig.Opener.Configure = c => c.Reply = packet =>
        {
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
            var reply = new byte[512]; reply[0] = header[0]; reply[1] = 200; header.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
        };
        var asset = await rig.Manager.UploadScreenAssetAsync(new("native rows", Convert.ToBase64String(Images.Gif(2, 2,
            GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10), GifFrame.Fill(0, 0, 2, 2, 2, delayCs: 10)))));
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "compact", Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Turzx.Id)).State == ScreenStates.Running, "initial image path");
        var ordinary = (await rig.Manager.GetScreenPerformanceAsync()).Single();
        ordinary.SourceFrames.Should().BeNull("outside a trial only the light counters are reported");
        ordinary.SourceSelection.Should().NotBeNull();
        var trial = await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id, 60000, 30));
        ScreenPerformanceDto? matched = null;
        await Wait.UntilAsync(async () =>
        {
            var p = (await rig.Manager.GetScreenPerformanceAsync()).Single();
            if (p.NativeVideo is not { AccessUnits: >= 6 } native || p.SourceFrames is not { Count: > 0 } rows) return false;
            var session = rows.Where(r => r.Generation == rows[^1].Generation).ToArray();
            if (native.AccessUnits != session.Length) return false; // another picture completed between the two reads
            matched = p;
            return true;
        }, "a snapshot whose native facts and rows describe the same submissions");
        var last = matched!.SourceFrames![^1];
        (last.SourceIndex, last.SourcePositionMs).Should().Be((matched.NativeVideo!.SourceFrameIndex, matched.NativeVideo.SourcePositionMs));
        matched.TrialId.Should().Be(trial.TrialId);
        matched.SourceFrames.Should().OnlyContain(r => r.SourceCount == 2 && r.SourceLoopMs == 200 && r.ImageBytes > 0 && r.QueueWaitMs >= 0);
        matched.SourceFrames.Zip(matched.SourceFrames.Skip(1)).Should().OnlyContain(p => p.Second.Sequence == p.First.Sequence + 1);
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single() is { NativeVideo: null, SourceFrames: null }, "ordinary path again");
    }

    /// <summary>
    /// Begins a 10 FPS rate trial (the scenario's own rate) and returns the trial-begin pictures at the unchanged instant: one (the
    /// command resets pacing), or two if the worker happened to observe the trial before the command that follows it.
    /// </summary>
    private static async Task<(string Trial, int Pictures)> BeginTrialAsync(SourceStepScenario s)
    {
        var before = s.Written;
        var trial = await s.Rig.Manager.BeginScreenRateTrialAsync(new([Devices.Tl1.Id], 1000 / SourceStepScenario.PeriodMs, 120_000));
        await Wait.UntilAsync(() => s.Written > before, "the trial's first picture at the frozen instant");
        await SourceStepScenario.SettleAsync();
        var pictures = s.Written - before;
        pictures.Should().BeInRange(1, 2, "only a pacing reset admits a picture while the clock is frozen");
        return (trial.TrialId, pictures);
    }

    /// <summary>The trial rows of the final continuity generation (the last trial-begin picture onwards).</summary>
    private static ScreenSourceFrameDto[] TrialRows(ScreenPerformanceDto perf, int beginPictures)
    {
        var rows = perf.SourceFrames!.Skip(beginPictures - 1).ToArray();
        perf.SourceFrames!.Take(beginPictures - 1).Where(r => r.Selection != "start" || r.SourceIndex != 0).Should().BeEmpty();
        return rows;
    }

    /// <summary>Row i is wire picture i of the trial: same index, the index its own position selects, contiguous sequence, one generation.</summary>
    private static void AssertCoherent(SourceStepScenario s, ScreenSourceFrameDto[] rows)
    {
        var wire = s.Wire();
        rows.Select(r => r.SourceIndex).Should().Equal(wire[^rows.Length..]);
        rows.Should().OnlyContain(r => r.SourceIndex == SourceStepScenario.FrameAt(r.SourcePositionMs));
        rows.Select(r => r.Sequence).Should().Equal(Enumerable.Range((int)rows[0].Sequence, rows.Length).Select(v => (long)v));
        rows.Select(r => r.Generation).Distinct().Should().ContainSingle();
        rows.Should().OnlyContain(r => r.SourceCount == 8 && r.SourceLoopMs == SourceStepScenario.LoopMs && r.ImageBytes == 15
            && r.SourceFrameMs == SourceStepScenario.DelaysMs[r.SourceIndex]);
    }
}

/// <summary>The ScreenPerformanceDto shape before source diagnostics, read with its own source-generated context (an old client).</summary>
internal sealed record LegacyScreenPerformanceDto(string ScreenId, string State, long FramesWritten, long Dropped, double EffectiveFps,
    int TargetFps, string? TrialId, int? DeviceSpeedCode, int Samples, long SkippedScheduleSlots,
    IReadOnlyDictionary<string, ScreenTimingDto> Stages, long ImageBytes, long PacketBytes, long RawReads, long ReadTimeouts, long ZeroLengthReplies,
    long MatchedReplies = 0, long CorrelatedTransactions = 0, NativeVideoPerformanceDto? NativeVideo = null);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(LegacyScreenPerformanceDto))]
internal sealed partial class LegacyPerformanceJson : JsonSerializerContext;
