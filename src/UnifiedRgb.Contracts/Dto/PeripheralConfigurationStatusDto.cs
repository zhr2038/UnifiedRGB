namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// Intent (optional, omitted from JSON when null so older readers see the same shape) says who asked for the handoff and
/// carries the owned firmware-maintenance lease, if any; see PeripheralMaintenanceFinish. No current Host sends it, and it
/// never authorises an automatic request: every handoff ends only by an explicit user finish.
/// </summary>
public sealed record PeripheralConfigurationStatusDto(bool ConfigurationMode, string State, string Detail,
    bool InterfacesReleased, DateTimeOffset? UpdatedAt, string? LastError,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PeripheralHandoffIntentDto? Intent = null);
