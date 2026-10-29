using System.Diagnostics;
using System.Globalization;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// Passive receiver discovery plus existing leased native diagnostics and opt-in, locally mapped direct keyboard frames.
/// P725 output is area0/static only; no animated mouse fields, OLED/input access or guessed keyboard 51 2C packet.
/// All controllers on one receiver use ONE RogReceiverSession. FrameBus supplies the existing shared scene time.
/// </summary>
public sealed class RogAdapter : IWholeFrameAdapter, INativeBringup, IFrameBudgetSource, IFramePacingSource, IRogPeripheralConfigurationLifetime, IAsyncDisposable
{
    private readonly IHidReportDevices _devices;
    private readonly RogOptions _options;
    private readonly Dictionary<ControllerId, Target> _targets = [];
    private readonly Dictionary<string, RogReceiverSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private Task? _configurationReleaseTask;
    private bool _disposed;
    private static readonly string[] ProbeNames = ["receiver-pairing", "keyboard-layout"];

    public RogAdapter(IHidReportDevices devices, RogOptions options)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        ArgumentNullException.ThrowIfNull(options);
        if (options.HidTimeoutMs is < 1 or > 2000 || options.MaximumFps is < 1 or > 30 || options.Keyboards is null || options.Keyboards.Length > 8
            || options.Mice is null || options.Mice.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Keyboards.Any(k => k is null || k.ReceiverKey is null || k.LedIds is null)) throw new ArgumentException("ROG route pins cannot be null.", nameof(options));
        var bindings = options.Keyboards.Select(k => k with { LedIds = (byte[])k.LedIds.Clone() }).ToArray();
        if (bindings.Any(k => k.ReceiverKey.Length != 64 || !k.ReceiverKey.All(char.IsAsciiHexDigit)
            || k.ReportId is not (2 or 3) || (k.WholeColorOnly ? k.LedIds.Length != 0 || k.KeyOrderVerified : k.LedIds.Length is < 1 or > RogProtocol.MaximumKeys)
            || k.LedIds.Distinct().Count() != k.LedIds.Length)
            || bindings.Select(k => (k.ReceiverKey.ToUpperInvariant(), k.ReportId)).Distinct().Count() != bindings.Length)
            throw new ArgumentException("ROG keyboard bindings require unique receiver/route pins and explicit distinct LED ids.", nameof(options));
        var mice = (RogMouseBinding[])options.Mice.Clone();
        if (mice.Any(m => m is null || m.ReceiverKey is null || m.ReceiverKey.Length != 64 || !m.ReceiverKey.All(char.IsAsciiHexDigit) || m.ReportId is not (2 or 3))
            || bindings.Select(k => (k.ReceiverKey.ToUpperInvariant(), k.ReportId)).Concat(mice.Select(m => (m.ReceiverKey.ToUpperInvariant(), m.ReportId))).Distinct().Count() != bindings.Length + mice.Length)
            throw new ArgumentException("ROG mouse bindings need distinct receiver/route pins, never a keyboard route.", nameof(options));
        _options = options with { Keyboards = bindings, Mice = mice };
    }

    public string Vendor => "ASUS ROG";
    public DeviceClass Class => DeviceClass.AsusRog;
    public IReadOnlyList<string> Probes => ProbeNames;
    public event Action<HotplugEvent>? Hotplug { add { } remove { } }

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var receivers = RogReceiver.Discover(_devices);
        var descriptors = new List<ControllerDescriptor>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var receiver in receivers)
            {
                if (!_sessions.TryGetValue(receiver.Key, out var session) || !session.IsAvailable)
                    _sessions[receiver.Key] = session = new RogReceiverSession(_devices, receiver, _options.HidTimeoutMs);
                AddTarget(receiver, session, null, descriptors);
                foreach (var binding in _options.Keyboards.Where(k => string.Equals(k.ReceiverKey, receiver.Key, StringComparison.OrdinalIgnoreCase)))
                    if (receiver.Channels.ContainsKey(binding.ReportId)) AddTarget(receiver, session, binding, descriptors);
                foreach (var binding in _options.Mice.Where(m => string.Equals(m.ReceiverKey, receiver.Key, StringComparison.OrdinalIgnoreCase)))
                    if (receiver.Channels.ContainsKey(binding.ReportId)) AddMouseTarget(receiver, session, binding, descriptors);
            }
        }
        return Task.FromResult<IReadOnlyList<ControllerDescriptor>>(descriptors);
    }

    private void AddMouseTarget(RogReceiver receiver, RogReceiverSession session, RogMouseBinding binding, List<ControllerDescriptor> result)
    {
        var id = new ControllerId($"asusr:{receiver.Key}:{binding.ReportId}");
        var descriptor = new ControllerDescriptor(id, Vendor, "ROG Harpe II Extreme Edition20 (area0, unmapped)", DeviceType.Mouse, Class,
            string.Empty, [AggregateZone.Descriptor("Mouse area0")], Capabilities.DirectOnly(_options.MaximumFps));
        if (!_targets.TryGetValue(id, out var existing) || !ReferenceEquals(existing.Session, session))
            _targets[id] = new Target(descriptor, session, null, new FrameBudget(_options.MaximumFps)) { MouseBinding = binding };
        result.Add(descriptor);
    }

    private void AddTarget(RogReceiver receiver, RogReceiverSession session, RogKeyboardBinding? binding, List<ControllerDescriptor> result)
    {
        var suffix = binding is null ? "receiver" : binding.ReportId.ToString(CultureInfo.InvariantCulture);
        var id = new ControllerId($"asusr:{receiver.Key}:{suffix}");
        var name = binding is null ? $"ROG receiver {receiver.ProductId:X4} (identity unqueried)" : "ROG Azoth X (locally pinned route)";
        ZoneDescriptor[] zones = binding is null ? [] : [binding.KeyOrderVerified
            ? new(new ZoneId(0), "Keyboard (verified local order)", ZoneLayout.Linear(binding.LedIds.Length))
            : AggregateZone.Descriptor("Keyboard")];
        var descriptor = new ControllerDescriptor(id, Vendor, name, binding is null ? DeviceType.Unknown : DeviceType.Keyboard,
            Class, string.Empty, zones, binding is null ? new(false, 0, [], 0, 0, 0) : Capabilities.DirectOnly(_options.MaximumFps));
        if (!_targets.TryGetValue(id, out var existing) || !ReferenceEquals(existing.Session, session))
            _targets[id] = new Target(descriptor, session, binding, new FrameBudget(_options.MaximumFps));
        result.Add(descriptor);
    }

    public bool IsControlVerified(ControllerId id) => Get(id).Binding?.ControlVerified == true || Get(id).MouseBinding?.ControlVerified == true;
    public string UnverifiedReason(ControllerId id) => Get(id).Binding is null && Get(id).MouseBinding is null
        ? "ROG receiver identity only; no automatic lighting takeover"
        : "ROG peripheral control has not been optically verified";

    public async Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = Get(id);
            if (!IsControlVerified(id)) throw new DeviceBlockedException(UnverifiedReason(id));
            target.Open = false;
            target.Paired = null;
            target.KeyboardNative = null;
            target.MouseStatic = null;
            var paired = await target.Session.QueryPairingAsync(ct).ConfigureAwait(false);
            if (target.MouseBinding is { } mouseBinding)
            {
                var mouse = paired.SingleOrDefault(p => p.ReportId == mouseBinding.ReportId && p.IsHarpeEdition20)
                    ?? throw new DeviceBlockedException("Pinned receiver route does not currently name a P725 mouse.");
                target.MouseStatic = RogProtocol.ParseMouseStatic(await target.Session.QueryMouseLightingAsync(mouse, 0, ct).ConfigureAwait(false), mouse.ReportId);
                target.Paired = mouse;
                target.Generation = target.Session.Generation;
                target.Open = true;
                return;
            }
            var keyboardBinding = target.Binding ?? throw new DeviceBlockedException("No keyboard route pin is configured.");
            var keyboard = paired.SingleOrDefault(p => p.ReportId == keyboardBinding.ReportId && p.IsAzothX)
                ?? throw new DeviceBlockedException("Pinned receiver route does not currently name an Azoth X.");
            await target.Session.QueryKeyboardAsync(keyboard, layout: true, ct).ConfigureAwait(false);
            if (keyboardBinding.WholeColorOnly) target.KeyboardNative = await target.Session.QueryKeyboardNativeAsync(keyboard, ct).ConfigureAwait(false);
            target.Paired = keyboard;
            target.Generation = target.Session.Generation;
            target.Open = true;
        }
        finally { _lifecycle.Release(); }
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) =>
        throw new NotSupportedException("ROG takes one complete controller frame through IWholeFrameAdapter.");

    public bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var target = Get(id);
        if (!target.Open || target.Binding is null && target.MouseBinding is null || target.Paired is null) throw new DeviceBlockedException("ROG peripheral is not open.");
        if (zones.Count != 1 || zones[0].Length != target.Descriptor.TotalLeds) throw new ArgumentException("ROG frame dimensions do not match the local mapping.", nameof(zones));
        using var frame = target.Budget.TryBeginFrame();
        if (frame is null) return false;
        if (target.MouseBinding is { } mouse)
        {
            var original = target.MouseStatic ?? throw new DeviceBlockedException("Mouse brightness snapshot unavailable.");
            target.Session.WriteMouseStaticAsync(target.Paired, new(original.Brightness, zones[0][0]), mouse.ControlVerified, target.Generation, CancellationToken.None, target.Budget).GetAwaiter().GetResult();
        }
        else if (target.Binding!.WholeColorOnly)
        {
            var state = target.KeyboardNative ?? throw new DeviceBlockedException("Keyboard effect snapshot unavailable.");
            var common = Convert.FromHexString(state.Payloads[0]);
            var color = RogProtocol.KeyboardColourFrame(zones[0][0], common[1], common[0]);
            target.Session.WriteKeyboardNativeAsync(target.Paired, color, target.Binding.ControlVerified,
                target.Generation, CancellationToken.None, target.Budget).GetAwaiter().GetResult();
        }
        else
        {
            var binding = target.Binding!;
            var keys = new RogKeyColor[binding.LedIds.Length];
            for (var i = 0; i < keys.Length; i++) keys[i] = new(binding.LedIds[i], zones[0][binding.KeyOrderVerified ? i : 0]);
            target.Session.WriteKeyboardAsync(target.Paired, keys, binding.ControlVerified, target.Generation, CancellationToken.None, target.Budget).GetAwaiter().GetResult();
        }
        target.LightingTouched = true;
        frame.Complete();
        return true;
    }

    public Task OpenForBringupAsync(ControllerId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Get(id).Bringup = true; // no open/report until an explicit read-only probe
        return Task.CompletedTask;
    }

    public async Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ProbeNames.Contains(probe, StringComparer.Ordinal)) throw new ArgumentException("Unknown ROG probe.", nameof(probe));
            var target = Get(id);
            if (!target.Bringup) throw new DeviceBlockedException("ROG query requires the existing native bring-up lease.");
            if (probe == "keyboard-layout" && target.Binding is null) throw new DeviceBlockedException("Keyboard layout query requires a local route pin.");
            var start = Stopwatch.GetTimestamp();
            var paired = await target.Session.QueryPairingAsync(ct).ConfigureAwait(false);
            var rows = new List<NativeProbeRow>
            {
                new("receiver-pairing", Convert.ToHexString(RogProtocol.PairingQuery()), 0, target.Session.PairingReplyHex, Stopwatch.GetElapsedTime(start).TotalMilliseconds),
            };
            var fields = new Dictionary<string, string> { ["pairedCount"] = paired.Count.ToString(CultureInfo.InvariantCulture) };
            foreach (var p in paired) fields[$"route{p.ReportId}"] = $"PID={p.ProductId:X4};notifyReportId={p.NotifyReportId};model={(p.IsAzothX ? "Azoth X" : p.IsHarpeEdition20 ? "P725" : "unknown")}";
            if (probe == "keyboard-layout")
            {
                var keyboard = paired.SingleOrDefault(p => p.ReportId == target.Binding!.ReportId && p.IsAzothX)
                    ?? throw new DeviceBlockedException("Pinned route is not an Azoth X.");
                start = Stopwatch.GetTimestamp();
                var reply = await target.Session.QueryKeyboardAsync(keyboard, layout: true, ct).ConfigureAwait(false);
                fields["layoutReply"] = Convert.ToHexString(reply);
                rows.Add(new("keyboard-layout", Convert.ToHexString(RogProtocol.KeyboardLayoutQuery(keyboard.ReportId)), 0,
                    Convert.ToHexString(reply), Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            }
            return new NativeProbeResult(probe, true, "Known ROG identity query completed; lighting/input unchanged; physical control unverified.",
                rows) { Fields = fields };
        }
        finally { _lifecycle.Release(); }
    }

    public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct) =>
        throw new NotSupportedException("ROG visual markers are not enabled before a reversible exact-model lighting path is verified.");

    public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct) => ReleaseAsync(id, ct);

    public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct)
    {
        var target = Get(id);
        target.Bringup = false;
        target.Open = false;
        return Task.FromResult(new NativeProbeResult("release", true, "No release command in evidence; sent nothing, prior lighting restoration unverified.", []));
    }

    public Task CloseAsync(ControllerId id) => ReleaseAsync(id, CancellationToken.None);
    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException("ROG firmware-effect layouts remain under research.");
    public Telemetry? ReadTelemetry(ControllerId id) => null;
    public FrameBudgetStats? FrameBudget(ControllerId id)
    {
        lock (_gate)
        {
            // A status snapshot can outlive configuration handoff. Metrics never reacquire a retired device.
            if (_disposed) return null;
            return _targets.TryGetValue(id, out var target) ? target.Budget.Stats()
                : throw new ArgumentException("Unknown ROG controller.", nameof(id));
        }
    }
    public TimeSpan DelayUntilNextFrame(ControllerId id) => Get(id).Budget.DelayUntilNextFrame();

    private Target Get(ControllerId id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _targets.TryGetValue(id, out var target) ? target : throw new ArgumentException("Unknown ROG controller.", nameof(id));
        }
    }

    public Task ReleaseForConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return _configurationReleaseTask ??= ReleaseConfigurationCoreAsync(ct);
    }

    private async Task ReleaseConfigurationCoreAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Target[] targets;
            RogReceiverSession[] sessions;
            lock (_gate)
            {
                _disposed = true;
                targets = _targets.Values.ToArray();
                sessions = _sessions.Values.ToArray();
            }

            var errors = new List<Exception>();
            foreach (var target in targets)
            {
                try
                {
                    if (target.Open && target.LightingTouched && target.Paired is not null)
                    {
                        if (target.MouseBinding is not null && target.MouseStatic is { } mouse)
                            await target.Session.WriteMouseStaticAsync(target.Paired, mouse, target.MouseBinding.ControlVerified,
                                target.Generation, ct).ConfigureAwait(false);
                        else if (target.Binding?.WholeColorOnly == true && target.KeyboardNative is { } keyboard)
                            await target.Session.WriteKeyboardNativeAsync(target.Paired, keyboard, target.Binding.ControlVerified,
                                target.Generation, ct).ConfigureAwait(false);
                        else if (target.Binding is not null)
                            throw new DeviceBlockedException("The direct keyboard route has no verified original-lighting restoration snapshot.");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
                finally { target.Open = false; target.Bringup = false; }
            }
            foreach (var session in sessions)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
            }
            if (errors.Count != 0) throw new AggregateException("ROG original settings restoration or receiver disposal was incomplete.", errors);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            RogReceiverSession[] sessions;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = _sessions.Values.ToArray();
            }
            foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class Target(ControllerDescriptor descriptor, RogReceiverSession session, RogKeyboardBinding? binding, FrameBudget budget)
    {
        public ControllerDescriptor Descriptor { get; } = descriptor;
        public RogReceiverSession Session { get; } = session;
        public RogKeyboardBinding? Binding { get; } = binding;
        public RogMouseBinding? MouseBinding { get; init; }
        public RogMouseStaticState? MouseStatic { get; set; }
        public RogKeyboardNativeState? KeyboardNative { get; set; }
        public FrameBudget Budget { get; } = budget;
        public RogPairedDevice? Paired { get; set; }
        public long Generation { get; set; }
        public bool Open { get; set; }
        public bool Bringup { get; set; }
        public bool LightingTouched { get; set; }
    }
}
