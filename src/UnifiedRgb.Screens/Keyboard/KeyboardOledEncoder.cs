using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Keyboard;

/// <summary>
/// Pure, allocation-bounded 208x64 gray encoder for the keyboard OLED animation (never the status page, whose /17 packing is
/// unchanged). Input is an opaque premultiplied BGRA raster (the compositor's flattened canvas). Luma is the integer
/// (54R + 183G + 19B + 128) >> 8; the tone curve is identity. "luma-round-v1" takes the nearest level (y*15 + 127) / 255;
/// "luma-bayer4-v1" adds the fixed 4x4 ordered threshold min(15, (y*15*32 + (2m+1)*255) / (255*32)). No error diffusion, no
/// palette training, no adaptive quality change. Output order is <see cref="KeyboardOledFormat"/> (low nibble = even pixel).
/// </summary>
public static class KeyboardOledEncoder
{
    private static readonly byte[] Bayer4 = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    public static int Luma(byte r, byte g, byte b) => ((54 * r) + (183 * g) + (19 * b) + 128) >> 8;

    public static int RoundLevel(int luma) => ((luma * 15) + 127) / 255;

    public static int BayerLevel(int luma, int x, int y)
    {
        var m = Bayer4[((y & 3) << 2) | (x & 3)];
        return Math.Min(15, ((luma * 15 * 32) + (((2 * m) + 1) * 255)) / (255 * 32));
    }

    /// <summary>Encodes into a new <see cref="KeyboardOledFormat.PackedBytes"/>-byte array.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, string encoding)
    {
        var packed = new byte[KeyboardOledFormat.PackedBytes];
        Encode(bgra, encoding, packed);
        return packed;
    }

    public static void Encode(ReadOnlySpan<byte> bgra, string encoding, Span<byte> packed)
    {
        if (!KeyboardDisplayAnimationEncodings.IsKnown(encoding)) throw new ArgumentException($"Unknown keyboard encoding '{encoding}'.", nameof(encoding));
        if (bgra.Length != KeyboardOledFormat.Pixels * 4) throw new ArgumentException($"Expected a {KeyboardOledFormat.Width}x{KeyboardOledFormat.Height} BGRA raster.", nameof(bgra));
        if (packed.Length != KeyboardOledFormat.PackedBytes) throw new ArgumentException($"Expected {KeyboardOledFormat.PackedBytes} bytes.", nameof(packed));
        var bayer = encoding == KeyboardDisplayAnimationEncodings.LumaBayer4;
        for (var p = 0; p < packed.Length; p++)
        {
            var even = Level(bgra, 2 * p, bayer);
            var odd = Level(bgra, (2 * p) + 1, bayer);
            packed[p] = (byte)(even | (odd << 4));
        }
    }

    private static int Level(ReadOnlySpan<byte> bgra, int pixel, bool bayer)
    {
        var i = pixel * 4;
        var y = Luma(bgra[i + 2], bgra[i + 1], bgra[i]);
        return bayer ? BayerLevel(y, pixel % KeyboardOledFormat.Width, pixel / KeyboardOledFormat.Width) : RoundLevel(y);
    }
}

/// <summary>
/// An immutable prepared loop of packed frames. Segment k shows <see cref="FrameAt"/>(k) from <see cref="StartMs"/>(k) until
/// the next start (or the loop end). <see cref="IndexAt"/> maps a shared-timeline position exactly as the case screens do
/// (floor of a finite positive position, else 0, modulo the asset's loop) and then binary-searches; it neither allocates nor
/// performs I/O. Frame arrays are shared and must never be mutated.
/// </summary>
public sealed class KeyboardOledFrames
{
    private readonly int[] _starts;
    private readonly byte[][] _frames;

    /// <summary>Takes ownership of the arrays (not copied): ascending starts from 0, one packed frame each.</summary>
    public KeyboardOledFrames(string assetId, string fit, string encoding, int durationMs, int sourceFrames, int[] starts, byte[][] frames)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(frames);
        if (starts.Length == 0 || starts.Length != frames.Length || starts[0] != 0) throw new ArgumentException("Invalid prepared frames.", nameof(starts));
        for (var i = 0; i < frames.Length; i++)
        {
            if (frames[i]?.Length != KeyboardOledFormat.PackedBytes || (i > 0 && starts[i] <= starts[i - 1]))
                throw new ArgumentException("Invalid prepared frames.", nameof(frames));
        }
        AssetId = assetId; Fit = fit; Encoding = encoding; DurationMs = durationMs; SourceFrames = sourceFrames;
        _starts = starts; _frames = frames;
    }

    public string AssetId { get; }
    public string Fit { get; }
    public string Encoding { get; }

    /// <summary>The asset's loop length (0 for a still image).</summary>
    public int DurationMs { get; }

    /// <summary>Source frames of the asset (before grid merging and deduplication).</summary>
    public int SourceFrames { get; }

    public int Count => _frames.Length;

    public long Bytes => (long)_frames.Length * KeyboardOledFormat.PackedBytes;

    public int StartMs(int index) => _starts[index];

    public byte[] FrameAt(int index) => _frames[index];

    public int IndexAt(double positionMs)
    {
        if (DurationMs <= 0 || _starts.Length == 1) return 0;
        var elapsed = double.IsFinite(positionMs) && positionMs > 0 ? (long)Math.Floor(positionMs) : 0;
        var t = (int)(elapsed % DurationMs);
        int lo = 0, hi = _starts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_starts[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
