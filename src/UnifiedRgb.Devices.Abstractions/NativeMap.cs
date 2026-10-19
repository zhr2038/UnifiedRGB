namespace UnifiedRgb.Devices;

/// <summary>
/// One owner-named zone of a mapped native controller (NATIVE design §3.5, §7.6): the record indices (GPU index, Aura LED
/// offset on <paramref name="Channel"/>, ENE LED) in strip order, their layout ("Linear" or "Point") and an optional
/// owner-entered placement. Written by Codex after E3m; nothing here presumes a logo, ring or strip.
/// </summary>
public sealed record NativeZoneMap(string Name, IReadOnlyList<int> Indices, string Layout = "Linear", ZonePlacement? Placement = null, int Channel = 0);

/// <summary>The mapped zones of one native controller with the evidence that produced them.</summary>
public sealed record NativeControllerMap(string? Evidence, IReadOnlyList<NativeZoneMap> Zones);

/// <summary>Read side of <c>native-maps.json</c> (schema 1); adapters read it at discovery. Null = unmapped (aggregate zone).</summary>
public interface INativeMapSource
{
    NativeControllerMap? Get(ControllerId id);
}

/// <summary>Validation shared by the native adapters: a map is used only when every zone is well formed.</summary>
public static class NativeMaps
{
    /// <summary>
    /// Null when <paramref name="map"/> fits <paramref name="records"/> records per channel (indices in range, unique across
    /// zones, every zone non-empty, layout Linear or Point with Point holding one index), else why not.
    /// </summary>
    public static string? Validate(NativeControllerMap map, int records, Func<int, int>? recordsOfChannel = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Zones is null || map.Zones.Count == 0)
        {
            return "map has no zones";
        }

        var seen = new HashSet<(int, int)>();
        foreach (var z in map.Zones)
        {
            if (string.IsNullOrWhiteSpace(z.Name) || z.Indices is null || z.Indices.Count == 0)
            {
                return "every mapped zone needs a name and at least one index";
            }

            if (z.Layout is not ("Linear" or "Point") || (z.Layout == "Point" && z.Indices.Count != 1))
            {
                return $"zone {z.Name}: layout must be Linear, or Point with one index";
            }

            if (z.Channel < 0)
            {
                return $"zone {z.Name}: channel {z.Channel} is negative";
            }

            var limit = recordsOfChannel?.Invoke(z.Channel) ?? (z.Channel == 0 ? records : 0);
            foreach (var i in z.Indices)
            {
                if (i < 0 || i >= limit)
                {
                    return $"zone {z.Name}: index {i} outside 0..{limit - 1} on channel {z.Channel}";
                }

                if (!seen.Add((z.Channel, i)))
                {
                    return $"zone {z.Name}: index {i} on channel {z.Channel} is in more than one zone";
                }
            }
        }

        return null;
    }

    /// <summary>Zone descriptors (<see cref="ZoneDescriptor.Mapped"/> = true) of a validated map, ids 0..n-1 in map order.</summary>
    public static IReadOnlyList<ZoneDescriptor> Zones(NativeControllerMap map) =>
        map.Zones.Select((z, i) => new ZoneDescriptor(
            new ZoneId(i),
            z.Name,
            z.Layout == "Point" ? ZoneLayout.Point : ZoneLayout.Linear(z.Indices.Count)) { Placement = z.Placement }).ToArray();
}
