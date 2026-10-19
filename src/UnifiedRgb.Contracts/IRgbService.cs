using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>
/// JSON-RPC surface of the Host over <c>\.\pipe\UnifiedRgb</c> (ARCHITECTURE §6). Method names on the wire drop the
/// "Async" suffix (e.g. "ListControllers"); events arrive as notifications (<see cref="RgbNotifications"/>).
/// </summary>
public interface IRgbService
{
    /// <summary>Raised after discovery, rescan, hotplug, takeover or a guard re-scan changes any controller state.</summary>
    event EventHandler<IReadOnlyList<ControllerDto>>? ControllersChanged;

    /// <summary>Raised every second with the same content as status.json.</summary>
    event EventHandler<StatusDto>? StatusChanged;

    /// <summary>Raised every second with read-only telemetry of active controllers that provide it.</summary>
    event EventHandler<IReadOnlyList<TelemetryDto>>? TelemetryTick;

    Task<IReadOnlyList<ControllerDto>> ListControllersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<EffectDto>> ListEffectsAsync(CancellationToken ct = default);

    Task<SceneDto> GetSceneAsync(CancellationToken ct = default);

    /// <summary>Replaces the whole scene; every target zone must exist. Persisted to active.json.</summary>
    Task SetSceneAsync(SceneDto scene, CancellationToken ct = default);

    /// <summary>Puts one assignment on its target zones, removing those zones from any other assignment. Persisted.</summary>
    Task SetAssignmentAsync(AssignmentDto assignment, CancellationToken ct = default);

    Task<IReadOnlyList<ProfileInfoDto>> ListProfilesAsync(CancellationToken ct = default);

    Task SaveProfileAsync(ProfileDto profile, CancellationToken ct = default);

    Task<ProfileDto> LoadProfileAsync(string name, CancellationToken ct = default);

    /// <summary>Loads a profile and makes it the running scene (zones of offline controllers are kept for later).</summary>
    Task ApplyProfileAsync(string name, CancellationToken ct = default);

    Task<StatusDto> GetStatusAsync(bool diagnostics = false, CancellationToken ct = default);

    Task<IReadOnlyList<TelemetryDto>> GetTelemetryAsync(CancellationToken ct = default);

    /// <summary>Per device class ("LianLi", "MsiGpu", "OpenRgb", "AsusAura", "EneDram"); persisted; off by default.</summary>
    Task<TakeoverDto> SetTakeoverAsync(string deviceClass, bool enabled, CancellationToken ct = default);

    /// <summary>Re-runs WriterGuard and discovery on every adapter.</summary>
    Task<IReadOnlyList<ControllerDto>> RescanAsync(CancellationToken ct = default);

    /// <summary>
    /// "Save current lighting to fans" (ARCHITECTURE-D2 §5): RF 21 to every Lian Li sender that completed an upload. Refused
    /// unless WireFormatVerified, after an upload, at most once per 60 s. Returns the number of senders saved.
    /// </summary>
    Task<int> SaveLianLiConfigAsync(CancellationToken ct = default);

    /// <summary>"Re-sync Lian Li" (D2 §2): re-uploads every current loop with a fresh EffectIndex. Returns the loops queued.</summary>
    Task<int> ResyncLianLiAsync(CancellationToken ct = default);

    /// <summary>Mounting of every discovered Lian Li device (WP-11).</summary>
    Task<IReadOnlyList<MountingDto>> ListMountingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates and applies a device's mounting, persists it to mounting.json and re-uploads the affected loops with a fresh
    /// EffectIndex (no RF 21 flash save). Invalid angle, fan slot or a non-exact transform (TL 90°/270°) → ArgumentException.
    /// </summary>
    Task<MountingDto> SetMountingAsync(MountingDto mounting, CancellationToken ct = default);

    /// <summary>Drops the GUI override (back to appsettings / identity) and re-uploads.</summary>
    Task<MountingDto> ResetMountingAsync(string controllerId, CancellationToken ct = default);

    /// <summary>
    /// WP-14 (H2-VERTICAL-FILL §3): shows a calibration step on an H2 block (one-frame loop through the normal upload path, with
    /// the candidate rotation/offset/half-step as a transient overlay). Repeatable for each ◀/▶ press. Nothing is saved; the
    /// mounting revision is unchanged. Non-ring devices → ArgumentException.
    /// </summary>
    Task<MountingDto> BeginRingCalibrationAsync(RingCalibrationDto calibration, CancellationToken ct = default);

    /// <summary>
    /// Ends a calibration. <paramref name="apply"/>: saves the last candidate offset/half-step with FaceOnFill on (revision +1)
    /// and re-uploads; otherwise drops it and re-uploads the unchanged scene (revision unchanged).
    /// </summary>
    Task<MountingDto> EndRingCalibrationAsync(string controllerId, bool apply, CancellationToken ct = default);

    /// <summary>
    /// NATIVE design §6: begins a leased bring-up session on a native controller for this connection. Requires the class enabled,
    /// its takeover on and the guard verdict Clear; ControlVerified is not required (the session is how an unverified adapter is
    /// opened). The controller is held: no scene frame reaches it. Another connection's session on it → InvalidOperationException.
    /// </summary>
    Task<BringupSessionDto> BeginBringupAsync(string controllerId, int leaseSeconds, CancellationToken ct = default);

    /// <summary>Runs one named probe in this connection's session; a failed probe releases and ends the session.</summary>
    Task<ProbeResultDto> RunNativeProbeAsync(string controllerId, string probe, CancellationToken ct = default);

    /// <summary>Writes one bounded marker in this connection's session; a failed step releases and ends the session.</summary>
    Task<ProbeResultDto> BeginMarkerAsync(MarkerDto marker, CancellationToken ct = default);

    /// <summary>Writes black over the last marker's range.</summary>
    Task<ProbeResultDto> EndMarkerAsync(string controllerId, CancellationToken ct = default);

    /// <summary>Extends this connection's lease by its length.</summary>
    Task<BringupSessionDto> RenewBringupAsync(string controllerId, CancellationToken ct = default);

    /// <summary>Releases and ends this connection's session; idempotent (<c>AlreadyEnded</c> when none runs).</summary>
    Task<BringupEndDto> EndBringupAsync(string controllerId, CancellationToken ct = default);

    /// <summary>
    /// NATIVE-PLAYBACK-CALIBRATION C5: the manual streamed reference-clock calibration in force, one coherent snapshot
    /// (initialised from LianLi:StreamedPlaybackRate / LianLi:PhaseTrimMs at start).
    /// </summary>
    Task<PlaybackCalibrationDto> GetPlaybackCalibrationAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the calibration live and returns the snapshot now in force. Both values are validated first (finite rate in
    /// [0.25, 4], finite offset in [−2000, 2000] ms); an invalid one → ArgumentOutOfRangeException with nothing changed. No RF
    /// re-upload, anchor reset or restart; runtime-only, nothing is written to disk.
    /// </summary>
    Task<PlaybackCalibrationDto> SetPlaybackCalibrationAsync(PlaybackCalibrationDto calibration, CancellationToken ct = default);
}
