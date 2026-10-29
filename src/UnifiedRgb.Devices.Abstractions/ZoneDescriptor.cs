namespace UnifiedRgb.Devices;

public sealed record ZoneDescriptor(ZoneId Id, string Name, ZoneLayout Layout)
{
    public int LedCount => Layout.LedCount;

    /// <summary>
    /// Vendor device family when the adapter knows one (Lian Li: "TL", "H2", ...); null otherwise. Core uses it to select
    /// vendor-faithful loop bodies (WP-11) without referencing the adapter.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>Physical units in the zone (Lian Li: fans on the chain); 0 = not applicable.</summary>
    public int Units { get; init; }

    /// <summary>
    /// False for a native adapter's aggregate zone (NATIVE design §2): control is verified but no LED/index position is
    /// known, so the zone is one <see cref="LayoutKind.Point"/> whose colour the adapter replicates to every record it owns.
    /// The GUI tags such zones "unmapped"; no spatial claim is possible on them. True for every existing adapter.
    /// </summary>
    public bool Mapped { get; init; } = true;

    /// <summary>What the zone shows. Only <see cref="ZoneMedia.Rgb"/> exists; screens are a later media kind (design §7.8).</summary>
    public ZoneMedia Media { get; init; } = ZoneMedia.Rgb;

    /// <summary>Optional owner-entered position in case coordinates (design §7.6); null = none.</summary>
    public ZonePlacement? Placement { get; init; }

    /// <summary>True per-key readiness; null for zones that do not claim independently addressable keyboard keys.</summary>
    public PerKeyCapability? PerKey { get; init; }

    /// <summary>Sparse local key coordinates in key-map order. Null uses the ordinary dense <see cref="Layout"/> projection.</summary>
    public IReadOnlyList<ZonePoint>? PerKeyPoints { get; init; }
}

/// <summary>Media kind of a zone. The screen seam (design §7.8) adds kinds later; native lighting uses RGB only.</summary>
public enum ZoneMedia
{
    Rgb = 0,
}

/// <summary>
/// Zone position in case coordinates (unit square of the case front, owner-entered, approximate): LED 0 at
/// (<paramref name="OriginX"/>, <paramref name="OriginY"/>), the strip running along (<paramref name="DirX"/>,
/// <paramref name="DirY"/>) for <paramref name="Length"/>. A Point zone sits at its origin (design §7.6).
/// </summary>
public sealed record ZonePlacement(float OriginX, float OriginY, float DirX, float DirY, float Length);
