using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Color;

/// <summary>
/// Shared color tables and user-color mapping (port targets: GenerateRainbowColorTable, mapUserColor,
/// StaticColorTypes from analysis 05 §2/§4). All tables are built once; lookups never allocate.
/// </summary>
public static class ColorTables
{
    public const int RainbowSize = 360;

    private static readonly Rgb[] RainbowTable = GenerateRainbowColorTable(RainbowSize);

    /// <summary>StaticColorTypes: cyan=0, green=1, yellow=2, red=3, purple=4, blue=5, white=6.</summary>
    public static IReadOnlyList<Rgb> StaticColorTypes { get; } =
    [
        new(0, 255, 255), new(0, 255, 0), new(255, 255, 0), new(255, 0, 0), new(128, 0, 255), new(0, 0, 255), new(255, 255, 255),
    ];

    public static ReadOnlySpan<Rgb> Rainbow => RainbowTable;

    /// <summary>n evenly spaced fully saturated hues starting at red.</summary>
    public static Rgb[] GenerateRainbowColorTable(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        var t = new Rgb[n];
        for (var i = 0; i < n; i++)
        {
            t[i] = Hsv.ToRgb(360f * i / n, 1f, 1f);
        }

        return t;
    }

    /// <summary>Rainbow color at phase p (any real; wraps to [0,1)).</summary>
    public static Rgb RainbowAt(float p)
    {
        p -= MathF.Floor(p);
        var idx = (int)(p * RainbowSize);
        return RainbowTable[idx >= RainbowSize ? 0 : idx];
    }

    /// <summary>
    /// mapUserColor semantics: user color k of the palette, wrapping. An empty palette means "rainbow":
    /// color k is taken from an evenly spaced 7-step rainbow.
    /// </summary>
    public static Rgb MapUserColor(ReadOnlySpan<Rgb> colors, int k)
    {
        if (colors.Length == 0)
        {
            return RainbowAt(Mod(k, 7) / 7f);
        }

        return colors[Mod(k, colors.Length)];
    }

    internal static int Mod(int a, int m)
    {
        var r = a % m;
        return r < 0 ? r + m : r;
    }
}
