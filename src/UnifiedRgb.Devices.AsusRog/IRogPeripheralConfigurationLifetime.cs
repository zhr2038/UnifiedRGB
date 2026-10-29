namespace UnifiedRgb.Devices.AsusRog;

public interface IRogPeripheralConfigurationLifetime
{
    Task ReleaseForConfigurationAsync(CancellationToken ct);
}
