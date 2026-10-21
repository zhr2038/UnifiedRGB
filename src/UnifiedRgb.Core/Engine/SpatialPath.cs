using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <summary>One target of a <see cref="SpatialStage"/>: the zone projects the WHOLE stage interval, optionally back to front.</summary>
/// <param name="Reverse">
/// Path-local reversal: the stage is sampled high-to-low before the zone's existing physical projection (TL fan columns and
/// ReverseChain, H2 ring fill). It never changes mounting and composes with it.
/// </param>
public sealed record SpatialBinding(ZoneRef Target, bool Reverse = false);

/// <summary>
/// One stage of a <see cref="SpatialPath"/>: <paramref name="Length"/> abstract source dots (not LEDs, ms or hardware records)
/// shared by every binding; several bindings are parallel projections of the same interval (one progress, not offsets).
/// </summary>
public sealed record SpatialStage(int Length, IReadOnlyList<SpatialBinding> Bindings);

/// <summary>Where one bound zone sits on the source axis: dots [Start, Start + Length) of <see cref="Total"/>.</summary>
public readonly record struct SpatialSpan(int Start, int Length, bool Reverse, int Total)
{
    /// <summary>Global U of a local coordinate <paramref name="localU"/> in [0, 1] (reversed within the stage when bound so).</summary>
    public float GlobalU(float localU)
    {
        var u = Reverse ? 1f - localU : localU;
        return (Start + (u * Length)) / Total;
    }
}

/// <summary>
/// SPATIAL-PATH (Codex design 2026-10-18): an optional explicit source axis for one assignment. Stages are consecutive in
/// LOW-to-HIGH source coordinate order; each binding uses its stage's whole interval. This is the source convention only:
/// an effect that retains high-to-low (shared Stacking) deposits the LAST stage first — there is no implicit flip.
/// Absent (null) path = the historical concatenation of targets in list order, byte for byte.
/// </summary>
public sealed record SpatialPath
{
    /// <summary>Longest stage and longest total path, in source dots.</summary>
    public const int MaxLength = 720;

    private readonly SpatialStage[] _stages = [];

    public SpatialPath(IReadOnlyList<SpatialStage> stages) => Stages = stages;

    /// <summary>The stages, deep-copied on assignment so a caller's lists cannot change a published scene.</summary>
    public IReadOnlyList<SpatialStage> Stages
    {
        get => _stages;
        init => _stages = Copy(value);
    }

    /// <summary>Sum of the stage lengths (checked; <see cref="Validate"/> bounds it).</summary>
    public int TotalLength => checked(_stages.Sum(s => s?.Length ?? 0));

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless: there is at least one stage and every stage has at least one binding;
    /// lengths are 1..720 and their (checked) total is ≤ 720; every one of <paramref name="targets"/> is bound exactly once and
    /// nothing else is bound; no null stage/binding/list.
    /// </summary>
    public void Validate(IReadOnlyList<ZoneRef> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (_stages.Length == 0)
        {
            throw new ArgumentException("A spatial path needs at least one stage.", nameof(targets));
        }

        var bound = new HashSet<ZoneRef>();
        var total = 0;
        for (var i = 0; i < _stages.Length; i++)
        {
            var s = _stages[i] ?? throw new ArgumentException($"Spatial stage {i} is null.", nameof(targets));
            if (s.Length is < 1 or > MaxLength)
            {
                throw new ArgumentException($"Spatial stage {i} length {s.Length} is outside 1..{MaxLength} source dots.", nameof(targets));
            }

            try
            {
                total = checked(total + s.Length);
            }
            catch (OverflowException ex)
            {
                throw new ArgumentException("Spatial path length overflows.", nameof(targets), ex);
            }

            if (total > MaxLength)
            {
                throw new ArgumentException($"Spatial path total {total} exceeds {MaxLength} source dots.", nameof(targets));
            }

            if (s.Bindings is null || s.Bindings.Count == 0)
            {
                throw new ArgumentException($"Spatial stage {i} has no target binding.", nameof(targets));
            }

            foreach (var b in s.Bindings)
            {
                if (b is null)
                {
                    throw new ArgumentException($"Spatial stage {i} has a null binding.", nameof(targets));
                }

                if (!bound.Add(b.Target))
                {
                    throw new ArgumentException($"Spatial path binds {b.Target} more than once.", nameof(targets));
                }
            }
        }

        var listed = new HashSet<ZoneRef>();
        foreach (var t in targets)
        {
            if (!listed.Add(t))
            {
                throw new ArgumentException($"Target {t} is listed more than once in an assignment with a spatial path.", nameof(targets));
            }

            if (!bound.Contains(t))
            {
                throw new ArgumentException($"Spatial path does not bind target {t}.", nameof(targets));
            }
        }

        foreach (var b in bound.Where(b => !listed.Contains(b)))
        {
            throw new ArgumentException($"Spatial path binds {b}, which is not a target of the assignment.", nameof(targets));
        }
    }

    /// <summary>The span of every bound zone (call after <see cref="Validate"/>). Missing devices never move other ranges.</summary>
    public IReadOnlyDictionary<ZoneRef, SpatialSpan> Resolve()
    {
        var total = TotalLength;
        var map = new Dictionary<ZoneRef, SpatialSpan>();
        var start = 0;
        foreach (var s in _stages)
        {
            foreach (var b in s.Bindings)
            {
                map[b.Target] = new SpatialSpan(start, s.Length, b.Reverse, total);
            }

            start += s.Length;
        }

        return map;
    }

    /// <summary>
    /// The path without bindings of <paramref name="removed"/> (zones taken over by another assignment). A stage left with no
    /// binding is dropped, so the remaining stages close up; null when nothing is left.
    /// </summary>
    public SpatialPath? Without(IReadOnlySet<ZoneRef> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var stages = _stages
            .Select(s => s with { Bindings = s.Bindings.Where(b => !removed.Contains(b.Target)).ToArray() })
            .Where(s => s.Bindings.Count > 0)
            .ToArray();
        return stages.Length == 0 ? null : new SpatialPath(stages);
    }

    /// <summary>"12:[gpu] | 24:[h2 ‖ ram ‖ ram ‖ rog] | 36:[top~]" (~ = reversed), low source end first.</summary>
    public string Summary(Func<ZoneRef, string>? name = null) =>
        string.Join(" | ", _stages.Select(s => $"{s.Length}:[{string.Join(" ‖ ", s.Bindings.Select(b => (name?.Invoke(b.Target) ?? b.Target.ToString()) + (b.Reverse ? "~" : string.Empty)))}]"));

    public bool Equals(SpatialPath? other) =>
        other is not null && _stages.Length == other._stages.Length
        && _stages.Zip(other._stages).All(p => p.First is not null && p.Second is not null && p.First.Length == p.Second.Length
            && p.First.Bindings is not null && p.Second.Bindings is not null && p.First.Bindings.SequenceEqual(p.Second.Bindings));

    public override int GetHashCode() => HashCode.Combine(_stages.Length, TotalLength);

    private static SpatialStage[] Copy(IReadOnlyList<SpatialStage>? stages) =>
        stages is null
            ? throw new ArgumentNullException(nameof(stages))
            : stages.Select(s => s is null ? null! : new SpatialStage(s.Length, s.Bindings is null ? null! : s.Bindings.ToArray())).ToArray();
}
