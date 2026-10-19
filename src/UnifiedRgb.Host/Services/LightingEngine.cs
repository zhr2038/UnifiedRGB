using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Host start/stop order (ARCHITECTURE §9): load active.json → WriterGuard + discovery (AdapterHost) → render plan → clock →
/// scene. Shutdown stops the clock first so the devices hold the last frame, ends native bring-up sessions, releases writers
/// and handles (GPU, ENE, Aura, then the rest), then restores the services Takeover stopped — except with
/// Bringup:HoldBlackOnRelease, where the restore is deferred and its records kept (overnight addendum §4, §9). Scene changes
/// are mirrored to active.json. Lian Li fans keep playing their last uploaded loop after shutdown; nothing is written to them
/// on stop (ARCHITECTURE-D2 §7).
/// </summary>
public sealed partial class LightingEngine(
    UnifiedRgbOptions options,
    AdapterHost adapters,
    FrameBus bus,
    EffectRegistry registry,
    IClock clock,
    ProfileStore profiles,
    MountingStore mountings,
    IWriterGuard guard,
    RgbNotifier notifier,
    ILogger<LightingEngine> logger,
    IFrameSetSource? frameSets = null) : IHostedService, IDisposable
{
    public const string CustomProfileName = "Custom";

    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;

    private readonly RenderLoop _loop = new(registry, bus);
    private readonly Stopwatch _uptime = new();
    private readonly SemaphoreSlim _scan = new(1, 1);
    private readonly object _sceneGate = new();
    private string _activeName = CustomProfileName;
    private Timer? _reanchor;
    private BringupManager? _bringup;

    public EffectRegistry Effects => registry;

    public AdapterHost Adapters => adapters;

    public RenderLoop Loop => _loop;

    /// <summary>Native bring-up sessions (NATIVE design §6); created on first use, ended before the adapters close.</summary>
    public BringupManager Bringup => LazyInitializer.EnsureInitialized(ref _bringup, () =>
        new BringupManager(adapters, _loop, guard, options.Bringup, ProbeLog.For(options, _logger), TimeProvider.System, _logger));

    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;

    /// <summary>Name of the profile the running scene came from ("Custom" after direct edits).</summary>
    public string ActiveProfileName => _activeName;

    /// <summary>The manual streamed reference-clock calibration in force (NATIVE-PLAYBACK-CALIBRATION C2).</summary>
    public PlaybackCalibration PlaybackCalibration => _loop.PlaybackCalibration;

    /// <summary>
    /// Replaces the calibration live (C4): validated before any change, applied from the next frame without RF re-upload,
    /// anchor reset or restart. Runtime-only: no options, appsettings or ProgramData file is written.
    /// </summary>
    public PlaybackCalibration SetPlaybackCalibration(PlaybackCalibration calibration)
    {
        var previous = _loop.PlaybackCalibration;
        var now = _loop.SetPlaybackCalibration(calibration);
        LogCalibrationSet(previous.StreamedPlaybackRate, previous.PhaseTrimMs, now.StreamedPlaybackRate, now.PhaseTrimMs);
        return now;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SceneStateFiles.Recover(options.ResolvedDataDirectory); // incomplete disk commits must be recovered before any adapter opens
        _uptime.Start();
        ApplyConfiguredCalibration();
        if (options.LianLi.UseDeviceClock)
        {
            LogDeviceClockEnabled(RenderLoop.DeviceClockMaxAgeSeconds);
        }

        _loop.ChainReversed = ChainReversed;
        _loop.Ring = RingOf;
        adapters.LoopUploaded += OnLoopUploaded;
        adapters.AdapterCreated += SeedMountings;
        if (frameSets is not null)
        {
            _loop.FrameSets = frameSets; // OPUS-GENERIC-SCENE-PACKAGES: the durable source is wired before active.json is applied
        }

        var restored = LoadActive();
        await _scan.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await adapters.StartAsync(cancellationToken).ConfigureAwait(false);
            RefreshControllers();
        }
        finally
        {
            _scan.Release();
        }

        ApplyStartupScene(restored);
        _loop.Attach(clock);
        clock.Start(options.Fps);
        var every = options.LianLi.ReanchorMinutes;
        if (every > 0 && double.IsFinite(every))
        {
            var period = TimeSpan.FromMinutes(Math.Max(every, 1.0 / 60));
            _reanchor = new Timer(_ => ResyncLoops(), null, period, period);
        }

        LogStarted(_loop.Controllers.Count, adapters.Controllers.Count, options.Fps);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_reanchor is { } timer)
        {
            _reanchor = null;
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        adapters.LoopUploaded -= OnLoopUploaded;
        adapters.AdapterCreated -= SeedMountings;
        clock.Stop();
        _loop.Detach();
        adapters.BeginStop(); // nothing is opened or reopened from here on, including by an ending bring-up session
        if (Volatile.Read(ref _bringup) is { } bringup)
        {
            await bringup.EndAllAsync("host stop").ConfigureAwait(false);
        }

        await adapters.StopAsync().ConfigureAwait(false);
        if (options.Bringup.HoldBlackOnRelease)
        {
            // addendum §4/§9: vendor services must not start and repaint the dark devices; takeover.json keeps every record
            LogRestoreDeferred();
        }
        else
        {
            try
            {
                await guard.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // records stay in takeover.json; the next clean exit or uninstall.ps1 retries
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRestoreFailed(ex);
            }
        }

        LogStopped();
    }

    /// <summary>Validates (every zone must exist) and swaps the scene atomically, then persists it to active.json.</summary>
    public void SetScene(Scene scene)
        => ChangeScene(scene, CustomProfileName, allowMissing: false, null);

    internal void ChangeScene(Scene scene, string name, bool allowMissing, IScreenSceneTransaction? screens, Action? beforeScreens = null, CancellationToken cancellationToken = default)
    {
        lock (_sceneGate)
        {
            var prepared = screens as IPreparedScreenSceneTransaction;
            void Commit()
            {
                ValidateStrict(scene, nameof(scene));
                _loop.SetScene(scene, allowMissing, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested(); // cancellation stops admission, never interrupts a durable commit halfway
                    profiles.SaveActiveWith(new ProfileDto(name, DtoMapper.ToDto(scene)), prepared?.Files ?? []);
                });
                _startupBlocked = null;
                _activeName = name;
                beforeScreens?.Invoke();
                if (prepared is not null) prepared.Publish();
                else screens?.Commit();
            }
            if (prepared is not null) prepared.Coordinate(Commit);
            else Commit();
        }
    }

    /// <summary>Puts <paramref name="assignment"/> on its zones (which must exist), taking them out of every other assignment.</summary>
    public void SetAssignment(Assignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var known = _loop.Controllers.SelectMany(c => c.Zones.Select(z => new ZoneRef(c.Id, z.Id))).ToHashSet();
        foreach (var t in assignment.Targets)
        {
            if (!known.Contains(t))
            {
                throw new ArgumentException($"Unknown zone {t}.", nameof(assignment));
            }
        }

        lock (_sceneGate)
        {
            var merged = Merge(_loop.Scene, assignment);
            ValidateStrict(merged, nameof(assignment));
            _loop.SetScene(merged, allowMissingZones: true, () => profiles.SaveActiveWith(new ProfileDto(CustomProfileName, DtoMapper.ToDto(merged)), []));
            _startupBlocked = null;
            _activeName = CustomProfileName;
        }
    }

    /// <summary>Makes a profile the running scene; zones of controllers that are not present yet are kept for later.</summary>
    public void ApplyProfile(ProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var scene = DtoMapper.FromDto(profile.Scene);
        ChangeScene(scene, profile.Name, allowMissing: true, null);
    }

    public static Scene Merge(Scene scene, Assignment assignment)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(assignment);
        var taken = assignment.Targets.ToHashSet();
        var kept = scene.Assignments
            .Select(a => a.Targets.Any(taken.Contains)
                ? a with { Targets = a.Targets.Where(t => !taken.Contains(t)).ToArray(), Path = a.Path?.Without(taken) } // SPATIAL-PATH: unbind them too
                : a)
            .Where(a => a.Targets.Count > 0);
        return new Scene([.. kept, assignment]) { Package = scene.Package };
    }

    /// <summary>Full rescan (RPC Rescan, hotplug). Serialised with <see cref="RecheckAsync"/>.</summary>
    public async Task<bool> RescanAsync(CancellationToken ct)
    {
        await _scan.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return AfterScan(await adapters.RescanAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            _scan.Release();
        }
    }

    /// <summary>
    /// Takeover switched off for <paramref name="cls"/> (Codex review): serialised with the scans so nothing reopens in
    /// between, the class's bring-up sessions end without reopening, its native writers are quiesced and closed, and only then
    /// does the guard restore vendor services (shared MSI NeededBy bookkeeping is the guard's; a service another class still
    /// needs stays stopped).
    /// </summary>
    public async Task<TakeoverResult> SetTakeoverOffAsync(DeviceClass cls, CancellationToken ct)
    {
        await _scan.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Bringup.EndClassAsync(cls, "takeover switched off").ConfigureAwait(false);
            await adapters.CloseClassAsync(cls, $"takeover off for {cls}").ConfigureAwait(false);
            return await guard.SetTakeoverAsync(cls, false, ct).ConfigureAwait(false);
        }
        finally
        {
            _scan.Release();
        }
    }

    /// <summary>The 5 s WriterGuard re-scan (pause/resume, open newly cleared controllers).</summary>
    public async Task<bool> RecheckAsync(CancellationToken ct)
    {
        await _scan.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return AfterScan(await adapters.RecheckAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            _scan.Release();
        }
    }

    /// <summary>
    /// Re-uploads every UploadedLoop zone's current loop with a fresh EffectIndex (GUI "Re-sync Lian Li",
    /// LianLi:ReanchorMinutes, hotplug reconnect). Returns the loops published.
    /// </summary>
    public int ResyncLoops()
    {
        lock (_sceneGate)
        {
            var report = _loop.RepublishLoops();
            if (report is { Published: > 0 })
            {
                LogLoopsResynced(report.Published, report.EffectIndex);
            }

            return report?.Published ?? 0;
        }
    }

    /// <summary>Mounting of every discovered Lian Li device (WP-11).</summary>
    public IReadOnlyList<MountingDto> ListMountings()
    {
        var stored = LoadMountings();
        return adapters.Controllers
            .Where(e => e.Adapter is LianLiAdapter)
            .Select(e => ToDto(e, ((LianLiAdapter)e.Adapter).MountingOf(e.Descriptor.Id), stored.ContainsKey(e.Descriptor.Id.Value)))
            .ToArray();
    }

    /// <summary>
    /// Validates (in the adapter, against the device's family) and applies a mounting, persists it, then forces a recompile
    /// and re-upload of every assignment covering the device with one fresh shared EffectIndex (DEBUG-SESSION-1 §3.3: the
    /// canonical RGB is unchanged, so SameContent would otherwise suppress the upload). <paramref name="mounting"/> null =
    /// reset to the appsettings default.
    /// </summary>
    public MountingDto SetMounting(string controllerId, MountingDto? mounting)
    {
        if (!MountingStore.IsValidId(controllerId))
        {
            throw new ArgumentException($"'{controllerId}' is not a Lian Li controller ID.", nameof(controllerId));
        }

        var id = new ControllerId(controllerId);
        var entry = adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id == id && e.Adapter is LianLiAdapter)
            ?? throw new ArgumentException($"No discovered Lian Li device {controllerId}.", nameof(controllerId));
        var adapter = (LianLiAdapter)entry.Adapter;
        lock (_sceneGate)
        {
            var rev = adapter.SetMounting(id, mounting is null ? null : FromDto(mounting)); // throws before anything is saved
            mountings.Save(controllerId, mounting);
            var report = _loop.RepublishLoops(id);
            LogMountingApplied(controllerId, rev.Revision, rev.Description, report?.Published ?? 0);
            notifier.PublishControllers(adapters.Controllers.Select(DtoMapper.ToDto).ToArray()); // ZoneDto.ChainReversed → preview
            return ToDto(entry, rev, mounting is not null);
        }
    }

    /// <summary>"Save current lighting to fans": RF 21 through every Lian Li adapter with an open controller (D2 §5 gate inside).</summary>
    public async Task<int> SaveLianLiConfigAsync(CancellationToken ct)
    {
        var lianLi = adapters.Controllers.Where(c => c.IsActive).Select(c => c.Adapter).OfType<LianLiAdapter>().Distinct().ToArray();
        if (lianLi.Length == 0)
        {
            throw new InvalidOperationException("No Lian Li device is open.");
        }

        var saved = 0;
        foreach (var a in lianLi)
        {
            saved += await a.SaveConfigAsync(Environment.UserName, ct).ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>Until a profile exists: one Rainbow group across every active zone in discovery order.</summary>
    public Scene DefaultScene()
    {
        var zones = _loop.Controllers.SelectMany(c => c.Zones.Select(z => new ZoneRef(c.Id, z.Id))).ToArray();
        return zones.Length == 0 ? Scene.Empty : new Scene([Assignment.ForGroup("All", zones, RainbowEffect.EffectId)]);
    }

    public IReadOnlyList<TelemetryDto> ReadTelemetry()
    {
        var list = new List<TelemetryDto>();
        foreach (var e in adapters.Controllers.Where(c => c.IsActive))
        {
            Devices.Telemetry? t;
            try
            {
                t = e.Adapter.ReadTelemetry(e.Descriptor.Id);
            }
#pragma warning disable CA1031 // telemetry is read-only and optional; it never fails the caller
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            if (t is not null)
            {
                list.Add(new TelemetryDto(e.Descriptor.Id.Value, t.Timestamp, t.FanRpm, t.TemperatureC));
            }
        }

        return list;
    }

    public StatusDto BuildStatus(bool diagnostics)
    {
        var cs = clock is MasterClock mc ? mc.GetStats() : default;
        var devices = adapters.Controllers.Select(e =>
        {
            var d = e.Descriptor;
            var budget = (e.Adapter as IFrameBudgetSource)?.FrameBudget(d.Id);
            var lease = e.BringupLease;
            if (e.Writer is null)
            {
                var state = lease is not null ? ControllerStates.BringUp
                    : e.Verdict.State == GuardState.Clear ? ControllerStates.Offline : e.Verdict.State.ToString();
                return WithBudget(new DeviceStatusDto(d.Id.Value, d.Name, state, lease ?? e.Verdict.Writer, 0, d.Capabilities.MaxFps, 0, 0, 0, null), budget);
            }

            var w = e.Writer.GetStats();
            var live = lease is not null ? ControllerStates.BringUp : w.Paused ? ControllerStates.Contended : ControllerStates.Active;
            return WithBudget(new DeviceStatusDto(d.Id.Value, d.Name, live, lease ?? e.Verdict.Writer,
                w.Fps, w.MaxFps, w.Written, w.Errors, w.Superseded, w.LastError, ToDto(w.LastUpload)), w.Budget ?? budget);
        }).ToArray();
        var candidates = guard.RunningCandidates()
            .Select(c => new WriterCandidateDto(c.Name, c.Affects.Select(a => a.ToString()).ToArray(), c.Reason)).ToArray();

        return new StatusDto(
            DateTimeOffset.Now,
            typeof(LightingEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Math.Round(UptimeSeconds, 3),
            _loop.FramesRendered,
            new ClockStatusDto(options.Fps, cs.Fps, cs.Ticks, cs.Overruns, cs.JitterP50Ms, cs.JitterP99Ms, cs.JitterMaxMs),
            devices,
            diagnostics ? PacketLog.Shared.Snapshot().Select(p => new PacketDto(p.Timestamp, p.Channel, p.Opcode, p.Length)).ToArray() : null,
            candidates,
            DtoMapper.ToDto(_loop.PlaybackCalibration),
            DtoMapper.ToDto(_loop.DeviceClockEnabled, _loop.ClockReport()),
            BlockedContent is { Count: > 0 } blocked ? blocked : null);
    }

    /// <summary>Complete-frame timing of a native controller (NATIVE design §9); unchanged for adapters without a budget.</summary>
    public static DeviceStatusDto WithBudget(DeviceStatusDto dto, Devices.Safety.FrameBudgetStats? budget)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return budget is null
            ? dto
            : dto with
            {
                FrameMsP50 = Math.Round(budget.P50Ms, 3),
                FrameMsP99 = Math.Round(budget.P99Ms, 3),
                EffectiveFps = budget.EffectiveFps,
                FpsSource = budget.FpsSource,
                BudgetSkips = budget.Skips,
            };
    }

    public static LoopUploadDto? ToDto(LoopUploadStats? s) => s is null
        ? null
        : new LoopUploadDto(s.At, s.EffectIndex, s.Frames, s.IntervalMs, s.RawBytes, s.CompressedBytes, s.Packets,
            Math.Round(s.Duration.TotalMilliseconds, 1), s.Decimations, s.CodecSha256, Math.Round(s.PeriodErrorMs, 2), s.Mapping,
            s.SourceFrames, s.StaticFallback, s.Geometry, s.ReceiverDelivery is { } r
                ? new RfDeliveryDto(r.State, r.ExpectedEffectIndex, r.ReportedEffectIndex, r.ReceiverCounter, r.ObservedAt, r.Retries, r.Detail)
                : null);

    public void Dispose()
    {
        _reanchor?.Dispose();
        _loop.Dispose();
        _scan.Dispose();
    }

    /// <summary>WP-13: a Lian Li device's whole-chain fan-order reversal, read by the render loop when it slices a group body.</summary>
    private bool ChainReversed(ControllerId id) =>
        adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id == id)?.Adapter is LianLiAdapter lianLi && lianLi.MountingOf(id).Settings.ReverseChain;

    /// <summary>WP-14: a Lian Li device's ring calibration (H2 vertical fill), read by the render loop when it slices a group body.</summary>
    private RingGeometry RingOf(ControllerId id)
    {
        if (adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id == id)?.Adapter is not LianLiAdapter lianLi)
        {
            return default;
        }

        var m = lianLi.MountingOf(id).Settings;
        return new RingGeometry(m.FaceOnFill, m.HalfStep, m.RingOffsetLeds);
    }

    /// <summary>
    /// WP-14 calibration step (H2-VERTICAL-FILL §3): validates the candidate as a transient adapter overlay (same revision),
    /// holds the device in the render loop and uploads the one-frame marker or wedge through the normal path. Nothing is saved.
    /// </summary>
    public MountingDto BeginRingCalibration(RingCalibrationDto c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (entry, adapter) = RingDevice(c.ControllerId);
        var wedge = c.Step switch
        {
            RingCalibrationDto.Marker => false,
            RingCalibrationDto.Wedge => true,
            _ => throw new ArgumentException($"Unknown calibration step '{c.Step}' (marker or wedge).", nameof(c)),
        };
        if (c.RingOffsetLeds is < -3 or > 3)
        {
            throw new ArgumentException("A calibration offset is within -3..+3 LEDs (the rotation carries quarter turns).", nameof(c));
        }

        var id = entry.Descriptor.Id;
        lock (_sceneGate)
        {
            var candidate = adapter.MountingOf(id).Settings.Clone();
            candidate.RotationDegrees = c.RotationDegrees;
            candidate.RingOffsetLeds = c.RingOffsetLeds;
            candidate.HalfStep = c.HalfStep;
            candidate.FaceOnFill = true;
            var overlay = adapter.BeginCalibration(id, candidate); // throws (invalid rotation) before anything is shown
            _loop.PublishCalibration(id, wedge ? RingFillMap.WedgeFrame(c.HalfStep) : RingFillMap.MarkerFrame(c.HalfStep));
            return ToDto(entry, overlay, LoadMountings().ContainsKey(c.ControllerId));
        }
    }

    /// <summary>
    /// Ends a calibration. Apply: drops the overlay, releases the hold and saves the candidate through
    /// <see cref="SetMounting"/> (one revision bump, persisted, re-uploaded). Cancel: drops the overlay and re-uploads the
    /// scene with the unchanged revision.
    /// </summary>
    public MountingDto EndRingCalibration(string controllerId, bool apply)
    {
        var (entry, adapter) = RingDevice(controllerId);
        var id = entry.Descriptor.Id;
        lock (_sceneGate)
        {
            var candidate = adapter.IsCalibrating(id) ? adapter.MountingOf(id).Settings.Clone() : null;
            var persisted = adapter.EndCalibration(id);
            if (apply && candidate is not null)
            {
                _loop.EndCalibration(id, republish: false);
                var current = ToDto(entry, persisted, LoadMountings().ContainsKey(controllerId));
                return SetMounting(controllerId, current with
                {
                    RotationDegrees = candidate.RotationDegrees,
                    RingOffsetLeds = candidate.RingOffsetLeds,
                    HalfStep = candidate.HalfStep,
                    FaceOnFill = true,
                });
            }

            _loop.EndCalibration(id, republish: true);
            return ToDto(entry, persisted, LoadMountings().ContainsKey(controllerId));
        }
    }

    private (ControllerEntry Entry, LianLiAdapter Adapter) RingDevice(string controllerId)
    {
        if (!MountingStore.IsValidId(controllerId))
        {
            throw new ArgumentException($"'{controllerId}' is not a Lian Li controller ID.", nameof(controllerId));
        }

        var id = new ControllerId(controllerId);
        var entry = adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id == id && e.Adapter is LianLiAdapter)
            ?? throw new ArgumentException($"No discovered Lian Li device {controllerId}.", nameof(controllerId));
        if (entry.Descriptor.Zones.Count == 0 || entry.Descriptor.Zones[0].Family != LConnectLoops.H2)
        {
            throw new ArgumentException($"{controllerId} is not an H2 ring; ring calibration applies to H2 blocks only.", nameof(controllerId));
        }

        return (entry, (LianLiAdapter)entry.Adapter);
    }

    private bool AfterScan(bool changed)
    {
        if (changed)
        {
            RefreshControllers();
            notifier.PublishControllers(adapters.Controllers.Select(DtoMapper.ToDto).ToArray());
        }

        // D2 §7: a sender that re-appears gets the current loops again, with a fresh EffectIndex
        var arrived = adapters.TakeArrivals();
        if (arrived.Count > 0 && adapters.Controllers.Any(c => c.IsActive && arrived.Contains(c.Descriptor.Id)
            && c.Descriptor.Capabilities.Delivery == Delivery.UploadedLoop))
        {
            ResyncLoops();
        }

        return changed;
    }

    /// <summary>
    /// D2 §2.3: the loop epoch t0 is the raw MasterClock time the first header went out (stopwatch stamp from the adapter);
    /// nothing is added here. Streamed group members then render at <c>((t − t0 − trim) × rate) mod P</c> with the runtime
    /// <see cref="PlaybackCalibration"/> (NATIVE-PLAYBACK-CALIBRATION C3), so the offset is applied once.
    /// </summary>
    private void OnLoopUploaded(ControllerId id, PendingLoop loop, LoopUploadStats? stats)
    {
        if (stats is null)
        {
            return;
        }

        if (stats.EffectIndex == loop.Loop.EffectIndex)
        {
            // NATIVE-RF-CLOCK R2: what this upload actually transmitted (RenderLoop ignores a superseded index)
            _loop.ReportLoopTiming(id, loop.Zone, stats.EffectIndex, stats.Frames, stats.IntervalMs, stats.StaticFallback);
        }

        if (stats.StartedTimestamp == 0)
        {
            return;
        }

        var ago = (Stopwatch.GetTimestamp() - stats.StartedTimestamp) / (double)Stopwatch.Frequency;
        _loop.AnchorLoop(loop.Loop.EffectIndex, clock.Now - ago);
    }

    /// <summary>
    /// C5: the initial snapshot from LianLi:StreamedPlaybackRate / LianLi:PhaseTrimMs (missing keys bind 1 / 0). Values the
    /// setter would refuse are not applied: the nominal 1 / 0 stays in force and the rejection is logged as an error.
    /// </summary>
    private void ApplyConfiguredCalibration()
    {
        var configured = new PlaybackCalibration(options.LianLi.StreamedPlaybackRate, options.LianLi.PhaseTrimMs);
        var why = configured.Validate();
        if (why is not null)
        {
            LogCalibrationRejected(why);
            return;
        }

        _loop.SetPlaybackCalibration(configured);
        LogCalibrationConfigured(configured.StreamedPlaybackRate, configured.PhaseTrimMs);
    }

    public static LianLiMounting FromDto(MountingDto d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var m = new LianLiMounting
        {
            RotationDegrees = d.RotationDegrees,
            Reverse = d.Reverse,
            ReverseChain = d.ReverseChain,
            RingOffsetLeds = d.RingOffsetLeds,
            HalfStep = d.HalfStep,
            FaceOnFill = d.FaceOnFill,
        };
        foreach (var f in d.Fans ?? [])
        {
            m.Fans.Add(new LianLiFanMounting { Fan = f.Fan, RotationDegrees = f.RotationDegrees, Reverse = f.Reverse });
        }

        return m;
    }

    private static MountingDto ToDto(ControllerEntry e, MountingRevision rev, bool overridden)
    {
        var zone = e.Descriptor.Zones.Count > 0 ? e.Descriptor.Zones[0] : null;
        var note = zone?.Family switch
        {
            "TL" => "TL: 0°/180° and mirror are exact LED permutations; 90°/270° have none (two 13-LED edges) and are refused. "
                + "Reverse fan order flips the whole chain in L-Connect group effects (fan 1 ↔ fan N).",
            "H2" => "H2 block: 24-LED ring, quarter turn = 6 LEDs, fine offset ±3 LEDs. Calibrate ring puts LED 0 at the bottom; "
                + "vertical fill makes translational group effects rise from the bottom. Reverse chain order flips the block's slice in L-Connect group effects.",
            _ => "No confirmed per-LED map for this family; identity only.",
        };
        return new MountingDto(e.Descriptor.Id.Value, rev.Settings.RotationDegrees, rev.Settings.Reverse,
            rev.Settings.Fans.OrderBy(f => f.Fan).Select(f => new FanMountingDto(f.Fan, f.RotationDegrees, f.Reverse)).ToArray(),
            zone?.Family, zone?.Units ?? 0, rev.Revision, rev.Description, overridden, note, rev.Settings.ReverseChain,
            rev.Settings.RingOffsetLeds, rev.Settings.HalfStep, rev.Settings.FaceOnFill);
    }

    /// <summary>mounting.json overrides go into each Lian Li adapter before its first discovery.</summary>
    private void SeedMountings(IDeviceAdapter adapter)
    {
        if (adapter is LianLiAdapter lianLi)
        {
            lianLi.SeedMountings(LoadMountings().ToDictionary(kv => new ControllerId(kv.Key), kv => FromDto(kv.Value)));
        }
    }

    private IReadOnlyDictionary<string, MountingDto> LoadMountings()
    {
        try
        {
            return mountings.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LogMountingUnreadable(ex);
            return new Dictionary<string, MountingDto>();
        }
    }

    private void RefreshControllers()
    {
        var entries = adapters.Controllers;
        var active = entries.Where(c => c.IsActive).Select(c => c.Descriptor).ToArray();
        var clocks = options.LianLi.UseDeviceClock ? DeviceClocks(entries) : null;
        lock (_sceneGate)
        {
            _loop.SetDeviceClocks(clocks);
            _loop.SetControllers(active);
        }
    }

    /// <summary>
    /// NATIVE-RF-CLOCK (LianLi:UseDeviceClock): the stable clock handle of every UploadedLoop controller whose adapter offers
    /// one, resolved here, outside the render lock. A handle follows its controller's open session, so a reopened sender is
    /// picked up without a rebind and a closed one reports no sample (the assignment falls back to its upload anchor).
    /// </summary>
    public static IReadOnlyDictionary<ControllerId, IDeviceClock> DeviceClocks(IEnumerable<ControllerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new Dictionary<ControllerId, IDeviceClock>();
        foreach (var e in entries)
        {
            if (e.Descriptor.Capabilities.Delivery == Delivery.UploadedLoop && e.Adapter is IDeviceClockProvider p && p.ClockOf(e.Descriptor.Id) is { } c)
            {
                map[e.Descriptor.Id] = c;
            }
        }

        return map;
    }

    private ProfileDto? LoadActive()
    {
        try
        {
            return profiles.LoadActive();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LogActiveUnreadable(ex);
            return null;
        }
    }

    private void ApplyStartupScene(ProfileDto? restored)
    {
        if (restored is not null)
        {
            try
            {
                lock (_sceneGate)
                {
                    _loop.SetScene(DtoMapper.FromDto(restored.Scene), allowMissingZones: true);
                    _activeName = restored.Name;
                }

                LogActiveRestored(restored.Name, restored.Scene.Assignments.Count);
                return;
            }
            catch (ArgumentException ex)
            {
                LogActiveUnreadable(ex);
                if (IsPackageScene(restored.Scene))
                {
                    // OPUS-GENERIC-SCENE-PACKAGES: never an invented/default show in place of package content: dark and labelled
                    BlockStartup(restored, ex.Message);
                    return;
                }
            }
        }

        lock (_sceneGate)
        {
            _loop.SetScene(DefaultScene());
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Engine started: {Active}/{Total} controllers active at {Fps} fps")]
    private partial void LogStarted(int active, int total, int fps);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Engine stopped")]
    private partial void LogStopped();

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Restored active profile {Name} ({Count} assignments)")]
    private partial void LogActiveRestored(string name, int count);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "active.json unreadable or invalid; using the default scene")]
    private partial void LogActiveUnreadable(Exception ex);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "active.json could not be written")]
    private partial void LogActiveSaveFailed(Exception ex);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "Re-published {Count} uploaded loop(s) with effect index {Index}")]
    private partial void LogLoopsResynced(int count, uint index);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Information, Message = "Mounting of {Id} set to revision {Revision} ({Description}); re-published {Count} loop(s)")]
    private partial void LogMountingApplied(string id, int revision, string description, int count);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "mounting.json unreadable or invalid; using the configured mountings")]
    private partial void LogMountingUnreadable(Exception ex);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Error, Message = "Restoring services stopped by Takeover failed; records kept in takeover.json")]
    private partial void LogRestoreFailed(Exception ex);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "Hold-black active: restoration deferred; vendor services stay stopped and takeover.json keeps every record")]
    private partial void LogRestoreDeferred();

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Streamed playback calibration (manual) from configuration: rate {Rate}, offset {TrimMs} ms")]
    private partial void LogCalibrationConfigured(double rate, double trimMs);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Error, Message = "Configured streamed playback calibration rejected ({Reason}); the nominal rate 1, offset 0 ms stays in force")]
    private partial void LogCalibrationRejected(string reason);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Streamed playback calibration (manual, runtime-only) set: rate {OldRate} -> {Rate}, offset {OldTrimMs} -> {TrimMs} ms")]
    private partial void LogCalibrationSet(double oldRate, double oldTrimMs, double rate, double trimMs);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Warning, Message = "LianLi:UseDeviceClock (experimental): streamed companions of uploaded loops follow the RF master counter at 1600 ticks/s (vendor bridge model, not a playback ACK); samples older than {MaxAgeSeconds} s or mixed domains fall back to the upload anchor")]
    private partial void LogDeviceClockEnabled(double maxAgeSeconds);
}
