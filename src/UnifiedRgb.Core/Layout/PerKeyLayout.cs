using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Layout;

/// <summary>
/// PER-KEY: samples for an assignment whose members include a zone with sparse per-key points (<see cref="ZoneDescriptor.PerKeyPoints"/>).
/// Every other member, and the LED offsets of all members, are exactly the ordinary concatenation (plain, placed or SPATIAL-PATH).
/// A per-key member keeps its keys' X/Y/θ in map order and maps each key's local U (its column position) into the U range its
/// LEDs would have had, so a spatial effect crosses the keyboard by physical column, not by LED record order. A single per-key
/// zone without a path is exactly <see cref="Projector.Project(ZoneDescriptor)"/>.
/// </summary>
public static class PerKeyLayout
{
    public static bool Any(IReadOnlyList<IReadOnlyList<ZonePoint>?> perKeyPoints)
    {
        ArgumentNullException.ThrowIfNull(perKeyPoints);
        return perKeyPoints.Any(p => p is not null);
    }

    public static Sample[] Concat(IReadOnlyList<ZoneLayout> zones, IReadOnlyList<ZonePlacement?> placements,
        IReadOnlyList<IReadOnlyList<ZonePoint>?> perKeyPoints, IReadOnlyList<Engine.SpatialSpan>? spans)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(perKeyPoints);
        if (placements.Count != zones.Count || perKeyPoints.Count != zones.Count)
        {
            throw new ArgumentException("One placement and one per-key point list (or null) per zone.", nameof(perKeyPoints));
        }

        var result = spans is not null ? GroupLayout.ConcatPath(zones, placements, spans)
            : zones.Count == 1 ? Projector.Project(zones[0], placements[0]) : GroupLayout.Concat(zones, placements);
        var total = result.Length;
        var k = 0;
        for (var z = 0; z < zones.Count; z++)
        {
            var n = zones[z].LedCount;
            if (perKeyPoints[z] is { } points)
            {
                if (placements[z] is not null)
                {
                    throw new NotSupportedException("Per-key case placement requires a separate physical desk calibration.");
                }

                if (points.Count != n)
                {
                    throw new ArgumentException("Per-key point count does not match the zone.", nameof(perKeyPoints));
                }

                for (var i = 0; i < n; i++)
                {
                    var p = points[i];
                    var u = spans is not null ? spans[z].GlobalU(p.U)
                        : zones.Count == 1 ? p.U
                        : (k + 0.5f + (p.U * (n - 1))) / total;
                    result[k + i] = new Sample(u, p.Theta, p.X, p.Y);
                }
            }

            k += n;
        }

        return result;
    }
}
