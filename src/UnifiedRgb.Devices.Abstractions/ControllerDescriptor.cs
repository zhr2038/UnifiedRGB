namespace UnifiedRgb.Devices;

public sealed record ControllerDescriptor(
    ControllerId Id,
    string Vendor,
    string Name,
    DeviceType DeviceType,
    DeviceClass Class,
    string Serial,
    IReadOnlyList<ZoneDescriptor> Zones,
    Capabilities Capabilities)
{
    public int TotalLeds
    {
        get
        {
            var n = 0;
            foreach (var z in Zones)
            {
                n += z.LedCount;
            }

            return n;
        }
    }
}
