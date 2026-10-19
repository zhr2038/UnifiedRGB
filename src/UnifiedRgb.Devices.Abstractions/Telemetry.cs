namespace UnifiedRgb.Devices;

/// <summary>Read-only telemetry (bonus feature): fan RPM per channel, temperature.</summary>
public sealed record Telemetry(DateTimeOffset Timestamp, IReadOnlyList<int> FanRpm, double? TemperatureC);

public enum HotplugKind
{
    Arrived,
    Removed,
}

public sealed record HotplugEvent(ControllerId Id, HotplugKind Kind);
