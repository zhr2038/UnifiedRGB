using System.Collections.Concurrent;

namespace UnifiedRgb.Devices.Virtual;

/// <summary>
/// Simulated controllers for tests and GUI preview. Keeps the last frame per zone; never touches hardware.
/// </summary>
public sealed class VirtualAdapter : IDeviceAdapter
{
    private readonly Dictionary<ControllerId, ControllerDescriptor> _catalog;
    private readonly ConcurrentDictionary<ControllerId, State> _open = new();

    public VirtualAdapter()
        : this(VirtualCatalog.Default)
    {
    }

    public VirtualAdapter(IEnumerable<ControllerDescriptor> catalog) =>
        _catalog = catalog.ToDictionary(c => c.Id);

    public event Action<HotplugEvent>? Hotplug;

    public string Vendor => "Virtual";

    public DeviceClass Class => DeviceClass.Virtual;

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ControllerDescriptor>>(_catalog.Values.ToArray());

    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        var d = _catalog.TryGetValue(id, out var c) ? c : throw new ArgumentException($"Unknown virtual controller {id}", nameof(id));
        _open.TryAdd(id, new State(d));
        return Task.CompletedTask;
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        if (!_open.TryGetValue(id, out var s))
        {
            throw new InvalidOperationException($"{id} is not open");
        }

        var buf = s.Zones[zone.Value];
        if (colors.Length != buf.Length)
        {
            throw new ArgumentException($"{id}/{zone}: expected {buf.Length} LEDs, got {colors.Length}", nameof(colors));
        }

        lock (buf)
        {
            colors.CopyTo(buf);
        }

        Interlocked.Increment(ref s.Writes);

        // stands in for the wire in SIM so the diagnostics ring buffer has traffic (opcode = zone, 3 bytes per LED)
        PacketLog.Shared.Record("Virtual", zone.Value, colors.Length * 3);
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) =>
        throw new NotSupportedException("Virtual controllers have no hardware modes.");

    public Telemetry? ReadTelemetry(ControllerId id) =>
        _open.ContainsKey(id) && id.Value.StartsWith("virtual:fan", StringComparison.Ordinal)
            ? new Telemetry(DateTimeOffset.UtcNow, [1200], null)
            : null;

    public Task CloseAsync(ControllerId id)
    {
        _open.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <summary>Copy of the last frame written to a zone (preview and tests).</summary>
    public Rgb[] Snapshot(ControllerId id, ZoneId zone)
    {
        var buf = _open[id].Zones[zone.Value];
        lock (buf)
        {
            return (Rgb[])buf.Clone();
        }
    }

    public long WriteCount(ControllerId id) => _open.TryGetValue(id, out var s) ? Interlocked.Read(ref s.Writes) : 0;

    /// <summary>Test hook: simulate unplug/replug.</summary>
    public void RaiseHotplug(ControllerId id, HotplugKind kind) => Hotplug?.Invoke(new HotplugEvent(id, kind));

    private sealed class State(ControllerDescriptor d)
    {
        public readonly Rgb[][] Zones = d.Zones.Select(z => new Rgb[z.Layout.LedCount]).ToArray();
        public long Writes;
    }
}

public sealed class VirtualAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.Virtual;

    public IDeviceAdapter Create(IServiceProvider sp) => new VirtualAdapter();
}
