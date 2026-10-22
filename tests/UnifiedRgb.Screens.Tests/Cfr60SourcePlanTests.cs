using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Runtime;
using Kind = UnifiedRgb.Screens.Runtime.Cfr60SelectionKind;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The UNWIRED CFR60 host plan (S1a part A): explicit eligibility, exact whole-loop enumeration at every integer phase and several
/// host clock frequencies, the floored-wall-time FrameAt counterexample, exact drift holds/skips, one hard budget for pending and
/// issued future pictures, and late/seek/clock/unknown/cancel outcomes as explicit faults. Pure arithmetic: no timers, threads or I/O.
/// </summary>
public sealed class Cfr60SourcePlanTests
{
    private const long Khz60 = 60_000; // 1000 ticks per slot, 60 ticks per millisecond
    private static readonly Cfr60Limits Wide = new(Cfr60Limits.HardMaxPictures, Cfr60Limits.HardMaxLeadMicroseconds, Cfr60Limits.HardMaxBytes);

    // ---- eligibility ----

    [Fact]
    public void Eligibility_names_the_first_failing_rule_for_every_non_cfr60_source()
    {
        Reason(null).Should().Be(Cfr60Eligibility.NoAsset);
        Reason(Asset(Cycle(960, 17, 16, 17), SegmentedVideo.Format)).Should().Be(Cfr60Eligibility.Sequence);
        Reason(Asset(Cycle(960, 17, 16, 17)) with { Sequence = new(4, "s", "s", 64, 64, []) }).Should().Be(Cfr60Eligibility.Sequence);
        Reason(Asset([100], "png")).Should().Be(Cfr60Eligibility.Still);
        Reason(Asset([])).Should().Be(Cfr60Eligibility.Still);
        Reason(Asset([17])).Should().Be(Cfr60Eligibility.Still);
        Reason(Asset(Cycle(960, 17, 16, 17), "gif")).Should().Be(Cfr60Eligibility.NotMp4);
        Reason(Asset(Cycle(960, 17, 16, 17), "jpeg")).Should().Be(Cfr60Eligibility.NotMp4);
        Reason(Asset(Cycle(Mp4Importer.MaxFrames + 1, 17, 16, 17))).Should().Be(Cfr60Eligibility.TooManyFrames);
        Reason(Asset(Cycle(480, 33, 34, 33))).Should().Be(Cfr60Eligibility.Cfr30, "30 FPS is reported, never doubled or held");
        var vfr = Cycle(960, 17, 16, 17);
        vfr[500] = 20;
        Reason(Asset(vfr)).Should().Be(Cfr60Eligibility.VariableRate);
        Reason(Asset(Cycle(960, 16, 33))).Should().Be(Cfr60Eligibility.VariableRate);
        Reason(Asset(Grid(119, 1983))).Should().Be(Cfr60Eligibility.TooFewFrames);
        Reason(Asset(Grid(120, 2000))).Should().Be(Cfr60Eligibility.Eligible);
        Reason(Asset(Cycle(600, 17))).Should().Be(Cfr60Eligibility.MeanOutOfRange, "17 ms is not 1000/60 ± 0.01 ms");
        Reason(Asset(Cycle(960, 16, 17, 17))).Should().Be(Cfr60Eligibility.CumulativeDeviation, "S_1 = 16 is 2/3 ms before 50/3");
        Reason(Asset([.. Enumerable.Repeat(17, 80), .. Enumerable.Repeat(16, 40)])).Should().Be(Cfr60Eligibility.CumulativeDeviation,
            "the exact mean of a bunched source does not make it a 60 Hz grid");
    }

    [Theory]
    [InlineData(10_006, true)]
    [InlineData(10_007, false)]
    [InlineData(9_994, true)]
    [InlineData(9_993, false)]
    public void The_mean_rule_is_exact_at_its_edges(int sum, bool eligible)
    {
        // 600 frames: |sum/600 − 1000/60| <= 0.01 ms  <=>  |sum − 10000| <= 6
        Reason(Asset(Grid(600, sum))).Should().Be(eligible ? Cfr60Eligibility.Eligible : Cfr60Eligibility.MeanOutOfRange);
    }

    [Fact]
    public void The_cumulative_rule_is_exact_at_half_a_millisecond_and_reports_the_deviation()
    {
        var edge = Cfr60Source.TryCreate(Asset(Grid(120, 2001)), out var reason)!;
        reason.Should().Be(Cfr60Eligibility.Eligible);
        edge.MaxDeviationMs.Should().Be(0.5, "S_20, S_60 and S_100 round k·2001/120 at exactly .5");
        Source(Cycle(960, 17, 16, 17)).MaxDeviationMs.Should().BeApproximately(1 / 3.0, 1e-12);
        Source(Grid(960, 16_000)).MaxDeviationMs.Should().BeLessThanOrEqualTo(0.5);
    }

    [Fact]
    public void The_source_copies_its_authored_starts_once()
    {
        var delays = Cycle(960, 17, 16, 17).ToList();
        var source = Cfr60Source.TryCreate(new StoredAsset("a", "a", "mp4", 64, 64, delays, 1), out _)!;
        delays[0] = 400;
        delays.RemoveAt(5);
        delays.Add(17);
        source.AssetId.Should().Be("a");
        source.FrameCount.Should().Be(960);
        source.LoopMs.Should().Be(16_000);
        Enumerable.Range(0, 961).Select(source.StartMs).Should().Equal(Starts(Cycle(960, 17, 16, 17)));
    }

    // ---- enumeration ----

    [Theory]
    [InlineData(60_000L)]
    [InlineData(10_000_000L)]
    [InlineData(3_579_545L)]
    public void Every_integer_phase_enumerates_authored_frames_in_exact_order_across_whole_loops(long frequency)
    {
        var delays = Cycle(960, 17, 16, 17);
        var starts = Starts(delays);
        var source = Source(delays);
        const int slots = (2 * 960) + 7;
        const long h0 = 987_654_321;
        var phases = Enumerable.Range(0, 50).Select(p => (long)p).Concat([7_993, 15_995, 15_999, (3 * 16_000) + 15_995]).ToArray();
        var failures = new List<string>();
        foreach (var x0 in phases)
        {
            var plan = new Cfr60SourcePlan(source, Wide, frequency);
            var picked = Drive(plan, h0, x0, frequency, slots, wrapPosition: x0 % 2 == 1);
            var x = x0 % 16_000;
            var u0 = ((3 * x) + 25) / 50; // the nearest mean-grid start; here sum/N is exactly 50/3
            for (var s = 0; s < slots && failures.Count < 10; s++)
            {
                var p = picked[s];
                long u = u0 + s, loop = u / 960;
                var index = (int)(u % 960);
                var grid = (double)((3 * x) + (50L * s) - (50 * u)) / 3;
                var authored = (double)((3 * x) + (50L * s) - (3 * ((loop * 16_000) + starts[index]))) / 3;
                var ticks = h0 + (long)((((Int128)s * frequency) + 59) / 60);
                var expected = new Cfr60Selection(1, s, u, index, loop, s == 0 ? Kind.Start : Kind.Advance, 0, ticks, grid, authored);
                if (p != expected) failures.Add(FormattableString.Invariant($"x0={x0} slot={s}: {p} != {expected}"));
            }

            (picked[^1].Loop - picked[0].Loop).Should().BeGreaterThanOrEqualTo(2, "two whole loop boundaries are crossed");
            Math.Abs(picked[0].GridResidualMs).Should().BeLessThanOrEqualTo(25.0 / 3);
            var c = plan.Counters();
            (c.Committed, c.Issued, c.Advances, c.Holds, c.Skips, c.Late, c.ClockJumps, c.Unknown, c.Stale).Should()
                .Be(((long)slots, (long)slots, slots - 1L, 0L, 0L, 0L, 0L, 0L, 0L));
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void A_near_loop_end_anchor_starts_at_the_next_loops_first_frame()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, (5 * 16_000) + 15_999);
        var start = plan.TryOffer(0, 15_999).Selection;
        (start.Kind, start.Unwrapped, start.Loop, start.Index, start.GridResidualMs).Should().Be((Kind.Start, 960L, 1L, 0, -1.0));
        plan.Anchor(0, 8);
        plan.TryOffer(0, 8).Selection.GridResidualMs.Should().Be(8, "8 ms is within half a slot of frame 0");
        plan.Anchor(0, 9);
        plan.TryOffer(0, 9).Selection.Index.Should().Be(1, "9 ms is nearer frame 1 (16.67 ms)");
    }

    [Fact]
    public void FrameAt_on_floored_wall_times_repeats_and_skips_in_pairs_at_integer_phases_where_the_plan_never_does()
    {
        var delays = Cycle(960, 17, 16, 17);
        var asset = Asset(delays);
        var source = Source(delays);
        var pairs = new int[50];
        var repeats = new int[50];
        for (var x0 = 0; x0 < 50; x0++)
        {
            var shown = new int[1921];
            for (var s = 0; s <= 1920; s++) shown[s] = asset.FrameAt(((3 * x0) + (50 * s)) / 3); // today's sampling of floored times
            for (var s = 1; s <= 1920; s++)
            {
                if (shown[s] != shown[s - 1]) continue;
                repeats[x0]++;
                for (var k = 1; k <= 2 && s + k <= 1920; k++)
                {
                    if ((shown[s + k] - shown[s + k - 1] + 960) % 960 == 2) { pairs[x0]++; break; }
                }
            }

            var plan = new Cfr60SourcePlan(source, Wide, Khz60);
            var picked = Drive(plan, 0, x0, Khz60, 1921);
            picked.Zip(picked.Skip(1)).Should().OnlyContain(p => p.Second.Unwrapped == p.First.Unwrapped + 1);
            (plan.Counters().Holds, plan.Counters().Skips).Should().Be((0L, 0L));
        }

        pairs.Select((count, phase) => (count, phase)).Where(p => p.count > 0).Should().Equal((640, 0), (640, 33));
        repeats.Sum().Should().Be(2 * 640, "every FrameAt repeat at these phases is half of a repeat/skip pair (320 per loop)");
    }

    // ---- drift ----

    [Theory]
    [InlineData(16_009, true)]
    [InlineData(15_991, false)]
    public void A_drifting_mean_gets_one_explicit_counted_correction_exactly_where_the_exact_residual_passes_half_a_slot(int sum, bool hold)
    {
        var kind = hold ? Kind.Hold : Kind.Skip;
        var source = Source(Grid(960, sum));
        foreach (var x0 in new long[] { 0, 1, 8, 9, 25, 49 })
        {
            var plan = new Cfr60SourcePlan(source, Wide, Khz60);
            var picked = Drive(plan, 0, x0, Khz60, 6000);
            var events = picked.Where(p => p.Kind is Kind.Hold or Kind.Skip).Select(p => (p.Slot, p.Kind)).ToArray();
            events.Should().Equal(Reference(960, sum, x0, 6000));
            events.Should().OnlyContain(e => e.Kind == kind);
            events.Zip(events.Skip(1)).Should().OnlyContain(e => e.Second.Slot - e.First.Slot > 1700, "eligibility bounds the drift to 0.01 ms/slot");
            var bound = Math.Max(25.0 / 3, (sum / 960.0) - (25.0 / 3)); // a period longer than P cannot fit inside ±P/2
            picked.Should().OnlyContain(p => Math.Abs(p.GridResidualMs) <= bound + 1e-9);
            picked.Should().NotContain(p => Math.Abs(p.GridResidualMs) > (25.0 / 3) + 1e-9 && !(hold && (p.Kind == Kind.Hold || p.Kind == Kind.Start)),
                "only a slower source, at its start or on a hold slot, exceeds P/2 (by less than T − P <= 0.01 ms)");
            picked.Should().OnlyContain(p => Math.Abs(p.GridResidualMs - p.AuthoredResidualMs) <= 0.5 + 1e-9, "eligibility: |S_k − k·T| <= 0.5 ms");
            Enumerable.Range(1, picked.Count - 1).Count(s => picked[s].Unwrapped - picked[s - 1].Unwrapped
                != picked[s].Kind switch { Kind.Hold => 0, Kind.Skip => 2, _ => 1 }).Should().Be(0, "a hold repeats and a skip passes one index");

            var c = plan.Counters();
            (c.Holds + c.Skips).Should().Be(events.Length);
            c.SkippedIndices.Should().Be(c.Skips);
            if (x0 == 0) events[0].Slot.Should().Be(889);
        }
    }

    // ---- budget ----

    [Fact]
    public void Pending_and_issued_future_pictures_share_one_hard_count_and_a_batch_is_never_filled_beyond_it()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, 0);
        for (var i = 0; i < 6; i++) IssueNext(plan, 0, 0, 100); // slot 0's time is now: it leaves the budget at the next offer
        plan.TryOffer(0, 0).Selection.Slot.Should().Be(6);
        plan.Counters().IssuedFuture.Should().Be(5);

        var batch = new List<Cfr60Selection>();
        Cfr60OfferResult offer;
        while ((offer = plan.TryOffer(0, 0)).Outcome == Cfr60Offer.Ready && batch.Count < 6)
        {
            plan.Commit(offer.Selection, 100).Should().Be(Cfr60Commit.Committed);
            batch.Add(offer.Selection);
        }

        batch.Select(s => s.Slot).Should().Equal(6, 7, 8);
        (offer.Outcome, offer.RetryAtTicks).Should().Be((Cfr60Offer.PicturesFull, (long?)1000), "slot 1's time frees the next picture");
        (plan.Counters().Pending, plan.Counters().IssuedFuture, plan.Capacity).Should().Be((3, 5, 0));
        plan.TryOffer(999, 16).Outcome.Should().Be(Cfr60Offer.PicturesFull);
        plan.TryOffer(1000, 16).Selection.Slot.Should().Be(9);
    }

    [Fact]
    public void The_lead_window_limits_selections_and_names_the_exact_tick_it_opens()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide with { MaxLeadMicroseconds = 50_000 }, Khz60);
        plan.Anchor(0, 0);
        for (var i = 0; i < 4; i++) IssueNext(plan, 0, 0, 100); // slots 0..3: slot 3 is exactly 50 ms ahead
        var limited = plan.TryOffer(0, 0);
        (limited.Outcome, limited.RetryAtTicks).Should().Be((Cfr60Offer.LeadLimited, (long?)1000));
        plan.TryOffer(999, 16).Outcome.Should().Be(Cfr60Offer.LeadLimited);
        plan.TryOffer(1000, 16).Selection.Slot.Should().Be(4);

        var hard = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide with { MaxPictures = 8 }, Khz60);
        hard.Anchor(0, 0);
        var slots = new List<long>();
        for (var r = hard.TryOffer(0, 0); r.Outcome == Cfr60Offer.Ready; r = hard.TryOffer(0, 0))
        {
            hard.Commit(r.Selection, 1).Should().Be(Cfr60Commit.Committed);
            slots.Add(r.Selection.Slot);
        }

        slots.Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
        hard.TryOffer(0, 0).Should().Be(new Cfr60OfferResult(Cfr60Offer.PicturesFull, default, null), "nothing submitted: no retry time is invented");
    }

    [Fact]
    public void Bytes_of_pending_and_issued_future_pictures_share_one_hard_budget()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide with { MaxBytes = 1000 }, Khz60);
        plan.Anchor(0, 0);
        IssueNext(plan, 0, 0, 1); // slot 0 is due now: it leaves the budget at the next offer
        var a = plan.TryOffer(0, 0).Selection;
        plan.Commit(a, 600).Should().Be(Cfr60Commit.Committed);
        var b = plan.TryOffer(0, 0).Selection;
        plan.Commit(b, 401).Should().Be(Cfr60Commit.InsufficientBytes);
        plan.Commit(b, 1001).Should().Be(Cfr60Commit.TooLarge);
        plan.Commit(b, 400).Should().Be(Cfr60Commit.Committed);
        plan.Submit(a, 0, acknowledged: true).Should().Be(Cfr60Submit.Issued);
        plan.Counters().FutureBytes.Should().Be(1000, "a submitted picture keeps its bytes until its slot time passes");
        var c = plan.TryOffer(0, 0).Selection;
        plan.Commit(c, 1).Should().Be(Cfr60Commit.InsufficientBytes);
        plan.TryOffer(1000, 16).Selection.Should().Be(c);
        plan.Commit(c, 600).Should().Be(Cfr60Commit.Committed);
        var counters = plan.Counters();
        (counters.FutureBytes, counters.InsufficientBytes, counters.TooLarge, counters.Committed).Should().Be((1000L, 2L, 1L, 4L));
        FluentActions.Invoking(() => plan.Commit(c, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Hard_limits_and_the_clock_frequency_are_validated()
    {
        var source = Source(Cycle(960, 17, 16, 17));
        foreach (var limits in new[] { Wide with { MaxPictures = 0 }, Wide with { MaxPictures = 9 }, Wide with { MaxLeadMicroseconds = 0 },
                     Wide with { MaxLeadMicroseconds = 133_335 }, Wide with { MaxBytes = 0 }, Wide with { MaxBytes = Cfr60Limits.HardMaxBytes + 1 } })
        {
            FluentActions.Invoking(() => new Cfr60SourcePlan(source, limits, Khz60)).Should().Throw<ArgumentOutOfRangeException>();
        }

        FluentActions.Invoking(() => new Cfr60SourcePlan(source, Wide, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new Cfr60SourcePlan(source, Wide, Khz60).Anchor(0, -1)).Should().Throw<ArgumentOutOfRangeException>();
        (Cfr60Limits.HardMaxPictures, Cfr60Limits.HardMaxLeadMicroseconds).Should().Be((8, 133_334L));
    }

    // ---- late, seek, clock and unknown outcomes ----

    [Fact]
    public void A_pending_selection_never_leaves_the_budget_and_faults_late_instead_of_resampling()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        var generation = plan.Anchor(0, 0);
        var first = Commit(plan, 0, 0);
        Commit(plan, 0, 0);
        Commit(plan, 0, 0);
        plan.TryOffer(500, 8).Outcome.Should().Be(Cfr60Offer.Ready, "exactly half a slot after slot 0 is not late");
        plan.TryOffer(501, 8).Outcome.Should().Be(Cfr60Offer.Late);
        plan.TryOffer(5000, 83).Should().Be(new Cfr60OfferResult(Cfr60Offer.Late, default, null), "a fault stays until an explicit anchor");
        plan.Submit(first, 501, acknowledged: true).Should().Be(Cfr60Submit.Stale);
        var c = plan.Counters();
        (c.State, c.Generation, c.Late, c.DiscardedPending, c.Pending, c.Issued).Should().Be((Cfr60PlanState.Late, generation + 1, 1L, 3L, 0, 0L));

        plan.Anchor(6000, 100);
        plan.TryOffer(6000, 100).Outcome.Should().Be(Cfr60Offer.Ready);
        var late = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        late.Anchor(0, 0);
        late.TryOffer(501, 8).Outcome.Should().Be(Cfr60Offer.Late, "slot 0 was never offered in time");
    }

    [Fact]
    public void A_submission_more_than_half_a_slot_after_its_slot_time_is_recorded_and_faults_late()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, 0);
        var s0 = Commit(plan, 0, 0);
        plan.Submit(s0, 501, acknowledged: true).Should().Be(Cfr60Submit.Late);
        (plan.State, plan.Counters().Issued, plan.Counters().Late).Should().Be((Cfr60PlanState.Late, 1L, 1L));
    }

    [Theory]
    [InlineData(1015, true)] // exactly +P/2 at 400 ticks (6.67 ms after a 1000 ms anchor)
    [InlineData(1016, false)]
    [InlineData(999, true)]
    [InlineData(998, false)]
    [InlineData(1007 + 16_000, true)] // a whole-loop seek is indistinguishable modulo the loop (reported limitation)
    [InlineData(1007 + 16_000 - 9, false)]
    public void A_source_position_more_than_half_a_slot_from_the_plan_faults_as_a_seek(int position, bool agrees)
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, 1000);
        plan.TryOffer(400, position).Outcome.Should().Be(agrees ? Cfr60Offer.Ready : Cfr60Offer.ClockJump);
        plan.State.Should().Be(agrees ? Cfr60PlanState.Active : Cfr60PlanState.ClockJump);
    }

    [Fact]
    public void Host_time_going_backwards_faults_offers_and_submissions()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(1000, 0);
        plan.TryOffer(1000, 0).Outcome.Should().Be(Cfr60Offer.Ready);
        plan.TryOffer(999, 0).Outcome.Should().Be(Cfr60Offer.ClockJump);
        plan.Anchor(0, 0);
        var s = Commit(plan, 400, 6);
        plan.Submit(s, 300, acknowledged: true).Should().Be(Cfr60Submit.ClockJump);
        (plan.State, plan.Counters().ClockJumps, plan.Counters().Issued).Should().Be((Cfr60PlanState.ClockJump, 2L, 1L));
        plan.TryOffer(5000, 0).Outcome.Should().Be(Cfr60Offer.ClockJump);
    }

    [Fact]
    public void A_shared_position_that_wraps_at_the_loop_end_is_not_a_seek()
    {
        var plan = new Cfr60SourcePlan(Source(Grid(960, 16_009)), Wide, Khz60);
        var picked = Drive(plan, 0, 15_990, Khz60, 240, wrapPosition: true);
        picked.Should().HaveCount(240);
        picked.Select(p => p.Loop).Distinct().Should().Equal(0L, 1L);
        plan.Counters().ClockJumps.Should().Be(0);
    }

    [Fact]
    public void An_unacknowledged_submission_is_unknown_kept_in_the_budget_and_faults_the_plan()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, 0);
        IssueNext(plan, 0, 0, 100);
        var a = Commit(plan, 0, 0, 500);
        var b = Commit(plan, 0, 0, 500);
        plan.Submit(a, 0, acknowledged: false).Should().Be(Cfr60Submit.Unknown);
        plan.TryOffer(0, 0).Outcome.Should().Be(Cfr60Offer.Unknown);
        plan.Submit(b, 0, acknowledged: true).Should().Be(Cfr60Submit.Stale);
        var c = plan.Counters();
        (c.State, c.Unknown, c.Issued, c.IssuedFuture, c.Pending, c.DiscardedPending, c.FutureBytes).Should()
            .Be((Cfr60PlanState.Unknown, 1L, 1L, 1, 0, 1L, 500L), "the unknown picture may be on the device: it stays in the budget");
        plan.Anchor(1000, 16);
        plan.TryOffer(1000, 16).Outcome.Should().Be(Cfr60Offer.Ready);
        plan.Counters().FutureBytes.Should().Be(0, "the unknown slot's time has passed");
    }

    // ---- generations ----

    [Fact]
    public void Cancel_and_anchor_invalidate_selections_while_issued_residue_stays_in_the_budget()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, 0).Should().Be(1);
        IssueNext(plan, 0, 0, 700);
        IssueNext(plan, 0, 0, 300);
        var pending = Commit(plan, 0, 0, 200);
        plan.Cancel();
        plan.Cancel(); // idempotent
        (plan.State, plan.Generation).Should().Be((Cfr60PlanState.Idle, 2L));
        plan.Commit(pending, 200).Should().Be(Cfr60Commit.Stale);
        plan.Submit(pending, 0, acknowledged: true).Should().Be(Cfr60Submit.Stale);
        plan.TryOffer(0, 0).Outcome.Should().Be(Cfr60Offer.Inactive);
        var c = plan.Counters();
        (c.Cancels, c.DiscardedPending, c.Pending, c.IssuedFuture, c.FutureBytes, c.Stale).Should().Be((1L, 1L, 0, 1, 300L, 2L));

        plan.Anchor(500, 8).Should().Be(3);
        var next = plan.TryOffer(500, 8);
        (next.Selection.Generation, next.Selection.Slot, next.Selection.SlotTicks, next.Selection.Kind).Should().Be((3L, 0L, 500L, Kind.Start));
        (plan.Counters().IssuedFuture, plan.Counters().FutureBytes, plan.Capacity).Should()
            .Be((1, 300L, 7), "slot 1 of the cancelled generation is still ahead of host time");
        plan.Commit(next.Selection, 1).Should().Be(Cfr60Commit.Committed);
        plan.Anchor(600, 10).Should().Be(4, "a re-anchor (seek) is a new generation");
        (plan.Counters().DiscardedPending, plan.Counters().Anchors).Should().Be((2L, 3L));
    }

    [Fact]
    public void Only_the_current_offer_commits_and_only_the_oldest_pending_selection_submits()
    {
        var plan = new Cfr60SourcePlan(Source(Cycle(960, 17, 16, 17)), Wide, Khz60);
        plan.Anchor(0, 0);
        var first = plan.TryOffer(0, 0).Selection;
        plan.Commit(first with { Index = 5 }, 10).Should().Be(Cfr60Commit.Stale);
        plan.Commit(first, 10).Should().Be(Cfr60Commit.Committed);
        plan.Commit(first, 10).Should().Be(Cfr60Commit.Stale, "a selection commits once");
        var second = plan.TryOffer(0, 0).Selection;
        plan.Commit(second, 10).Should().Be(Cfr60Commit.Committed);
        plan.Submit(second, 0, acknowledged: true).Should().Be(Cfr60Submit.Stale, "submissions keep slot order");
        plan.Submit(first, 0, acknowledged: true).Should().Be(Cfr60Submit.Issued);
        plan.Submit(second, 0, acknowledged: true).Should().Be(Cfr60Submit.Issued);
        plan.Submit(second, 0, acknowledged: true).Should().Be(Cfr60Submit.Stale);
        (plan.Counters().Stale, plan.Counters().Issued).Should().Be((4L, 2L));
    }

    [Fact]
    public void Steady_state_offers_commits_and_submissions_do_not_allocate()
    {
        var plan = new Cfr60SourcePlan(Source(Grid(960, 16_009)), Wide, 10_000_000);
        Run(plan, 0, 4000); // JIT and first-use costs
        var before = GC.GetAllocatedBytesForCurrentThread();
        var issued = Run(plan, 1_000_000_000_000, 6000); // after every earlier slot time

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        issued.Should().Be(6000);
        allocated.Should().Be(0);
    }

    // ---- helpers ----

    /// <summary>
    /// A perfect host: offers whenever the budget allows, commits and submits at once (acknowledged), otherwise waits exactly until
    /// the plan's retry time. The source position is the anchor position plus host time (floored to ms), optionally wrapped.
    /// </summary>
    private static List<Cfr60Selection> Drive(Cfr60SourcePlan plan, long anchorTicks, long anchorPositionMs, long frequency, int slots,
        bool wrapPosition = false)
    {
        var picked = new List<Cfr60Selection>(slots);
        plan.Anchor(anchorTicks, anchorPositionMs);
        var now = anchorTicks;
        for (var guard = 0; picked.Count < slots; guard++)
        {
            if (guard > (4 * slots) + 16) throw new InvalidOperationException("the plan stopped making progress");
            var position = anchorPositionMs + (long)((Int128)(now - anchorTicks) * 1000 / frequency);
            if (wrapPosition) position %= plan.Source.LoopMs;
            var offer = plan.TryOffer(now, position);
            if (offer.Outcome == Cfr60Offer.Ready)
            {
                if (plan.Commit(offer.Selection, 1000) != Cfr60Commit.Committed || plan.Submit(offer.Selection, now, acknowledged: true) != Cfr60Submit.Issued)
                    throw new InvalidOperationException(FormattableString.Invariant($"slot {offer.Selection.Slot} was refused at {now}"));
                picked.Add(offer.Selection);
            }
            else if (offer.Outcome is Cfr60Offer.LeadLimited or Cfr60Offer.PicturesFull && offer.RetryAtTicks is { } retry && retry > now)
            {
                now = retry;
            }
            else
            {
                throw new InvalidOperationException(FormattableString.Invariant($"unexpected {offer.Outcome} at {now}"));
            }
        }

        return picked;
    }

    /// <summary>The same perfect host without any allocation (for the allocation test).</summary>
    private static int Run(Cfr60SourcePlan plan, long start, int slots)
    {
        plan.Anchor(start, 0);
        var now = start;
        var issued = 0;
        while (issued < slots)
        {
            var offer = plan.TryOffer(now, (now - start) / 10_000 % plan.Source.LoopMs); // 10 MHz ticks
            if (offer.Outcome == Cfr60Offer.Ready)
            {
                if (plan.Commit(offer.Selection, 1000) != Cfr60Commit.Committed || plan.Submit(offer.Selection, now, true) != Cfr60Submit.Issued) return -1;
                issued++;
                _ = plan.Counters();
            }
            else if (offer.RetryAtTicks is { } retry && retry > now)
            {
                now = retry;
            }
            else
            {
                return -2;
            }
        }

        return issued;
    }

    private static void IssueNext(Cfr60SourcePlan plan, long now, long position, int bytes)
    {
        var selection = Commit(plan, now, position, bytes);
        plan.Submit(selection, now, acknowledged: true).Should().Be(Cfr60Submit.Issued);
    }

    private static Cfr60Selection Commit(Cfr60SourcePlan plan, long now, long position, int bytes = 100)
    {
        var offer = plan.TryOffer(now, position);
        offer.Outcome.Should().Be(Cfr60Offer.Ready);
        plan.Commit(offer.Selection, bytes).Should().Be(Cfr60Commit.Committed);
        return offer.Selection;
    }

    /// <summary>Independent restatement of the rule in exact integers: hold below −P/2, skip above +P/2 (residual in 1/(3N) ms).</summary>
    private static (long Slot, Kind Kind)[] Reference(long n, long sum, long x0, int slots)
    {
        var events = new List<(long, Kind)>();
        var u = ((3 * n * x0) + (25 * n)) / (3 * sum);
        for (long s = 1; s < slots; s++)
        {
            var residual = (3 * n * x0) + (50 * n * s) - (3 * (u + 1) * sum);
            if (residual < -25 * n) events.Add((s, Kind.Hold));
            else if (residual > 25 * n) { events.Add((s, Kind.Skip)); u += 2; }
            else u++;
        }

        return [.. events];
    }

    private static Cfr60Eligibility Reason(StoredAsset? asset)
    {
        var source = Cfr60Source.TryCreate(asset, out var reason);
        (source is null).Should().Be(reason != Cfr60Eligibility.Eligible);
        return reason;
    }

    private static Cfr60Source Source(int[] delays) => Cfr60Source.TryCreate(Asset(delays), out _) ?? throw new InvalidOperationException("not eligible");

    private static StoredAsset Asset(int[] delays, string format = "mp4") => new("a", "a", format, 64, 64, delays, 1);

    private static int[] Cycle(int frames, params int[] pattern) => [.. Enumerable.Range(0, frames).Select(i => pattern[i % pattern.Length])];

    /// <summary>Delays whose starts are k·sum/N rounded half up: within 0.5 ms of the mean grid by construction.</summary>
    private static int[] Grid(int frames, int sum)
    {
        var start = (long k) => (int)(((2 * k * sum) + frames) / (2L * frames));
        return [.. Enumerable.Range(0, frames).Select(k => start(k + 1) - start(k))];
    }

    private static int[] Starts(int[] delays)
    {
        var starts = new int[delays.Length + 1];
        for (var i = 0; i < delays.Length; i++) starts[i + 1] = starts[i] + delays[i];
        return starts;
    }
}
