using UnifiedRgb.Screens.Composition;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Only immutable encoded bytes, never a retained decoded raster or a USB packet/timestamp.</summary>
internal sealed record ScreenImage(byte Command, byte[] Image, string Note)
{
    public static ScreenImage Of(EncodedFrame frame) => new(frame.Command, frame.Image, frame.Note);
}

/// <summary>
/// Per-worker cache for an unchanged assignment with no live overlay. Its owner checks the current timeline and asset
/// before every lookup and clears it on assignment/policy changes. Admission stops at either bound: a looping animation
/// larger than the budget retains a useful prefix instead of thrashing an LRU on every loop. STA-owned; no locks or I/O.
/// </summary>
internal sealed class ScreenAnimationCache(long maxBytes, int maxEntries = 512)
{
    private readonly Dictionary<(string Asset, int Frame), ScreenImage> _images = [];
    public long Bytes { get; private set; }
    public int Count => _images.Count;

    public ScreenImage? Find(string asset, int frame) => _images.GetValueOrDefault((asset, frame));

    public bool Add(string asset, int frame, ScreenImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Image.LongLength > maxBytes - Bytes || _images.Count >= maxEntries || !_images.TryAdd((asset, frame), image)) return false;
        Bytes += image.Image.LongLength;
        return true;
    }

    public void Clear()
    {
        _images.Clear();
        Bytes = 0;
    }
}
