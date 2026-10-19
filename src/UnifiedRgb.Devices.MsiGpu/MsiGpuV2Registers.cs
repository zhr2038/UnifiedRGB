namespace UnifiedRgb.Devices.MsiGpu;

/// <summary>
/// MSIGPUv2 register map (OpenRGB <c>MSIGPUv2Controller</c> semantics, 01 §Option B). PROVISIONAL: the analysis pack does
/// not contain the map, so these values must be confirmed against the OpenRGB source and on hardware before
/// <see cref="MsiGpuOptions.RegisterMapVerified"/> is set; until then the adapter never opens the device.
/// There is deliberately no "save/commit to EEPROM" register: nothing here persists to the controller's flash.
/// </summary>
public static class MsiGpuV2Registers
{
    public const byte I2cAddress = 0x68;

    public const byte Mode = 0x22;
    public const byte ColorBlock1 = 0x27;
    public const byte ColorBlock2 = 0x60;
    public const byte ColorBlock3 = 0x70;
    public const byte Brightness = 0x36;
    public const byte Speed = 0x38;

    /// <summary>Every register the firewall lets through.</summary>
    public static IReadOnlyList<byte> Writable { get; } = [Mode, ColorBlock1, ColorBlock2, ColorBlock3, Brightness, Speed];

    public static IReadOnlyList<byte> ColorBlocks { get; } = [ColorBlock1, ColorBlock2, ColorBlock3];

    /// <summary>Five hardware brightness levels (20 % steps).</summary>
    public static IReadOnlyList<byte> BrightnessLevels { get; } = [0x14, 0x28, 0x3C, 0x50, 0x64];

    /// <summary>Three hardware speeds, slow → fast.</summary>
    public static IReadOnlyList<byte> SpeedLevels { get; } = [0x02, 0x01, 0x00];
}

/// <summary>Hardware lighting modes exposed through SetFirmwareModeAsync (values provisional, see MsiGpuV2Registers).</summary>
public static class MsiGpuModes
{
    public const byte Off = 0x01;
    public const byte Flashing = 0x02;
    public const byte DoubleFlashing = 0x03;
    public const byte Breathing = 0x04;
    public const byte Rainbow = 0x08;
    public const byte Static = 0x13;

    public static IReadOnlyList<FirmwareModeInfo> All { get; } =
    [
        new("Off", Off, 0, 0, false, false),
        new("Static", Static, 1, 1, false, true),
        new("Breathing", Breathing, 1, 3, true, true),
        new("Flashing", Flashing, 1, 3, true, true),
        new("DoubleFlashing", DoubleFlashing, 1, 3, true, true),
        new("Rainbow", Rainbow, 0, 0, true, true),
    ];
}
