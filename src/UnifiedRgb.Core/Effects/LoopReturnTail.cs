namespace UnifiedRgb.Core.Effects;

/// <summary>
/// STACK-RETURN: a pure source-strip tail for a filled loop, applied to the whole source strip BEFORE any path, family or
/// native slicing (so every zone sees one extended timeline). The forward frames are kept as they are (same arrays), the final
/// frame is held, then multiplied by a travelling feather that drains it from the end the forward moving bar finished at:
/// for return frame k = 1..R, t = k / R, boundary = (D + W)(1 − t); dot j (counted from the end drained LAST) has
/// u = (boundary − (j + 0.5)) / W and weight 1 for u ≥ 1, 0 for u ≤ 0, else S · (1 − s + s · cos 4πu) with S = u²(3 − 2u).
/// Two soft ripples inside the feather, never a global breathing: the full region stays full, the cleared region black, no
/// byte exceeds the held frame, and frame R is exact black.
/// </summary>
public static class LoopReturnTail
{
    /// <summary>
    /// The drain weight of source dot <paramref name="dot"/> (0 = the end drained last) of <paramref name="dots"/> at return
    /// frame <paramref name="frame"/> of <paramref name="frames"/> (1-based; the last is 0 everywhere).
    /// </summary>
    public static double Weight(int dot, int dots, int frame, int frames, int width, double strength)
    {
        var t = (double)frame / frames;
        var boundary = (dots + width) * (1 - t);
        var u = (boundary - (dot + 0.5)) / width;
        if (u >= 1)
        {
            return 1;
        }

        if (u <= 0)
        {
            return 0;
        }

        var s = u * u * (3 - (2 * u));
        return s * (1 - strength + (strength * Math.Cos(4 * Math.PI * u)));
    }

    /// <summary>One channel scaled by a weight in [0, 1], rounded half-up (never above <paramref name="value"/>).</summary>
    public static byte Scale(byte value, double weight) => (byte)Math.Floor((value * weight) + 0.5);

    /// <summary>
    /// <paramref name="frames"/> (each <paramref name="dots"/> RGB dots), then <paramref name="holdFrames"/> copies of the last
    /// frame, then <paramref name="returnFrames"/> drain frames. <paramref name="drainHighFirst"/>: the high source end drains
    /// first and the low end last (the forward bar ran low → high); false mirrors it.
    /// </summary>
    public static List<byte[]> Extend(IReadOnlyList<byte[]> frames, int dots, int holdFrames, int returnFrames, int width, double strength,
        bool drainHighFirst, int maxFrames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dots);
        ArgumentOutOfRangeException.ThrowIfNegative(holdFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(returnFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (!(strength >= 0 && strength <= Engine.LoopReturn.MaxWaveStrength))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, $"The ripple strength must be 0..{Engine.LoopReturn.MaxWaveStrength}.");
        }

        if (frames.Count == 0)
        {
            throw new ArgumentException("No frame to hold.", nameof(frames));
        }

        if ((long)frames.Count + holdFrames + returnFrames > maxFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(returnFrames), $"{frames.Count} + {holdFrames} + {returnFrames} frames exceed {maxFrames}.");
        }

        foreach (var f in frames)
        {
            if (f is null || f.Length != dots * 3)
            {
                throw new ArgumentException($"Every frame must be {dots} RGB dots.", nameof(frames));
            }
        }

        var full = frames[^1];
        var list = new List<byte[]>(frames.Count + holdFrames + returnFrames);
        list.AddRange(frames);
        for (var h = 0; h < holdFrames; h++)
        {
            list.Add((byte[])full.Clone());
        }

        for (var k = 1; k <= returnFrames; k++)
        {
            var f = new byte[dots * 3];
            for (var j = 0; j < dots; j++)
            {
                var at = drainHighFirst ? j : dots - 1 - j;
                var w = Weight(j, dots, k, returnFrames, width, strength);
                for (var c = 0; c < 3; c++)
                {
                    f[(at * 3) + c] = Scale(full[(at * 3) + c], w);
                }
            }

            list.Add(f);
        }

        return list;
    }
}
