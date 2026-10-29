using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Layout;

/// <summary>
/// Turns a zone layout into per-LED sample coordinates (ARCHITECTURE §5):
/// Linear → u = i/(n-1); Ring → θ and u = i/n (periodic); Matrix → (x,y), u = x; Point → u = 0.5.
/// </summary>
public static class Projector
{
    public static Sample[] Project(ZoneLayout layout)
    {
        var s = new Sample[layout.LedCount];
        Project(layout, s);
        return s;
    }

    /// <summary>
    /// NATIVE design §7.6: with a <paramref name="placement"/> (native-maps.json, case-front unit square) X/Y are moved into case
    /// coordinates: a Point zone sits at the origin, any other layout runs from the origin along the normalised direction for
    /// <c>Length</c>, parameterised by its own local u. U and θ are unchanged, so generators that travel along U behave as
    /// before; null placement is exactly <see cref="Project(ZoneLayout)"/> (Lian Li zones never carry one).
    /// </summary>
    public static Sample[] Project(ZoneLayout layout, ZonePlacement? placement)
    {
        var s = Project(layout);
        if (placement is not null)
        {
            Place(layout, placement, s);
        }

        return s;
    }

    /// <summary>Projects sparse per-key coordinates in map order, or the ordinary dense zone layout.</summary>
    public static Sample[] Project(ZoneDescriptor zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.PerKeyPoints is null) return Project(zone.Layout, zone.Placement);
        if (zone.Placement is not null) throw new NotSupportedException("Per-key case placement requires a separate physical desk calibration.");
        if (zone.PerKeyPoints.Count != zone.LedCount) throw new ArgumentException("Per-key point count does not match the zone.", nameof(zone));
        return zone.PerKeyPoints.Select(p => new Sample(p.U, p.Theta, p.X, p.Y)).ToArray();
    }

    /// <summary>Rewrites X/Y of <paramref name="samples"/> (projected from <paramref name="layout"/>) by <paramref name="placement"/>.</summary>
    public static void Place(ZoneLayout layout, ZonePlacement placement, Span<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (layout.Kind == LayoutKind.Point)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = samples[i] with { X = placement.OriginX, Y = placement.OriginY };
            }

            return;
        }

        var norm = MathF.Sqrt((placement.DirX * placement.DirX) + (placement.DirY * placement.DirY));
        var (dx, dy) = norm > 0 && float.IsFinite(norm) ? (placement.DirX / norm, placement.DirY / norm) : (0f, 0f);
        var length = float.IsFinite(placement.Length) ? placement.Length : 0f;
        var local = Project(layout);
        for (var i = 0; i < samples.Length; i++)
        {
            var u = local[i].U;
            samples[i] = samples[i] with { X = placement.OriginX + (dx * length * u), Y = placement.OriginY + (dy * length * u) };
        }
    }

    public static void Project(ZoneLayout layout, Span<Sample> output)
    {
        if (output.Length != layout.LedCount)
        {
            throw new ArgumentException($"Output must have {layout.LedCount} samples.", nameof(output));
        }

        switch (layout.Kind)
        {
            case LayoutKind.Point:
                output[0] = new Sample(0.5f, 0f, 0.5f, 0.5f);
                break;

            case LayoutKind.Linear:
            {
                var n = layout.Width;
                for (var i = 0; i < n; i++)
                {
                    var u = n == 1 ? 0.5f : (float)i / (n - 1);
                    output[i] = new Sample(u, 0f, u, 0.5f);
                }

                break;
            }

            case LayoutKind.Ring:
            {
                var n = layout.Width;
                var dir = layout.Clockwise ? 1f : -1f;
                for (var i = 0; i < n; i++)
                {
                    var u = (float)i / n;
                    var theta = layout.StartAngle + (dir * u * MathF.Tau);
                    output[i] = new Sample(u, theta, 0.5f + (0.5f * MathF.Cos(theta)), 0.5f + (0.5f * MathF.Sin(theta)));
                }

                break;
            }

            case LayoutKind.Matrix:
            {
                int w = layout.Width, h = layout.Height;
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var fx = w == 1 ? 0.5f : (float)x / (w - 1);
                        var fy = h == 1 ? 0.5f : (float)y / (h - 1);
                        output[(y * w) + x] = new Sample(fx, 0f, fx, fy);
                    }
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }
}
