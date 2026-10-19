namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// A native bring-up session (NATIVE design §6), owned by the pipe connection that began it. <c>ExpiresAt</c> is the lease
/// end; renew before it. <c>HoldBlackOnRelease</c> mirrors the Host option (overnight addendum §4): while true, every release
/// leaves the device in host mode showing black and only black markers are accepted.
/// </summary>
public sealed record BringupSessionDto(
    string ControllerId,
    int LeaseSeconds,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Probes,
    bool HoldBlackOnRelease);

/// <summary>Result of <c>EndBringup</c>: <c>AlreadyEnded</c> when no session was running (a second End is a no-op).</summary>
public sealed record BringupEndDto(string ControllerId, bool AlreadyEnded, ProbeResultDto? Release = null);

/// <summary>A bounded marker: <c>Count</c> records from <c>Start</c> on <c>Channel</c> set to <c>Rgb</c> ("#RRGGBB").</summary>
public sealed record MarkerDto(string ControllerId, int Start, int Count, string Rgb, int Channel = 0);

/// <summary>One recorded step: request bytes, native status (0 = success), response, wall time.</summary>
public sealed record ProbeRowDto(string Step, string RequestHex, int Status, string? ResponseHex, double WallMs, string? Note = null);

/// <summary>
/// Outcome of a probe, marker or release. <c>Fields</c> carries decoded values and the overnight evidence vocabulary
/// (<c>comm</c>, <c>readback</c>, <c>playback</c>, <c>optical</c>, <c>topology</c>, <c>release</c>; addendum §2).
/// <c>SessionEnded</c> is true when a failed step ended the session (release already run; nothing is retried).
/// </summary>
public sealed record ProbeResultDto(
    string ControllerId,
    string Probe,
    bool Ok,
    string Summary,
    IReadOnlyList<ProbeRowDto> Rows,
    IReadOnlyDictionary<string, string> Fields,
    bool SessionEnded = false);

/// <summary>A running program that is not a confirmed lighting writer (NATIVE design §8); informational only.</summary>
public sealed record WriterCandidateDto(string Name, IReadOnlyList<string> Affects, string Reason);
