namespace UnifiedRgb.Transports.Nvapi;

/// <summary>
/// ITransport over NVAPI I2C. Frame layout: <c>[device(7-bit), register, data...]</c>. Reads are not part of the
/// lighting path and are rejected. Call from one owner thread only. <paramref name="profile"/> builds the NVAPI struct
/// (default: the legacy OpenRGB form used by the dormant legacy adapter).
/// </summary>
public sealed class NvapiI2cTransport(INvapi nvapi, IntPtr gpu, NvI2cProfile? profile = null) : Devices.ITransport
{
    private readonly NvI2cProfile _profile = profile ?? NvI2cProfile.LegacyOpenRgb;

    public const int DeviceOffset = 0;
    public const int RegisterOffset = 1;

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var f = frame.Span;
        if (f.Length < 3)
        {
            throw new ArgumentException("I2C frame needs device, register and at least one data byte.", nameof(frame));
        }

        var status = nvapi.I2cWrite(gpu, f[DeviceOffset], f[RegisterOffset], f[2..], _profile);
        if (status != 0)
        {
            throw new NvapiException(status, "I2CWriteEx");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        throw new NotSupportedException("The GPU lighting path is write-only.");
}
