using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.App.Preview;

/// <summary>
/// The GUI preview runs the very generators the Host runs (same Core assembly, same projection and group concatenation as
/// RenderLoop), so a preview frame is bit-identical to the generator output the Host feeds into each device's output stage.
/// With a <see cref="LoopTiming"/> (Lian Li zones, ARCHITECTURE-D2 §6) it plays the compiled loop: time wraps at the loop
/// period and steps at the loop interval, so the preview shows exactly the frames uploaded to the fans.
/// </summary>
public sealed class PreviewRenderer
{
    public const double Fps = 60.0;

    private readonly IEffectGenerator _generator;
    private readonly Sample[] _samples;
    private readonly Rgb[] _colors;
    private readonly EffectParams _params;
    private readonly Rgb[] _output;
    private readonly (int Offset, BottomEnd Bottom, bool HalfStep)[] _fills = [];
    private readonly RingSampling _sampling;
    private readonly Rgb[] _ring = new Rgb[RingFillMap.Leds];

    /// <param name="zones">
    /// WP-14: the wire zones behind <paramref name="layouts"/> (same order), so H2 blocks calibrated for vertical fill are drawn
    /// with LED 0 at the bottom and a generic translational group renders them as Linear(24) placed by height, as the Host does.
    /// </param>
    public PreviewRenderer(IEffectGenerator generator, IReadOnlyList<ZoneLayout> layouts, EffectParams parameters, PreviewLoop? loop = null,
        LConnectPlan? vendor = null, IReadOnlyList<ZoneDto>? zones = null)
    {
        Loop = vendor is null ? loop : new PreviewLoop(new LoopTiming(vendor.Zones[0].Frames, vendor.Zones[0].IntervalMs), vendor.PeriodSeconds);
        Vendor = vendor;
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        ArgumentNullException.ThrowIfNull(layouts);
        _params = parameters ?? throw new ArgumentNullException(nameof(parameters));
        if (layouts.Count == 0)
        {
            throw new ArgumentException("At least one zone.", nameof(layouts));
        }

        Layouts = layouts;
        Faces = layouts.Select((_, i) => zones is not null && i < zones.Count && IsFaceOnFill(zones[i]) ? new RingFace(zones[i].HalfStep) : (RingFace?)null).ToArray();
        var sampled = layouts;
        var shape = EffectGeometry.Of(generator.Id);
        if (vendor is null && zones is not null && zones.Count(z => z.Family is not null) > 1 && shape.IsTranslational)
        {
            // RenderLoop.Compile's generic group rule (more than one uploaded-loop zone): a vertical-fill H2 renders as Linear(24) along u, then is placed by height
            var fills = new List<(int, BottomEnd, bool)>();
            var substituted = layouts.ToArray();
            var o = 0;
            for (var i = 0; i < layouts.Count; i++)
            {
                if (i < zones.Count && IsFaceOnFill(zones[i]) && layouts[i].LedCount == RingFillMap.Leds)
                {
                    substituted[i] = ZoneLayout.Linear(RingFillMap.Leds);
                    fills.Add((o, shape.Bottom!.Value, zones[i].HalfStep));
                }

                o += layouts[i].LedCount;
            }

            sampled = substituted;
            _fills = [.. fills];
            _sampling = RingFillMap.SamplingFor(generator.Id);
        }

        _samples = sampled.Count == 1 ? Projector.Project(sampled[0]) : GroupLayout.Concat(sampled);
        _colors = parameters.Colors.Count > 0 ? [.. parameters.Colors] : [.. generator.Schema.DefaultColors];
        _output = new Rgb[_samples.Length];
    }

    public IReadOnlyList<ZoneLayout> Layouts { get; }

    /// <summary>Per layout: an H2 block calibrated for vertical fill (drawn with canonical LED 0 at the bottom), or null.</summary>
    public IReadOnlyList<RingFace?> Faces { get; }

    /// <summary>Whether the generic preview remaps an H2 block by height (H2-VERTICAL-FILL §1, generic group path).</summary>
    public bool VerticalFill => _fills.Length > 0;

    private static bool IsFaceOnFill(ZoneDto z) => z.FaceOnFill && string.Equals(z.Family, LConnectLoops.H2, StringComparison.Ordinal);

    /// <summary>The loop being previewed, or null for a streamed (continuous) preview.</summary>
    public PreviewLoop? Loop { get; }

    /// <summary>The L-Connect loops being previewed (WP-11, same planner as the Host's RenderLoop), or null.</summary>
    public LConnectPlan? Vendor { get; }

    /// <summary>Loop period in seconds (0 when not a loop, or a one-frame static loop).</summary>
    public double LoopSeconds => Loop?.PeriodSeconds ?? 0;

    /// <summary>
    /// The L-Connect plan the Host compiles for <paramref name="effectId"/> on <paramref name="zones"/> under
    /// <paramref name="policy"/> (RenderLoop.Compile's rule: every zone a tagged Lian Li TL/H2 loop zone), or null = generic.
    /// </summary>
    public static LConnectPlan? VendorPlanFor(IEffectGenerator generator, EffectParams parameters, IReadOnlyList<ZoneDto> zones, LoopPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(zones);
        if (policy != LoopPolicy.LConnect || zones.Count == 0)
        {
            return null;
        }

        var colors = parameters.Colors.Count > 0 ? parameters.Colors : generator.Schema.DefaultColors;
        return LConnectLoops.TryPlan(generator.Id, parameters, colors,
            zones.Select(z => new LConnectZone(z.Family ?? string.Empty, z.Units, z.LedCount, z.ChainReversed, z.FaceOnFill, z.HalfStep)).ToArray());
    }

    /// <summary>The loop the Host compiles for one UploadedLoop zone with <paramref name="caps"/> (LoopCompiler, same rules).</summary>
    public static PreviewLoop LoopFor(IEffectGenerator generator, EffectParams parameters, Devices.Capabilities caps)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(parameters);
        var resolved = parameters.Colors.Count > 0 ? parameters : parameters with { Colors = [.. generator.Schema.DefaultColors] };
        var period = LoopCompiler.GroupPeriod(LoopCompiler.NaturalPeriod(generator, resolved), [caps], out _);
        return new PreviewLoop(LoopCompiler.TimingFor(period, caps), period);
    }

    /// <summary>Wall time → the loop frame time the fans show at <paramref name="t"/> (identity when not a loop).</summary>
    public double LoopTime(double t)
    {
        if (Loop is not { } loop || loop.Timing.Frames <= 1 || loop.Timing.IntervalMs <= 0)
        {
            return Loop is null ? t : 0;
        }

        var l = loop.Timing;
        var frame = (long)Math.Floor(t * 1000.0 / l.IntervalMs) % l.Frames;
        return LoopCompiler.FrameTime((int)(frame < 0 ? frame + l.Frames : frame), l.Frames, loop.PeriodSeconds);
    }

    public ReadOnlySpan<Sample> Samples => _samples;

    /// <summary>Renders the frame at time <paramref name="t"/> seconds. The returned buffer is reused by the next call.</summary>
    public Rgb[] Render(double t)
    {
        if (Vendor is { } plan)
        {
            // the uploaded frames themselves (layout order), stepping at each zone's own interval
            var o = 0;
            foreach (var z in plan.Zones)
            {
                var leds = z.Rgb.Length / 3 / z.Frames;
                var frame = (int)(((long)Math.Floor(t * 1000.0 / z.IntervalMs) % z.Frames + z.Frames) % z.Frames);
                for (var i = 0; i < leds && o < _output.Length; i++, o++)
                {
                    var b = ((frame * leds) + i) * 3;
                    _output[o] = new Rgb(z.Rgb[b], z.Rgb[b + 1], z.Rgb[b + 2]);
                }
            }

            return _output;
        }

        var ctx = new RenderContext(LoopTime(t), 1.0 / Fps, _colors, _params.Speed, _params.Brightness, _params.Direction, _samples, _params.Seed);
        _generator.Render(in ctx, _output);
        foreach (var (offset, bottom, half) in _fills)
        {
            // the Host's RingFillMap remap (WP-16: shared taps, rounding and Stacking-only interpolation policy)
            _output.AsSpan(offset, RingFillMap.Leds).CopyTo(_ring);
            RingFillMap.Apply(_ring, _output.AsSpan(offset, RingFillMap.Leds), bottom, half, _sampling);
        }

        return _output;
    }

    /// <summary>Wire zone → engine layout. Ring start angle/direction are not on the wire; previews use the defaults.</summary>
    public static ZoneLayout ToLayout(ZoneDto z)
    {
        ArgumentNullException.ThrowIfNull(z);
        return z.Layout switch
        {
            nameof(LayoutKind.Ring) => ZoneLayout.Ring(z.LedCount),
            nameof(LayoutKind.Matrix) when z.Width > 0 && z.Height > 0 => ZoneLayout.Matrix(z.Width, z.Height),
            nameof(LayoutKind.Point) => ZoneLayout.Point,
            _ => ZoneLayout.Linear(Math.Max(1, z.LedCount)),
        };
    }

    /// <summary>Wire assignment → engine params (same parsing as the Host's DtoMapper).</summary>
    public static EffectParams ToParams(AssignmentDto a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var colors = (a.Colors ?? []).Select(ColorText.Parse).ToArray();
        return new EffectParams(colors, a.Speed, a.Brightness, a.Reverse ? Direction.Reverse : Direction.Forward, a.Seed);
    }
}

/// <summary>WP-14: an H2 face drawn by height: canonical LED 0 at the bottom (half-step: the bottom between LEDs 23 and 0).</summary>
public readonly record struct RingFace(bool HalfStep);

/// <summary>A previewed loop: the wire timing (frame steps) and the exact period the frames were compiled over.</summary>
public sealed record PreviewLoop(LoopTiming Timing, double PeriodSeconds);

public static class ColorText
{
    public static string Format(Rgb c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? s, out Rgb color)
    {
        var t = s?.Trim().TrimStart('#') ?? string.Empty;
        if (t.Length == 6 && uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            color = Rgb.FromUInt32(v);
            return true;
        }

        color = default;
        return false;
    }

    public static Rgb Parse(string s) => TryParse(s, out var c) ? c : throw new FormatException($"'{s}' is not #RRGGBB.");
}
