using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Color;

/// <summary>
/// Per-device output correction applied after the generators (ARCHITECTURE §5): brightness, gamma and white
/// point, baked into three 256-entry lookup tables. Immutable; Apply never allocates.
/// </summary>
public sealed class OutputStage
{
    private readonly byte[] _r = new byte[256];
    private readonly byte[] _g = new byte[256];
    private readonly byte[] _b = new byte[256];

    public OutputStage(float gamma = 1f, Rgb? whitePoint = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gamma, 0f);
        Gamma = gamma;
        WhitePoint = whitePoint ?? Rgb.White;
        for (var i = 0; i < 256; i++)
        {
            var lin = MathF.Pow(i / 255f, gamma);
            _r[i] = Hsv.ToByte(lin * WhitePoint.R / 255f);
            _g[i] = Hsv.ToByte(lin * WhitePoint.G / 255f);
            _b[i] = Hsv.ToByte(lin * WhitePoint.B / 255f);
        }
    }

    public static OutputStage Identity { get; } = new();

    public float Gamma { get; }

    public Rgb WhitePoint { get; }

    /// <summary>Scales by brightness in [0,1] then applies the device LUTs in place.</summary>
    public void Apply(Span<Rgb> pixels, float brightness)
    {
        var scale = (int)MathF.Round(Math.Clamp(brightness, 0f, 1f) * 256f);
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            pixels[i] = new Rgb(_r[(p.R * scale) >> 8], _g[(p.G * scale) >> 8], _b[(p.B * scale) >> 8]);
        }
    }
}
