namespace UnifiedRgb.Core.Effects;

/// <summary>
/// Optional on generators whose output is exactly periodic in T (ARCHITECTURE-D2 §1): the LoopCompiler renders one period
/// for UploadedLoop zones. 0 means time-invariant (one frame). Generators without it (random or very long periods) get the
/// fixed seeded fallback loop.
/// </summary>
public interface ILoopable
{
    /// <summary>Seconds after which the output repeats, for these params (Colors already resolved to the palette used).</summary>
    double LoopSeconds(in EffectParams p);
}

/// <summary>Periods of the shared <see cref="RenderContext.Phase"/> shapes.</summary>
public static class LoopPeriod
{
    /// <summary>Same mapping as <see cref="RenderContext.Rate"/> (speed clamped to [0,1]).</summary>
    public static double Rate(float speed, double min, double max) => min * Math.Pow(max / min, Math.Clamp(speed, 0f, 1f));

    /// <summary><paramref name="cycles"/> phase cycles at the given speed range.</summary>
    public static double Cycles(in EffectParams p, double min, double max, double cycles) => cycles / Rate(p.Speed, min, max);

    /// <summary>One cycle per palette colour (<c>ctx.Color(Floor(ph))</c>): the palette size, or 7 for the rainbow fallback.</summary>
    public static double PerColor(in EffectParams p, double min, double max) => Cycles(p, min, max, p.Colors.Count > 0 ? p.Colors.Count : 7);
}
