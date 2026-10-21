using UnifiedRgb.Core.Color;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects.LianLi;

/// <summary>
/// Procedural content for the existing uploaded-loop route IDs. The public generator registry supplies all non-stacking
/// pixels. Stacking is derived from a moving interval and a retained suffix, without manufacturer tables or scratch state.
/// This is a redesign, not a claim of clean-room authorship or byte-identical manufacturer animation.
/// </summary>
public static partial class LConnectLoops
{
    // Dense spatial gradients need fewer unique samples to remain inside RF SRAM. The interval multiplier preserves
    // the authored cycle duration; these are generated samples, never decimated frames from an accepted asset.
    private static int ProceduralStride(string effect) => effect is "Wave" or "Tide" or "Ripple" ? 2 : 1;
    public static bool HasTwinkleTable => false;

    public static byte[][]? TlFrames(int mode, int fans, int bright, int direction, IReadOnlyList<Rgb> colors)
    {
        if (fans is < 1 or > 4 || !TlModes.Contains(mode)) return null;
        var effect = FamilyModeTable.Default.Modes(Tl).First(m => m.VendorMode == mode).EffectId;
        if (effect == "Stacking")
        {
            var source = Stack(fans * 12, bright, direction, colors.Count == 0 ? new Rgb(255, 0, 0) : colors[0]);
            var layout = SliceGroup(new(Tl, fans, fans * 26), source, 0, fanAligned: true);
            return layout.Chunk(fans * 26 * 3).ToArray();
        }
        var strip = Procedural(effect, fans * 13, bright, direction, colors);
        return strip.Select(f => f.Concat(f).ToArray()).ToArray();
    }

    public static byte[][]? H2Frames(int mode, int bright, int direction, IReadOnlyList<Rgb> colors) =>
        !H2Modes.Contains(mode) ? null : Procedural(FamilyModeTable.Default.Modes(H2).First(m => m.VendorMode == mode).EffectId,
            24, bright, direction, colors);

    public static byte[][] H2Twinkle(int bright) => Procedural("Twinkle", 24, bright, 0, []);

    public static int H2IntervalMs(int mode, int speedType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mode, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, H2IntervalBase.Length);
        return speedType * H2IntervalBase[mode - 1];
    }

    public static IReadOnlyList<byte[]> UiFrames(int mode, int dots, int bright, int direction, byte[,] ui) =>
        UiModeFrames(mode, dots, bright, direction, ui, out var reason) ?? throw new ArgumentException(reason, nameof(mode));

    public static IReadOnlyList<byte[]>? UiModeFrames(int mode, int dots, int bright, int direction, byte[,] ui, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(ui);
        refusal = UiModeRefusal(mode, dots);
        if (refusal is not null) return null;
        var effect = GroupRoutes.FirstOrDefault(r => r.Value == mode).Key;
        if (effect is null) { refusal = $"Unknown uploaded-loop route {mode}."; return null; }
        var colors = Enumerable.Range(0, ui.GetLength(0)).Select(i => new Rgb(ui[i, 0], ui[i, 1], ui[i, 2])).ToArray();
        return mode == StackingUi ? Stack(dots, bright, direction, colors.FirstOrDefault()) : Procedural(effect, dots, bright, direction, colors);
    }

    // Existing path/budget constraints remain conservative; replacing content does not relax a device transport gate.
    public static string? UiModeRefusal(int mode, int dots)
    {
        var max = mode == 27 ? 520 : 720;
        if (dots < 24 || dots > max) return $"UiMode{mode} source dots must be in 24..{max} (requested {dots}).";
        return mode == StackingUi && UiStackFrames(dots) > 4096
            ? $"UiMode7 over {dots} dots exceeds the 4096-frame compatibility budget."
            : null;
    }

    public static int UiStackFrames(int dots)
    {
        if (dots <= 0) return 0;
        var passes = (dots + 11L) / 12;
        return checked((int)((passes * dots) - (6 * passes * (passes - 1))));
    }

    private static byte[][] Stack(int dots, int bright, int direction, Rgb color)
    {
        var frames = new byte[UiStackFrames(dots)][];
        var next = 0;
        for (var deposited = 0; deposited < dots; deposited += 12)
        {
            var travel = dots - deposited;
            for (var head = 0; head < travel; head++)
            {
                var frame = new byte[dots * 3];
                for (var position = 0; position < dots; position++)
                {
                    if (position >= travel || (position <= head && position > head - 12))
                    {
                        Write(frame, direction == 1 ? dots - 1 - position : position, color, bright);
                    }
                }
                frames[next++] = frame;
            }
        }
        return frames;
    }

    private static byte[][] Procedural(string effect, int dots, int bright, int direction, IReadOnlyList<Rgb> colors)
    {
        var generator = EffectRegistry.Default[effect];
        var palette = colors.Count == 0 ? generator.Schema.DefaultColors.ToArray() : colors.ToArray();
        var p = new EffectParams(palette);
        var seconds = generator is ILoopable loopable ? loopable.LoopSeconds(p) : 6;
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidOperationException("Invalid procedural loop duration.");
        // 200 is the pre-existing generic compilation budget; static content needs one frame.
        var count = seconds == 0 ? 1 : 200 / ProceduralStride(effect);
        var samples = Enumerable.Range(0, dots).Select(i => new Sample(dots == 1 ? 0 : i / (float)(dots - 1), 0, 0, 0)).ToArray();
        var rgb = new Rgb[dots];
        var result = new byte[count][];
        for (var f = 0; f < count; f++)
        {
            var context = new RenderContext(seconds * f / count, seconds / count, palette, .5f, 1,
                Direction.Forward, samples, 1);
            generator.Render(context, rgb);
            var frame = new byte[dots * 3];
            for (var i = 0; i < dots; i++) Write(frame, direction == 1 ? dots - 1 - i : i, rgb[i], bright);
            result[f] = frame;
        }
        return result;
    }

    private static void Write(byte[] frame, int dot, Rgb color, int bright)
    {
        var b = Math.Clamp(bright, 0, 255);
        frame[dot * 3] = (byte)(color.R * b / 256);
        frame[(dot * 3) + 1] = (byte)(color.G * b / 256);
        frame[(dot * 3) + 2] = (byte)(color.B * b / 256);
    }

    public static Rgb CapColour(Rgb c, int max)
    {
        var sum = c.R + c.G + c.B;
        if (sum <= max) return c;
        var ratio = Math.Clamp(max / (double)sum, 0, 1);
        return new Rgb((byte)(c.R * ratio), (byte)(c.G * ratio), (byte)(c.B * ratio));
    }

    // Retained name is a compatibility API. Defaults now come from this application's procedural palette.
    public static byte[,] Slv3UserColour(int tb, IReadOnlyList<Rgb> colors, int max)
    {
        var table = new byte[4, 3];
        for (var i = 0; i < 4; i++)
        {
            var c = CapColour(ColorTables.MapUserColor(colors.ToArray(), i + tb), max);
            (table[i, 0], table[i, 1], table[i, 2]) = (c.R, c.G, c.B);
        }
        return table;
    }

    private static LConnectZoneLoop? PerDevice(LConnectZone zone, string effectId, int speed, int bright, int dir, IReadOnlyList<Rgb> colors,
        out string variant, out string reason)
    {
        var mode = zone.Family == Tl ? TlModeFor(effectId) : H2ModeFor(effectId);
        variant = $"UnifiedRgb procedural {zone.Family}Mode{mode}";
        reason = string.Empty;
        if (mode is null) { reason = $"No {zone.Family} compatibility route for {effectId}."; return null; }
        var frames = zone.Family == Tl ? TlFrames(mode.Value, zone.Fans, bright, dir, colors) : H2Frames(mode.Value, bright, dir, colors);
        if (frames is null) return null;
        return new(frames.Length, (zone.Family == Tl ? speed * TlIntervalBase : H2IntervalMs(mode.Value, speed)) * ProceduralStride(effectId),
            frames.SelectMany(f => f).ToArray());
    }
}
