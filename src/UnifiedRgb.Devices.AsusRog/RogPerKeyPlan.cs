namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// Independently verified local maps for the shared keyboard lighting owner. <see cref="Map"/> defines the zone's physical
/// geometry; a frame uses the current owner's route map, whose LED ids and record order may differ from that geometry map.
/// </summary>
public sealed record RogPerKeyPlan(PerKeyCapability Capability, PerKeyMap? Map, IReadOnlyList<string> Transports)
{
    public bool Ready => Capability.Ready && Map is not null;
    public IReadOnlyDictionary<string, PerKeyMap> RouteMaps { get; init; } = new Dictionary<string, PerKeyMap>();
}

public static class RogPerKeyPlanner
{
    /// <summary>
    /// Null when unconfigured. Each verified route is evaluated independently: a missing RF map does not block a verified USB
    /// map, and vice versa. The active route supplies the geometry when already known; otherwise the first ready route does.
    /// </summary>
    public static RogPerKeyPlan? Plan(RogPerKeyOptions? options, IReadOnlyList<string> verifiedTransports, string? activeTransport = null)
    {
        ArgumentNullException.ThrowIfNull(verifiedTransports);
        if (options is null || !options.Configured) return null;
        if (verifiedTransports.Count == 0)
            return Blocked(PerKeyCapability.Unsupported("No keyboard lighting route is verified."), verifiedTransports);

        var maps = new Dictionary<string, PerKeyMap>(StringComparer.Ordinal);
        var capabilities = new Dictionary<string, PerKeyCapability>(StringComparer.Ordinal);
        foreach (var transport in verifiedTransports)
        {
            var entries = (options.Maps ?? []).Where(m => m is not null && m.Transport == transport).ToArray();
            if (entries.Length > 1)
            {
                capabilities[transport] = new(PerKeyState.InvalidMap, $"{transport}: more than one local map is configured.", transport, 0, RogProtocol.MaximumKeys);
                continue;
            }
            // No map for this transport: show the first configured map so the reason names the mismatch instead of nothing.
            var entry = entries.FirstOrDefault() ?? (options.Maps ?? []).FirstOrDefault(m => m is not null);
            var map = entry?.ToMap();
            var capability = PerKeyMaps.Evaluate(options.Enabled, map, transport, controlVerified: true,
                keyOrderVerified: entry?.KeyOrderVerified == true, RogProtocol.MaximumKeys);
            capabilities[transport] = capability with { Reason = $"{transport}: {capability.Reason}" };
            if (capability.Ready) maps[transport] = map!;
        }

        if (activeTransport is not null && !maps.ContainsKey(activeTransport))
            return Blocked(capabilities.GetValueOrDefault(activeTransport)
                ?? PerKeyCapability.Unsupported($"{activeTransport}: this lighting route is not verified."), verifiedTransports);
        if (maps.Count == 0) return Blocked(capabilities[verifiedTransports[0]], verifiedTransports);
        var selected = activeTransport ?? maps.Keys.First();
        return new(capabilities[selected] with { Transport = string.Join("+", maps.Keys) }, maps[selected], maps.Keys.ToArray())
        {
            RouteMaps = maps,
        };
    }

    private static RogPerKeyPlan Blocked(PerKeyCapability capability, IReadOnlyList<string> transports) => new(capability, null, transports.ToArray());
}
