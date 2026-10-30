using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

public enum PerKeyAdmissionVerdict
{
    /// <summary>The zone claims no per-key representation (ordinary or aggregate colour); per-key admission does not apply.</summary>
    NotPerKey = 0,
    /// <summary>The per-key capability is not Ready (unsupported, disabled, missing/invalid map, unverified, ...).</summary>
    CapabilityBlocked,
    /// <summary>Retained so every later value keeps its number; no longer produced (unproven restoration is diagnostic only).</summary>
    RestorationUnproven,
    /// <summary>The backend reports an actual failure: the status word, the capability or one of its components is Failed.</summary>
    RestorationFailed,
    Admitted,
    /// <summary>Retained so the enum keeps its values; no longer produced.</summary>
    RestorationIncomplete,
    /// <summary>Retained so the enum keeps its values; no longer produced.</summary>
    RestorationMismatch,
}

public sealed record PerKeyAdmissionResult(PerKeyAdmissionVerdict Verdict, string Reason)
{
    public bool AllowsOutput => Verdict is PerKeyAdmissionVerdict.NotPerKey or PerKeyAdmissionVerdict.Admitted;
}

/// <summary>
/// Client-side admission before any request that would make a zone's per-key representation output. A Ready capability (a
/// local route map that passed the backend's map gates) is admitted: the Host's shared actor owns every write and its own
/// restore. Restoration words and the itemised capability are diagnostics only; an Unproven, unknown or missing report never
/// blocks, and only an actual Failed report (status word, capability or component) does. Anything not Ready stays blocked. The
/// zone's shape decides whether output would be per-key: an unmapped Point zone only carries aggregate colour whatever its
/// per-key status, so aggregate scenes, followers and lights-off are untouched. This never makes a device or map supported and
/// says nothing about optical output.
/// </summary>
public static class PerKeyAdmission
{
    public const string ReadyState = "Ready";
    public const string AggregateLayout = "Point";

    private const string DefaultFailed = "The original-lighting snapshot or restore preflight failed.";

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
    /// aggregate zone too): CapabilityBlocked when not Ready, RestorationFailed only for an actual Failed report, else Admitted.
    /// </summary>
    public static PerKeyAdmissionResult EvaluateStatus(PerKeyStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State != ReadyState)
            return new(PerKeyAdmissionVerdict.CapabilityBlocked, $"Per-key capability is {status.State}: {status.Reason}");
        if (status.Restoration == PerKeyRestorationStates.Failed)
            return new(PerKeyAdmissionVerdict.RestorationFailed, status.RestorationReason ?? DefaultFailed);

        var capability = status.RestorationCapability;
        var failed = Components(capability).Where(c => c.State == PerKeyRestorationStates.Failed).Select(c => c.Component).ToArray();
        if (capability?.State == PerKeyRestorationStates.Failed || failed.Length > 0)
            return new(PerKeyAdmissionVerdict.RestorationFailed,
                $"The restoration capability reports a failure{(failed.Length > 0 ? $" ({string.Join(", ", failed)})" : string.Empty)}: {capability!.Reason}");

        var restoration = status.Restoration == PerKeyRestorationStates.Verified
            ? "restoration diagnostics report Verified"
            : $"restoration diagnostics report {(string.IsNullOrWhiteSpace(status.Restoration) ? "nothing" : status.Restoration)} (informational only)";
        return new(PerKeyAdmissionVerdict.Admitted,
            $"Per-key map is Ready for {status.Transport ?? "its route"} ({status.KeyCount} keys); {restoration}; optical output is not certified.");
    }

    /// <summary>
    /// Diagnostics only: the <see cref="PerKeyRestorationContract.Required"/> components not reported exactly once as Verified,
    /// in that order; all of them when no capability is reported. Never used to admit or refuse output.
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

    // A wire report may omit the list or carry null entries: neither is ever read as Verified or as Failed.
    private static PerKeyRestorationComponentDto[] Components(PerKeyRestorationCapabilityDto? capability) =>
        capability?.Components?.Where(c => c is not null).ToArray() ?? [];
}

public sealed record PerKeyBlockedTarget(ZoneRefDto Target, ControllerDto Controller, ZoneDto Zone, PerKeyAdmissionResult Result);
