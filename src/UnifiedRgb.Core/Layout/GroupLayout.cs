using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Layout;

/// <summary>
/// Concatenates member zones along u in user order (ARCHITECTURE §5). Every LED of the group gets an evenly
/// spaced global u = (k + 0.5) / N, so a spatial effect flows continuously fans → GPU → DRAM. θ/X/Y keep the
/// member's local geometry.
/// </summary>
public static class GroupLayout
{
    public static Sample[] Concat(IReadOnlyList<ZoneLayout> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var total = 0;
        foreach (var z in zones)
        {
            total += z.LedCount;
        }

        var result = new Sample[total];
        var k = 0;
        foreach (var z in zones)
        {
            var local = Projector.Project(z);
            foreach (var s in local)
            {
                result[k] = s with { U = (k + 0.5f) / total };
                k++;
            }
        }

        return result;
    }

    /// <summary>
    /// <see cref="Concat(IReadOnlyList{ZoneLayout})"/> with optional per-zone placements (NATIVE design §7.6): U stays the
    /// travel coordinate of the concatenation; a placed zone's X/Y come from <see cref="Projector.Place"/>. With no placement
    /// at all the result is identical to the plain overload.
    /// </summary>
    public static Sample[] Concat(IReadOnlyList<ZoneLayout> zones, IReadOnlyList<ZonePlacement?> placements)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count != zones.Count)
        {
            throw new ArgumentException("One placement (or null) per zone.", nameof(placements));
        }

        var result = Concat(zones);
        var k = 0;
        for (var z = 0; z < zones.Count; z++)
        {
            var n = zones[z].LedCount;
            if (placements[z] is { } placement)
            {
                Projector.Place(zones[z], placement, result.AsSpan(k, n));
            }

            k += n;
        }

        return result;
    }

    /// <summary>
    /// SPATIAL-PATH: like <see cref="Concat(IReadOnlyList{ZoneLayout}, IReadOnlyList{ZonePlacement?})"/> (same member order and
    /// LED offsets, X/Y/θ/placement untouched) but each zone's U is its normal local U mapped onto its stage,
    /// <c>(start + u_local · length) / total</c>, reversed within the stage when bound so. A Point sits at its stage centre and
    /// every Matrix row shares the same local U. Missing members simply are not listed: the others keep their ranges.
    /// </summary>
    public static Sample[] ConcatPath(IReadOnlyList<ZoneLayout> zones, IReadOnlyList<ZonePlacement?> placements, IReadOnlyList<Engine.SpatialSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count != zones.Count)
        {
            throw new ArgumentException("One span per zone.", nameof(spans));
        }

        var result = Concat(zones, placements);
        var k = 0;
        for (var z = 0; z < zones.Count; z++)
        {
            var local = Projector.Project(zones[z]);
            for (var i = 0; i < local.Length; i++)
            {
                result[k + i] = result[k + i] with { U = spans[z].GlobalU(local[i].U) };
            }

            k += local.Length;
        }

        return result;
    }
}
