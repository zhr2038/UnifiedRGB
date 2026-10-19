namespace UnifiedRgb.Transports.Nvapi;

/// <summary>PCI identifiers as NVAPI reports them: DeviceId = (device &lt;&lt; 16) | vendor, SubSystemId = (ssDevice &lt;&lt; 16) | ssVendor.</summary>
public readonly record struct NvPciIds(uint DeviceId, uint SubSystemId, uint RevisionId, uint ExtDeviceId)
{
    public ushort Vendor => (ushort)(DeviceId & 0xFFFF);

    public ushort Device => (ushort)(DeviceId >> 16);
}

/// <summary>
/// The complete NVAPI surface this product uses: GPU enumeration, PCI ids, I2C read/write, read-only thermal.
/// No clock, voltage, power or fan entry point exists here or in <see cref="NvapiNative"/>.
/// </summary>
public interface INvapi
{
    /// <summary>False when nvapi64.dll is absent or initialization fails.</summary>
    bool Initialize();

    IReadOnlyList<IntPtr> EnumPhysicalGpus();

    NvPciIds GetPciIdentifiers(IntPtr gpu);

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="register"/> of the 7-bit I2C <paramref name="device"/>, building
    /// NV_I2C_INFO_V3 from <paramref name="profile"/> (which must name the same device). Returns the NvAPI status verbatim (0 = OK).
    /// </summary>
    int I2cWrite(IntPtr gpu, byte device, byte register, ReadOnlySpan<byte> data, NvI2cProfile profile);

    /// <summary>
    /// Reads up to <paramref name="data"/>.Length bytes from <paramref name="register"/>; same profile rules; status verbatim.
    /// <paramref name="data"/> is cleared before the call. <paramref name="returnedSize"/> is the NV_I2C_INFO_V3 Size field as
    /// the driver left it (callers must validate it against the expected length; a short or oversized value is not data).
    /// </summary>
    int I2cRead(IntPtr gpu, byte device, byte register, Span<byte> data, NvI2cProfile profile, out int returnedSize);

    /// <summary>Read-only GPU core temperature in °C, or null.</summary>
    int? GetGpuTemperature(IntPtr gpu);
}

public sealed class NvapiException(int status, string operation)
    : InvalidOperationException($"NVAPI {operation} failed with status {status}")
{
    public int Status { get; } = status;
}
