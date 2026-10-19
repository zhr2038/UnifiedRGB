using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>
/// How a wireless device is physically mounted (WP-11, DEBUG-SESSION-1 §3.2). Viewed from the lit face; rotation is clockwise
/// and applied after <see cref="Reverse"/> (a mirror). A chain setting repeats the same local transform on every fan without
/// changing fan order; <see cref="Fans"/> overrides it per chain slot. Identity by default. Independent of an effect's
/// Reverse, and never inferred from reverse-blade airflow or PWM/RPM flags.
/// </summary>
public sealed class LianLiMounting
{
    public int RotationDegrees { get; set; }

    /// <summary>Mounting mirror: each fan's LED index direction is reversed (TL: along both edges; ring: about LED 0).</summary>
    public bool Reverse { get; set; }

    public IList<LianLiFanMounting> Fans { get; } = new List<LianLiFanMounting>();

    /// <summary>
    /// WP-13 (DEBUG-SESSION-3 §4): whole-chain fan-order reversal, L-Connect's per-device <c>directions[i] = 1</c> in
    /// <c>GetTL</c>/<c>GetH2</c> (IL:73949–73952, :73963–73966; il-RFController.cs:1325–1327). It maps chain position
    /// (fan, row, k) → (fans − 1 − fan, row, 12 − k) when a Quick/Sync group body is sliced, i.e. in canonical RGB before the
    /// wire map, so it is NOT part of <see cref="MountingRevision.Map"/> and is orthogonal to the fan-local <see cref="Reverse"/>
    /// mirror. Per-device bodies (a single target, UIEffect 2/8) and generic loops are not sliced and are unaffected, as in
    /// the vendor.
    /// </summary>
    public bool ReverseChain { get; set; }

    /// <summary>
    /// WP-14 (H2-VERTICAL-FILL §3): fine clockwise ring shift in LEDs, added to the quarter-turn shift
    /// (<c>S = Rotation/90·6 + RingOffsetLeds</c>). Part of the ring wire map; ring (H2) only. The GUI keeps it in [−3, +3].
    /// </summary>
    public int RingOffsetLeds { get; set; }

    /// <summary>WP-14: the block's bottom lies between two LEDs. Selects the s = 0.5 fill table only; not part of the wire map.</summary>
    public bool HalfStep { get; set; }

    /// <summary>WP-14: translational group effects fill the block face by height (<c>RingFillMap</c>). Engine-side; not in the wire map.</summary>
    public bool FaceOnFill { get; set; }

    /// <summary>
    /// Whether the fan-local wire map is the identity (<see cref="ReverseChain"/>, <see cref="HalfStep"/> and
    /// <see cref="FaceOnFill"/> are applied upstream, not here).
    /// </summary>
    public bool IsIdentity => RotationDegrees == 0 && !Reverse && RingOffsetLeds == 0 && Fans.All(f => f.RotationDegrees == 0 && !f.Reverse);

    public LianLiMounting Clone()
    {
        var c = new LianLiMounting
        {
            RotationDegrees = RotationDegrees,
            Reverse = Reverse,
            ReverseChain = ReverseChain,
            RingOffsetLeds = RingOffsetLeds,
            HalfStep = HalfStep,
            FaceOnFill = FaceOnFill,
        };
        foreach (var f in Fans)
        {
            c.Fans.Add(new LianLiFanMounting { Fan = f.Fan, RotationDegrees = f.RotationDegrees, Reverse = f.Reverse });
        }

        return c;
    }
}

/// <summary>Per-fan override of <see cref="LianLiMounting"/>; <see cref="Fan"/> is the 0-based chain slot.</summary>
public sealed class LianLiFanMounting
{
    public int Fan { get; set; }

    public int RotationDegrees { get; set; }

    public bool Reverse { get; set; }
}

/// <summary>
/// One immutable mounting revision of a device: the validated settings and the wire permutation they produce
/// (<c>final[Map[j]] = wire[j]</c>, applied after the family's <see cref="PacketAssembler.WireIndex"/>). Map is null for identity,
/// so identity uploads stay byte-equal to the unmapped path.
/// </summary>
public sealed record MountingRevision(int Revision, LianLiMounting Settings, IReadOnlyList<int>? Map, string Description)
{
    public static MountingRevision Identity { get; } = new(0, new LianLiMounting(), null, "identity");
}

/// <summary>
/// Exact discrete mounting maps (DEBUG-SESSION-1 §3.2 "Exact discrete maps"). Only bijective, fan-local transforms are built:
/// <list type="bullet">
/// <item>TL (13 top + 13 bottom LEDs per fan on the wire): mirror = <c>k → 12 − k</c> on both edges; 180° = edges swapped and
/// <c>k → 12 − k</c>. 90°/270° are refused: two sampled edges are not invariant under a quarter turn, so no permutation exists
/// and a 6/7-LED shift would be fake (needs per-LED geometry, SELF_AUDIT WP-11).</item>
/// <item>Ring families with whole 24-LED units (H2 block): mirror = <c>i → (24 − i) mod 24</c>; quarter turn = 6 indices.</item>
/// </list>
/// Other families accept identity only. Index direction and LED 0 position are not yet confirmed on hardware (§3.4).
/// </summary>
public static class MountingMap
{
    public static IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    /// <summary>Validates <paramref name="m"/> for a device and returns its wire map (null = identity), or an error.</summary>
    public static bool TryBuild(PacketAssembler family, int ledCount, LianLiMounting m, out int[]? map, out string error)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(m);
        map = null;
        error = string.Empty;
        var units = family.UnitsIn(ledCount);
        foreach (var f in m.Fans)
        {
            if (f.Fan < 0 || f.Fan >= Math.Max(units, 1))
            {
                error = $"Fan slot {f.Fan} is outside 0..{Math.Max(units, 1) - 1}.";
                return false;
            }

            if (m.Fans.Count(x => x.Fan == f.Fan) > 1)
            {
                error = $"Fan slot {f.Fan} is listed twice.";
                return false;
            }
        }

        var all = m.Fans.Select(f => (f.RotationDegrees, f.Reverse)).Append((m.RotationDegrees, m.Reverse)).ToArray();
        if (all.Any(x => !Rotations.Contains(x.RotationDegrees)))
        {
            error = "Rotation must be 0, 90, 180 or 270 degrees.";
            return false;
        }

        var ring = family is not TlPacketAssembler && family.Spec.Shape == LayoutKind.Ring && family.Spec.LedsPerUnit == 24 && units >= 1;
        if (m.RingOffsetLeds is < -23 or > 23)
        {
            error = "Ring offset must be within -23..+23 LEDs.";
            return false;
        }

        if (!ring && (m.RingOffsetLeds != 0 || m.HalfStep || m.FaceOnFill))
        {
            error = "Ring offset, half-step and vertical fill apply to ring (H2) devices only.";
            return false;
        }

        if (m.IsIdentity)
        {
            return true;
        }

        var offset = m.RingOffsetLeds;
        Func<int, int, bool, int>? local = family switch
        {
            TlPacketAssembler => TlLocal,
            _ when ring => (i, rot, rev) => RingLocal(i, rot, rev, offset),
            _ => null,
        };
        if (local is null)
        {
            error = $"{family.Family} has no confirmed per-LED mounting map; only identity is accepted.";
            return false;
        }

        if (family is TlPacketAssembler && all.Any(x => x.RotationDegrees is 90 or 270))
        {
            error = "TL fans have two 13-LED edges: a 90°/270° turn has no exact LED permutation (needs per-LED geometry; not faked).";
            return false;
        }

        var per = family.Spec.LedsPerUnit;
        map = new int[units * per];
        for (var fan = 0; fan < units; fan++)
        {
            var o = m.Fans.FirstOrDefault(x => x.Fan == fan);
            var (rot, rev) = o is null ? (m.RotationDegrees, m.Reverse) : (o.RotationDegrees, o.Reverse);
            for (var i = 0; i < per; i++)
            {
                map[(fan * per) + i] = (fan * per) + local(i, rot, rev);
            }
        }

        return true;
    }

    /// <summary>
    /// Applies <paramref name="map"/> to whole wire-order frames (triplets kept intact). Null map → the input unchanged.
    /// </summary>
    public static ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> wire, int ledCount, IReadOnlyList<int>? map)
    {
        if (map is null)
        {
            return wire;
        }

        if (map.Count != ledCount)
        {
            throw new ArgumentException($"Mounting map covers {map.Count} LEDs; the loop has {ledCount}.", nameof(map));
        }

        var frameBytes = 3 * ledCount;
        var src = wire.Span;
        var dst = new byte[wire.Length];
        for (var f = 0; f < wire.Length; f += frameBytes)
        {
            for (var j = 0; j < ledCount; j++)
            {
                src.Slice(f + (3 * j), 3).CopyTo(dst.AsSpan(f + (3 * map[j]), 3));
            }
        }

        return dst;
    }

    public static string Describe(LianLiMounting m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var chain = m.ReverseChain ? "fan order reversed" : null;
        var fill = m.FaceOnFill ? $"vertical fill{(m.HalfStep ? ", half-step" : string.Empty)}" : m.HalfStep ? "half-step" : null;
        chain = chain is null ? fill : fill is null ? chain : $"{chain}; {fill}";
        if (m.IsIdentity)
        {
            return chain ?? "identity";
        }

        var s = $"{m.RotationDegrees}°{(m.Reverse ? " mirrored" : string.Empty)}";
        if (m.RingOffsetLeds != 0)
        {
            s += $" offset {m.RingOffsetLeds.ToString("+0;-0", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        s = m.Fans.Count == 0 ? s : s + "; " + string.Join(", ", m.Fans.OrderBy(f => f.Fan).Select(f => $"fan {f.Fan + 1}: {f.RotationDegrees}°{(f.Reverse ? " mirrored" : string.Empty)}"));
        return chain is null ? s : $"{s}; {chain}";
    }

    /// <summary>TL fan-local wire index <c>row·13 + k</c>: mirror k first, then 180° swaps the edges and mirrors k again.</summary>
    private static int TlLocal(int i, int rotation, bool reverse)
    {
        var (row, k) = (i / TlPacketAssembler.LedsPerEdge, i % TlPacketAssembler.LedsPerEdge);
        if (reverse)
        {
            k = TlPacketAssembler.LedsPerEdge - 1 - k;
        }

        if (rotation == 180)
        {
            (row, k) = (1 - row, TlPacketAssembler.LedsPerEdge - 1 - k);
        }

        return (row * TlPacketAssembler.LedsPerEdge) + k;
    }

    /// <summary>
    /// 24-LED ring: mirror about LED 0, then rotate clockwise by <c>S = rotation/90·6 + offset</c> indices (mod 24, WP-14
    /// H2-VERTICAL-FILL §3).
    /// </summary>
    public static int RingLocal(int i, int rotation, bool reverse, int offset = 0)
    {
        if (reverse)
        {
            i = (24 - i) % 24;
        }

        return ((((i + (rotation / 90 * 6) + offset) % 24) + 24) % 24);
    }
}
