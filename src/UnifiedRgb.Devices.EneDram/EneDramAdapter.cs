using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.EneDram;

/// <summary>
/// ENE DRAM lighting over native SMBus through the pinned PawnIO module (NATIVE design §5; no OpenRGB runtime, no MSI or
/// ASUS software). Every compound passes SmbusFirewall → FrameValidator → FrameBudget (one per PIIX4 port, shared by the
/// sticks on it) before the bus. Each port is a bring-up target (<c>enedram:p0</c>, <c>enedram:p1</c>) that is listed without
/// any bus access; probe <c>ene-scan</c> inside a leased session finds the controllers, which are then listed as
/// <c>enedram:p0:0xNN</c>. A writer opens a controller only when both <see cref="EneDramOptions.PawnIoModuleVerified"/> and
/// <see cref="EneDramOptions.ControlVerified"/> are true; only then does discovery itself scan (and, with
/// <see cref="EneDramOptions.RemapOnOpen"/>, run the reference address bootstrap once per port). Session opens and probes
/// never switch direct mode on: the first marker does. Release/Close/Host stop send direct off (0x8020 ← 0, 0x80A0 ← 1)
/// for what this process switched on, except under hold-black, which skips it and leaves the stick in direct mode, black.
/// Whether an address remap persists across a reboot is unknown (RQ-E7).
/// </summary>
public sealed partial class EneDramAdapter : IWholeFrameAdapter, INativeBringup, IFrameBudgetSource, IFramePacingSource, IActiveDiscovery, IAsyncDisposable
{
    public const string ProbePawnIoId = "pawnio-id";
    public const string ProbeEneScan = "ene-scan";
    public const string ProbeEneRemap = "ene-remap";

    /// <summary>Row status of a compound refused before the bus (firewall, validator, budget scope, port not open): nothing sent.</summary>
    public const int StatusRefused = -1;

    /// <summary>Row status of a transaction that reported success without its documented returned cell: no data.</summary>
    public const int StatusNoData = -2;

    /// <summary>Win32 WAIT_TIMEOUT: the compound was dropped whole (global SMBus mutex not acquired, or port closed); nothing sent.</summary>
    public const int StatusDropped = 258;

    /// <summary><c>sleepModeApplied</c> of an opened port that does not report the mode the ioctl accepted.</summary>
    public const string SleepModeNotObserved = "not-observed";

    /// <summary><c>sleepModeApplied</c> of a port that did not open (nothing was applied).</summary>
    public const string SleepModeNotApplied = "not-applied";

    private static readonly string[] ProbeNames = [ProbePawnIoId, ProbeEneScan, ProbeEneRemap];

    /// <summary>Compound wire frames: Probe 2 bytes … RegBlock 5 + 3 bytes (SmbusCompound.ToWire).</summary>
    private static readonly FrameValidationRules WireRules = new() { MinLength = 2, MaxLength = 5 + EneRegisters.MaxBlock };

    private readonly IEneSmbusProvider _provider;
    private readonly EneDramOptions _options;
    private readonly ILogger _logger;
    private readonly INativeMapSource? _maps;
    private readonly Func<long>? _budgetClock;
    private readonly long _budgetTicksPerSecond;
    private readonly int _ceilingFps;
    private readonly IReadOnlyList<byte> _candidates;
    private readonly DeviceState _online = new();
    private readonly object _gate = new();
    private readonly Port[] _ports;
    private bool _disposed;

    /// <param name="budgetClock">FrameBudget tick source (tests); default the Stopwatch.</param>
    public EneDramAdapter(
        IEneSmbusProvider provider,
        EneDramOptions options,
        ILogger? logger = null,
        INativeMapSource? maps = null,
        Func<long>? budgetClock = null,
        long budgetTicksPerSecond = 0)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MutexWaitMs, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.FrameBudgetMs, nameof(options));
        _candidates = EneDramDefaults.Candidates(options.CandidateAddresses);
        _logger = logger ?? NullLogger.Instance;
        _maps = maps;
        _budgetClock = budgetClock;
        _budgetTicksPerSecond = budgetTicksPerSecond;
        _ceilingFps = options.FrameBudgetMs > 0 ? Safety.FrameBudget.CeilingFromBudgetMs(options.FrameBudgetMs) : EneDramDefaults.CeilingFps;
        _ports = [.. EneDramDefaults.Ports.Select(n => new Port(n, new SmbusFirewall(_candidates, options.RemapOnOpen, _logger), NewBudget()))];
    }

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public string Vendor => "ENE";

    public DeviceClass Class => DeviceClass.EneDram;

    public IReadOnlyList<string> Probes => ProbeNames;

    /// <summary>The bring-up target of a PIIX4 port: <c>enedram:p0</c>.</summary>
    public static ControllerId PortId(int port) => new(string.Create(CultureInfo.InvariantCulture, $"enedram:p{port}"));

    /// <summary>A controller found by a scan: <c>enedram:p0:0x77</c> (the port is part of the id, design §5.2).</summary>
    public static ControllerId StickId(int port, byte address) => new(string.Create(CultureInfo.InvariantCulture, $"enedram:p{port}:0x{address:X2}"));

    /// <summary>
    /// The port targets and every controller a scan of this process found. Sends nothing unless both gates are true; then
    /// each port is scanned once per process (after the remap bootstrap when <see cref="EneDramOptions.RemapOnOpen"/>).
    /// </summary>
    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);
            }
        }

        var active = _options.PawnIoModuleVerified && _options.ControlVerified;
        var result = new List<ControllerDescriptor>();
        foreach (var p in _ports)
        {
            lock (p.Sync)
            {
                if (active && !p.ScanAttempted)
                {
                    p.ScanAttempted = true; // once per process: later rescans list what is known without bus traffic
                    VerifiedScan(p);
                }

                result.Add(PortDescriptor(p));
                result.AddRange(p.Sticks.Values.Select(s => s.Descriptor));
            }
        }

        return Task.FromResult<IReadOnlyList<ControllerDescriptor>>(result);
    }

    /// <summary>The writer path; only with both gates, a known version and a usable LED count. Switches direct mode on.</summary>
    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        var (p, s) = Resolve(id);
        if (s is null || !IsControlVerified(id))
        {
            throw new DeviceBlockedException(UnverifiedReason(id));
        }

        lock (p.Sync)
        {
            if (s.BlackFailed)
            {
                throw new DeviceBlockedException("a previous black send failed; nothing more is sent in this process");
            }

            if (p.OpenForBringup && p.SessionDirect.Contains(s.Address))
            {
                throw new DeviceBlockedException($"{p.Id} is in a bring-up session that drives this controller");
            }

            EnsureBus(p);
            if (!s.DirectReady)
            {
                var rows = new List<NativeProbeRow>();
                bool ok;
                using (p.Budget.BeginControl())
                {
                    ok = Direct(p, s, on: true, rows);
                }

                if (!ok)
                {
                    var failed = rows.First(r => r.Status != 0);
                    throw new IOException(string.Create(CultureInfo.InvariantCulture, $"ENE 0x{s.Address:X2} direct on failed at '{failed.Step}' (status {failed.Status}{(failed.Note is null ? string.Empty : ": " + failed.Note)})"));
                }
            }

            s.OpenForWriter = true;
            s.LastFrame = null;
        }

        return Task.CompletedTask;
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        var (p, s) = Resolve(id);
        lock (p.Sync)
        {
            if (s is null || s.Descriptor.Zones.Count != 1 || zone.Value != s.Descriptor.Zones[0].Id.Value)
            {
                throw new ArgumentException("This controller takes whole frames (IWholeFrameAdapter).", nameof(zone));
            }
        }

        _ = WriteFrame(id, [colors.ToArray()]);
    }

    /// <summary>
    /// One complete frame of one stick: a <c>RegBlock</c> per LED in R,B,G order (or three byte writes per LED once the block
    /// transaction failed), inside one FrameBudget frame of the port. An unchanged frame sends nothing.
    /// </summary>
    public bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var (p, s) = Resolve(id);
        if (s is null)
        {
            throw new InvalidOperationException($"{id} is a bring-up target, not a controller");
        }

        lock (p.Sync)
        {
            if (!s.OpenForWriter || s.Version is null)
            {
                throw new InvalidOperationException($"{id} is not open for frames");
            }

            if (s.BlackFailed || s.OpenForBringup)
            {
                return false; // nothing more after a failed hold-black black send; a session owns the stick meanwhile
            }

            var colors = StickFrame(s, zones);
            var bytes = Rbg(colors);
            if (s.LastFrame is { } last && last.AsSpan().SequenceEqual(bytes))
            {
                return false; // cached frame: no native write, sample or budget refusal
            }

            using var scope = p.Budget.TryBeginFrame(s.Member);
            if (scope is null)
            {
                return false;
            }

            var op = WriteLeds(p, s, colors, null);
            if (!op.Ok)
            {
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"ENE 0x{s.Address:X2} frame failed (status {op.Status}{(op.Note is null ? string.Empty : ": " + op.Note)})"));
            }

            scope.Complete();
            s.LastFrame = bytes;
            return true;
        }
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) =>
        throw new NotSupportedException("Only direct mode is admitted on the native ENE path.");

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    /// <summary>Writer close: direct off for a stick the writer had open (skipped under hold-black). Idempotent; never throws for the bus.</summary>
    public Task CloseAsync(ControllerId id)
    {
        var (p, s) = Resolve(id);
        if (s is null)
        {
            return Task.CompletedTask;
        }

        lock (p.Sync)
        {
            var wasOpen = s.OpenForWriter;
            s.OpenForWriter = false;
            s.LastFrame = null;
            p.Budget.RemoveMember(s.Member);
            if (!wasOpen || !s.DirectOn)
            {
                return Task.CompletedTask;
            }

            if (_options.HoldBlackOnRelease)
            {
                LogDirectOffDeferred(s.Id.Value, "close");
                return Task.CompletedTask;
            }

            if (p.Bus is not null)
            {
                var rows = new List<NativeProbeRow>();
                using (p.Budget.BeginControl())
                {
                    if (!Direct(p, s, on: false, rows))
                    {
                        LogDirectOffFailed(s.Id.Value, rows.First(r => r.Status != 0).Status);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public FrameBudgetStats? FrameBudget(ControllerId id)
    {
        foreach (var p in _ports)
        {
            lock (p.Sync)
            {
                if (p.Id == id || p.Sticks.Values.Any(s => s.Id == id))
                {
                    return p.Budget.Stats();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A stick's deadline in its port's one shared FrameBudget, for its own member (the member its frames are admitted by);
    /// the port target itself and unknown ids take no frames and report zero.
    /// </summary>
    public TimeSpan DelayUntilNextFrame(ControllerId id)
    {
        foreach (var p in _ports)
        {
            string? member = null;
            lock (p.Sync)
            {
                member = p.Sticks.Values.FirstOrDefault(s => s.Id == id)?.Member;
            }

            if (member is not null)
            {
                return p.Budget.DelayUntilNextFrame(member);
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>Port targets never; a controller when both gates are set and its version and LED count are usable.</summary>
    public bool IsControlVerified(ControllerId id)
    {
        var (p, s) = Resolve(id);
        lock (p.Sync)
        {
            return s is not null && s.Blocked is null && _options.PawnIoModuleVerified && _options.ControlVerified;
        }
    }

    public string UnverifiedReason(ControllerId id)
    {
        var (p, s) = Resolve(id);
        string why;
        lock (p.Sync)
        {
            why = s is null ? $"{p.Id} is a bring-up target (SMBus port {p.Number}), never a writer"
                : s.Blocked ?? (_options.PawnIoModuleVerified ? "EneDram control not verified" : "EneDram PawnIO module not verified");
        }

        return _options.HoldBlackOnRelease ? why + "; hold-black active" : why;
    }

    /// <summary>
    /// Bring-up open: marks the session; sends nothing, loads nothing and switches no mode (the first marker does). A
    /// controller whose address a port session's marker drives is refused (the port and controller ids alias one writer).
    /// </summary>
    public Task OpenForBringupAsync(ControllerId id, CancellationToken ct)
    {
        var (p, s) = Resolve(id);
        lock (p.Sync)
        {
            if (s is null)
            {
                p.OpenForBringup = true;
            }
            else
            {
                if (p.OpenForBringup && p.SessionDirect.Contains(s.Address))
                {
                    throw new DeviceBlockedException($"{p.Id} is in a bring-up session whose marker drives 0x{s.Address:X2}; that session must end first");
                }

                s.OpenForBringup = true;
            }
        }

        return Task.CompletedTask;
    }

    public Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct)
    {
        if (probe is not (ProbePawnIoId or ProbeEneScan or ProbeEneRemap))
        {
            throw new ArgumentException($"Unknown ENE probe '{probe}' (known: {string.Join(", ", ProbeNames)}).", nameof(probe));
        }

        var (p, s) = InSession(id);
        lock (p.Sync)
        {
            if (probe == ProbeEneRemap)
            {
                var refusal = RemapRefusal(p, s);
                if (refusal is not null)
                {
                    return Task.FromResult(Refused(ProbeEneRemap, refusal));
                }
            }

            var blocked = OpenBus(p, probe);
            if (blocked is not null)
            {
                return Task.FromResult(blocked);
            }

            return Task.FromResult(probe switch
            {
                ProbePawnIoId => Identity(p),
                ProbeEneScan => Scan(p),
                _ => Remap(p),
            });
        }
    }

    /// <summary>
    /// A bounded marker frame on one controller: on a port target <see cref="NativeMarker.Channel"/> is the SMBus address of a
    /// controller this process's scan found; on a controller target it is 0 or its own address. The whole stick is written,
    /// <c>Start..Start+Count-1</c> at the marker colour and the rest black; direct mode is switched on first if this process
    /// has not. Under hold-black only black markers are accepted, refused before any packet.
    /// </summary>
    public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Channel < 0 || marker.Start < 0 || marker.Count < 1)
        {
            throw new ArgumentException($"ENE marker needs channel ≥ 0, start ≥ 0 and count ≥ 1 (got {marker.Channel}/{marker.Start}/{marker.Count}).", nameof(marker));
        }

        if (_options.HoldBlackOnRelease && marker.Color != Rgb.Black)
        {
            throw new ArgumentException("Hold-black is active: only black (#000000) markers are accepted.", nameof(marker));
        }

        var (p, target) = InSession(id);
        lock (p.Sync)
        {
            Stick s;
            if (target is null)
            {
                if (marker.Channel is < 1 or > 0x7F || !p.Sticks.TryGetValue((byte)marker.Channel, out var found))
                {
                    var known = string.Join(", ", p.Sticks.Keys.Select(a => $"0x{a:X2}"));
                    throw new ArgumentException($"On {p.Id} the marker channel is the SMBus address of a controller found by ene-scan in this process (known: {(known.Length == 0 ? "none" : known)}).", nameof(marker));
                }

                s = found;
                if (s.OpenForWriter || s.OpenForBringup)
                {
                    return Task.FromResult(Refused("marker", $"{s.Id} is open by a writer or its own bring-up session"));
                }
            }
            else
            {
                s = target;
                if (marker.Channel != 0 && marker.Channel != s.Address)
                {
                    throw new ArgumentException($"{s.Id}: marker channel must be 0 or 0x{s.Address:X2} (got {marker.Channel}).", nameof(marker));
                }
            }

            if (s.Blocked is { } why)
            {
                return Task.FromResult(Refused("marker", why));
            }

            // overflow-safe: Start + Count can wrap
            if (marker.Start >= s.Leds || marker.Count > s.Leds - marker.Start)
            {
                throw new ArgumentException($"ENE marker must lie within LEDs 0..{s.Leds - 1} of 0x{s.Address:X2} (got start {marker.Start}, count {marker.Count}).", nameof(marker));
            }

            if (s.BlackFailed)
            {
                return Task.FromResult(Refused("marker", "a previous black send failed; nothing more is sent in this process"));
            }

            var black = marker.Color == Rgb.Black;
            var rows = new List<NativeProbeRow>();
            var ok = true;
            using (p.Budget.BeginControl())
            {
                if (!s.DirectReady)
                {
                    ok = Direct(p, s, on: true, rows);
                }

                if (s.DirectOn && target is null)
                {
                    p.SessionDirect.Add(s.Address);
                }

                if (ok)
                {
                    var colors = new Rgb[s.Leds];
                    colors.AsSpan(marker.Start, marker.Count).Fill(marker.Color);
                    ok = WriteLeds(p, s, colors, rows).Ok;
                    if (target is null)
                    {
                        p.MarkerAddress = s.Address;
                    }
                    else
                    {
                        s.MarkerActive = true;
                    }
                }
            }

            BlackOutcome(s, rows, black);
            var fields = Comm(rows);
            fields["controller"] = $"0x{s.Address:X2}";
            fields["version"] = s.Version!.Name;
            fields["leds"] = s.Leds.ToString(CultureInfo.InvariantCulture);
            fields["directBase"] = $"0x{s.Version.DirectBase:X4}";
            fields["byteFallback"] = s.ByteFallback ? "true" : "false";
            fields["marker"] = $"{marker.Start}+{marker.Count} #{marker.Color.R:X2}{marker.Color.G:X2}{marker.Color.B:X2}";
            return Task.FromResult(Result("marker", ok, ok ? "marker frame sent (transport accepted; playback unobserved)" : "marker frame failed", rows, fields));
        }
    }

    /// <summary>Black over the whole stick of the last marker; direct mode stays on until release.</summary>
    public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct)
    {
        var (p, target) = InSession(id);
        lock (p.Sync)
        {
            var s = target ?? (p.MarkerAddress is { } a && p.Sticks.TryGetValue(a, out var x) ? x : null);
            var active = target is null ? p.MarkerAddress is not null : target.MarkerActive;
            if (!active || s is null)
            {
                return Task.FromResult(Result("end-marker", true, "no marker to clear", [], new Dictionary<string, string> { ["comm"] = "not-attempted" }));
            }

            if (s.BlackFailed)
            {
                return Task.FromResult(Refused("end-marker", "a previous black send failed; nothing more is sent in this process"));
            }

            var rows = new List<NativeProbeRow>();
            bool ok;
            using (p.Budget.BeginControl())
            {
                ok = WriteLeds(p, s, new Rgb[s.Leds], rows).Ok;
            }

            if (target is null)
            {
                p.MarkerAddress = null;
            }
            else
            {
                target.MarkerActive = false;
            }

            BlackOutcome(s, rows, black: true);
            return Task.FromResult(Result("end-marker", ok, ok ? "black frame sent" : "black frame failed", rows, Comm(rows)));
        }
    }

    /// <summary>
    /// Ends the session's hold on what it switched to direct mode: direct off (0x8020 ← 0, 0x80A0 ← 1) once per stick, no
    /// retry; under hold-black nothing is sent (<c>deferred-hold-black</c>); nothing entered → <c>not-applicable</c>. Idempotent.
    /// </summary>
    public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct)
    {
        var (p, target) = Resolve(id);
        lock (p.Sync)
        {
            List<Stick> covered;
            if (target is null)
            {
                covered = [.. p.SessionDirect.Select(a => p.Sticks[a]).Where(s => s.DirectOn && !s.OpenForWriter)];
                p.OpenForBringup = false;
                p.MarkerAddress = null;
                p.SessionDirect.Clear();
            }
            else
            {
                // only a session this controller opened is ended: a refused open (a port marker drives it) sends nothing
                covered = target.OpenForBringup && target.DirectOn ? [target] : [];
                target.OpenForBringup = false;
                target.MarkerActive = false;
            }

            if (covered.Count == 0)
            {
                return Task.FromResult(Result("release", true, "direct mode was not entered: nothing sent", [],
                    new Dictionary<string, string> { ["comm"] = "not-attempted", ["release"] = "not-applicable" }));
            }

            var list = string.Join(",", covered.Select(s => $"0x{s.Address:X2}"));
            if (_options.HoldBlackOnRelease)
            {
                foreach (var s in covered)
                {
                    LogDirectOffDeferred(s.Id.Value, "release");
                }

                return Task.FromResult(Result("release", true, "hold-black: direct off skipped (the stick stays in direct mode, black)", [],
                    new Dictionary<string, string> { ["comm"] = "not-attempted", ["release"] = "deferred-hold-black", ["directOn"] = list }));
            }

            var rows = new List<NativeProbeRow>();
            var ok = true;
            using (p.Budget.BeginControl())
            {
                foreach (var s in covered)
                {
                    ok &= Direct(p, s, on: false, rows);
                }
            }

            var fields = Comm(rows);
            fields["release"] = ok ? "done" : "failed";
            fields["controllers"] = list;
            return Task.FromResult(Result("release", ok, ok ? "direct off sent (firmware effect expected to return; unobserved)" : "direct off failed (no retry)", rows, fields));
        }
    }

    /// <summary>Host stop: direct off for every stick this process switched on (skipped under hold-black), then the ports close.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        foreach (var p in _ports)
        {
            lock (p.Sync)
            {
                foreach (var s in p.Sticks.Values)
                {
                    s.OpenForWriter = false;
                    s.OpenForBringup = false;
                    if (!s.DirectOn)
                    {
                        continue;
                    }

                    if (_options.HoldBlackOnRelease)
                    {
                        LogDirectOffDeferred(s.Id.Value, "host stop");
                        continue;
                    }

                    if (p.Bus is not null)
                    {
                        var rows = new List<NativeProbeRow>();
                        using (p.Budget.BeginControl())
                        {
                            if (!Direct(p, s, on: false, rows))
                            {
                                LogDirectOffFailed(s.Id.Value, rows.First(r => r.Status != 0).Status);
                            }
                        }
                    }
                }

                p.OpenForBringup = false;
                p.Bus?.Dispose();
                p.Bus = null;
            }
        }

        _provider.Dispose();
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

    private static bool IsRead(SmbusCompoundKind kind) => kind is SmbusCompoundKind.Probe or SmbusCompoundKind.ReadSig or SmbusCompoundKind.RegRead;

    private static byte[] Rbg(ReadOnlySpan<Rgb> colors)
    {
        var bytes = new byte[colors.Length * 3];
        for (var i = 0; i < colors.Length; i++)
        {
            bytes[3 * i] = colors[i].R;
            bytes[(3 * i) + 1] = colors[i].B;
            bytes[(3 * i) + 2] = colors[i].G;
        }

        return bytes;
    }

    private static Rgb[] StickFrame(Stick s, IReadOnlyList<Rgb[]> zones)
    {
        var colors = new Rgb[s.Leds];
        for (var z = 0; z < s.Bindings.Length && z < zones.Count; z++)
        {
            var src = zones[z];
            if (s.Bindings[z].Indices is not { } idx)
            {
                AggregateZone.Replicate(src, colors);
                continue;
            }

            for (var k = 0; k < idx.Length && k < src.Length; k++)
            {
                colors[idx[k]] = src[k];
            }
        }

        return colors;
    }

    private FrameBudget NewBudget() => new(_ceilingFps, _budgetClock, _budgetTicksPerSecond);

    private ControllerDescriptor PortDescriptor(Port p) =>
        new(p.Id, "ENE", string.Create(CultureInfo.InvariantCulture, $"ENE SMBus port {p.Number} (bring-up target)"), DeviceType.Dram, DeviceClass.EneDram,
            string.Create(CultureInfo.InvariantCulture, $"p{p.Number}"), [], Capabilities.DirectOnly(_ceilingFps));

    /// <summary>Descriptor and zone bindings of a controller: mapped zones when native-maps.json fits, else the aggregate Point zone.</summary>
    private void Describe(Port p, Stick s)
    {
        var name = $"DRAM @0x{s.Address:X2}";
        var serial = string.Create(CultureInfo.InvariantCulture, $"p{p.Number}:0x{s.Address:X2}");
        if (s.Blocked is not null)
        {
            s.Bindings = [];
            s.Descriptor = new ControllerDescriptor(s.Id, "ENE", $"{name} (blocked: {s.Blocked})", DeviceType.Dram, DeviceClass.EneDram, serial, [], Capabilities.DirectOnly(_ceilingFps));
            return;
        }

        var zones = new List<ZoneDescriptor>();
        var map = _maps?.Get(s.Id);
        var why = map is null ? "no map" : NativeMaps.Validate(map, s.Leds);
        if (map is not null && why is null)
        {
            zones.AddRange(NativeMaps.Zones(map));
            s.Bindings = [.. map.Zones.Select(z => new Binding(z.Indices.ToArray()))];
        }
        else
        {
            if (map is not null)
            {
                LogMapRejected(s.Id.Value, why!);
            }

            zones.Add(AggregateZone.Descriptor(name));
            s.Bindings = [new Binding(null)];
        }

        s.Descriptor = new ControllerDescriptor(s.Id, "ENE", $"{name} ({s.Version!.Name}, {s.Leds} LEDs)", DeviceType.Dram, DeviceClass.EneDram, serial, zones, Capabilities.DirectOnly(_ceilingFps));
    }

    private (Port Port, Stick? Stick) Resolve(ControllerId id)
    {
        foreach (var p in _ports)
        {
            if (p.Id == id)
            {
                return (p, null);
            }

            lock (p.Sync)
            {
                var s = p.Sticks.Values.FirstOrDefault(x => x.Id == id);
                if (s is not null)
                {
                    return (p, s);
                }
            }
        }

        throw new ArgumentException($"Unknown ENE controller {id}", nameof(id));
    }

    private (Port Port, Stick? Stick) InSession(ControllerId id)
    {
        var (p, s) = Resolve(id);
        lock (p.Sync)
        {
            var open = s is null ? p.OpenForBringup : s.OpenForBringup;
            return open ? (p, s) : throw new InvalidOperationException($"{id}: probes and markers need a bring-up session.");
        }
    }

    /// <summary>Opens the port's PawnIO handle once (pins, identity 1022:790B, port selection); throws Blocked otherwise.</summary>
    private void EnsureBus(Port p)
    {
        if (p.Bus is not null)
        {
            return;
        }

        try
        {
            p.Bus = _provider.Open(p.Number);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeviceBlockedException($"PawnIO port {p.Number} open failed: {ex.Message}", ex);
        }
    }

    private NativeProbeResult? OpenBus(Port p, string probe)
    {
        try
        {
            EnsureBus(p);
            return null;
        }
        catch (DeviceBlockedException ex)
        {
            var fields = new Dictionary<string, string>(_provider.Pins)
            {
                ["sleepModeConfigured"] = _options.SleepMode.ToString(CultureInfo.InvariantCulture),
                ["sleepModeApplied"] = SleepModeNotApplied,
                ["comm"] = "blocked",
                ["blocked"] = ex.Reason,
            };
            var rows = new List<NativeProbeRow>();

            // the loader got as far as pawnio_version: the refused evidence, or that of the library already loaded
            if ((PawnIoVersionEvidence.Of(ex) ?? _provider.Version) is { } version)
            {
                AddVersion(fields, rows, version);
            }

            rows.Add(new NativeProbeRow("pawnio open", string.Empty, StatusRefused, null, 0, ex.Reason));
            return Result(probe, false, $"PawnIO port {p.Number} blocked: {ex.Reason}", rows, fields);
        }
    }

    /// <summary>The observed version fields (expected and actual side by side with the pins) and the <c>pawnio_version</c> row.</summary>
    private static void AddVersion(Dictionary<string, string> fields, List<NativeProbeRow> rows, PawnIoVersionEvidence version)
    {
        foreach (var (key, value) in version.Fields)
        {
            fields[key] = value;
        }

        rows.Add(new NativeProbeRow("pawnio_version", string.Empty, version.Hr, version.RawHex, 0, version.Note));
    }

    /// <summary>Verified discovery of one port (both gates): the bootstrap when RemapOnOpen, else a plain scan; failures are logged.</summary>
    private void VerifiedScan(Port p)
    {
        try
        {
            EnsureBus(p);
        }
        catch (DeviceBlockedException ex)
        {
            LogPortBlocked(p.Number, ex.Reason);
            return;
        }

        var refusal = _options.RemapOnOpen ? RemapRefusal(p, null) : null;
        var r = _options.RemapOnOpen && refusal is null ? Remap(p) : Scan(p);
        LogVerifiedScan(p.Number, refusal is null ? r.Summary : $"{r.Summary} (no bootstrap: {refusal})");
    }

    /// <summary>E1: the PawnIO identity of this port's handle (opened by <see cref="OpenBus"/>); no SMBus transaction.</summary>
    private NativeProbeResult Identity(Port p)
    {
        var id = p.Bus!.Identity;
        var fields = new Dictionary<string, string>(_provider.Pins)
        {
            ["port"] = p.Number.ToString(CultureInfo.InvariantCulture),
            ["type"] = id.Type,
            ["ioBase"] = $"0x{id.IoBase:X}",
            ["pci"] = $"{id.PciVendor:X4}:{id.PciDevice:X4}",
            ["subsystem"] = $"{id.SubsystemVendor:X4}:{id.SubsystemDevice:X4}",
            ["amd790B"] = id.IsAmd790B ? "true" : "false",
            ["moduleVerifiedGate"] = _options.PawnIoModuleVerified ? "true" : "false",

            // §A: what was configured and what the opened handle reports the ioctl accepted, never one inferred from the other
            ["sleepModeConfigured"] = _options.SleepMode.ToString(CultureInfo.InvariantCulture),
            ["sleepModeApplied"] = p.Bus.AppliedSleepMode is { } applied ? applied.ToString(CultureInfo.InvariantCulture) : SleepModeNotObserved,
            ["comm"] = "ok",
            ["readback"] = "none",
        };
        var rows = new List<NativeProbeRow>();
        if (_provider.Version is { } version)
        {
            AddVersion(fields, rows, version);
        }

        rows.Add(new NativeProbeRow("pawnio open + ioctl_identity + ioctl_piix4_port_sel", string.Empty, 0, $"{id.Type} {fields["pci"]}", 0, "module and library pins verified before loading"));
        return new NativeProbeResult(ProbePawnIoId, id.IsAmd790B, $"PawnIO SmbusPIIX4 port {p.Number}: {id.Type} {fields["pci"]}, I/O 0x{id.IoBase:X}", rows) { Fields = fields };
    }

    /// <summary>
    /// E2 "with pointer writes" (design §5.4): per candidate Probe (fallback ReadSig 0x00), the 0xA0..0xAF signature, then
    /// pointer reads of the Micron check, name and config table. Responding addresses are observations: the number of modules
    /// behind one address is unknown until the E3c smoke.
    /// </summary>
    private NativeProbeResult Scan(Port p)
    {
        var rows = new List<NativeProbeRow>();
        var fields = new Dictionary<string, string>
        {
            ["port"] = p.Number.ToString(CultureInfo.InvariantCulture),
            ["label"] = "E2 with pointer writes",
            ["candidates"] = string.Join(",", _candidates.Select(a => $"0x{a:X2}")),
        };
        var found = new List<Found>();
        var timeoutsBefore = p.Bus!.MutexTimeouts;
        using (p.Budget.BeginControl())
        {
            p.Firewall.DiscoveryOpen = true;
            try
            {
                foreach (var a in _candidates)
                {
                    fields[$"a0x{a:X2}"] = Detect(p, a, rows, out var f);
                    if (f is not null)
                    {
                        found.Add(f);
                    }
                }
            }
            finally
            {
                p.Firewall.DiscoveryOpen = false;
            }
        }

        var seen = new HashSet<byte>();
        foreach (var f in found)
        {
            seen.Add(f.Address);
            var s = Upsert(p, f, fields);
            fields[$"name0x{f.Address:X2}"] = f.Name;
            fields[$"config0x{f.Address:X2}"] = Convert.ToHexString(f.Config);
            fields[$"version0x{f.Address:X2}"] = s.Version?.Name ?? "not in table";
            fields[$"leds0x{f.Address:X2}"] = s.Leds.ToString(CultureInfo.InvariantCulture);
            if (s.Blocked is not null)
            {
                fields[$"blocked0x{f.Address:X2}"] = s.Blocked;
            }
        }

        foreach (var s in p.Sticks.Values.Where(x => !seen.Contains(x.Address) && !x.OpenForWriter))
        {
            s.Blocked = "not found by the last scan";
            p.Firewall.ForgetDirect(s.Address);
            Describe(p, s);
        }

        var dropped = rows.Count(r => r.Status == StatusDropped);
        var refused = rows.Count(r => r.Status == StatusRefused);
        var usable = found.Select(f => p.Sticks[f.Address]).Where(s => s.Blocked is null).Select(s => $"0x{s.Address:X2}").ToArray();
        var responding = found.Select(f => $"0x{f.Address:X2}").ToArray();
        fields["responding"] = string.Join(",", responding);
        fields["controllers"] = string.Join(",", usable);
        fields["moduleCount"] = "unknown: a responding address is an observation; only the E3c smoke shows how many modules answer it";
        fields["mutexTimeouts"] = (p.Bus.MutexTimeouts - timeoutsBefore).ToString(CultureInfo.InvariantCulture);
        fields["mutexTimeoutsTotal"] = p.Bus.MutexTimeouts.ToString(CultureInfo.InvariantCulture);
        fields["abandonedMutexAcquisitions"] = p.Bus.AbandonedMutexAcquisitions.ToString(CultureInfo.InvariantCulture);
        fields["dropped"] = dropped.ToString(CultureInfo.InvariantCulture);
        fields["refused"] = refused.ToString(CultureInfo.InvariantCulture);
        fields["comm"] = refused > 0 ? "error(-1)" : dropped > 0 ? "error(258)" : "ok";
        fields["readback"] = "none";
        p.Scanned = true;
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"port {p.Number}: {usable.Length} usable ENE controller(s){(usable.Length > 0 ? " at " + string.Join(",", usable) : string.Empty)}, {responding.Length} passing detection; module count behind each address unknown{(dropped > 0 ? $"; {dropped} compound(s) dropped" : string.Empty)}");
        return new NativeProbeResult(ProbeEneScan, refused == 0 && dropped == 0, summary, rows) { Fields = fields };
    }

    /// <summary>The reference detect sequence for one address; returns the verdict text, <paramref name="found"/> on success.</summary>
    private string Detect(Port p, byte a, List<NativeProbeRow> rows, out Found? found)
    {
        found = null;
        var probe = Step(p, SmbusCompound.Probe(a), $"probe 0x{a:X2}", rows);
        if (Inconclusive(probe) is { } stop)
        {
            return stop;
        }

        var ack = probe.Ok;
        var via = "receive byte";
        if (!ack)
        {
            var zero = Step(p, SmbusCompound.ReadSig(a, 0x00), $"read 0x{a:X2} cmd 0x00", rows);
            if (Inconclusive(zero) is { } stop0)
            {
                return stop0;
            }

            ack = zero.Ok;
            via = "read byte data 0x00";
        }

        if (!ack)
        {
            return "NACK";
        }

        for (var i = 0; i < 16; i++)
        {
            var cmd = (byte)(0xA0 + i);
            var r = Step(p, SmbusCompound.ReadSig(a, cmd), $"signature 0x{a:X2} cmd 0x{cmd:X2}", rows);
            if (Inconclusive(r) is { } stopSig)
            {
                return stopSig;
            }

            if (!r.Ok || r.Value != i)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"ACK ({via}); signature fail at 0x{cmd:X2} (expected 0x{i:X2}, got {(r.Ok ? $"0x{r.Value:X2}" : $"status {r.Status}")})");
            }
        }

        if (!ReadRange(p, a, EneRegisters.MicronCheck, EneRegisters.MicronCheckLength, rows, out var micron, out var why))
        {
            return $"ACK ({via}); signature pass; Micron check read failed: {why}";
        }

        if (EneVersions.IsMicron(micron))
        {
            return $"ACK ({via}); signature pass; Micron device: skipped (reference)";
        }

        if (!ReadRange(p, a, EneRegisters.DeviceName, EneRegisters.DeviceNameLength, rows, out var name, out why))
        {
            return $"ACK ({via}); signature pass; name read failed: {why}";
        }

        if (!ReadRange(p, a, EneRegisters.ConfigTable, EneRegisters.ConfigTableLength, rows, out var config, out why))
        {
            return $"ACK ({via}); signature pass; config read failed: {why}";
        }

        found = new Found(a, EneVersions.NameOf(name), config);
        return $"ACK ({via}); signature pass; name '{found.Name}'";
    }

    private static string? Inconclusive(Op op) => op.Status switch
    {
        StatusDropped => $"inconclusive: compound dropped ({op.Note})",
        StatusRefused => $"refused: {op.Note}",
        _ => null,
    };

    /// <summary>Pointer reads of <paramref name="length"/> consecutive registers; any failed read fails the range (no zero filling).</summary>
    private bool ReadRange(Port p, byte a, ushort start, int length, List<NativeProbeRow> rows, out byte[] bytes, out string why)
    {
        bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var reg = (ushort)(start + i);
            var r = Step(p, SmbusCompound.RegRead(a, reg), $"read 0x{a:X2} reg 0x{reg:X4}", rows);
            if (!r.Ok)
            {
                why = string.Create(CultureInfo.InvariantCulture, $"0x{reg:X4} status {r.Status}{(r.Note is null ? string.Empty : ": " + r.Note)}");
                return false;
            }

            bytes[i] = r.Value;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>Creates or refreshes the controller of a detected address (a stick a writer or session has open is left as it is).</summary>
    private Stick Upsert(Port p, Found f, Dictionary<string, string> fields)
    {
        if (!p.Sticks.TryGetValue(f.Address, out var s))
        {
            s = new Stick(StickId(p.Number, f.Address), f.Address);
            p.Sticks[f.Address] = s;
        }
        else if (s.OpenForWriter || s.OpenForBringup)
        {
            fields[$"note0x{f.Address:X2}"] = "a writer or bring-up session has this controller open: not refreshed";
            return s;
        }

        var version = EneVersions.Find(f.Name);
        s.Name = f.Name;
        s.Version = version;
        s.Leds = version is null ? 0 : f.Config[version.LedCountOffset];
        s.Blocked = version is null ? $"ENE name '{f.Name}' not in table"
            : s.Leds < 1 ? string.Create(CultureInfo.InvariantCulture, $"config[0x{version.LedCountOffset:X2}] reports no LEDs")
            : s.Leds > version.MaxLeds ? string.Create(CultureInfo.InvariantCulture, $"config LED count {s.Leds} exceeds the {version.MaxLeds}-LED direct range of {version.Name}")
            : null;
        if (s.Blocked is null)
        {
            p.Firewall.SetDirect(s.Address, version!.DirectBase, s.Leds);
        }
        else
        {
            p.Firewall.ForgetDirect(s.Address);
        }

        Describe(p, s);
        return s;
    }

    private static string? RemapRefusal(Port p, Stick? target)
    {
        if (target is not null)
        {
            return $"ene-remap runs on the port target {p.Id}";
        }

        if (!p.Firewall.RemapEnabled)
        {
            return "EneDram:RemapOnOpen is off: the address bootstrap is not admitted";
        }

        if (p.RemapRun)
        {
            return "the bootstrap already ran on this port in this process; re-scan with ene-scan";
        }

        if (p.Sticks.Values.Any(s => s.DirectOn || s.OpenForWriter || s.OpenForBringup))
        {
            return "a controller on this port is in direct mode, open by a writer or in a bring-up session (read-only included)";
        }

        return null;
    }

    /// <summary>
    /// The reference DRAM bootstrap, once (design §5.4): for slot 0..7 while 0x77 ACKs, advance to the first candidate that
    /// does not ACK, then at 0x77 write 0x80F8 ← slot and 0x80F9 ← address &lt;&lt; 1; then re-scan. A dropped or refused
    /// compound or a failed write ends it (no retry). Whether the result persists across a reboot is unknown (RQ-E7).
    /// </summary>
    private NativeProbeResult Remap(Port p)
    {
        if (!_candidates.Contains(EneRegisters.DefaultDramAddress))
        {
            return Refused(ProbeEneRemap, "0x77 is not among the configured candidates");
        }

        p.RemapRun = true;
        var rows = new List<NativeProbeRow>();
        var remapped = new List<string>();
        string? abort = null;
        const byte at = EneRegisters.DefaultDramAddress;
        using (p.Budget.BeginControl())
        {
            p.Firewall.DiscoveryOpen = true;
            try
            {
                var idx = -1;
                for (var slot = 0; slot < 8; slot++)
                {
                    var head = Step(p, SmbusCompound.Probe(at), $"slot {slot}: probe 0x77", rows);
                    abort = Inconclusive(head);
                    if (abort is not null || !head.Ok)
                    {
                        break; // the reference stops when 0x77 no longer answers
                    }

                    var r = default(Op);
                    do
                    {
                        idx++;
                        if (idx >= _candidates.Count)
                        {
                            break;
                        }

                        r = Step(p, SmbusCompound.Probe(_candidates[idx]), $"slot {slot}: probe 0x{_candidates[idx]:X2}", rows);
                        abort = Inconclusive(r);
                    }
                    while (abort is null && r.Ok);

                    if (abort is not null)
                    {
                        break;
                    }

                    if (idx >= _candidates.Count)
                    {
                        continue; // every candidate answers: nothing to assign (reference)
                    }

                    var target = _candidates[idx];
                    var w1 = Step(p, SmbusCompound.RegWrite(at, EneRegisters.SlotIndex, (byte)slot), $"slot {slot}: 0x80F8 <- {slot}", rows);
                    var w2 = w1.Ok ? Step(p, SmbusCompound.RegWrite(at, EneRegisters.I2cAddress, (byte)(target << 1)), $"slot {slot}: 0x80F9 <- 0x{target << 1:X2} (0x{target:X2})", rows) : w1;
                    if (!w2.Ok)
                    {
                        abort = string.Create(CultureInfo.InvariantCulture, $"slot {slot} write failed (status {w2.Status})");
                        break;
                    }

                    remapped.Add(string.Create(CultureInfo.InvariantCulture, $"slot {slot}->0x{target:X2}"));
                }
            }
            finally
            {
                p.Firewall.DiscoveryOpen = false;
            }
        }

        var scan = Scan(p);
        var fields = new Dictionary<string, string>(scan.Fields)
        {
            ["remap"] = remapped.Count == 0 ? "none" : string.Join(",", remapped),
            ["persistence"] = "unknown (RQ-E7): repeat ene-scan after the next reboot and record the result",
        };
        if (abort is not null)
        {
            fields["abort"] = abort;
        }

        rows.AddRange(scan.Rows);
        var ok = abort is null && scan.Ok;
        return new NativeProbeResult(ProbeEneRemap, ok,
            string.Create(CultureInfo.InvariantCulture, $"bootstrap: {remapped.Count} slot(s) assigned{(abort is null ? string.Empty : $" ({abort})")}; re-scan: {scan.Summary}; persistence across a reboot unknown"),
            rows) { Fields = fields };
    }

    /// <summary>Direct on (0x8020 ← 1, 0x80A0 ← 1) or off (0x8020 ← 0, 0x80A0 ← 1), stopping at the first failure.</summary>
    private bool Direct(Port p, Stick s, bool on, List<NativeProbeRow> rows)
    {
        var v = (byte)(on ? 1 : 0);
        var w = Step(p, SmbusCompound.RegWrite(s.Address, EneRegisters.Direct, v), $"0x{s.Address:X2} direct {(on ? "on" : "off")}: 0x8020 <- {v}", rows);

        // a sent (not dropped/refused) enable may have taken effect: release then still sends direct off; off is never retried
        s.DirectOn = on && w.Status is not (StatusDropped or StatusRefused);
        s.DirectReady = false;
        if (!w.Ok)
        {
            return false;
        }

        var ok = Step(p, SmbusCompound.RegWrite(s.Address, EneRegisters.Apply, EneRegisters.ApplyValue), $"0x{s.Address:X2} apply: 0x80A0 <- 01", rows).Ok;
        s.DirectReady = on && ok; // a later writer open or marker re-sends an enable that did not fully succeed
        return ok;
    }

    /// <summary>Every LED of the stick in R,B,G: one RegBlock each, or three byte writes once a block transaction failed.</summary>
    private Op WriteLeds(Port p, Stick s, ReadOnlySpan<Rgb> colors, List<NativeProbeRow>? rows)
    {
        var rbg = new byte[3];
        for (var i = 0; i < colors.Length; i++)
        {
            var reg = (ushort)(s.Version!.DirectBase + (3 * i));
            rbg[0] = colors[i].R;
            rbg[1] = colors[i].B;
            rbg[2] = colors[i].G;
            if (!s.ByteFallback)
            {
                var b = Step(p, SmbusCompound.RegBlock(s.Address, reg, rbg), $"0x{s.Address:X2} LED {i} block 0x{reg:X4}", rows);
                if (b.Ok)
                {
                    continue;
                }

                if (b.FailedStep != "block")
                {
                    return b; // pointer failed, dropped or refused: stop, no retry
                }

                s.ByteFallback = true; // reference: a failed block write falls back to single bytes
                LogByteFallback(s.Id.Value, b.Status);
            }

            for (var k = 0; k < 3; k++)
            {
                var w = Step(p, SmbusCompound.RegWrite(s.Address, (ushort)(reg + k), rbg[k]), $"0x{s.Address:X2} LED {i} byte 0x{reg + k:X4}", rows);
                if (!w.Ok)
                {
                    return w;
                }
            }
        }

        return default;
    }

    /// <summary>§9: under hold-black a failed black send latches; nothing more is sent to that controller in this process.</summary>
    private void BlackOutcome(Stick s, List<NativeProbeRow> rows, bool black)
    {
        if (black && _options.HoldBlackOnRelease && rows.Exists(r => r.Status != 0))
        {
            s.BlackFailed = true;
            LogBlackFailed(s.Id.Value, rows.First(r => r.Status != 0).Status);
        }
    }

    /// <summary>
    /// One compound through the chain: SmbusFirewall → FrameValidator (wire length) → FrameBudget scope → the port. A refusal
    /// sends nothing (status -1); a mutex timeout drops the compound whole (258); success without data is -2.
    /// </summary>
    private Op Step(Port p, SmbusCompound c, string label, List<NativeProbeRow>? rows)
    {
        var wire = c.ToWire();
        var started = Stopwatch.GetTimestamp();
        Op op;
        try
        {
            p.Firewall.Ensure(c);
            var verdict = FrameValidator.Validate(wire, WireRules, _online);
            if (verdict != FrameRejectReason.None)
            {
                throw new FrameRejectedException(verdict, wire.Length);
            }

            p.Budget.CountOperation();
            var bus = p.Bus ?? throw new InvalidOperationException($"PawnIO port {p.Number} is not open");
            var r = bus.Execute(c);
            if (!r.Dropped)
            {
                PacketLog.Shared.Record(p.Channel, wire[0], wire.Length);
            }

            op = r.Ok ? new Op(0, r.Value, null, null)
                : r.Dropped ? new Op(StatusDropped, 0, r.Failure, r.Step)
                : new Op(r.Status != 0 ? r.Status : StatusNoData, 0, r.Failure, r.Step);
        }
        catch (InvalidOperationException ex) // ForbiddenSmbusException, FrameRejectedException, out-of-scope send, port not open
        {
            op = new Op(StatusRefused, 0, ex.Message, null);
        }

        rows?.Add(new NativeProbeRow(label, Convert.ToHexString(wire), op.Status, op.Ok && IsRead(c.Kind) ? $"{op.Value:X2}" : null,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, op.Note));
        return op;
    }

    [LoggerMessage(EventId = 6201, Level = LogLevel.Warning, Message = "ENE SMBus port {Port}: PawnIO blocked ({Why})")]
    private partial void LogPortBlocked(int port, string why);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Warning, Message = "ENE {Id}: native map rejected ({Why}); using the aggregate zone")]
    private partial void LogMapRejected(string id, string why);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Error, Message = "ENE {Id}: black send failed with status {Status}; hold-black outcome unknown, nothing more is sent")]
    private partial void LogBlackFailed(string id, int status);

    [LoggerMessage(EventId = 6204, Level = LogLevel.Information, Message = "ENE {Id}: block write failed (status {Status}); single-byte writes from now on")]
    private partial void LogByteFallback(string id, int status);

    [LoggerMessage(EventId = 6205, Level = LogLevel.Information, Message = "ENE {Id}: hold-black active, direct off skipped at {When} (stick stays in direct mode)")]
    private partial void LogDirectOffDeferred(string id, string when);

    [LoggerMessage(EventId = 6206, Level = LogLevel.Warning, Message = "ENE {Id}: direct off failed with status {Status} (no retry)")]
    private partial void LogDirectOffFailed(string id, int status);

    [LoggerMessage(EventId = 6207, Level = LogLevel.Information, Message = "ENE SMBus port {Port} verified discovery: {Summary}")]
    private partial void LogVerifiedScan(int port, string summary);

    /// <summary>A compound's outcome: status 0 = success (Value read), else the row status; FailedStep from the transport.</summary>
    private readonly record struct Op(int Status, byte Value, string? Note, string? FailedStep)
    {
        public bool Ok => Status == 0;
    }

    private sealed record Found(byte Address, string Name, byte[] Config);

    /// <summary>Zone z of a controller: <see cref="Indices"/> places a mapped zone; null replicates the aggregate colour.</summary>
    private sealed record Binding(int[]? Indices);

    private sealed class Port(int number, SmbusFirewall firewall, FrameBudget budget)
    {
        public object Sync { get; } = new();

        public int Number { get; } = number;

        public ControllerId Id { get; } = PortId(number);

        public string Channel { get; } = string.Create(CultureInfo.InvariantCulture, $"EneDram:p{number}");

        public SmbusFirewall Firewall { get; } = firewall;

        public FrameBudget Budget { get; } = budget;

        public IPawnIoPort? Bus { get; set; }

        public bool OpenForBringup { get; set; }

        public bool ScanAttempted { get; set; }

        public bool Scanned { get; set; }

        public bool RemapRun { get; set; }

        /// <summary>The controller of the port session's last marker.</summary>
        public byte? MarkerAddress { get; set; }

        /// <summary>Controllers the port session switched to direct mode (released with it).</summary>
        public HashSet<byte> SessionDirect { get; } = [];

        public Dictionary<byte, Stick> Sticks { get; } = [];
    }

    private sealed class Stick(ControllerId id, byte address)
    {
        public ControllerId Id { get; } = id;

        public byte Address { get; } = address;

        public string Member { get; } = $"0x{address:X2}";

        public string Name { get; set; } = string.Empty;

        public EneVersion? Version { get; set; }

        public int Leds { get; set; }

        /// <summary>Why this controller cannot be driven (name not in table, LED count, not found); null = usable.</summary>
        public string? Blocked { get; set; }

        public ControllerDescriptor Descriptor { get; set; } = null!;

        public Binding[] Bindings { get; set; } = [];

        /// <summary>This process switched direct mode on (or sent the enable) and has not sent direct off since.</summary>
        public bool DirectOn { get; set; }

        /// <summary>Both direct-on writes (0x8020 ← 1, 0x80A0 ← 1) succeeded; otherwise the next open or marker sends them again.</summary>
        public bool DirectReady { get; set; }

        public bool OpenForWriter { get; set; }

        public bool OpenForBringup { get; set; }

        public bool BlackFailed { get; set; }

        public bool ByteFallback { get; set; }

        public bool MarkerActive { get; set; }

        public byte[]? LastFrame { get; set; }
    }
}
