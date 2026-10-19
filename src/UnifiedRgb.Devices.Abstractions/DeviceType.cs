namespace UnifiedRgb.Devices;

/// <summary>Device type, numbered exactly like Lian Li's / OpenRGB's DeviceType (analysis 05 §4).</summary>
public enum DeviceType
{
    Motherboard = 0,
    Dram = 1,
    Gpu = 2,
    Cooler = 3,
    Ledstrip = 4,
    Keyboard = 5,
    Mouse = 6,
    Mousemat = 7,
    Headset = 8,
    HeadsetStand = 9,
    Gamepad = 10,
    Light = 11,
    Speaker = 12,
    Virtual = 13,
    Unknown = 14,
}

/// <summary>Device class used for writer arbitration (WriterGuard) and takeover toggles.</summary>
public enum DeviceClass
{
    LianLi,
    MsiGpu,
    OpenRgb,
    Virtual,

    /// <summary>ASUS Aura mainboard over native HID (NATIVE-2).</summary>
    AsusAura,

    /// <summary>ENE DRAM over native SMBus through PawnIO (NATIVE-3).</summary>
    EneDram,
}
