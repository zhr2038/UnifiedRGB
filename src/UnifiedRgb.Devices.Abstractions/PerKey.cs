namespace UnifiedRgb.Devices;

/// <summary>One physically scanned key and its protocol LED record. Coordinates are local to the keyboard, not the case.</summary>
public sealed record PerKeyCell(int LedId, string Key, int Row, int Column);

/// <summary>
/// A local, device-specific key map. <see cref="Verified"/> and <see cref="Complete"/> may be set only after a physical scan;
/// <see cref="Evidence"/> identifies that scan. No map is supplied by the product.
/// </summary>
public sealed record PerKeyMap(
    string Model,
    string Transport,
    IReadOnlyList<PerKeyCell> Keys,
    bool Complete,
    bool Verified,
    string? Evidence);

public enum PerKeyState
{
    Unsupported,
    Disabled,
    MissingMap,
    InvalidMap,
    TransportMismatch,
    Unverified,
    ControlUnverified,
    KeyOrderUnverified,
    Ready,
}

/// <summary>Readiness for genuine independent key colours, never inferred from aggregate-colour support.</summary>
public sealed record PerKeyCapability(PerKeyState State, string Reason, string? Transport, int KeyCount, int MaximumKeys)
{
    public bool Ready => State == PerKeyState.Ready;

    public static PerKeyCapability Unsupported(string reason) => new(PerKeyState.Unsupported, reason, null, 0, 0);
}

public readonly record struct ZonePoint(float U, float Theta, float X, float Y);

public static class PerKeyMaps
{
    public static string? Validate(PerKeyMap? map, int maximumKeys = 128)
    {
        if (map is null) return "map is missing";
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumKeys, 1);
        if (string.IsNullOrWhiteSpace(map.Model)) return "model is missing";
        if (string.IsNullOrWhiteSpace(map.Transport)) return "transport is missing";
        if (map.Keys is null || map.Keys.Count == 0) return "map has no keys";
        if (map.Keys.Count > maximumKeys) return $"map exceeds the {maximumKeys}-key protocol limit";

        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<(int, int)>();
        foreach (var key in map.Keys)
        {
            if (key is null) return "map contains a null key";
            if (key.LedId < 0 || key.LedId > byte.MaxValue) return $"key {key.Key}: LED id {key.LedId} is outside 0..255";
            if (string.IsNullOrWhiteSpace(key.Key)) return $"LED {key.LedId}: key name is missing";
            if (key.Row < 0 || key.Column < 0) return $"key {key.Key}: row and column must be non-negative";
            if (!ids.Add(key.LedId)) return $"key {key.Key}: duplicate LED id {key.LedId}";
            if (!names.Add(key.Key)) return $"key {key.Key}: duplicate key name";
            if (!positions.Add((key.Row, key.Column))) return $"key {key.Key}: duplicate row/column position";
        }

        return null;
    }

    public static PerKeyCapability Evaluate(
        bool enabled,
        PerKeyMap? map,
        string supportedTransport,
        bool controlVerified,
        bool keyOrderVerified,
        int maximumKeys = 128)
    {
        if (string.IsNullOrWhiteSpace(supportedTransport)) throw new ArgumentException("A supported transport is required.", nameof(supportedTransport));
        var count = map?.Keys?.Count ?? 0;
        PerKeyCapability Capability(PerKeyState state, string reason) =>
            new(state, reason, map?.Transport, count, maximumKeys);

        if (!enabled) return Capability(PerKeyState.Disabled, "Per-key output is disabled; aggregate keyboard colour is a separate capability.");
        if (map is null) return Capability(PerKeyState.MissingMap, "No local physical key map is configured.");
        if (Validate(map, maximumKeys) is { } invalid) return Capability(PerKeyState.InvalidMap, invalid);
        if (!string.Equals(map.Transport, supportedTransport, StringComparison.Ordinal))
            return Capability(PerKeyState.TransportMismatch, $"Map transport '{map.Transport}' does not match required '{supportedTransport}'.");
        if (!map.Complete) return Capability(PerKeyState.Unverified, "The physical key scan is incomplete.");
        if (!map.Verified || string.IsNullOrWhiteSpace(map.Evidence))
            return Capability(PerKeyState.Unverified, "The map lacks physical key-scan verification and provenance.");
        if (!controlVerified) return Capability(PerKeyState.ControlUnverified, "Keyboard lighting control has not been verified.");
        if (!keyOrderVerified) return Capability(PerKeyState.KeyOrderUnverified, "Physical key order has not been verified.");
        return Capability(PerKeyState.Ready, "Local map and output gates are ready; optical per-key playback is still not certified.");
    }
}

/// <summary>Converts a sparse physical map to normalized coordinates without filling nonexistent matrix cells.</summary>
public static class PerKeyProjection
{
    public static ZonePoint[] Project(PerKeyMap map)
    {
        if (PerKeyMaps.Validate(map) is { } invalid) throw new ArgumentException(invalid, nameof(map));
        var minRow = map.Keys.Min(k => k.Row);
        var maxRow = map.Keys.Max(k => k.Row);
        var minColumn = map.Keys.Min(k => k.Column);
        var maxColumn = map.Keys.Max(k => k.Column);
        return map.Keys.Select(key =>
        {
            var u = Axis(key.Column, minColumn, maxColumn);
            var y = Axis(key.Row, minRow, maxRow);
            return new ZonePoint(u, 0, u, y);
        }).ToArray();
    }

    private static float Axis(int value, int minimum, int maximum) =>
        minimum == maximum ? 0.5f : (float)(value - minimum) / (maximum - minimum);
}

public sealed record PerKeyFrameCell(byte LedId, string Key, int Row, int Column, Rgb Color);

/// <summary>A software-generated frame for diagnostics. It is not evidence that a device displayed independent key colours.</summary>
public sealed record PerKeyFrame(long Index, IReadOnlyList<PerKeyFrameCell> Keys);

/// <summary>Optional adapter diagnostics for the last genuinely mapped per-key frame; aggregate paths return null.</summary>
public interface IPerKeyFrameSource
{
    PerKeyFrame? ReadPerKeyFrame(ControllerId id);
}
