using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>
/// The <see cref="IScenePackageService"/> RPC target (OPUS-GENERIC-SCENE-PACKAGES). Import, list, details and binding saves
/// write only the ProgramData store: no device I/O and no active-scene change. Apply validates the whole candidate (content,
/// binding snapshot, live targets, strict loops' exact interval and actual wire budget, screen scene and its media) before
/// anything changes, then activates RGB and screens together on one timeline. Calls are serialized.
/// </summary>
public sealed partial class ScenePackageService : IScenePackageService, ISceneLibraryService, IHostedService, IDisposable
{
    public const string KindLighting = "lighting";
    public const string KindScreen = "screen";

    /// <summary>Base64 text of the largest accepted ZIP.</summary>
    public const long MaxBase64Length = (ScenePackageFormat.MaxZipBytes + 2) / 3 * 4;

    private const int MaxUploadNameLength = 128;
    private const int MaxAssetNameLength = 64;

    private readonly ScenePackageFrameSource _source;
    private readonly ScenePackageTimeline _timeline;
    private readonly LightingEngine _engine;
    private readonly IScreenPackageMedia? _media;
    private readonly IScreenService? _screens;
    private readonly IScreenSceneBinding? _screenScenes;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _hydrationStop = new();
    private int _disposed;

    public ScenePackageService(ScenePackageFrameSource source, ScenePackageTimeline timeline, LightingEngine engine, ILogger<ScenePackageService> log,
        IScreenPackageMedia? media = null, IScreenService? screens = null, IScreenSceneBinding? screenScenes = null, ProfileStore? profiles = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _media = media;
        _screens = screens;
        _screenScenes = screenScenes;
        _profiles = profiles;
        if (_media is not null)
        {
            // package media stays protected from generic deletion, and package screens read the shared timeline
            _media.AssetPinned = id => _source.Packages.PinsAsset(id) || (_profiles is not null && ProfileReferencesAsset(id));
            _media.Timeline = _timeline;
        }
        if (_profiles is not null)
        {
            _profiles.BeforeReferenceCommit = ValidateProfileReferences;
            _referenceRegistration = SceneStateFiles.RegisterValidation(Store.DataRoot, ValidateStateReferences);
        }
    }

    private ScenePackageStore Store => _source.Packages;

    private ScenePackageBindingStore BindingStore => _source.Bindings;

    /// <summary>The blocked reason of an active package timeline until startup hydration has verified and re-installed it.</summary>
    public const string StartupPreparing = "startup: the package media is being re-verified and re-installed from its immutable original";

    /// <summary>
    /// Longest wait of Host startup for <see cref="StartAsync"/>'s media hydration. Hydration that takes longer continues in the
    /// background; its package screens stay BLOCKED (black, labelled) until it completes, never showing substitute content.
    /// </summary>
    internal TimeSpan StartupHydrationWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The startup hydration (tests).</summary>
    internal Task Hydration { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Startup, registered BEFORE the screen subsystem starts its workers: every package timeline that is active after a restart
    /// (the restored RGB scene's provenance and the persisted screen scene's TimelineIds) is first BLOCKED, then re-verified from the
    /// immutable store and its screen media re-installed (normalized again from the original when the screen cache was deleted or
    /// is incomplete), and only then warmed. Missing/corrupt originals or media that cannot be re-installed stay blocked, labelled.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ids = ActiveTimelineIds();
        if (ids.Count == 0)
        {
            return;
        }

        foreach (var id in ids)
        {
            _hydrating.TryAdd(id, 0);
            _timeline.Block(id, StartupPreparing); // before any screen worker can compose a frame of it
        }

        var hydrationToken = _hydrationStop.Token; // capture before queuing; Dispose may run before a queued task starts
        Hydration = Task.Run(() => HydrateAsync(ids, hydrationToken), CancellationToken.None);
        var finished = await Task.WhenAny(Hydration, Task.Delay(StartupHydrationWait, cancellationToken)).ConfigureAwait(false);
        if (finished != Hydration)
        {
            LogHydrationSlow(ids.Count, StartupHydrationWait.TotalSeconds);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _hydrationStop.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // This service owns and cancels its one startup hydration task.
        await Hydration.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _hydrationStop.Cancel();
        _hydrationStop.Dispose();
        _referenceRegistration?.Dispose();
        if (_profiles?.BeforeReferenceCommit == ValidateProfileReferences) _profiles.BeforeReferenceCommit = null;
        _gate.Dispose();
    }

    public async Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (packages, _) = await Task.Run(Store.List, ct).ConfigureAwait(false);
            return packages.Select(InfoOf).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var name = package.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxUploadNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException($"An upload name must be 1..{MaxUploadNameLength} printable characters.", nameof(package));
        }

        if (string.IsNullOrEmpty(package.Base64) || package.Base64.Length > MaxBase64Length)
        {
            throw new ArgumentException($"A package upload is 1..{ScenePackageFormat.MaxZipBytes} bytes of Base64 ZIP.", nameof(package));
        }

        var zip = new byte[package.Base64.Length / 4 * 3];
        if (!Convert.TryFromBase64String(package.Base64, zip, out var written))
        {
            throw new ArgumentException("The package upload is not valid Base64.", nameof(package));
        }

        Array.Resize(ref zip, written);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var content = await Task.Run(() => ScenePackageReader.Read(zip), ct).ConfigureAwait(false);
            var media = new List<(ScenePackageMediaRecord, byte[])>();
            foreach (var s in content.Manifest.Screens)
            {
                media.Add(await ProbeAsync(s, content.Files[s.File], ct).ConfigureAwait(false));
            }

            var (installed, created) = await Task.Run(() => Store.Install(content, zip, media), ct).ConfigureAwait(false);
            _source.Restore(installed.ContentId);
            await Task.Run(() => _timeline.Retry(installed.ContentId), ct).ConfigureAwait(false); // cached resolution failures only
            LogImported(installed.ContentId, installed.Manifest.Id, installed.Manifest.Revision, created);
            return DetailsOf(installed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return DetailsOf(await RequireAsync(contentId, ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var package = await RequireAsync(contentId, ct).ConfigureAwait(false);
            var snapshot = BindingStore.Current(package.ContentId, out var pointerProblem);
            if (snapshot is null)
            {
                var problems = new List<string> { "No binding has been saved for this package." };
                if (pointerProblem is not null)
                {
                    problems.Add(pointerProblem);
                }

                return new ScenePackageBindingStateDto(package.ContentId, null,
                    package.Manifest.Roles.Select(r => new ScenePackageBindingDto(r)).ToArray(), false, problems);
            }

            var live = await EvaluateAsync(package, snapshot.Bindings, requireActive: true, ct).ConfigureAwait(false);
            return new ScenePackageBindingStateDto(package.ContentId, snapshot.BindingId, snapshot.Bindings.Select(ToDto).ToArray(), live.Count == 0, live);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var package = await RequireAsync(bindings.ContentId, ct).ConfigureAwait(false);
            var requested = bindings.Bindings ?? [];
            var (entries, problems) = Normalize(package, requested);
            if (problems.Count == 0)
            {
                problems.AddRange(await EvaluateAsync(package, entries, requireActive: false, ct).ConfigureAwait(false));
            }

            if (problems.Count > 0)
            {
                // refused: the previous snapshot and current pointer stay exactly as they were
                return new ScenePackageBindingStateDto(package.ContentId, null, requested, false, problems);
            }

            var snapshot = BindingStore.Save(package.ContentId, entries);
            var live = await EvaluateAsync(package, entries, requireActive: true, ct).ConfigureAwait(false);
            LogBindingSaved(package.ContentId, snapshot.BindingId);
            return new ScenePackageBindingStateDto(package.ContentId, snapshot.BindingId, entries.Select(ToDto).ToArray(), live.Count == 0, live);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyLockedAsync(contentId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScenePackageApplyDto> ApplyScenePackageCheckedAsync(ScenePackageApplyRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ScenePackageFormat.IsSha256Hex(request.ContentId) || !ScenePackageFormat.IsSha256Hex(request.ExpectedBindingId))
            throw new ArgumentException("A checked apply requires exact package and binding SHA-256 ids.", nameof(request));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ApplyLockedAsync(request.ContentId, ct, request.ExpectedBindingId).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ScenePackageApplyDto> ApplyLockedAsync(string contentId, CancellationToken ct, string? expectedBindingId = null)
    {
        if (!Store.TryGet(contentId, out var package, out var problem))
        {
            return new ScenePackageApplyDto(false, contentId, null, problem ?? "Unknown package.");
        }

        var snapshot = BindingStore.Current(package.ContentId, out var pointerProblem);
        if (snapshot is null)
        {
            return new ScenePackageApplyDto(false, package.ContentId, null, pointerProblem ?? "No binding has been saved for this package.");
        }

        if (expectedBindingId is not null && !string.Equals(snapshot.BindingId, expectedBindingId, StringComparison.Ordinal))
            return new ScenePackageApplyDto(false, package.ContentId, snapshot.BindingId, "Not applied: device bindings changed after review. Refresh and review the saved binding before applying.");

        var live = await EvaluateAsync(package, snapshot.Bindings, requireActive: true, ct).ConfigureAwait(false);
        if (live.Count > 0)
        {
            return new ScenePackageApplyDto(false, package.ContentId, snapshot.BindingId, "Not applied: " + string.Join("; ", live));
        }

        var m = package.Manifest;
        var timelineId = ScenePackageIds.TimelineIdOf(package.ContentId, snapshot.BindingId);
        var provenance = new ScenePackageProvenance(package.ContentId, snapshot.BindingId, ScenePackageFormat.PlaybackContract);
        var current = _engine.Loop.Scene;
        Scene scene;
        if (m.Lighting.Count > 0)
        {
            // one Frames assignment over every lighting target in manifest order: one frame set, one anchor, one phase.
            // StaticColor is only the assignment's nominal effect id; strict content never falls back to it.
            var assignment = new Assignment(snapshot.LightingTargets(m), StaticColorEffect.EffectId, new EffectParams([], 0.5f, 1f), m.Name, ContentStrategy.Frames)
            {
                FrameSetId = timelineId,
            };
            scene = new Scene([assignment]) { Package = provenance };
        }
        else
        {
            // screen-only: the RGB scene stays, minus any earlier package content
            scene = new Scene(current.Assignments.Where(a => !ScenePackageIds.IsTimelineId(a.FrameSetId)).ToArray()) { Package = provenance };
        }

        ScreenSceneDto? screenScene;
        try
        {
            screenScene = await ScreenSceneOfAsync(package, snapshot, timelineId, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return new ScenePackageApplyDto(false, package.ContentId, snapshot.BindingId, "Not applied: " + ex.Message);
        }

        // Resolve without publication: a later admission failure must preserve the old timeline, including a pinned screen hold.
        var timelineFacts = await Task.Run(() => _timeline.Prepare(timelineId), ct).ConfigureAwait(false);
        if (timelineFacts.Problem is { } unresolved)
        {
            return new ScenePackageApplyDto(false, package.ContentId, snapshot.BindingId, "Not applied: " + unresolved);
        }

        IScreenSceneTransaction? transaction = null;
        try
        {
            if (screenScene is not null)
            {
                if (_screenScenes is null)
                {
                    return new ScenePackageApplyDto(false, package.ContentId, snapshot.BindingId, "Not applied: this Host has no screen subsystem.");
                }

                transaction = await _screenScenes.BeginSceneAsync(screenScene, ct).ConfigureAwait(false);
            }

            _engine.ChangeScene(scene, LightingEngine.CustomProfileName, allowMissing: false, transaction, () =>
            {
                _timeline.Publish(timelineId, timelineFacts);
                _timeline.RestartEpoch(timelineId);
            }, ct);
        }
        catch (ArgumentException ex)
        {
            return new ScenePackageApplyDto(false, package.ContentId, snapshot.BindingId, "Not applied: " + ex.Message);
        }
        finally
        {
            transaction?.Dispose();
        }

        var position = _timeline.Position(timelineId);
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"Applied '{m.Name}' r{m.Revision} ({m.Lighting.Count} lighting, {m.Screens.Count} screen role(s)); timeline {timelineId}: {position?.Source ?? "unavailable"}.");
        LogApplied(package.ContentId, snapshot.BindingId, position?.Source ?? "unavailable");
        return new ScenePackageApplyDto(true, package.ContentId, snapshot.BindingId, detail);
    }

    /// <summary>
    /// The candidate screen scene: this package's screens (installed media, manifest overlay/fit/fps, shared timeline) plus every
    /// other current screen that is neither bound by it nor on an earlier package timeline. Null when nothing changes. Package
    /// content carries no orientation: each screen shows it in its saved LOCAL rotation (0 without one), never a scene value;
    /// the screen transaction normalizes again under its mutation gate.
    /// </summary>
    private async Task<ScreenSceneDto?> ScreenSceneOfAsync(InstalledScenePackage package, BindingSnapshot snapshot, string timelineId, CancellationToken ct)
    {
        var current = _screenScenes?.CurrentForScene()?.Assignments ?? [];
        var bound = package.Manifest.Screens.Select(s => snapshot.Bindings.Single(b => b.Role == s.Role).ScreenId!).ToHashSet(StringComparer.Ordinal);
        var kept = current.Where(a => !bound.Contains(a.ScreenId) && !ScenePackageIds.IsTimelineId(a.TimelineId)).ToList();
        if (package.Manifest.Screens.Count == 0 && kept.Count == current.Count)
        {
            return null;
        }

        foreach (var s in package.Manifest.Screens)
        {
            var assetId = await InstallMediaAsync(package, s, ct).ConfigureAwait(false);
            var screenId = snapshot.Bindings.Single(b => b.Role == s.Role).ScreenId!;
            // schemaVersion 2 content: its accent and region (scene content, never device settings); v1 keeps the historical accent
            var assignment = new ScreenAssignmentDto(screenId, assetId, "#000000", s.Overlay, 0, 1, s.Fps, true, Fit: s.Fit, TimelineId: timelineId,
                OverlayLayout: s.OverlayLayout);
            kept.Add(s.AccentColor is { } accent ? assignment with { AccentColor = accent } : assignment);
        }

        var candidate = new ScreenSceneDto(kept);
        return _screenScenes is null ? candidate : _screenScenes.NormalizeToLocal(candidate);
    }

    private async Task<string> InstallMediaAsync(InstalledScenePackage package, ScreenTrack track, CancellationToken ct)
    {
        if (_media is null)
        {
            throw new ArgumentException("This Host has no screen subsystem.");
        }

        byte[] bytes;
        try
        {
            bytes = package.ReadFile(track.File);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"screen role {track.Role}: {ex.Message}", ex);
        }

        var name = $"{package.Manifest.Id} {track.Role}";
        return await _media.InstallPackageAssetAsync(name.Length > MaxAssetNameLength ? name[..MaxAssetNameLength] : name, bytes, ct).ConfigureAwait(false);
    }

    private async Task<(ScenePackageMediaRecord, byte[])> ProbeAsync(ScreenTrack track, byte[] data, CancellationToken ct)
    {
        if (_media is null)
        {
            throw ScenePackageFormat.Bad("this Host has no screen subsystem, so screen tracks cannot be validated");
        }

        ScreenMediaInfo info;
        try
        {
            info = await _media.ProbeMediaAsync(data, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw ScenePackageFormat.Bad($"screen '{track.Role}' media is not accepted by the screen importer ({ex.Message})");
        }

        if (info.DurationMs != 0 && info.DurationMs != track.DurationMs)
        {
            throw ScenePackageFormat.Bad(
                $"screen '{track.Role}' decodes to {info.DurationMs} ms ({info.Frames} frames), not its declared {track.DurationMs} ms loop (never retimed)");
        }

        return (new ScenePackageMediaRecord(track.Role, info.Format, info.Width, info.Height, info.Frames, info.DurationMs), info.ThumbnailPng);
    }

    /// <summary>Structural checks of a requested binding set: known roles, exactly once each, right target kind.</summary>
    private static (IReadOnlyList<BindingEntry> Entries, List<string> Problems) Normalize(InstalledScenePackage package, IReadOnlyList<ScenePackageBindingDto> requested)
    {
        var problems = new List<string>();
        var byRole = new Dictionary<string, ScenePackageBindingDto>(StringComparer.Ordinal);
        foreach (var b in requested)
        {
            if (b?.Role is not { } role)
            {
                problems.Add("A binding without a role.");
                continue;
            }

            if (!byRole.TryAdd(role, b))
            {
                problems.Add($"Role {ScenePackageFormat.Printable(role)} is bound more than once.");
            }
        }

        var roles = package.Manifest.Roles.ToHashSet(StringComparer.Ordinal);
        problems.AddRange(byRole.Keys.Where(r => !roles.Contains(r)).Select(r => $"Role {ScenePackageFormat.Printable(r)} is not a role of this package."));
        var entries = new List<BindingEntry>();
        foreach (var l in package.Manifest.Lighting)
        {
            if (!byRole.TryGetValue(l.Role, out var b) || b.Zone is null)
            {
                problems.Add($"Lighting role {l.Role} is unbound.");
            }
            else if (b.ScreenId is not null || b.Zone.ControllerId is null)
            {
                problems.Add($"Lighting role {l.Role} needs exactly one zone (no screen).");
            }
            else
            {
                entries.Add(new BindingEntry(l.Role, BindingEntry.Lighting, b.Zone.ControllerId, b.Zone.ZoneId, null));
            }
        }

        foreach (var s in package.Manifest.Screens)
        {
            if (!byRole.TryGetValue(s.Role, out var b) || string.IsNullOrEmpty(b.ScreenId))
            {
                problems.Add($"Screen role {s.Role} is unbound.");
            }
            else if (b.Zone is not null)
            {
                problems.Add($"Screen role {s.Role} needs exactly one screen (no zone).");
            }
            else
            {
                entries.Add(new BindingEntry(s.Role, BindingEntry.Screen, null, null, b.ScreenId));
            }
        }

        return (entries, problems);
    }

    /// <summary>
    /// Target checks against what is discovered now: lighting zones exist with EXACT LED counts (no resampling), are unique, and an
    /// uploaded-loop zone can carry the interval exactly; screens are discovered and unique. <paramref name="requireActive"/> also
    /// requires every lighting controller to be active and unpaused and every screen Idle/Running with output enabled (Ready / Apply);
    /// a save accepts a discovered but inactive one.
    /// </summary>
    private async Task<List<string>> EvaluateAsync(InstalledScenePackage package, IReadOnlyList<BindingEntry> entries, bool requireActive, CancellationToken ct)
    {
        var problems = new List<string>();
        var controllers = _engine.Adapters.Controllers;
        var zones = new HashSet<ZoneRef>();
        foreach (var l in package.Manifest.Lighting)
        {
            var entry = entries.FirstOrDefault(e => e.Role == l.Role);
            if (entry?.Zone is not { } zone)
            {
                problems.Add($"Lighting role {l.Role} is unbound.");
                continue;
            }

            if (!zones.Add(zone))
            {
                problems.Add($"Zone {zone} is bound to more than one role.");
            }

            var c = controllers.FirstOrDefault(e => e.Descriptor.Id == zone.Controller);
            var z = c?.Descriptor.Zones.FirstOrDefault(d => d.Id == zone.Zone);
            if (c is null || z is null)
            {
                problems.Add($"Lighting role {l.Role}: zone {zone} is not discovered (offline or unknown).");
                continue;
            }

            if (z.Layout.LedCount != l.LedCount)
            {
                problems.Add($"Lighting role {l.Role}: zone {zone} has {z.Layout.LedCount} LEDs, the track exactly {l.LedCount} (never resampled).");
            }

            if (c.Descriptor.Capabilities.Delivery == Delivery.UploadedLoop && !FrameSet.TryRawTicks(l.FrameDurationMs, out _))
            {
                problems.Add($"Lighting role {l.Role}: {l.FrameDurationMs} ms is not exactly representable as RF raw ticks at {FrameSet.DeviceTicksPerSecond:0}/s.");
            }

            if (requireActive && LightingEngine.StrictUnavailable(c) is { } why)
            {
                problems.Add($"Lighting role {l.Role}: controller {zone.Controller} {why}; a package plays on all of its targets or not at all.");
            }
        }

        if (package.Manifest.Screens.Count > 0)
        {
            var discovered = _screens is null ? [] : await _screens.ListScreensAsync(ct).ConfigureAwait(false);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in package.Manifest.Screens)
            {
                var screenId = entries.FirstOrDefault(e => e.Role == s.Role)?.ScreenId;
                if (string.IsNullOrEmpty(screenId))
                {
                    problems.Add($"Screen role {s.Role} is unbound.");
                    continue;
                }

                if (!used.Add(screenId))
                {
                    problems.Add($"Screen {screenId} is bound to more than one role.");
                }

                string? regionProblem = null;
                if (s.OverlayLayout is { } layout)
                {
                    regionProblem = OverlayLayouts.TargetProblem(screenId);
                    if (regionProblem is null)
                    {
                        try
                        {
                            var probe = new ScreenAssignmentDto(screenId, Overlay: "dashboard", OverlayLayout: layout);
                            var normalized = _screenScenes?.NormalizeToLocal(new ScreenSceneDto([probe])).Assignments.Single() ?? probe;
                            regionProblem = OverlayLayouts.TargetProblem(screenId, layout, normalized.Rotation);
                        }
                        catch (ArgumentException ex)
                        {
                            regionProblem = ex.Message;
                        }
                    }
                }
                if (regionProblem is { } region)
                {
                    problems.Add($"Screen role {s.Role}: screen {screenId}: {region} (the package's overlay region is never dropped or widened).");
                }

                var device = discovered.FirstOrDefault(d => string.Equals(d.Id, screenId, StringComparison.Ordinal));
                if (device is null)
                {
                    problems.Add($"Screen role {s.Role}: screen {screenId} is not discovered.");
                }
                else if (requireActive && ScreenUnavailable(device) is { } why)
                {
                    problems.Add($"Screen role {s.Role}: screen {screenId} {why}; a package plays on all of its targets or not at all.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Why a discovered screen cannot take package output now, or null. Ready: Idle or Running (present, output gate enabled, a
    /// worker can own it), or Stopped by an explicit StopScreens pause, which the apply's screen-scene commit resumes exactly like
    /// SetScreenScene; in each case the family's ControlVerified owner flag must be set. Refused: Disabled output (never enabled
    /// here), Blocked (another owner), Error, Disconnected, Unsupported, Stopped by a fan-LCD restart, or control not verified.
    /// </summary>
    internal string? ScreenUnavailable(ScreenDeviceDto device)
    {
        var state = device.State switch
        {
            ScreenStates.Idle or ScreenStates.Running or ScreenStates.Sleeping => null,
            ScreenStates.Stopped when _media?.RestartHolds(device.Id) == true => "is Stopped by a fan-LCD restart in progress",
            ScreenStates.Stopped => null, // a StopScreens pause: the apply resumes it (no output gate is changed)
            _ => $"is {device.State}{(device.BlockedBy is { } by ? " (" + by + ")" : "")}, not ready for output",
        };
        return state ?? (device.ControlVerified ? null : "is not control-verified for its family (ControlVerified=false; never enabled here)");
    }

    private async Task<InstalledScenePackage> RequireAsync(string contentId, CancellationToken ct)
    {
        var (ok, package, problem) = await Task.Run(() => (Store.TryGet(contentId, out var p, out var why), p, why), ct).ConfigureAwait(false);
        return ok ? package : throw new ArgumentException(problem ?? "Unknown scene package.", nameof(contentId));
    }

    /// <summary>Package timelines active now: the RGB scene's provenance and every persisted screen assignment's TimelineId.</summary>
    private HashSet<string> ActiveTimelineIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_engine.Loop.Scene.Package is { } pk)
        {
            ids.Add(ScenePackageIds.TimelineIdOf(pk.ContentId, pk.BindingId));
        }

        foreach (var a in _screenScenes?.CurrentForScene()?.Assignments ?? [])
        {
            if (ScenePackageIds.IsTimelineId(a.TimelineId))
            {
                ids.Add(a.TimelineId!);
            }
        }

        return ids;
    }

    /// <summary>Re-verifies each active timeline and re-installs its screen media (idempotent by content), then warms it.</summary>
    private async Task HydrateAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!_source.TryResolve(id, out var package, out _, out var problem))
                {
                    Block(id, problem ?? "the package content cannot be resolved");
                    continue;
                }

                foreach (var s in package.Manifest.Screens)
                {
                    await InstallMediaAsync(package, s, ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                if (_timeline.Warm(id) is { } unresolved)
                {
                    LogActiveBlocked(id, unresolved);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
#pragma warning disable CA1031 // a hydration failure (corrupt original, importer/store refusal) blocks that timeline, labelled; startup continues
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Block(id, $"package media could not be re-installed from its immutable original ({ex.Message})");
            }
            finally { _hydrating.TryRemove(id, out _); }
        }

        void Block(string id, string why)
        {
            _timeline.Block(id, "startup: " + why);
            LogActiveBlocked(id, why);
        }
    }

    private static ScenePackageInfoDto InfoOf(InstalledScenePackage p) =>
        new(p.ContentId, p.Manifest.Id, p.Manifest.Name, p.Manifest.Revision, ScenePackageFormat.PlaybackContract, p.Manifest.DurationMs,
            p.Manifest.Lighting.Count, p.Manifest.Screens.Count, p.Manifest.SchemaVersion);

    private static ScenePackageDetailsDto DetailsOf(InstalledScenePackage p)
    {
        var roles = new List<ScenePackageRoleDto>();
        foreach (var l in p.Manifest.Lighting)
        {
            var rgb = p.Lighting[l.File];
            var colors = new string[l.LedCount];
            for (var i = 0; i < l.LedCount; i++)
            {
                colors[i] = string.Create(CultureInfo.InvariantCulture, $"#{rgb[i * 3]:X2}{rgb[(i * 3) + 1]:X2}{rgb[(i * 3) + 2]:X2}");
            }

            roles.Add(new ScenePackageRoleDto(l.Role, l.Label ?? l.Role, KindLighting, l.LedCount, 0, 0, l.Frames, p.Manifest.DurationMs, colors));
        }

        for (var i = 0; i < p.Manifest.Screens.Count; i++)
        {
            var s = p.Manifest.Screens[i];
            var media = p.Media[i];
            var thumb = p.Thumbnail(s.Role);
            roles.Add(new ScenePackageRoleDto(s.Role, s.Label ?? s.Role, KindScreen, 0, media.Width, media.Height, media.Frames, s.DurationMs, [],
                thumb is null ? null : Convert.ToBase64String(thumb)));
        }

        return new ScenePackageDetailsDto(InfoOf(p), roles);
    }

    private static ScenePackageBindingDto ToDto(BindingEntry e) =>
        new(e.Role, e.Zone is { } z ? new ZoneRefDto(z.Controller.Value, z.Zone.Value) : null, e.ScreenId);

    [LoggerMessage(EventId = 4111, Level = LogLevel.Information, Message = "Scene package {ContentId} ({Id} r{Revision}) imported (new entry: {Created})")]
    private partial void LogImported(string contentId, string id, int revision, bool created);

    [LoggerMessage(EventId = 4112, Level = LogLevel.Information, Message = "Scene package {ContentId} binding snapshot {BindingId} saved as current")]
    private partial void LogBindingSaved(string contentId, string bindingId);

    [LoggerMessage(EventId = 4113, Level = LogLevel.Information, Message = "Scene package {ContentId} applied with binding {BindingId}; timeline {Source}")]
    private partial void LogApplied(string contentId, string bindingId, string source);

    [LoggerMessage(EventId = 4114, Level = LogLevel.Error, Message = "Active scene package timeline {Id} is blocked: {Reason}")]
    private partial void LogActiveBlocked(string id, string reason);

    [LoggerMessage(EventId = 4115, Level = LogLevel.Warning,
        Message = "Startup hydration of {Count} active package timeline(s) exceeded {Seconds} s; their screens stay blocked until it completes")]
    private partial void LogHydrationSlow(int count, double seconds);
}
