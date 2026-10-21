using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects.LianLi;

/// <summary>One Lian Li uploaded-loop zone as L-Connect's generators see it: family ("TL"/"H2"), fan count and LED count.</summary>
/// <summary>
/// One target of a vendor loop. <paramref name="ReverseChain"/> (WP-13) is the device's <c>directions[i] = 1</c>: a group
/// body's slice is taken back to front (GetTL/GetH2 <c>num5 = stop + start − num5</c>), i.e. (fan, row, k) →
/// (fans − 1 − fan, row, 12 − k) in canonical chain order. It does not affect per-device or generic loops.
/// <paramref name="FaceOnFill"/>/<paramref name="HalfStep"/> (WP-14, H2-VERTICAL-FILL): an H2 slice of a translational group
/// body is remapped by height with <see cref="RingFillMap"/> after the chain reversal; ignored for TL and per-device bodies.
/// </summary>
public readonly record struct LConnectZone(string Family, int Fans, int LedCount, bool ReverseChain = false, bool FaceOnFill = false, bool HalfStep = false);

/// <summary>One zone's L-Connect loop: frames × LEDs × R,G,B in the zone's <em>layout</em> order (the adapter maps to wire order).</summary>
public sealed record LConnectZoneLoop(int Frames, double IntervalMs, byte[] Rgb)
{
    public double PeriodSeconds => Frames * IntervalMs / 1000.0;
}

/// <summary>
/// The loops of one assignment (target order) and which vendor path produced them. Shared Quick/Sync routes give every
/// zone the same period; the per-device exceptions (UIEffect 2 RainbowMorph, UIEffect 8 Twinkle) do not: each device
/// plays its own family body at its own frame count and interval (e.g. Twinkle TL 200 × 55 ms, H2 200 × 100 ms at speed
/// 0.5), and nothing re-aligns them. <see cref="PeriodSeconds"/> is the shared period when there is one, otherwise the
/// time after which the whole group repeats (least common multiple of the zone periods); <see cref="Summary"/> lists each.
/// </summary>
public sealed record LConnectPlan(string Variant, IReadOnlyList<LConnectZoneLoop> Zones)
{
    /// <summary>Whether every zone has the same period (all shared routes; a per-device route only when the bodies agree).</summary>
    public bool Synchronous => Zones.Count == 0 || Zones.All(z => z.PeriodSeconds.Equals(Zones[0].PeriodSeconds));

    public double PeriodSeconds => Zones.Count == 0 ? 0 : Synchronous ? Zones[0].PeriodSeconds : JointPeriodMs() / 1000.0;

    /// <summary>"N frames × I ms = P s" for a shared period, else one entry per zone (zone order) and the joint repeat.</summary>
    public string Summary => Zones.Count == 0
        ? string.Empty
        : Synchronous
            ? $"{Zones[0].Frames} frames × {Zones[0].IntervalMs:0.##} ms = {Zones[0].PeriodSeconds:0.###} s"
            : string.Join("; ", Zones.Select((z, i) => $"zone {i}: {z.Frames} × {z.IntervalMs:0.##} ms = {z.PeriodSeconds:0.###} s"))
                + $" (independent periods; the group repeats after {PeriodSeconds:0.###} s)";

    private double JointPeriodMs()
    {
        // vendor intervals are whole milliseconds (SpeedType × base), so each period is an integer number of ms
        long l = 1;
        foreach (var z in Zones)
        {
            var p = (long)Math.Round(z.Frames * z.IntervalMs);
            l = p <= 0 ? l : l / Gcd(l, p) * p;
        }

        return l;

        static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);
    }
}

/// <summary>
/// Uploaded-loop compatibility routing and physical projection. Persisted route identifiers and measured interval
/// units are retained for interoperability. Animation pixels come from UnifiedRgb's procedural generators and
/// IndependentLoopBodies, not the historical translated manufacturer bodies. See SOURCE-PROVENANCE.md.
/// </summary>
public static partial class LConnectLoops
{
    public const string Tl = "TL";
    public const string H2 = "H2";

    /// <summary>
    /// NATIVE design §7.3: a streamed (native) zone in a Vendor group; <see cref="LConnectZone.LedCount"/> is its logical LED
    /// count N (1 for an aggregate Point zone, never its physical record count). A shared route slices it as N plain linear
    /// dots (no fan columns, no chain reversal, no ring fill). Shared Stacking instead gives it the deposit-aligned extent
    /// <see cref="StreamedStackingExtent"/> (NATIVE-STACKING-MIXED-ADDENDUM). It has no per-device body and never forms a
    /// plan alone.
    /// </summary>
    public const string Streamed = "Streamed";

    /// <summary><c>RgbEffect.Render</c>: <c>interval_base = 11</c> for every TL body.</summary>
    public const int TlIntervalBase = 11;

    /// <summary><c>RgbEffect(lightSet, ledNum, frameNum, rgbData)</c>: <c>iDefSpeed = 12</c> for UIEffect 1..26.</summary>
    public const int UiIntervalBase = 12;

    /// <summary>The same constructor: <c>iDefSpeed = 18</c> for UIEffect 27..30 (Heartbeat .. Ripple).</summary>
    public const int UiSlowIntervalBase = 18;

    /// <summary><c>initUserColor()</c> default; H2 uses <c>initUserColor(570)</c>.</summary>
    public const int RgbMax = 600;

    public const int H2RgbMax = 570;

    /// <summary>TL bodies lianli.slv3.dll generates itself (<c>GetTLEffect</c>: TLEffect ≤ 7, 19, 29), compatibility identifiers.</summary>
    public static IReadOnlyList<int> TlModes { get; } = [1, 2, 3, 4, 5, 6, 7, 19, 29];

    /// <summary>
    /// H2 bodies selected for the vendor path: <c>slv3.models.H2Effects</c> 1..8 (Rainbow, RainbowMorph, StaticColor,
    /// Breathing, Runway, Meteor, Taichi, Twinkle → H2Mode1..8; DEBUG-SESSION-2 §6.3). Enum 9..11 (Voice, Pump, Bounce →
    /// H2Mode10..12) are not ported and have no application effect of the same name.
    /// </summary>
    public static IReadOnlyList<int> H2Modes { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary><c>GetH2Effect</c>'s interval_base per H2Mode1..8.</summary>
    private static readonly int[] H2IntervalBase = [20, 11, 20, 11, 20, 20, 20, 20];

    /// <summary>
    /// Quick/Sync routes (application effect → <c>UIEffects</c> value, DEBUG-SESSION-2 §6.2). 2 and 8 are the vendor's
    /// per-device exceptions (<c>RFController</c> :1193/:1297); every other value is a shared <c>UiModeN</c> body.
    /// </summary>
    public static IReadOnlyDictionary<string, int> GroupRoutes { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Rainbow"] = 1,
        ["RainbowMorph"] = 2,
        ["StaticColor"] = 3,
        ["Breathing"] = 4,
        ["Runway"] = 5,
        ["Meteor"] = 6,
        ["Stacking"] = 7,
        ["Twinkle"] = 8,
        ["ColorCycle"] = 9,
        ["CoverCycle"] = 10,
        ["Wave"] = 11,
        ["MeteorShower"] = 12,
        ["Tide"] = 13,
        ["ElectricCurrent"] = 14,
        ["MopUp"] = 15,
        ["Disco"] = 16,
        ["Mixing"] = 17,
        ["Warning"] = 22,
        ["Echo"] = 26,
        ["HeartBeat"] = 27,
        ["Ripple"] = 30,
    };


    /// <summary>
    /// <c>LWirelessUtils.ValueToRFLampEffectSpeedType</c>: GUI 0/25/50/75/100 → L1..L5 = 7/6/5/4/3 (DEBUG-SESSION-1 §6).
    /// The vendor only has those five steps; our 0..1 speed is snapped to the nearest (<see cref="VendorSpeed"/>).
    /// </summary>
    public static int SpeedType(float speed) =>
        7 - (int)Math.Round(Math.Clamp(float.IsFinite(speed) ? speed : 0.5f, 0f, 1f) * 4, MidpointRounding.AwayFromZero);

    /// <summary>The vendor speed step <paramref name="speed"/> plays at under the L-Connect policy: 0, 0.25, 0.5, 0.75 or 1.</summary>
    public static float VendorSpeed(float speed) => (7 - SpeedType(speed)) / 4f;

    /// <summary>
    /// <c>BrightnessType</c>: the vendor GUI's 0/25/50/75/100 % are 0/64/128/192/255 (DEBUG-SESSION-2 §5.2), i.e.
    /// <c>min(255, round(b × 256))</c>; values between the five steps follow the same line.
    /// </summary>
    public static int BrightnessType(float brightness) =>
        Math.Min(255, (int)Math.Round(Math.Clamp(float.IsFinite(brightness) ? brightness : 1f, 0f, 1f) * 256, MidpointRounding.AwayFromZero));

    /// <summary><c>iDir</c>: the vendor default 0 is our Forward; Reverse = 1 (spatial write reversal in each body).</summary>
    public static int VendorDirection(Direction direction) => direction == Direction.Reverse ? 1 : 0;

    /// <summary>
    /// Whether <paramref name="zone"/> is a TL chain (1..4 fans × 26) or a bare H2 block (24) the vendor bodies cover, or a
    /// streamed member (≥ 1 LED) of a shared route.
    /// </summary>
    public static bool Supports(LConnectZone zone) => zone.Family switch
    {
        Tl => zone.Fans is >= 1 and <= 4 && zone.LedCount == zone.Fans * 26,
        H2 => zone.LedCount == 24,
        Streamed => zone.LedCount >= 1,
        _ => false,
    };

    /// <summary>The TL vendor mode for <paramref name="effectId"/> (from the family mode table), or null.</summary>
    public static int? TlModeFor(string effectId) => VendorModeFor(Tl, effectId, TlModes);

    public static int? H2ModeFor(string effectId) => VendorModeFor(H2, effectId, H2Modes);

    /// <summary>The interval base a Quick/Sync UIEffect uses: 18 ms for 27..30, else 12 ms.</summary>
    public static int UiIntervalBaseFor(int uiEffect) => uiEffect is >= 27 and <= 30 ? UiSlowIntervalBase : UiIntervalBase;

    /// <summary>
    /// The L-Connect loops for <paramref name="effectId"/> on <paramref name="zones"/> (target order), or null when the vendor
    /// path does not cover this effect/zone set (the caller keeps the generic generator). <paramref name="colors"/> are the
    /// resolved user colours.
    /// </summary>
    public static LConnectPlan? TryPlan(string effectId, EffectParams p, IReadOnlyList<Rgb> colors, IReadOnlyList<LConnectZone> zones) =>
        TryPlan(effectId, p, colors, zones, out _);

    /// <summary>
    /// <see cref="TryPlan(string, EffectParams, IReadOnlyList{Rgb}, IReadOnlyList{LConnectZone})"/>; when it returns null,
    /// <paramref name="reason"/> says why (reported with the generic fallback).
    /// </summary>
    public static LConnectPlan? TryPlan(string effectId, EffectParams p, IReadOnlyList<Rgb> colors, IReadOnlyList<LConnectZone> zones, out string reason) =>
        TryPlan(effectId, p, colors, zones, null, out reason);

    /// <summary>
    /// SPATIAL-PATH: with <paramref name="spans"/> (one per zone, from <see cref="Engine.SpatialPath.Resolve"/>) a shared
    /// UiMode body is rendered once over the path's total length (unchanged body, palette, brightness, direction, interval);
    /// each zone projects its stage — reversed first when bound so — onto its own logical strip and then through the existing
    /// family geometry: TL resampled to its natural dots (13 per fan; shared Stacking exactly 12 per fan, expanded to 13
    /// columns, fan order/ReverseChain unchanged), H2 resampled to 24 then ReverseChain and RingFillMap, a streamed aggregate
    /// Point the stage's coverage mean, a mapped streamed strip midpoint-nearest. Shared Stacking requires every stage length to
    /// be a multiple of 12 and a TL stage of exactly 12 · fans (else null with the reason). With spans a single zone is still a
    /// member of the whole authored path (its siblings may be offline): it gets its stage of the shared body over the full
    /// total, never the per-device family body, so its frames, interval and RGB equal its part of the full plan. Null spans =
    /// the historical route byte for byte (a single zone keeps its per-device body).
    /// </summary>
    public static LConnectPlan? TryPlan(string effectId, EffectParams p, IReadOnlyList<Rgb> colors, IReadOnlyList<LConnectZone> zones,
        IReadOnlyList<Engine.SpatialSpan>? spans, out string reason) =>
        TryPlan(effectId, p, colors, zones, spans, null, out reason);

    /// <summary>
    /// STACK-RETURN: <see cref="TryPlan(string, EffectParams, IReadOnlyList{Rgb}, IReadOnlyList{LConnectZone}, IReadOnlyList{Engine.SpatialSpan}, out string)"/>
    /// with an optional <paramref name="loopReturn"/>: shared Stacking over an explicit path appends the hold and drain tail to
    /// the whole source strip (<see cref="LoopReturnTail"/>) before any stage, family or native slicing, so every zone gets the
    /// same extended frame count and interval; the forward frames stay byte for byte. Any other route returns null with the
    /// reason (never a silently dropped tail). Null = the overload above exactly.
    /// </summary>
    public static LConnectPlan? TryPlan(string effectId, EffectParams p, IReadOnlyList<Rgb> colors, IReadOnlyList<LConnectZone> zones,
        IReadOnlyList<Engine.SpatialSpan>? spans, Engine.LoopReturn? loopReturn, out string reason)
    {
        ArgumentNullException.ThrowIfNull(effectId);
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(zones);
        reason = string.Empty;
        if (zones.Count == 0)
        {
            reason = "no loop zones";
            return null;
        }

        foreach (var bad in zones.Where(z => !Supports(z)))
        {
            reason = string.IsNullOrEmpty(bad.Family)
                ? "zone has no Lian Li family tag"
                : $"{bad.Family} zone ({bad.Fans} fans, {bad.LedCount} LEDs) has no uploaded-loop route (TL 1..4 fans, bare H2 block only)";
            return null;
        }

        if (zones.All(z => z.Family == Streamed))
        {
            reason = "no Lian Li zone carries an uploaded loop";
            return null;
        }

        if (loopReturn is not null && (spans is null || !IsStackingRoute(effectId)))
        {
            // Assignment.ValidateLoopReturn rejects this combination; never silently drop the authored tail
            reason = $"a loop return needs shared Stacking over an explicit spatial path (not {effectId}{(spans is null ? " without a path" : string.Empty)})";
            return null;
        }

        var speed = SpeedType(p.Speed);
        var bright = BrightnessType(p.Brightness);
        var dir = VendorDirection(p.Direction);
        if (zones.Count == 1 && spans is null)
        {
            var loop = PerDevice(zones[0], effectId, speed, bright, dir, colors, out var variant, out reason);
            return loop is null ? null : new LConnectPlan(variant + RingSuffix(zones, null), [loop]);
        }

        if (!GroupRoutes.TryGetValue(effectId, out var ui))
        {
            reason = $"no Quick/Sync UIEffect is established for {effectId} (DEBUG-SESSION-2 §6.2)";
            return null;
        }

        if (ui is 2 or 8)
        {
            if (spans is not null)
            {
                // Assignment.ValidatePath rejects this combination; never silently drop the authored path
                reason = $"UIEffect {ui} is per-device; an explicit spatial path is not supported (use Program)";
                return null;
            }

            if (zones.Any(z => z.Family == Streamed))
            {
                // the engine plans the Lian Li zones alone and renders streamed members live (§7.3 "+program(N)")
                reason = $"UIEffect {ui} is per-device; a streamed zone has no family body";
                return null;
            }

            // UIEffect 2/8: SetUIEffect hands each device its own family body (TLMode2/H2Mode2, TLMode29/H2Mode8)
            var loops = new LConnectZoneLoop[zones.Count];
            for (var i = 0; i < zones.Count; i++)
            {
                var l = PerDevice(zones[i], effectId, speed, bright, dir, colors, out _, out var why);
                if (l is null)
                {
                    reason = $"UIEffect {ui} is per-device; zone {i}: {why}";
                    return null;
                }

                loops[i] = l;
            }

            var bodies = ui == 2 ? "TLMode2/H2Mode2" : "TLMode29/H2Mode8";
            return new LConnectPlan($"UnifiedRgb uploaded UIEffect {ui} (per-device {bodies}){RingSuffix(zones, null)}", loops);
        }

        // WP-15: shared Stacking deposits 12 dots per pass, so it gets a 12-dot-per-fan source extent (fan-aligned placement)
        var aligned = ui == StackingUi;
        if (spans is not null)
        {
            return PlanPath(effectId, ui, aligned, speed, bright, dir, colors, zones, spans, loopReturn, out reason);
        }

        int Extent(LConnectZone z) => aligned ? StackingDots(z) : Dots(z);
        var dots = zones.Sum(Extent);
        var frames = UiModeFrames(ui, dots, bright, dir, UiColours(colors), out var refusal);
        if (frames is null)
        {
            reason = refusal!;
            return null;
        }

        var interval = speed * UiIntervalBaseFor(ui) * ProceduralStride(effectId);
        var result = new LConnectZoneLoop[zones.Count];
        var start = 0;
        var fill = EffectGeometry.Of(effectId);
        var sampling = RingFillMap.SamplingFor(effectId);
        for (var z = 0; z < zones.Count; z++)
        {
            result[z] = new LConnectZoneLoop(frames.Count, interval, SliceGroup(zones[z], frames, start, FillFor(zones[z], fill), aligned, sampling));
            start += Extent(zones[z]);
        }

        var label = aligned ? $"UnifiedRgb uploaded UiMode{ui}, fan-aligned Stacking over {dots} source dots" : $"UnifiedRgb uploaded UiMode{ui} over {dots} dots";
        var padded = aligned ? zones.Count(z => z.Family == Streamed) : 0;
        if (padded > 0)
        {
            // our adaptation (addendum §3): never claimed equal to a vendor group, which has no streamed members
            label += $", {padded} streamed zone(s) padded to 12-dot deposits";
        }

        return new LConnectPlan(label + RingSuffix(zones, fill, sampling), result);
    }

    private static LConnectPlan? PlanPath(string effectId, int ui, bool aligned, int speed, int bright, int dir, IReadOnlyList<Rgb> colors,
        IReadOnlyList<LConnectZone> zones, IReadOnlyList<Engine.SpatialSpan> spans, Engine.LoopReturn? loopReturn, out string reason)
    {
        reason = string.Empty;
        if (spans.Count != zones.Count)
        {
            throw new ArgumentException("One span per zone.", nameof(spans));
        }

        var total = spans[0].Total;
        if (aligned)
        {
            for (var z = 0; z < zones.Count; z++)
            {
                if (spans[z].Length % 12 != 0)
                {
                    reason = $"spatial Stacking stage of zone {z} is {spans[z].Length} source dots, not a multiple of 12";
                    return null;
                }

                if (zones[z].Family == Tl && spans[z].Length != zones[z].Fans * 12)
                {
                    reason = $"spatial Stacking stage of TL zone {z} is {spans[z].Length} source dots, not 12 per fan ({zones[z].Fans * 12})";
                    return null;
                }
            }
        }

        var frames = UiModeFrames(ui, total, bright, dir, UiColours(colors), out var refusal);
        if (frames is null)
        {
            reason = refusal!;
            return null;
        }

        var interval = speed * UiIntervalBaseFor(ui) * ProceduralStride(effectId);
        var tail = string.Empty;
        if (loopReturn is not null)
        {
            // STACK-RETURN: on the whole source strip, before the stage/family/native slicing below; the drain starts where the
            // forward moving bar finished (the high source end for vendor direction 0) and ends where it started
            var hold = loopReturn.HoldFrames(interval);
            var drain = loopReturn.ReturnFrames(interval);
            if ((long)frames.Count + hold + drain > Engine.LoopReturn.MaxExtendedFrames)
            {
                reason = $"loop return: {frames.Count} + {hold} + {drain} frames exceed {Engine.LoopReturn.MaxExtendedFrames}";
                return null;
            }

            frames = LoopReturnTail.Extend(frames, total, hold, drain, loopReturn.WaveWidthDots, loopReturn.WaveStrength, drainHighFirst: dir == 0,
                Engine.LoopReturn.MaxExtendedFrames);
            tail = $", loop return: hold {hold} + reverse-flow drain {drain} frames from the {(dir == 0 ? "high" : "low")} source end";
        }

        var fill = EffectGeometry.Of(effectId);
        var sampling = RingFillMap.SamplingFor(effectId);
        var result = new LConnectZoneLoop[zones.Count];
        for (var z = 0; z < zones.Count; z++)
        {
            var zone = zones[z];
            var span = spans[z];
            if (zone.Family == Streamed)
            {
                // the stage itself is the extent: an aggregate Point gets its coverage mean, a mapped strip midpoint-nearest
                var local = StageStrip(frames, span, span.Length);
                var dst = new byte[frames.Count * zone.LedCount * 3];
                SliceStreamedStacking(local, 0, span.Length, zone.LedCount, dst);
                result[z] = new LConnectZoneLoop(frames.Count, interval, dst);
                continue;
            }

            var natural = aligned ? StackingDots(zone) : Dots(zone);
            result[z] = new LConnectZoneLoop(frames.Count, interval, SliceGroup(zone, StageStrip(frames, span, natural), 0, FillFor(zone, fill), aligned, sampling));
        }

        var stages = spans.Select(sp => sp.Start).Distinct().Count();
        var label = $"UnifiedRgb uploaded UiMode{ui} over an explicit spatial path of {total} source dots ({stages} stage(s) bound here){(aligned ? ", fan-aligned Stacking" : string.Empty)}";
        return new LConnectPlan(label + tail + RingSuffix(zones, fill, sampling), result);
    }

    /// <summary>
    /// SPATIAL-PATH: a zone's stage of every frame as a logical strip of <paramref name="dots"/> dots: the stage is reversed
    /// first when bound so, then resampled midpoint-nearest (local dot j ← stage dot ⌊(2j + 1) · length / (2 · dots)⌋, the
    /// identity when the lengths agree).
    /// </summary>
    private static List<byte[]> StageStrip(IReadOnlyList<byte[]> frames, Engine.SpatialSpan span, int dots)
    {
        var map = new int[dots];
        for (var j = 0; j < dots; j++)
        {
            var r = (int)((((2L * j) + 1) * span.Length) / (2L * dots));
            map[j] = span.Start + (span.Reverse ? span.Length - 1 - r : r);
        }

        var list = new List<byte[]>(frames.Count);
        foreach (var f in frames)
        {
            var d = new byte[dots * 3];
            for (var j = 0; j < dots; j++)
            {
                f.AsSpan(map[j] * 3, 3).CopyTo(d.AsSpan(j * 3));
            }

            list.Add(d);
        }

        return list;
    }

    /// <summary>
    /// WP-14: the bottom end of an H2 slice's vertical fill, or null (TL, flag off, or a non-translational effect). Only the
    /// shared group slicing calls this; per-device bodies are never remapped.
    /// </summary>
    public static BottomEnd? FillFor(LConnectZone zone, EffectShape shape) =>
        zone.Family == H2 && zone.FaceOnFill && shape.IsTranslational ? shape.Bottom : null;

    /// <summary>Whether <see cref="TryPlan(string, EffectParams, IReadOnlyList{Rgb}, IReadOnlyList{LConnectZone})"/> slices one shared UiMode body (not per-device bodies).</summary>
    public static bool IsSharedGroup(string effectId, int zoneCount) => IsSharedGroup(effectId, zoneCount, explicitPath: false);

    /// <summary>
    /// SPATIAL-PATH: with an explicit path even one present zone is a shared-body member (see
    /// <see cref="TryPlan(string, EffectParams, IReadOnlyList{Rgb}, IReadOnlyList{LConnectZone}, IReadOnlyList{Engine.SpatialSpan}?, out string)"/>).
    /// </summary>
    public static bool IsSharedGroup(string effectId, int zoneCount, bool explicitPath) =>
        (zoneCount > 1 || (explicitPath && zoneCount == 1)) && GroupRoutes.TryGetValue(effectId, out var ui) && ui is not (2 or 8);

    /// <summary>Whether a group of <paramref name="effectId"/> is the shared Stacking body (UIEffect 7, 12-dot deposits).</summary>
    public static bool IsStackingRoute(string effectId) => GroupRoutes.TryGetValue(effectId, out var ui) && ui == StackingUi;

    /// <summary>
    /// STACK-RETURN: why <paramref name="loopReturn"/> cannot extend shared Stacking over a path of <paramref name="totalDots"/>
    /// source dots at <paramref name="speed"/> (forward + hold + drain beyond <see cref="Engine.LoopReturn.MaxExtendedFrames"/>),
    /// or null. A path outside the vendor body's own range is not refused here: the plan reports that and falls back exactly as
    /// it does without a return.
    /// </summary>
    public static string? LoopReturnRefusal(float speed, int totalDots, Engine.LoopReturn loopReturn)
    {
        ArgumentNullException.ThrowIfNull(loopReturn);
        if (UiModeRefusal(StackingUi, totalDots) is not null)
        {
            return null;
        }

        var interval = SpeedType(speed) * UiIntervalBaseFor(StackingUi);
        var frames = (long)UiStackFrames(totalDots) + loopReturn.HoldFrames(interval) + loopReturn.ReturnFrames(interval);
        return frames > Engine.LoopReturn.MaxExtendedFrames
            ? $"Stacking over {totalDots} source dots with this loop return needs {frames} frames, beyond {Engine.LoopReturn.MaxExtendedFrames}"
            : null;
    }

    /// <summary>Whether a group of <paramref name="effectId"/> hands every device its own body (UIEffect 2 RainbowMorph, 8 Twinkle).</summary>
    public static bool IsPerDeviceGroupRoute(string effectId) => GroupRoutes.TryGetValue(effectId, out var ui) && ui is 2 or 8;

    /// <summary>
    /// The plan-variant suffix (H2-VERTICAL-FILL §5): "; H2 vertical-fill (bottom=…[, half-step][, interpolated])" when the
    /// remap ran (WP-16: "interpolated" = <see cref="RingSampling.Linear"/>), "; H2 ring" when an H2 has the flag but was not
    /// remapped (non-translational effect or a per-device body).
    /// </summary>
    private static string RingSuffix(IReadOnlyList<LConnectZone> zones, EffectShape? shape, RingSampling sampling = RingSampling.Nearest)
    {
        var flagged = zones.Where(z => z.Family == H2 && z.FaceOnFill).ToArray();
        if (flagged.Length == 0)
        {
            return string.Empty;
        }

        var filled = shape is { } s ? flagged.FirstOrDefault(z => FillFor(z, s) is not null) : default;
        return filled.Family is null
            ? "; H2 ring"
            : $"; H2 vertical-fill (bottom={shape!.Value.Bottom}{(filled.HalfStep ? ", half-step" : string.Empty)}{(sampling == RingSampling.Linear ? ", interpolated" : string.Empty)})";
    }

    private static int? VendorModeFor(string family, string effectId, IReadOnlyList<int> allowed) =>
        FamilyModeTable.Default.Modes(family)
            .FirstOrDefault(m => string.Equals(m.EffectId, effectId, StringComparison.Ordinal) && m.VendorSelected && m.VendorMode is { } v && allowed.Contains(v))
            ?.VendorMode;

    /// <summary>Logical UI dots: <c>RfDevice.UIDot</c> = 13 per TL fan; an H2 block is 24 + FanNum × 8 (bare block: 24).</summary>
    private static int Dots(LConnectZone z) => z.Family switch { Tl => z.Fans * 13, Streamed => z.LedCount, _ => 24 };

    /// <summary>The shared Stacking route (<c>UIEffects</c> 7), the only body with fan-aligned placement (WP-15).</summary>
    private const int StackingUi = 7;

    /// <summary>
    /// WP-15: shared Stacking's source extent, one 12-dot deposit per TL fan (H2 keeps 24); a streamed zone gets
    /// <see cref="StreamedStackingExtent"/> so every extent stays a multiple of 12.
    /// </summary>
    private static int StackingDots(LConnectZone z) => z.Family switch { Tl => z.Fans * 12, Streamed => StreamedStackingExtent(z.LedCount), _ => 24 };

    /// <summary>
    /// NATIVE-STACKING-MIXED-ADDENDUM §2: a streamed zone of N logical LEDs occupies 12 · max(1, ⌊N / 12 + 1/2⌋) source dots of
    /// shared Stacking (the nearest whole number of 12-dot deposits, at least one), so band boundaries never split a TL fan.
    /// </summary>
    public static int StreamedStackingExtent(int logicalLeds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalLeds, 1);
        return 12 * Math.Max(1, (logicalLeds + 6) / 12);
    }

    /// <summary>
    /// WP-15: TL physical column <paramref name="p"/> → source dot for fan-aligned Stacking. The physical row is reversed first
    /// (ReverseChain), then fan = q / 13 and column k = q % 13 take source fan·12 + ⌊(11k + 6) / 12⌋, i.e. the expansion
    /// 0,1,2,3,4,5,6,6,7,8,9,10,11 per fan.
    /// </summary>
    private static int StackingSource(int start, int width, int p, bool reverseChain)
    {
        var q = reverseChain ? width - 1 - p : p;
        return start + ((q / 13) * 12) + ((((q % 13) * 11) + 6) / 12);
    }

    /// <summary>
    /// GetTL / GetH2. Direction 0: TL fan k's both edges take dots start + k·13 + j; H2 takes 24 dots from start. Direction 1
    /// (<see cref="LConnectZone.ReverseChain"/>, IL:73949–73952, :73963–73966): local position p takes dot start + width − 1 − p,
    /// the same for both TL rows.
    /// </summary>
    /// <remarks>
    /// WP-14: with <paramref name="fill"/> (an H2 slice of a translational body, <see cref="FillFor"/>) the 24 sliced dots,
    /// after any chain reversal, are placed by height with <see cref="RingFillMap.Apply"/>; frames and their order are unchanged.
    /// WP-15: with <paramref name="fanAligned"/> (shared Stacking) a TL slice reads <see cref="StackingSource"/> instead of
    /// contiguous dots; H2 slicing is unchanged.
    /// WP-16: <paramref name="sampling"/> selects the H2 fill's strip sampling (<see cref="RingFillMap.SamplingFor"/>); TL
    /// slices and the frames themselves are unaffected.
    /// </remarks>
    private static byte[] SliceGroup(LConnectZone z, IReadOnlyList<byte[]> frames, int start, BottomEnd? fill = null, bool fanAligned = false,
        RingSampling sampling = RingSampling.Nearest)
    {
        var leds = z.LedCount;
        var dst = new byte[frames.Count * leds * 3];
        if (z.Family == Streamed)
        {
            if (fanAligned)
            {
                SliceStreamedStacking(frames, start, StreamedStackingExtent(leds), leds, dst);
                return dst;
            }

            // §7.3: plain linear dots in target order (no fan columns, no chain reversal, no ring fill)
            for (var i = 0; i < frames.Count; i++)
            {
                frames[i].AsSpan(start * 3, leds * 3).CopyTo(dst.AsSpan(i * leds * 3));
            }

            return dst;
        }

        for (var i = 0; i < frames.Count; i++)
        {
            var o = i * leds * 3;
            var width = z.Family == Tl ? z.Fans * 13 : 24;
            var rows = z.Family == Tl ? 2 : 1;
            for (var row = 0; row < rows; row++)
            {
                var d = dst.AsSpan(o + (row * width * 3), width * 3);
                if (fanAligned && z.Family == Tl)
                {
                    for (var p = 0; p < width; p++)
                    {
                        frames[i].AsSpan(StackingSource(start, width, p, z.ReverseChain) * 3, 3).CopyTo(d[(p * 3)..]);
                    }

                    continue;
                }

                if (!z.ReverseChain)
                {
                    frames[i].AsSpan(start * 3, width * 3).CopyTo(d);
                    continue;
                }

                for (var p = 0; p < width; p++)
                {
                    frames[i].AsSpan((start + width - 1 - p) * 3, 3).CopyTo(d[(p * 3)..]);
                }
            }
        }

        if (fill is { } bottom && z.Family == H2)
        {
            return RingFillMap.ApplyFrames(dst, bottom, z.HalfStep, sampling);
        }

        return dst;
    }

    /// <summary>
    /// NATIVE-STACKING-MIXED-ADDENDUM §3: a streamed zone's slice of the deposit-aligned extent [start, start + extent).
    /// N = 1 (aggregate Point): coverage mean per channel, ⌊(Σ + extent/2) / extent⌋, which for the single Stacking colour c and
    /// n lit dots is ⌊(c·n + 6) / 12⌋. N ≥ 2 (mapped): midpoint-nearest, LED j ← dot start + ⌊(2j + 1)·extent / (2N)⌋.
    /// No reversal, fill or half-step (a mapped zone's orientation is its map's index order).
    /// </summary>
    private static void SliceStreamedStacking(IReadOnlyList<byte[]> frames, int start, int extent, int leds, byte[] dst)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var src = frames[i].AsSpan(start * 3, extent * 3);
            var o = i * leds * 3;
            if (leds == 1)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var k = 0; k < extent; k++)
                    {
                        sum += src[(k * 3) + c];
                    }

                    dst[o + c] = (byte)((sum + (extent / 2)) / extent);
                }

                continue;
            }

            for (var j = 0; j < leds; j++)
            {
                var k = (((2 * j) + 1) * extent) / (2 * leds);
                src.Slice(k * 3, 3).CopyTo(dst.AsSpan(o + (j * 3), 3));
            }
        }
    }

    /// <summary><c>GetUIeEffect</c>'s <c>array[6, 3]</c>: up to six user colours, each capped (RGBMax 600), the rest black.</summary>
    private static byte[,] UiColours(IReadOnlyList<Rgb> colors)
    {
        var ui = new byte[Math.Clamp(colors.Count, 1, 6), 3];
        for (var i = 0; i < colors.Count && i < 6; i++)
        {
            var c = CapColour(colors[i], RgbMax);
            (ui[i, 0], ui[i, 1], ui[i, 2]) = (c.R, c.G, c.B);
        }

        return ui;
    }


}
