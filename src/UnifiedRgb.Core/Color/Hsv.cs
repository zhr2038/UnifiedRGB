using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Color;

/// <summary>HSV helpers (port target: slv3.RgbEffect.HsvToRgb). Hue in degrees [0,360), S and V in [0,1].</summary>
public static class Hsv
{
    public static Rgb ToRgb(float hue, float saturation, float value)
    {
        hue %= 360f;
        if (hue < 0)
        {
            hue += 360f;
        }

        saturation = Math.Clamp(saturation, 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);
        var c = value * saturation;
        var hp = hue / 60f;
        var x = c * (1f - MathF.Abs((hp % 2f) - 1f));
        float r, g, b;
        switch ((int)hp)
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }

        var m = value - c;
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    public static (float H, float S, float V) FromRgb(Rgb c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d = max - min;
        float h = 0;
        if (d > 0)
        {
            if (max == r)
            {
                h = 60f * (((g - b) / d) % 6f);
            }
            else if (max == g)
            {
                h = 60f * (((b - r) / d) + 2f);
            }
            else
            {
                h = 60f * (((r - g) / d) + 4f);
            }
        }

        if (h < 0)
        {
            h += 360f;
        }

        return (h, max == 0 ? 0 : d / max, max);
    }

    internal static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
