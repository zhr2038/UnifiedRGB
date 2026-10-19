namespace UnifiedRgb.Contracts.Dto;

/// <summary>Read-only device telemetry (fan RPM, temperature); controllers without telemetry are omitted.</summary>
public sealed record TelemetryDto(string ControllerId, DateTimeOffset Timestamp, IReadOnlyList<int> FanRpm, double? TemperatureC);
