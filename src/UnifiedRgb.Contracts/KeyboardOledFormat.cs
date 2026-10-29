namespace UnifiedRgb.Contracts;

/// <summary>
/// The keyboard OLED image as the owner uploads it: 208x64 pixels, 4-bit gray levels 0..15, row-major, two pixels per byte.
/// Byte p holds pixel 2p in its LOW nibble and pixel 2p+1 in its HIGH nibble (the existing status-page order). The Host
/// encoder and the App preview share this one definition, so a preview shows exactly the packed bytes, nibble order included.
/// </summary>
public static class KeyboardOledFormat
{
    public const int Width = 208;
    public const int Height = 64;
    public const int Pixels = Width * Height;
    public const int PackedBytes = Pixels / 2;

    /// <summary>Packs <see cref="Pixels"/> levels (each 0..15) into <see cref="PackedBytes"/> bytes.</summary>
    public static void Pack(ReadOnlySpan<byte> levels, Span<byte> packed)
    {
        if (levels.Length != Pixels) throw new ArgumentException($"Expected {Pixels} levels.", nameof(levels));
        if (packed.Length != PackedBytes) throw new ArgumentException($"Expected {PackedBytes} bytes.", nameof(packed));
        for (var p = 0; p < PackedBytes; p++)
        {
            var even = levels[2 * p];
            var odd = levels[(2 * p) + 1];
            if (even > 15 || odd > 15) throw new ArgumentOutOfRangeException(nameof(levels), "A gray level is 0..15.");
            packed[p] = (byte)(even | (odd << 4));
        }
    }

    /// <summary>Unpacks <see cref="PackedBytes"/> bytes into one 0..15 level per pixel.</summary>
    public static void Unpack(ReadOnlySpan<byte> packed, Span<byte> levels)
    {
        if (packed.Length != PackedBytes) throw new ArgumentException($"Expected {PackedBytes} bytes.", nameof(packed));
        if (levels.Length != Pixels) throw new ArgumentException($"Expected {Pixels} levels.", nameof(levels));
        for (var p = 0; p < PackedBytes; p++)
        {
            levels[2 * p] = (byte)(packed[p] & 0x0F);
            levels[(2 * p) + 1] = (byte)(packed[p] >> 4);
        }
    }
}
