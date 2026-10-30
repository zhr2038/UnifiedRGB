using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <summary>
/// Optional local fallback for one aggregate point, or one zone whose own per-key capability is Ready for every LED. Copies
/// one already rendered RGB value to every target LED in the SAME tick; does not modify scene content, bindings, output
/// stages, frame-set meaning or clocks. No chaining, uploads, grouping or interpolation. An explicit scene assignment to the
/// target always wins, and then each LED shows its own authored colour. Empty by default.
/// </summary>
public sealed record ZoneFollower(ZoneRef Source, int SourceLed, ZoneRef Target);

public sealed record ZoneFollowerStatus(ZoneFollower Binding, string State);

public sealed partial class RenderLoop
{
    public const int MaximumFollowers = 16;
    private ZoneFollower[] _followers = [];
    private FollowSlice[] _followSlices = [];
    private IReadOnlyList<ZoneFollowerStatus> _followerStatus = Array.Empty<ZoneFollowerStatus>();

    public IReadOnlyList<ZoneFollowerStatus> FollowerStatus { get { lock (_gate) return _followerStatus; } }

    /// <summary>Validates and freezes local fallback routes. No hardware I/O, scene publication or loop upload.</summary>
    public void SetFollowers(IReadOnlyList<ZoneFollower> followers)
    {
        ArgumentNullException.ThrowIfNull(followers);
        if (followers.Count > MaximumFollowers) throw new ArgumentOutOfRangeException(nameof(followers));
        var copy = followers.ToArray();
        if (copy.Any(f => f is null || f.SourceLed < 0 || string.IsNullOrWhiteSpace(f.Source.Controller.Value)
            || string.IsNullOrWhiteSpace(f.Target.Controller.Value) || f.Source.Zone.Value < 0 || f.Target.Zone.Value < 0))
            throw new ArgumentException("Follower endpoints and source LED must be valid.", nameof(followers));
        var targets = copy.Select(f => f.Target.Controller).ToHashSet();
        if (copy.Select(f => f.Target).Distinct().Count() != copy.Length || copy.Any(f => targets.Contains(f.Source.Controller)))
            throw new ArgumentException("Followers cannot share targets, self-follow or form chains.", nameof(followers));
        lock (_gate) { _followers = copy; CompileFollowers(_scene); }
    }

    private void CompileFollowers(Scene scene)
    {
        if (_followers.Length == 0) { _followSlices = []; _followerStatus = Array.Empty<ZoneFollowerStatus>(); return; }
        var explicitTargets = scene.Assignments.SelectMany(a => a.Targets).ToHashSet();
        var slices = new List<FollowSlice>(_followers.Length);
        var status = new List<ZoneFollowerStatus>(_followers.Length);
        foreach (var f in _followers)
        {
            string verdict;
            var source = Array.FindIndex(_controllers, c => c.Descriptor.Id == f.Source.Controller);
            var target = Array.FindIndex(_controllers, c => c.Descriptor.Id == f.Target.Controller);
            if (explicitTargets.Contains(f.Target)) verdict = "SceneAssigned";
            else if (source < 0 || target < 0) verdict = "MissingController";
            else if (_controllers[source].IsLoop || _controllers[target].IsLoop) verdict = "RequiresStreamedSourceAndTarget";
            else
            {
                var sourceZones = _controllers[source].Mailbox.Controller.Zones;
                var sourceZone = -1;
                for (var z = 0; z < sourceZones.Count; z++) if (sourceZones[z].Id == f.Source.Zone) { sourceZone = z; break; }
                var targetDescriptor = _controllers[target].Mailbox.Controller;
                if (sourceZone < 0 || f.SourceLed >= sourceZones[sourceZone].LedCount) verdict = "SourcePixelUnavailable";
                else if (targetDescriptor.Zones.Count != 1 || targetDescriptor.Zones[0].Id != f.Target.Zone || !WholeColourTarget(targetDescriptor.Zones[0]))
                    verdict = "TargetMustBeSingleAggregatePoint";
                else if (_controllers[target].Stage.Gamma != 1f || _controllers[target].Stage.WhitePoint != Rgb.White)
                    verdict = "TargetRequiresIdentityColourStage";
                else
                {
                    slices.Add(new(source, sourceZone, f.SourceLed, target));
                    verdict = "Ready";
                }
            }
            status.Add(new(f, verdict));
        }
        _followSlices = slices.ToArray(); _followerStatus = status.AsReadOnly();
    }

    private void ApplyFollowers()
    {
        foreach (var f in _followSlices)
        {
            var source = _controllers[f.Source]; var target = _controllers[f.Target];
            if (_bringup.Contains(source.Descriptor.Id) || _bringup.Contains(target.Descriptor.Id)) continue;
            // Staged source RGB is copied exactly; only identity-colour targets are eligible, so no target correction is
            // silently bypassed or double-applied. Both buffers belong to this renderer under _gate, not to writers.
            target.Mailbox.Back.Zones[0].AsSpan().Fill(source.Mailbox.Back.Zones[f.SourceZone][f.SourceLed]);
        }
    }

    // The aggregate point, or a zone whose own capability reports Ready per-key output for exactly its LEDs. Ordinary,
    // unmapped or not-Ready multi-LED zones are not guessed at.
    private static bool WholeColourTarget(ZoneDescriptor zone) =>
        zone.LedCount == 1 || (zone.PerKey is { Ready: true } perKey && perKey.KeyCount == zone.LedCount);

    private bool FollowerSourceHeld(int controller)
    {
        foreach (var f in _followSlices)
            if (f.Target == controller && _bringup.Contains(_controllers[f.Source].Descriptor.Id)) return true;
        return false;
    }

    private Mailbox? FollowerDependency(int controller)
    {
        foreach (var f in _followSlices) if (f.Target == controller) return _controllers[f.Source].Mailbox;
        return null;
    }

    private readonly record struct FollowSlice(int Source, int SourceZone, int SourceLed, int Target);
}
