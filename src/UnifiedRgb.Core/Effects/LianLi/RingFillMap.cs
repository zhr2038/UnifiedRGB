using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects.LianLi;

/// <summary>
/// How an H2 vertical fill samples the 24-dot strip (WP-16). <see cref="Nearest"/> is the WP-14 table <c>d_i</c> and the default
/// everywhere; <see cref="Linear"/> blends the two strip dots around the LED's continuous height and is selected only for
/// group Stacking (<see cref="RingFillMap.SamplingFor"/>).
/// </summary>
public enum RingSampling
{
    Nearest,
    Linear,
}

/// <summary>
/// H2 waterblock vertical fill (H2-VERTICAL-FILL §1/§2): a slice-time remap of the H2's 24 strip dots onto its 24 ring LEDs by
/// height. Canonical ring LED i sits at <c>θ_i = (i + s)·15°</c> from the physical bottom (<c>s = 0.5</c> with
/// <c>HalfStep</c>), height <c>h_i = (1 − cos θ_i)/2</c>, and takes strip dot <c>d_i = round(h_i·23)</c> (half up) counted from
/// the strip end that is the bottom. Mirror pairs (<c>i, 24 − i</c>; half-step <c>i, 23 − i</c>) take the same dot, so the
/// fill is mirror-symmetric. Not a mounting map (not bijective, effect-dependent); the vendor body and the frame count are
/// untouched — only the placement of the slice's dots changes.
/// <para>
/// WP-16: <see cref="RingSampling.Linear"/> samples the continuous height <c>x_i = 23·(1 − cos θ_i)/2</c> instead: taps
/// <c>a = ⌊x⌋</c>, <c>b = min(a + 1, 23)</c>, <c>t = x − a</c>, each channel <c>round_half_up((1 − t)·A + t·B)</c>. Taps are
/// computed once per canonical mirror pair and shared with the partner, so pairs stay byte-identical. The calibration
/// marker and wedge (<see cref="MarkerFrame"/>, <see cref="WedgeFrame"/>, <see cref="LitAt"/>) deliberately stay binary
/// nearest-table position fixtures; they are not a preview of interpolated smoothness.
/// </para>
/// </summary>
public static class RingFillMap
{
    public const int Leds = 24;

    /// <summary>Normative §2 table, <c>s = 0</c>: d_i for i = 0..12 (mirror 24 − i).</summary>
    private static readonly int[] Whole = [0, 0, 2, 3, 6, 9, 12, 14, 17, 20, 21, 23, 23];

    /// <summary>Normative §2 table, <c>s = 0.5</c>: d_i for i = 0..11 (mirror 23 − i).</summary>
    private static readonly int[] Half = [0, 1, 2, 4, 7, 10, 13, 16, 19, 21, 22, 23];

    private static readonly int[] WholeRing = Expand(false);
    private static readonly int[] HalfRing = Expand(true);

    private static readonly Tap[] WholeTaps = ExpandTaps(false);
    private static readonly Tap[] HalfTaps = ExpandTaps(true);

    /// <summary>A linear-sampling tap: strip dots <c>A</c> and <c>B</c> (0 = bottom end) and the weight <c>T</c> of B.</summary>
    private readonly record struct Tap(int A, int B, double T);

    /// <summary>
    /// WP-16 policy: group Stacking (the accumulating fill whose coarse height steps the owner reported) samples linearly;
    /// every other effect keeps <see cref="RingSampling.Nearest"/>.
    /// </summary>
    public static RingSampling SamplingFor(string effectId) =>
        string.Equals(effectId, "Stacking", StringComparison.Ordinal) ? RingSampling.Linear : RingSampling.Nearest;

    /// <summary>The strip dot (0 = bottom end) each canonical ring LED 0..23 takes.</summary>
    public static IReadOnlyList<int> Dots(bool halfStep) => halfStep ? HalfRing : WholeRing;

    /// <summary>The §2 derivation <c>round((1 − cos((i + s)·15°))/2 · 23)</c>, half up (a tolerance absorbs cos(90°) ≠ 0 in doubles).</summary>
    public static int Derive(int i, bool halfStep)
    {
        var theta = (i + (halfStep ? 0.5 : 0)) * 15.0 * Math.PI / 180.0;
        var x = (1 - Math.Cos(theta)) / 2 * 23;
        return (int)Math.Floor(x + 0.5 + 1e-9);
    }

    /// <summary>The LED's mirror partner: <c>24 − i</c> (mod 24), or <c>23 − i</c> with half-step.</summary>
    public static int Mirror(int i, bool halfStep) => halfStep ? 23 - i : (Leds - i) % Leds;

    /// <summary>
    /// Remaps one frame: canonical LED i takes slice dot <c>d_i</c> (<paramref name="bottom"/> = Low) or <c>23 − d_i</c>
    /// (High). <paramref name="slice"/> and <paramref name="dst"/> are 24 × R,G,B and must not overlap.
    /// </summary>
    public static void Apply(ReadOnlySpan<byte> slice, Span<byte> dst, BottomEnd bottom, bool halfStep) =>
        Apply(slice, dst, bottom, halfStep, RingSampling.Nearest);

    /// <summary>
    /// <see cref="Apply(ReadOnlySpan{byte}, Span{byte}, BottomEnd, bool)"/> with an explicit <paramref name="sampling"/>;
    /// <see cref="RingSampling.Linear"/> blends each LED's two taps per channel (WP-16).
    /// </summary>
    public static void Apply(ReadOnlySpan<byte> slice, Span<byte> dst, BottomEnd bottom, bool halfStep, RingSampling sampling)
    {
        if (slice.Length != Leds * 3 || dst.Length != Leds * 3)
        {
            throw new ArgumentException("A ring fill frame is 24 LEDs × 3 bytes.");
        }

        if (sampling == RingSampling.Linear)
        {
            var taps = halfStep ? HalfTaps : WholeTaps;
            for (var i = 0; i < Leds; i++)
            {
                var (a, b) = Strip(taps[i], bottom);
                for (var ch = 0; ch < 3; ch++)
                {
                    dst[(i * 3) + ch] = Blend(slice[(a * 3) + ch], slice[(b * 3) + ch], taps[i].T);
                }
            }

            return;
        }

        var dots = halfStep ? HalfRing : WholeRing;
        for (var i = 0; i < Leds; i++)
        {
            var d = bottom == BottomEnd.Low ? dots[i] : Leds - 1 - dots[i];
            slice.Slice(d * 3, 3).CopyTo(dst[(i * 3)..]);
        }
    }

    /// <summary>
    /// The same remap on colours (the App preview's generic path), sharing the taps and rounding of the byte path.
    /// <paramref name="slice"/> and <paramref name="dst"/> are 24 colours and must not overlap.
    /// </summary>
    public static void Apply(ReadOnlySpan<Rgb> slice, Span<Rgb> dst, BottomEnd bottom, bool halfStep, RingSampling sampling)
    {
        if (slice.Length != Leds || dst.Length != Leds)
        {
            throw new ArgumentException("A ring fill frame is 24 LEDs.");
        }

        if (sampling == RingSampling.Linear)
        {
            var taps = halfStep ? HalfTaps : WholeTaps;
            for (var i = 0; i < Leds; i++)
            {
                var (a, b) = Strip(taps[i], bottom);
                var t = taps[i].T;
                dst[i] = new Rgb(Blend(slice[a].R, slice[b].R, t), Blend(slice[a].G, slice[b].G, t), Blend(slice[a].B, slice[b].B, t));
            }

            return;
        }

        var dots = halfStep ? HalfRing : WholeRing;
        for (var i = 0; i < Leds; i++)
        {
            dst[i] = slice[bottom == BottomEnd.Low ? dots[i] : Leds - 1 - dots[i]];
        }
    }

    /// <summary>Remaps every frame of a 24-LED loop (frames × 24 × 3); frame count and order are unchanged.</summary>
    public static byte[] ApplyFrames(ReadOnlySpan<byte> rgb, BottomEnd bottom, bool halfStep) =>
        ApplyFrames(rgb, bottom, halfStep, RingSampling.Nearest);

    /// <summary><see cref="ApplyFrames(ReadOnlySpan{byte}, BottomEnd, bool)"/> with an explicit <paramref name="sampling"/>.</summary>
    public static byte[] ApplyFrames(ReadOnlySpan<byte> rgb, BottomEnd bottom, bool halfStep, RingSampling sampling)
    {
        if (rgb.Length % (Leds * 3) != 0)
        {
            throw new ArgumentException("Ring fill data must be whole 24-LED frames.", nameof(rgb));
        }

        var dst = new byte[rgb.Length];
        for (var o = 0; o < rgb.Length; o += Leds * 3)
        {
            Apply(rgb.Slice(o, Leds * 3), dst.AsSpan(o, Leds * 3), bottom, halfStep, sampling);
        }

        return dst;
    }

    /// <summary>
    /// The canonical LEDs a fill at progress <paramref name="p"/> (0..1 of the strip from the bottom) lights: strip dots
    /// 0..round(p·23) are lit, so LED i is lit when <c>d_i ≤ round(p·23)</c>.
    /// </summary>
    public static IReadOnlyList<int> LitAt(double p, bool halfStep)
    {
        var cut = (int)Math.Floor((Math.Clamp(p, 0, 1) * 23) + 0.5 + 1e-9);
        var dots = halfStep ? HalfRing : WholeRing;
        return Enumerable.Range(0, Leds).Where(i => dots[i] <= cut).ToArray();
    }

    /// <summary>Calibration step 1 (§3): canonical LED 0 red (half-step: LEDs 23 and 0), LEDs 1..3 dim green, one frame.</summary>
    public static byte[] MarkerFrame(bool halfStep)
    {
        var f = new byte[Leds * 3];
        for (var i = 1; i <= 3; i++)
        {
            f[(i * 3) + 1] = 32;
        }

        f[0] = 255;
        if (halfStep)
        {
            f[23 * 3] = 255;
        }

        return f;
    }

    /// <summary>Calibration step 2 (§3): the fill at <c>p = 0.25</c> (the LEDs of <see cref="LitAt"/>) in white, one frame.</summary>
    public static byte[] WedgeFrame(bool halfStep)
    {
        var f = new byte[Leds * 3];
        foreach (var i in LitAt(0.25, halfStep))
        {
            f[i * 3] = f[(i * 3) + 1] = f[(i * 3) + 2] = 200;
        }

        return f;
    }

    /// <summary>
    /// GUI normalisation (§3): the effective clockwise shift <c>S = rotation/90·6 + offset</c> re-expressed with
    /// <c>offset ∈ [−3, +2]</c> (a ±3 tie keeps −3) and rotation in {0, 90, 180, 270}. S mod 24 is preserved.
    /// </summary>
    public static (int RotationDegrees, int RingOffsetLeds) NormalizeShift(int rotationDegrees, int ringOffsetLeds)
    {
        var s = ((((rotationDegrees / 90 * 6) + ringOffsetLeds) % Leds) + Leds) % Leds;
        var quarter = (s + 3) / 6 % 4;
        var offset = s - ((s + 3) / 6 * 6);
        return (quarter * 90, offset);
    }

    /// <summary>A tap's strip dots as slice indices: the bottom end is slice 0 (Low) or slice 23 (High).</summary>
    private static (int A, int B) Strip(Tap tap, BottomEnd bottom) =>
        bottom == BottomEnd.Low ? (tap.A, tap.B) : (Leds - 1 - tap.A, Leds - 1 - tap.B);

    /// <summary>
    /// One channel: <c>round_half_up((1 − t)·a + t·b)</c> clamped to 0..255; equal taps return the value exactly, and a small
    /// tolerance keeps exact midpoints (x.5 up to double error) rounding up deterministically.
    /// </summary>
    private static byte Blend(byte a, byte b, double t)
    {
        if (a == b)
        {
            return a;
        }

        var v = ((1 - t) * a) + (t * b);
        return (byte)Math.Clamp((int)Math.Floor(v + 0.5 + 1e-9), 0, 255);
    }

    /// <summary>
    /// Linear taps for every LED: computed for the canonical half of each mirror pair (i = 0..12 whole, 0..11 half-step) and
    /// copied to its partner. A height within 1e-9 of an integer snaps to it (cos(90°) etc. are not exact in doubles).
    /// </summary>
    private static Tap[] ExpandTaps(bool halfStep)
    {
        var ring = new Tap[Leds];
        var count = halfStep ? Half.Length : Whole.Length;
        for (var i = 0; i < count; i++)
        {
            var theta = (i + (halfStep ? 0.5 : 0)) * 15.0 * Math.PI / 180.0;
            var x = (1 - Math.Cos(theta)) / 2 * (Leds - 1);
            var nearest = Math.Round(x);
            if (Math.Abs(x - nearest) < 1e-9)
            {
                x = nearest;
            }

            x = Math.Clamp(x, 0, Leds - 1);
            var a = (int)Math.Floor(x);
            var tap = new Tap(a, Math.Min(a + 1, Leds - 1), x - a);
            ring[i] = tap;
            ring[Mirror(i, halfStep)] = tap;
        }

        return ring;
    }

    private static int[] Expand(bool halfStep)
    {
        var ring = new int[Leds];
        var table = halfStep ? Half : Whole;
        for (var i = 0; i < table.Length; i++)
        {
            ring[i] = table[i];
            ring[Mirror(i, halfStep)] = table[i];
        }

        return ring;
    }
}
