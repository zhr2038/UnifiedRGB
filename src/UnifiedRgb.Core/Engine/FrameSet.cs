using System.Collections.Concurrent;

namespace UnifiedRgb.Core.Engine;

/// <summary>
/// An authored frame set (NATIVE design §7.2 <see cref="ContentStrategy.Frames"/>): <paramref name="Frames"/> frames at
/// <paramref name="IntervalMs"/>, one byte block per target zone in assignment order (<c>Frames × ZoneLedCounts[i] × R,G,B</c>,
/// layout order). Authored at full scale; the assignment's brightness and the controller's output stage apply on delivery.
/// The SCENE-HEARTFLOW asset loader is a later order; this is only the in-memory contract.
/// </summary>
public sealed record FrameSet(string Id, int Frames, double IntervalMs, IReadOnlyList<int> ZoneLedCounts, IReadOnlyList<byte[]> ZoneRgb)
{
    /// <summary>The Lian Li RF master counter rate a <see cref="FrameSetTiming.PhysicalMs"/> interval is expressed in on the wire.</summary>
    public const double DeviceTicksPerSecond = 1600;

    /// <summary>
    /// How <see cref="IntervalMs"/> is interpreted (OPUS-GENERIC-SCENE-PACKAGES). <see cref="FrameSetTiming.Nominal"/> (the default,
    /// every historical frame set) keeps the legacy meaning exactly: the value is also the uploaded header interval.
    /// <see cref="FrameSetTiming.PhysicalMs"/> is opt-in: authored physical milliseconds, uploaded as
    /// <c>IntervalMs × 1600 / 1000</c> raw ticks (60 ms → 96) when exactly representable, else refused (never quantized).
    /// </summary>
    public FrameSetTiming Timing { get; init; } = FrameSetTiming.Nominal;

    public double PeriodSeconds => Frames * IntervalMs / 1000.0;

    /// <summary>
    /// The uploaded header interval of a physical <paramref name="intervalMs"/>: raw RF ticks at <see cref="DeviceTicksPerSecond"/>,
    /// which the header carries as whole units plus hundredths (u16 + byte). False when the value is not exactly representable
    /// there (it would have to be quantized) or out of the header's range.
    /// </summary>
    public static bool TryRawTicks(double intervalMs, out double rawTicks)
    {
        rawTicks = 0;
        if (!double.IsFinite(intervalMs) || intervalMs <= 0)
        {
            return false;
        }

        var centi = intervalMs * DeviceTicksPerSecond / 1000.0 * 100.0;
        var rounded = Math.Round(centi);
        if (rounded < 1 || rounded >= (ushort.MaxValue + 1) * 100.0 || Math.Abs(centi - rounded) > 1e-6)
        {
            return false;
        }

        rawTicks = rounded / 100.0;
        return Math.Abs((rawTicks * 1000.0 / DeviceTicksPerSecond) - intervalMs) <= 1e-9 * Math.Max(1, intervalMs);
    }

    /// <summary>Null when the set is well formed, else why not.</summary>
    public string? Validate()
    {
        if (Frames < 1 || !double.IsFinite(IntervalMs) || IntervalMs <= 0)
        {
            return $"frame set {Id} needs ≥ 1 frame and a positive interval";
        }

        if (ZoneLedCounts.Count != ZoneRgb.Count)
        {
            return $"frame set {Id} has {ZoneLedCounts.Count} zone sizes but {ZoneRgb.Count} zone blocks";
        }

        for (var i = 0; i < ZoneRgb.Count; i++)
        {
            if (ZoneLedCounts[i] < 1 || ZoneRgb[i].Length != Frames * ZoneLedCounts[i] * 3)
            {
                return $"frame set {Id} zone {i} block is {ZoneRgb[i].Length} bytes, not {Frames} × {ZoneLedCounts[i]} × 3";
            }
        }

        return null;
    }
}

/// <summary>How a <see cref="FrameSet"/>'s interval is interpreted.</summary>
public enum FrameSetTiming
{
    /// <summary>Historical: the interval is the nominal header value as well (unchanged behavior).</summary>
    Nominal,

    /// <summary>Opt-in: authored physical milliseconds on the 1600 ticks/s RF master model; exact or refused.</summary>
    PhysicalMs,
}

/// <summary>Where <see cref="ContentStrategy.Frames"/> assignments find their frame sets.</summary>
public interface IFrameSetSource
{
    bool TryGet(string id, out FrameSet frameSet);

    /// <summary>
    /// True when this source OWNS <paramref name="id"/> as strict, immutable content (an installed scene package): such an
    /// assignment never falls back to its effect, is never decimated, and when it cannot be played it is BLOCKED (dark, with the
    /// reason in <see cref="RenderLoop.BlockedContent"/>). Default false: every historical id keeps the effect fallback.
    /// </summary>
    bool Claims(string id) => false;

    /// <summary>Why a claimed id does not resolve right now (diagnostics); null when unknown or resolvable.</summary>
    string? Problem(string id) => null;

    /// <summary>
    /// The exact targets (in order) a claimed id is bound to, or null when the source pins none. A strict assignment whose targets
    /// differ is blocked: content is never retargeted by editing the assignment instead of the binding snapshot.
    /// </summary>
    IReadOnlyList<ZoneRef>? BoundTargets(string id) => null;
}

/// <summary>An in-memory <see cref="IFrameSetSource"/> (tests; the asset loader comes later).</summary>
public sealed class InMemoryFrameSetSource : IFrameSetSource
{
    private readonly ConcurrentDictionary<string, FrameSet> _sets = new(StringComparer.Ordinal);

    public void Add(FrameSet frameSet)
    {
        ArgumentNullException.ThrowIfNull(frameSet);
        var why = frameSet.Validate();
        if (why is not null)
        {
            throw new ArgumentException(why, nameof(frameSet));
        }

        _sets[frameSet.Id] = frameSet;
    }

    public bool TryGet(string id, out FrameSet frameSet) => _sets.TryGetValue(id, out frameSet!);
}

/// <summary>
/// Plays a compiled loop on a streamed zone (NATIVE design §7.5): at loop time <c>tt</c> the frame is
/// <c>k = floor(tt × 1000 / intervalMs) mod frames</c>, held without blending to match the stepped vendor playback.
/// </summary>
public interface IStreamedLoopPlayer
{
    int FrameAt(double tt, double intervalMs, int frames);
}

public sealed class StreamedLoopPlayer : IStreamedLoopPlayer
{
    /// <summary>Absorbs the binary rounding of <c>tt × 1000</c> at exact frame boundaries (0.12 s × 1000 = 119.999…).</summary>
    private const double BoundaryEpsilon = 1e-9;

    public static StreamedLoopPlayer Default { get; } = new();

    public int FrameAt(double tt, double intervalMs, int frames)
    {
        if (frames <= 1 || !(intervalMs > 0) || !double.IsFinite(tt))
        {
            return 0;
        }

        var k = Math.Floor((tt * 1000.0 / intervalMs) + BoundaryEpsilon) % frames;
        if (k < 0)
        {
            k += frames;
        }

        return (int)k;
    }
}
