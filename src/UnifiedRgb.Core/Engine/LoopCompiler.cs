using UnifiedRgb.Core.Color;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <summary>Frames and interval chosen for one UploadedLoop zone.</summary>
/// <param name="IntervalMs">Header interval, rounded to the wire's 0.01 ms.</param>
public readonly record struct LoopTiming(int Frames, double IntervalMs);

/// <summary>One UploadedLoop zone of an assignment: where its LEDs sit in the group, its output stage and its frame count.</summary>
public sealed record LoopTarget(int SourceOffset, int LedCount, OutputStage Stage, int Frames);

/// <summary>
/// Diagnostics of one compilation (Diagnostics and tests): <paramref name="Published"/> UploadedLoop zones sent with
/// <paramref name="EffectIndex"/>, <paramref name="Unchanged"/> kept. With no uploaded-loop zone present all three are 0: a
/// zero-upload report that transmitted nothing and carries only the current variants and truncations.
/// </summary>
/// <param name="Variants">Per loop assignment: which path compiled it ("L-Connect TL per-chain TLMode1", "generic", ...).</param>
public sealed record LoopCompileReport(uint EffectIndex, int Published, int Unchanged, IReadOnlyList<string> Truncated,
    IReadOnlyList<string>? Variants = null);

/// <summary>
/// Renders UploadedLoop zones (ARCHITECTURE-D2 §1–§4): the same generator, through the same Projector/GroupLayout samples
/// and OutputStage as the streamed path, evaluated at <c>t_k = k · P / frames_k</c>, packed frame-major R,G,B.
/// Period P comes from <see cref="ILoopable"/>; generators without it get a fixed seeded loop of <see cref="FallbackFrames"/>
/// frames at the default interval. Every zone shares P (<c>interval_i = P / frames_i</c>), so a group head crosses chains and
/// streamed members with one period. P beyond <c>MaxLoopFrames × </c><see cref="MaxIntervalMs"/> is truncated.
/// </summary>
public sealed class LoopCompiler
{
    /// <summary>Longest per-frame interval used to stretch a long period (D2 §4: 200 × 50 ms = 10 s).</summary>
    public const double MaxIntervalMs = 50;

    /// <summary>Frames of the seeded fallback loop for effects without a period (D2 §1).</summary>
    public const int FallbackFrames = 200;

    public const int DefaultMaxFrames = 200;

    public const double DefaultIntervalMs = 20;

    private readonly Func<DateTime> _wallClock;
    private readonly object _gate = new();
    private uint _lastIndex;
    private DateTime _lastDay;

    public LoopCompiler(Func<DateTime>? wallClock = null) => _wallClock = wallClock ?? (() => DateTime.Now);

    /// <summary>
    /// <c>Util.GetTimeStamp</c>: ms since local midnight, shared by every zone of one compile. Strictly increasing within a
    /// day, so a re-sync of identical content is never mistaken for the loop the device already plays.
    /// </summary>
    public uint NextEffectIndex()
    {
        var now = _wallClock();
        var index = (uint)(now - now.Date).TotalMilliseconds;
        lock (_gate)
        {
            if (now.Date == _lastDay && index <= _lastIndex)
            {
                index = _lastIndex + 1;
            }

            _lastDay = now.Date;
            _lastIndex = index;
            return index;
        }
    }

    public static int MaxFramesOf(Capabilities caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        return caps.MaxLoopFrames > 0 ? Math.Min(caps.MaxLoopFrames, ushort.MaxValue) : DefaultMaxFrames;
    }

    public static double MinIntervalOf(Capabilities caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        return caps.MinIntervalMs > 0 ? caps.MinIntervalMs : DefaultIntervalMs;
    }

    /// <summary>
    /// Natural period of <paramref name="generator"/> for <paramref name="resolved"/> params (Colors = the palette actually
    /// rendered). 0 = time-invariant; null = no period (random effects): use the fallback loop.
    /// </summary>
    public static double? NaturalPeriod(IEffectGenerator generator, EffectParams resolved)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (generator is not ILoopable l)
        {
            return null;
        }

        var p = l.LoopSeconds(resolved);
        return double.IsFinite(p) && p > 0 ? p : 0;
    }

    /// <summary>
    /// The period every zone of a group uses: the natural period (or the fallback loop), capped by the zone that can hold the
    /// least time (<c>MaxLoopFrames × 50 ms</c>). <paramref name="truncated"/> reports a cap.
    /// </summary>
    public static double GroupPeriod(double? natural, IReadOnlyList<Capabilities> zones, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(zones);
        truncated = false;
        if (zones.Count == 0)
        {
            return natural ?? 0;
        }

        var p = natural ?? (FallbackFrames * zones.Min(MinIntervalOf) / 1000.0);
        if (p <= 0)
        {
            return 0;
        }

        var cap = zones.Min(c => MaxFramesOf(c) * Math.Max(MaxIntervalMs, MinIntervalOf(c))) / 1000.0;
        if (p > cap + 1e-9)
        {
            truncated = true;
            return cap;
        }

        return p;
    }

    /// <summary>Frames and interval for one zone: as many frames as the minimum interval allows, at most MaxLoopFrames.</summary>
    public static LoopTiming TimingFor(double periodSeconds, Capabilities caps)
    {
        var minInterval = MinIntervalOf(caps);
        if (periodSeconds <= 0)
        {
            return new LoopTiming(1, minInterval);
        }

        var frames = (int)Math.Floor((periodSeconds * 1000.0 / minInterval) + 1e-9);
        frames = Math.Clamp(frames, 1, MaxFramesOf(caps));
        return new LoopTiming(frames, Math.Round(periodSeconds * 1000.0 / frames, 2));
    }

    /// <summary>Time of frame k of a loop of <paramref name="frames"/> frames over <paramref name="periodSeconds"/>.</summary>
    public static double FrameTime(int k, int frames, double periodSeconds) => frames <= 1 || periodSeconds <= 0 ? 0 : k * periodSeconds / frames;

    /// <summary>
    /// Renders every target's loop: for each distinct frame count, the whole group is rendered at <see cref="FrameTime"/> and
    /// each target receives its slice after its OutputStage, exactly as RenderLoop would at that t.
    /// </summary>
    public static byte[][] Render(
        IEffectGenerator generator,
        Sample[] samples,
        Rgb[] colors,
        float speed,
        float brightness,
        Direction direction,
        uint seed,
        double periodSeconds,
        IReadOnlyList<LoopTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(targets);
        var scratch = new Rgb[samples.Length];
        var result = new byte[targets.Count][];
        for (var i = 0; i < targets.Count; i++)
        {
            result[i] = new byte[targets[i].Frames * targets[i].LedCount * 3];
        }

        foreach (var frames in targets.Select(t => t.Frames).Distinct())
        {
            var dt = frames <= 1 || periodSeconds <= 0 ? 0 : periodSeconds / frames;
            for (var k = 0; k < frames; k++)
            {
                var ctx = new RenderContext(FrameTime(k, frames, periodSeconds), dt, colors, speed, brightness, direction, samples, seed);
                generator.Render(in ctx, scratch);
                for (var i = 0; i < targets.Count; i++)
                {
                    var tg = targets[i];
                    if (tg.Frames != frames)
                    {
                        continue;
                    }

                    var zone = scratch.AsSpan(tg.SourceOffset, tg.LedCount).ToArray();
                    tg.Stage.Apply(zone, brightness);
                    Pack(zone, result[i].AsSpan(k * tg.LedCount * 3));
                }
            }
        }

        return result;
    }

    /// <summary>One frame as <c>Util.GetRgbData</c> lays it out: per LED R, G, B.</summary>
    public static void Pack(ReadOnlySpan<Rgb> leds, Span<byte> dst)
    {
        for (var i = 0; i < leds.Length; i++)
        {
            (dst[3 * i], dst[(3 * i) + 1], dst[(3 * i) + 2]) = (leds[i].R, leds[i].G, leds[i].B);
        }
    }
}
