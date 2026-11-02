namespace UnifiedRgb.Contracts.Dto;

/// <summary>Sanitized local quota observation. Null means unavailable, never zero. No account identity is exposed.</summary>
public sealed record KeyboardCodexWindowDto(int DurationMinutes, string Label, double RemainingPercent, DateTimeOffset ResetsAt);

public sealed record KeyboardCodexUsageDto(bool Enabled, string State, string? Bucket,
    KeyboardCodexWindowDto? Primary, KeyboardCodexWindowDto? Secondary, DateTimeOffset? LastSuccess, string? Error,
    bool? DedicatedPageSupported = null, bool DedicatedPageActive = false, string? OpenActionStatus = null)
{
    /// <summary>Host software routes quota only to its own physical page. Absent on the older mixed-idle Host.</summary>
    public bool IndependentPageRouting { get; init; }
}
