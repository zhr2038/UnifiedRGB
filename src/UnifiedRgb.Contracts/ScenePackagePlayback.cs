namespace UnifiedRgb.Contracts;

using Dto;

/// <summary>Shared deterministic source-time and RGB transforms used by Host preparation and local previews.</summary>
public static class ScenePackagePlayback
{
    public const string EditedContract = "rgb24-hold-physical-ms-v1+editor-v1";

    public static string? Validate(ScenePackagePlaybackDto? p)
    {
        if (p is null) return null;
        if (!double.IsFinite(p.Speed) || p.Speed is < .25 or > 4) return "播放速度范围为 0.25～4 倍。";
        if (!double.IsFinite(p.LightingBrightness) || p.LightingBrightness is < 0 or > 1) return "灯光亮度范围为 0～100%。";
        if (!double.IsFinite(p.HueDegrees) || p.HueDegrees is < -180 or > 180) return "色相范围为 -180～180 度。";
        if (p.Screens is { Count: > 16 }) return "屏幕参数超过 16 个角色。";
        if (p.Screens is { } screens)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in screens)
            {
                if (s is null || string.IsNullOrWhiteSpace(s.Role) || !seen.Add(s.Role)) return "屏幕参数包含空角色或重复角色。";
                if (s.Brightness is { } b && (!double.IsFinite(b) || b is < 0 or > 1)) return "屏幕亮度范围为 0～100%。";
                if (s.Fps is < 1 or > 60) return "屏幕发送上限范围为 1～60 fps。";
                if (s.Overlay is not (null or "none" or "compact" or "dashboard")) return "未知的屏幕叠层。";
                if (s.Fit is not (null or "contain" or "cover")) return "未知的屏幕适配方式。";
                if (s.AccentColor is { } c && (c.Length != 7 || c[0] != '#' || !c.AsSpan(1).ToString().All(char.IsAsciiHexDigit))) return "强调色应为 #RRGGBB。";
                if (s.OverlayLayout is { } r && (!Finite(r.X, r.Y, r.Width, r.Height, r.BackgroundOpacity) || r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || r.X + r.Width > 1 || r.Y + r.Height > 1 || r.Columns is < 1 or > 4 || r.BackgroundOpacity is < 0 or > 1)) return "监控区域必须完整位于画布内，列数为 1～4。";
            }
        }
        return null;
    }

    public static bool IsIdentity(ScenePackagePlaybackDto? p) => p is null ||
        p.Speed == 1 && !p.Reverse && p.LightingBrightness == 1 && p.HueDegrees == 0 && p.LightingEnabled && (p.Screens is null || p.Screens.Count == 0);

    public static double SourcePosition(double elapsedMs, double durationMs, ScenePackagePlaybackDto? p)
    {
        if (!double.IsFinite(elapsedMs) || !double.IsFinite(durationMs) || durationMs <= 0) return 0;
        var value = Math.Max(0, elapsedMs) * (p?.Speed ?? 1);
        value -= Math.Floor(value / durationMs) * durationMs;
        return p?.Reverse == true ? Math.Max(0, durationMs - value - .000001) : value;
    }

    public static void TransformRgb(byte r, byte g, byte b, ScenePackagePlaybackDto? p, out byte red, out byte green, out byte blue)
    {
        var level = p is null ? 1 : p.LightingEnabled ? p.LightingBrightness : 0;
        if (p is null || p.HueDegrees == 0)
        {
            red = Scale(r, level); green = Scale(g, level); blue = Scale(b, level); return;
        }
        var rf = r / 255.0; var gf = g / 255.0; var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf)); var min = Math.Min(rf, Math.Min(gf, bf)); var delta = max - min;
        var h = delta == 0 ? 0 : max == rf ? (gf - bf) / delta : max == gf ? 2 + ((bf - rf) / delta) : 4 + ((rf - gf) / delta);
        h = ((h * 60) + p.HueDegrees + 720) % 360 / 60;
        var c = delta; var x = c * (1 - Math.Abs((h % 2) - 1)); var m = min;
        var v = h switch { < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x), < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x) };
        red = Scale((v.Item1 + m) * 255, level); green = Scale((v.Item2 + m) * 255, level); blue = Scale((v.Item3 + m) * 255, level);
    }

    public static byte[] TransformFrames(ReadOnlySpan<byte> source, int leds, int frames, ScenePackagePlaybackDto p)
    {
        if (leds < 1 || frames < 1 || source.Length != checked(leds * frames * 3)) throw new ArgumentException("Invalid RGB frame dimensions.", nameof(source));
        var result = new byte[source.Length]; var stride = leds * 3;
        for (var f = 0; f < frames; f++)
        {
            var sourceFrame = p.Reverse ? frames - f - 1 : f;
            for (var i = 0; i < stride; i += 3)
            {
                var offset = sourceFrame * stride + i;
                TransformRgb(source[offset], source[offset + 1], source[offset + 2], p, out result[f * stride + i], out result[f * stride + i + 1], out result[f * stride + i + 2]);
            }
        }
        return result;
    }

    private static byte Scale(double value, double brightness) => (byte)Math.Clamp((int)Math.Round(value * brightness), 0, 255);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
}
