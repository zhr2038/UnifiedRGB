namespace UnifiedRgb.Devices.MsiGpu;

/// <summary>One I2C register write; <see cref="ToFrame"/> gives the NvapiI2cTransport frame [device, register, data...].</summary>
public sealed record I2cWrite(byte Register, byte[] Data)
{
    public byte[] ToFrame() => [MsiGpuV2Registers.I2cAddress, Register, .. Data];

    public override string ToString() => Convert.ToHexString(ToFrame());
}

/// <summary>Pure register-sequence encoder for the MSIGPUv2 map (no I/O, fully testable).</summary>
public static class MsiGpuEncoder
{
    /// <summary>Direct mode: Static at full hardware brightness; engine brightness is applied in the OutputStage.</summary>
    public static IReadOnlyList<I2cWrite> EnterDirect() =>
    [
        new(MsiGpuV2Registers.Mode, [MsiGpuModes.Static]),
        new(MsiGpuV2Registers.Brightness, [MsiGpuV2Registers.BrightnessLevels[^1]]),
    ];

    public static I2cWrite DirectColor(Rgb c) => new(MsiGpuV2Registers.ColorBlock1, [c.R, c.G, c.B]);

    /// <summary>
    /// Hardware mode with up to 3 colors, speed 0..2 (slow→fast) and brightness 0..4. Throws on anything out of range so
    /// no unvalidated byte ever reaches the bus.
    /// </summary>
    public static IReadOnlyList<I2cWrite> FirmwareMode(FirmwareMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var info = MsiGpuModes.All.FirstOrDefault(m => m.Name == mode.Name)
            ?? throw new ArgumentException($"Unknown MSI GPU mode '{mode.Name}'.", nameof(mode));
        var colors = mode.Colors ?? [];
        if (colors.Count < info.MinColors || colors.Count > info.MaxColors)
        {
            throw new ArgumentException($"{info.Name} takes {info.MinColors}..{info.MaxColors} colors, got {colors.Count}.", nameof(mode));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(mode.Speed);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mode.Speed, MsiGpuV2Registers.SpeedLevels.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(mode.Brightness);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mode.Brightness, MsiGpuV2Registers.BrightnessLevels.Count);

        var ops = new List<I2cWrite>();
        for (var i = 0; i < colors.Count; i++)
        {
            ops.Add(new(MsiGpuV2Registers.ColorBlocks[i], [colors[i].R, colors[i].G, colors[i].B]));
        }

        if (info.HasSpeed)
        {
            ops.Add(new(MsiGpuV2Registers.Speed, [MsiGpuV2Registers.SpeedLevels[mode.Speed]]));
        }

        if (info.HasBrightness)
        {
            ops.Add(new(MsiGpuV2Registers.Brightness, [MsiGpuV2Registers.BrightnessLevels[mode.Brightness]]));
        }

        ops.Add(new(MsiGpuV2Registers.Mode, [(byte)info.Value]));
        return ops;
    }
}
