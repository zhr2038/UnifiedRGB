using System.Diagnostics;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Color;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <summary>
/// Renders the active scene once per master-clock tick: every assignment's generator renders its (possibly
/// cross-device) sample set from the same t, the result is sliced into controller zone buffers, corrected by the
/// controller's OutputStage and published to the FrameBus. Scene changes are compiled off the hot path;
/// RenderFrame performs no allocations.
/// UploadedLoop controllers (ARCHITECTURE-D2 §1–§2) are skipped on ticks: on every scene/controller change the
/// <see cref="LoopCompiler"/> renders their loops once and publishes them (unchanged loops are not re-published unless
/// forced). Streamed members of a group that also holds loop zones render at <c>((t − t0 − trim) × rate) mod P</c>, where t0 is
/// the raw host instant the loop's first header went out (<see cref="AnchorLoop"/>) and trim / rate are the manual streamed
/// reference-clock calibration (<see cref="PlaybackCalibration"/>, default 0 / 1, NATIVE-PLAYBACK-CALIBRATION).
/// NATIVE design §7: each assignment has one <see cref="ContentStrategy"/>. <c>Vendor</c> keeps the L-Connect body and gives
/// streamed members a slice of the same plan, played frame by frame by <see cref="Player"/> from the loops' anchor (§7.3,
/// §7.5); per-device routes keep the family bodies and render streamed members live (<c>+program(N)</c>). <c>Program</c>
/// renders the generator for every zone; <c>Frames</c> delivers an authored <see cref="FrameSet"/>. A native bring-up session
/// holds its controller (<see cref="Hold"/>): no scene frame is published to it until <see cref="ReleaseHold"/>.
/// NATIVE-RF-CLOCK (experimental, off unless <see cref="SetDeviceClocks"/> binds clocks): when every uploaded companion of an
/// assignment reports a fresh sample of ONE device clock, its streamed members take their phase from that counter instead of
/// the upload anchor (<see cref="DeviceClockPhase"/>); anything else keeps the legacy timing, with the reason reported.
/// </summary>
public sealed class RenderLoop : IDisposable
{
    private readonly EffectRegistry _registry;
    private readonly FrameBus _bus;
    private readonly LoopCompiler _loops;
    private readonly object _gate = new();
    private readonly Dictionary<(ControllerId, ZoneId), LoopFrames> _lastLoops = [];
    private readonly Dictionary<(ControllerId, ZoneId), LoopAnchor> _anchors = [];
    private readonly Dictionary<(ControllerId, ZoneId), UploadedTiming> _uploaded = []; // NATIVE-RF-CLOCK R2: actual transmitted timing
    private readonly HashSet<ControllerId> _held = [];
    private readonly HashSet<ControllerId> _bringup = [];
    private Dictionary<(ControllerId, ZoneId), PlayedSlice> _lastPlayed = [];
    private ControllerSlot[] _controllers = [];
    private Plan _plan = Plan.Empty;
    private Scene _scene = Scene.Empty;
    private IClock? _clock;
    private long _frames;
    private double _lastT;
    private PlaybackCalibration _calibration = PlaybackCalibration.Default;
    private IReadOnlyDictionary<ControllerId, IDeviceClock>? _deviceClocks;

    public RenderLoop(EffectRegistry registry, FrameBus bus, LoopCompiler? loops = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _loops = loops ?? new LoopCompiler();
    }

    public Scene Scene => _scene;

    /// <summary>
    /// WP-13: per-controller whole-chain fan-order reversal (vendor GetTL/GetH2 direction 1), consulted when a Quick/Sync group
    /// body is sliced. Null = never reversed. Changing a device's answer requires <see cref="RepublishLoops(ControllerId?)"/>.
    /// </summary>
    public Func<ControllerId, bool>? ChainReversed { get; set; }

    /// <summary>
    /// WP-14 (H2-VERTICAL-FILL): per-controller ring calibration of H2 zones. FaceOnFill on a translational group effect
    /// remaps the H2 slice by height (<see cref="RingFillMap"/>); the offset only feeds the Diagnostics geometry tag (the
    /// adapter's wire map applies it). Null = plain ring everywhere.
    /// </summary>
    public Func<ControllerId, RingGeometry>? Ring { get; set; }

    /// <summary>Where <see cref="ContentStrategy.Frames"/> assignments find their frame sets; null = none (they fall back to Program).</summary>
    public IFrameSetSource? FrameSets { get; set; }

    /// <summary>Plays compiled loops on streamed zones (NATIVE design §7.5).</summary>
    public IStreamedLoopPlayer Player { get; set; } = StreamedLoopPlayer.Default;

    public long FramesRendered => Interlocked.Read(ref _frames);

    /// <summary>
    /// The current streamed reference-clock calibration (NATIVE-PLAYBACK-CALIBRATION C2): one coherent snapshot, read under the
    /// render lock, so a frame never samples a rate of one setting with an offset of another.
    /// </summary>
    public PlaybackCalibration PlaybackCalibration
    {
        get
        {
            lock (_gate)
            {
                return _calibration;
            }
        }
    }

    /// <summary>
    /// Replaces the calibration atomically (C2, C4): both values are validated before anything changes, so an invalid input
    /// (NaN, infinity, a rate outside [0.25, 4] or an offset outside [−2000, 2000] ms) throws
    /// <see cref="ArgumentOutOfRangeException"/> and leaves the old snapshot and every anchor intact. Applied from the next
    /// rendered frame: nothing is re-uploaded, no anchor or epoch is reset or moved, and anchors keep their raw T0, so the offset
    /// is applied once at sampling and never accumulates. Returns the snapshot now in force.
    /// </summary>
    public PlaybackCalibration SetPlaybackCalibration(PlaybackCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var why = calibration.Validate();
        if (why is not null)
        {
            throw new ArgumentOutOfRangeException(nameof(calibration), why);
        }

        lock (_gate)
        {
            _calibration = calibration;
            return _calibration;
        }
    }

    /// <summary>A device-clock sample older than this (host seconds) is stale: the assignment falls back to its upload anchor.</summary>
    public const double DeviceClockMaxAgeSeconds = 3;

    /// <summary>
    /// A sample newer than the frame's host time by more than this is invalid (R3). Smaller negative ages are the legitimate
    /// race of a sample published after the frame's time was taken, extrapolated backwards exactly.
    /// </summary>
    public const double DeviceClockMaxFutureSeconds = 0.5;

    /// <summary>
    /// Nominal rate assumed for a report with no sample (the Lian Li master counter: 1600 ticks/s); a bound sample states its own.
    /// </summary>
    public const double DeviceClockTicksPerSecond = 1600;

    /// <summary>
    /// Monotonic host time in <see cref="Stopwatch"/> ticks, read at most once per rendered frame and only while a device clock
    /// is bound; the time base device-clock samples are stamped in. Injectable for deterministic tests.
    /// </summary>
    public Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>Whether <see cref="SetDeviceClocks"/> bound a clock map (the experimental LianLi:UseDeviceClock path).</summary>
    public bool DeviceClockEnabled
    {
        get
        {
            lock (_gate)
            {
                return _deviceClocks is not null;
            }
        }
    }

    /// <summary>
    /// NATIVE-RF-CLOCK: binds (or with null unbinds) the device clocks of UploadedLoop controllers. The map is consulted off
    /// the hot path, now for the current plan and at every later compile; a controller without an entry has no device clock.
    /// Handles are expected to follow their controller's session (a closed session reports no sample), so nothing here
    /// caches a sample. Null restores the legacy upload-anchor timing everywhere.
    /// </summary>
    public void SetDeviceClocks(IReadOnlyDictionary<ControllerId, IDeviceClock>? clocks)
    {
        lock (_gate)
        {
            _deviceClocks = clocks;
            BindClocks(_plan);
        }
    }

    /// <summary>
    /// Which reference clock each assignment's streamed members used on the last rendered frame, with the device clock's
    /// current sample where one is bound (Diagnostics). Extrapolated and derived values are host model values, not readings
    /// of receiver playback.
    /// </summary>
    public IReadOnlyList<StreamedClockReport> ClockReport()
    {
        lock (_gate)
        {
            var now = Timestamp();
            var list = new List<StreamedClockReport>();
            foreach (var it in _plan.Items)
            {
                var cb = it.Clock;
                var sample = cb.Clocks.Length > 0 ? cb.Clocks[0]?.Latest : null;
                var device = cb.Mode == StreamedClockMode.DeviceClock;
                var tps = sample?.TicksPerSecond ?? DeviceClockTicksPerSecond;
                list.Add(new StreamedClockReport(cb.Label, cb.Mode, cb.Mode == StreamedClockMode.UploadAnchor ? Describe(cb) : null)
                {
                    Domain = sample?.Domain.ToString(),
                    RawTicks = sample?.Ticks,
                    SampleAgeSeconds = sample?.AgeSeconds(now),
                    QueryLatencyMs = sample is null ? null : sample.QueryLatencySeconds * 1000.0,
                    TicksPerSecond = tps,
                    ExtrapolatedTicks = sample?.TicksAt(now),
                    ReferenceFrames = cb.RefFrames,
                    ReferenceInterval = cb.RefInterval,
                    NominalPeriodMs = cb.RefPeriodTicks,
                    DerivedPeriodMs = cb.RefFrames > 0 ? cb.RefPeriodTicks * 1000.0 / tps : null,
                    WirePeriodsDiffer = cb.PeriodsDiffer,
                    ReferenceFrame = device ? (int)Math.Floor(cb.Phase * cb.RefFrames) : null,
                    UploadedFrames = cb.UploadedFrames,
                    UploadedInterval = cb.UploadedInterval,
                });
            }

            return list;
        }
    }

    /// <summary>
    /// Streamed zones of the current plan that play precomputed loop content (a Vendor slice or a frame set, NATIVE design
    /// §7.5) rather than rendering live; Diagnostics and tests.
    /// </summary>
    public int PlayedSliceCount => Volatile.Read(ref _plan).Items.Sum(i => i.Played.Length);

    /// <summary>True when <see cref="PlayedSliceCount"/> &gt; 0.</summary>
    public bool HasPlayedSlices => PlayedSliceCount > 0;

    public IReadOnlyList<ControllerDescriptor> Controllers => _controllers.Select(c => c.Descriptor).ToArray();

    /// <summary>
    /// Diagnostics of the CURRENT compilation (the last scene/controller change): UploadedLoop zones published or kept, plus
    /// the variants and truncations. A compilation with variants but no uploaded-loop zone (e.g. every RF controller absent:
    /// a native-only live fallback) gets a zero-upload report (Published 0, Unchanged 0, EffectIndex 0) that transmitted
    /// nothing. A compilation with nothing to report is null (also before the first), never the previous compilation's report.
    /// </summary>
    public LoopCompileReport? LastLoopCompile { get; private set; }

    /// <summary>
    /// OPUS-GENERIC-SCENE-PACKAGES: why strict content of the current compilation cannot play (claimed frame set missing,
    /// corrupt, mismatched or not representable); empty when everything plays. Such content is dark, never a fallback show.
    /// </summary>
    public IReadOnlyList<string> BlockedContent => Volatile.Read(ref _plan).Blocked ?? [];

    /// <summary>
    /// Compiles <paramref name="scene"/> as an activation with missing zones allowed would, WITHOUT publishing, anchoring or
    /// changing anything, and returns the strict loops it would upload plus every blocked reason (whole-candidate validation of
    /// package content before commit). Unknown effects throw as in <see cref="SetScene(Scene)"/>.
    /// </summary>
    public StrictPreflight PreflightStrict(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            ValidatePaths(scene);
            var plan = Compile(scene, skipMissing: true);
            var loops = plan.Items.SelectMany(it => it.Loops.Where(l => l.Strict).Select(l =>
                new StrictLoop(l.Key.Item1, l.Key.Item2, new LoopFrames(l.Timing.Frames, l.Rgb.Length / 3 / l.Timing.Frames, l.Timing.IntervalMs, 0, l.Rgb)
                {
                    Geometry = l.Geometry,
                    Strict = true,
                }))).ToArray();
            return new StrictPreflight(loops, plan.Blocked ?? []);
        }
    }

    /// <summary>
    /// OPUS-GENERIC-SCENE-PACKAGES: the shared timeline of the current plan's strict frame set <paramref name="timelineId"/> at the
    /// clock's current time, sampled exactly as its streamed members are (device-clock phase, else the labelled upload anchor or
    /// local epoch); null when no assignment of the current plan carries it. Reads only cached clock samples: no I/O.
    /// </summary>
    public TimelinePosition? Timeline(string timelineId)
    {
        lock (_gate)
        {
            return TimelineAt(timelineId, _clock?.Now ?? _lastT);
        }
    }

    /// <summary><see cref="Timeline(string)"/> at master time <paramref name="t"/> (deterministic tests).</summary>
    public TimelinePosition? TimelineAt(string timelineId, double t)
    {
        ArgumentException.ThrowIfNullOrEmpty(timelineId);
        lock (_gate)
        {
            foreach (var it in _plan.Items)
            {
                if (!string.Equals(it.TimelineId, timelineId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (it.BlockedReason is { } why)
                {
                    return new TimelinePosition(0, 0, TimelineSources.Blocked, why);
                }

                var periodMs = it.Period * 1000.0;
                if (_deviceClocks is not null && it.Clock.Shares)
                {
                    var now = 0L;
                    var haveNow = false;
                    var phase = DeviceClockPhase(it.Clock, it.Physical ? 1.0 : _calibration.StreamedPlaybackRate, it.Physical ? 0.0 : _calibration.PhaseTrimMs,
                        ref now, ref haveNow);
                    if (phase >= 0)
                    {
                        return new TimelinePosition(phase * periodMs, periodMs, TimelineSources.DeviceClock);
                    }
                }

                var anchor = it.Anchor;
                if (anchor is null || it.Period <= 0)
                {
                    return new TimelinePosition(0, periodMs, TimelineSources.LocalEpoch);
                }

                var elapsed = anchor.Local ? t - anchor.T0 : (t - anchor.T0 - (it.Physical ? 0 : _calibration.PhaseTrimMs / 1000.0)) * (it.Physical ? 1.0 : _calibration.StreamedPlaybackRate);
                var position = Wrap(elapsed, it.Period) * 1000.0;
                var source = anchor.Local
                    ? TimelineSources.LocalEpoch
                    : $"{TimelineSources.UploadAnchor}: {(it.Clock.Shares ? Describe(it.Clock) : "no uploaded loop clock")}";
                return new TimelinePosition(position >= periodMs ? 0 : position, periodMs, source);
            }

            return null;
        }
    }

    /// <summary>Replaces the controller set (discovery/hotplug) and recompiles the current scene.</summary>
    public void SetControllers(IEnumerable<ControllerDescriptor> controllers, Func<ControllerId, OutputStage>? stageFor = null)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        lock (_gate)
        {
            var list = controllers.ToArray();
            foreach (var old in _controllers)
            {
                if (!list.Any(c => c.Id == old.Descriptor.Id))
                {
                    _bus.Unregister(old.Descriptor.Id);
                }
            }

            foreach (var key in _lastLoops.Keys.Where(k => !list.Any(c => c.Id == k.Item1)).ToArray())
            {
                _lastLoops.Remove(key); // a controller that comes back gets its loop again
                _uploaded.Remove(key);
            }

            _controllers = list
                .Select(c => new ControllerSlot(c, _bus.Register(c), stageFor?.Invoke(c.Id) ?? OutputStage.Identity,
                    c.Capabilities.Delivery == Delivery.UploadedLoop))
                .ToArray();
            Activate(_scene, skipMissing: true, force: false);
        }
    }

    /// <summary>Validates and activates a scene. Throws for unknown effects or zones.</summary>
    public void SetScene(Scene scene) => SetScene(scene, allowMissingZones: false);

    /// <summary>
    /// With <paramref name="allowMissingZones"/> (profiles restored while a controller is offline or Blocked) targets that do
    /// not exist yet are kept in the scene but not rendered; they light up when SetControllers brings the controller in.
    /// Unknown effects always throw.
    /// </summary>
    public void SetScene(Scene scene, bool allowMissingZones) => SetScene(scene, allowMissingZones, null);

    /// <summary>Prepare completely, then run a durable-state commit before any live plan/anchor/mailbox publication.</summary>
    public void SetScene(Scene scene, bool allowMissingZones, Action? beforePublish)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            ValidatePaths(scene);
            var compiled = Compile(scene, allowMissingZones);
            beforePublish?.Invoke();
            Activate(compiled, force: false, forceFor: null);
            _scene = scene;
        }
    }

    /// <summary>
    /// Re-publishes every UploadedLoop zone's loop with a fresh EffectIndex even when unchanged: hotplug reconnect,
    /// LianLi:ReanchorMinutes and the GUI's "Re-sync Lian Li" (ARCHITECTURE-D2 §2, §7).
    /// </summary>
    public LoopCompileReport? RepublishLoops()
    {
        lock (_gate)
        {
            Activate(_scene, skipMissing: true, force: true);
            return LastLoopCompile;
        }
    }

    /// <summary>
    /// Recompiles and re-publishes, with one fresh shared EffectIndex, every assignment whose loops cover
    /// <paramref name="controller"/> (all its companion loops together, even when their canonical RGB is unchanged), plus the
    /// controller's uncovered black zones; other assignments keep their loops. Used when a device-side mapping changes
    /// (WP-11 mounting, DEBUG-SESSION-1 §3.3).
    /// </summary>
    public LoopCompileReport? RepublishLoops(ControllerId controller)
    {
        lock (_gate)
        {
            Activate(_scene, skipMissing: true, force: false, forceFor: controller);
            return LastLoopCompile;
        }
    }

    /// <summary>
    /// WP-14 ring calibration (H2-VERTICAL-FILL §3): holds <paramref name="controller"/> (scene compiles stop publishing to it)
    /// and publishes a one-frame loop through the normal bus/upload path with a fresh EffectIndex. Repeatable (each step press).
    /// </summary>
    public void PublishCalibration(ControllerId controller, ReadOnlyMemory<byte> frame)
    {
        lock (_gate)
        {
            var slot = Array.FindIndex(_controllers, c => c.Descriptor.Id == controller);
            if (slot < 0 || !_controllers[slot].IsLoop || _controllers[slot].Descriptor.Zones.Count == 0)
            {
                throw new ArgumentException($"{controller} is not an uploaded-loop controller.", nameof(controller));
            }

            var d = _controllers[slot].Descriptor;
            var z = d.Zones[0];
            if (frame.Length != z.LedCount * 3)
            {
                throw new ArgumentException($"A calibration frame for {controller} is {z.LedCount} LEDs.", nameof(frame));
            }

            _held.Add(controller);
            _lastLoops.Remove((controller, z.Id)); // the scene loop is re-sent on release
            _uploaded.Remove((controller, z.Id));
            ReverifyTiming(); // the device now plays the marker, not the scene loop: no device-clock lock against it
            var timing = LoopCompiler.TimingFor(0, d.Capabilities);
            _bus.PublishLoop(controller, z.Id, new LoopFrames(1, z.LedCount, timing.IntervalMs, _loops.NextEffectIndex(), frame.ToArray()) { Geometry = "calibration" });
        }
    }

    /// <summary>
    /// Releases a calibration hold; with <paramref name="republish"/> the controller's scene loops go out again with a fresh
    /// EffectIndex (Cancel). Apply releases without republishing because the mounting change republishes itself.
    /// </summary>
    public LoopCompileReport? EndCalibration(ControllerId controller, bool republish)
    {
        lock (_gate)
        {
            if (!_held.Remove(controller) || !republish)
            {
                return LastLoopCompile;
            }

            Activate(_scene, skipMissing: true, force: false, forceFor: controller);
            return LastLoopCompile;
        }
    }

    /// <summary>Whether a ring calibration currently holds <paramref name="controller"/>.</summary>
    public bool IsHeld(ControllerId controller)
    {
        lock (_gate)
        {
            return _held.Contains(controller);
        }
    }

    /// <summary>
    /// NATIVE design §6: a bring-up session holds <paramref name="controller"/>. No scene frame or loop is published to it
    /// (streamed frames are rendered but not handed to the bus) until <see cref="ReleaseHold"/>. Idempotent.
    /// </summary>
    public void Hold(ControllerId controller)
    {
        lock (_gate)
        {
            _bringup.Add(controller);
        }
    }

    /// <summary>
    /// Ends a bring-up hold. An uploaded-loop controller gets its scene loops again with a fresh EffectIndex; a streamed one
    /// simply receives the next tick's frame. Idempotent: a controller that is not held is left alone.
    /// </summary>
    public void ReleaseHold(ControllerId controller)
    {
        lock (_gate)
        {
            if (!_bringup.Remove(controller))
            {
                return;
            }

            if (_controllers.Any(c => c.Descriptor.Id == controller && c.IsLoop))
            {
                Activate(_scene, skipMissing: true, force: false, forceFor: controller);
            }
        }
    }

    /// <summary>Whether a native bring-up session currently holds <paramref name="controller"/>.</summary>
    public bool IsBringupHeld(ControllerId controller)
    {
        lock (_gate)
        {
            return _bringup.Contains(controller);
        }
    }

    /// <summary>
    /// Anchors the streamed members of every group whose loops carry <paramref name="effectIndex"/>: t0 is the raw MasterClock
    /// time the first header went out, with no calibration added (the <see cref="PlaybackCalibration"/> offset is applied once,
    /// when a frame is sampled). The earliest report wins. Local frame-set epochs are never moved.
    /// </summary>
    public void AnchorLoop(uint effectIndex, double t0)
    {
        lock (_gate)
        {
            foreach (var a in _anchors.Values)
            {
                if (!a.Local && a.EffectIndex == effectIndex && (!a.Anchored || t0 < a.T0))
                {
                    a.T0 = t0;
                    a.Anchored = true;
                }
            }
        }
    }

    /// <summary>
    /// NATIVE-RF-CLOCK R2: the timing a completed upload actually transmitted (after any adapter decimation): frames and header
    /// interval of the loop sent to <paramref name="controller"/>/<paramref name="zone"/> under <paramref name="effectIndex"/>.
    /// Recorded only while that index is the zone's current published loop, so a late completion of a superseded index never
    /// replaces the verdict of a newer one; an identical-content apply keeps both the index and the verdict, a resync or a
    /// reconnect publishes a new index that needs its own report. A device-clock phase is used only when every uploaded
    /// companion's transmitted wire period equals its compiled one (else the assignment falls back with the reason).
    /// </summary>
    public void ReportLoopTiming(ControllerId controller, ZoneId zone, uint effectIndex, int frames, double intervalMs, bool staticFallback)
    {
        lock (_gate)
        {
            if (!_lastLoops.TryGetValue((controller, zone), out var current) || current.EffectIndex != effectIndex)
            {
                return; // superseded, or no longer this scene's loop
            }

            _uploaded[(controller, zone)] = new UploadedTiming(effectIndex, frames, intervalMs, staticFallback);
            ReverifyTiming();
        }
    }

    public void Attach(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Detach();
        _clock = clock;
        clock.Tick += OnTick;
    }

    public void Detach()
    {
        if (_clock is not null)
        {
            _clock.Tick -= OnTick;
            _clock = null;
        }
    }

    public void Dispose() => Detach();

    /// <summary>Renders and publishes one frame for every Streamed controller. Allocation-free.</summary>
    public void RenderFrame(double t, double dt)
    {
        lock (_gate)
        {
            _lastT = t;
            var frameIndex = Interlocked.Increment(ref _frames);
            var controllers = _controllers;
            for (var c = 0; c < controllers.Length; c++)
            {
                if (controllers[c].IsLoop)
                {
                    continue;
                }

                var back = controllers[c].Mailbox.Back;
                back.Index = frameIndex;
                back.T = t;
                var zones = back.Zones;
                for (var z = 0; z < zones.Length; z++)
                {
                    Array.Clear(zones[z]);
                }
            }

            var rate = _calibration.StreamedPlaybackRate;
            var trim = _calibration.PhaseTrimMs / 1000.0;
            var items = _plan.Items;
            var clocks = _deviceClocks is not null;
            var now = 0L;
            var haveNow = false;
            for (var i = 0; i < items.Length; i++)
            {
                var it = items[i];
                var anchor = it.Anchor;

                // OPUS-GENERIC-SCENE-PACKAGES: a physical-ms frame set is authored physical time; the manual streamed calibration
                // (a hand-tuned legacy correction) never applies to it
                var itRate = it.Physical ? 1.0 : rate;
                var itTrim = it.Physical ? 0.0 : trim;

                // NATIVE-RF-CLOCK: the normalised phase [0, 1) of the reference uploaded loop from the device counter, or -1 (legacy)
                var phase = clocks && it.Clock.Shares ? DeviceClockPhase(it.Clock, itRate, it.Physical ? 0.0 : _calibration.PhaseTrimMs, ref now, ref haveNow) : -1;

                // NATIVE-PLAYBACK-CALIBRATION C3: an uploaded companion's elapsed time from the raw first-header T0, calibrated
                // once; a local frame-set epoch keeps t − T0. At 1 / 0 the result equals t − T0 exactly (x − 0 = x, x × 1 = x).
                var elapsed = anchor is null ? t : anchor.Local ? t - anchor.T0 : (t - anchor.T0 - itTrim) * itRate;
                if (it.Slices.Length > 0)
                {
                    // live generic slices only when they share the group's epoch and period; per-device routes (LivePeriod 0) stay at t.
                    // With a device clock the phase maps onto the generator's compiled timeline [0, LivePeriod) (frame k of the loop
                    // was rendered at k × LivePeriod / frames), and Dt follows d(tt)/dt = rate × ticks/s × LivePeriod / reference period.
                    var shared = it.LivePeriod > 0 && phase >= 0;
                    var tt = shared ? phase * it.LivePeriod : anchor is not null && it.LivePeriod > 0 ? Wrap(elapsed, it.LivePeriod) : t;
                    var logicalDt = shared ? dt * rate * it.Clock.TicksPerSecond * it.LivePeriod / it.Clock.RefPeriodTicks
                        : anchor is { Local: false } && it.LivePeriod > 0 ? dt * rate : dt;
                    var ctx = new RenderContext(tt, logicalDt, it.Colors, it.Speed, it.Brightness, it.Direction, it.Samples, it.Seed);
                    it.Generator.Render(in ctx, it.Scratch);
                    for (var s = 0; s < it.Slices.Length; s++)
                    {
                        var sl = it.Slices[s];
                        var slot = controllers[sl.ControllerSlot];
                        var dst = slot.Mailbox.Back.Zones[sl.ZoneIndex].AsSpan();
                        it.Scratch.AsSpan(sl.SourceOffset, dst.Length).CopyTo(dst);
                        slot.Stage.Apply(dst, it.Brightness);
                    }
                }

                if (it.Played.Length > 0)
                {
                    // §7.5: frame k of the compiled slice from the loops' own anchor and period, held (no blending); with a device clock
                    // the same player and hold, at the reference loop's phase of the counter mapped onto the slice's own frames ×
                    // interval (so the slice shows frame floor(phase × frames), whatever the assignment's nominal period)
                    var pt = anchor is null ? t : it.Period > 0 ? Wrap(elapsed, it.Period) : elapsed;
                    for (var s = 0; s < it.Played.Length; s++)
                    {
                        var pl = it.Played[s];
                        var slot = controllers[pl.ControllerSlot];
                        var dst = slot.Mailbox.Back.Zones[pl.ZoneIndex].AsSpan();
                        var k = Player.FrameAt(phase >= 0 ? phase * pl.Frames * pl.IntervalMs / 1000.0 : pt, pl.IntervalMs, pl.Frames);
                        if ((uint)k >= (uint)pl.Frames)
                        {
                            k = 0;
                        }

                        pl.Pixels.AsSpan(k * pl.Leds, dst.Length).CopyTo(dst);
                        slot.Stage.Apply(dst, pl.Brightness);
                    }
                }
            }

            for (var c = 0; c < controllers.Length; c++)
            {
                if (!controllers[c].IsLoop && !_bringup.Contains(controllers[c].Descriptor.Id))
                {
                    _bus.TryPublishCurrent(controllers[c].Mailbox, controllers[c].Mailbox.Back);
                }
            }
        }
    }

    private void OnTick(double t, double dt) => RenderFrame(t, dt);

    /// <summary>
    /// NATIVE-RF-CLOCK, in the recovered vendor bridge model: an RF device shows frame floor(C / I) mod N of its loop, C its
    /// master counter (ticks of 1 / <see cref="DeviceClockSample.TicksPerSecond"/> s) and I the header interval in ticks.
    /// Returns the normalised phase of the reference loop (the assignment's first uploaded loop, wire-rounded interval):
    /// <c>wrap((C − trim × ticks/s) × rate, N × I) / (N × I)</c>, C extrapolated from the newest sample to now. The trim is
    /// a host-time delay (positive delays the streamed members); the rate is a residual factor, 1 by default. Returns -1 and
    /// records why when the sample is missing or stale or the companions do not share ONE clock domain (legacy anchor then).
    /// Allocation-free; reads each handle once.
    /// </summary>
    private double DeviceClockPhase(ClockBinding cb, double rate, double trimMs, ref long now, ref bool haveNow)
    {
        if (cb.RefPeriodTicks <= 0 || cb.RefFrames <= 1)
        {
            return Fallback(cb, ClockFallback.StaticReference);
        }

        if (cb.Timing != ClockFallback.None)
        {
            return Fallback(cb, cb.Timing); // the transmitted timing is not yet known, or differs from the compiled reference
        }

        if (cb.Clocks.Length == 0 || cb.Clocks[0] is not { } first)
        {
            return Fallback(cb, ClockFallback.NoClock);
        }

        var s0 = first.Latest;
        if (s0 is null)
        {
            return Fallback(cb, ClockFallback.NoSample);
        }

        for (var j = 1; j < cb.Clocks.Length; j++)
        {
            if (cb.Clocks[j] is not { } other)
            {
                return Fallback(cb, ClockFallback.NoClock);
            }

            var s = other.Latest;
            if (s is null)
            {
                return Fallback(cb, ClockFallback.NoSample);
            }

            if (!s.Domain.Equals(s0.Domain))
            {
                return Fallback(cb, ClockFallback.MixedDomains);
            }
        }

        if (!haveNow)
        {
            now = Timestamp();
            haveNow = true;
        }

        var age = s0.AgeSeconds(now);
        cb.Age = age;
        if (age > DeviceClockMaxAgeSeconds)
        {
            return Fallback(cb, ClockFallback.Stale);
        }

        if (age < -DeviceClockMaxFutureSeconds)
        {
            // R3: a sample published after this frame's host time was taken is legitimately a little newer (backward
            // extrapolation is exact for it); one far in the future is invalid metadata and is not extrapolated
            return Fallback(cb, ClockFallback.Future);
        }

        var counter = s0.TicksAt(now);
        var ticks = (counter - (trimMs / 1000.0 * s0.TicksPerSecond)) * rate;
        var phase = Wrap(ticks, cb.RefPeriodTicks) / cb.RefPeriodTicks;
        if (phase >= 1)
        {
            phase = 0; // a wrap remainder that rounds up to the period
        }

        cb.Mode = StreamedClockMode.DeviceClock;
        cb.Reason = ClockFallback.None;
        cb.TicksPerSecond = s0.TicksPerSecond;
        cb.Phase = phase;
        return phase;
    }

    private static double Fallback(ClockBinding cb, ClockFallback why)
    {
        cb.Mode = StreamedClockMode.UploadAnchor;
        cb.Reason = why;
        return -1;
    }

    private static string Describe(ClockBinding cb) => cb.Reason switch
    {
        ClockFallback.Disabled => "device clock disabled (LianLi:UseDeviceClock=false)",
        ClockFallback.NotRendered => "no frame rendered since the last compile",
        ClockFallback.StaticReference => "the reference loop is a single frame",
        ClockFallback.NoClock => $"no device clock for {cb.Missing?.Value ?? "an uploaded companion"}",
        ClockFallback.NoSample => "no validated device-clock sample (session closed or never read)",
        ClockFallback.Stale => $"device-clock sample is stale ({cb.Age:0.###} s > {DeviceClockMaxAgeSeconds:0} s)",
        ClockFallback.MixedDomains => "the uploaded companions report different clock domains",
        ClockFallback.Future => $"device-clock sample is {-cb.Age:0.###} s in the future (> {DeviceClockMaxFutureSeconds:0.###} s): invalid timestamp",
        ClockFallback.TimingPending => $"transmitted timing of {cb.TimingKey ?? "an uploaded companion"} not reported for its current effect index",
        ClockFallback.TimingChanged => $"transmitted timing of {cb.TimingKey} ({cb.TimingDetail}) differs from the compiled reference (unsupported: decimated or static upload)",
        _ => "upload anchor",
    };

    /// <summary>Resolves every shared item's per-loop clock handles from the bound map (caller holds the lock; off the hot path).</summary>
    private void BindClocks(Plan plan)
    {
        foreach (var it in plan.Items)
        {
            var cb = it.Clock;
            if (!cb.Shares)
            {
                continue;
            }

            cb.Missing = null;
            cb.Mode = StreamedClockMode.UploadAnchor;
            if (_deviceClocks is null)
            {
                cb.Clocks = [];
                cb.Reason = ClockFallback.Disabled;
                continue;
            }

            var handles = new IDeviceClock?[it.Loops.Length];
            for (var l = 0; l < handles.Length; l++)
            {
                var id = it.Loops[l].Key.Item1;
                handles[l] = _deviceClocks.TryGetValue(id, out var h) ? h : null;
                if (handles[l] is null && cb.Missing is null)
                {
                    cb.Missing = id;
                }
            }

            cb.Clocks = handles;
            cb.Reason = ClockFallback.NotRendered;
            VerifyTiming(it);
        }
    }

    /// <summary>
    /// R2 (caller holds the lock): whether every uploaded loop of a shared item has reported its transmitted timing for its
    /// current index, and whether that wire period equals the compiled one; records the reference loop's transmitted timing for
    /// the report. Minimal policy: any difference (adapter decimation, static fallback) is an explicit unsupported-case
    /// fallback, never a lock against a period the device does not play.
    /// </summary>
    private void ReverifyTiming()
    {
        foreach (var it in _plan.Items)
        {
            if (it.Clock.Shares)
            {
                VerifyTiming(it);
            }
        }
    }

    private void VerifyTiming(PlanItem it)
    {
        var cb = it.Clock;
        cb.Timing = ClockFallback.None;
        cb.TimingKey = null;
        cb.TimingDetail = null;
        cb.UploadedFrames = null;
        cb.UploadedInterval = null;
        for (var l = 0; l < it.Loops.Length; l++)
        {
            var loop = it.Loops[l];
            var key = loop.Key;
            if (!_lastLoops.TryGetValue(key, out var current) || !_uploaded.TryGetValue(key, out var up) || up.EffectIndex != current.EffectIndex)
            {
                if (cb.Timing == ClockFallback.None)
                {
                    (cb.Timing, cb.TimingKey) = (ClockFallback.TimingPending, key.Item1.Value);
                }

                continue;
            }

            var sentInterval = ClockBinding.WireInterval(up.IntervalMs);
            var compiledInterval = ClockBinding.WireInterval(loop.Timing.IntervalMs);
            if (l == 0)
            {
                (cb.UploadedFrames, cb.UploadedInterval) = (up.Frames, sentInterval);
            }

            if (up.StaticFallback || up.Frames != loop.Timing.Frames || sentInterval != compiledInterval)
            {
                (cb.Timing, cb.TimingKey) = (ClockFallback.TimingChanged, key.Item1.Value);
                cb.TimingDetail = $"sent {up.Frames} x {sentInterval:0.##}{(up.StaticFallback ? " static" : string.Empty)}, compiled {loop.Timing.Frames} x {compiledInterval:0.##}";
                return; // a definite mismatch outranks a pending report
            }
        }
    }

    private static double Wrap(double x, double period)
    {
        var r = x % period;
        return r < 0 ? r + period : r;
    }

    /// <summary>Compiles the plan, then publishes the loops it produced (caller holds the lock).</summary>
    private void Activate(Scene scene, bool skipMissing, bool force, ControllerId? forceFor = null)
    {
        var compiled = Compile(scene, skipMissing);
        Activate(compiled, force, forceFor);
    }

    private void Activate(Plan compiled, bool force, ControllerId? forceFor)
    {
        var now = _clock?.Now ?? _lastT;
        uint? index = null;
        var published = 0;
        var unchanged = 0;
        var covered = new HashSet<(ControllerId, ZoneId)>();
        var localAnchors = new HashSet<(ControllerId, ZoneId)>();
        var played = new Dictionary<(ControllerId, ZoneId), PlayedSlice>();
        var publish = new List<(ControllerId Controller, ZoneId Zone, byte[] Rgb, LoopTiming Timing, string? Geometry, bool Strict)>();
        var items = new PlanItem[compiled.Items.Length];
        for (var i = 0; i < compiled.Items.Length; i++)
        {
            var it = compiled.Items[i];
            items[i] = it;
            foreach (var pl in it.Played)
            {
                played[pl.Key] = pl;
            }

            if (it.Loops.Length == 0)
            {
                if (it.Played.Length > 0)
                {
                    // frame-played streamed zones without an uploaded companion: a local epoch, kept while the content is unchanged
                    var pkey = it.Played[0].Key;
                    localAnchors.Add(pkey);
                    var samePlay = it.Played.All(pl => _lastPlayed.TryGetValue(pl.Key, out var last) && SamePlayed(last, pl));
                    if (samePlay && _anchors.TryGetValue(pkey, out var keptPlay) && keptPlay.Local)
                    {
                        items[i] = it with { Anchor = keptPlay };
                    }
                    else
                    {
                        var local = new LoopAnchor { EffectIndex = 0, T0 = now, Anchored = true, Local = true };
                        _anchors[pkey] = local;
                        items[i] = it with { Anchor = local };
                    }
                }

                continue;
            }

            foreach (var l in it.Loops)
            {
                covered.Add(l.Key);
            }

            var forced = force || (forceFor is { } f && it.Loops.Any(l => l.Key.Item1 == f));
            var same = !forced && it.Loops.All(l => _lastLoops.TryGetValue(l.Key, out var last) && SameContent(last, l.Rgb, l.Timing));
            var key = it.Loops[0].Key;
            if (same && _anchors.TryGetValue(key, out var kept))
            {
                unchanged += it.Loops.Length;
                items[i] = it with { Anchor = kept };
                continue;
            }

            index ??= _loops.NextEffectIndex();
            var anchor = new LoopAnchor { EffectIndex = index.Value, T0 = now };
            _anchors[key] = anchor;
            items[i] = it with { Anchor = anchor };
            publish.AddRange(it.Loops.Select(l => (l.Key.Item1, l.Key.Item2, l.Rgb, l.Timing, l.Geometry, l.Strict)));
        }

        // UploadedLoop zones no assignment covers are dark, as streamed zones are: a one-frame black loop
        foreach (var slot in _controllers.Where(c => c.IsLoop))
        {
            foreach (var z in slot.Descriptor.Zones)
            {
                if (covered.Contains((slot.Descriptor.Id, z.Id)))
                {
                    continue;
                }

                var timing = LoopCompiler.TimingFor(0, slot.Descriptor.Capabilities);
                var black = new byte[z.LedCount * 3];
                if (!force && slot.Descriptor.Id != forceFor && _lastLoops.TryGetValue((slot.Descriptor.Id, z.Id), out var last) && SameContent(last, black, timing))
                {
                    unchanged++;
                    continue;
                }

                index ??= _loops.NextEffectIndex();
                publish.Add((slot.Descriptor.Id, z.Id, black, timing, IsH2(z) ? "ring" : null, false));
            }
        }

        foreach (var stale in _anchors.Keys.Where(k => !covered.Contains(k) && !localAnchors.Contains(k)).ToArray())
        {
            _anchors.Remove(stale);
        }

        _lastPlayed = played;
        _plan = new Plan(items, compiled.Truncated, compiled.Variants, compiled.Blocked);
        foreach (var (controller, zone, rgb, timing, geometry, strict) in publish)
        {
            if (_held.Contains(controller) || _bringup.Contains(controller))
            {
                continue; // WP-14 ring calibration / NATIVE §6 bring-up owns this controller until its release re-publishes it
            }

            var loop = new LoopFrames(timing.Frames, rgb.Length / 3 / timing.Frames, timing.IntervalMs, index!.Value, rgb) { Geometry = geometry, Strict = strict };
            var slot = Array.Find(_controllers, c => c.Descriptor.Id == controller);
            if (slot is not null && _bus.TryPublishCurrentLoop(slot.Mailbox, zone, loop))
            {
                _lastLoops[(controller, zone)] = loop;
                published++;
            }
            else
            {
                _lastLoops.Remove((controller, zone));
                _uploaded.Remove((controller, zone));
            }
        }

        BindClocks(_plan); // after publishing: the transmitted-timing verdict refers to the indices now on the bus

        // STACK-RETURN review2: the report always describes this compilation, so a vendor success never outlives its RF chains
        LastLoopCompile = publish.Count > 0 || unchanged > 0 || compiled.Variants.Count > 0
            ? new LoopCompileReport(index ?? 0, published, unchanged, compiled.Truncated, compiled.Variants)
            : null;
    }

    /// <summary>
    /// SPATIAL-PATH: rejects (before anything is published) a malformed path or one combined with an unsupported route
    /// (<see cref="Assignment.ValidatePath"/>, topology only, so it holds with every controller absent, including the Vendor
    /// Stacking multiple-of-12 rule; STACK-RETURN: <see cref="Assignment.ValidateLoopReturn"/> likewise), and a Vendor
    /// shared-Stacking path whose present TL zone's stage is not 12 per fan.
    /// Absent targets are checked when they appear: a recompile then falls back to Program with the reason, never throws.
    /// </summary>
    private void ValidatePaths(Scene scene)
    {
        foreach (var a in scene.Assignments)
        {
            ArgumentNullException.ThrowIfNull(a, nameof(scene));
            a.Validate();
            if (a.Path is null || a.Strategy != ContentStrategy.Vendor || !LConnectLoops.IsStackingRoute(a.EffectId))
            {
                continue;
            }

            var spans = a.Path.Resolve();
            foreach (var (target, span) in spans)
            {
                var c = _controllers.FirstOrDefault(c => c.Descriptor.Id == target.Controller)?.Descriptor;
                var zi = c is null ? -1 : FindZone(c, target.Zone);
                if (zi >= 0 && c!.Zones[zi].Family == LConnectLoops.Tl && span.Length != c.Zones[zi].Units * 12)
                {
                    throw new ArgumentException(
                        $"Spatial Stacking stage of TL zone {target} is {span.Length} source dots, not 12 per fan ({c.Zones[zi].Units * 12}).", nameof(scene));
                }
            }
        }
    }

    private static bool SameContent(LoopFrames last, byte[] rgb, LoopTiming timing) =>
        last.Frames == timing.Frames && last.IntervalMs.Equals(timing.IntervalMs) && last.Rgb.Span.SequenceEqual(rgb);

    private static bool SamePlayed(PlayedSlice a, PlayedSlice b) =>
        a.Frames == b.Frames && a.Leds == b.Leds && a.IntervalMs.Equals(b.IntervalMs) && a.Brightness.Equals(b.Brightness)
        && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    private Plan Compile(Scene scene, bool skipMissing)
    {
        var items = new List<PlanItem>();
        var truncated = new List<string>();
        var variants = new List<string>();
        var blocked = new List<string>();
        foreach (var a in scene.Assignments)
        {
            if (!_registry.TryGet(a.EffectId, out var gen))
            {
                throw new ArgumentException($"Unknown effect '{a.EffectId}'.", nameof(scene));
            }

            var layouts = new List<ZoneLayout>();
            var placements = new List<ZonePlacement?>();
            var members = new List<(Slice Slice, bool IsLoop)>();
            var layoutOf = new Dictionary<(int, int), int>();
            var offset = 0;
            var missing = false;
            var spanOf = a.Path?.Resolve(); // SPATIAL-PATH: ranges from the path, never from which members are present
            var spans = new List<SpatialSpan>();
            // SPATIAL-PATH R1: the Program samples of an explicit path read a TL chain in its calibrated physical order, as the
            // vendor SliceGroup does (ReverseChain reverses the whole strip): local U is reversed by ChainReversed XOR the
            // binding's artistic Reverse. U only (X/Y/theta untouched), TL only (H2 and streamed Points keep the binding).
            var programSpans = new List<SpatialSpan>();
            foreach (var target in a.Targets)
            {
                var slot = Array.FindIndex(_controllers, c => c.Descriptor.Id == target.Controller);
                var zoneIndex = slot < 0 ? -1 : FindZone(_controllers[slot].Descriptor, target.Zone);
                if (zoneIndex < 0)
                {
                    if (skipMissing)
                    {
                        missing = true;
                        continue;
                    }

                    throw new ArgumentException($"Unknown zone {target}.", nameof(scene));
                }

                var zone = _controllers[slot].Descriptor.Zones[zoneIndex];
                layoutOf.TryAdd((slot, zoneIndex), layouts.Count);
                layouts.Add(zone.Layout);
                placements.Add(zone.Placement);
                members.Add((new Slice(slot, zoneIndex, offset), _controllers[slot].IsLoop));
                if (spanOf is not null)
                {
                    var span = spanOf[target];
                    spans.Add(span);
                    var chainFlip = zone.Family == LConnectLoops.Tl && ChainReversed?.Invoke(target.Controller) == true;
                    programSpans.Add(chainFlip ? span with { Reverse = !span.Reverse } : span);
                }

                offset += zone.LedCount;
            }

            if (layouts.Count == 0)
            {
                if (missing)
                {
                    continue;
                }

                throw new ArgumentException("Assignment has no targets.", nameof(scene));
            }

            // §7.6: X/Y from an owner-entered placement when present; U (and every Lian Li geometry) unchanged
            var samples = spanOf is not null
                ? GroupLayout.ConcatPath(layouts, placements, programSpans)
                : layouts.Count == 1 ? Projector.Project(layouts[0], placements[0]) : GroupLayout.Concat(layouts, placements);
            var loopSlices = members.Where(m => m.IsLoop).Select(m => m.Slice).ToList();
            var slices = members.Where(m => !m.IsLoop).Select(m => m.Slice).ToList();
            var played = new List<PlayedSlice>();
            var p = a.Params;
            var colors = p.Colors.Count > 0 ? p.Colors.ToArray() : gen.Schema.DefaultColors.ToArray();
            var loops = Array.Empty<CompiledLoop>();
            var period = 0.0;
            var livePeriod = double.NaN; // NaN = the assignment period
            var label = a.GroupName ?? a.Targets[0].ToString();
            var pathNote = a.Path is null ? string.Empty : $"; spatial path {a.Path.TotalLength} dots: {a.Path.Summary()}";
            // STACK-RETURN: a fallback that cannot carry the authored tail says so instead of dropping it silently
            var returnNote = a.LoopReturn is null ? string.Empty : $"; loop return NOT applied ({a.LoopReturn.Summary()}): this fallback has no hold/drain tail";
            string fallback;
            var planned = false;
            string? timelineId = null;
            var physical = false;
            string? blockedReason = null;
            if (a.Strategy == ContentStrategy.Frames)
            {
                // OPUS-GENERIC-SCENE-PACKAGES: an id the source claims is strict content: no effect fallback, no decimation, and a
                // physical-ms set is uploaded at its exact raw-tick interval or blocked
                var strict = !string.IsNullOrEmpty(a.FrameSetId) && FrameSets?.Claims(a.FrameSetId) == true;
                var set = ResolveFrameSet(a, members, out var why);
                if (set is null && strict && FrameSets!.Problem(a.FrameSetId!) is { } problem)
                {
                    why = problem;
                }

                if (set is not null && strict && FrameSets!.BoundTargets(a.FrameSetId!) is { } bound && !bound.SequenceEqual(a.Targets))
                {
                    why = $"frame set {set.Id}: the assignment targets are not exactly the bound targets of its binding snapshot (never retargeted)";
                    set = null;
                }

                var rawTicks = 0.0;
                if (set is { Timing: FrameSetTiming.PhysicalMs } && !FrameSet.TryRawTicks(set.IntervalMs, out rawTicks) && members.Any(m => m.IsLoop))
                {
                    why = $"frame set {set.Id}: {set.IntervalMs} ms is not exactly representable as raw RF ticks at {FrameSet.DeviceTicksPerSecond:0}/s (never quantized)";
                    set = null;
                }

                fallback = $"Frames: {why}";
                if (set is null && strict)
                {
                    // blocked: nothing of this assignment is rendered (its loop zones fall to the uncovered black loop, streamed zones
                    // stay cleared); the reason is reported, never an invented show
                    blockedReason = $"{label}: BLOCKED Frames:{a.FrameSetId}: {why}";
                    blocked.Add(blockedReason);
                    variants.Add(blockedReason);
                    timelineId = a.FrameSetId;
                    slices.Clear();
                    planned = true;
                }
                else if (set is not null)
                {
                    physical = set.Timing == FrameSetTiming.PhysicalMs;
                    timelineId = strict ? set.Id : null;
                    var wireInterval = physical ? rawTicks : set.IntervalMs;
                    // §7.2 Frames: the authored frames for every zone, one period, one anchor; brightness applies on delivery
                    period = set.PeriodSeconds;
                    var list = new List<CompiledLoop>();
                    for (var i = 0; i < members.Count; i++)
                    {
                        var (s, isLoop) = members[i];
                        var c = _controllers[s.ControllerSlot].Descriptor;
                        var z = c.Zones[s.ZoneIndex];
                        if (isLoop)
                        {
                            list.Add(new CompiledLoop((c.Id, z.Id), Staged(_controllers[s.ControllerSlot].Stage, set.ZoneRgb[i], p.Brightness),
                                new LoopTiming(set.Frames, wireInterval), IsH2(z) ? "ring" : null, strict));
                        }
                        else
                        {
                            played.Add(PlayedOf(s, c, z, set.ZoneRgb[i], set.Frames, set.IntervalMs, p.Brightness));
                        }
                    }

                    loops = [.. list];
                    slices.Clear();
                    planned = true;
                    variants.Add(physical
                        ? $"{label}: Frames:{set.Id}: {set.Frames} frames × {set.IntervalMs:0.##} physical ms (RF {rawTicks:0.##} raw ticks at {FrameSet.DeviceTicksPerSecond:0}/s) = {set.PeriodSeconds:0.###} s{(strict ? "; strict" : string.Empty)}"
                        : $"{label}: Frames:{set.Id}: {set.Frames} frames × {set.IntervalMs:0.##} ms = {set.PeriodSeconds:0.###} s{(strict ? "; strict" : string.Empty)}");
                }
            }
            else if (a.Strategy == ContentStrategy.Vendor && loopSlices.Count > 0 && slices.Count > 0 && LConnectLoops.IsPerDeviceGroupRoute(a.EffectId))
            {
                // §7.3 per-device route (RainbowMorph, Twinkle): Lian Li zones keep their family bodies (no shared phase in the
                // vendor either); streamed members render the Program equivalent live
                var vendor = LConnectLoops.TryPlan(a.EffectId, p, colors, loopSlices.Select(VendorZoneOf).ToArray(), out var why);
                fallback = $"LConnect: {why}";
                if (vendor is not null)
                {
                    period = vendor.PeriodSeconds;
                    livePeriod = 0;
                    loops = VendorLoops(a.EffectId, vendor, loopSlices.Select(sl => (sl, true)).ToList(), played);
                    planned = true;
                    variants.Add($"{label}: Vendor:{a.EffectId}+program({slices.Count}) {vendor.Variant}: {vendor.Summary}");
                }
            }
            else if (a.Strategy == ContentStrategy.Vendor && loopSlices.Count > 0)
            {
                // §7.3: one plan over the whole strip in target order; streamed members get their slice of it, played (§7.5)
                var zones = members.Select(m => m.IsLoop
                    ? VendorZoneOf(m.Slice)
                    : new LConnectZone(LConnectLoops.Streamed, 0, _controllers[m.Slice.ControllerSlot].Descriptor.Zones[m.Slice.ZoneIndex].LedCount)).ToArray();
                var vendor = LConnectLoops.TryPlan(a.EffectId, p, colors, zones, spanOf is null ? null : spans, a.LoopReturn, out var why);
                fallback = $"LConnect: {why}";
                if (vendor is not null)
                {
                    // WP-11: L-Connect's own frames, frame count and interval; no MaxLoopFrames cap (the adapter's size fallback applies)
                    period = vendor.PeriodSeconds;
                    loops = VendorLoops(a.EffectId, vendor, members, played, explicitPath: spanOf is not null);
                    slices.Clear();
                    planned = true;
                    variants.Add($"{label}: Vendor:{a.EffectId} {vendor.Variant}: {vendor.Summary}{pathNote}");
                }
            }
            else
            {
                // Program, or Vendor with no Lian Li zone (nothing carries a vendor body: streamed zones render live as today)
                fallback = a.Strategy == ContentStrategy.Program ? "Generic" : "LConnect: no Lian Li zone carries a vendor body";
            }

            if (!planned && loopSlices.Count > 0)
            {
                var caps = loopSlices.Select(s => _controllers[s.ControllerSlot].Descriptor.Capabilities).ToArray();
                var natural = LoopCompiler.NaturalPeriod(gen, p with { Colors = colors });
                period = LoopCompiler.GroupPeriod(natural, caps, out var cut);
                if (cut)
                {
                    truncated.Add($"{label}: {a.EffectId} period {natural:0.###} s truncated to {period:0.###} s");
                }

                var timings = caps.Select(c => LoopCompiler.TimingFor(period, c)).ToArray();
                var targets = loopSlices.Select((s, i) => new LoopTarget(
                    s.SourceOffset,
                    _controllers[s.ControllerSlot].Descriptor.Zones[s.ZoneIndex].LedCount,
                    _controllers[s.ControllerSlot].Stage,
                    timings[i].Frames)).ToArray();
                // WP-14 (H2-VERTICAL-FILL §1): a vertical-fill H2 of a multi-zone translational group is rendered as Linear(24)
                // along the group's u, then placed by height with the same RingFillMap as the vendor slices. "Multi-zone" counts
                // every logical member, streamed ones included (an H2 grouped with one GPU zone is a group, not a lone ring).
                var shape = EffectGeometry.Of(a.EffectId);
                var fills = loopSlices.Select(s =>
                {
                    var c = _controllers[s.ControllerSlot].Descriptor;
                    var z = c.Zones[s.ZoneIndex];
                    return (members.Count > 1 || spanOf is not null) && IsH2(z) && z.LedCount == RingFillMap.Leds && RingOf(c.Id).FaceOnFill && shape.IsTranslational
                        ? shape.Bottom
                        : null;
                }).ToArray();
                var renderSamples = samples;
                if (fills.Any(f => f is not null))
                {
                    var fillLayouts = layouts.ToArray();
                    for (var i = 0; i < loopSlices.Count; i++)
                    {
                        if (fills[i] is not null)
                        {
                            fillLayouts[layoutOf[(loopSlices[i].ControllerSlot, loopSlices[i].ZoneIndex)]] = ZoneLayout.Linear(RingFillMap.Leds);
                        }
                    }

                    renderSamples = spanOf is not null ? GroupLayout.ConcatPath(fillLayouts, placements, programSpans) : GroupLayout.Concat(fillLayouts, placements);
                }

                var rgb = LoopCompiler.Render(gen, renderSamples, colors, p.Speed, p.Brightness, p.Direction, p.Seed, period, targets);
                var sampling = RingFillMap.SamplingFor(a.EffectId);
                loops = loopSlices.Select((s, i) =>
                {
                    var c = _controllers[s.ControllerSlot].Descriptor;
                    var z = c.Zones[s.ZoneIndex];
                    var data = fills[i] is { } bottom ? RingFillMap.ApplyFrames(rgb[i], bottom, RingOf(c.Id).HalfStep, sampling) : rgb[i];
                    return new CompiledLoop((c.Id, z.Id), data, timings[i], GeometryTag(z, RingOf(c.Id), shape, fills[i], members.Count == 1 && spanOf is null, sampling));
                }).ToArray();
                var filledAt = Array.FindIndex(fills, f => f is not null);
                var flagged = loopSlices.Any(s => IsH2(_controllers[s.ControllerSlot].Descriptor.Zones[s.ZoneIndex]) && RingOf(_controllers[s.ControllerSlot].Descriptor.Id).FaceOnFill);
                var suffix = filledAt >= 0
                    ? $"; H2 vertical-fill (bottom={fills[filledAt]}{(RingOf(_controllers[loopSlices[filledAt].ControllerSlot].Descriptor.Id).HalfStep ? ", half-step" : string.Empty)}{(sampling == RingSampling.Linear ? ", interpolated" : string.Empty)})"
                    : flagged ? "; H2 ring" : string.Empty;
                variants.Add($"{label}: Program:{a.EffectId} generic ({fallback}){suffix}{pathNote}{returnNote}");
            }
            else if (!planned && a.LoopReturn is not null)
            {
                // no uploaded-loop member: the streamed members render the generic effect live
                variants.Add($"{label}: Program:{a.EffectId} live ({fallback}){pathNote}{returnNote}");
            }

            var live = double.IsNaN(livePeriod) ? period : livePeriod;
            items.Add(new PlanItem(gen, samples, new Rgb[samples.Length], colors, p.Speed, p.Brightness, p.Direction, p.Seed, [.. slices], [.. played],
                loops, period, live, null, ClockBinding.For(label, loops, played.Count > 0, slices.Count > 0, live > 0, physical), timelineId, physical, blockedReason));
        }

        return new Plan([.. items], truncated, variants, blocked);
    }

    /// <summary>
    /// The compiled loops of a vendor plan whose zones are <paramref name="members"/> in order: uploaded-loop members become
    /// loops (staged at full scale, the body applied its brightness), streamed members are added to <paramref name="played"/>.
    /// </summary>
    private CompiledLoop[] VendorLoops(string effectId, LConnectPlan vendor, List<(Slice Slice, bool IsLoop)> members, List<PlayedSlice> played,
        bool explicitPath = false)
    {
        var shape = EffectGeometry.Of(effectId);
        var shared = LConnectLoops.IsSharedGroup(effectId, members.Count, explicitPath);
        var sampling = RingFillMap.SamplingFor(effectId);
        var list = new List<CompiledLoop>();
        for (var i = 0; i < members.Count; i++)
        {
            var (s, isLoop) = members[i];
            var c = _controllers[s.ControllerSlot].Descriptor;
            var z = c.Zones[s.ZoneIndex];
            var zl = vendor.Zones[i];
            if (!isLoop)
            {
                played.Add(PlayedOf(s, c, z, zl.Rgb, zl.Frames, zl.IntervalMs, 1f));
                continue;
            }

            var filled = shared ? LConnectLoops.FillFor(VendorZone(c, s.ZoneIndex), shape) : null;
            list.Add(new CompiledLoop(
                (c.Id, z.Id),
                Staged(_controllers[s.ControllerSlot].Stage, zl.Rgb),
                new LoopTiming(zl.Frames, zl.IntervalMs),
                GeometryTag(z, RingOf(c.Id), shape, filled, !shared, sampling)));
        }

        return [.. list];
    }

    /// <summary>A streamed zone's frames (layout order) as pixels, played by <see cref="Player"/> and staged on every tick.</summary>
    private static PlayedSlice PlayedOf(Slice s, ControllerDescriptor c, ZoneDescriptor z, byte[] rgb, int frames, double intervalMs, float brightness)
    {
        var leds = z.LedCount;
        if (rgb.Length != frames * leds * 3)
        {
            throw new InvalidOperationException($"{c.Id}/{z.Id}: {rgb.Length} bytes are not {frames} frames × {leds} LEDs.");
        }

        var px = new Rgb[frames * leds];
        for (var i = 0; i < px.Length; i++)
        {
            px[i] = new Rgb(rgb[3 * i], rgb[(3 * i) + 1], rgb[(3 * i) + 2]);
        }

        return new PlayedSlice((c.Id, z.Id), s.ControllerSlot, s.ZoneIndex, px, frames, leds, intervalMs, brightness);
    }

    /// <summary>The assignment's frame set when it exists, is well formed and matches the targets zone by zone; else null and why.</summary>
    private FrameSet? ResolveFrameSet(Assignment a, List<(Slice Slice, bool IsLoop)> members, out string why)
    {
        if (string.IsNullOrEmpty(a.FrameSetId))
        {
            why = "the assignment names no frame set";
            return null;
        }

        if (FrameSets is null || !FrameSets.TryGet(a.FrameSetId, out var set))
        {
            why = $"frame set {a.FrameSetId} not found";
            return null;
        }

        var bad = set.Validate();
        if (bad is not null)
        {
            why = bad;
            return null;
        }

        if (set.ZoneLedCounts.Count != members.Count)
        {
            why = $"frame set {set.Id} has {set.ZoneLedCounts.Count} zones, the assignment {members.Count}";
            return null;
        }

        for (var i = 0; i < members.Count; i++)
        {
            var leds = _controllers[members[i].Slice.ControllerSlot].Descriptor.Zones[members[i].Slice.ZoneIndex].LedCount;
            if (set.ZoneLedCounts[i] != leds)
            {
                why = $"frame set {set.Id} zone {i} has {set.ZoneLedCounts[i]} LEDs, the target {leds}";
                return null;
            }
        }

        why = string.Empty;
        return set;
    }

    private LConnectZone VendorZoneOf(Slice s) => VendorZone(_controllers[s.ControllerSlot].Descriptor, s.ZoneIndex);

    /// <summary>The vendor view of a loop zone (family/units tagged by the adapter's Describe).</summary>
    private LConnectZone VendorZone(ControllerDescriptor c, int zoneIndex)
    {
        var z = c.Zones[zoneIndex];
        var ring = RingOf(c.Id);
        return new(z.Family ?? string.Empty, z.Units, z.LedCount, ChainReversed?.Invoke(c.Id) == true, ring.FaceOnFill, ring.HalfStep);
    }

    private RingGeometry RingOf(ControllerId id) => Ring?.Invoke(id) ?? default;

    private static bool IsH2(ZoneDescriptor z) => string.Equals(z.Family, LConnectLoops.H2, StringComparison.Ordinal);

    /// <summary>The Diagnostics geometry tag of one loop zone (H2-VERTICAL-FILL §5); null for non-H2 zones.</summary>
    /// <remarks>WP-16: an interpolated fill (<see cref="RingSampling.Linear"/>) appends " sampling=linear"; nearest keeps the WP-14 tag.</remarks>
    private static string? GeometryTag(ZoneDescriptor z, RingGeometry ring, EffectShape shape, BottomEnd? filled, bool perDevice,
        RingSampling sampling = RingSampling.Nearest)
    {
        if (!IsH2(z))
        {
            return null;
        }

        if (filled is { } bottom)
        {
            var offset = ring.OffsetLeds.ToString("+0;-0;+0", System.Globalization.CultureInfo.InvariantCulture);
            return $"vertical-fill offset={offset} half={(ring.HalfStep ? "true" : "false")} bottom={bottom}{(sampling == RingSampling.Linear ? " sampling=linear" : string.Empty)}";
        }

        if (!ring.FaceOnFill)
        {
            return "ring";
        }

        if (shape.IsTranslational)
        {
            return perDevice ? "ring (per-device body)" : "ring";
        }

        return $"ring (effect is {shape.Class.ToString().ToLowerInvariant()})";
    }

    /// <summary>
    /// The controller's OutputStage at <paramref name="brightness"/> (full scale for vendor bodies, which already applied their
    /// brightness; the assignment's brightness for authored frame sets).
    /// </summary>
    private static byte[] Staged(OutputStage stage, byte[] rgb, float brightness = 1f)
    {
        if (ReferenceEquals(stage, OutputStage.Identity) && brightness >= 1f)
        {
            return rgb;
        }

        var px = new Rgb[rgb.Length / 3];
        for (var i = 0; i < px.Length; i++)
        {
            px[i] = new Rgb(rgb[3 * i], rgb[(3 * i) + 1], rgb[(3 * i) + 2]);
        }

        stage.Apply(px, brightness);
        var dst = new byte[rgb.Length];
        LoopCompiler.Pack(px, dst);
        return dst;
    }

    private static int FindZone(ControllerDescriptor d, ZoneId id)
    {
        for (var i = 0; i < d.Zones.Count; i++)
        {
            if (d.Zones[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record ControllerSlot(ControllerDescriptor Descriptor, Mailbox Mailbox, OutputStage Stage, bool IsLoop);

    private readonly record struct Slice(int ControllerSlot, int ZoneIndex, int SourceOffset);

    private sealed record CompiledLoop((ControllerId, ZoneId) Key, byte[] Rgb, LoopTiming Timing, string? Geometry = null, bool Strict = false);

    /// <summary>A streamed zone that plays compiled frames (Vendor slice or Frames) instead of rendering the generator live.</summary>
    private sealed record PlayedSlice((ControllerId, ZoneId) Key, int ControllerSlot, int ZoneIndex, Rgb[] Pixels, int Frames, int Leds, double IntervalMs, float Brightness);

    /// <summary>The epoch the loops of one compile share; mutable so AnchorLoop can correct it after the upload starts.</summary>
    private sealed class LoopAnchor
    {
        public uint EffectIndex { get; init; }

        public double T0 { get; set; }

        public bool Anchored { get; set; }

        /// <summary>An epoch of frame-played streamed zones with no uploaded companion; no adapter report moves it.</summary>
        public bool Local { get; init; }
    }

    private enum ClockFallback
    {
        None,
        Disabled,
        NotRendered,
        StaticReference,
        NoClock,
        NoSample,
        Stale,
        MixedDomains,
        Future,
        TimingPending,
        TimingChanged,
    }

    /// <summary>What a completed upload actually transmitted for one zone (R2).</summary>
    private sealed record UploadedTiming(uint EffectIndex, int Frames, double IntervalMs, bool StaticFallback);

    /// <summary>
    /// One assignment's reference-clock binding: the clock handles of its uploaded loops (resolved off the hot path), the
    /// reference loop's wire timing, and what the last frame used. Mutated only under the render lock.
    /// </summary>
    private sealed class ClockBinding
    {
        public required string Label { get; init; }

        /// <summary>Streamed members share the uploaded loops' phase (played slices, or live slices with the group period).</summary>
        public bool Shares { get; init; }

        public int RefFrames { get; init; }

        /// <summary>The reference loop's header interval, rounded as the wire carries it (0.01 units).</summary>
        public double RefInterval { get; init; }

        /// <summary>Frames × wire interval: the reference loop's period in header units (= device ticks in the bridge model).</summary>
        public double RefPeriodTicks => RefFrames * RefInterval;

        /// <summary>Another uploaded loop of the assignment has a different wire period (reported; the first loop is the reference).</summary>
        public bool PeriodsDiffer { get; init; }

        public IDeviceClock?[] Clocks { get; set; } = [];

        public ControllerId? Missing { get; set; }

        public StreamedClockMode Mode { get; set; }

        public ClockFallback Reason { get; set; } = ClockFallback.Disabled;

        public double Age { get; set; }

        public double Phase { get; set; }

        public double TicksPerSecond { get; set; } = DeviceClockTicksPerSecond;

        /// <summary>R2 verdict on the transmitted timing: None (verified equal), TimingPending or TimingChanged.</summary>
        public ClockFallback Timing { get; set; } = ClockFallback.TimingPending;

        public string? TimingKey { get; set; }

        public string? TimingDetail { get; set; }

        /// <summary>The reference loop's transmitted frames / wire interval for its current index (null until reported).</summary>
        public int? UploadedFrames { get; set; }

        public double? UploadedInterval { get; set; }

        /// <param name="physical">
        /// A physical-ms frame set (OPUS-GENERIC-SCENE-PACKAGES): its uploaded loops always bind the device clock, even without a
        /// streamed member, because the package's shared timeline (screens) reads the same phase.
        /// </param>
        public static ClockBinding For(string label, CompiledLoop[] loops, bool played, bool live, bool livePeriod, bool physical = false)
        {
            var shares = loops.Length > 0 && (played || (live && livePeriod) || physical);
            var mode = loops.Length == 0
                ? played ? StreamedClockMode.LocalEpoch : StreamedClockMode.MasterTime
                : shares ? StreamedClockMode.UploadAnchor : live ? StreamedClockMode.MasterTime : StreamedClockMode.LoopsOnly;
            if (loops.Length == 0)
            {
                return new ClockBinding { Label = label, Mode = mode, Reason = ClockFallback.None };
            }

            var wire = loops.Select(l => l.Timing.Frames * WireInterval(l.Timing.IntervalMs)).ToArray();
            return new ClockBinding
            {
                Label = label,
                Shares = shares,
                Mode = mode,
                Reason = shares ? ClockFallback.Disabled : ClockFallback.None,
                RefFrames = loops[0].Timing.Frames,
                RefInterval = WireInterval(loops[0].Timing.IntervalMs),
                PeriodsDiffer = wire.Any(w => w != wire[0]),
            };
        }

        /// <summary>The header's interval resolution: units + hundredths (the adapter rounds <c>I × 100</c>).</summary>
        public static double WireInterval(double interval) => Math.Round(interval * 100.0) / 100.0;
    }

    private sealed record PlanItem(
        IEffectGenerator Generator,
        Sample[] Samples,
        Rgb[] Scratch,
        Rgb[] Colors,
        float Speed,
        float Brightness,
        Direction Direction,
        uint Seed,
        Slice[] Slices,
        PlayedSlice[] Played,
        CompiledLoop[] Loops,
        double Period,
        double LivePeriod,
        LoopAnchor? Anchor,
        ClockBinding Clock,
        string? TimelineId = null,
        bool Physical = false,
        string? BlockedReason = null);

    private sealed record Plan(PlanItem[] Items, IReadOnlyList<string> Truncated, IReadOnlyList<string> Variants, IReadOnlyList<string>? Blocked = null)
    {
        public static Plan Empty { get; } = new([], [], []);
    }
}
