namespace UnifiedRgb.Transports.Nvapi;

/// <summary>
/// How an NV_I2C_INFO_V3 transaction is built (NATIVE design §3.2). <see cref="MsiNv5000"/> reproduces what MSI's NDA
/// passes to NvAPI_I2CWriteEx/ReadEx for the NV5000 indexed path; <see cref="LegacyOpenRgb"/> is the OpenRGB accessor's
/// form, kept for the dormant legacy adapter and the <c>fwinfo-legacy-profile</c> diagnosis only.
/// </summary>
/// <param name="DevAddress7">7-bit device address; the struct carries it shifted left once (0x68 → 0xD0).</param>
/// <param name="SpeedField">The deprecated <c>i2cSpeed</c> field (0xFFFF = use the enum).</param>
/// <param name="SpeedKhzEnum">The <c>i2cSpeedKhz</c> enum (6 = 400 kHz, 0 = default).</param>
/// <param name="ThirdArg">Initial value of the DWORD whose address is passed as the call's third argument.</param>
/// <param name="RegAddrSize">Register address bytes.</param>
public sealed record NvI2cProfile(
    string Name,
    byte DevAddress7,
    uint SpeedField,
    uint SpeedKhzEnum,
    byte PortId,
    bool PortIdSet,
    uint ThirdArg,
    byte RegAddrSize)
{
    public static NvI2cProfile MsiNv5000 { get; } = new("MsiNv5000", 0x68, 0xFFFF, 6, 1, true, 1, 1);

    public static NvI2cProfile LegacyOpenRgb { get; } = new("LegacyOpenRgb", 0x68, 0xFFFF, 0, 1, true, 0, 1);
}
