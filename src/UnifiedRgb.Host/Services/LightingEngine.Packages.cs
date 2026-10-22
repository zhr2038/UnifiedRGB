using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// OPUS-GENERIC-SCENE-PACKAGES integration of the engine: whole-candidate validation of strict package content before any
/// activation (resolvable content and binding snapshot, exact bound targets, every target present, active and ready, exact raw-tick
/// interval, and the wire budget measured through each adapter's actual transform/codec), plus the blocked diagnostics.
/// A scene without package content (every historical scene) takes none of these paths.
/// </summary>
public sealed partial class LightingEngine
{
    private volatile string? _startupBlocked;

    /// <summary>Why strict package content of the running scene cannot play; empty when nothing is blocked.</summary>
    public IReadOnlyList<string> BlockedContent
    {
        get
        {
            var blocked = _loop.BlockedContent;
            if (options.StartupRecoveryNotice is { } repair) return [repair, .. blocked];
            return _startupBlocked is { } startup ? [startup, .. blocked] : blocked;
        }
    }

    /// <summary>True when <paramref name="scene"/> carries package provenance or a package frame set id.</summary>
    public static bool IsPackageScene(SceneDto scene) =>
        scene?.Package is not null || (scene?.Assignments?.Any(a => ScenePackageIds.IsTimelineId(a?.FrameSetId)) ?? false);

    /// <summary>
    /// Throws ArgumentException (nothing changed) when <paramref name="scene"/> carries strict content that cannot play exactly.
    /// Called under the scene gate, before <see cref="RenderLoop.SetScene(Scene, bool)"/>.
    /// </summary>
    private void ValidateStrict(Scene scene, string paramName, bool requireActive = true)
    {
        var strict = scene.Assignments
            .Where(a => a.Strategy == ContentStrategy.Frames && ScenePackageIds.IsTimelineId(a.FrameSetId))
            .ToArray();
        if (strict.Length == 0 && scene.Package is null)
        {
            return; // every historical scene: unchanged
        }

        if (frameSets is null)
        {
            throw new ArgumentException("This Host has no scene-package source; package content cannot be applied.", paramName);
        }

        if (scene.Package is not { } package)
        {
            throw new ArgumentException("Package frame sets need the scene's package provenance (content id, binding id, contract).", paramName);
        }

        var expectedContract = frameSets is ScenePackageFrameSource packageSource
            && packageSource.TryResolve(ScenePackageIds.TimelineIdOf(package.ContentId, package.BindingId), out _, out var binding, out _)
            ? binding.PlaybackContract : ScenePackageFormat.PlaybackContract;
        if (!string.Equals(package.PlaybackContract, expectedContract, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported playback contract '{package.PlaybackContract}' (this program plays '{ScenePackageFormat.PlaybackContract}' only).", paramName);
        }

        var timeline = ScenePackageIds.TimelineIdOf(package.ContentId, package.BindingId);
        if (frameSets.Problem(timeline) is { } problem)
        {
            throw new ArgumentException($"Scene package {package.ContentId} binding {package.BindingId}: {problem}", paramName);
        }

        if (frameSets.BoundTargets(timeline) is { } bound && !strict.Any(a => a.Targets.SequenceEqual(bound)))
        {
            // e.g. SetAssignment over every package zone would drop the package assignment while the provenance stays
            throw new ArgumentException(
                $"Scene package {package.ContentId}: its lighting must play on exactly its bound targets; package zones are never partially overridden (apply another scene instead).",
                paramName);
        }

        var active = _loop.Controllers.SelectMany(c => c.Zones.Select(z => new ZoneRef(c.Id, z.Id))).ToHashSet();
        var entries = adapters.Controllers;
        foreach (var a in strict)
        {
            if (!string.Equals(a.FrameSetId, timeline, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Frame set {a.FrameSetId} does not belong to the scene's package {timeline}.", paramName);
            }
            if (!requireActive) continue;

            foreach (var t in a.Targets.Where(t => !active.Contains(t)))
            {
                throw new ArgumentException($"Package target {t} is not present and active; package content is never partially applied.", paramName);
            }

            // Topology alone is not readiness: every bound writer (streamed or uploaded) must be able to play now, whichever RPC
            // (package apply, raw SetScene, ApplyProfile, a SetAssignment re-validating the merged scene) carries the content.
            foreach (var controller in a.Targets.Select(t => t.Controller).Distinct())
            {
                if (StrictUnavailable(entries.FirstOrDefault(e => e.Descriptor.Id == controller)) is { } why)
                {
                    throw new ArgumentException(
                        $"Package target {controller} {why}; package content plays on all of its targets or not at all.", paramName);
                }
            }
        }

        var preflight = _loop.PreflightStrict(scene);
        if (preflight.Blocked.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", preflight.Blocked), paramName);
        }

        foreach (var l in preflight.Loops)
        {
            var entry = adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id == l.Controller && e.IsActive)
                ?? throw new ArgumentException($"Package loop target {l.Controller} is not active.", paramName);
            if (entry.Adapter is not ILoopBudgetProbe probe)
            {
                throw new ArgumentException($"{l.Controller}: its adapter cannot measure a strict loop's wire budget; strict content is refused.", paramName);
            }

            var budget = probe.MeasureLoop(l.Controller, l.Zone, l.Loop);
            if (!budget.Fits)
            {
                throw new ArgumentException(
                    $"{l.Controller}/{l.Zone}: strict loop does not fit ({budget.Problem ?? $"{budget.CompressedBytes} > {budget.MaxBytes} bytes"}); never decimated.",
                    paramName);
            }
        }
    }

    /// <summary>
    /// Why the controller cannot play strict package content right now, or null when it can: discovered, its writer open, not
    /// leased by a bring-up session, its guard verdict Clear and its writer not paused. Shared by the engine's strict admission
    /// and the package service's Ready/Apply evaluation, so neither path can admit a partial show.
    /// </summary>
    internal static string? StrictUnavailable(ControllerEntry? entry) => entry switch
    {
        null => "is not discovered (offline or unknown)",
        { BringupLease: { } lease } => $"is leased by bring-up session {lease}",
        { Verdict.State: not GuardState.Clear } e => $"is {e.Verdict.State.ToString().ToLowerInvariant()} by {e.Verdict.Writer ?? "another writer"}",
        { Writer: null } => "is not active (no open writer)",
        { Writer.Paused: true } => "is paused (contended)",
        _ => null,
    };

    /// <summary>Startup with package content that cannot even be activated: dark (empty scene, provenance kept) and labelled.</summary>
    private void BlockStartup(ProfileDto restored, string why)
    {
        ScenePackageProvenance? provenance = null;
        if (restored.Scene.Package is { } pk)
        {
            try
            {
                provenance = DtoMapper.FromDto(pk);
            }
            catch (ArgumentException)
            {
                provenance = null;
            }
        }

        _startupBlocked = $"startup: package scene '{restored.Name}' BLOCKED: {why}";
        lock (_sceneGate)
        {
            _loop.SetScene(Scene.Empty with { Package = provenance }, allowMissingZones: true);
            _activeName = restored.Name;
        }

        LogPackageStartupBlocked(restored.Name, why);
    }

    [LoggerMessage(EventId = 1015, Level = LogLevel.Error, Message = "Package scene {Name} blocked at startup ({Reason}); its zones stay dark, no default show")]
    private partial void LogPackageStartupBlocked(string name, string reason);
}
