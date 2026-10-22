using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Why an asset is or is not a CFR60 candidate. Rules are checked in declaration order; the first failing one is reported.</summary>
internal enum Cfr60Eligibility : byte
{
    Eligible,
    NoAsset,

    /// <summary>A schema4 segmented sequence: clip boundaries inside the loop.</summary>
    Sequence,

    /// <summary>Fewer than two frames.</summary>
    Still,

    /// <summary>An animated GIF/PNG/JPEG, not a single MP4.</summary>
    NotMp4,

    /// <summary>More delays than a single MP4 may hold (<see cref="Mp4Importer.MaxFrames"/>).</summary>
    TooManyFrames,

    /// <summary>Every delay is 33 or 34 ms: a 30 FPS source. Not held or doubled by this plan.</summary>
    Cfr30,

    /// <summary>A delay other than 16 or 17 ms (VFR or another rate).</summary>
    VariableRate,

    /// <summary>Fewer than <see cref="Cfr60Source.MinFrames"/> frames.</summary>
    TooFewFrames,

    /// <summary>The mean delay is outside 1000/60 ± 0.01 ms.</summary>
    MeanOutOfRange,

    /// <summary>Some authored start S_k is more than 0.5 ms from k·sum/N.</summary>
    CumulativeDeviation,
}

/// <summary>
/// The immutable CFR60 view of ONE single, non-segmented MP4, computed once from the asset's existing normalized delays (no asset
/// rewrite, no I/O, integer arithmetic only). Eligible when: not a sequence, at least <see cref="MinFrames"/> frames, every delay 16
/// or 17 ms, the mean delay sum/N within 1000/60 ± 0.01 ms, and every authored start S_k within 0.5 ms of k·sum/N. The authored
/// starts are copied, so nothing that later happens to the asset's list can change a plan built from this source.
/// </summary>
internal sealed class Cfr60Source
{
    public const int MinFrames = 120;

    private readonly int[] _starts; // S_0..S_N; S_N is the loop length

    private Cfr60Source(string assetId, int[] starts, long maxDeviationTimesN)
    {
        AssetId = assetId;
        _starts = starts;
        MaxDeviationMs = (double)maxDeviationTimesN / (starts.Length - 1);
    }

    public string AssetId { get; }

    public int FrameCount => _starts.Length - 1;

    public int LoopMs => _starts[^1];

    /// <summary>The largest |S_k − k·sum/N| (reported only; the eligibility rule is exact).</summary>
    public double MaxDeviationMs { get; }

    /// <summary>Authored start S_k of frame <paramref name="index"/>, 0..<see cref="FrameCount"/> (S_N = <see cref="LoopMs"/>).</summary>
    public int StartMs(int index) => _starts[index];

    public static Cfr60Source? TryCreate(StoredAsset? asset, out Cfr60Eligibility eligibility)
    {
        eligibility = Screen(asset);
        if (eligibility != Cfr60Eligibility.Eligible) return null;

        var delays = asset!.DelaysMs;
        var starts = new int[delays.Count + 1]; // the one copy; every later rule reads only this copy
        bool cfr60 = true, cfr30 = true;
        for (var i = 0; i < delays.Count; i++)
        {
            var d = delays[i];
            cfr60 &= d is 16 or 17;
            cfr30 &= d is 33 or 34;
            starts[i + 1] = unchecked(starts[i] + d); // only read when every delay is 16 or 17
        }

        long n = delays.Count, sum = starts[^1];
        long deviation = 0;
        if (!cfr60) eligibility = cfr30 ? Cfr60Eligibility.Cfr30 : Cfr60Eligibility.VariableRate;
        else if (n < MinFrames) eligibility = Cfr60Eligibility.TooFewFrames;
        else if (5 * Math.Abs((60 * sum) - (1000 * n)) > 3 * n) eligibility = Cfr60Eligibility.MeanOutOfRange; // |sum/N − 1000/60| <= 0.01
        else
        {
            for (var k = 0; k <= n; k++)
            {
                deviation = Math.Max(deviation, Math.Abs((n * starts[k]) - (k * sum)));
            }

            if (2 * deviation > n) eligibility = Cfr60Eligibility.CumulativeDeviation; // |S_k − k·sum/N| <= 0.5 for every k
        }

        return eligibility == Cfr60Eligibility.Eligible ? new(asset.Id, starts, deviation) : null;
    }

    private static Cfr60Eligibility Screen(StoredAsset? asset) => asset switch
    {
        null => Cfr60Eligibility.NoAsset,
        { Sequence: not null } or { Format: SegmentedVideo.Format } => Cfr60Eligibility.Sequence,
        { FrameCount: < 2 } => Cfr60Eligibility.Still,
        { Format: not "mp4" } => Cfr60Eligibility.NotMp4,
        { FrameCount: > Mp4Importer.MaxFrames } => Cfr60Eligibility.TooManyFrames,
        _ => Cfr60Eligibility.Eligible,
    };
}

/// <summary>
/// Hard host budget of selected future pictures: pending (committed, not yet submitted) plus submitted ones whose slot time has not
/// passed, together at most <see cref="MaxPictures"/>, at most <see cref="MaxLeadMicroseconds"/> ahead of host now, and at most
/// <see cref="MaxBytes"/> of access-unit bytes. Nothing is ever selected beyond it, for example to fill a batch.
/// </summary>
internal readonly record struct Cfr60Limits(int MaxPictures, long MaxLeadMicroseconds, long MaxBytes)
{
    public const int HardMaxPictures = 8;
    public const long HardMaxLeadMicroseconds = 133_334;
    public const long HardMaxBytes = HardMaxPictures * (long)MfH264IntraEncoder.MaxEncodedBytes;
}

internal enum Cfr60SelectionKind : byte
{
    /// <summary>The first selection of a generation (anchored at the authored frame nearest to the observed source position).</summary>
    Start,

    /// <summary>The next authored index.</summary>
    Advance,

    /// <summary>The previous index again: the next one would be more than half a slot early. One explicit, counted repeat.</summary>
    Hold,

    /// <summary>One index passed over: the next one would be more than half a slot late. One explicit, counted skip.</summary>
    Skip,
}

/// <summary>
/// One candidate host selection: slot <see cref="Slot"/> of generation <see cref="Generation"/> shows authored frame
/// <see cref="Index"/> of loop <see cref="Loop"/> (<see cref="Unwrapped"/> = Loop·N + Index). <see cref="SlotTicks"/> is the
/// slot's HOST time (rounded up to a tick). GridResidualMs: slot source position minus the frame's mean-grid start (the hold/skip
/// rule); AuthoredResidualMs: minus its authored integer-ms start. |GridResidualMs| &lt;= max(P/2, T − P/2) with T = sum/N (P = 50/3
/// ms): a source slower than 60 Hz cannot stay inside a window narrower than its own period, so right after a hold (and at a start)
/// it may exceed P/2 by T − P (&lt;= 0.01 ms by eligibility). A host plan only: no device presentation time is implied.
/// </summary>
internal readonly record struct Cfr60Selection(long Generation, long Slot, long Unwrapped, int Index, long Loop, Cfr60SelectionKind Kind,
    int SkippedIndices, long SlotTicks, double GridResidualMs, double AuthoredResidualMs);

internal enum Cfr60PlanState : byte
{
    /// <summary>Not anchored, or cancelled.</summary>
    Idle,
    Active,

    /// <summary>Faulted: a slot was (or was submitted) more than half a slot late. Needs an explicit new anchor.</summary>
    Late,

    /// <summary>Faulted: host time went backwards or the source position left the plan by more than half a slot (seek).</summary>
    ClockJump,

    /// <summary>Faulted: a submission's acceptance is unknown.</summary>
    Unknown,
}

internal enum Cfr60Offer : byte
{
    Ready,

    /// <summary>The next slot is beyond the lead window; retry at <see cref="Cfr60OfferResult.RetryAtTicks"/>.</summary>
    LeadLimited,

    /// <summary>The picture budget is full; retry when the earliest submitted slot passes, or (null) after a submission.</summary>
    PicturesFull,
    Inactive,
    Late,
    ClockJump,
    Unknown,
}

internal readonly record struct Cfr60OfferResult(Cfr60Offer Outcome, Cfr60Selection Selection, long? RetryAtTicks);

internal enum Cfr60Commit : byte
{
    Committed,

    /// <summary>Pending plus submitted future bytes would exceed the byte budget now; nothing was committed.</summary>
    InsufficientBytes,

    /// <summary>The picture alone exceeds the byte budget and can never be committed.</summary>
    TooLarge,

    /// <summary>Not the current offer of the current generation.</summary>
    Stale,
}

internal enum Cfr60Submit : byte
{
    Issued,

    /// <summary>Submitted more than half a slot after its slot time: recorded, then the plan faults (Late).</summary>
    Late,

    /// <summary>Acceptance unknown: counted as submitted for the budget, then the plan faults (Unknown).</summary>
    Unknown,

    /// <summary>Host time went backwards: recorded, then the plan faults (ClockJump).</summary>
    ClockJump,

    /// <summary>Not the oldest pending selection of the current generation; nothing changed.</summary>
    Stale,
}

internal readonly record struct Cfr60PlanCounters(long Generation, Cfr60PlanState State, long Anchors, long Cancels, long Committed,
    long Issued, long Advances, long Holds, long Skips, long SkippedIndices, long Late, long ClockJumps, long Unknown,
    long InsufficientBytes, long TooLarge, long Stale, long DiscardedPending, int Pending, int IssuedFuture, long FutureBytes);

/// <summary>
/// A candidate HOST plan for one eligible CFR60 source, UNWIRED (no production path uses it): host slot n (60 per second from the
/// anchor) selects authored frames strictly in sequence, so it can neither repeat nor skip an index by sampling. A drift between the
/// asset's mean frame period and 1000/60 ms accumulates in an exact residual and is answered by ONE explicit, counted hold or skip
/// only when it exceeds half a slot. The residual uses the asset's mean grid (k·sum/N): its authored integer-ms starts differ from
/// it by at most 0.5 ms (eligibility), and deciding on them would bring back phase-dependent repeat/skip pairs. All arithmetic is
/// integer (residuals in 1/(3N) ms, times in host ticks). Pending and submitted future pictures share one hard budget
/// (<see cref="Cfr60Limits"/>); submitted pictures stay in it until their slot time passes, across generations too. Anchor, Cancel
/// and every fault start a new generation and discard pending selections; late slots, seeks/clock jumps, unknown acceptance and an
/// exhausted budget are explicit outcomes and counters, never resampling or success. No timers, threads, I/O or allocation after
/// construction; not thread-safe (one owner). Claims nothing about device decode, presentation or acknowledgement epochs.
/// </summary>
internal sealed class Cfr60SourcePlan
{
    private const int SlotsPerSecond = 60;

    private readonly Cfr60Source _source;
    private readonly Cfr60Limits _limits;
    private readonly long _frequency;
    private readonly Entry[] _entries;
    private int _count;
    private Cfr60PlanState _state;
    private long _generation, _anchorTicks, _anchorPositionMs, _lastTicks, _nextSlot, _firstUnwrapped, _lastUnwrapped;
    private Cfr60Selection _offer;
    private bool _offered;
    private long _anchors, _cancels, _committed, _issued, _advances, _holds, _skips, _skippedIndices, _late, _clockJumps, _unknown,
        _insufficientBytes, _tooLarge, _stale, _discardedPending;

    public Cfr60SourcePlan(Cfr60Source source, Cfr60Limits limits, long frequency)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxPictures, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaxPictures, Cfr60Limits.HardMaxPictures);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxLeadMicroseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaxLeadMicroseconds, Cfr60Limits.HardMaxLeadMicroseconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaxBytes, Cfr60Limits.HardMaxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 1);
        _source = source;
        _limits = limits;
        _frequency = frequency;
        _entries = new Entry[limits.MaxPictures];
    }

    public Cfr60Source Source => _source;

    public Cfr60Limits Limits => _limits;

    public long Generation => _generation;

    public Cfr60PlanState State => _state;

    /// <summary>Pictures that still fit the count budget as of the last offer (pending and submitted future ones excluded).</summary>
    public int Capacity => _limits.MaxPictures - _count;

    /// <summary>
    /// Starts a new generation: slot 0 is at <paramref name="hostTicks"/>, where the source position is
    /// <paramref name="sourcePositionMs"/> (shared timeline or local elapsed; taken modulo the loop). Pending selections are discarded;
    /// submitted ones keep their budget until their slot time passes.
    /// </summary>
    public long Anchor(long hostTicks, long sourcePositionMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourcePositionMs);
        Invalidate(Cfr60PlanState.Active);
        _anchors++;
        _anchorTicks = hostTicks;
        _lastTicks = hostTicks;
        _anchorPositionMs = sourcePositionMs % _source.LoopMs;
        _nextSlot = 0;
        long n = _source.FrameCount;
        _firstUnwrapped = ((3 * n * _anchorPositionMs) + (25 * n)) / (3L * _source.LoopMs); // nearest mean-grid start (residual >= −P/2)
        _lastUnwrapped = _firstUnwrapped - 1;
        return _generation;
    }

    /// <summary>Invalidates every outstanding selection (new generation) and stops offering until the next anchor.</summary>
    public void Cancel()
    {
        if (_state == Cfr60PlanState.Idle) return;
        Invalidate(Cfr60PlanState.Idle);
        _cancels++;
    }

    /// <summary>
    /// The next selection if the budget allows it at <paramref name="hostTicks"/>. <paramref name="sourcePositionMs"/> is the
    /// source position read at that time; it is only compared with the plan (more than half a slot off, modulo the loop: a seek or
    /// clock jump), never used to resample.
    /// </summary>
    public Cfr60OfferResult TryOffer(long hostTicks, long sourcePositionMs)
    {
        _offered = false;
        switch (_state)
        {
            case Cfr60PlanState.Idle: return new(Cfr60Offer.Inactive, default, null);
            case Cfr60PlanState.Late: return new(Cfr60Offer.Late, default, null);
            case Cfr60PlanState.ClockJump: return new(Cfr60Offer.ClockJump, default, null);
            case Cfr60PlanState.Unknown: return new(Cfr60Offer.Unknown, default, null);
        }

        if (hostTicks < _lastTicks || !SourceAgrees(hostTicks, sourcePositionMs))
        {
            Fault(Cfr60PlanState.ClockJump);
            return new(Cfr60Offer.ClockJump, default, null);
        }

        _lastTicks = hostTicks;
        Retire(hostTicks);
        var pending = OldestPending();
        if ((pending >= 0 && IsLate(_entries[pending].Slot, hostTicks)) || IsLate(_nextSlot, hostTicks))
        {
            Fault(Cfr60PlanState.Late);
            return new(Cfr60Offer.Late, default, null);
        }

        if (_count >= _limits.MaxPictures) return new(Cfr60Offer.PicturesFull, default, EarliestSubmittedTicks());
        if (!WithinLead(_nextSlot, hostTicks)) return new(Cfr60Offer.LeadLimited, default, LeadOpensAt(_nextSlot));

        _offer = Next();
        _offered = true;
        return new(Cfr60Offer.Ready, _offer, null);
    }

    /// <summary>Commits the current offer with its access-unit size; it becomes pending.</summary>
    public Cfr60Commit Commit(in Cfr60Selection selection, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        if (_state != Cfr60PlanState.Active || !_offered || selection != _offer)
        {
            _stale++;
            return Cfr60Commit.Stale;
        }

        if (bytes > _limits.MaxBytes)
        {
            _tooLarge++;
            return Cfr60Commit.TooLarge;
        }

        if (FutureBytes() + bytes > _limits.MaxBytes)
        {
            _insufficientBytes++;
            return Cfr60Commit.InsufficientBytes;
        }

        _entries[_count++] = new(_generation, selection.Slot, selection.SlotTicks, bytes, false);
        _offered = false;
        _nextSlot++;
        _lastUnwrapped = selection.Unwrapped;
        _committed++;
        switch (selection.Kind)
        {
            case Cfr60SelectionKind.Advance: _advances++; break;
            case Cfr60SelectionKind.Hold: _holds++; break;
            case Cfr60SelectionKind.Skip: _skips++; _skippedIndices += selection.SkippedIndices; break;
        }

        return Cfr60Commit.Committed;
    }

    /// <summary>
    /// Records the oldest pending selection as submitted at <paramref name="hostTicks"/>. Not acknowledged: its acceptance is unknown;
    /// it is counted as submitted for the budget and the plan faults. A host fact only, never a presentation.
    /// </summary>
    public Cfr60Submit Submit(in Cfr60Selection selection, long hostTicks, bool acknowledged)
    {
        var i = OldestPending();
        if (_state != Cfr60PlanState.Active || i < 0 || _entries[i].Generation != selection.Generation || _entries[i].Slot != selection.Slot)
        {
            _stale++;
            return Cfr60Submit.Stale;
        }

        _entries[i] = _entries[i] with { Submitted = true };
        if (!acknowledged)
        {
            _unknown++;
            Fault(Cfr60PlanState.Unknown);
            return Cfr60Submit.Unknown;
        }

        _issued++;
        if (hostTicks < _lastTicks)
        {
            Fault(Cfr60PlanState.ClockJump);
            return Cfr60Submit.ClockJump;
        }

        _lastTicks = hostTicks;
        if (IsLate(selection.Slot, hostTicks))
        {
            Fault(Cfr60PlanState.Late);
            return Cfr60Submit.Late;
        }

        return Cfr60Submit.Issued;
    }

    public Cfr60PlanCounters Counters()
    {
        var pending = 0;
        for (var i = 0; i < _count; i++)
        {
            if (!_entries[i].Submitted) pending++;
        }

        return new(_generation, _state, _anchors, _cancels, _committed, _issued, _advances, _holds, _skips, _skippedIndices, _late,
            _clockJumps, _unknown, _insufficientBytes, _tooLarge, _stale, _discardedPending, pending, _count - pending, FutureBytes());
    }

    /// <summary>The selection for <see cref="_nextSlot"/>: the next index in sequence, or one explicit hold or skip.</summary>
    private Cfr60Selection Next()
    {
        var slot = _nextSlot;
        long n = _source.FrameCount;
        long u;
        var kind = Cfr60SelectionKind.Start;
        var skipped = 0;
        if (slot == 0)
        {
            u = _firstUnwrapped;
        }
        else
        {
            u = _lastUnwrapped + 1;
            var residual = Residual(slot, u);
            if (residual < -25 * n)
            {
                u = _lastUnwrapped;
                kind = Cfr60SelectionKind.Hold;
            }
            else if (residual > 25 * n)
            {
                // Eligibility bounds the drift to 0.01 ms per slot, so one index brings the residual back below +P/2.
                u++;
                skipped = 1;
                kind = Cfr60SelectionKind.Skip;
            }
            else
            {
                kind = Cfr60SelectionKind.Advance;
            }
        }

        var loop = u / n;
        var index = (int)(u % n);
        var authoredThirds = (3 * _anchorPositionMs) + (50 * (Int128)slot) - (3 * ((loop * _source.LoopMs) + _source.StartMs(index)));
        var slotTicks = _anchorTicks + (long)((((Int128)slot * _frequency) + SlotsPerSecond - 1) / SlotsPerSecond);
        return new(_generation, slot, u, index, loop, kind, skipped, slotTicks, (double)Residual(slot, u) / (3 * n), (double)authoredThirds / 3);
    }

    /// <summary>Slot source position minus the mean-grid start u·sum/N, in 1/(3N) ms: 3N·X0 + 50N·slot − 3u·sum.</summary>
    private Int128 Residual(long slot, long unwrapped)
    {
        Int128 n = _source.FrameCount;
        return (3 * n * _anchorPositionMs) + (50 * n * slot) - (3 * (Int128)unwrapped * _source.LoopMs);
    }

    /// <summary>The observed source position is within half a slot of the anchor's prediction, modulo the loop.</summary>
    private bool SourceAgrees(long hostTicks, long sourcePositionMs)
    {
        if (sourcePositionMs < 0) return false;
        Int128 f = _frequency;
        var loop = 3 * f * _source.LoopMs;
        var error = ((3 * f * (sourcePositionMs - _anchorPositionMs)) - (3000 * (Int128)(hostTicks - _anchorTicks))) % loop; // thirds·f
        if (error < 0) error += loop;
        if (2 * error > loop) error -= loop;
        return Int128.Abs(error) <= 25 * f;
    }

    /// <summary>More than half a slot after the slot time: 120·(now − anchor) &gt; (2·slot + 1)·f.</summary>
    private bool IsLate(long slot, long hostTicks) =>
        120 * (Int128)(hostTicks - _anchorTicks) > ((2 * (Int128)slot) + 1) * _frequency;

    /// <summary>Slot time − now &lt;= lead: 10^6·slot·f − 6·10^7·(now − anchor) &lt;= 60·lead_us·f.</summary>
    private bool WithinLead(long slot, long hostTicks) =>
        (1_000_000 * (Int128)slot * _frequency) - (60_000_000 * (Int128)(hostTicks - _anchorTicks))
            <= 60 * (Int128)_limits.MaxLeadMicroseconds * _frequency;

    private long LeadOpensAt(long slot)
    {
        var numerator = (1_000_000 * (Int128)slot * _frequency) - (60 * (Int128)_limits.MaxLeadMicroseconds * _frequency);
        var elapsed = numerator <= 0 ? 0 : (numerator + 60_000_000 - 1) / 60_000_000;
        return _anchorTicks + (long)elapsed;
    }

    private long? EarliestSubmittedTicks()
    {
        long? earliest = null;
        for (var i = 0; i < _count; i++)
        {
            if (_entries[i].Submitted && (earliest is null || _entries[i].SlotTicks < earliest)) earliest = _entries[i].SlotTicks;
        }

        return earliest;
    }

    private int OldestPending()
    {
        var oldest = -1;
        for (var i = 0; i < _count; i++)
        {
            if (!_entries[i].Submitted && _entries[i].Generation == _generation && (oldest < 0 || _entries[i].Slot < _entries[oldest].Slot)) oldest = i;
        }

        return oldest;
    }

    private long FutureBytes()
    {
        long bytes = 0;
        for (var i = 0; i < _count; i++) bytes += _entries[i].Bytes;
        return bytes;
    }

    /// <summary>Submitted selections leave the budget once their slot time has passed; pending ones never do.</summary>
    private void Retire(long hostTicks)
    {
        var kept = 0;
        for (var i = 0; i < _count; i++)
        {
            if (!_entries[i].Submitted || _entries[i].SlotTicks > hostTicks) _entries[kept++] = _entries[i];
        }

        _count = kept;
    }

    private void Fault(Cfr60PlanState state)
    {
        switch (state)
        {
            case Cfr60PlanState.Late: _late++; break;
            case Cfr60PlanState.ClockJump: _clockJumps++; break;
        }

        Invalidate(state);
    }

    /// <summary>A new generation: the offer and every pending selection are dropped; submitted ones keep their budget.</summary>
    private void Invalidate(Cfr60PlanState state)
    {
        _generation++;
        _offered = false;
        _state = state;
        var kept = 0;
        for (var i = 0; i < _count; i++)
        {
            if (_entries[i].Submitted) _entries[kept++] = _entries[i];
        }

        _discardedPending += _count - kept;
        _count = kept;
    }

    private readonly record struct Entry(long Generation, long Slot, long SlotTicks, int Bytes, bool Submitted);
}
