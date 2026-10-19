namespace UnifiedRgb.Devices;

/// <summary>Plugin contract implemented once per vendor. Adapters never see the effect engine.</summary>
public interface IDeviceAdapter
{
    string Vendor { get; }

    DeviceClass Class { get; }

    Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct);

    Task OpenAsync(ControllerId id, CancellationToken ct);

    /// <summary>Called only by the controller's DeviceWriter thread.</summary>
    void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors);

    /// <summary>Optional: hardware lighting mode. Adapters without modes throw NotSupportedException.</summary>
    Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct);

    /// <summary>Optional read-only telemetry; null when unsupported.</summary>
    Telemetry? ReadTelemetry(ControllerId id);

    Task CloseAsync(ControllerId id);

    event Action<HotplugEvent>? Hotplug;
}

public interface IDeviceAdapterFactory
{
    DeviceClass Class { get; }

    IDeviceAdapter Create(IServiceProvider sp);
}

/// <summary>
/// Marks an adapter whose discovery itself drives hardware (e.g. launching a process that probes SMBus). AdapterHost asks
/// WriterGuard before calling <see cref="IDeviceAdapter.DiscoverAsync"/> on such adapters, not only before OpenAsync.
/// </summary>
public interface IActiveDiscovery
{
}
