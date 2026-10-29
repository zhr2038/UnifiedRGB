using System.Security.Cryptography;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The independent scalar oracle: a VERBATIM copy of the pre-change <c>MfH264IntraEncoder.ToNv12</c> (original file SHA-256
/// 965E1AA7307744E0DC22785CADCF43BE30ACC7C8B2BFCC604BB5AB968B425095). It is never optimized; every faster path must equal it byte
/// for byte (BT.709 full/limited integer coefficients, +128 rounding, arithmetic shifts, clamps, rounded 2x2 chroma averages).
/// </summary>
internal static class Nv12Oracle
{
    public static byte[] ToNv12(PixelFrame frame, bool fullRange = false)
    {
        var w = frame.Width; var h = frame.Height; var p = frame.Pixels;
        var data = new byte[checked(w * h * 3 / 2)];
        for (var y = 0; y < h; y += 2)
        for (var x = 0; x < w; x += 2)
        {
            int sumR = 0, sumG = 0, sumB = 0;
            for (var yy = 0; yy < 2; yy++)
            for (var xx = 0; xx < 2; xx++)
            {
                var i = ((y + yy) * w + x + xx) * 4; int b = p[i], g = p[i + 1], r = p[i + 2];
                data[(y + yy) * w + x + xx] = fullRange
                    ? (byte)Math.Clamp((54 * r + 183 * g + 19 * b + 128) >> 8, 0, 255)
                    : (byte)Math.Clamp(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8), 16, 235);
                sumR += r; sumG += g; sumB += b;
            }
            var r4 = (sumR + 2) / 4; var g4 = (sumG + 2) / 4; var b4 = (sumB + 2) / 4;
            var uv = w * h + y / 2 * w + x;
            data[uv] = fullRange ? (byte)Math.Clamp(128 + ((-29 * r4 - 99 * g4 + 128 * b4 + 128) >> 8), 0, 255)
                : (byte)Math.Clamp(128 + ((-26 * r4 - 87 * g4 + 112 * b4 + 128) >> 8), 16, 240);
            data[uv + 1] = fullRange ? (byte)Math.Clamp(128 + ((128 * r4 - 116 * g4 - 12 * b4 + 128) >> 8), 0, 255)
                : (byte)Math.Clamp(128 + ((112 * r4 - 102 * g4 - 10 * b4 + 128) >> 8), 16, 240);
        }
        return data;
    }
}

/// <summary>Deterministic rasters for the NV12/H.264 tests (seeded; identical in every build).</summary>
internal static class Nv12Rasters
{
    public static PixelFrame Noise(int w, int h, int seed)
    {
        var frame = new PixelFrame(w, h);
        new Random(seed).NextBytes(frame.Pixels); // random alpha too: the conversion must ignore it
        return frame;
    }

    public static PixelFrame Gradient(int w, int h)
    {
        var frame = new PixelFrame(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = ((y * w) + x) * 4;
            frame.Pixels[i] = (byte)(x * 255 / Math.Max(1, w - 1));
            frame.Pixels[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
            frame.Pixels[i + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
            frame.Pixels[i + 3] = 255;
        }

        return frame;
    }

    /// <summary>Smooth photo-like content (overlapping waves in every channel), opaque.</summary>
    public static PixelFrame Scene(int w, int h)
    {
        var frame = new PixelFrame(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = ((y * w) + x) * 4;
            frame.Pixels[i] = Wave(80, (x + y) / 61.0 + 2);
            frame.Pixels[i + 1] = Wave(90, (x / 23.0) - (y / 41.0) + 1);
            frame.Pixels[i + 2] = Wave(100, (x / 37.0) + (y / 53.0));
            frame.Pixels[i + 3] = 255;
        }

        return frame;
    }

    private static byte Wave(double amplitude, double phase) => (byte)Math.Clamp((int)Math.Round(128 + (amplitude * Math.Sin(phase))), 0, 255);

    public static readonly PcTelemetryDto Telemetry = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero), 25.04, 12L << 30, 32L << 30, 42,
        61.4, 8L << 30, 32L << 30, "s", null);

    /// <summary>The landscape scene with the actual dashboard telemetry layer blended in and flattened, as the compositor does (STA).</summary>
    public static PixelFrame SceneWithTelemetry(int w, int h)
    {
        var canvas = Scene(w, h);
        var layer = new OverlayRenderer().Render(w, h, OverlayShape.Rectangle, "dashboard", System.Windows.Media.Color.FromRgb(255, 200, 40),
            Telemetry, Telemetry.Timestamp, null) ?? throw new InvalidOperationException("WPF offscreen rendering is unavailable");
        PixelOps.BlendOver(canvas, layer, 0, 0);
        PixelOps.Flatten(canvas);
        return canvas;
    }

    /// <summary>Named representative rasters: the actual 1920x480 / 480x1920 sizes plus small and odd-width-tail shapes (STA).</summary>
    public static Task<List<(string Name, PixelFrame Frame)>> RepresentativeAsync() => Sta.RunAsync(() =>
    {
        var overlay = SceneWithTelemetry(1920, 480);
        return new List<(string, PixelFrame)>
        {
            ("noise-1920x480", Noise(1920, 480, 1)),
            ("noise-480x1920", Noise(480, 1920, 2)),
            ("gradient-1920x480", Gradient(1920, 480)),
            ("gradient-480x1920", Gradient(480, 1920)),
            ("scene-1920x480", Scene(1920, 480)),
            ("scene-telemetry-1920x480", overlay),
            ("scene-telemetry-480x1920", PixelOps.Rotate(overlay, 90)),
            ("noise-2x2", Noise(2, 2, 3)),
            ("noise-6x4", Noise(6, 4, 4)),
            ("noise-34x18", Noise(34, 18, 5)),
            ("noise-66x2", Noise(66, 2, 6)),
            ("noise-1922x6", Noise(1922, 6, 7)),
            ("noise-480x480", Noise(480, 480, 8)),
        };
    });

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>The first differing index (or -1) without a per-element assertion over megabytes.</summary>
    public static int FirstDifference(byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length) return Math.Min(actual.Length, expected.Length);
        var i = actual.AsSpan().CommonPrefixLength(expected);
        return i == actual.Length ? -1 : i;
    }
}
