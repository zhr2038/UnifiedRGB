using System.Buffers.Binary;
using UnifiedRgb.Devices.LianLi.Protocol;

namespace UnifiedRgb.Devices.LianLi.Codec;

/// <summary>
/// Bounded, deterministic, lossless RF loop codec. No native library, disk access, frame removal or colour quantization.
/// Wire coding and the checked decoder are derived from tinyuz 1.1.1 (HouSisong, MIT); see THIRD-PARTY-NOTICES.md.
/// The hash-chain/lazy match finder is ours. Compression runs only during loop preparation, never on the render clock.
/// </summary>
public sealed class ManagedTinyuzEncoder : IRgbAnimationEncoder
{
    public const string Identity = "unifiedrgb-tinyuz-managed-v2-4k";
    // Complete RF header domain (65535 frames, 255 RGB LEDs). Reject before allocating beyond it.
    public const int MaxInputBytes = ushort.MaxValue * byte.MaxValue * 3;
    // The observed vendor streams cap the decoder dictionary at 4096 bytes. Desktop TuzDec can decode much larger
    // streams, but that does not establish receiver SRAM capacity (TL rejected v1's larger dictionaries).
    public const int DictionaryLimit = 4096;
    private const int BigPosition = 2687;
    private const int MaxMatch = 65535;

    public string CodecHash => Identity;

    public byte[] Encode(ReadOnlySpan<byte> rgbData)
    {
        if (rgbData.IsEmpty || rgbData.Length > MaxInputBytes)
        { throw new ArgumentOutOfRangeException(nameof(rgbData), $"Codec input must contain 1..{MaxInputBytes} bytes."); }

        // Different parses matter more than longer searches for RGB runs. Select the smaller complete stream.
        var first = EncodePass(rgbData, lazy: false);
        var second = EncodePass(rgbData, lazy: true);
        var packed = second.Length < first.Length ? second : first;
        if (BinaryPrimitives.ReadUInt32LittleEndian(packed) > DictionaryLimit)
        { throw new InvalidDataException("Tinyuz dictionary exceeds the receiver-compatible 4 KiB limit; upload refused."); }
        // The independent checked reader is also a production integrity gate, before any upload is possible.
        if (!Decode(packed, rgbData.Length).AsSpan().SequenceEqual(rgbData))
        { throw new InvalidDataException("Managed tinyuz round-trip mismatch; upload refused."); }
        return packed;
    }

    private static byte[] EncodePass(ReadOnlySpan<byte> data, bool lazy)
    {
        var heads = new int[65536];
        var previous = new int[DictionaryLimit];
        Array.Fill(heads, -1);
        Array.Fill(previous, -1);
        var writer = new CodeWriter();
        long work = ((long)data.Length * 128) + 1048576;
        var inserted = 0;
        var position = 0;
        while (position < data.Length)
        {
            var match = Find(data, position, heads, previous, writer, ref work);
            Insert(data, position, heads, previous);
            inserted = position + 1;
            if (lazy && match.Length > 0 && position + 1 < data.Length)
            {
                var next = Find(data, position + 1, heads, previous, writer, ref work);
                if (next.Length > match.Length + 1)
                { writer.Literal(data[position++]); continue; }
            }
            if (match.Length == 0)
            { writer.Literal(data[position++]); continue; }

            writer.Match(match.Length, match.Distance);
            position += match.Length;
            // Match the receiver-compatible 4 KiB window; auxiliary memory is independent of loop length.
            for (; inserted < position; inserted++)
            { Insert(data, inserted, heads, previous); }
        }
        return writer.Finish();
    }

    private static void Insert(ReadOnlySpan<byte> data, int position, int[] heads, int[] previous)
    {
        if (position + 1 >= data.Length) { return; }
        var hash = Hash(data, position);
        previous[position & (DictionaryLimit - 1)] = heads[hash];
        heads[hash] = position;
    }

    private static (int Length, int Distance) Find(ReadOnlySpan<byte> data, int position, int[] heads,
        int[] previous, CodeWriter writer, ref long work)
    {
        if (position + 1 >= data.Length || work <= 0) { return default; }
        var candidate = heads[Hash(data, position)];
        var bestLength = 0;
        var bestDistance = 0;
        var bestSaving = 0;
        var limit = Math.Min(MaxMatch, data.Length - position);
        for (var count = 0; candidate >= 0 && position - candidate <= DictionaryLimit && count < 512 && work > 0; count++)
        {
            var distance = position - candidate;
            if (bestLength > 0 && data[candidate + bestLength] != data[position + bestLength])
            {
                work--;
                var skip = previous[candidate & (DictionaryLimit - 1)];
                if (skip >= candidate) { break; }
                candidate = skip; continue;
            }
            // Charge the full compared span, including vectorized comparisons, to a deterministic work budget.
            var cap = (int)Math.Min(limit, work);
            if (cap < 2) { break; }
            var length = data.Slice(candidate, cap).CommonPrefixLength(data.Slice(position, cap));
            work -= Math.Max(2, length + 1);
            var saving = (length * 9) - writer.MatchBits(length, distance);
            if (saving > bestSaving)
            { (bestLength, bestDistance, bestSaving) = (length, distance, saving); }
            if (length == limit) { break; }
            var next = previous[candidate & (DictionaryLimit - 1)];
            if (next >= candidate) { break; }
            candidate = next;
        }
        return (bestLength, bestDistance);
    }

    private static int Hash(ReadOnlySpan<byte> data, int position) => position + 3 < data.Length
        ? (int)((BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) * 2654435761U) >> 16)
        : BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);

    private sealed class CodeWriter
    {
        private readonly List<byte> _bytes = [1, 0, 0, 0];
        private int _bitIndex;
        private int _bitCount;
        private int _lastDistance = 1;
        private int _maxDistance = 1;
        private bool _hasLiteral;

        public void Literal(byte value)
        { Bit(1); _bytes.Add(value); _hasLiteral = true; }

        public int MatchBits(int length, int distance)
        {
            var same = _hasLiteral && _lastDistance == distance;
            var saved = length - 2 - (!same && distance > BigPosition ? 1 : 0);
            if (saved < 0) { return int.MaxValue; }
            return 1 + NumberBits(saved, 1) + (_hasLiteral ? 1 : 0)
                + (same ? 0 : 8 + (distance >= 128 ? NumberBits((distance - 128) >> 7, 2) : 0));
        }

        public void Match(int length, int distance)
        {
            var same = _hasLiteral && _lastDistance == distance;
            Bit(0);
            Number(length - 2 - (!same && distance > BigPosition ? 1 : 0), 1);
            if (_hasLiteral) { Bit(same ? 1 : 0); }
            if (!same) { Position(distance); }
            _maxDistance = Math.Max(_maxDistance, distance);
            _lastDistance = distance;
            _hasLiteral = false;
        }

        public byte[] Finish()
        {
            Bit(0); Number(3, 1);
            if (_hasLiteral) { Bit(0); }
            Position(0);
            var result = _bytes.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(result, _maxDistance);
            return result;
        }

        private void Position(int distance)
        {
            if (distance < 128) { _bytes.Add((byte)distance); return; }
            distance -= 128;
            _bytes.Add((byte)((distance & 127) | 128));
            Number(distance >> 7, 2);
        }

        private void Bit(int value)
        {
            if (_bitCount == 0) { _bitIndex = _bytes.Count; _bytes.Add(0); }
            _bytes[_bitIndex] |= (byte)(value << _bitCount);
            _bitCount = (_bitCount + 1) & 7;
        }

        private static int NumberBits(int value, int payloadBits)
        {
            var count = 1;
            for (long threshold = 1 << payloadBits; value >= threshold; threshold <<= payloadBits)
            { value -= (int)threshold; count++; }
            return count * (payloadBits + 1);
        }

        private void Number(int value, int payloadBits)
        {
            var groups = NumberBits(value, payloadBits) / (payloadBits + 1);
            for (var i = 1; i < groups; i++) { value -= 1 << (i * payloadBits); }
            for (var group = groups - 1; group >= 0; group--)
            {
                for (var b = 0; b < payloadBits; b++) { Bit((value >> ((group * payloadBits) + b)) & 1); }
                Bit(group == 0 ? 0 : 1);
            }
        }
    }

    /// <summary>Checked reader of the tinyuz 1.1.1 format, including literal lines and clip boundaries.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalLength)
    {
        if (originalLength is <= 0 or > MaxInputBytes) { throw new ArgumentOutOfRangeException(nameof(originalLength)); }
        if (compressed.Length < 6) { throw new InvalidDataException("Truncated tinyuz stream."); }
        var dictionary = BinaryPrimitives.ReadUInt32LittleEndian(compressed);
        if (dictionary == 0 || dictionary > MaxInputBytes) { throw new InvalidDataException("Invalid tinyuz dictionary."); }
        var reader = new CodeReader(compressed[4..]);
        var result = new byte[originalLength];
        var output = 0;
        var lastDistance = 1;
        var hasLiteral = false;
        while (true)
        {
            if (reader.Bits(1) == 1)
            {
                if (output == result.Length) { throw new InvalidDataException("Tinyuz output overflow."); }
                result[output++] = reader.Byte(); hasLiteral = true; continue;
            }
            var length = reader.Number(1);
            var same = hasLiteral && reader.Bits(1) == 1;
            var distance = same ? lastDistance : reader.Position();
            if (!same && distance > BigPosition) { length = checked(length + 1); }
            hasLiteral = false;
            if (distance != 0)
            {
                if (distance > dictionary || distance > output || length > result.Length - output - 2)
                { throw new InvalidDataException("Invalid tinyuz match."); }
                length += 2;
                for (var i = 0; i < length; i++) { result[output] = result[output - distance]; output++; }
                lastDistance = distance;
            }
            else if (length == 1)
            {
                var count = checked(reader.Number(2) + 15);
                if (count > result.Length - output) { throw new InvalidDataException("Tinyuz literal overflow."); }
                for (var i = 0; i < count; i++) { result[output++] = reader.Byte(); }
                hasLiteral = true;
            }
            else
            {
                reader.Align(); lastDistance = 1;
                if (length == 2) { continue; }
                if (length != 3 || output != result.Length || !reader.AtEnd)
                { throw new InvalidDataException("Invalid tinyuz termination or output length."); }
                return result;
            }
        }
    }

    private ref struct CodeReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        private int _bits;
        private int _count;
        public readonly bool AtEnd => _position == _data.Length;
        public byte Byte() => _position < _data.Length ? _data[_position++] : throw new InvalidDataException("Truncated tinyuz data.");
        public void Align() { _bits = 0; _count = 0; }
        public int Bits(int count)
        {
            var result = 0;
            for (var i = 0; i < count; i++)
            {
                if (_count == 0) { _bits = Byte(); _count = 8; }
                result |= (_bits & 1) << i; _bits >>= 1; _count--;
            }
            return result;
        }
        public int Number(int payloadBits)
        {
            long result = 0;
            while (true)
            {
                var value = Bits(payloadBits + 1);
                result = (result << payloadBits) + (value & ((1 << payloadBits) - 1));
                if (result > MaxInputBytes) { throw new InvalidDataException("Tinyuz integer exceeds bound."); }
                if ((value & (1 << payloadBits)) == 0) { return (int)result; }
                result++;
            }
        }
        public int Position()
        {
            var value = Byte();
            if (value < 128) { return value; }
            var high = Number(2);
            if (high > (MaxInputBytes >> 7)) { throw new InvalidDataException("Tinyuz distance exceeds bound."); }
            return ((value & 127) | (high << 7)) + 128;
        }
    }
}
