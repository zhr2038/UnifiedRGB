using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Encodes one composed canvas for the wire: the production <see cref="ScreenFrameEncoder"/> (or a test double).</summary>
internal delegate EncodedFrame ScreenEncodeFunc(ScreenFamilyInfo family, PixelFrame canvas, int turzxLandscapeRotation, string quality, bool indexed8Verified);

/// <summary>
/// Local per-screen device settings (<see cref="IScreenDeviceSettingsService"/>): the content rotation and encoder policy of each
/// physical screen, kept apart from scene content, profiles, scene packages, package bindings and content hashes.
/// <list type="number">
/// <item>A SAVED local setting is authoritative. Every screen-scene commit (SetScreenScene, RGB SetScene/ApplyProfile with
/// Screens, scene packages, the startup restore of the persisted scene) shows the content at the saved rotation. Candidates are
/// normalized when they are built (<see cref="NormalizeToLocal"/>) and AGAIN under the mutation gate in BeginSceneAsync, so a
/// concurrent settings save is never lost.</item>
/// <item>A screen WITHOUT a saved setting keeps the old behaviour: the candidate's own Rotation (a scene package's is 0). Package
/// applies, profile restores and RGB scenes never write a setting.</item>
/// <item>The direct legacy <see cref="SetScreenSceneAsync"/> (an old client's explicit choice) establishes a MISSING setting from a
/// nonzero Rotation; 0 is "no preference" and establishes nothing. Once a setting exists, legacy rotations no longer change it:
/// only <see cref="SetScreenDeviceSettingsAsync"/> does.</item>
/// <item>First run (no settings file): settings are seeded from the nonzero rotations of the PERSISTED screen scene and the file
/// is written (even when empty), so this migration runs once. No device I/O; an unreadable file is never migrated over.</item>
/// </list>
/// The encoder policy reaches display workers and preview from memory (<see cref="EncodingOf"/>). The palette research token is
/// not selectable; the capability flag alone never enables it.
/// </summary>
public sealed partial class ScreenManager : IScreenDeviceSettingsService
{
    // Immutable snapshot replaced under _gate (and only under the mutation gate); display workers read it lock-free.
    private IReadOnlyDictionary<string, ScreenDeviceSetting>? _deviceSettings;

    /// <summary>Replaces the production encoder for workers and preview (tests only; null in production).</summary>
    internal ScreenEncodeFunc? EncoderForTest { get; set; }

    /// <summary>The settings file (diagnostics/tests).</summary>
    internal string DeviceSettingsPath => _settingsStore.Path;

    public Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string screenId, CancellationToken ct = default)
    {
        if (!ScreenIds.TryParse(screenId, out _, out _))
        {
            throw new ArgumentException($"'{screenId}' is not a screen id (screen:tl-lcd|h2|turzx:<instance>).", nameof(screenId));
        }

        ScreenDeviceSetting setting;
        lock (_gate)
        {
            EnsureScene();
            setting = _deviceSettings!.GetValueOrDefault(screenId) ?? ScreenDeviceSetting.Default;
        }

        return Task.FromResult(Dto(screenId, setting));
    }

    /// <summary>
    /// Validates and saves one screen's settings, then updates only that screen's ACTIVE assignment orientation (content, overlay,
    /// brightness, timeline and every other screen unchanged) and wakes its worker with the CURRENT pause flag. Never adds an
    /// assignment, starts an unassigned screen, resumes a StopScreens pause or touches output gates or RGB. Invalid input, an
    /// unverified palette policy or a full store throws ArgumentException; a failed file write throws InvalidOperationException. Either
    /// way nothing changes.
    /// </summary>
    public async Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = settings.ScreenId;
        if (ScreenDeviceSettingsStore.Problem(id, settings.Rotation, settings.EncodingQuality) is { } problem)
        {
            throw new ArgumentException(problem + " Nothing was changed.", nameof(settings));
        }

        // the palette policy is not selectable today (IsKnown refuses it above); this gate also holds if a later version allows it
        if (settings.EncodingQuality == ScreenEncodingPolicies.PaletteFirstV1 && !_indexed8Verified.Contains(id))
        {
            throw new ArgumentException(
                $"{id}: {ScreenEncodingPolicies.PaletteFirstV1} needs this exact screen id in UnifiedRgb:Screens:Indexed8VerifiedScreenIds " +
                "(hardware Indexed8 output verified by the owner). Refused; nothing was changed.", nameof(settings));
        }

        if (settings.EncodingQuality == ScreenEncodingPolicies.NativeH264IntraV1 && !NativeVideoVerified(id))
            throw new ArgumentException("Native H.264 is available only for an individually verified H2/TURZX with clock/loop/live-information acceptance. Nothing was changed.", nameof(settings));

        var wanted = new ScreenDeviceSetting(settings.Rotation, settings.EncodingQuality);
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_publicationGate)
            {
            ObjectDisposedException.ThrowIf(_stopped || _disposed, this);
            Dictionary<string, ScreenDeviceSetting> next;
            ScreenSceneDto normalized;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureScene();
                var current = _deviceSettings!;
                if (current.TryGetValue(id, out var saved) && saved == wanted)
                {
                    return Dto(id, saved); // unchanged: no write, no worker update
                }

                if (!current.ContainsKey(id) && current.Count >= ScreenDeviceSettingsStore.MaxEntries)
                {
                    throw new ArgumentException(
                        $"At most {ScreenDeviceSettingsStore.MaxEntries} screens can have local settings. Refused; nothing was changed.", nameof(settings));
                }

                next = new Dictionary<string, ScreenDeviceSetting>(current, StringComparer.Ordinal) { [id] = wanted };
                normalized = NormalizeWith(_scene!, next);
            }

            // file I/O outside _gate, still under the mutation gate (no scene commit can interleave); a failure changes nothing
            try
            {
                List<SceneStateFile> files = [new("screens/device-settings.json", ScreenDeviceSettingsStore.Serialize(next))];
                if (!ReferenceEquals(normalized, _scene)) files.Add(new("screens/scene.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(normalized, ContractsJsonContext.Default.ScreenSceneDto)));
                ct.ThrowIfCancellationRequested();
                SceneStateFiles.Commit(_stateRoot, files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The local screen settings could not be saved ({ex.Message}). Nothing was changed.", ex);
            }

            ScreenSceneDto? changed = null;
            lock (_gate)
            {
                Volatile.Write(ref _deviceSettings, next);
                AbortVideoTrialOf(id); // an explicit local setting change supersedes a transient test transport
                if (!ReferenceEquals(normalized, _scene))
                {
                    _scene = normalized;
                    changed = normalized;
                }

                if (_workers.TryGetValue(id, out var w))
                {
                    w.Post(AssignmentFor(id), _paused); // new orientation and/or encoder policy; the pause flag is kept
                }
            }

            LogDeviceSettingsSaved(_log, id, wanted.Rotation, wanted.EncodingQuality, changed is not null);
            return Dto(id, wanted);
            }
        }
        finally
        {
            _mutation.Release();
        }
    }

    /// <summary>
    /// The candidate with each assignment's Rotation replaced by its screen's SAVED local rotation; screens without a saved setting
    /// are unchanged. No validation and no I/O beyond the first load of the settings.
    /// </summary>
    public ScreenSceneDto NormalizeToLocal(ScreenSceneDto scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            EnsureScene();
            return NormalizeHeld(scene);
        }
    }

    /// <summary>The encoder policy of <paramref name="screenId"/> from memory (display workers, preview). Legacy until loaded.</summary>
    internal ScreenEncodingSelection EncodingOf(string screenId)
    {
        if (VideoTrialOf(screenId) is not null) return new(ScreenEncodingPolicies.NativeH264IntraV1, false);
        var settings = Volatile.Read(ref _deviceSettings);
        var quality = settings is not null && settings.TryGetValue(screenId, out var s) ? s.EncodingQuality : ScreenEncodingPolicies.Legacy;
        var verified = _indexed8Verified.Contains(screenId);
        return quality == ScreenEncodingPolicies.Legacy && !verified ? ScreenEncodingSelection.Legacy : new ScreenEncodingSelection(quality, verified);
    }

    private EncodedFrame Encode(ScreenFamilyInfo family, PixelFrame canvas, int turzxLandscapeRotation, string quality, bool indexed8Verified) =>
        EncoderForTest is { } test
            ? test(family, canvas, turzxLandscapeRotation, quality, indexed8Verified)
            : ScreenFrameEncoder.Encode(family, canvas, turzxLandscapeRotation, quality, indexed8Verified);

    private ScreenDeviceSettingsDto Dto(string screenId, ScreenDeviceSetting s) =>
        new(screenId, s.Rotation, s.EncodingQuality, _indexed8Verified.Contains(screenId), NativeVideoVerified(screenId),
            ScreenIds.TryParse(screenId, out var family, out _) && family.Codec is ScreenCodec.TlJpeg or ScreenCodec.H2Jpeg);

    /// <summary>_gate held. Each assignment with a saved setting at that rotation; the same instance when nothing changes.</summary>
    private ScreenSceneDto NormalizeHeld(ScreenSceneDto scene)
        => NormalizeWith(scene, _deviceSettings!);

    private static ScreenSceneDto NormalizeWith(ScreenSceneDto scene, IReadOnlyDictionary<string, ScreenDeviceSetting> settings)
    {
        ScreenAssignmentDto[]? copy = null;
        for (var i = 0; i < scene.Assignments.Count; i++)
        {
            var a = scene.Assignments[i];
            if (a is not null && a.ScreenId is not null && settings.TryGetValue(a.ScreenId, out var s) && s.Rotation != a.Rotation)
            {
                copy ??= [.. scene.Assignments];
                copy[i] = a with { Rotation = s.Rotation };
            }
        }

        return copy is null ? scene : new ScreenSceneDto(copy);
    }

    /// <summary>
    /// _gate held. The settings after the direct legacy SetScreenScene of <paramref name="scene"/>: a missing setting is established
    /// from a nonzero Rotation (encoder policy legacy). Null when nothing is established. A full store refuses the scene.
    /// </summary>
    private Dictionary<string, ScreenDeviceSetting>? EstablishedHeld(ScreenSceneDto scene)
    {
        var current = _deviceSettings!;
        Dictionary<string, ScreenDeviceSetting>? next = null;
        foreach (var a in scene.Assignments)
        {
            if (a.Rotation != 0 && !current.ContainsKey(a.ScreenId))
            {
                next ??= new Dictionary<string, ScreenDeviceSetting>(current, StringComparer.Ordinal);
                next[a.ScreenId] = new ScreenDeviceSetting(a.Rotation, ScreenEncodingPolicies.Legacy);
            }
        }

        if (next is not null && next.Count > ScreenDeviceSettingsStore.MaxEntries)
        {
            throw new ArgumentException($"At most {ScreenDeviceSettingsStore.MaxEntries} screens can have local settings; the scene's new rotations cannot be kept.", nameof(scene));
        }

        return next;
    }

    /// <summary>_gate held, once: loads the settings or, on the first run, seeds them from the persisted scene and writes the file.</summary>
    private void LoadDeviceSettingsHeld(ScreenSceneDto persisted)
    {
        if (_deviceSettings is not null)
        {
            return;
        }

        var loaded = _settingsStore.Load();
        if (loaded is null)
        {
            loaded = new Dictionary<string, ScreenDeviceSetting>(StringComparer.Ordinal);
            foreach (var a in persisted.Assignments)
            {
                if (a.Rotation != 0 && loaded.Count < ScreenDeviceSettingsStore.MaxEntries)
                {
                    loaded.TryAdd(a.ScreenId, new ScreenDeviceSetting(a.Rotation, ScreenEncodingPolicies.Legacy));
                }
            }

            try
            {
                _settingsStore.Save(loaded);
                LogDeviceSettingsMigrated(_log, loaded.Count, _settingsStore.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogDeviceSettingsSaveFailed(_log, _settingsStore.Path, ex.Message); // in memory now; the next start seeds again
            }
        }

        Volatile.Write(ref _deviceSettings, loaded);
    }

    private static HashSet<string> Indexed8Ids(ScreensOptions options, ILogger log)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in options.Indexed8VerifiedScreenIds ?? [])
        {
            if (ScreenIds.TryParse(id, out _, out _))
            {
                ids.Add(id);
            }
            else
            {
                LogIndexed8IdIgnored(log, id ?? string.Empty);
            }
        }

        return ids;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local screen settings of {Id} saved: rotation {Rotation}, encoding {Quality} (active assignment updated: {Updated})")]
    private static partial void LogDeviceSettingsSaved(ILogger logger, string id, int rotation, string quality, bool updated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Local screen settings created from the persisted screen scene ({Count} nonzero rotations) at {Path}")]
    private static partial void LogDeviceSettingsMigrated(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local screen settings could not be saved to {Path}: {Reason}")]
    private static partial void LogDeviceSettingsSaveFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UnifiedRgb:Screens:Indexed8VerifiedScreenIds entry '{Id}' is not an exact screen id; ignored")]
    private static partial void LogIndexed8IdIgnored(ILogger logger, string id);
}
