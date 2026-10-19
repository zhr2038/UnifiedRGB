namespace UnifiedRgb.Contracts;

/// <summary>JSON-RPC notification names raised by the Host (the <see cref="IRgbService"/> event names).</summary>
public static class RgbNotifications
{
    public const string ControllersChanged = nameof(IRgbService.ControllersChanged);
    public const string StatusChanged = nameof(IRgbService.StatusChanged);
    public const string TelemetryTick = nameof(IRgbService.TelemetryTick);
}
