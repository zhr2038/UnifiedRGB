using System.Runtime.InteropServices;

namespace UnifiedRgb.Devices;

/// <summary>8-bit RGB triple, wire-compatible (3 bytes, R,G,B order).</summary>
[StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public uint ToUInt32() => ((uint)R << 16) | ((uint)G << 8) | B;

    public static Rgb FromUInt32(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
