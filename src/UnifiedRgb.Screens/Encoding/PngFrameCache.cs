using System.Security.Cryptography;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Encoding;

/// <summary>Encoded bytes only: no retained full rasters, no files. One process-wide LRU with hard byte and entry limits.</summary>
internal sealed class PngFrameCache(long maxBytes, int maxEntries)
{
    internal sealed record Value(byte[] Image, int Bits, string Note);
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Value Value)>> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Value Value)> _lru = new();
    private long _bytes;
    public (long Bytes, int Entries) Size { get { lock (_gate) return (_bytes, _items.Count); } }

    public static string Key(PixelFrame canvas, int rotation, string policy) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{canvas.Width}x{canvas.Height}/{rotation}/{policy}/{Convert.ToHexString(SHA256.HashData(canvas.Pixels))}");

    public Value? Find(string key)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var node)) return null;
            _lru.Remove(node); _lru.AddFirst(node);
            return node.Value.Value;
        }
    }

    // Image bytes are immutable after publication; only internal protocol/preview code sees them (both copy/decode).
    public void Add(string key, Value value)
    {
        lock (_gate)
        {
            if (value.Image.LongLength > maxBytes || maxEntries < 1) return;
            if (_items.Remove(key, out var old)) { _bytes -= old.Value.Value.Image.LongLength; _lru.Remove(old); }
            while (_lru.Last is { } tail && (_bytes + value.Image.LongLength > maxBytes || _items.Count >= maxEntries))
            {
                _items.Remove(tail.Value.Key); _bytes -= tail.Value.Value.Image.LongLength; _lru.RemoveLast();
            }
            _items.Add(key, _lru.AddFirst((key, value))); _bytes += value.Image.LongLength;
        }
    }
}
