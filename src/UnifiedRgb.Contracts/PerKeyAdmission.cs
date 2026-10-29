using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

public enum PerKeyAdmissionVerdict
{
    /// <summary>The zone claims no per-key representation (ordinary or aggregate colour); per-key admission does not apply.</summary>
    NotPerKey = 0,
    /// <summary>The per-key capability is not Ready (unsupported, disabled, missing/invalid map, unverified, ...).</summary>
    CapabilityBlocked,
    RestorationUnproven,
    RestorationFailed,
    Admitted,
    /// <summary>
    /// Restoration is reported Verified, but the itemised capability is absent, not Verified, or does not verify every
    /// required component exactly once.
    /// </summary>
    RestorationIncomplete,
    /// <summary>The capability names another contract version, another or no transport, no scope, or a component v1 does not define.</summary>
    RestorationMismatch,
}

public sealed record PerKeyAdmissionResult(PerKeyAdmissionVerdict Verdict, string Reason)
{
    public bool AllowsOutput => Verdict is PerKeyAdmissionVerdict.NotPerKey or PerKeyAdmissionVerdict.Admitted;
}

/// <summary>
/// Client-side admission before any request that would make a zone's per-key representation output: the capability must be
/// Ready, restoration Verified AND the <see cref="PerKeyRestorationContract.Version"/> capability must verify every required
/// component (effect, inactive static bank, brightness, profile, RF scope) for the same transport and a stated scope. It is
/// checked before the request is sent, never left to a release-time failure. The zone's shape decides whether output would be
/// per-key: an unmapped Point zone only carries aggregate colour whatever its per-key status, so aggregate scenes, followers
/// and lights-off are untouched. The Host remains the write gate; this never makes a device or map supported.
/// </summary>
public static class PerKeyAdmission
{
    public const string ReadyState = "Ready";
    public const string AggregateLayout = "Point";

    private const string DefaultFailed = "The original-lighting snapshot or restore preflight failed.";
    private const string DefaultUnproven = "Complete original-lighting restoration is not proven.";

    /// <summary>The aggregate whole-device shape: one unmapped Point whose single colour the adapter replicates.</summary>
    public static bool IsAggregate(ZoneDto zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return !zone.Mapped && zone.Layout == AggregateLayout;
    }

    /// <summary>A zone whose output would be per-key: it reports per-key status and is not the aggregate shape.</summary>
    public static bool IsPerKeyTarget(ZoneDto zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.PerKey is not null && !IsAggregate(zone);
    }

    public static PerKeyAdmissionResult Evaluate(ZoneDto zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return IsPerKeyTarget(zone) ? EvaluateStatus(zone.PerKey!) : new(PerKeyAdmissionVerdict.NotPerKey, "Not a per-key target.");
    }

    /// <summary>
    /// The verdict a per-key status would get as a per-key target, whatever the zone's shape (readiness text uses it for the
    /// aggregate zone too). Any Failed word wins over every other restoration word; nothing unknown is read as Verified.
    /// </summary>
    public static PerKeyAdmissionResult EvaluateStatus(PerKeyStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State != ReadyState)
            return new(PerKeyAdmissionVerdict.CapabilityBlocked, $"Per-key capability is {status.State}: {status.Reason}");
        if (status.Restoration == PerKeyRestorationStates.Failed)
            return new(PerKeyAdmissionVerdict.RestorationFailed, status.RestorationReason ?? DefaultFailed);

        var capability = status.RestorationCapability;
        var components = Components(capability);
        var failed = components.Where(c => c.State == PerKeyRestorationStates.Failed).Select(c => c.Component).ToArray();
        if (capability?.State == PerKeyRestorationStates.Failed || failed.Length > 0)
            return new(PerKeyAdmissionVerdict.RestorationFailed,
                $"The restoration capability reports a failure{(failed.Length > 0 ? $" ({string.Join(", ", failed)})" : string.Empty)}: {capability!.Reason}");
        if (status.Restoration != PerKeyRestorationStates.Verified)
            return new(PerKeyAdmissionVerdict.RestorationUnproven, status.RestorationReason ?? DefaultUnproven);

        if (capability is null)
            return new(PerKeyAdmissionVerdict.RestorationIncomplete,
                $"Restoration is reported Verified without the itemised {PerKeyRestorationContract.Version} capability; {Join(PerKeyRestorationContract.Required)} are not proven.");
        if (capability.Contract != PerKeyRestorationContract.Version)
            return new(PerKeyAdmissionVerdict.RestorationMismatch,
                $"Restoration contract '{capability.Contract}' is not {PerKeyRestorationContract.Version}.");
        if (status.Transport is null || capability.Transport != status.Transport)
            return new(PerKeyAdmissionVerdict.RestorationMismatch,
                $"Restoration transport '{capability.Transport}' does not match the per-key transport '{status.Transport}'.");
        if (string.IsNullOrWhiteSpace(capability.Scope))
            return new(PerKeyAdmissionVerdict.RestorationMismatch, "The restoration capability states no device scope.");
        if (capability.Components?.Any(c => c is null) == true)
            return new(PerKeyAdmissionVerdict.RestorationMismatch, "The restoration capability carries an empty component entry.");
        var unknown = components.Select(c => c.Component).Where(n => !PerKeyRestorationContract.Required.Contains(n, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            return new(PerKeyAdmissionVerdict.RestorationMismatch,
                $"Component(s) {Join(unknown.Select(n => $"'{n}'"))} are not defined by {PerKeyRestorationContract.Version}.");

        var unproven = UnprovenComponents(status);
        if (unproven.Count > 0)
            return new(PerKeyAdmissionVerdict.RestorationIncomplete, $"Original-lighting restoration is incomplete: {Join(unproven)} not verified.");
        if (capability.State != PerKeyRestorationStates.Verified)
            return new(PerKeyAdmissionVerdict.RestorationIncomplete, $"The restoration capability is {capability.State}: {capability.Reason}");
        return new(PerKeyAdmissionVerdict.Admitted,
            $"Per-key map gates and complete {PerKeyRestorationContract.Version} restoration are verified for {status.Transport} ({capability.Scope}); optical output is not certified.");
    }

    /// <summary>
    /// The required components not reported exactly once as Verified, in <see cref="PerKeyRestorationContract.Required"/> order;
    /// all of them when no capability is reported.
    /// </summary>
    public static IReadOnlyList<string> UnprovenComponents(PerKeyStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var components = Components(status.RestorationCapability);
        return PerKeyRestorationContract.Required
            .Where(name => components.Where(c => c.Component == name).ToArray() is not [{ State: PerKeyRestorationStates.Verified }])
            .ToArray();
    }

    /// <summary>Every target whose per-key output is not admitted. Targets missing from <paramref name="controllers"/> are left to the Host.</summary>
    public static IReadOnlyList<PerKeyBlockedTarget> Blocked(IEnumerable<ZoneRefDto> targets, IEnumerable<ControllerDto> controllers)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(controllers);
        var known = controllers.ToArray();
        var blocked = new List<PerKeyBlockedTarget>();
        foreach (var target in targets.Distinct())
        {
            var controller = known.FirstOrDefault(c => c.Id == target.ControllerId);
            var zone = controller?.Zones.FirstOrDefault(z => z.Id == target.ZoneId);
            if (controller is null || zone is null) continue;
            var result = Evaluate(zone);
            if (!result.AllowsOutput) blocked.Add(new(target, controller, zone, result));
        }
        return blocked;
    }

    // A wire report may omit the list or carry null entries: neither is ever read as Verified (a null entry is also a mismatch).
    private static PerKeyRestorationComponentDto[] Components(PerKeyRestorationCapabilityDto? capability) =>
        capability?.Components?.Where(c => c is not null).ToArray() ?? [];

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);
}

public sealed record PerKeyBlockedTarget(ZoneRefDto Target, ControllerDto Controller, ZoneDto Zone, PerKeyAdmissionResult Result);
