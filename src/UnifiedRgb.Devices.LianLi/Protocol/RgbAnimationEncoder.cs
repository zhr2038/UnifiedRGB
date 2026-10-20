namespace UnifiedRgb.Devices.LianLi.Protocol;

/// <summary>
/// Compresses an uncompressed animation (<c>rgb_data</c>: frames × LEDs × R,G,B) into what the device decompresses.
/// Default: <see cref="Codec.ManagedTinyuzEncoder"/> without a native dependency. Optional vendor comparison is explicit.
/// </summary>
public interface IRgbAnimationEncoder
{
    /// <summary>Identifies the codec in Diagnostics (managed version identity, or the optional vendor DLL's SHA-256).</summary>
    string CodecHash => GetType().Name;

    byte[] Encode(ReadOnlySpan<byte> rgbData);
}
