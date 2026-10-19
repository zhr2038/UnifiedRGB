namespace UnifiedRgb.Core.Effects.LianLi;

/// <summary>
/// WP-12 (DEBUG-SESSION-2 §6.2/§8): the shared Quick/Sync <c>UiModeN</c> bodies L-Connect slices across a device group, and
/// H2Mode8, ported line by line from analysis/il/il-RgbEffect.cs. Every body returns frame-major logical-dot frames
/// (R,G,B per dot) exactly as the IL fills <c>array[frame, channel, dot]</c>: the IL's loop structure, holds (repeated
/// frames), black frames, persistent scratch arrays (Stacking, Ripple), integer <c>× bright &gt;&gt; 8</c> and its
/// direction handling (several bodies ignore <c>iDir</c>, as the vendor does) are kept; nothing is interpolated.
/// </summary>
public static partial class LConnectLoops
{
    /// <summary>UiMode6 (Meteor) tail.</summary>
    private static readonly byte[] UiMeteorTail = [12, 16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 255];

    /// <summary>UiMode9 (ColorCycle) colour index per position (0 = black, n = user colour n − 1).</summary>
    private static readonly byte[] UiColourCycle =
    [
        1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 3, 3, 3, 3,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>UiMode11 (Wave) intensity table.</summary>
    private static readonly byte[] UiWave = [0, 0, 4, 8, 16, 32, 64, 96, 128, 160, 192, 255, 255, 192, 160, 128, 96, 64, 32, 16, 8, 4, 0, 0];

    /// <summary>UiMode12 (MeteorShower) intensity and colour index tables.</summary>
    private static readonly byte[] UiShowerIntensity =
    [
        255, 128, 64, 32, 16, 8, 0, 0, 0, 0, 255, 64, 32, 16, 0, 0, 0, 0, 0, 255, 192, 168, 128, 96, 64, 32, 16, 8, 0, 0,
        0, 0, 0, 0, 0, 255, 128, 64, 32, 16, 8, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static readonly byte[] UiShowerColour =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    /// <summary>UiMode16 (Disco) colour index per position.</summary>
    private static readonly byte[] UiDisco = [0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3];

    /// <summary>UiMode22 (Warning) colour per state (0 = black) and the frames each state is held.</summary>
    private static readonly byte[] UiWarningColour = [0, 1, 0, 2, 0, 1, 0, 2, 0, 1, 0, 2, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 2, 0, 2, 0, 2, 0, 2, 0, 2];

    private static readonly byte[] UiWarningHold = [8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 2, 2, 2, 2, 8, 2, 2, 2, 2, 2, 2, 2, 2, 2];

    /// <summary>UiMode26 (Echo) tables: 27 positions up to 48 dots, 38 above.</summary>
    private static readonly byte[] UiEchoShort = [0, 0, 45, 60, 0, 0, 75, 90, 0, 0, 105, 120, 0, 0, 135, 150, 0, 0, 165, 180, 195, 0, 0, 210, 225, 240, 255];

    private static readonly byte[] UiEchoLong =
    [
        0, 0, 45, 45, 0, 0, 55, 55, 0, 0, 65, 65, 0, 0, 75, 85, 95, 0, 0, 105, 115, 125, 135, 0, 0, 145, 155, 165, 175, 185, 0, 0,
        195, 215, 225, 235, 245, 255,
    ];

    /// <summary>UiMode27 (Heartbeat) pulse envelope, per-beat offsets and the 60 pulse positions.</summary>
    private static readonly int[] UiBeatEnvelope = [51, 102, 153, 204, 255, 204, 153, 102, 51, 0];

    private static readonly int[] UiBeatShift = [0, 5, 10, 3, 6, 12, 5, 10, 6, 3];

    private static readonly int[] UiBeatDots =
    [
        3, 20, 32, 56, 80, 90, 113, 132, 147, 158, 177, 190, 208, 215, 232, 257, 275, 294, 305, 321, 342, 357, 378, 395, 409,
        429, 445, 468, 481, 496, 512, 527, 545, 566, 581, 590, 606, 625, 642, 658, 676, 692, 716, 732, 750, 766, 782, 794, 815,
        829, 850, 866, 883, 896, 910, 924, 942, 957, 969, 982,
    ];

    /// <summary>UiMode30 (Ripple) ring intensities and per-pass template offsets.</summary>
    private static readonly int[] UiRippleLevel = [255, 168, 128, 64, 32];

    private static readonly int[] UiRippleOffset = [4, 20, 36, 12, 44, 28];

    /// <summary>H2Mode8 fixed palette and colour index per block LED (the user colours are not used).</summary>
    private static readonly byte[,] H2TwinklePalette = { { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 }, { 255, 255, 0 }, { 0, 255, 255 }, { 255, 0, 255 }, { 255, 255, 255 } };

    private static readonly byte[] H2TwinkleColour = [1, 0, 6, 3, 2, 4, 1, 0, 2, 5, 0, 3, 1, 6, 4, 2, 0, 5, 1, 2, 3, 6, 4, 5];

    private static readonly Lazy<byte[]> H2TwinkleTable = new(() => LoadTable("UnifiedRgb.Core.h2-twinkle.bin", 200 * 24));

    private static readonly Lazy<byte[]> RippleTemplate = new(() => LoadTable("UnifiedRgb.Core.ui-ripple.bin", 34 * 48));

    /// <summary>
    /// The frames UiMode<paramref name="mode"/> generates over <paramref name="dots"/> logical dots, or null when the vendor
    /// body would not produce them (dot range outside the body's clamp, or more frames than its scratch array holds).
    /// </summary>
    public static IReadOnlyList<byte[]>? UiModeFrames(int mode, int dots, int bright, int direction, byte[,] ui, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(ui);
        refusal = UiModeRefusal(mode, dots);
        if (refusal is not null)
        {
            return null;
        }

        direction = direction > 1 ? 0 : direction;
        return mode switch
        {
            1 or 3 or 4 => UiFrames(mode, dots, bright, direction, ui),
            5 => UiRunway(dots, bright, ui),
            6 => UiMeteor(dots, bright, direction, ui),
            7 => UiStack(dots, bright, direction, ui),
            9 => UiTiled(dots, 48, i => UiColourCycle[i] == 0 ? default : Scale(ui, UiColourCycle[i] - 1, bright), direction, null),
            10 => UiCoverCycle(dots, bright, direction, ui),
            11 => UiTiled(dots, 24, i => Scale(ui, 0, UiWave[i]), direction, bright),
            12 => UiTiled(dots, 48, i => Scale(ui, UiShowerColour[i], UiShowerIntensity[i]), direction, bright),
            13 => UiTide(dots, bright, ui),
            14 => UiElectricCurrent(dots, bright, ui),
            15 => UiMopUp(dots, bright, ui),
            16 => UiTiled(dots, 12, i => Scale(ui, UiDisco[i], bright), direction, null),
            17 => UiMixing(dots, bright, ui),
            22 => UiWarning(dots, bright, ui),
            26 => UiEcho(dots, bright, ui),
            27 => UiHeartbeat(dots, bright, ui),
            30 => UiRipple(dots, bright, ui),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"UiMode{mode} is not ported"),
        };
    }

    /// <summary>
    /// Why <see cref="UiModeFrames"/> refuses UiMode<paramref name="mode"/> over <paramref name="dots"/> (dot range outside the
    /// body's clamp, or more frames than its scratch array holds), or null when it renders them.
    /// </summary>
    public static string? UiModeRefusal(int mode, int dots)
    {
        var max = mode == 27 ? 520 : 720;
        if (dots < 24 || dots > max)
        {
            return $"UiMode{mode} clamps {dots} dots to 24 (vendor range 24..{max}): not reproduced";
        }

        return mode == 7 && UiStackFrames(dots) > 4096
            ? $"UiMode7 over {dots} dots needs {UiStackFrames(dots)} frames, beyond the vendor's 4096-frame scratch"
            : null;
    }

    /// <summary>UiMode7's frame count: passes of 12 shrink the run until it is gone (141 dots → 141 + 129 + … + 9 = 900).</summary>
    public static int UiStackFrames(int dots)
    {
        var frames = 0;
        for (var used = 0; used < dots; used += 12)
        {
            frames += dots - used;
        }

        return frames;
    }

    /// <summary>H2Mode8 (H2Effects.Twinkle): 200 frames of the fixed palette modulated by its own 200 × 24 table.</summary>
    public static byte[][] H2Twinkle(int bright)
    {
        var table = H2TwinkleTable.Value;
        var frames = new byte[200][];
        for (var i = 0; i < 200; i++)
        {
            var f = new byte[24 * 3];
            for (var j = 0; j < 24; j++)
            {
                var c = H2TwinkleColour[j];
                var t = table[(i * 24) + j];
                Put(f, j, (byte)(H2TwinklePalette[c, 0] * t >> 8) * bright >> 8, (byte)(H2TwinklePalette[c, 1] * t >> 8) * bright >> 8,
                    (byte)(H2TwinklePalette[c, 2] * t >> 8) * bright >> 8);
            }

            frames[i] = f;
        }

        return frames;
    }

    /// <summary>(user colour <paramref name="index"/>) × <paramref name="level"/> &gt;&gt; 8, per channel.</summary>
    private static (int R, int G, int B) Scale(byte[,] ui, int index, int level) =>
        (ui[index, 0] * level >> 8, ui[index, 1] * level >> 8, ui[index, 2] * level >> 8);

    private static void Put(byte[] a, int led, (int R, int G, int B) c) => Put(a, led, c.R, c.G, c.B);

    /// <summary>
    /// UiMode9/11/12/16: a <paramref name="period"/>-position pattern, written mirrored when direction is 0, shifted one
    /// position per frame (<paramref name="period"/> frames) and tiled over the dots; <paramref name="bright"/> is applied
    /// after the pattern lookup (null = the lookup already applied it, as UiMode9/16 do: no second shift).
    /// </summary>
    private static List<byte[]> UiTiled(int dots, int period, Func<int, (int R, int G, int B)> at, int direction, int? bright)
    {
        var seg = new byte[period * 3];
        var frames = new List<byte[]>(period);
        for (var i = 0; i < period; i++)
        {
            var n = i;
            for (var j = 0; j < period; j++)
            {
                Put(seg, direction == 0 ? period - j - 1 : j, at(n));
                n = n + 1 >= period ? n + 1 - period : n + 1;
            }

            var f = new byte[dots * 3];
            for (var d = 0; d < dots; d++)
            {
                var s = (d % period) * 3;
                if (bright is { } b)
                {
                    Put(f, d, seg[s] * b >> 8, seg[s + 1] * b >> 8, seg[s + 2] * b >> 8);
                }
                else
                {
                    seg.AsSpan(s, 3).CopyTo(f.AsSpan(d * 3));
                }
            }

            frames.Add(f);
        }

        return frames;
    }

    /// <summary>UiMode5: a 12-dot colour-1 bar sweeps over colour 2, forward then back (iDir unused): 2 × (dots + 11) frames.</summary>
    private static List<byte[]> UiRunway(int dots, int bright, byte[,] ui)
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < dots + 11; j++)
            {
                var f = new byte[dots * 3];
                for (var k = 0; k < dots; k++)
                {
                    var c = k <= j && k + 12 > j ? 0 : 1;
                    Put(f, i != 0 ? dots - k - 1 : k, Scale(ui, c, bright));
                }

                frames.Add(f);
            }
        }

        return frames;
    }

    /// <summary>UiMode6: colour 1 × a 12-value tail on a dots + 12 ring, rotated one position per frame: dots + 12 frames.</summary>
    private static List<byte[]> UiMeteor(int dots, int bright, int direction, byte[,] ui)
    {
        var len = dots + 12;
        var ring = new byte[len * 3];
        for (var i = 0; i < 12; i++)
        {
            Put(ring, i, Scale(ui, 0, UiMeteorTail[i]));
        }

        var frames = new List<byte[]>(len);
        var start = 12;
        for (var j = 0; j < len; j++)
        {
            var f = new byte[dots * 3];
            for (var i = 0; i < dots; i++)
            {
                var n = start + i;
                if (n >= len)
                {
                    n = n - dots - 12;
                }

                Put(f, direction == 1 ? dots - i - 1 : i, ring[3 * n] * bright >> 8, ring[(3 * n) + 1] * bright >> 8, ring[(3 * n) + 2] * bright >> 8);
            }

            start = start != 0 ? start - 1 : len - 1;
            frames.Add(f);
        }

        return frames;
    }

    /// <summary>
    /// UiMode7 (Stack): a 12-dot colour-1 bar runs over the unfilled part, which shrinks by 12 per pass; the scratch row
    /// persists, so each pass's final bar stays lit (the stack). Every frame of every pass is kept.
    /// </summary>
    private static List<byte[]> UiStack(int dots, int bright, int direction, byte[,] ui)
    {
        var row = new byte[dots * 3];
        var frames = new List<byte[]>(UiStackFrames(dots));
        for (var used = 0; used < dots; used += 12)
        {
            for (var i = 0; i < dots - used; i++)
            {
                for (var k = 0; k < dots - used; k++)
                {
                    var lit = k <= i && k + 12 > i;
                    Put(row, direction == 1 ? dots - k - 1 : k, lit ? Scale(ui, 0, bright) : default);
                }

                frames.Add((byte[])row.Clone());
            }
        }

        return frames;
    }

    /// <summary>UiMode10: colour 1 covers colour 2 from the start, then colour 2 covers colour 1: 2 × dots frames.</summary>
    private static List<byte[]> UiCoverCycle(int dots, int bright, int direction, byte[,] ui)
    {
        var row = new byte[dots * 3];
        var frames = new List<byte[]>(2 * dots);
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < dots; j++)
            {
                for (var k = 0; k < dots; k++)
                {
                    Put(row, k, Scale(ui, k < j ? i : (i == 0 ? 1 : 0), bright));
                }

                var f = new byte[dots * 3];
                for (var k = 0; k < dots; k++)
                {
                    row.AsSpan(3 * (direction == 1 ? dots - k - 1 : k), 3).CopyTo(f.AsSpan(3 * k));
                }

                frames.Add(f);
            }
        }

        return frames;
    }

    /// <summary>
    /// UiMode13: both halves (⌈dots/2⌉ each, the second mirrored) fill with colour i over colour i − 1 (colour 4 first);
    /// for odd dots the second half's last position falls past the group, as in the vendor's dots + 8 buffer.
    /// </summary>
    private static List<byte[]> UiTide(int dots, int bright, byte[,] ui)
    {
        var half = (dots + 1) / 2;
        var frames = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < half; j++)
            {
                var f = new byte[(dots + 8) * 3];
                for (var k = 0; k < half; k++)
                {
                    var c = Scale(ui, k <= j ? i : (i != 0 ? i - 1 : 3), bright);
                    Put(f, k, c);
                    Put(f, half - k - 1 + half, c);
                }

                frames.Add(f[..(dots * 3)]);
            }
        }

        return frames;
    }

    /// <summary>
    /// UiMode14: per colour 1..2 a 12-dot bar runs out from the middle of both halves and back; each colour opens with 10
    /// black hold frames (the IL resets k to 0 while its counter is below 10) and the return run steps three dots a frame.
    /// </summary>
    private static List<byte[]> UiElectricCurrent(int dots, int bright, byte[,] ui)
    {
        var half = (dots + 1) / 2;
        var frames = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            var holds = 0;
            for (var j = 0; j < 2; j++)
            {
                for (var k = 0; k < half + 12 - 1; k++)
                {
                    if (holds < 10)
                    {
                        holds++;
                        k = 0;
                    }

                    var f = new byte[(dots + 8) * 3];
                    for (var l = 0; l < half; l++)
                    {
                        var c = l < k && l + 12 >= k ? Scale(ui, i, bright) : default;
                        Put(f, j != 0 ? half - l - 1 : l, c);
                        Put(f, (j != 0 ? l : half - l - 1) + half, c);
                    }

                    if (j != 0)
                    {
                        k += 2;
                    }

                    frames.Add(f[..(dots * 3)]);
                }
            }
        }

        return frames;
    }

    /// <summary>UiMode15: an 8-dot bar of colour 1, then colour 2, sweeps forward then back over black: 4 × (dots + 8) frames.</summary>
    private static List<byte[]> UiMopUp(int dots, int bright, byte[,] ui)
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < 2; j++)
            {
                for (var k = 0; k < dots + 8; k++)
                {
                    var f = new byte[dots * 3];
                    for (var l = 0; l < dots; l++)
                    {
                        Put(f, j != 0 ? dots - l - 1 : l, l <= k && l + 8 > k ? Scale(ui, i, bright) : default);
                    }

                    frames.Add(f);
                }
            }
        }

        return frames;
    }

    /// <summary>
    /// UiMode17: colours 1 and 2 run in from the ends of the two halves, then fill and drain with their saturated sum
    /// (capped: × 178 &gt;&gt; 8 when the sum exceeds 536); the fill and drain passes step two dots a frame.
    /// </summary>
    private static List<byte[]> UiMixing(int dots, int bright, byte[,] ui)
    {
        var mix = new int[3];
        for (var c = 0; c < 3; c++)
        {
            mix[c] = ui[0, c] + ui[1, c] >= 255 ? 255 : ui[0, c] + ui[1, c];
        }

        if (mix[0] + mix[1] + mix[2] > 536)
        {
            for (var c = 0; c < 3; c++)
            {
                mix[c] = (byte)(mix[c] * 178 >> 8);
            }
        }

        var half = (dots + 1) / 2;
        var frames = new List<byte[]>();
        for (var j = 0; j < 3; j++)
        {
            for (var k = 0; k < half + 12 - 1; k++)
            {
                var f = new byte[(dots + 8) * 3];
                for (var side = 0; side < 2; side++)
                {
                    for (var i = 0; i < half; i++)
                    {
                        (int R, int G, int B) c = default;
                        if ((j == 1 && i < k) || (j == 2 && i + 12 > k))
                        {
                            c = (mix[0], mix[1], mix[2]);
                        }
                        else if (i < k && i + 12 > k)
                        {
                            c = (ui[side, 0], ui[side, 1], ui[side, 2]);
                        }

                        var pos = side == 0 ? (j != 0 ? half - i - 1 : i) : (j != 0 ? i : half - i - 1) + half;
                        Put(f, pos, c.R * bright >> 8, c.G * bright >> 8, c.B * bright >> 8);
                    }
                }

                if (j != 0)
                {
                    k++;
                }

                frames.Add(f[..(dots * 3)]);
            }
        }

        return frames;
    }

    /// <summary>UiMode22: 32 whole-group states (black or colour 1/2), each held its table count of frames: 148 frames.</summary>
    private static List<byte[]> UiWarning(int dots, int bright, byte[,] ui)
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 32; i++)
        {
            var c = UiWarningColour[i] != 0 ? Scale(ui, UiWarningColour[i] - 1, bright) : default;
            for (var j = 0; j < UiWarningHold[i]; j++)
            {
                frames.Add(Fill(dots, c.R, c.G, c.B));
            }
        }

        return frames;
    }

    /// <summary>
    /// UiMode26: a colour-2 pulse train on a colour-1 × 64 &gt;&gt; 8 background rotates along a dots + n ring; each frame
    /// adds its mirror image (saturating at 255) before brightness. n = 27 up to 48 dots, else 38 (whose zero entries keep
    /// the background, unlike the 27-entry table).
    /// </summary>
    private static List<byte[]> UiEcho(int dots, int bright, byte[,] ui)
    {
        var n = dots > 48 ? 38 : 27;
        var len = dots + n;
        var ring = new int[len, 3];
        for (var i = 0; i < len; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                ring[i, c] = ui[0, c] * 64 >> 8;
                if (i < n && n == 27)
                {
                    ring[i, c] = UiEchoShort[i] != 0 ? ui[1, c] * UiEchoShort[i] >> 8 : 0;
                }

                if (i < n && n == 38 && UiEchoLong[i] != 0)
                {
                    ring[i, c] = ui[1, c] * UiEchoLong[i] >> 8;
                }
            }
        }

        var shifted = new int[dots, 3];
        var frames = new List<byte[]>(len);
        var start = n;
        for (var j = 0; j < len; j++)
        {
            for (var i = 0; i < dots; i++)
            {
                var k = start + i;
                if (k >= len)
                {
                    k = k - dots - n;
                }

                for (var c = 0; c < 3; c++)
                {
                    shifted[i, c] = ring[k, c];
                }
            }

            start = start != 0 ? start - 1 : len - 1;
            var f = new byte[dots * 3];
            for (var i = 0; i < dots; i++)
            {
                var m = dots - i - 1; // array3[2] is the mirror of array3[1]
                Put(f, i, Math.Min(255, shifted[i, 0] + shifted[m, 0]) * bright >> 8, Math.Min(255, shifted[i, 1] + shifted[m, 1]) * bright >> 8,
                    Math.Min(255, shifted[i, 2] + shifted[m, 2]) * bright >> 8);
            }

            frames.Add(f);
        }

        return frames;
    }

    /// <summary>
    /// UiMode27 (Heartbeat): 10 beats × 40 frames. In frames 10..29 of each beat the 60 marked dots (shifted per beat) pulse
    /// colour 2 along the envelope, their neighbours at −51 and −102; everything else is colour 1 × 64 &gt;&gt; 8. 400 frames.
    /// </summary>
    private static List<byte[]> UiHeartbeat(int dots, int bright, byte[,] ui)
    {
        var marks = new byte[1000];
        var frames = new List<byte[]>(400);
        for (var i = 0; i < 10; i++)
        {
            Array.Clear(marks);
            foreach (var d in UiBeatDots)
            {
                if (d + UiBeatShift[i] < 1000)
                {
                    marks[d + UiBeatShift[i]] = 1;
                }
            }

            for (var l = 0; l < 40; l++)
            {
                var f = new byte[dots * 3];
                var e = UiBeatEnvelope[l % 10];
                for (var j = 0; j < dots; j++)
                {
                    var c = Scale(ui, 0, 64);
                    if (l / 10 == 1 || l / 10 == 2)
                    {
                        if (marks[j] == 1)
                        {
                            c = Scale(ui, 1, e);
                        }

                        if (j > 0 && j < dots - 1 && l % 10 < 9 && (marks[j + 1] == 1 || marks[j - 1] == 1))
                        {
                            c = Scale(ui, 1, Math.Max(0, e - 51));
                        }

                        if (j > 1 && j < dots - 2 && l % 10 > 0 && l % 10 < 8 && (marks[j + 2] == 1 || marks[j - 2] == 1))
                        {
                            c = Scale(ui, 1, Math.Max(0, e - 102));
                        }
                    }

                    Put(f, j, c.R * bright >> 8, c.G * bright >> 8, c.B * bright >> 8);
                }

                frames.Add(f);
            }
        }

        return frames;
    }

    /// <summary>
    /// UiMode30 (Ripple): 6 passes over the 34 × 48 ring template, each opening with 16 hold frames of row 0 (the IL resets
    /// k while its counter is below 16), stamped at a per-pass offset into a persistent 1000-dot field (earlier passes'
    /// rings survive where the new stamp does not reach) and read from dot 48 on: 6 × 49 = 294 frames.
    /// </summary>
    private static List<byte[]> UiRipple(int dots, int bright, byte[,] ui)
    {
        var template = RippleTemplate.Value;
        var bg = Scale(ui, 0, 128);
        var field = new byte[1000 * 3];
        for (var i = 0; i < 1000; i++)
        {
            Put(field, i, bg);
        }

        var row = new byte[48 * 3];
        var tiles = (dots / 48) + 1;
        var frames = new List<byte[]>(294);
        for (var j = 0; j < 6; j++)
        {
            var holds = 0;
            for (var k = 0; k < 34; k++)
            {
                if (holds < 16)
                {
                    holds++;
                    k = 0;
                }

                for (var i = 0; i < 48; i++)
                {
                    var t = template[(k * 48) + i];
                    Put(row, i, t != 0 ? Scale(ui, 1, UiRippleLevel[t - 1]) : bg);
                }

                for (var l = 0; l < tiles; l++)
                {
                    row.CopyTo(field, ((l * 48) + UiRippleOffset[j]) * 3);
                }

                var f = new byte[dots * 3];
                for (var i = 0; i < dots * 3; i++)
                {
                    f[i] = (byte)(field[(48 * 3) + i] * bright >> 8);
                }

                frames.Add(f);
            }
        }

        return frames;
    }

    private static byte[] LoadTable(string name, int length)
    {
        using var s = typeof(LConnectLoops).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Vendor table {name} is not embedded (tools/codegen).");
        if (s.Length != length)
        {
            throw new InvalidDataException($"Vendor table {name} is {s.Length} bytes, expected {length}.");
        }

        var table = new byte[length];
        s.ReadExactly(table);
        return table;
    }
}
