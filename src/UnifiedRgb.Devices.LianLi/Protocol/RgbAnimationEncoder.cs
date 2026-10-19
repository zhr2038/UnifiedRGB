namespace UnifiedRgb.Devices.LianLi.Protocol;

/// <summary>
/// Compresses an uncompressed animation (<c>rgb_data</c>: frames × LEDs × R,G,B) into what the device decompresses.
/// L-Connect's <c>Util.CompressYuz</c> P/Invokes <c>yuz.dll!TuzEnc</c> (tinyuz). M9: <see cref="Codec.TinyuzEncoder"/> loads
/// the vendor's own dll from the configured path (ARCHITECTURE-D2 §3); tests may inject a stand-in to exercise the framing.
/// </summary>
public interface IRgbAnimationEncoder
{
    /// <summary>Identifies the codec in Diagnostics (TinyuzEncoder: the dll's SHA-256).</summary>
    string CodecHash => GetType().Name;

    byte[] Encode(ReadOnlySpan<byte> rgbData);
}
