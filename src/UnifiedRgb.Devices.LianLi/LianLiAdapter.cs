using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>A wireless device the owner lists until RF device enumeration is verified on hardware.</summary>
public sealed class LianLiDeviceConfig
{
    public string Mac { get; set; } = string.Empty;

    /// <summary>Family name (e.g. "TL"). Empty → resolved from <see cref="RecType"/>.</summary>
    public string Family { get; set; } = string.Empty;

    /// <summary>L-Connect's <c>RecType[0]</c> (savedDevices.config), e.g. 28 = TL, 10 = H2 waterblock. 0 = not given.</summary>
    public int RecType { get; set; }

    /// <summary>L-Connect's <c>dev_type</c> (0 for fan groups, 10/11 for an H2 waterblock).</summary>
    public int DevType { get; set; }

    /// <summary>L-Connect's <c>FanNum</c>. With <see cref="RecType"/> set, <see cref="Leds"/> must equal the IL's <c>LedNum</c>.</summary>
    public int FanNum { get; set; }

    public int Leds { get; set; }

    public string? Name { get; set; }

    /// <summary>Index into the discovered sender dongles (ordered by device path).</summary>
    public int Sender { get; set; }

    /// <summary>The device's RF channel (receiver device-list record byte 12). 0 = not configured → not driven.</summary>
    public int Channel { get; set; }

    /// <summary>The device's rx slot, 1..13 (device-list record byte 13). 0 = not configured → not driven.</summary>
    public int Rx { get; set; }

    /// <summary>Physical mounting (WP-11); the Host's mounting.json overrides it per controller. Identity by default.</summary>
    public LianLiMounting Mounting { get; set; } = new();
}

/// <summary><c>LianLi:Loop</c> (ARCHITECTURE-D2 §4).</summary>
public sealed class LianLiLoopOptions
{
    /// <summary>Default per-frame interval (the IL's common <c>interval_base</c>).</summary>
    public double IntervalMs { get; set; } = 20;

    /// <summary>Most frames per loop (the IL's literal upper value).</summary>
    public int MaxFrames { get; set; } = 200;
}

public enum LianLiCompressionCodec { Managed, VendorYuz }

public sealed class LianLiOptions
{
    /// <summary>
    /// Safety gate (like MsiGpu:RegisterMapVerified). Means "our RF 32 upload bytes equal L-Connect's for the same input"
    /// (ARCHITECTURE-D2 §3); flipping it needs the [SIM] codec self-check plus the StaticColor and Rainbow usbpcap captures
    /// [HW]. While false no Lian Li device is opened and nothing is written (the codec is still loaded and self-checked).
    /// </summary>
    public bool WireFormatVerified { get; set; }

    /// <summary>
    /// Optional explicit vendor codec path, used ONLY when Codec=VendorYuz. Never searched for, copied or redistributed.
    /// </summary>
    public string YuzDllPath { get; set; } = string.Empty;

    /// <summary>Managed is independent and redistributable. VendorYuz is an explicit local comparison/rollback option.</summary>
    public LianLiCompressionCodec Codec { get; set; } = LianLiCompressionCodec.Managed;

    public LianLiLoopOptions Loop { get; set; } = new();

    /// <summary>
    /// Manual offset (ms) of the streamed reference clock of group members that share an uploaded loop's epoch
    /// (NATIVE-PLAYBACK-CALIBRATION): they sample at <c>(t − T0 − PhaseTrimMs / 1000) × StreamedPlaybackRate</c> from the raw
    /// first-header T0; positive delays them. Finite in [−2000, 2000]; source default 0. A host-side calibration, never a packet
    /// field; the Host's initial value of its runtime calibration (GetPlaybackCalibration / SetPlaybackCalibration).
    /// </summary>
    public double PhaseTrimMs { get; set; }

    /// <summary>
    /// Manual rate of that streamed reference clock relative to the nominal header model (frames × header interval), referenced
    /// to the uploaded RF domain (NATIVE-PLAYBACK-CALIBRATION C1). Finite in [0.25, 4]; source default 1. Never a hardware, bus
    /// or packet speed: RF bytes, upload intervals and the master clock are unchanged by it.
    /// </summary>
    public double StreamedPlaybackRate { get; set; } = 1;

    /// <summary>
    /// Experimental (NATIVE-RF-CLOCK), source default false. True: streamed companions of uploaded loops take their phase from
    /// the RF sender's master counter (USB 17 <c>time_tmos</c>, extrapolated at 1600 ticks/s), read as the recovered vendor
    /// bridge does: frame = counter / header interval mod frames, one header interval unit per 0.625 ms tick. This is the
    /// vendor host model, not firmware proof or a playback ACK. Missing, stale or mixed clocks fall back to the upload anchor.
    /// <see cref="PhaseTrimMs"/> stays a host-time delay (ms → ticks × 1.6); <see cref="StreamedPlaybackRate"/> becomes a
    /// residual factor on the counter (keep 1: no extra 1.6 is implied). Nothing on the wire changes.
    /// </summary>
    public bool UseDeviceClock { get; set; }

    /// <summary>Read-only receiver generation observations; source default disabled until the receiver interface is verified.</summary>
    public bool ReceiverFeedbackEnabled { get; set; }

    /// <summary>At most two same-generation/payload retries on advancing, repeatedly mismatching reports. Requires feedback.</summary>
    public bool ReceiverRetriesEnabled { get; set; }

    /// <summary>Re-upload current loops with a fresh EffectIndex every N minutes; 0 = off (D2 §2).</summary>
    public double ReanchorMinutes { get; set; }

    /// <summary>WinUSB DeviceInterfaceGUID of the RF dongles (from the INF). Empty → no discovery. Name kept for config compatibility.</summary>
    public string ReceiverInterfaceGuid { get; set; } = string.Empty;

    /// <summary>The master RF channel ClockSync is broadcast on (<c>MasterDevice.MasterChannel</c>, initially 8).</summary>
    public byte MasterChannel { get; set; } = LianLiWire.DefaultMasterChannel;

    public IList<LianLiDeviceConfig> Devices { get; } = new List<LianLiDeviceConfig>();
}

/// <summary>
/// Lian Li wireless (RF) devices through the USB sender dongle, UploadedLoop delivery (ARCHITECTURE-D2). The IL has no
/// per-frame colour path: a device plays an uploaded, compressed loop (RF 32, <c>SyncRgbData</c>). One RfDevice = one chain =
/// one zone. <see cref="UploadLoopAsync"/> compresses the engine's <see cref="LoopFrames"/> with the configured codec (halving frames
/// while over 12288 bytes, then a static frame), checks the loop header, and relays it in 240-byte packets (four USB 16
/// chunks each) under the sender's packet budget, through OpcodeFirewall → FrameValidator (and the firewall again inside
/// the physical transport). Uploads are serialised per sender. <see cref="WriteFrame"/> is not supported. RF 16 (bind + fan
/// PWM) is never sent; RF 21 only through <see cref="SaveConfigAsync"/>.
/// </summary>
public sealed partial class LianLiAdapter : ILoopUploadAdapter, IDeviceClockProvider, ILoopRecoveryControl, IAsyncDisposable
{
    /// <summary>D2 §5: at most one SaveConfig per minute.</summary>
    public static readonly TimeSpan SaveConfigMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>D2 §4: halvings tried before falling back to a static frame.</summary>
    public const int MaxDecimations = 3;

    private readonly LianLiOptions _options;
    private readonly IUsbEnumerator _enumerator;
    private readonly Func<UsbDeviceInfo, ITransport> _open;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<ControllerId, RfDevice> _found = [];
    private readonly Dictionary<ControllerId, Session> _sessions = [];
    private readonly Dictionary<string, RfSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceState _senderState = new();
    private readonly IHotplugSource? _hotplug;
    private readonly Func<IRgbAnimationEncoder>? _encoderFactory;
    private readonly Dictionary<ControllerId, LoopUploadStats> _lastUploads = [];
    private readonly Dictionary<ControllerId, MountingRevision> _mountings = [];
    private readonly Dictionary<ControllerId, LianLiMounting> _mountingOverrides = [];
    private readonly Dictionary<ControllerId, MountingRevision> _calibrating = [];
    private readonly Dictionary<ControllerId, SessionClock> _clocks = [];
    private readonly Func<long>? _timestamp;
    private int _mountingRevision;
    private readonly Action<TimeSpan>? _sleep;
    private readonly Func<DateTime>? _now;
    private IRgbAnimationEncoder? _encoder;
    private bool _ownsEncoder;
    private DateTime? _lastSave;
    private Timer? _keepAlive;
    private readonly object _senderLifecycleGate = new();
    private readonly Func<byte[]>? _receiverProbe;
    private readonly object _feedbackGate = new();
    private TimeSpan _lastReceiverQuery = -TimeSpan.FromDays(1);
    private readonly bool _startKeepAliveTimer;
    private readonly Func<TimeSpan> _feedbackElapsed;

    public LianLiAdapter(
        LianLiOptions options,
        IUsbEnumerator enumerator,
        Func<UsbDeviceInfo, ITransport> open,
        IHotplugSource? hotplug = null,
        ILogger? logger = null,
        IRgbAnimationEncoder? encoder = null,
        Action<TimeSpan>? sleep = null,
        Func<DateTime>? now = null,
        Func<IRgbAnimationEncoder>? encoderFactory = null,
        Func<long>? timestamp = null,
        Func<byte[]>? receiverProbe = null,
        bool startKeepAliveTimer = true,
        Func<TimeSpan>? feedbackElapsed = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _logger = logger ?? NullLogger.Instance;
        _hotplug = hotplug;
        _encoder = encoder;
        _encoderFactory = encoderFactory;
        _sleep = sleep;
        _now = now;
        _timestamp = timestamp;
        _receiverProbe = receiverProbe;
        _startKeepAliveTimer = startKeepAliveTimer;
        var feedbackEpoch = Stopwatch.GetTimestamp();
        _feedbackElapsed = feedbackElapsed ?? (() => Stopwatch.GetElapsedTime(feedbackEpoch));
        if (_hotplug is not null)
        {
            _hotplug.Changed += OnInterfaceChanged;
        }
    }

    public event Action<HotplugEvent>? Hotplug;

    public string Vendor => "Lian Li";

    public DeviceClass Class => DeviceClass.LianLi;

    /// <summary>Passive: enumerates sender dongle interfaces (no I/O to the device) and pairs them with the configured devices.</summary>
    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var devices = LianLiDiscovery.Discover(_options, _enumerator, _logger);
        lock (_gate)
        {
            _found.Clear();
            foreach (var d in devices)
            {
                var id = IdOf(d);
                _found[id] = d;
                var settings = _mountingOverrides.TryGetValue(id, out var o) ? o : ConfiguredMounting(d);
                if (_mountings.TryGetValue(id, out var current) && SameMounting(current.Settings, settings))
                {
                    continue; // rediscovery keeps the revision (and its map)
                }

                if (MountingMap.TryBuild(d.Assembler, d.LedCount, settings, out var map, out var error))
                {
                    _mountings[id] = new MountingRevision(++_mountingRevision, settings.Clone(), map, MountingMap.Describe(settings));
                }
                else
                {
                    LogMountingRejected(id.Value, error);
                    _mountings[id] = MountingRevision.Identity with { Revision = ++_mountingRevision };
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ControllerDescriptor>>(devices.Select(d => Describe(d, _options.Loop)).ToArray());
    }

    public static ControllerId IdOf(RfDevice d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new ControllerId($"lianli:rf:{d.Mac}");
    }

    /// <summary>
    /// One zone per device: the whole chain as one LED array, as <c>FanNum</c> holds it in the IL (D2 §1). A single fan is
    /// a ring; a chain of fans runs end to end (linear u), so a group head crosses fans in order.
    /// </summary>
    public static ControllerDescriptor Describe(RfDevice d, LianLiLoopOptions? loop = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        loop ??= new LianLiLoopOptions();
        var zones = new[]
        {
            new ZoneDescriptor(ChainZone, ZoneName(d), d.Assembler.ChainLayout(d.LedCount))
            {
                Family = d.Assembler.Family,
                Units = d.Assembler.UnitsIn(d.LedCount),
            },
        };
        return new ControllerDescriptor(IdOf(d), "Lian Li", d.Name, DeviceType.Cooler, DeviceClass.LianLi, d.Mac.ToString(), zones,
            Capabilities.UploadedLoopOnly(Math.Clamp(loop.MaxFrames, 1, ushort.MaxValue), Math.Max(1, loop.IntervalMs), LianLiWire.MaxCompressedAnimation));
    }

    public static ZoneId ChainZone { get; } = new(0);

    /// <summary>The device's current mounting revision (identity for unknown devices).</summary>
    public MountingRevision MountingOf(ControllerId id)
    {
        lock (_gate)
        {
            if (_calibrating.TryGetValue(id, out var c))
            {
                return c;
            }

            return _mountings.TryGetValue(id, out var r) ? r : MountingRevision.Identity;
        }
    }

    /// <summary>
    /// WP-14 ring calibration (H2-VERTICAL-FILL §3): a transient mounting overlay used by uploads while the owner steps the
    /// marker. Validated like <see cref="SetMounting"/>; keeps the current revision number (nothing is saved or bumped) and is
    /// dropped by <see cref="EndCalibration"/>. Repeated calls replace the overlay.
    /// </summary>
    public MountingRevision BeginCalibration(ControllerId id, LianLiMounting mounting)
    {
        ArgumentNullException.ThrowIfNull(mounting);
        lock (_gate)
        {
            if (!_found.TryGetValue(id, out var d))
            {
                throw new ArgumentException($"Unknown Lian Li device {id}.", nameof(id));
            }

            var settings = mounting.Clone();
            if (!MountingMap.TryBuild(d.Assembler, d.LedCount, settings, out var map, out var error))
            {
                throw new ArgumentException(error, nameof(mounting));
            }

            var revision = _mountings.TryGetValue(id, out var r) ? r.Revision : 0;
            var overlay = new MountingRevision(revision, settings, map, MountingMap.Describe(settings) + " (calibrating)");
            _calibrating[id] = overlay;
            return overlay;
        }
    }

    /// <summary>Drops a calibration overlay; returns the (unchanged) persisted revision.</summary>
    public MountingRevision EndCalibration(ControllerId id)
    {
        lock (_gate)
        {
            _calibrating.Remove(id);
            return _mountings.TryGetValue(id, out var r) ? r : MountingRevision.Identity;
        }
    }

    /// <summary>Whether a calibration overlay is active for <paramref name="id"/>.</summary>
    public bool IsCalibrating(ControllerId id)
    {
        lock (_gate)
        {
            return _calibrating.ContainsKey(id);
        }
    }

    /// <summary>
    /// Sets (or with null resets to the configured default) a device's mounting. Validated against the discovered family; an
    /// invalid setting throws <see cref="ArgumentException"/> and changes nothing. Returns the new immutable revision. The
    /// caller re-uploads the device's loop (canonical RGB is unchanged, DEBUG-SESSION-1 §3.3); nothing is flashed.
    /// </summary>
    public MountingRevision SetMounting(ControllerId id, LianLiMounting? mounting)
    {
        lock (_gate)
        {
            if (!_found.TryGetValue(id, out var d))
            {
                throw new ArgumentException($"Unknown Lian Li device {id}.", nameof(id));
            }

            var settings = mounting?.Clone() ?? ConfiguredMounting(d);
            if (!MountingMap.TryBuild(d.Assembler, d.LedCount, settings, out var map, out var error))
            {
                throw new ArgumentException(error, nameof(mounting));
            }

            if (mounting is null)
            {
                _mountingOverrides.Remove(id);
            }
            else
            {
                _mountingOverrides[id] = settings;
            }

            var rev = new MountingRevision(++_mountingRevision, settings, map, MountingMap.Describe(settings));
            _mountings[id] = rev;
            return rev;
        }
    }

    /// <summary>Host-persisted overrides (mounting.json) applied before discovery; validated when the device is discovered.</summary>
    public void SeedMountings(IReadOnlyDictionary<ControllerId, LianLiMounting> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        lock (_gate)
        {
            foreach (var (id, m) in overrides)
            {
                _mountingOverrides[id] = m.Clone();
            }
        }
    }

    /// <summary>The loaded codec's identity (SHA-256 for yuz.dll), or null before the first open.</summary>
    public string? CodecHash => _encoder?.CodecHash;

    /// <summary>
    /// Loads and self-checks the codec first (failure → <see cref="DeviceBlockedException"/>, shown as Blocked), then applies
    /// the WireFormatVerified gate. Sends only USB 17 (master MAC/clock); RF 18 is not needed (D2 §6).
    /// </summary>
    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        EnsureEncoder();
        if (!_options.WireFormatVerified)
        {
            throw new InvalidOperationException(
                "Lian Li wire format is not verified against captures (LianLi:WireFormatVerified=false); the device is not opened.");
        }

        RfDevice dev;
        lock (_gate)
        {
            dev = _found.TryGetValue(id, out var d) ? d : throw new ArgumentException($"Unknown Lian Li device {id}", nameof(id));
        }

        var sender = AcquireSender(dev.Sender); // reads the master MAC; no command is sent to the device on open
        lock (_gate)
        {
            _sessions[id] = new Session(dev, sender);
            ClockHandle(id).Bind(sender);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// NATIVE-RF-CLOCK: a stable handle on the master counter of the sender <paramref name="id"/>'s open session plays
    /// through. It follows open/close (no sample while closed, a reopened sender's fresh domain afterwards) and reads only
    /// what the sender's existing USB 17 queries cached: no query, handle or command is added. Null for an unknown id.
    /// </summary>
    public IDeviceClock? ClockOf(ControllerId id)
    {
        lock (_gate)
        {
            return _found.ContainsKey(id) || _sessions.ContainsKey(id) ? ClockHandle(id) : null;
        }
    }

    /// <summary>RF fans do not take streamed frames: they play uploaded loops (D2 §1; M5c dev. 8 removed).</summary>
    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) =>
        throw new NotSupportedException("Lian Li RF devices play uploaded loops (UploadLoopAsync); they take no streamed frames.");

    /// <summary>
    /// Compresses and uploads one loop. Waits for the sender's upload gate with <paramref name="ct"/> (a queued upload is
    /// abortable); once it holds the gate the upload runs to completion.
    /// </summary>
    public async Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loop);
        var s = Get(id);
        if (zone != ChainZone)
        {
            throw new ArgumentException($"{id} has one zone ({ChainZone}).", nameof(zone));
        }

        if (loop.LedCount != s.Device.LedCount)
        {
            throw new ArgumentException($"{id} has {s.Device.LedCount} LEDs; the loop has {loop.LedCount}.", nameof(loop));
        }

        // Invalidate queued recovery immediately, before mapping/compression or waiting for the upload gate.
        var generation = new object();
        lock (_gate)
        {
            s.Generation = generation;
            s.Delivery = null;
            if (_lastUploads.TryGetValue(id, out var previous)) { _lastUploads[id] = previous with { ReceiverDelivery = null }; }
        }

        var encoder = _encoder ?? throw new InvalidOperationException("No codec loaded.");
        var mounting = MountingOf(id); // one immutable revision for every frame of this upload
        if (s.Device.Assembler.ReordersWire || mounting.Map is not null)
        {
            // W then the fan-local mounting map O (§3.2), each exactly once, triplets intact; independent of ReordersWire (H2)
            var wire = MountingMap.Apply(s.Device.Assembler.ToWireOrder(loop.Rgb, loop.LedCount), loop.LedCount, mounting.Map);
            loop = new LoopFrames(loop.Frames, loop.LedCount, loop.IntervalMs, loop.EffectIndex, wire) { Geometry = loop.Geometry };
        }

        var payload = Compress(encoder, loop, LianLiWire.MaxCompressedAnimation);
        if (loop.Strict && payload.Decimations > 0)
        {
            // OPUS-GENERIC-SCENE-PACKAGES: strict content is uploaded exactly or not at all (the engine preflight should have
            // refused it already); nothing is sent, so the device keeps its previous loop
            throw new InvalidDataException($"{id}: strict loop of {loop.Frames} frames exceeds the {LianLiWire.MaxCompressedAnimation}-byte budget; not decimated, not uploaded.");
        }

        if (payload.Decimations > MaxDecimations)
        {
            LogStaticFallback(id.Value, loop.Frames, payload.Compressed.Length);
        }

        await s.Sender.UploadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cancellation can race a free gate. Until this transition no header is on the wire,
            // so a superseded queued loop must not start merely because acquisition won that race.
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            long started = 0;
            var packets = s.Sender.Upload(s.Device, payload.Animation, payload.Compressed, loop.EffectIndex, () =>
            {
                ct.ThrowIfCancellationRequested(); // last queued-to-in-flight boundary, before the first header
                started = Stopwatch.GetTimestamp();
            });
            var stats = new LoopUploadStats(
                new DateTimeOffset(_now?.Invoke() ?? DateTime.Now),
                started,
                loop.EffectIndex,
                payload.Animation.TotalFrames,
                payload.Animation.IntervalMs,
                payload.RawBytes,
                payload.Compressed.Length,
                packets,
                sw.Elapsed,
                payload.Decimations,
                encoder.CodecHash)
            {
                PeriodErrorMs = payload.PeriodErrorMs,
                Mapping = $"rev {mounting.Revision}: {mounting.Description}",
                SourceFrames = loop.Frames,
                StaticFallback = payload.Decimations > MaxDecimations,
                Geometry = loop.Geometry,
            };
            lock (_gate)
            {
                _lastUploads[id] = stats;
                if (ReferenceEquals(s.Generation, generation) && s.Sender.Master is { } master)
                {
                    s.Delivery = new PendingDelivery(generation, loop.EffectIndex, payload.Animation, payload.Compressed.ToArray(),
                        new RfDeliveryTracker(loop.EffectIndex, s.Device.Mac, master.Mac, s.Device.Address, _feedbackElapsed()));
                    if (_options.ReceiverFeedbackEnabled)
                    { _lastUploads[id] = stats with { ReceiverDelivery = s.Delivery.Tracker.Status }; }
                }
            }
        }
        finally
        {
            s.Sender.UploadGate.Release();
        }
    }

    public LoopUploadStats? LastUpload(ControllerId id)
    {
        lock (_gate)
        {
            if (!_lastUploads.TryGetValue(id, out var stats)) { return null; }
            if (_sessions.TryGetValue(id, out var session) && session.Delivery is { } pending && IsCurrent(session, pending))
            { return stats with { ReceiverDelivery = _options.ReceiverFeedbackEnabled ? pending.Tracker.Snapshot(_feedbackElapsed()) : null }; }
            if (stats.ReceiverDelivery is { } old)
            { return stats with { ReceiverDelivery = old with { State = "Unknown", ReportedEffectIndex = null, ReceiverCounter = null, ObservedAt = null, Detail = "No current upload/session for receiver evidence." } }; }
            return stats;
        }
    }

    /// <summary>
    /// D2 §4 size policy: compress; while over <paramref name="maxBytes"/> halve the frame count (N' = ⌈N/2⌉), resampling the
    /// ORIGINAL loop at <c>⌊k·N/N'⌋</c> and setting <c>I' = P/N'</c> rounded to the header's 0.01 ms, so the period P is kept
    /// (WP-11: the old "keep every 2nd frame, double I" stretched P for odd N); up to <see cref="MaxDecimations"/> times; then
    /// frame 0 as a one-frame loop (<c>Decimations = MaxDecimations + 1</c>). <see cref="LoopPayload.PeriodErrorMs"/> reports
    /// the remaining wire rounding.
    /// </summary>
    public static LoopPayload Compress(IRgbAnimationEncoder encoder, LoopFrames loop, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(loop);
        var frameBytes = loop.LedCount * 3;
        var source = loop.Rgb.Span;
        var rgb = source.ToArray();
        var frames = loop.Frames;
        var interval = loop.IntervalMs;
        var periodMs = loop.Frames * loop.IntervalMs;
        for (var d = 0; d <= MaxDecimations; d++)
        {
            var packed = encoder.Encode(rgb);
            if (packed.Length <= maxBytes)
            {
                return new LoopPayload(new RgbAnimation(packed.Length, frames, loop.LedCount, interval), packed, rgb.Length, d)
                {
                    PeriodErrorMs = d == 0 ? 0 : (frames * WireInterval(interval)) - periodMs,
                };
            }

            if (d == MaxDecimations || frames == 1)
            {
                break;
            }

            var kept = (frames + 1) / 2;
            var next = new byte[kept * frameBytes];
            for (var k = 0; k < kept; k++)
            {
                var src = (int)((long)k * loop.Frames / kept);
                source.Slice(src * frameBytes, frameBytes).CopyTo(next.AsSpan(k * frameBytes));
            }

            (rgb, frames, interval) = (next, kept, WireInterval(periodMs / kept));
        }

        var first = loop.Rgb[..frameBytes].ToArray();
        var still = encoder.Encode(first);
        if (still.Length > maxBytes)
        {
            throw new InvalidDataException($"Even one frame compresses to {still.Length} bytes (> {maxBytes}).");
        }

        return new LoopPayload(new RgbAnimation(still.Length, 1, loop.LedCount, loop.IntervalMs), still, first.Length, MaxDecimations + 1);
    }

    /// <summary>
    /// "Save current lighting to fans" (D2 §5): RF 21 broadcast once per sender that has completed an upload. Refused unless
    /// WireFormatVerified, at least one upload completed, and the last save was ≥ 60 s ago. Logged at Warning. Never called
    /// automatically. Returns the number of senders saved.
    /// </summary>
    public async Task<int> SaveConfigAsync(string requestedBy, CancellationToken ct)
    {
        if (!_options.WireFormatVerified)
        {
            throw new InvalidOperationException("Lian Li wire format is not verified; SaveConfig is disabled.");
        }

        RfSender[] senders;
        var now = _now?.Invoke() ?? DateTime.Now;
        lock (_gate)
        {
            senders = _senders.Values.Where(x => x.CompletedUploads > 0).ToArray();
            if (senders.Length == 0)
            {
                throw new InvalidOperationException("SaveConfig needs a completed loop upload first.");
            }

            if (_lastSave is { } last && now - last < SaveConfigMinInterval)
            {
                throw new InvalidOperationException($"SaveConfig is limited to one per {SaveConfigMinInterval.TotalSeconds:0} s.");
            }

            _lastSave = now;
        }

        LogSaveConfig(requestedBy, senders.Length);
        foreach (var r in senders)
        {
            await r.UploadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                r.SaveConfig();
            }
            finally
            {
                r.UploadGate.Release();
            }
        }

        return senders.Length;
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) =>
        throw new NotSupportedException("Lian Li devices are driven with uploaded loops (RF 32) only.");

    /// <summary>Fan RPM is read-only telemetry; its reply format is pending captures, so none is reported yet.</summary>
    public Telemetry? ReadTelemetry(ControllerId id) => null;

    /// <summary>Sync switches 36/38/39 (V150 MB sync, V150 light sync, TL v3 LED sync). Nothing else is accepted.</summary>
    public Task SetSyncSwitchAsync(ControllerId id, RfCmd sw, bool on, CancellationToken ct)
    {
        if (sw is not (RfCmd.V150MBSyncSwitch or RfCmd.V150LightSyncSwitch or RfCmd.TLv3LedLightSyncSwitch))
        {
            throw new ArgumentOutOfRangeException(nameof(sw), sw, "Not a sync switch.");
        }

        var s = Get(id);
        s.Sender.Control(s.Device, sw, s.NextCmdSeq(), on ? (byte)1 : (byte)0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// RF 16 is bind AND fan PWM in the IL (<c>SyncControlInfo</c>: [16] slave index, [17..20] PWM per fan). A bind built
    /// without a verified PWM ownership/validation path would overwrite fan speeds. The new read-only generation feedback
    /// does not authorize fan control or pairing; binding remains disabled (SELF_AUDIT M5c).
    /// </summary>
    public Task BindAsync(UsbDeviceInfo sender, RfMac device, BindConfirmation confirmation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!_options.WireFormatVerified)
        {
            throw new InvalidOperationException("Lian Li wire format is not verified; binding is disabled.");
        }

        LogBind(device.ToString(), confirmation.ConfirmedBy);
        throw new NotSupportedException("RF bind also sets fan PWM; it needs the receiver device list, which v1 does not read.");
    }

    public Task CloseAsync(ControllerId id)
    {
        lock (_feedbackGate) { return CloseSession(id); }
    }

    private Task CloseSession(ControllerId id)
    {
        Session? s;
        lock (_gate)
        {
            _sessions.Remove(id, out s);
            if (_clocks.TryGetValue(id, out var clock))
            {
                clock.Bind(null);
            }
        }

        if (s is not null)
        {
            ReleaseSender(s.Device.Sender);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hotplug is not null)
        {
            _hotplug.Changed -= OnInterfaceChanged;
        }

        ControllerId[] open;
        lock (_gate)
        {
            open = [.. _sessions.Keys];
        }

        foreach (var id in open)
        {
            await CloseAsync(id).ConfigureAwait(false);
        }

        if (_ownsEncoder)
        {
            (_encoder as IDisposable)?.Dispose();
            _encoder = null;
        }
    }

    /// <summary>Runs keep-alive on every open sender; exposed for tests (the timer calls it every 250 ms).</summary>
    public void KeepAlive()
    {
        RfSender[] senders;
        lock (_gate)
        {
            senders = [.. _senders.Values];
        }

        foreach (var r in senders)
        {
            try
            {
                r.Tick();
            }
#pragma warning disable CA1031 // a failed heartbeat is logged; the write path reports device errors
            catch (Exception ex) when (ex is not ForbiddenOpcodeException)
#pragma warning restore CA1031
            {
                LogKeepAliveFailed(r.Info.Path, ex);
            }
        }

        ObserveReceiverFeedback();
    }

    /// <summary>One bounded receiver observation outside uploads; no query when disabled or sender topology is ambiguous.</summary>
    public void ObserveReceiverFeedback()
    {
        if (!_options.ReceiverFeedbackEnabled || _receiverProbe is null || !Monitor.TryEnter(_feedbackGate)) { return; }
        try
        {
            if (_feedbackElapsed() - _lastReceiverQuery < TimeSpan.FromSeconds(1)) { return; }
            Session[] sessions;
            lock (_gate) { sessions = _sessions.Values.Where(s => s.Delivery is not null).ToArray(); }
            var senders = sessions.Select(s => s.Sender).Distinct().ToArray();
            if (senders.Length != 1 || !senders[0].UploadGate.Wait(0)) { return; }
            var sender = senders[0];
            try
            {
                _lastReceiverQuery = _feedbackElapsed();
                var reports = RfReceiverReport.ParseComplete(_receiverProbe());
                var now = new DateTimeOffset(_now?.Invoke() ?? DateTime.Now); // actual receipt time, not query start
                var observedElapsed = _feedbackElapsed();
                var retriedThisPass = false; // let master clock queries and owner uploads run between receiver recoveries
                foreach (var session in sessions)
                {
                    PendingDelivery? pending;
                    bool retry;
                    lock (_gate)
                    {
                        pending = session.Delivery;
                        if (pending is null || !IsCurrent(session, pending)) { continue; }
                        var report = reports.SingleOrDefault(r => r.Device.Mac == session.Device.Mac);
                        if (report is not null && (!session.Device.Assembler.Spec.RecTypes.Contains(report.Device.RecType)
                            || (session.Device.Assembler.Family == "TL" && report.Device.FanCount != session.Device.Assembler.UnitsIn(session.Device.LedCount))))
                        { report = null; }
                        retry = pending.Tracker.Observe(report, now, _options.ReceiverRetriesEnabled && session.RecoveryAllowed, observedElapsed);
                        PublishDelivery(session, pending);
                    }
                    if (!retry || retriedThisPass) { continue; }
                    retriedThisPass = true;
                    var committed = false;
                    try
                    {
                        sender.Upload(session.Device, pending.Animation, pending.Compressed, pending.EffectIndex, () =>
                        {
                            lock (_gate)
                            {
                                var commitElapsed = _feedbackElapsed();
                                if (!IsCurrent(session, pending) || !session.RecoveryAllowed || !pending.Tracker.CanCommitRetry(commitElapsed))
                                { throw new OperationCanceledException("Superseded, expired or unowned RF recovery."); }
                                pending.Tracker.Retried(commitElapsed); // reserve BEFORE sending, for ALL completion/error paths
                                committed = true;
                                PublishDelivery(session, pending);
                            }
                        }); // already holds UploadGate; committed retry is non-cancellable, exactly like the original upload
                        LogReceiverRetry(session.Device.Mac.ToString(), pending.EffectIndex);
                    }
                    catch (OperationCanceledException) when (!committed) { /* pre-commit supersession consumes no attempt */ }
#pragma warning disable CA1031 // every committed attempt was already charged; non-I/O validation/offline failures block recovery
                    catch (Exception ex) when (ex is not ForbiddenOpcodeException)
#pragma warning restore CA1031
                    {
                        lock (_gate)
                        {
                            if (IsCurrent(session, pending)) { pending.Tracker.Failed(ex.Message, ex is not IOException); PublishDelivery(session, pending); }
                        }
                    }
                }
            }
            finally { sender.UploadGate.Release(); }
        }
#pragma warning disable CA1031 // observation failure is unknown, never success or permission to retry
        catch (Exception ex) when (ex is not ForbiddenOpcodeException)
#pragma warning restore CA1031
        {
            lock (_gate)
            {
                foreach (var s in _sessions.Values)
                {
                    if (s.Delivery is not { } p) { continue; }
                    p.Tracker.Unknown(ex.Message);
                    PublishDelivery(s, p);
                }
            }
        }
        finally { Monitor.Exit(_feedbackGate); }
    }

    public void SetRecoveryAllowed(ControllerId id, bool allowed)
    {
        // The same gate encloses a committed retry, so revoke returns only after it has finished.
        lock (_feedbackGate)
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(id, out var session)) { session.RecoveryAllowed = allowed; }
            }
        }
    }

    // Caller holds _gate. Session identity AND private generation identity, never token numeric ordering alone.
    private bool IsCurrent(Session session, PendingDelivery pending) =>
        _sessions.TryGetValue(IdOf(session.Device), out var current) && ReferenceEquals(current, session)
        && ReferenceEquals(session.Generation, pending.Generation) && ReferenceEquals(session.Delivery, pending);

    private void PublishDelivery(Session session, PendingDelivery pending)
    {
        var id = IdOf(session.Device);
        if (_lastUploads.TryGetValue(id, out var stats) && IsCurrent(session, pending))
        { _lastUploads[id] = stats with { ReceiverDelivery = pending.Tracker.Status }; }
    }

    /// <summary>The appsettings default (UnifiedRgb:LianLi:Devices[].Mounting) for a discovered device.</summary>
    private LianLiMounting ConfiguredMounting(RfDevice d)
    {
        foreach (var c in _options.Devices)
        {
            try
            {
                if (RfMac.Parse(c.Mac) == d.Mac)
                {
                    return c.Mounting?.Clone() ?? new LianLiMounting();
                }
            }
            catch (FormatException)
            {
                // a malformed entry was skipped by discovery
            }
        }

        return new LianLiMounting();
    }

    /// <summary>The header's interval resolution: integer ms + hundredths (<c>LianLiWire</c> rounds <c>I × 100</c>).</summary>
    private static double WireInterval(double ms) => Math.Round(ms * 100.0) / 100.0;

    private static bool SameMounting(LianLiMounting a, LianLiMounting b) =>
        string.Equals(MountingMap.Describe(a), MountingMap.Describe(b), StringComparison.Ordinal);

    private static string ZoneName(RfDevice d)
    {
        var fans = d.Assembler.UnitsIn(d.LedCount);
        return (d.Assembler.Spec.Shape == LayoutKind.Ring || d.Assembler.ReordersWire) && fans > 1 ? $"Chain ({fans} fans)" : d.Assembler.Family;
    }

    /// <summary>Loads the codec once (self-checked by its factory); missing → Blocked.</summary>
    private void EnsureEncoder()
    {
        lock (_gate)
        {
            if (_encoder is not null)
            {
                return;
            }

            if (_encoderFactory is null)
            {
                throw new DeviceBlockedException("yuz.dll missing (no codec configured)");
            }

            _encoder = _encoderFactory();
            _ownsEncoder = true;
            LogCodec(_encoder.CodecHash);
        }
    }

    private Session Get(ControllerId id)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var s) ? s : throw new InvalidOperationException($"{id} is not open");
        }
    }

    /// <summary>The controller's clock handle, created once (caller holds the lock).</summary>
    private SessionClock ClockHandle(ControllerId id)
    {
        if (!_clocks.TryGetValue(id, out var c))
        {
            c = new SessionClock();
            _clocks[id] = c;
        }

        return c;
    }

    private RfSender AcquireSender(UsbDeviceInfo info)
    {
        lock (_senderLifecycleGate)
        {
        lock (_gate)
        {
            if (!_senders.TryGetValue(info.Path, out var r))
            {
                r = new RfSender(info, _open(info), _senderState, _options.MasterChannel, _now, _sleep, _timestamp);
                MasterInfo master;
                try
                {
                    master = r.ReadMaster();
                }
                catch
                {
                    r.Dispose();
                    throw;
                }

                LogSender(info.Path, master.Mac.ToString());
                _senders[info.Path] = r;
                if (_startKeepAliveTimer) { _keepAlive ??= new Timer(_ => KeepAlive(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250)); }
            }

            r.RefCount++;
            return r;
        }
        }
    }

    private void ReleaseSender(UsbDeviceInfo info)
    {
        lock (_senderLifecycleGate)
        {
        Timer? stop = null;
        RfSender? retired = null;
        lock (_gate)
        {
            if (_senders.TryGetValue(info.Path, out var r) && --r.RefCount <= 0)
            {
                _senders.Remove(info.Path);
                retired = r;
                if (_senders.Count == 0)
                {
                    stop = _keepAlive;
                    _keepAlive = null;
                }
            }
        }

        stop?.Dispose();
        // Never wait for an upload/read under the adapter gate: committed upload callbacks need that gate.
        // The lifecycle gate prevents reopening the same physical sender before its previous handle has closed.
        retired?.Dispose();
        }
    }

    private void OnInterfaceChanged(DeviceInterfaceChange change, string path)
    {
        RfDevice[] affected;
        lock (_gate)
        {
            affected = _found.Values.Where(d => string.Equals(d.Sender.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (affected.Length > 0)
            {
                _senderState.IsOnline = change == DeviceInterfaceChange.Arrived; // receiver/hub/LCD changes do not affect the sender
            }
        }

        foreach (var d in affected)
        {
            Hotplug?.Invoke(new HotplugEvent(IdOf(d), change == DeviceInterfaceChange.Arrived ? HotplugKind.Arrived : HotplugKind.Removed));
        }
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Lian Li RF sender {Path} opened (master MAC {Mac})")]
    private partial void LogSender(string path, string mac);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Lian Li keep-alive failed on {Path}")]
    private partial void LogKeepAliveFailed(string path, Exception ex);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "RF bind of {Mac} requested by {User} (refused: bind also sets fan PWM)")]
    private partial void LogBind(string mac, string user);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Lian Li codec selected (identity {Hash}); managed payloads are verified before upload")]
    private partial void LogCodec(string hash);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "Lian Li loop for {Id} ({Frames} frames) stays over the upload limit after halving; uploading frame 0 as a static loop ({Bytes} bytes)")]
    private partial void LogStaticFallback(string id, int frames, int bytes);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning, Message = "Lian Li mounting for {Id} rejected ({Error}); using identity")]
    private partial void LogMountingRejected(string id, string error);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Warning, Message = "RF 21 SaveConfig requested by {User}: writing the current lighting to flash on {Senders} sender(s)")]
    private partial void LogSaveConfig(string user, int senders);

    [LoggerMessage(EventId = 5008, Level = LogLevel.Warning, Message = "RF receiver {Mac} still reported an old generation: resent the SAME generation {EffectIndex} and payload (bounded recovery)")]
    private partial void LogReceiverRetry(string mac, uint effectIndex);

    private sealed record PendingDelivery(object Generation, uint EffectIndex, RgbAnimation Animation, byte[] Compressed, RfDeliveryTracker Tracker);

    /// <summary>A controller's view of its current session's sender clock: two volatile reads, no lock, no allocation.</summary>
    private sealed class SessionClock : IDeviceClock
    {
        private RfSender? _sender;

        public DeviceClockSample? Latest => Volatile.Read(ref _sender)?.Clock.Latest;

        public void Bind(RfSender? sender) => Volatile.Write(ref _sender, sender);
    }

    private sealed class Session
    {
        private byte _cmdSeq;

        public Session(RfDevice device, RfSender sender)
        {
            Device = device;
            Sender = sender;
        }

        public RfDevice Device { get; }

        public RfSender Sender { get; }

        public object? Generation { get; set; }

        public PendingDelivery? Delivery { get; set; }

        public bool RecoveryAllowed { get; set; }

        /// <summary><c>targe_cmd_seq</c>: incremented per command, 1..254.</summary>
        public byte NextCmdSeq() => _cmdSeq = (byte)(_cmdSeq >= 254 ? 1 : _cmdSeq + 1);
    }
}

/// <summary>A loop ready for the wire: header fields, compressed bytes, raw size and halvings applied (&gt; MaxDecimations = static).</summary>
public sealed record LoopPayload(RgbAnimation Animation, byte[] Compressed, int RawBytes, int Decimations)
{
    /// <summary>Wire period (frames × 0.01 ms-rounded interval) minus the input loop's period, after halving; 0 otherwise.</summary>
    public double PeriodErrorMs { get; init; }
}

/// <summary>Proof that a human confirmed an RF pairing in the GUI.</summary>
public sealed record BindConfirmation(string ConfirmedBy, DateTimeOffset At);
