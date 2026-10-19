using System.Runtime.CompilerServices;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects;

/// <summary>Allocation-free math shared by all generators. Randomness is a stateless hash of (seed, a, b).</summary>
public static class EffectMath
{
    public static IReadOnlyList<Rgb> Trio { get; } = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255)];

    public static IReadOnlyList<Rgb> Duo { get; } = [new(255, 0, 0), new(0, 0, 255)];

    public static IReadOnlyList<Rgb> One { get; } = [new(255, 0, 0)];

    public static IReadOnlyList<Rgb> None { get; } = [];

    /// <summary>32-bit avalanche hash (lowbias32 finalizer) of three words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint seed, uint a, uint b = 0)
    {
        var h = seed ^ (a * 0x9E3779B9u) ^ (b * 0x85EBCA6Bu) ^ 0xC2B2AE35u;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Deterministic uniform value in [0,1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rand(uint seed, long a, long b = 0) => (Hash(seed, (uint)a ^ (uint)(a >> 32), (uint)b) >> 8) * (1f / 16777216f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RandInt(uint seed, long a, long b, int maxExclusive) => (int)(Hash(seed, (uint)a ^ (uint)(a >> 32), (uint)b) % (uint)maxExclusive);

    /// <summary>Fractional part in [0,1), computed in double to stay exact over long uptimes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Frac(double x) => (float)(x - Math.Floor(x));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Floor(double x) => (long)Math.Floor(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

    public static float SmoothStep(float e0, float e1, float x)
    {
        var t = Saturate((x - e0) / (e1 - e0));
        return t * t * (3f - (2f * t));
    }

    /// <summary>Triangle wave 0→1→0 over one period.</summary>
    public static float Tri(float f) => f < 0.5f ? 2f * f : 2f - (2f * f);

    /// <summary>Linear hat of half-width w centred at c.</summary>
    public static float Hat(float x, float c, float w) => Saturate(1f - (MathF.Abs(x - c) / w));

    public static Rgb Scale(Rgb c, float v)
    {
        v = Saturate(v);
        return new Rgb((byte)((c.R * v) + 0.5f), (byte)((c.G * v) + 0.5f), (byte)((c.B * v) + 0.5f));
    }

    public static Rgb Lerp(Rgb a, Rgb b, float t)
    {
        t = Saturate(t);
        return new Rgb(
            (byte)(a.R + ((b.R - a.R) * t) + 0.5f),
            (byte)(a.G + ((b.G - a.G) * t) + 0.5f),
            (byte)(a.B + ((b.B - a.B) * t) + 0.5f));
    }

    public static Rgb AddSat(Rgb a, Rgb b) =>
        new((byte)Math.Min(255, a.R + b.R), (byte)Math.Min(255, a.G + b.G), (byte)Math.Min(255, a.B + b.B));

    /// <summary>Meteor intensity for distance d behind the head (0 = head) with the given tail length.</summary>
    public static float Tail(float d, float tail, float exponent = 2f) =>
        d < 0f || d > tail ? 0f : MathF.Pow(1f - (d / tail), exponent);

    /// <summary>Gaussian-like pulse used by the heartbeat effects.</summary>
    public static float Pulse(float x, float c, float w)
    {
        var z = (x - c) / w;
        return MathF.Exp(-z * z);
    }

    /// <summary>Second palette color, or black when the user supplied a single color.</summary>
    public static Rgb Secondary(in RenderContext ctx) => ctx.Colors.Length == 1 ? Rgb.Black : ctx.Color(1);
}
