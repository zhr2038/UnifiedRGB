using UnifiedRgb.Transports.OpenRgb;

namespace UnifiedRgb.Devices.OpenRgbSdk;

/// <summary>
/// Defence in depth behind openrgb-headless.json: even if the headless instance detects more than intended, only the ASUS
/// motherboard (Aura) and ENE DRAM controllers are ever exposed or written. GPUs, Lian Li and every other type are ignored.
/// </summary>
public static class DetectorAllowList
{
    public static bool IsAllowed(OpenRgbControllerData controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return (DeviceType)controller.Type switch
        {
            DeviceType.Motherboard => controller.Vendor.Contains("ASUS", StringComparison.OrdinalIgnoreCase),
            DeviceType.Dram => string.Equals(controller.Vendor, "ENE", StringComparison.OrdinalIgnoreCase)
                || controller.Name.StartsWith("ENE ", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
