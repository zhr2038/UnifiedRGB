namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// Result of <c>SetTakeover</c>. <c>StoppedServices</c> are restored on clean exit; <c>ProcessesToClose</c> are GUI writers
/// the Host never kills — the user is asked to close them. <c>Error</c> is set when a service could not be stopped.
/// </summary>
public sealed record TakeoverDto(
    string DeviceClass,
    bool Enabled,
    IReadOnlyList<string> StoppedServices,
    IReadOnlyList<string> ProcessesToClose,
    string? Error = null);
