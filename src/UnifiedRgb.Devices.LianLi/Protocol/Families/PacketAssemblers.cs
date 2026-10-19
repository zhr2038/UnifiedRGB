namespace UnifiedRgb.Devices.LianLi.Protocol.Families;

/// <summary>
/// Per-family wire facts. Pinned from the current lianli.slv3.dll IL (analysis/il, M5b/M5c):
/// <list type="bullet">
/// <item><see cref="RecTypes"/>: the RecType <c>RfDevice</c> derives from the reported fan type (fans 20–26 → 20, 36–39 → 36,
/// 43–50 → 43, …; see <see cref="RfDeviceRecord.RecTypeOfFan"/>) and <c>RFController.SetLightEffect</c> dispatches on;
/// empty = the family is not an RF-relayed device (HS2, 8.8-inch LED and Strimer are separate USB devices).</item>
/// <item><see cref="LedsPerUnit"/>: <c>RfDevice.LedNum</c> per RecType (20 → 40, 59 → 52, 36/43 → 44, 41/126 → 24, 63 → 9,
/// 66 → 88) and the <c>Combing*</c> strides (INFV3 50; conflicts with LedNum 44, see SELF_AUDIT M5c).</item>
/// <item>Channel order R,G,B per LED, frame-major (<c>RgbEffect.GetRgbData</c>, <c>GetP28ByCL</c>) for every family.</item>
/// <item><see cref="WholeUnits"/>: the LED count must be a whole number of units (TL: 26 per fan, <c>RfDevice.LedNum</c>).</item>
/// </list>
/// </summary>
public sealed record FamilyWireSpec(string Family, IReadOnlyList<byte> RecTypes, int LedsPerUnit, int MaxUnits, LayoutKind Shape, int MatrixWidth = 0,
    bool WholeUnits = false)
{
    public bool IsRfRelayed => RecTypes.Count > 0;
}

/// <summary>
/// Frame → one frame of the uncompressed animation L-Connect uploads (<c>rgb_data</c>): ledCount × R,G,B, IL-pinned
/// (<c>Util.GetRgbData</c>). The device type is not in the payload; it is known from the device list. The upload compresses
/// this with <c>Util.CompressYuz</c> (native tinyuz, not in the IL) — see <see cref="RfPackets.RgbUpload"/> and SELF_AUDIT M5c.
/// Allocation-free: callers pass a reusable payload buffer sized by <see cref="PayloadLength"/>.
/// </summary>
public abstract class PacketAssembler
{
    protected PacketAssembler(FamilyWireSpec spec) => Spec = spec ?? throw new ArgumentNullException(nameof(spec));

    public FamilyWireSpec Spec { get; }

    public string Family => Spec.Family;

    public static int PayloadLength(int ledCount) => 3 * ledCount;

    /// <summary>True when <see cref="WireIndex"/> is not the identity (the zone layout's LED order differs from the wire's).</summary>
    public virtual bool ReordersWire => false;

    /// <summary>
    /// The one chain zone of a device (ARCHITECTURE-D2 §1): a single unit is a ring when the family is ring-shaped, otherwise
    /// the chain runs end to end.
    /// </summary>
    public virtual ZoneLayout ChainLayout(int ledCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledCount);
        return Spec.Shape == LayoutKind.Ring && ledCount == Spec.LedsPerUnit ? ZoneLayout.Ring(ledCount) : ZoneLayout.Linear(ledCount);
    }

    /// <summary>Units (fans) in a chain of <paramref name="ledCount"/> LEDs, or 0 when it is not a whole number of units.</summary>
    public int UnitsIn(int ledCount) => Spec.LedsPerUnit > 0 && ledCount > 0 && ledCount % Spec.LedsPerUnit == 0 ? ledCount / Spec.LedsPerUnit : 0;

    /// <summary>Wire position of LED <paramref name="layoutIndex"/> of <see cref="ChainLayout"/>. Identity unless overridden.</summary>
    public virtual int WireIndex(int layoutIndex, int ledCount) => layoutIndex;

    /// <summary>
    /// Reorders whole frames of R,G,B data (frames × <paramref name="ledCount"/> × 3, layout order) into wire order. Returns
    /// the input unchanged when <see cref="ReordersWire"/> is false.
    /// </summary>
    public ReadOnlyMemory<byte> ToWireOrder(ReadOnlyMemory<byte> rgb, int ledCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledCount);
        var frameBytes = 3 * ledCount;
        if (rgb.Length % frameBytes != 0)
        {
            throw new ArgumentException($"Data is not whole frames of {ledCount} LEDs.", nameof(rgb));
        }

        if (!ReordersWire)
        {
            return rgb;
        }

        var src = rgb.Span;
        var dst = new byte[rgb.Length];
        for (var f = 0; f < rgb.Length; f += frameBytes)
        {
            for (var i = 0; i < ledCount; i++)
            {
                src.Slice(f + (3 * i), 3).CopyTo(dst.AsSpan(f + (3 * WireIndex(i, ledCount)), 3));
            }
        }

        return dst;
    }

    /// <summary>Zones for a device reporting <paramref name="ledCount"/> LEDs: one ring/row per unit when it divides evenly.</summary>
    public virtual IReadOnlyList<ZoneLayout> Zones(int ledCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledCount);
        var per = Spec.LedsPerUnit;
        var units = per > 0 && ledCount % per == 0 ? ledCount / per : 0;
        if (units is < 1 || units > Spec.MaxUnits)
        {
            return [ZoneLayout.Linear(ledCount)];
        }

        return Spec.Shape switch
        {
            LayoutKind.Ring => Enumerable.Range(0, units).Select(_ => ZoneLayout.Ring(per)).ToArray(),
            LayoutKind.Matrix when Spec.MatrixWidth > 0 && ledCount % Spec.MatrixWidth == 0 => [ZoneLayout.Matrix(Spec.MatrixWidth, ledCount / Spec.MatrixWidth)],
            _ => [ZoneLayout.Linear(ledCount)],
        };
    }

    public int Assemble(ReadOnlySpan<Rgb> leds, Span<byte> payload)
    {
        var len = PayloadLength(leds.Length);
        if (payload.Length < len)
        {
            throw new ArgumentException("Payload buffer too small.", nameof(payload));
        }

        if (!Spec.IsRfRelayed)
        {
            throw new InvalidOperationException($"{Spec.Family} is not an RF-relayed family.");
        }

        for (var i = 0; i < leds.Length; i++)
        {
            var o = 3 * WireIndex(i, leds.Length);
            (payload[o], payload[o + 1], payload[o + 2]) = (leds[i].R, leds[i].G, leds[i].B);
        }

        return len;
    }
}

public sealed class P28PacketAssembler() : PacketAssembler(new("P28", [63], 9, 12, LayoutKind.Ring));

public sealed class Hs2PacketAssembler() : PacketAssembler(new("HS2", [], 35, 1, LayoutKind.Linear));

public sealed class InfV3PacketAssembler() : PacketAssembler(new("INFV3", [43], 50, 4, LayoutKind.Ring));

public sealed class Slv4PacketAssembler() : PacketAssembler(new("SLv4", [59], 52, 4, LayoutKind.Ring));

public sealed class ClPacketAssembler() : PacketAssembler(new("CL", [41, 126], 24, 4, LayoutKind.Ring));

/// <summary>
/// H2 waterblock (dev type 10/11; M10 re-check against <c>GetWaterBlockEffect</c>/<c>CombingWaterBlockRgbData</c>): LEDs 0..23
/// are the block's ring (<c>H2Mode1</c> rotates mod 24); each attached CL fan appends 24 (8 centre + 16 outer), so
/// <c>LedNum = 24 + FanNum × 24</c>. Only the bare block (24) is driven: the CL-fan tail is not one ring per 24 LEDs.
/// </summary>
public sealed class H2PacketAssembler() : PacketAssembler(new("H2", [10, 11], 24, 1, LayoutKind.Ring, WholeUnits: true));

/// <summary>
/// TL fans (RecType 28 for fan types 27–35, 51 for 51–58; <c>RfDevice</c> IL). Each fan is 26 LEDs on the wire: 13 on the
/// top edge then 13 on the bottom edge (<c>TLMode*</c> fill <c>[fan·26, fan·26+13)</c> for the "Up" scope and
/// <c>[fan·26+13, fan·26+26)</c> for "Down"; <c>UpsideDown</c> swaps the halves). Both edges index the same position
/// <c>fan·13 + k</c> along the chain (UIDot = 13 per fan), so the chain is a 2-row matrix (fans·13 wide, top row first),
/// reordered to fan-major on the wire.
/// </summary>
public sealed class TlPacketAssembler() : PacketAssembler(new("TL", [28, 51], 26, 4, LayoutKind.Matrix, 13, WholeUnits: true))
{
    public const int LedsPerEdge = 13;

    public override bool ReordersWire => true;

    public override IReadOnlyList<ZoneLayout> Zones(int ledCount) => [ChainLayout(ledCount)];

    public override ZoneLayout ChainLayout(int ledCount)
    {
        if (UnitsIn(ledCount) < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ledCount), "A TL chain is a whole number of 26-LED fans.");
        }

        return ZoneLayout.Matrix(ledCount / 2, 2);
    }

    public override int WireIndex(int layoutIndex, int ledCount)
    {
        var width = ledCount / 2;
        var row = layoutIndex / width;
        var col = layoutIndex % width;
        return ((col / LedsPerEdge) * 26) + (row * LedsPerEdge) + (col % LedsPerEdge);
    }
}

public sealed class InfPacketAssembler() : PacketAssembler(new("INF", [36], 44, 4, LayoutKind.Ring));

public sealed class L88PacketAssembler() : PacketAssembler(new("L88", [], 60, 1, LayoutKind.Linear));

public sealed class Slv3PacketAssembler() : PacketAssembler(new("SLv3", [20], 40, 4, LayoutKind.Ring));

public sealed class StrimerPacketAssembler() : PacketAssembler(new("Strimer", [], 20, 12, LayoutKind.Matrix, 20));

public sealed class V150PacketAssembler() : PacketAssembler(new("V150", [66], 88, 1, LayoutKind.Linear));

/// <summary>Maps the RecType a wireless device reports to its family assembler; unknown types are not driven.</summary>
public static class FamilyDetector
{
    public static IReadOnlyList<PacketAssembler> All { get; } =
    [
        new P28PacketAssembler(), new Hs2PacketAssembler(), new InfV3PacketAssembler(), new Slv4PacketAssembler(), new ClPacketAssembler(),
        new H2PacketAssembler(), new InfPacketAssembler(), new L88PacketAssembler(), new Slv3PacketAssembler(), new StrimerPacketAssembler(),
        new V150PacketAssembler(), new TlPacketAssembler(),
    ];

    /// <summary>Families reporting <paramref name="recType"/> (at most one since M5c pinned INF = 36, INFV3 = 43).</summary>
    public static IReadOnlyList<PacketAssembler> ByRecType(byte recType) => All.Where(a => a.Spec.RecTypes.Contains(recType)).ToArray();

    public static PacketAssembler? ByFamily(string family) =>
        All.FirstOrDefault(a => string.Equals(a.Family, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A configured device is driven only when its family is RF-relayed and its LED count is plausible for that family and
    /// fits the upload header's one-byte LED count.
    /// </summary>
    public static bool IsDrivable(PacketAssembler family, int ledNum)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family.Spec.IsRfRelayed && ledNum > 0 && ledNum <= byte.MaxValue && ledNum <= family.Spec.LedsPerUnit * family.Spec.MaxUnits
            && (!family.Spec.WholeUnits || family.UnitsIn(ledNum) > 0);
    }
}
