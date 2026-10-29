using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

public enum PeripheralFinishKind
{
    /// <summary>No handoff is requested: normal keyboard/mouse integration, nothing to finish.</summary>
    NoHandoff,
    /// <summary>A user's manual (Gear Link) request: only the user's explicit resume ends it.</summary>
    ManualIntent,
    /// <summary>The Host does not say who asked (every current Host, or unreadable records): treated exactly like a manual request.</summary>
    OriginUnknown,
    /// <summary>The owned maintenance lease is active inside its TTL: its owner holds the peripherals; no retry or resume.</summary>
    MaintenanceRunning,
    /// <summary>The lease expired, or claims to be active without a valid TTL: ownership no longer holds.</summary>
    LeaseExpired,
    /// <summary>The owner ended its maintenance as failed.</summary>
    MaintenanceFailed,
    /// <summary>
    /// The outcome, lease identity, owner or completion time is not proven (missing, blank, outside the lease or later than
    /// the observation): never treated as finished or ready.
    /// </summary>
    OutcomeUnknown,
    /// <summary>The lease belongs to another transport role, or either role is missing, blank or not a known role (asleep/offline/unconfirmed).</summary>
    RoleUnmatched,
    /// <summary>Owned maintenance completed, but the handoff is in Error with interfaces unreleased: an explicit Enter retry first.</summary>
    RetryRequired,
    /// <summary>Owned maintenance completed and the Host is still entering or resuming: wait for a settled readback.</summary>
    Waiting,
    /// <summary>
    /// Retired: never returned since revision 4 (the automatic resume it allowed was an unscoped ordinary Exit). The value is
    /// kept so every numeric value stays stable; see <see cref="ManualFinishRequired"/>.
    /// </summary>
    AutoFinish,
    /// <summary>
    /// Owned maintenance completed on the same known role and the handoff is Ready with interfaces released: only the user's
    /// explicit finish ends it. Nothing is sent automatically: the Host has no lease-scoped finish, and an ordinary Exit is not
    /// a substitute for one.
    /// </summary>
    ManualFinishRequired,
}

public sealed record PeripheralFinishVerdict(PeripheralFinishKind Kind, string Reason, string? LeaseId = null)
{
    /// <summary>The handoff was requested by an owned firmware maintenance (whatever its outcome), not by a user or an unknown party.</summary>
    public bool FirmwareMaintenance => Kind is not (PeripheralFinishKind.NoHandoff or PeripheralFinishKind.ManualIntent or PeripheralFinishKind.OriginUnknown);
}

/// <summary>
/// Classifies a requested keyboard/mouse handoff for display and for which explicit user action is offered. It never authorises
/// an automatic request: the Host has no atomic lease-scoped finish (INTERFACE-PROPOSAL PM-HOST-1 is not implemented), the
/// current Host never reports an Intent, and an ordinary Exit cannot tell an owned lease from a newer manual request, so every
/// handoff ends only by the user's explicit finish (an ordinary Exit through every Host journal, ownership and vendor guard).
/// Only an owned maintenance (lease and owner stated) that its owner ended as Completed inside the lease lifetime, not later
/// than this observation, on the same known role, with the handoff Ready and its interfaces released, is shown as completed;
/// failed, unknown, expired, incomplete and role-changed leases are never reported as ready or successful.
/// </summary>
public static class PeripheralMaintenanceFinish
{
    public const string ActiveState = "Active", ReadyState = "Ready", ErrorState = "Error", EnteringState = "Entering", ResumingState = "Resuming";

    public static PeripheralFinishVerdict Evaluate(PeripheralConfigurationStatusDto status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status is { ConfigurationMode: false, State: ActiveState })
            return new(PeripheralFinishKind.NoHandoff, "No handoff is requested; normal keyboard/mouse integration.");
        var intent = status.Intent;
        if (intent?.Origin == PeripheralHandoffOrigins.Manual)
            return new(PeripheralFinishKind.ManualIntent, "A user's manual handoff request is kept until the user resumes.");
        if (intent?.Origin != PeripheralHandoffOrigins.FirmwareMaintenance)
            return new(PeripheralFinishKind.OriginUnknown, "The Host does not report who requested the handoff; it is kept like a manual request.");
        if (string.IsNullOrWhiteSpace(intent.LeaseId))
            return new(PeripheralFinishKind.OutcomeUnknown, "The firmware-maintenance request carries no lease identity; ownership is unproven.");
        var lease = intent.LeaseId;
        if (string.IsNullOrWhiteSpace(intent.Owner))
            return new(PeripheralFinishKind.OutcomeUnknown, "The firmware-maintenance lease names no owner; ownership is unproven.", lease);

        switch (intent.LeaseState)
        {
            case PeripheralMaintenanceLeaseStates.Active:
                return intent.ExpiresAt is { } expires && now <= expires
                    ? new(PeripheralFinishKind.MaintenanceRunning, "The owned firmware maintenance is still running.", lease)
                    : new(PeripheralFinishKind.LeaseExpired, "The maintenance lease was not renewed inside a valid TTL; ownership no longer holds.", lease);
            case PeripheralMaintenanceLeaseStates.Expired:
                return new(PeripheralFinishKind.LeaseExpired, "The maintenance lease expired before its owner ended it.", lease);
            case PeripheralMaintenanceLeaseStates.Failed:
                return new(PeripheralFinishKind.MaintenanceFailed, intent.Reason ?? "The owner ended the firmware maintenance as failed.", lease);
            case PeripheralMaintenanceLeaseStates.Completed:
                break;
            default:
                return new(PeripheralFinishKind.OutcomeUnknown, intent.Reason ?? "The firmware-maintenance outcome is unknown.", lease);
        }

        if (intent.EndedAt is not { } ended || intent.ExpiresAt is not { } lifetime || ended > lifetime)
            return new(PeripheralFinishKind.OutcomeUnknown, "Completion is not proven inside the lease lifetime.", lease);
        if (ended > now)
            return new(PeripheralFinishKind.OutcomeUnknown, "The reported completion time is later than this observation; completion is not proven.", lease);
        if (!KnownRole(intent.LeaseRole) || !KnownRole(intent.CurrentRole) || intent.LeaseRole != intent.CurrentRole)
            return new(PeripheralFinishKind.RoleUnmatched,
                $"The lease was scoped to role {RoleText(intent.LeaseRole)}; the current role is {RoleText(intent.CurrentRole)}.", lease);
        return status switch
        {
            { State: ReadyState, ConfigurationMode: true, InterfacesReleased: true } =>
                new(PeripheralFinishKind.ManualFinishRequired, "Owned maintenance completed and the handoff is Ready with interfaces released; only an explicit finish resumes.", lease),
            { State: ErrorState, ConfigurationMode: true, InterfacesReleased: false } =>
                new(PeripheralFinishKind.RetryRequired, "Owned maintenance completed, but the handoff is in Error with interfaces unreleased; resume alone cannot clear it.", lease),
            { State: EnteringState or ResumingState } =>
                new(PeripheralFinishKind.Waiting, "Owned maintenance completed; the Host is still entering or resuming.", lease),
            _ => new(PeripheralFinishKind.OutcomeUnknown, $"Handoff state {status.State} does not prove a clean release.", lease),
        };
    }

    // Roles are exact words; a missing, blank or unrecognised role is never treated as confirmed, even when both sides agree.
    private static bool KnownRole(string? role) => role is PeripheralRoles.Wired or PeripheralRoles.Rf;

    private static string RoleText(string? role) => string.IsNullOrWhiteSpace(role) ? "unconfirmed" : role;
}
