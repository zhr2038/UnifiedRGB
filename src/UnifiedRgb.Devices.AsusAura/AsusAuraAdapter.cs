using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.AsusAura;

/// <summary>
/// ASUS ROG STRIX B850-F onboard lighting over native Aura HID (NATIVE design §4; no OpenRGB runtime, no ASUS software).
/// Only the MI_02 collection with usage page 0xFF72 / usage 0x00A1 and 65-byte reports is opened, never MI_00. Every report
/// passes AuraReportFirewall → FrameValidator{65} → FrameBudget. The 0x82 + 0xB0 queries give the firmware string and the
/// config table; the effect-channel (0x35) and direct-channel (0x40) namespaces are derived separately from it. Until
/// <see cref="AsusAuraOptions.ControlVerified"/> no writer opens the board; a leased bring-up session is the only way in, and
/// its open and probes never select direct mode (the first marker does). Without a native map the onboard LEDs are one
/// aggregate Point zone replicated to all <c>config[0x1B]</c> LEDs (design §2). No release command is in evidence: release,
/// Close and Host stop send nothing (design §4.5); the last frame remains.
/// </summary>
public sealed partial class AsusAuraAdapter : IWholeFrameAdapter, INativeBringup, IFrameBudgetSource, IFramePacingSource, IAsyncDisposable
{
    public const string ProbeHidCaps = "hid-caps";
    public const string ProbeAuraId = "aura-id";

    /// <summary>Win32 ERROR_TIMEOUT, the row status of a read that delivered nothing within HidTimeoutMs.</summary>
    public const int StatusTimeout = 1460;

    private static readonly string[] ProbeNames = [ProbeHidCaps, ProbeAuraId];

    private readonly IHidReportDevices _devices;
    private readonly AsusAuraOptions _options;
    private readonly ILogger _logger;
    private readonly INativeMapSource? _maps;
    private readonly Func<long>? _budgetClock;
    private readonly long _budgetTicksPerSecond;
    private readonly int _ceilingFps;
    private readonly int[] _headerLeds;
    private readonly object _gate = new();
    private readonly Dictionary<ControllerId, Board> _boards = [];
    private bool _disposed;

    /// <param name="budgetClock">FrameBudget tick source (tests); default the Stopwatch.</param>
    public AsusAuraAdapter(
        IHidReportDevices devices,
        AsusAuraOptions options,
        ILogger? logger = null,
        INativeMapSource? maps = null,
        Func<long>? budgetClock = null,
        long budgetTicksPerSecond = 0)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.HidTimeoutMs, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.FrameBudgetMs, nameof(options));
        _headerLeds = (options.Headers ?? []).Select(h => h?.LedCount ?? 0).ToArray();
        if (_headerLeds.Any(n => n < 0 || n > AuraReports.MaxDirectLeds))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"AsusAura:Headers LedCount must be 0..{AuraReports.MaxDirectLeds}.");
        }

        _logger = logger ?? NullLogger.Instance;
        _maps = maps;
        _budgetClock = budgetClock;
        _budgetTicksPerSecond = budgetTicksPerSecond;
        _ceilingFps = options.FrameBudgetMs > 0 ? Safety.FrameBudget.CeilingFromBudgetMs(options.FrameBudgetMs) : AsusAuraDefaults.CeilingFps;
    }

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public string Vendor => "ASUS";

    public DeviceClass Class => DeviceClass.AsusAura;

    public IReadOnlyList<string> Probes => ProbeNames;

    /// <summary>
    /// The chain of design §4.2: AuraReportFirewall → FrameValidator{65,65} → FrameBudget gate (outermost); the firewall admits
    /// only the two queries until it is configured with the channel table.
    /// </summary>
    public static ITransport BuildChain(ITransport inner, FrameBudget budget, out AuraReportFirewall firewall, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        firewall = new AuraReportFirewall(new PacketTap(inner, "AsusAura", f => f.Length > 1 ? f.Span[1] : -1), logger);
        var rules = new FrameValidationRules
        {
            MinLength = AuraProtocol.ReportLength,
            MaxLength = AuraProtocol.ReportLength,
            AllowedLengths = new HashSet<int> { AuraProtocol.ReportLength },
        };
        return budget.Gate(new FrameValidator(firewall, rules, new DeviceState()));
    }

    /// <summary>Passive: enumerates the HID collections and reads their caps; no report is sent or read.</summary>
    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var rule = AsusAuraDefaults.Rule;
        var selected = rule.Select(_devices.Enumerate(rule.Vid, rule.Pid), out var rejected);
        foreach (var why in rejected)
        {
            LogCollectionSkipped(why);
        }

        var result = new List<ControllerDescriptor>();
        lock (_gate)
        {
            for (var k = 0; k < selected.Count; k++)
            {
                var c = selected[k];
                var id = new ControllerId(string.Create(CultureInfo.InvariantCulture, $"asusaura:{rule.Pid:X4}:{k}"));
                var (zones, bindings) = ZonesFor(id);
                var descriptor = new ControllerDescriptor(id, "ASUS", $"ASUS Aura mainboard ({rule.Vid:X4}:{rule.Pid:X4})", DeviceType.Motherboard,
                    DeviceClass.AsusAura, UsbDeviceInfo.FromPath(c.Path)?.InstanceSuffix ?? string.Empty, zones, Capabilities.DirectOnly(_ceilingFps));
                if (_boards.TryGetValue(id, out var known))
                {
                    lock (known.Sync)
                    {
                        known.Descriptor = descriptor;
                        known.Bindings = bindings;
                    }
                }
                else
                {
                    _boards[id] = new Board(c, descriptor, bindings, NewBudget());
                }

                result.Add(descriptor);
            }
        }

        return Task.FromResult<IReadOnlyList<ControllerDescriptor>>(result);
    }

    /// <summary>
    /// The writer path; only with <see cref="AsusAuraOptions.ControlVerified"/>. Reads the identity (0x82 + 0xB0) when this
    /// process has not, checks every zone against the derived channels, then selects direct mode (0x35) on each used effect
    /// channel not yet selected since the last close.
    /// </summary>
    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        var b = Get(id);
        if (!_options.ControlVerified)
        {
            throw new DeviceBlockedException(UnverifiedReason(id));
        }

        lock (b.Sync)
        {
            if (b.BlackFailed)
            {
                throw new DeviceBlockedException("a previous black send failed; nothing more is sent in this process");
            }

            EnsureChain(b);
            if (b.Topology is null)
            {
                var r = ReadIdentity(b);
                if (!r.Ok)
                {
                    throw new DeviceBlockedException($"Aura identity read failed: {r.Summary}");
                }
            }

            var channels = UsedChannels(b, out var why) ?? throw new DeviceBlockedException(why);
            foreach (var ch in channels)
            {
                if (b.DirectSelected.Contains(ch.EffectChannel))
                {
                    continue;
                }

                var row = Control(b, $"direct mode 0x35 effect channel {ch.EffectChannel}", [AuraReports.DirectMode(ch.EffectChannel)], black: false)[0];
                if (row.Status != 0)
                {
                    throw new DeviceBlockedException(string.Create(CultureInfo.InvariantCulture, $"Aura direct mode select on effect channel {ch.EffectChannel} failed (status {row.Status}{(row.Note is null ? string.Empty : ": " + row.Note)})"));
                }

                b.DirectSelected.Add(ch.EffectChannel);
            }

            b.OpenForWriter = true;
        }

        return Task.CompletedTask;
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        var b = Get(id);
        lock (b.Sync)
        {
            if (b.Descriptor.Zones.Count != 1 || zone.Value != b.Descriptor.Zones[0].Id.Value)
            {
                throw new ArgumentException("This board takes whole frames (IWholeFrameAdapter).", nameof(zone));
            }
        }

        _ = WriteFrame(id, [colors.ToArray()]);
    }

    /// <summary>
    /// One complete frame: every used direct channel in full (unmapped LEDs black), ⌈N/20⌉ reports per channel, all inside one
    /// FrameBudget frame. An unchanged frame sends nothing.
    /// </summary>
    public bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var b = Get(id);
        lock (b.Sync)
        {
            if (!b.OpenForWriter || b.Topology is null)
            {
                throw new InvalidOperationException($"{id} is not open for frames");
            }

            if (b.BlackFailed)
            {
                return false; // after a failed hold-black black send nothing more is sent
            }

            var reports = new List<byte[]>();
            foreach (var (ch, colors) in ChannelFrames(b, zones))
            {
                reports.AddRange(AuraReports.Direct(ch.DirectChannel, colors));
            }

            var flat = reports.SelectMany(r => r).ToArray();
            if (b.LastFrame is { } last && last.AsSpan().SequenceEqual(flat))
            {
                return false; // cached frame: no native write, sample or budget refusal
            }

            using var scope = b.Budget.TryBeginFrame();
            if (scope is null)
            {
                return false;
            }

            foreach (var r in reports)
            {
                Send(b.Chain!, r);
            }

            scope.Complete();
            b.LastFrame = flat;
            return true;
        }
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) =>
        throw new NotSupportedException("Only direct mode is admitted on the native Aura path.");

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    /// <summary>Writer close: nothing is sent (no release command in evidence); the direct-mode state is not assumed afterwards.</summary>
    public Task CloseAsync(ControllerId id)
    {
        var b = Get(id);
        lock (b.Sync)
        {
            b.OpenForWriter = false;
            b.LastFrame = null;
            b.DirectSelected.Clear();
        }

        return Task.CompletedTask;
    }

    public FrameBudgetStats? FrameBudget(ControllerId id)
    {
        lock (_gate)
        {
            return _boards.TryGetValue(id, out var b) ? b.Budget.Stats() : null;
        }
    }

    /// <summary>The board's own FrameBudget deadline (the budget <see cref="WriteFrame(ControllerId, IReadOnlyList{Rgb[]})"/> admits by).</summary>
    public TimeSpan DelayUntilNextFrame(ControllerId id)
    {
        lock (_gate)
        {
            return _boards.TryGetValue(id, out var b) ? b.Budget.DelayUntilNextFrame() : TimeSpan.Zero;
        }
    }

    public bool IsControlVerified(ControllerId id) => _options.ControlVerified;

    public string UnverifiedReason(ControllerId id) => "AsusAura control not verified";

    /// <summary>Bring-up open: marks the session; sends nothing and selects no mode (the first marker does).</summary>
    public Task OpenForBringupAsync(ControllerId id, CancellationToken ct)
    {
        var b = Get(id);
        lock (b.Sync)
        {
            b.OpenForBringup = true;
        }

        return Task.CompletedTask;
    }

    public Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct)
    {
        if (probe is not (ProbeHidCaps or ProbeAuraId))
        {
            throw new ArgumentException($"Unknown Aura probe '{probe}' (known: {string.Join(", ", ProbeNames)}).", nameof(probe));
        }

        var b = InSession(id);
        lock (b.Sync)
        {
            if (probe == ProbeHidCaps)
            {
                return Task.FromResult(HidCaps(b));
            }

            try
            {
                EnsureChain(b);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                return Task.FromResult(Result(ProbeAuraId, false, $"HID open failed: {ex.Message}", [],
                    new Dictionary<string, string> { ["comm"] = string.Create(CultureInfo.InvariantCulture, $"error({StatusOf(ex)})") }));
            }

            return Task.FromResult(ReadIdentity(b));
        }
    }

    /// <summary>
    /// A bounded marker frame on one channel (0 = onboard, i + 1 = header i): the whole channel, <c>Start..Start+Count-1</c>
    /// at the marker colour, the rest black. Needs the identity (probe aura-id) of this process; selects direct mode on that
    /// effect channel first if not yet selected. Under hold-black only black markers are accepted, refused before any packet.
    /// </summary>
    public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Channel < 0 || marker.Start < 0 || marker.Count < 1)
        {
            throw new ArgumentException($"Aura marker needs channel ≥ 0, start ≥ 0 and count ≥ 1 (got {marker.Channel}/{marker.Start}/{marker.Count}).", nameof(marker));
        }

        if (_options.HoldBlackOnRelease && marker.Color != Rgb.Black)
        {
            throw new ArgumentException("Hold-black is active: only black (#000000) markers are accepted.", nameof(marker));
        }

        var b = InSession(id);
        lock (b.Sync)
        {
            if (b.Topology is null)
            {
                return Task.FromResult(Refused("marker", "config table not read in this process: run probe aura-id first"));
            }

            var ch = b.Topology.ByMarker(marker.Channel)
                ?? throw new ArgumentException($"Aura marker channel {marker.Channel} is not a derived channel ({b.Topology.DirectChannelsText}).", nameof(marker));

            // overflow-safe: Start + Count can wrap
            if (marker.Start >= ch.Leds || marker.Count > ch.Leds - marker.Start)
            {
                throw new ArgumentException($"Aura marker must lie within {ch.Name} LEDs 0..{ch.Leds - 1} (got start {marker.Start}, count {marker.Count}).", nameof(marker));
            }

            if (b.BlackFailed)
            {
                return Task.FromResult(Refused("marker", "a previous black send failed; nothing more is sent in this process"));
            }

            var rows = new List<NativeProbeRow>();
            if (!b.DirectSelected.Contains(ch.EffectChannel))
            {
                rows.AddRange(Control(b, $"direct mode 0x35 effect channel {ch.EffectChannel}", [AuraReports.DirectMode(ch.EffectChannel)], black: false));
                if (rows[^1].Status != 0)
                {
                    return Task.FromResult(Result("marker", false, "direct mode select failed", rows, Comm(rows)));
                }

                b.DirectSelected.Add(ch.EffectChannel);
            }

            var colors = new Rgb[ch.Leds];
            colors.AsSpan(marker.Start, marker.Count).Fill(marker.Color);
            var reports = AuraReports.Direct(ch.DirectChannel, colors);
            rows.AddRange(Control(b, $"direct 0x40 channel 0x{ch.DirectChannel:X2}", reports, marker.Color == Rgb.Black));
            b.Marker = ch;
            var ok = rows.TrueForAll(r => r.Status == 0);
            var fields = Comm(rows);
            fields["channel"] = ch.Name;
            fields["effectChannel"] = ch.EffectChannel.ToString(CultureInfo.InvariantCulture);
            fields["directChannel"] = $"0x{ch.DirectChannel:X2}";
            fields["reports"] = reports.Count.ToString(CultureInfo.InvariantCulture);
            fields["marker"] = $"{marker.Start}+{marker.Count} #{marker.Color.R:X2}{marker.Color.G:X2}{marker.Color.B:X2}";
            return Task.FromResult(Result("marker", ok, ok ? "marker frame sent (transport accepted; playback unobserved)" : "marker frame failed", rows, fields));
        }
    }

    /// <summary>Black over the whole channel of the last marker.</summary>
    public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct)
    {
        var b = InSession(id);
        lock (b.Sync)
        {
            if (b.Marker is not { } ch)
            {
                return Task.FromResult(Result("end-marker", true, "no marker to clear", [], new Dictionary<string, string> { ["comm"] = "not-attempted" }));
            }

            if (b.BlackFailed)
            {
                return Task.FromResult(Refused("end-marker", "a previous black send failed; nothing more is sent in this process"));
            }

            var rows = Control(b, $"direct 0x40 channel 0x{ch.DirectChannel:X2} black", AuraReports.Direct(ch.DirectChannel, new Rgb[ch.Leds]), black: true);
            b.Marker = null;
            var ok = rows.TrueForAll(r => r.Status == 0);
            return Task.FromResult(Result("end-marker", ok, ok ? "black frame sent" : "black frame failed", rows, Comm(rows)));
        }
    }

    /// <summary>No release command is in evidence (design §4.5): nothing is sent; the last frame remains.</summary>
    public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct)
    {
        var b = Get(id);
        lock (b.Sync)
        {
            b.OpenForBringup = false;
            b.Marker = null;
            b.DirectSelected.Clear();
        }

        return Task.FromResult(Result("release", true, "no release command in evidence: nothing sent (the last frame remains)", [],
            new Dictionary<string, string> { ["comm"] = "not-attempted", ["release"] = "not-applicable" }));
    }

    /// <summary>Host stop: closes the HID handles; nothing is sent.</summary>
    public ValueTask DisposeAsync()
    {
        Board[] boards;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            boards = [.. _boards.Values];
        }

        foreach (var b in boards)
        {
            lock (b.Sync)
            {
                b.OpenForWriter = false;
                b.OpenForBringup = false;
                b.Transport?.Dispose();
                b.Transport = null;
                b.Chain = null;
            }
        }

        return ValueTask.CompletedTask;
    }

    private static NativeProbeResult Refused(string step, string why) =>
        new(step, false, why, []) { Fields = new Dictionary<string, string> { ["comm"] = "not-attempted" } };

    private static NativeProbeResult Result(string step, bool ok, string summary, IReadOnlyList<NativeProbeRow> rows, Dictionary<string, string> fields)
    {
        fields.TryAdd("readback", "none");
        return new NativeProbeResult(step, ok, summary, rows) { Fields = fields };
    }

    private static Dictionary<string, string> Comm(IEnumerable<NativeProbeRow> rows)
    {
        var failed = rows.FirstOrDefault(r => r.Status != 0);
        return new Dictionary<string, string>
        {
            ["comm"] = failed is null ? "ok" : string.Create(CultureInfo.InvariantCulture, $"error({failed.Status})"),
        };
    }

    private static void Send(ITransport chain, byte[] report)
    {
        var vt = chain.SendAsync(report, CancellationToken.None);
        if (!vt.IsCompletedSuccessfully)
        {
            vt.AsTask().GetAwaiter().GetResult();
        }
    }

    private static int Read(ITransport chain, byte[] buffer)
    {
        var vt = chain.ReadAsync(buffer, CancellationToken.None);
        return vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Row status of a failed send/read: Win32 error, ERROR_TIMEOUT, or -1 for a firewall/validator refusal.</summary>
    private static int StatusOf(Exception ex) => ex switch
    {
        TimeoutException => StatusTimeout,
        Win32Exception w => w.NativeErrorCode,
        IOException io => io.HResult & 0xFFFF,
        _ => -1,
    };

    private FrameBudget NewBudget() => new(_ceilingFps, _budgetClock, _budgetTicksPerSecond);

    /// <summary>
    /// Mapped zones from native-maps.json (validated against the onboard byte range and the configured header counts), else
    /// the aggregate onboard Point zone; each configured header the map does not cover is one Linear zone.
    /// </summary>
    private (IReadOnlyList<ZoneDescriptor> Zones, Binding[] Bindings) ZonesFor(ControllerId id)
    {
        var zones = new List<ZoneDescriptor>();
        var bindings = new List<Binding>();
        var map = _maps?.Get(id);
        var why = map is null ? "no map" : NativeMaps.Validate(map, byte.MaxValue, ChannelLimit);
        if (map is not null && why is null)
        {
            zones.AddRange(NativeMaps.Zones(map));
            bindings.AddRange(map.Zones.Select(z => new Binding(z.Channel, z.Indices.ToArray())));
        }
        else
        {
            if (map is not null)
            {
                LogMapRejected(id.Value, why!);
            }

            zones.Add(AggregateZone.Descriptor("Onboard"));
            bindings.Add(new Binding(0, null, Aggregate: true));
        }

        for (var i = 0; i < _headerLeds.Length; i++)
        {
            if (_headerLeds[i] > 0 && !bindings.Exists(x => x.Channel == i + 1))
            {
                zones.Add(new ZoneDescriptor(new ZoneId(zones.Count), string.Create(CultureInfo.InvariantCulture, $"ARGB header {i + 1}"), ZoneLayout.Linear(_headerLeds[i])));
                bindings.Add(new Binding(i + 1, null));
            }
        }

        return (zones, bindings.ToArray());
    }

    /// <summary>Records of a map channel: 0 onboard (byte range), 1..n configured headers, anything else (negative too) none.</summary>
    private int ChannelLimit(int channel) => channel == 0 ? byte.MaxValue : channel >= 1 && channel <= _headerLeds.Length ? _headerLeds[channel - 1] : 0;

    /// <summary>The derived channels the zones use (onboard first), or null with the reason a zone cannot be served.</summary>
    private static List<AuraChannel>? UsedChannels(Board b, out string why)
    {
        var used = new List<AuraChannel>();
        foreach (var z in b.Bindings)
        {
            var ch = b.Topology!.ByMarker(z.Channel);
            if (ch is null)
            {
                why = z.Channel == 0
                    ? "config table reports no onboard LEDs (config[0x1B] = 0)"
                    : $"header {z.Channel} is not a derived channel ({string.Join("; ", b.Topology.Notes)})";
                return null;
            }

            if (z.Indices is { } idx && idx.Any(i => i >= ch.Leds))
            {
                why = $"native map indices exceed {ch.Name} ({ch.Leds} LEDs per the config table)";
                return null;
            }

            if (!used.Contains(ch))
            {
                used.Add(ch);
            }
        }

        if (used.Count == 0)
        {
            why = "no zone to serve";
            return null;
        }

        used.Sort((x, y) => x.Marker.CompareTo(y.Marker));
        why = string.Empty;
        return used;
    }

    private static List<(AuraChannel Channel, Rgb[] Colors)> ChannelFrames(Board b, IReadOnlyList<Rgb[]> zones)
    {
        var channels = UsedChannels(b, out var why) ?? throw new InvalidOperationException(why);
        var frames = channels.Select(ch => (Channel: ch, Colors: new Rgb[ch.Leds])).ToList();
        for (var z = 0; z < b.Bindings.Length && z < zones.Count; z++)
        {
            var binding = b.Bindings[z];
            var colors = frames.First(f => f.Channel.Marker == binding.Channel).Colors;
            var src = zones[z];
            if (binding.Aggregate)
            {
                AggregateZone.Replicate(src, colors);
            }
            else if (binding.Indices is { } idx)
            {
                for (var k = 0; k < idx.Length && k < src.Length; k++)
                {
                    colors[idx[k]] = src[k];
                }
            }
            else
            {
                src.AsSpan(0, Math.Min(src.Length, colors.Length)).CopyTo(colors);
            }
        }

        return frames;
    }

    private Board Get(ControllerId id)
    {
        lock (_gate)
        {
            return _boards.TryGetValue(id, out var b) ? b : throw new ArgumentException($"Unknown Aura board {id}", nameof(id));
        }
    }

    private Board InSession(ControllerId id)
    {
        var b = Get(id);
        lock (b.Sync)
        {
            return b.OpenForBringup ? b : throw new InvalidOperationException($"{id}: probes and markers need a bring-up session.");
        }
    }

    private void EnsureChain(Board b)
    {
        if (b.Chain is null)
        {
            b.Transport = HidReportTransport.Open(_devices, b.Collection, AsusAuraDefaults.Rule, _options.HidTimeoutMs);
            b.Chain = BuildChain(b.Transport, b.Budget, out var fw, _logger);
            b.Firewall = fw;
            if (b.Topology is not null)
            {
                fw.Configure(b.Topology.FirewallTable());
            }
        }
    }

    /// <summary>E1: the caps of every 19AF collection and whether this board's collection is the selected one. No handle for reports is opened.</summary>
    private NativeProbeResult HidCaps(Board b)
    {
        var sw = Stopwatch.StartNew();
        var rule = AsusAuraDefaults.Rule;
        var all = _devices.Enumerate(rule.Vid, rule.Pid);
        var rows = new List<NativeProbeRow>();
        var fields = new Dictionary<string, string>
        {
            ["collections"] = all.Count.ToString(CultureInfo.InvariantCulture),
            ["comm"] = "ok",
            ["readback"] = "none",
        };
        for (var i = 0; i < all.Count; i++)
        {
            var c = all[i];
            var why = rule.Reject(c);
            fields[string.Create(CultureInfo.InvariantCulture, $"c{i}")] = $"{c}{(why is null ? " [selected]" : $" [{why}]")}";
            rows.Add(new NativeProbeRow($"caps {c.Path}", string.Empty, c.Error is null ? 0 : -1, null, sw.Elapsed.TotalMilliseconds, c.ToString()));
        }

        var ours = all.FirstOrDefault(c => string.Equals(c.Path, b.Collection.Path, StringComparison.OrdinalIgnoreCase));
        var reason = ours is null ? "this board's collection is no longer present" : rule.Reject(ours);
        fields["selected"] = reason is null ? ours!.ToString() : $"none ({reason})";
        return new NativeProbeResult(ProbeHidCaps, reason is null, reason is null ? $"selected {ours}" : $"collection not usable: {reason}", rows) { Fields = fields };
    }

    /// <summary>E2: 0x82 then 0xB0 (control sequence), each reply checked for returned size, report id and reply id.</summary>
    private NativeProbeResult ReadIdentity(Board b)
    {
        var rows = new List<NativeProbeRow>();
        var fields = new Dictionary<string, string> { ["readback"] = "none" };
        NativeProbeResult Invalid(NativeProbeRow row, string what)
        {
            fields["comm"] = row.Status == 0 ? "ok" : string.Create(CultureInfo.InvariantCulture, $"error({row.Status})");
            fields["invalid"] = what;
            return new NativeProbeResult(ProbeAuraId, false, $"identity query invalid: {what}", rows) { Fields = fields };
        }

        string firmware;
        byte[] table;
        using (b.Budget.BeginControl())
        {
            var (fwRow, fwReply, fwReturned) = Query(b, AuraProtocol.RequestFirmware);
            rows.Add(fwRow);
            fields["firmwareReturnedSize"] = fwReturned.ToString(CultureInfo.InvariantCulture);
            if (fwRow.Status != 0)
            {
                return Invalid(fwRow, "0x82 failed");
            }

            if (!AuraReports.TryDecodeFirmware(fwReturned, fwReply, out firmware, out var fwWhy))
            {
                return Invalid(fwRow, $"0x82 reply: {fwWhy}");
            }

            fields["firmware"] = firmware;
            var (cfgRow, cfgReply, cfgReturned) = Query(b, AuraProtocol.RequestConfig);
            rows.Add(cfgRow);
            fields["configReturnedSize"] = cfgReturned.ToString(CultureInfo.InvariantCulture);
            if (cfgRow.Status != 0)
            {
                return Invalid(cfgRow, "0xB0 failed");
            }

            if (!AuraReports.TryDecodeConfig(cfgReturned, cfgReply, out table, out var cfgWhy))
            {
                return Invalid(cfgRow, $"0xB0 reply: {cfgWhy}");
            }
        }

        var config = new AuraConfig(firmware, table);
        var topology = AuraTopology.Derive(config, _headerLeds);
        b.Config = config;
        b.Topology = topology;
        b.Firewall!.Configure(topology.FirewallTable());
        fields["comm"] = "ok";
        fields["config"] = config.TableHex;
        fields["onboardLeds"] = config.OnboardLeds.ToString(CultureInfo.InvariantCulture);
        fields["rgbHeaders"] = config.RgbHeaders.ToString(CultureInfo.InvariantCulture);
        fields["rgbHeadersRaw"] = config.RgbHeadersRaw.ToString(CultureInfo.InvariantCulture);
        fields["addressableHeaders"] = config.AddressableHeaders.ToString(CultureInfo.InvariantCulture);
        fields["effectChannels"] = config.EffectChannels.ToString(CultureInfo.InvariantCulture);
        fields["directChannels"] = topology.DirectChannelsText;
        if (topology.Notes.Count > 0)
        {
            fields["notes"] = string.Join("; ", topology.Notes);
        }

        return new NativeProbeResult(ProbeAuraId, true,
            string.Create(CultureInfo.InvariantCulture, $"firmware '{firmware}', onboard {config.OnboardLeds} LEDs, {config.AddressableHeaders} addressable header(s)"), rows) { Fields = fields };
    }

    /// <summary>One query and its reply; the buffer starts zeroed and the returned size is reported as delivered.</summary>
    private static (NativeProbeRow Row, byte[] Reply, int Returned) Query(Board b, byte command)
    {
        var request = AuraReports.Query(command);
        var reply = new byte[AuraProtocol.ReportLength];
        var returned = 0;
        var status = 0;
        string? note = null;
        var sw = Stopwatch.StartNew();
        try
        {
            Send(b.Chain!, request);
            returned = Read(b.Chain!, reply);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or TimeoutException or InvalidOperationException)
        {
            status = StatusOf(ex);
            note = ex.Message;
        }

        if (returned < 0 || returned > reply.Length)
        {
            note = string.Create(CultureInfo.InvariantCulture, $"implausible returned size {returned}");
            returned = -1;
        }

        var row = new NativeProbeRow($"query 0x{command:X2}", Convert.ToHexString(request), status,
            status == 0 && returned > 0 ? Convert.ToHexString(reply, 0, returned) : null, sw.Elapsed.TotalMilliseconds,
            note ?? string.Create(CultureInfo.InvariantCulture, $"returned size {returned}"));
        return (row, reply, returned);
    }

    /// <summary>
    /// A control sequence (exempt from the frame limiter, design §9): the reports in order, stopping at the first failure (no
    /// retry). A failed black send under hold-black latches <see cref="Board.BlackFailed"/>.
    /// </summary>
    private List<NativeProbeRow> Control(Board b, string step, IReadOnlyList<byte[]> reports, bool black)
    {
        var rows = new List<NativeProbeRow>();
        using (b.Budget.BeginControl())
        {
            for (var i = 0; i < reports.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                var status = 0;
                string? note = null;
                try
                {
                    Send(b.Chain!, reports[i]);
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or TimeoutException or InvalidOperationException)
                {
                    status = StatusOf(ex);
                    note = ex.Message;
                }

                var label = reports.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{step} report {i + 1}/{reports.Count}") : step;
                rows.Add(new NativeProbeRow(label, Convert.ToHexString(reports[i]), status, null, sw.Elapsed.TotalMilliseconds, note));
                if (status != 0)
                {
                    break;
                }
            }
        }

        if (black && _options.HoldBlackOnRelease && rows.Exists(r => r.Status != 0))
        {
            b.BlackFailed = true; // §9: no retry after a failed black send; the physical outcome is unknown
            LogBlackFailed(b.Descriptor.Id.Value, rows.First(r => r.Status != 0).Status);
        }

        return rows;
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Debug, Message = "Aura HID collection skipped: {Why}")]
    private partial void LogCollectionSkipped(string why);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning, Message = "Aura {Id}: native map rejected ({Why}); using the aggregate zone")]
    private partial void LogMapRejected(string id, string why);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Error, Message = "Aura {Id}: black send failed with status {Status}; hold-black outcome unknown, nothing more is sent")]
    private partial void LogBlackFailed(string id, int status);

    /// <summary>
    /// Zone z of the descriptor: <see cref="Channel"/> (0 onboard, i + 1 header i); <see cref="Aggregate"/> replicates the
    /// zone's one colour to every LED of the channel, <see cref="Indices"/> places a mapped zone, otherwise strip order.
    /// </summary>
    private sealed record Binding(int Channel, int[]? Indices, bool Aggregate = false);

    private sealed class Board(HidCollectionInfo collection, ControllerDescriptor descriptor, Binding[] bindings, FrameBudget budget)
    {
        public object Sync { get; } = new();

        public HidCollectionInfo Collection { get; } = collection;

        public ControllerDescriptor Descriptor { get; set; } = descriptor;

        public Binding[] Bindings { get; set; } = bindings;

        public FrameBudget Budget { get; } = budget;

        public HidReportTransport? Transport { get; set; }

        public ITransport? Chain { get; set; }

        public AuraReportFirewall? Firewall { get; set; }

        public AuraConfig? Config { get; set; }

        public AuraTopology? Topology { get; set; }

        public HashSet<byte> DirectSelected { get; } = [];

        public bool OpenForWriter { get; set; }

        public bool OpenForBringup { get; set; }

        public bool BlackFailed { get; set; }

        public AuraChannel? Marker { get; set; }

        public byte[]? LastFrame { get; set; }
    }
}
