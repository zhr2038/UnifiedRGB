namespace UnifiedRgb.Devices.Virtual;

/// <summary>
/// Simulated rig mirroring the owner's hardware (IMPLEMENTATION_PLAN M2): 4 fan rings, 1 strip, 1 GPU point,
/// 2 DRAM sticks Linear(8), motherboard with a 2-LED onboard zone and 3 × 20-LED ARGB headers.
/// </summary>
public static class VirtualCatalog
{
    public const int DefaultMaxFps = 60;

    public static IReadOnlyList<ControllerDescriptor> Default { get; } = Build(DefaultMaxFps);

    public static IReadOnlyList<ControllerDescriptor> Build(int maxFps)
    {
        var caps = Capabilities.DirectOnly(maxFps);
        var list = new List<ControllerDescriptor>();
        for (var i = 1; i <= 4; i++)
        {
            list.Add(Controller($"virtual:fan{i}", $"Virtual Fan {i}", DeviceType.Cooler, caps, ("Ring", ZoneLayout.Ring(16))));
        }

        list.Add(Controller("virtual:strip", "Virtual Strip", DeviceType.Ledstrip, caps, ("Strip", ZoneLayout.Linear(30))));
        list.Add(Controller("virtual:gpu", "Virtual GPU", DeviceType.Gpu, caps, ("Logo", ZoneLayout.Point)));
        list.Add(Controller("virtual:dram1", "Virtual DRAM 1", DeviceType.Dram, caps, ("DRAM", ZoneLayout.Linear(8))));
        list.Add(Controller("virtual:dram2", "Virtual DRAM 2", DeviceType.Dram, caps, ("DRAM", ZoneLayout.Linear(8))));
        list.Add(Controller("virtual:mb", "Virtual Motherboard", DeviceType.Motherboard, caps,
            ("Onboard", ZoneLayout.Linear(2)),
            ("ARGB Header 1", ZoneLayout.Linear(20)),
            ("ARGB Header 2", ZoneLayout.Linear(20)),
            ("ARGB Header 3", ZoneLayout.Linear(20))));
        return list;
    }

    private static ControllerDescriptor Controller(string id, string name, DeviceType type, Capabilities caps, params (string Name, ZoneLayout Layout)[] zones) =>
        new(new ControllerId(id), "Virtual", name, type, DeviceClass.Virtual, id,
            zones.Select((z, i) => new ZoneDescriptor(new ZoneId(i), z.Name, z.Layout)).ToArray(), caps);
}
