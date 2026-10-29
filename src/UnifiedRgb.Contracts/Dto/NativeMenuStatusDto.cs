namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// DesiredEnabled is the persisted local request; PreferenceReadable is false when that file could not be read (then the Host
/// treats it as off). SourcePresent: a trusted source is installed. Active: a verified source context is armed right now.
/// State: Off, Unavailable, Paused, Active, Error or Stopped.
/// </summary>
public sealed record NativeMenuStatusDto(bool DesiredEnabled, bool PreferenceReadable, bool SourcePresent, bool Active,
    string State, string Detail, string? LastOutcome, DateTimeOffset? UpdatedAt, string? LastError);
