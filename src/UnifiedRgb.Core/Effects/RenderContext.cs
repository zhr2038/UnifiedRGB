using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects;

public enum Direction
{
    Forward = 0,
    Reverse = 1,
}

/// <summary>
/// Everything a generator may read for one frame (IMPLEMENTATION_PLAN §1). A ref struct over caller-owned spans,
/// so building one per frame never allocates. Speed and Brightness are normalized to [0,1].
/// </summary>
public readonly ref struct RenderContext
{
    public RenderContext(double t, double dt, ReadOnlySpan<Rgb> colors, float speed, float brightness, Direction dir, ReadOnlySpan<Sample> samples, uint seed)
    {
        T = t;
        Dt = dt;
        Colors = colors;
        Speed = Math.Clamp(speed, 0f, 1f);
        Brightness = Math.Clamp(brightness, 0f, 1f);
        Dir = dir;
        Samples = samples;
        Seed = seed;
    }

    public double T { get; }

    public double Dt { get; }

    public ReadOnlySpan<Rgb> Colors { get; }

    public float Speed { get; }

    public float Brightness { get; }

    public Direction Dir { get; }

    public ReadOnlySpan<Sample> Samples { get; }

    public uint Seed { get; }

    /// <summary>Position of LED i along the zone/group, mirrored when Dir = Reverse.</summary>
    public float U(int i)
    {
        var u = Samples[i].U;
        return Dir == Direction.Reverse ? 1f - u : u;
    }

    /// <summary>Index of LED i in travel order (mirrored when Dir = Reverse).</summary>
    public int Index(int i) => Dir == Direction.Reverse ? Samples.Length - 1 - i : i;

    /// <summary>Exponential speed mapping: speed 0 → min, 1 → max (units per second).</summary>
    public double Rate(double min, double max) => min * Math.Pow(max / min, Speed);

    /// <summary>Cycles elapsed at the given speed range (double precision, safe for long uptimes).</summary>
    public double Phase(double min, double max) => T * Rate(min, max);

    /// <summary>mapUserColor: palette color k (wrapping), or a 7-step rainbow when the palette is empty.</summary>
    public Rgb Color(long k) => UnifiedRgb.Core.Color.ColorTables.MapUserColor(Colors, (int)(k % 1_000_000));
}
