using UnifiedRgb.Devices.LianLi.Protocol;

namespace UnifiedRgb.Devices.LianLi.Codec;

public static class RgbAnimationEncoders
{
    /// <summary>No auto-detection, native fallback or vendor installation dependency on the managed path.</summary>
    public static IRgbAnimationEncoder Create(LianLiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Codec switch
        {
            LianLiCompressionCodec.Managed => new ManagedTinyuzEncoder(),
            LianLiCompressionCodec.VendorYuz => TinyuzEncoder.Open(Environment.ExpandEnvironmentVariables(options.YuzDllPath)),
            _ => throw new DeviceBlockedException($"Unknown Lian Li compression codec: {options.Codec}."),
        };
    }
}
