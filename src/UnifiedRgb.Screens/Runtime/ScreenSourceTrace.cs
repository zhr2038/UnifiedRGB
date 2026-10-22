using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// What ONE picture attempt selected, captured in that attempt and recorded only after its submission succeeded. Asset null: no
/// source asset (still/static/blocked/missing). NewGeneration: the worker broke continuity (command, trial, channel, session or a
/// failed attempt since the previous submission). FrameStart is the attempt's host timestamp in <see cref="Frequency"/> ticks.
/// </summary>
internal readonly record struct ScreenSourceSample(StoredAsset? Asset, int Index, long PositionMs, long FrameStart, long Frequency,
    bool CacheHit, bool NewGeneration);

internal enum ScreenSourceKind : byte { None, Start, Advance, Repeat, Skip, Discontinuity }

internal enum ScreenSourceGap : byte { None, Backward, Jump, LoopGap, Unknown }

/// <summary>One recorded submission; numeric only (no strings or references), so the ring never retains or allocates anything.</summary>
internal readonly record struct ScreenSourceRow(long Sequence, long Generation, ScreenSourceKind Kind, ScreenSourceGap Gap, bool LoopWrap,
    int Skipped, int Index, int Count, int FrameMs, long PositionMs, int LoopMs, bool CacheHit, double HostIntervalMs, double PacingWait,
    double Compose, double Encode, double Write, double Read, double QueueWait, double OverlayCompose, double OverlayEncode,
    double OverlayWrite, double OverlayRead, double Work, int ImageBytes);

/// <summary>
/// HOST source-selection diagnostics: classifies every successful submission against the previous one of the same continuity
/// generation, keeps cumulative counters and the latest <see cref="Capacity"/> rows in a ring allocated once. Recording allocates
/// nothing; loop metadata (frame count, loop length) is recomputed only when the asset identity changes. Not thread-safe: its owner
/// (<see cref="ScreenPerformance"/>) serializes it. Nothing here influences selection, pacing or what is sent.
/// </summary>
internal sealed class ScreenSourceTrace
{
    public const int Capacity = 256;

    /// <summary>A source advance counts as host-paced playback only if it matches host elapsed time within max(8 ms, 25 %).</summary>
    public const double ToleranceMs = 8, ToleranceFraction = 0.25;

    private readonly ScreenSourceRow[] _rows = new ScreenSourceRow[Capacity];
    private int _count, _next;
    private long _sequence, _generation, _noSource, _starts, _advances, _repeats, _skips, _skippedIndices, _discontinuities, _loopWraps;
    private bool _hasLast;
    private long _lastFrameStart;
    private ScreenSourceRow _last;
    private StoredAsset? _lastSource; // animated source of _last (null: none)
    private StoredAsset? _metaAsset;
    private int _metaCount, _metaLoopMs;

    public int Count => _count;

    public long Submissions => _sequence;

    /// <summary>Empties the row window only; counters, sequence and continuity are unaffected.</summary>
    public void Clear()
    {
        _count = 0;
        _next = 0;
    }

    public void Record(in ScreenSourceSample s, in ScreenFrameWork work)
    {
        var asset = s.Asset;
        if (!ReferenceEquals(asset, _metaAsset))
        {
            _metaAsset = asset;
            _metaCount = asset?.FrameCount ?? 0;
            _metaLoopMs = asset?.DurationMs ?? 0; // a Sum over the delays: once per asset identity, never per picture
        }

        var valid = asset is not null && (uint)s.Index < (uint)_metaCount;
        var animated = valid && _metaCount > 1 && _metaLoopMs > 0;
        var source = animated ? asset : null;
        var hostMs = _hasLast && s.Frequency > 0 ? (s.FrameStart - _lastFrameStart) * 1000.0 / s.Frequency : 0;
        var newGeneration = !_hasLast || s.NewGeneration || !SameSource(source);
        if (newGeneration) _generation++;

        var kind = ScreenSourceKind.None;
        var gap = ScreenSourceGap.None;
        var wrap = false;
        var skipped = 0;
        if (animated)
        {
            kind = newGeneration ? ScreenSourceKind.Start
                : Classify(_metaCount, _metaLoopMs, _last.Index, _last.PositionMs, s.Index, s.PositionMs, hostMs, out skipped, out wrap, out gap);
        }

        switch (kind)
        {
            case ScreenSourceKind.None: _noSource++; break;
            case ScreenSourceKind.Start: _starts++; break;
            case ScreenSourceKind.Advance: _advances++; break;
            case ScreenSourceKind.Repeat: _repeats++; break;
            case ScreenSourceKind.Skip: _skips++; _skippedIndices += skipped; break;
            default: _discontinuities++; break;
        }

        if (wrap) _loopWraps++;
        var native = work.Native;
        var row = new ScreenSourceRow(++_sequence, _generation, kind, gap, wrap, skipped, valid ? s.Index : -1, asset is null ? 0 : _metaCount,
            valid ? asset!.DelaysMs[s.Index] : 0, s.PositionMs, _metaLoopMs, s.CacheHit, hostMs, work.Wait, work.Compose, work.Encode,
            work.Write, work.Read, native.QueueSleep, work.OverlayCompose, work.OverlayEncode, native.OverlayWrite, native.OverlayRead,
            work.Work, work.ImageBytes);
        _rows[_next] = row;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        _last = row;
        _lastSource = source;
        _lastFrameStart = s.FrameStart;
        _hasLast = true;
    }

    /// <summary>
    /// The previous index/position of the same generation → this one. Positions are the exact values the indices were selected with.
    /// A forward step within one loop is exact: k index steps → advance (1), repeat (0) or skip (k-1 missing), the latter only if the
    /// source advance matches host time (otherwise a seek/clock jump: "jump"). A backward position is a continuing loop only if its
    /// forward advance inside the loop matches host time (a shared timeline wrapping at its own loop), else "backward". A shared
    /// position may be modulo the loop, so it only fixes the advance modulo whole loops: if the source advanced a whole loop or more,
    /// or host time allows a hidden whole loop (host elapsed of a loop or more, or the advance plus one loop matching host time), the
    /// indices passed are unknowable: "loop-gap". Invalid inputs claim nothing ("unknown"). Nothing is inferred from device replies.
    /// </summary>
    internal static ScreenSourceKind Classify(int count, int loopMs, int previousIndex, long previousPositionMs, int index, long positionMs,
        double hostMs, out int skipped, out bool loopWrap, out ScreenSourceGap gap)
    {
        skipped = 0;
        loopWrap = false;
        gap = ScreenSourceGap.None;
        if (count < 2 || loopMs <= 0 || (uint)index >= (uint)count || (uint)previousIndex >= (uint)count || positionMs < 0
            || previousPositionMs < 0 || !double.IsFinite(hostMs) || hostMs < 0)
        {
            gap = ScreenSourceGap.Unknown; // never produced by the worker (it clamps positions; host time is monotonic)
            return ScreenSourceKind.Discontinuity;
        }

        var delta = positionMs - previousPositionMs;
        var advance = Mod(delta, loopMs); // the forward advance inside the loop; equals delta for 0 <= delta < loop
        if (delta >= loopMs || hostMs >= loopMs || Consistent(advance + loopMs, hostMs))
        {
            gap = ScreenSourceGap.LoopGap;
            return ScreenSourceKind.Discontinuity;
        }

        var from = Mod(previousPositionMs, loopMs);
        if (delta < 0 && !Consistent(advance, hostMs))
        {
            gap = ScreenSourceGap.Backward;
            return ScreenSourceKind.Discontinuity;
        }

        var wraps = from + advance >= loopMs;
        var steps = (wraps ? count : 0) + index - previousIndex;
        if (steps < 0 || steps > count)
        {
            gap = ScreenSourceGap.Unknown; // the indices do not fit their positions: claim nothing
            return ScreenSourceKind.Discontinuity;
        }

        if (steps >= 2 && !Consistent(advance, hostMs))
        {
            gap = ScreenSourceGap.Jump;
            return ScreenSourceKind.Discontinuity;
        }

        loopWrap = wraps;
        if (steps <= 1) return steps == 0 ? ScreenSourceKind.Repeat : ScreenSourceKind.Advance;
        skipped = steps - 1;
        return ScreenSourceKind.Skip;
    }

    internal static bool Consistent(double advanceMs, double hostMs) =>
        Math.Abs(advanceMs - hostMs) <= Math.Max(ToleranceMs, hostMs * ToleranceFraction);

    public ScreenSourceSelectionDto Summary() => new(_generation, _sequence, _noSource, _starts, _advances, _repeats, _skips,
        _skippedIndices, _discontinuities, _loopWraps);

    /// <summary>A copy of the row window, oldest first (a diagnostic read; never on the picture path).</summary>
    public ScreenSourceRow[] Rows()
    {
        var rows = new ScreenSourceRow[_count];
        var first = (_next - _count + Capacity) % Capacity;
        for (var i = 0; i < rows.Length; i++) rows[i] = _rows[(first + i) % Capacity];
        return rows;
    }

    public static ScreenSourceFrameDto ToDto(ScreenSourceRow r) => new(r.Sequence, r.Generation, Name(r.Kind), Name(r.Gap), r.LoopWrap,
        r.Skipped, r.Index, r.Count, r.FrameMs, r.PositionMs, r.LoopMs, r.CacheHit, r.HostIntervalMs, r.PacingWait, r.Compose, r.Encode,
        r.Write, r.Read, r.QueueWait, r.OverlayCompose, r.OverlayEncode, r.OverlayWrite, r.OverlayRead, r.Work, r.ImageBytes);

    internal static string Name(ScreenSourceKind kind) => kind switch
    {
        ScreenSourceKind.Start => "start",
        ScreenSourceKind.Advance => "advance",
        ScreenSourceKind.Repeat => "repeat",
        ScreenSourceKind.Skip => "skip",
        ScreenSourceKind.Discontinuity => "discontinuity",
        _ => "none",
    };

    internal static string? Name(ScreenSourceGap gap) => gap switch
    {
        ScreenSourceGap.Backward => "backward",
        ScreenSourceGap.Jump => "jump",
        ScreenSourceGap.LoopGap => "loop-gap",
        ScreenSourceGap.Unknown => "unknown",
        _ => null,
    };

    private bool SameSource(StoredAsset? source) =>
        ReferenceEquals(source, _lastSource) || (source is not null && _lastSource is not null
            && string.Equals(source.Id, _lastSource.Id, StringComparison.Ordinal) && _last.Count == _metaCount && _last.LoopMs == _metaLoopMs);

    private static long Mod(long value, int loopMs)
    {
        var m = value % loopMs;
        return m < 0 ? m + loopMs : m;
    }
}
