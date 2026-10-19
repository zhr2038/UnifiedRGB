using System.Collections.Concurrent;
using System.Diagnostics;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>
/// The durable <see cref="IFrameSetSource"/> of installed packages. It CLAIMS every "package:&lt;contentId&gt;:&lt;bindingId&gt;" id:
/// such frame sets are strict (no effect fallback, no decimation, blocked when unresolvable) and use the opt-in
/// <see cref="FrameSetTiming.PhysicalMs"/> contract. The set is built from the verified immutable RGB24 bytes in lighting
/// manifest order, which is also the bound target order (<see cref="BoundTargets"/>). Nothing is regenerated or rescaled.
/// </summary>
public sealed class ScenePackageFrameSource(ScenePackageStore packages, ScenePackageBindingStore bindings) : IFrameSetSource
{
    private readonly ConcurrentDictionary<string, (FrameSet Set, IReadOnlyList<ZoneRef> Targets)> _built = new(StringComparer.Ordinal);

    public ScenePackageStore Packages => packages;

    public ScenePackageBindingStore Bindings => bindings;

    public bool Claims(string id) => ScenePackageIds.IsTimelineId(id);

    public bool TryGet(string id, out FrameSet frameSet)
    {
        frameSet = null!;
        if (!Resolve(id, out var built, out _))
        {
            return false;
        }

        frameSet = built.Set;
        return true;
    }

    /// <summary>Why a package id cannot play (missing/corrupt package or binding snapshot); null when it resolves (screen-only included).</summary>
    public string? Problem(string id) => Resolve(id, out _, out var problem) ? null : problem;

    public IReadOnlyList<ZoneRef>? BoundTargets(string id) => Resolve(id, out var built, out _) ? built.Targets : null;

    /// <summary>The verified package and binding snapshot of a timeline id (also for screen-only packages, which have no frame set).</summary>
    public bool TryResolve(string id, out InstalledScenePackage package, out BindingSnapshot snapshot, out string? problem)
    {
        package = null!;
        snapshot = null!;
        if (!ScenePackageIds.TryParse(id, out var contentId, out var bindingId))
        {
            problem = $"'{ScenePackageFormat.Printable(id)}' is not a package timeline id";
            return false;
        }

        if (!packages.TryGet(contentId, out package, out problem))
        {
            return false;
        }

        if (!bindings.TryLoad(contentId, bindingId, out snapshot, out problem))
        {
            return false;
        }

        var roles = package.Manifest.Roles.ToArray();
        var bound = snapshot.Bindings;
        if (bound.Count != roles.Length || !roles.All(r => bound.Count(b => b.Role == r) == 1))
        {
            problem = $"binding snapshot {bindingId} does not bind exactly the package's roles";
            return false;
        }

        foreach (var l in package.Manifest.Lighting)
        {
            if (bound.Single(b => b.Role == l.Role) is not { Kind: BindingEntry.Lighting, Zone: not null })
            {
                problem = $"binding snapshot {bindingId}: lighting role {l.Role} is not bound to a zone";
                return false;
            }
        }

        foreach (var s in package.Manifest.Screens)
        {
            if (bound.Single(b => b.Role == s.Role) is not { Kind: BindingEntry.Screen, ScreenId: { Length: > 0 } })
            {
                problem = $"binding snapshot {bindingId}: screen role {s.Role} is not bound to a screen";
                return false;
            }
        }

        problem = null;
        return true;
    }

    private bool Resolve(string id, out (FrameSet Set, IReadOnlyList<ZoneRef> Targets) built, out string? problem)
    {
        if (_built.TryGetValue(id, out built))
        {
            problem = null;
            return true;
        }

        if (!TryResolve(id, out var package, out var snapshot, out problem))
        {
            return false;
        }

        var m = package.Manifest;
        if (m.Lighting.Count == 0)
        {
            problem = null; // a resolvable screen-only package: valid provenance, simply no frame set
            return false;
        }

        var first = m.Lighting[0];
        var set = new FrameSet(id, first.Frames, first.FrameDurationMs, m.Lighting.Select(l => l.LedCount).ToArray(),
            m.Lighting.Select(l => package.Lighting[l.File]).ToArray())
        {
            Timing = FrameSetTiming.PhysicalMs,
        };
        if (set.Validate() is { } bad)
        {
            problem = bad;
            return false;
        }

        built = (set, snapshot.LightingTargets(m));
        _built[id] = built;
        problem = null;
        return true;
    }
}

/// <summary>
/// The shared package timeline the screen workers read. <see cref="Position"/> is CACHE ONLY (no store, binding or file I/O, also
/// for missing or corrupt content): a timeline id resolves once in <see cref="Warm"/>, outside rendering (package apply, startup
/// hydration, or one background warm scheduled by the first unknown read, which reports "being prepared" meanwhile). A package
/// with RGB follows the render loop's own position for that frame set (device clock when bound, else its labelled upload/local
/// anchor, exactly what the RGB plays); when that RGB is not in the render plan the screens are BLOCKED, never put on an
/// independent clock. Only a screen-only package uses one shared monotonic apply epoch per timeline id. Unresolvable content and
/// an explicit <see cref="Block"/> (e.g. media that could not be re-installed) are reported blocked, never substituted.
/// </summary>
public sealed class ScenePackageTimeline : IScreenTimeline
{
    /// <summary>The blocked reason while an unknown id is being resolved in the background.</summary>
    public const string Preparing = "the package timeline is being prepared (not yet verified)";

    /// <summary>The blocked reason of a package with RGB whose frame set is not in the render plan.</summary>
    public const string RgbNotInPlan = "the package RGB is not in the render plan; its screens never run on an independent clock";

    private readonly Func<string, TimelineFacts> _resolve;
    private readonly Func<string, TimelinePosition?> _loopTimeline;
    private readonly ConcurrentDictionary<string, TimelineFacts> _facts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warming = new(StringComparer.Ordinal);

    public ScenePackageTimeline(ScenePackageFrameSource source, Func<string, TimelinePosition?> loopTimeline)
        : this(id => FactsOf(source, id), loopTimeline)
    {
        ArgumentNullException.ThrowIfNull(source);
    }

    /// <summary>Test seam: <paramref name="resolve"/> is the only place a timeline id is resolved (it may do I/O).</summary>
    internal ScenePackageTimeline(Func<string, TimelineFacts> resolve, Func<string, TimelinePosition?> loopTimeline)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _loopTimeline = loopTimeline ?? throw new ArgumentNullException(nameof(loopTimeline));
    }

    /// <summary>What a timeline id resolved to: its loop duration and whether it has RGB, or why it cannot play.</summary>
    /// <param name="Pinned">An explicit <see cref="Block"/>: only <see cref="Warm"/> (an apply or completed hydration) lifts it.</param>
    internal sealed record TimelineFacts(double DurationMs, bool HasLighting, string? Problem, bool Pinned = false);

    /// <summary>Resolves <paramref name="timelineId"/> now (store/binding I/O; never on a rendering path) and caches the result.</summary>
    /// <returns>Why it cannot play, or null.</returns>
    public string? Warm(string timelineId)
    {
        var facts = Prepare(timelineId);
        Publish(timelineId, facts);
        return facts.Problem;
    }

    /// <summary>Resolves a candidate outside rendering without changing the currently published timeline or its hold.</summary>
    internal TimelineFacts Prepare(string timelineId) => _resolve(timelineId);

    /// <summary>Publishes previously prepared facts only after the candidate passed engine admission.</summary>
    internal void Publish(string timelineId, TimelineFacts facts) => _facts[timelineId] = facts;

    /// <summary>Marks <paramref name="timelineId"/> blocked (its screens show black, labelled) until the next <see cref="Warm"/>.</summary>
    public void Block(string timelineId, string why) => _facts[timelineId] = new TimelineFacts(0, false, why, Pinned: true);

    /// <summary>
    /// After an import of <paramref name="contentId"/>: re-resolves its cached resolution failures (e.g. a repaired store entry).
    /// Successful entries and explicit blocks stay exactly as they are, so a re-import never interrupts a playing package.
    /// </summary>
    public void Retry(string contentId)
    {
        foreach (var (id, facts) in _facts)
        {
            if (facts is { Problem: not null, Pinned: false } && ScenePackageIds.TryParse(id, out var c, out _)
                && string.Equals(c, contentId, StringComparison.Ordinal))
            {
                Warm(id);
            }
        }
    }

    /// <summary>Restarts the apply epoch of <paramref name="timelineId"/> (a package apply); RGB-driven positions are unaffected.</summary>
    public void RestartEpoch(string timelineId) => _epochs[timelineId] = Stopwatch.GetTimestamp();

    public ScreenTimelineSample? Position(string timelineId)
    {
        if (!_facts.TryGetValue(timelineId, out var facts))
        {
            ScheduleWarm(timelineId);
            return new ScreenTimelineSample(0, TimelineSources.Blocked, Preparing);
        }

        if (facts.Problem is { } problem)
        {
            return new ScreenTimelineSample(0, TimelineSources.Blocked, problem);
        }

        if (facts.HasLighting)
        {
            return _loopTimeline(timelineId) is { } t
                ? new ScreenTimelineSample(t.PositionMs, t.Source, t.Blocked)
                : new ScreenTimelineSample(0, TimelineSources.Blocked, RgbNotInPlan);
        }

        var start = _epochs.GetOrAdd(timelineId, _ => Stopwatch.GetTimestamp());
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var period = facts.DurationMs;
        return new ScreenTimelineSample(elapsedMs - (Math.Floor(elapsedMs / period) * period), TimelineSources.ApplyEpoch);
    }

    internal static TimelineFacts FactsOf(ScenePackageFrameSource source, string timelineId) =>
        source.TryResolve(timelineId, out var package, out _, out var problem)
            ? new TimelineFacts(package.Manifest.DurationMs, package.Manifest.Lighting.Count > 0, null)
            : new TimelineFacts(0, false, problem ?? "the package content cannot be resolved");

    private void ScheduleWarm(string timelineId)
    {
        if (!_warming.TryAdd(timelineId, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // never overwrites what an apply, a hydration or a block recorded meanwhile
                _facts.TryAdd(timelineId, _resolve(timelineId));
            }
#pragma warning disable CA1031 // a background warm must never fault: the id then stays blocked, labelled
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _facts.TryAdd(timelineId, new TimelineFacts(0, false, $"the package timeline could not be prepared ({ex.GetType().Name}: {ex.Message})"));
            }
            finally
            {
                _warming.TryRemove(timelineId, out _);
            }
        });
    }
}
