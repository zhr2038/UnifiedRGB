namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// Who asked for the persisted keyboard/mouse handoff, and the owned firmware-maintenance lease behind it when there is one.
/// A Host would report it from the maintenance owner's own records (INTERFACE-PROPOSAL PM-LEASE-1, PM-HOST-1); no current Host
/// does, so every current status reads as an unknown origin. The application never infers it from Detail/LastError text. A
/// manual request, made before, during or after a lease, is reported as Manual. LeaseRole is the transport role the lease (and
/// its journal) was scoped to; CurrentRole is the role the Host currently sees for that keyboard, or null while it is asleep,
/// offline or unconfirmed; a blank or unrecognised role counts as unconfirmed. A missing intent reads as an unknown origin.
/// </summary>
public sealed record PeripheralHandoffIntentDto(
    string Origin,
    string? LeaseId = null,
    string? Owner = null,
    string? LeaseRole = null,
    string? CurrentRole = null,
    string LeaseState = PeripheralMaintenanceLeaseStates.Unknown,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? EndedAt = null,
    string? Reason = null);

/// <summary>Exact, case-sensitive origins of a handoff request; anything else is treated as unknown.</summary>
public static class PeripheralHandoffOrigins
{
    public const string Manual = "Manual";
    public const string FirmwareMaintenance = "FirmwareMaintenance";
    public const string Unknown = "Unknown";
}

/// <summary>
/// Exact, case-sensitive lease states. Completed means the owner ended its own maintenance with a known good outcome; Failed
/// and Unknown are ended leases without one; Expired is a lease its owner stopped renewing inside its TTL.
/// </summary>
public static class PeripheralMaintenanceLeaseStates
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
    public const string Expired = "Expired";
}

/// <summary>Exact, case-sensitive transport roles of a keyboard; a lease and its journal belong to one role only.</summary>
public static class PeripheralRoles
{
    public const string Wired = "Wired";
    public const string Rf = "Rf";
}
