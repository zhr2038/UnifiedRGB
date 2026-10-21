using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Nvapi;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.MsiGpu;

/// <summary>
/// MSI RTX 5090 over native NVAPI I2C, the recovered NV5000 indexed path (NATIVE design §3; never NDA.dll, MSI software or
/// OpenRGB). Session enter 0x06 ← 0, frames 0x04 ← <c>[0x01, (i, R, G, B, 0x01) × N]</c> with N = MaxIndices ≤ 34 (indices 0..33
/// only), release 0x06 ← 1 after ≥ 100 ms. Every write passes I2cRegisterFirewall("MsiNv5000") → FrameValidator{3, 3 + 5·N} →
/// FrameBudget; the 0xEC family read is admitted by the same policy. Indexed writes need the live family gate
/// (<c>ModuleType == 0x5000 || LEDType == 0x81</c>); until <see cref="MsiGpuOptions.ControlVerified"/> no writer opens the card,
/// and a leased bring-up session (<see cref="INativeBringup"/>) is the only way in. Without a native map the card is one
/// aggregate Point zone whose colour is replicated to every record (design §3.5).
/// Hold-black (overnight addendum §4, §9): with <see cref="MsiGpuOptions.HoldBlackOnRelease"/> every release of an entered card
/// sends one all-black frame instead of 0x06 ← 1 and keeps the handle; an optional keep-alive resends that fixed frame as a
/// control sequence; a never-entered card is sent nothing; after the first failed black send nothing more is sent.
/// </summary>
public sealed partial class MsiNv5000Adapter : IWholeFrameAdapter, INativeBringup, IFrameBudgetSource, IFramePacingSource, IAsyncDisposable
{
    public const string ProbeFwInfo = "fwinfo";
    public const string ProbeFwInfoLegacy = "fwinfo-legacy-profile";

    private static readonly string[] ProbeNames = [ProbeFwInfo, ProbeFwInfoLegacy];

    private readonly INvapi _nvapi;
    private readonly MsiGpuOptions _options;
    private readonly ILogger _logger;
    private readonly INativeMapSource? _maps;
    private readonly TimeProvider _time;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<long>? _budgetClock;
    private readonly long _budgetTicksPerSecond;
    private readonly int _ceilingFps;
    private readonly object _gate = new();
    private readonly Dictionary<ControllerId, Gpu> _gpus = [];
    private bool _disposed;

    /// <param name="time">Keep-alive timers (tests inject a manual provider); default <see cref="TimeProvider.System"/>.</param>
    /// <param name="sleep">The release delay; default <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    /// <param name="budgetClock">FrameBudget tick source (tests); default the Stopwatch.</param>
    public MsiNv5000Adapter(
        INvapi nvapi,
        MsiGpuOptions options,
        ILogger? logger = null,
        INativeMapSource? maps = null,
        TimeProvider? time = null,
        Action<TimeSpan>? sleep = null,
        Func<long>? budgetClock = null,
        long budgetTicksPerSecond = 0)
    {
        _nvapi = nvapi ?? throw new ArgumentNullException(nameof(nvapi));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxIndices, 1, nameof(options));
        if (options.MaxIndices > Nv5000.EvidencedIndices)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxIndices,
                $"MsiGpu:MaxIndices {options.MaxIndices} exceeds the evidenced NV5000 range (indices 0..{Nv5000.EvidencedIndices - 1}); no expansion.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReleaseDelayMs, Nv5000Defaults.ReleaseDelayMs, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.KeepAliveMs, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.FrameBudgetMs, nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _maps = maps;
        _time = time ?? TimeProvider.System;
        _sleep = sleep ?? Thread.Sleep;
        _budgetClock = budgetClock;
        _budgetTicksPerSecond = budgetTicksPerSecond;
        _ceilingFps = options.FrameBudgetMs > 0 ? Safety.FrameBudget.CeilingFromBudgetMs(options.FrameBudgetMs) : Nv5000Defaults.CeilingFps;
    }

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public string Vendor => "MSI";

    public DeviceClass Class => DeviceClass.MsiGpu;

    public IReadOnlyList<string> Probes => ProbeNames;

    /// <summary>
    /// The chain of design §3.3: I2cRegisterFirewall("MsiNv5000") → FrameValidator{3, 3 + 5·maxIndices} → FrameBudget gate
    /// (outermost). <paramref name="firewall"/> is the firewall, whose read check guards the 0xEC read.
    /// </summary>
    public static ITransport BuildChain(ITransport inner, FrameBudget budget, int maxIndices, out I2cRegisterFirewall firewall, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        firewall = new I2cRegisterFirewall(new PacketTap(inner, "MsiGpu", f => f.Length > 1 ? f.Span[1] : -1), I2cFirewallPolicy.MsiNv5000(maxIndices), logger);
        var rules = new FrameValidationRules { MinLength = 3, MaxLength = Nv5000.WireLength(maxIndices) };
        return budget.Gate(new FrameValidator(firewall, rules, new DeviceState()));
    }

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var devices = MsiGpuDiscovery.Discover(_nvapi);
        var result = new List<ControllerDescriptor>();
        lock (_gate)
        {
            foreach (var d in devices)
            {
                var id = d.Descriptor.Id;
                var (zones, indices) = ZonesFor(id);
                var descriptor = new ControllerDescriptor(id, "MSI", d.Descriptor.Name, DeviceType.Gpu, DeviceClass.MsiGpu, d.Descriptor.Serial,
                    zones, Capabilities.DirectOnly(_ceilingFps));
                if (_gpus.TryGetValue(id, out var known))
                {
                    lock (known.Sync)
                    {
                        known.Descriptor = descriptor;
                        known.ZoneIndices = indices;
                    }
                }
                else
                {
                    _gpus[id] = new Gpu(d.Handle, descriptor, indices, NewBudget());
                }

                result.Add(descriptor);
            }
        }

        return Task.FromResult<IReadOnlyList<ControllerDescriptor>>(result);
    }

    /// <summary>
    /// The writer path; only with <see cref="MsiGpuOptions.ControlVerified"/>. Establishes the family gate with one 0xEC read
    /// when this process has not, then enters host mode (0x06 ← 0).
    /// </summary>
    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        var g = Get(id);
        if (!_options.ControlVerified)
        {
            throw new DeviceBlockedException(UnverifiedReason(id));
        }

        lock (g.Sync)
        {
            if (g.BlackFailed)
            {
                throw new DeviceBlockedException("a previous black send failed; nothing more is sent in this process");
            }

            EnsureChain(g);
            if (g.Gate == FamilyGate.Unset)
            {
                _ = ReadFamily(g, NvI2cProfile.MsiNv5000, setGate: true, out _);
            }

            if (g.Gate != FamilyGate.Pass)
            {
                throw new DeviceBlockedException(g.GateReason ?? "GPU family gate not established (0xEC read failed)");
            }

            if (!g.Entered)
            {
                var row = Control(g, "enter 0x06<-0", Nv5000Frames.Session(Nv5000.SessionEnter), black: false);
                g.Entered = true; // attempted: the card may be in host mode even when the call failed
                if (row.Status != 0)
                {
                    throw new NvapiException(row.Status, "I2CWriteEx (enter)");
                }
            }

            g.OpenForWriter = true;
        }

        return Task.CompletedTask;
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        var g = Get(id);
        Rgb[][] zones;
        lock (g.Sync)
        {
            if (g.Descriptor.Zones.Count != 1 || zone.Value != g.Descriptor.Zones[0].Id.Value)
            {
                throw new ArgumentException("This GPU takes whole frames (IWholeFrameAdapter).", nameof(zone));
            }

            zones = [colors.ToArray()];
        }

        _ = WriteFrame(id, zones);
    }

    /// <summary>One complete frame: every record 0..MaxIndices-1 (unmapped indices black), one NVAPI call inside one FrameBudget frame.</summary>
    public bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var g = Get(id);
        lock (g.Sync)
        {
            if (g.Releasing) return false;
            if (!g.OpenForWriter || g.Gate != FamilyGate.Pass || !g.Entered)
            {
                throw new InvalidOperationException($"{id} is not open for frames");
            }

            if (g.Holding || g.BlackFailed)
            {
                return false; // hold-black owns the card until the next reviewed restart; after a failed black send nothing more
            }

            var frame = Nv5000Frames.Frame(Records(g, zones));
            var now = Stopwatch.GetTimestamp();
            if (g.LastFrame is { } last && last.AsSpan().SequenceEqual(frame)
                && (_options.KeepAliveMs == 0 || now - g.LastSent < _options.KeepAliveMs * (Stopwatch.Frequency / 1000)))
            {
                return false; // unchanged inside keep-alive window: no native write to count
            }

            using var scope = g.Budget.TryBeginFrame();
            if (scope is null)
            {
                return false;
            }

            Send(g.Chain!, frame);
            scope.Complete();
            g.LastFrame = frame;
            g.LastSent = now;
            return true;
        }
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) =>
        throw new NotSupportedException("No firmware mode is established for the NV5000 path.");

    public Telemetry? ReadTelemetry(ControllerId id)
    {
        Gpu? g;
        lock (_gate)
        {
            _gpus.TryGetValue(id, out g);
        }

        return g is null ? null : GpuTelemetry.Read(_nvapi, g.Handle);
    }

    /// <summary>Writer close: the release sequence (hold-black: one black frame, handle kept); a never-entered card gets nothing.</summary>
    public Task CloseAsync(ControllerId id)
    {
        var g = Get(id);
        _ = ReleaseCore(g);
        lock (g.Sync)
        {
            g.OpenForWriter = false;
        }

        return Task.CompletedTask;
    }

    public FrameBudgetStats? FrameBudget(ControllerId id)
    {
        lock (_gate)
        {
            return _gpus.TryGetValue(id, out var g) ? g.Budget.Stats() : null;
        }
    }

    /// <summary>The card's own FrameBudget deadline (the budget <see cref="WriteFrame(ControllerId, IReadOnlyList{Rgb[]})"/> admits by).</summary>
    public TimeSpan DelayUntilNextFrame(ControllerId id)
    {
        lock (_gate)
        {
            return _gpus.TryGetValue(id, out var g) ? g.Budget.DelayUntilNextFrame() : TimeSpan.Zero;
        }
    }

    public bool IsControlVerified(ControllerId id) => _options.ControlVerified;

    public string UnverifiedReason(ControllerId id)
    {
        Gpu? g;
        lock (_gate)
        {
            _gpus.TryGetValue(id, out g);
        }

        if (g is null)
        {
            return "MsiGpu control not verified";
        }

        lock (g.Sync)
        {
            if (g.Gate == FamilyGate.Fail && g.GateReason is { } reason)
            {
                return reason;
            }

            return g.Holding ? "MsiGpu control not verified; hold-black active" : "MsiGpu control not verified";
        }
    }

    /// <summary>Bring-up open: builds the chain; sends nothing (enter is the first marker's job).</summary>
    public Task OpenForBringupAsync(ControllerId id, CancellationToken ct)
    {
        var g = Get(id);
        lock (g.Sync)
        {
            EnsureChain(g);
            g.OpenForBringup = true;
        }

        return Task.CompletedTask;
    }

    public Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct)
    {
        var profile = probe switch
        {
            ProbeFwInfo => NvI2cProfile.MsiNv5000,
            ProbeFwInfoLegacy => NvI2cProfile.LegacyOpenRgb,
            _ => throw new ArgumentException($"Unknown GPU probe '{probe}' (known: {string.Join(", ", ProbeNames)}).", nameof(probe)),
        };
        var g = InSession(id);
        lock (g.Sync)
        {
            // only the production profile sets the gate; the legacy-profile read is diagnosis and never enables writes
            var r = ReadFamily(g, profile, setGate: probe == ProbeFwInfo, out var fields);
            return Task.FromResult(r with { Probe = probe, Fields = fields });
        }
    }

    /// <summary>
    /// A bounded marker frame: all MaxIndices records, <c>Start..Start+Count-1</c> at the marker colour, the rest black (no
    /// subset frame is assumed, RQ-G3). Enters host mode first if this process has not. Under hold-black only black markers
    /// are accepted, refused before any packet.
    /// </summary>
    public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        // overflow-safe (Start + Count can wrap): every bound is checked before any packet, including the enter
        if (marker.Channel != 0 || marker.Start < 0 || marker.Start >= _options.MaxIndices || marker.Count < 1 || marker.Count > _options.MaxIndices - marker.Start)
        {
            throw new ArgumentException(
                $"GPU marker must be channel 0 within indices 0..{_options.MaxIndices - 1} (got start {marker.Start}, count {marker.Count}).",
                nameof(marker));
        }

        if (_options.HoldBlackOnRelease && marker.Color != Rgb.Black)
        {
            throw new ArgumentException("Hold-black is active: only black (#000000) markers are accepted.", nameof(marker));
        }

        var g = InSession(id);
        lock (g.Sync)
        {
            if (g.Gate != FamilyGate.Pass)
            {
                return Task.FromResult(Refused("marker", g.Gate == FamilyGate.Fail ? g.GateReason! : "family gate not passed: run probe fwinfo first"));
            }

            if (g.BlackFailed)
            {
                return Task.FromResult(Refused("marker", "a previous black send failed; nothing more is sent in this process"));
            }

            var rows = new List<NativeProbeRow>();
            if (!g.Entered)
            {
                rows.Add(Control(g, "enter 0x06<-0", Nv5000Frames.Session(Nv5000.SessionEnter), black: false));
                g.Entered = true; // attempted: release logic must treat the card as possibly in host mode
                if (rows[^1].Status != 0)
                {
                    return Task.FromResult(Result("marker", false, $"enter failed with NVAPI status {rows[^1].Status}", rows, Comm(rows)));
                }
            }

            var colors = new Rgb[_options.MaxIndices];
            colors.AsSpan(marker.Start, marker.Count).Fill(marker.Color);
            var black = marker.Color == Rgb.Black;
            rows.Add(Control(g, $"frame 0x04 {_options.MaxIndices} records", Nv5000Frames.Frame(colors), black));
            g.Marker = (marker.Start, marker.Count);
            g.Holding = false;
            var ok = rows[^1].Status == 0;
            var fields = Comm(rows);
            fields["records"] = _options.MaxIndices.ToString(CultureInfo.InvariantCulture);
            fields["marker"] = $"{marker.Start}+{marker.Count} #{marker.Color.R:X2}{marker.Color.G:X2}{marker.Color.B:X2}";
            return Task.FromResult(Result("marker", ok, ok ? "marker frame sent (transport accepted; playback unobserved)" : "marker frame failed", rows, fields));
        }
    }

    public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct)
    {
        var g = InSession(id);
        lock (g.Sync)
        {
            if (g.Marker is null)
            {
                return Task.FromResult(Result("end-marker", true, "no marker to clear", [], new Dictionary<string, string> { ["comm"] = "not-attempted" }));
            }

            if (g.BlackFailed)
            {
                return Task.FromResult(Refused("end-marker", "a previous black send failed; nothing more is sent in this process"));
            }

            var row = Control(g, $"frame 0x04 {_options.MaxIndices} records black", Nv5000Frames.Black(_options.MaxIndices), black: true);
            g.Marker = null;
            return Task.FromResult(Result("end-marker", row.Status == 0, row.Status == 0 ? "black frame sent" : "black frame failed", [row], Comm([row])));
        }
    }

    public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct)
    {
        var g = Get(id);
        var r = ReleaseCore(g);
        lock (g.Sync)
        {
            g.OpenForBringup = false;
        }

        return Task.FromResult(r);
    }

    /// <summary>Host stop: stops keep-alive timers; a held card gets one more black frame, an entered card in normal mode its release.</summary>
    public ValueTask DisposeAsync()
    {
        Gpu[] gpus;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            gpus = [.. _gpus.Values];
        }

        foreach (var g in gpus)
        {
            lock (g.ReleaseGate)
            {
            var normalRelease = false;
            lock (g.Sync)
            {
                StopKeepAlive(g);
                if (g.Disposed)
                {
                    continue;
                }

                g.Disposed = true;
                if (!g.Entered || g.Chain is null)
                {
                    continue; // never entered: nothing is sent
                }

                if (_options.HoldBlackOnRelease)
                {
                    if (!g.BlackFailed)
                    {
                        var row = Control(g, "final black frame (hold-black)", Nv5000Frames.Black(_options.MaxIndices), black: true);
                        LogFinalBlack(g.Descriptor.Id.Value, row.Status);
                    }
                }
                else
                {
                    normalRelease = true;
                }
            }
            if (normalRelease) _ = ReleaseCore(g);
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

    private static void Send(ITransport chain, byte[] frame)
    {
        var vt = chain.SendAsync(frame, CancellationToken.None);
        if (!vt.IsCompletedSuccessfully)
        {
            vt.AsTask().GetAwaiter().GetResult();
        }
    }

    private static void StopKeepAlive(Gpu g)
    {
        g.KeepAlive?.Dispose();
        g.KeepAlive = null;
    }

    private FrameBudget NewBudget() => new(_ceilingFps, _budgetClock, _budgetTicksPerSecond);

    private (IReadOnlyList<ZoneDescriptor> Zones, int[][]? Indices) ZonesFor(ControllerId id)
    {
        var map = _maps?.Get(id);
        if (map is not null)
        {
            var why = NativeMaps.Validate(map, _options.MaxIndices);
            if (why is null)
            {
                return (NativeMaps.Zones(map), map.Zones.Select(z => z.Indices.ToArray()).ToArray());
            }

            LogMapRejected(id.Value, why);
        }

        return ([AggregateZone.Descriptor("GPU")], null);
    }

    private Rgb[] Records(Gpu g, IReadOnlyList<Rgb[]> zones)
    {
        if (g.ZoneIndices is null)
        {
            return AggregateZone.Replicate(zones.Count > 0 ? zones[0] : [], _options.MaxIndices);
        }

        var records = new Rgb[_options.MaxIndices]; // indices in no zone stay black in every frame (RQ-G3 not established)
        for (var z = 0; z < g.ZoneIndices.Length && z < zones.Count; z++)
        {
            var idx = g.ZoneIndices[z];
            for (var k = 0; k < idx.Length && k < zones[z].Length; k++)
            {
                records[idx[k]] = zones[z][k];
            }
        }

        return records;
    }

    private Gpu Get(ControllerId id)
    {
        lock (_gate)
        {
            return _gpus.TryGetValue(id, out var g) ? g : throw new ArgumentException($"Unknown MSI GPU {id}", nameof(id));
        }
    }

    private Gpu InSession(ControllerId id)
    {
        var g = Get(id);
        lock (g.Sync)
        {
            return g.OpenForBringup ? g : throw new InvalidOperationException($"{id}: probes and markers need a bring-up session.");
        }
    }

    private void EnsureChain(Gpu g)
    {
        if (g.Releasing) throw new DeviceBlockedException("GPU release is in progress; no new device operation was admitted.");
        if (g.Chain is null)
        {
            g.Chain = BuildChain(new NvapiI2cTransport(_nvapi, g.Handle, NvI2cProfile.MsiNv5000), g.Budget, _options.MaxIndices, out var fw, _logger);
            g.Firewall = fw;
        }
    }

    /// <summary>One control-sequence send (exempt from the frame limiter, design §9) as a probe row; never throws for NVAPI errors.</summary>
    private NativeProbeRow Control(Gpu g, string step, byte[] frame, bool black)
    {
        if (g.Releasing) return new NativeProbeRow(step, Convert.ToHexString(frame), -1, null, 0, "GPU release is in progress; command was not sent.");
        var sw = Stopwatch.StartNew();
        int status;
        string? note = null;
        using (g.Budget.BeginControl())
        {
            try
            {
                Send(g.Chain!, frame);
                status = 0;
            }
            catch (NvapiException ex)
            {
                status = ex.Status;
            }
            catch (InvalidOperationException ex)
            {
                // firewall/validator refusal or missing entry point: nothing (more) reached the bus
                status = -1;
                note = ex.Message;
            }
        }

        if (status != 0 && black && _options.HoldBlackOnRelease)
        {
            g.BlackFailed = true; // §9: no retry after a failed black send; the physical outcome is unknown
            StopKeepAlive(g);
            LogBlackFailed(g.Descriptor.Id.Value, status);
        }

        return new NativeProbeRow(step, Convert.ToHexString(frame), status, null, sw.Elapsed.TotalMilliseconds, note);
    }

    /// <summary>The 0xEC read (E2) with status, returned-size and plausibility checks; sets the gate only when asked.</summary>
    private NativeProbeResult ReadFamily(Gpu g, NvI2cProfile profile, bool setGate, out Dictionary<string, string> fields)
    {
        EnsureChain(g);
        g.Firewall!.EnsureRead(Nv5000.Device, Nv5000.FamilyRegister, Nv5000.FamilyLength);
        var data = new byte[Nv5000.FamilyLength];
        var returned = 0;
        int status;
        string? note = null;
        var sw = Stopwatch.StartNew();
        using (g.Budget.BeginControl())
        {
            g.Budget.CountOperation();
            try
            {
                status = _nvapi.I2cRead(g.Handle, Nv5000.Device, Nv5000.FamilyRegister, data, profile, out returned);
            }
            catch (InvalidOperationException ex)
            {
                status = -1;
                note = ex.Message;
            }
        }

        PacketLog.Shared.Record("MsiGpu-R", Nv5000.FamilyRegister, Nv5000.FamilyLength);
        var valid = Nv5000Family.TryDecode(status, returned, data, out var family, out var why);
        fields = new Dictionary<string, string>
        {
            ["profile"] = profile.Name,
            ["status"] = status.ToString(CultureInfo.InvariantCulture),
            ["returnedSize"] = returned.ToString(CultureInfo.InvariantCulture),
            ["raw"] = status == 0 ? Convert.ToHexString(data) : string.Empty,
            ["expected"] = Nv5000Family.CachedExpectationHex,
            ["comm"] = status == 0 ? "ok" : string.Create(CultureInfo.InvariantCulture, $"error({status})"),
            ["readback"] = "none",
        };
        string gate;
        if (valid)
        {
            fields["LEDType"] = $"0x{family.LedType:X2}";
            fields["ModuleType"] = $"0x{family.ModuleType:X4}";
            fields["FWVersion"] = $"0x{family.FwVersion:X2}";
            gate = family.IsNv5000 ? "pass" : "fail";
        }
        else
        {
            fields["invalid"] = why;
            gate = "invalid";
        }

        if (setGate)
        {
            if (valid)
            {
                g.Gate = family.IsNv5000 ? FamilyGate.Pass : FamilyGate.Fail;
                g.GateReason = family.IsNv5000 ? null : $"GPU family not NV5000: LEDType=0x{family.LedType:X2} ModuleType=0x{family.ModuleType:X4}";
            }

            fields["gate"] = gate;
        }
        else
        {
            fields["gate"] = $"not-applied ({gate}; diagnosis only)";
        }

        var row = new NativeProbeRow(
            $"read 0xEC len {Nv5000.FamilyLength} ({profile.Name})",
            $"{Nv5000.Device:X2}{Nv5000.FamilyRegister:X2}{Nv5000.FamilyLength:X2}",
            status,
            status == 0 ? Convert.ToHexString(data) : null,
            sw.Elapsed.TotalMilliseconds,
            note ?? string.Create(CultureInfo.InvariantCulture, $"returned size {returned}"));
        var ok = valid && (!setGate || family.IsNv5000);
        var summary = valid ? $"{family}; gate {fields["gate"]}" : $"family read invalid: {why}";
        return new NativeProbeResult(setGate ? ProbeFwInfo : ProbeFwInfoLegacy, ok, summary, [row]) { Fields = fields };
    }

    private NativeProbeResult ReleaseCore(Gpu g)
    {
        lock (g.ReleaseGate)
        {
        var delay = false;
        lock (g.Sync)
        {
            if (!_options.HoldBlackOnRelease && g.Entered && g.Chain is not null)
            {
                StopKeepAlive(g);
                g.Releasing = true;
                delay = true;
            }
            else return ReleaseLocked(g);
        }
        try
        {
            if (delay) _sleep(TimeSpan.FromMilliseconds(_options.ReleaseDelayMs));
            lock (g.Sync) { g.Releasing = false; return ReleaseLocked(g); }
        }
        finally { lock (g.Sync) g.Releasing = false; }
        }
    }

    private NativeProbeResult ReleaseLocked(Gpu g)
    {
        if (!g.Entered || g.Chain is null)
        {
            return Result("release", true, "never entered in this process: nothing sent", [],
                new Dictionary<string, string> { ["comm"] = "not-attempted", ["release"] = "not-applicable" });
        }

        if (_options.HoldBlackOnRelease)
        {
            if (g.Holding)
            {
                return Result("release", true, "hold-black already active: nothing resent", [],
                    new Dictionary<string, string> { ["comm"] = "not-attempted", ["release"] = "deferred-hold-black" });
            }

            if (g.BlackFailed)
            {
                return Result("release", false, "a previous black send failed: physical outcome unknown, not retried", [],
                    new Dictionary<string, string> { ["comm"] = "not-attempted", ["release"] = "deferred-hold-black" });
            }

            var row = Control(g, "hold-black frame (no 0x06<-1)", Nv5000Frames.Black(_options.MaxIndices), black: true);
            var fields = Comm([row]);
            fields["release"] = "deferred-hold-black";
            if (row.Status != 0)
            {
                return Result("release", false, "hold-black frame failed: physical outcome unknown, not retried", [row], fields);
            }

            g.Holding = true;
            g.Marker = null;
            if (_options.KeepAliveMs > 0 && g.KeepAlive is null && !g.Disposed)
            {
                var period = TimeSpan.FromMilliseconds(_options.KeepAliveMs);
                g.KeepAlive = _time.CreateTimer(OnKeepAlive, g, period, period);
            }

            return Result("release", true, "hold-black: black frame sent, host mode kept", [row], fields);
        }

        StopKeepAlive(g);
        var release = Control(g, "release 0x06<-1", Nv5000Frames.Session(Nv5000.SessionRelease), black: false);
        g.Entered = false; // attempted once; never retried in a loop
        g.Marker = null;
        g.LastFrame = null;
        var f = Comm([release]);
        f["release"] = release.Status == 0 ? "done" : "failed";
        return Result("release", release.Status == 0, release.Status == 0 ? "released (0x06<-1)" : "release write failed", [release], f);
    }

    private void OnKeepAlive(object? state)
    {
        var g = (Gpu)state!;
        lock (g.Sync)
        {
            if (g.Disposed || !g.Holding || g.BlackFailed || g.KeepAlive is null)
            {
                return;
            }

            var row = Control(g, "keep-alive black frame", Nv5000Frames.Black(_options.MaxIndices), black: true);
            if (row.Status == 0)
            {
                g.KeepAlives++;
            }
        }
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "GPU {Id}: native map rejected ({Why}); using the aggregate zone")]
    private partial void LogMapRejected(string id, string why);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "GPU {Id}: black frame failed with status {Status}; hold-black outcome unknown, nothing more is sent")]
    private partial void LogBlackFailed(string id, int status);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "GPU {Id}: final hold-black frame at stop, status {Status}")]
    private partial void LogFinalBlack(string id, int status);

    private enum FamilyGate
    {
        Unset,
        Pass,
        Fail,
    }

    private sealed class Gpu(IntPtr handle, ControllerDescriptor descriptor, int[][]? indices, FrameBudget budget)
    {
        public object Sync { get; } = new();
        public object ReleaseGate { get; } = new();
        public bool Releasing { get; set; }

        public IntPtr Handle { get; } = handle;

        public ControllerDescriptor Descriptor { get; set; } = descriptor;

        public int[][]? ZoneIndices { get; set; } = indices;

        public FrameBudget Budget { get; } = budget;

        public ITransport? Chain { get; set; }

        public I2cRegisterFirewall? Firewall { get; set; }

        public FamilyGate Gate { get; set; }

        public string? GateReason { get; set; }

        public bool Entered { get; set; }

        public bool Holding { get; set; }

        public bool BlackFailed { get; set; }

        public bool Disposed { get; set; }

        public bool OpenForWriter { get; set; }

        public bool OpenForBringup { get; set; }

        public ITimer? KeepAlive { get; set; }

        public long KeepAlives { get; set; }

        public byte[]? LastFrame { get; set; }

        public long LastSent { get; set; }

        public (int Start, int Count)? Marker { get; set; }
    }
}
