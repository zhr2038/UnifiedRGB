using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Devices.Safety;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Existing FrameBus/DeviceWriter path into the one shared OLED/lighting owner; no independent animation clock. Without per-key
/// configuration the keyboard is exactly the aggregate whole-keyboard zone. A per-key plan that is not Ready keeps that zone and
/// only reports why; a Ready local route makes the zone one record per physical key. Complete C0 81 frames use the current
/// owner's independently verified route map; another route's missing scan never substitutes guessed LED ids.
/// </summary>
public sealed class WiredKeyboardAdapter : IWholeFrameAdapter, INativeBringup, IRogPeripheralConfigurationLifetime, IPerKeyFrameSource, IDisposable
{
    // NativeMarker.Channel is numeric in the existing RPC contract; status names this channel "per-key-led".
    public const int PerKeyLedMarkerChannel = 1;
    private readonly RogWiredLightingBridge _bridge;
    private readonly ControllerDescriptor _descriptor;
    private readonly RogPerKeyPlan? _perKey;
    private readonly IReadOnlyList<string> _verifiedRoutes;
    private volatile bool _open;
    private PerKeyFrame? _lastPerKeyFrame;
    private long _perKeyFrames;
    private NormalFrame? _lastNormalFrame;
    private NormalFrame? _bringupFrame;
    private string? _bringupTransport;
    private bool _markerActive;
    private sealed record NormalFrame(Rgb? Aggregate, IReadOnlyList<RogKeyColor>? Keys, string? Transport);
    public WiredKeyboardAdapter(RogWiredLightingBridge bridge, KeyboardDisplayOptions options, RogPerKeyOptions? perKey = null)
    {
        _bridge = bridge;
        if (!options.HasVerifiedLightingRoute)
            throw new InvalidOperationException("Keyboard lighting requires explicit local verification.");
        var stableKey = options.HasVerifiedWiredLighting ? options.DeviceKey : options.WirelessReceiverKey;
        if (stableKey.Length != 64 || !stableKey.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Keyboard lighting requires a stable verified identity.");
        List<string> routes = [];
        if (options.HasVerifiedWiredLighting) routes.Add(RogPerKeyLighting.WiredTransport);
        if (options.HasVerifiedWirelessLighting) routes.Add(RogPerKeyLighting.WirelessTransport);
        _verifiedRoutes = routes.ToArray();
        _perKey = RogPerKeyPlanner.Plan(perKey, routes, bridge.KeyFrameTransport);
        var zone = _perKey switch
        {
            null => AggregateZone.Descriptor("Keyboard"),
            { Ready: true } plan => new ZoneDescriptor(new ZoneId(0), "Keyboard keys (per-key)", ZoneLayout.Linear(plan.Map!.Keys.Count))
            {
                PerKey = plan.Capability,
                PerKeyPoints = PerKeyProjection.Project(plan.Map),
            },
            var plan => AggregateZone.Descriptor("Keyboard") with { PerKey = plan.Capability },
        };
        _descriptor = new(new("asusr:wired:" + stableKey), "ASUS ROG", _perKey?.Ready == true ? "ROG 夜魔 X（逐键）" : "ROG 夜魔 X（整区）",
            DeviceType.Keyboard, DeviceClass.AsusRog, string.Empty, [zone], Capabilities.DirectOnly(10));
        _bridge.AvailabilityChanged += Changed;
    }
    public string Vendor => "ASUS ROG";
    public DeviceClass Class => DeviceClass.AsusRog;
    public IReadOnlyList<string> Probes => ["per-key-status"];
    public bool IsControlVerified(ControllerId id) { Require(id); return true; }
    public string UnverifiedReason(ControllerId id) => "Wired lighting requires verified local pins.";
    public Task OpenForBringupAsync(ControllerId id, CancellationToken ct)
    {
        Require(id); ct.ThrowIfCancellationRequested();
        var transport = _bridge.KeyFrameTransport;
        if (!_bridge.Available || transport is null || !_verifiedRoutes.Contains(transport))
            throw new DeviceBlockedException("The shared actor has no verified per-key lighting route.");
        var frame = Volatile.Read(ref _lastNormalFrame)
            ?? throw new DeviceBlockedException("No completed normal lighting frame is available to restore after a marker.");
        if (frame.Keys is not null && frame.Transport != transport)
            throw new DeviceBlockedException("The last normal key frame belongs to a different transport.");
        _bringupFrame = frame; _bringupTransport = transport;
        return Task.CompletedTask;
    }
    public Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct)
    {
        Require(id); ct.ThrowIfCancellationRequested();
        if (probe != "per-key-status") throw new ArgumentException("Unknown keyboard probe.", nameof(probe));
        return Task.FromResult(new NativeProbeResult(probe, true, "Shared actor route only; physical LED mapping is unverified.", [])
        {
            Fields = new Dictionary<string, string> { ["transport"] = _bridge.KeyFrameTransport ?? "unavailable", ["channel"] = "per-key-led",
                ["markerChannel"] = "1", ["physicalMap"] = "unverified" },
        });
    }
    public async Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct)
    {
        Require(id); ArgumentNullException.ThrowIfNull(marker); ct.ThrowIfCancellationRequested();
        if (marker.Channel != PerKeyLedMarkerChannel || marker.Count != 1 || marker.Start is < 0 or > 127)
            throw new ArgumentException("per-key-led requires channel 1, exactly one raw LED id in 0..127.", nameof(marker));
        RequireBringupRoute();
        if (_markerActive) await RestoreMarkerAsync(ct).ConfigureAwait(false);
        _markerActive = true; // An uncertain send still needs the existing bring-up release path.
        await _bridge.WriteKeysAsync([new((byte)marker.Start, marker.Color)], _bringupTransport!, ct).ConfigureAwait(false);
        return new("marker", true, $"Shared actor completed raw LED {marker.Start}; physical key and colour remain unverified.", [])
        {
            Fields = new Dictionary<string, string> { ["transport"] = _bringupTransport!, ["channel"] = "per-key-led",
                ["ledId"] = marker.Start.ToString(System.Globalization.CultureInfo.InvariantCulture), ["physicalMap"] = "unverified" },
        };
    }
    public async Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct)
    {
        Require(id); ct.ThrowIfCancellationRequested();
        await RestoreMarkerAsync(ct).ConfigureAwait(false);
        return new("end-marker", true, "The shared actor completed restoration of the captured normal scene frame.", []);
    }
    public async Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct)
    {
        Require(id); ct.ThrowIfCancellationRequested();
        await RestoreMarkerAsync(ct).ConfigureAwait(false);
        _bringupFrame = null; _bringupTransport = null;
        return new("release", true, "Marker released; shared actor retains the normal scene frame.", []);
    }
    private void RequireBringupRoute()
    {
        if (_bringupFrame is null || !_bridge.Available || _bridge.KeyFrameTransport != _bringupTransport)
            throw new DeviceBlockedException("The marker's shared lighting route is no longer available.");
    }
    private async Task RestoreMarkerAsync(CancellationToken ct)
    {
        if (!_markerActive) return;
        RequireBringupRoute();
        if (_bringupFrame!.Aggregate is { } colour) await _bridge.WriteAsync(colour, ct).ConfigureAwait(false);
        else await _bridge.WriteKeysAsync(_bringupFrame.Keys!, _bringupTransport!, ct).ConfigureAwait(false);
        _markerActive = false;
    }
    public event Action<HotplugEvent>? Hotplug;
    private void Changed(bool available) => Hotplug?.Invoke(new(_descriptor.Id, available ? HotplugKind.Arrived : HotplugKind.Removed));
    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_descriptor]); }
    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Require(id);
        if (!_bridge.Available) throw new DeviceBlockedException("Keyboard USB owner is connecting.");
        _open = true; return Task.CompletedTask;
    }
    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new NotSupportedException("Complete frame required.");
    public bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones)
    {
        Require(id); ArgumentNullException.ThrowIfNull(zones);
        if (!_open || !_bridge.Available) throw new DeviceBlockedException("Keyboard USB owner is not ready.");
        if (_perKey is { Ready: true } plan) return WriteKeys(plan, zones);
        if (zones.Count != 1 || zones[0].Length != 1) throw new ArgumentException("Only one whole-keyboard colour is supported.", nameof(zones));
        var colour = zones[0][0];
        _bridge.WriteAsync(colour, CancellationToken.None).GetAwaiter().GetResult();
        Volatile.Write(ref _lastNormalFrame, new(colour, null, _bridge.KeyFrameTransport));
        return true;
    }
    // One colour per mapped key in map order; the owner's route picks the transport, never this adapter.
    private bool WriteKeys(RogPerKeyPlan plan, IReadOnlyList<Rgb[]> zones)
    {
        var geometry = plan.Map!.Keys;
        if (zones.Count != 1 || zones[0].Length != geometry.Count) throw new ArgumentException("One colour per mapped key is required.", nameof(zones));
        var transport = _bridge.KeyFrameTransport;
        if (transport is null || !plan.RouteMaps.TryGetValue(transport, out var routeMap))
            throw new DeviceBlockedException("Keyboard lighting owner does not service per-key frames on a verified map transport.");
        var cells = routeMap.Keys;
        var indices = geometry.Select((cell, index) => (cell, index)).ToDictionary(p => (p.cell.Key, p.cell.Row, p.cell.Column), p => p.index);
        if (cells.Count != geometry.Count || cells.Any(cell => !indices.ContainsKey((cell.Key, cell.Row, cell.Column))))
            throw new DeviceBlockedException("The active route map has different physical keys or geometry; rediscover it with that route selected.");
        var colors = zones[0];
        var keys = new RogKeyColor[cells.Count];
        var frame = new PerKeyFrameCell[cells.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            var cell = cells[i];
            var color = colors[indices[(cell.Key, cell.Row, cell.Column)]];
            keys[i] = new((byte)cell.LedId, color);
            frame[i] = new((byte)cell.LedId, cell.Key, cell.Row, cell.Column, color);
        }
        _bridge.WriteKeysAsync(keys, transport, CancellationToken.None).GetAwaiter().GetResult();
        Volatile.Write(ref _lastNormalFrame, new(null, Array.AsReadOnly(keys), transport));
        Volatile.Write(ref _lastPerKeyFrame, new(Interlocked.Increment(ref _perKeyFrames), Array.AsReadOnly(frame)));
        return true;
    }
    public PerKeyFrame? ReadPerKeyFrame(ControllerId id) { Require(id); return Volatile.Read(ref _lastPerKeyFrame); }
    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
    public Telemetry? ReadTelemetry(ControllerId id) => null;
    public Task CloseAsync(ControllerId id) { Require(id); _open = false; return Task.CompletedTask; }
    public Task ReleaseForConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Dispose();
        return Task.CompletedTask;
    }
    public void Dispose() { _open = false; _bridge.AvailabilityChanged -= Changed; }
    private void Require(ControllerId id) { if (id != _descriptor.Id) throw new ArgumentException("Unknown wired keyboard.", nameof(id)); }
}

public sealed class WiredKeyboardAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.AsusRog;
    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var options = (UnifiedRgbOptions)sp.GetService(typeof(UnifiedRgbOptions))!;
        return new WiredKeyboardAdapter((RogWiredLightingBridge)sp.GetService(typeof(RogWiredLightingBridge))!,
            options.KeyboardDisplay, options.AsusRog?.KeyboardPerKey);
    }
}
