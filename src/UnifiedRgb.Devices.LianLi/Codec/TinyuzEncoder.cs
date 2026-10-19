using UnifiedRgb.Devices.LianLi.Protocol;

namespace UnifiedRgb.Devices.LianLi.Codec;

/// <summary>
/// <see cref="IRgbAnimationEncoder"/> over the vendor's <c>yuz.dll</c> (ARCHITECTURE-D2 §3). <see cref="Open"/> loads the
/// dll from the configured path and runs the self-check; any failure is a <see cref="DeviceBlockedException"/>.
/// </summary>
public sealed class TinyuzEncoder : IRgbAnimationEncoder, IDisposable
{
    /// <summary>Self-check input sizes: one 160-LED frame, a 50-frame and a full 200-frame SLV3 chain loop.</summary>
    public static IReadOnlyList<int> SelfCheckSizes { get; } = [480, 24_000, 96_000];

    private readonly YuzNative _native;

    private TinyuzEncoder(YuzNative native) => _native = native;

    public string Path => _native.Path;

    public string Sha256 => _native.Sha256;

    public string CodecHash => Sha256;

    /// <summary>Loads <paramref name="yuzDllPath"/> and runs <see cref="SelfCheck"/>; disposes and rethrows on failure.</summary>
    public static TinyuzEncoder Open(string? yuzDllPath)
    {
        var native = YuzNative.Load(yuzDllPath);
        var enc = new TinyuzEncoder(native);
        try
        {
            enc.SelfCheck();
            return enc;
        }
        catch
        {
            enc.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Animation-like test data, R,G,B per LED in 160-LED frames: a 40-LED (one fan) gradient scrolling by frame, with a
    /// rare deterministic sparkle. Like real effect loops it is compressible; pure noise is not (tinyuz expands it).
    /// </summary>
    public static byte[] SampleAnimation(int bytes)
    {
        var data = new byte[bytes];
        uint x = 0x9E3779B9;
        for (var i = 0; i < bytes; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            var led = i / 3 % 160;
            var frame = i / 480;
            data[i] = (x & 0xFF) == 0 ? (byte)(x >> 8) : (byte)((((led % 40) + frame) * 6) + ((i % 3) * 85));
        }

        return data;
    }

    /// <summary>
    /// D2 §3: <c>TuzDec(TuzEnc(x)) == x</c> and <c>len(TuzEnc(x)) ≤ len(x)</c> for each of <see cref="SelfCheckSizes"/>.
    /// Throws <see cref="DeviceBlockedException"/> on the first mismatch.
    /// </summary>
    public void SelfCheck()
    {
        foreach (var size in SelfCheckSizes)
        {
            var x = SampleAnimation(size);
            byte[] packed;
            byte[] back;
            try
            {
                packed = _native.Encode(x);
                back = _native.Decode(packed, x.Length);
            }
            catch (InvalidDataException ex)
            {
                throw new DeviceBlockedException($"yuz.dll self-check failed at {size} bytes: {ex.Message}", ex);
            }

            if (packed.Length > x.Length || !back.AsSpan().SequenceEqual(x))
            {
                throw new DeviceBlockedException($"yuz.dll self-check failed at {size} bytes (round trip or size).");
            }
        }
    }

    public byte[] Encode(ReadOnlySpan<byte> rgbData) => _native.Encode(rgbData);

    public byte[] Decode(ReadOnlySpan<byte> compressed, int originalLength) => _native.Decode(compressed, originalLength);

    public void Dispose() => _native.Dispose();
}
