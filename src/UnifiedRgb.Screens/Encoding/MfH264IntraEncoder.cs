using System.Runtime.InteropServices;
using UnifiedRgb.Screens.Media;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace UnifiedRgb.Screens.Encoding;

/// <summary>
/// The Windows inbox H.264 transform, synchronous low-latency/all-IDR Baseline. The shared scene clock still chooses every
/// picture: there is no autonomous media player or inter-frame dependency. STA-owned, bounded raster/output, no disk I/O.
/// Encoded access units always carry SPS/PPS and are checked for IDR-only VCL before they can reach a device.
/// </summary>
internal sealed class MfH264IntraEncoder : IDisposable
{
    internal const int MaxEncodedBytes = 2 * 1024 * 1024;
    private readonly int _width, _height;
    private readonly bool _fullRange;
    private readonly bool _singleSlice;
    private readonly IMFTransform _encoder;
    private readonly object _codecOwner;
    private readonly IMfCodecApi _codec;
    private long _index;
    private bool _disposed;
    public int LastSliceCount { get; private set; }

    public MfH264IntraEncoder(int width, int height, bool fullRange = false, bool singleSlice = false, int quality = 95, int? workerThreads = null)
    {
        if (workerThreads is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(workerThreads));
        if (quality is < 95 or > 100) throw new ArgumentOutOfRangeException(nameof(quality), "Native quality may only remain95 or increase to100.");
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 || (long)width * height > 1920L * 1920)
            throw new ArgumentOutOfRangeException(nameof(width), "H.264 needs an even, bounded raster.");
        _width = width; _height = height; _fullRange = fullRange; _singleSlice = singleSlice;
        MFStartup().CheckError();
        IMFTransform? transform = null; object? owner = null;
        try
        {
            transform = MfCodecNative.Create(new("6ca50344-051a-4ded-9779-a43305165e35")); // Windows H.264 encoder, not a vendor MFT
            owner = Marshal.GetObjectForIUnknown(transform.NativePointer);
            var codec = (IMfCodecApi)owner;
            Set(codec, "95f31b26-95a4-41aa-9303-246a7fc6eef1", (uint)1); // GOP=1
            Set(codec, "8d390aac-dc5c-4200-b57f-814d04babab2", (uint)0); // no B pictures
            Set(codec, "9c27891a-ed7a-40e1-88e8-b22727a024ee", true); // AVLowLatencyMode: one input -> one output
            Set(codec, "1c0608e9-370c-4710-8a58-cb6181c42423", (uint)3); // quality VBR
            Set(codec, "fcbf57a3-7ea5-4b0c-9644-69b40c39c391", (uint)95); // AVEncCommonQuality, legacy ordering
            // Null preserves the production encoder's automatic thread/slice selection. Golden-wire fixtures can explicitly
            // reproduce the original machine's 16 workers on a smaller CI VM without changing any golden bytes or live policy.
            if (workerThreads is { } threads) Set(codec, "b0c8bf60-16f7-4951-a30b-1db1609293d6", (uint)threads);
            if (singleSlice)
            {
                // Static slice parameters MUST be set before media types; a successful post-start setter can have no effect.
                Set(codec, "e9e782ef-5f18-44c9-a90b-e9c3c2c17b0b", (uint)2); // macroblock rows per slice
                Set(codec, "92f51df3-07a5-4172-aefe-c69ca3b60e35", (uint)((height + 15) / 16));
            }
            if (quality != 95) Set(codec, "fcbf57a3-7ea5-4b0c-9644-69b40c39c391", (uint)quality);
            using var output = Type(VideoFormatGuids.H264, width, height, fullRange);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)24_000_000).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)66).CheckError();
            transform.SetOutputType(0, output, 0);
            using var input = Type(VideoFormatGuids.NV12, width, height, fullRange);
            transform.SetInputType(0, input, 0);
            Start(transform);
            _encoder = transform; _codecOwner = owner; _codec = codec;
        }
        catch
        {
            if (owner is not null) Marshal.ReleaseComObject(owner);
            transform?.Dispose(); MFShutdown().CheckError(); throw;
        }
    }

    public byte[] Encode(PixelFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != _width || frame.Height != _height) throw new ArgumentException("H.264 raster changed.", nameof(frame));
        // The NV12 bytes are converted straight into the locked input buffer: identical bytes to ToNv12, without a managed copy.
        using var sample = Sample(checked(_width * _height * 3 / 2), _index++,
            target => Nv12Converter.Convert(frame.Pixels, _width, _height, _fullRange, target));
        Set(_codec, "398c1b98-8353-475a-9ef2-8f265d260345", (uint)1); // ForceKeyFrame is VT_UI4, NOT VT_BOOL
        _encoder.ProcessInput(0, sample, 0);
        var bytes = H264ColourMetadata.SpecifyCanvasColour(Output(_encoder, MaxEncodedBytes), _fullRange);
        var kinds = H264AnnexB.Types(bytes);
        if (!kinds.Contains(7) || !kinds.Contains(8) || !kinds.Contains(5) || kinds.Any(t => t is >= 1 and <= 4))
            throw new InvalidDataException("H.264 encoder did not produce an independently decodable SPS/PPS/IDR access unit.");
        LastSliceCount = kinds.Count(t => t == 5);
        if (_singleSlice && LastSliceCount != 1) throw new InvalidDataException("The requested single-slice encoder did not emit one complete IDR slice; nothing may be sent.");
        return bytes;
    }

    /// <summary>Preview-only decode of the exact encoded access unit, through the Windows inbox decoder.</summary>
    public static PixelFrame Decode(byte[] data, int width, int height, bool fullRange = false)
    {
        if (data.Length > MaxEncodedBytes || width < 2 || height < 2 || (long)width * height > 1920L * 1920)
            throw new ArgumentException("H.264 preview exceeds its bounds.");
        MFStartup().CheckError();
        try
        {
            using var decoder = MfCodecNative.Create(new("62ce7e72-4c71-4d20-b15d-452831a87d9d"));
            using var input = Type(VideoFormatGuids.H264, width, height, fullRange);
            decoder.SetInputType(0, input, 0);
            using var output = Type(VideoFormatGuids.NV12, width, height, fullRange);
            decoder.SetOutputType(0, output, 0);
            Start(decoder);
            using var sample = Sample(data, 0);
            decoder.ProcessInput(0, sample, 0);
            decoder.ProcessMessage((TMessageType)0x10000002, 0); // NOTIFY_END_OF_STREAM
            decoder.ProcessMessage((TMessageType)1, 0); // COMMAND_DRAIN: the single preview picture needs no successor
            byte[] bytes;
            try { bytes = Output(decoder, checked(width * height * 2)); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0xC00D6D61))
            {
                // Explicit SPS colour information may complete the decoder's output type only after the first picture.
                // Accept one bounded renegotiation, with the same raster and NV12 matrix/range used by FromNv12.
                var configured = false;
                for (var i = 0; i < 16; i++)
                {
                    using var available = decoder.GetOutputAvailableType(0, i);
                    if (available.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                    var size = available.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    if ((size >> 32) != (uint)width || (uint)size != (uint)height) throw new InvalidDataException("H.264 preview changed its raster.");
                    available.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)1).CheckError();
                    available.Set(MediaTypeAttributeKeys.VideoNominalRange, fullRange ? (uint)1 : 2).CheckError();
                    decoder.SetOutputType(0, available, 0); configured = true; break;
                }
                if (!configured) throw new InvalidDataException("H.264 decoder has no matching NV12 output.");
                bytes = Output(decoder, checked(width * height * 2));
            }
            return FromNv12(bytes, width, height, fullRange);
        }
        finally { MFShutdown().CheckError(); }
    }

    private static IMFMediaType Type(Guid subtype, int width, int height, bool fullRange)
    {
        var type = MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, 60, 1);
            MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2).CheckError();
            type.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)1).CheckError(); // BT.709
            type.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)2).CheckError();
            type.Set(MediaTypeAttributeKeys.TransferFunction, (uint)7).CheckError(); // compositor pixels are sRGB
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, fullRange ? (uint)1 : 2).CheckError(); // 0..255 or 16..235, matching the actual samples
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    private static void Start(IMFTransform transform)
    {
        transform.ProcessMessage((TMessageType)0x10000000, 0); // NOTIFY_BEGIN_STREAMING
        transform.ProcessMessage((TMessageType)0x10000003, 0); // NOTIFY_START_OF_STREAM
    }

    private delegate void SampleFill(Span<byte> target);

    private static IMFSample Sample(byte[] bytes, long index) => Sample(bytes.Length, index, bytes.CopyTo);

    private static unsafe IMFSample Sample(int length, long index, SampleFill fill)
    {
        var sample = MFCreateSample();
        try
        {
            using var buffer = MFCreateMemoryBuffer(length);
            buffer.Lock(out var pointer, out var capacity, out _);
            try
            {
                if (capacity < length) throw new InvalidDataException("Media Foundation input buffer is shorter than requested.");
                fill(new Span<byte>((void*)pointer, length));
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = length; sample.AddBuffer(buffer);
            sample.SampleTime = index * 10_000_000 / 60; sample.SampleDuration = 10_000_000 / 60;
            return sample;
        }
        catch { sample.Dispose(); throw; }
    }

    private static byte[] Output(IMFTransform transform, int maxBytes)
    {
        using var sample = MFCreateSample(); using var buffer = MFCreateMemoryBuffer(maxBytes);
        sample.AddBuffer(buffer);
        {
            // SharpGen 2.2 __MarshalFrom constructs a second IMFSample wrapper for the caller-owned pointer.
            // Keep exactly one owner of our output sample/buffer; call the fixed IMFTransform ABI without re-wrapping it.
            MfCodecNative.ProcessOutput(transform.NativePointer, sample.NativePointer);
            using var data = sample.ConvertToContiguousBuffer();
            data.Lock(out var pointer, out _, out var length);
            try
            {
                if (length < 1 || length > maxBytes) throw new InvalidDataException("H.264 transform output exceeds its buffer.");
                var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes;
            }
            finally { data.Unlock(); }
        }
    }

    private static void Set(IMfCodecApi codec, string key, object value) => Marshal.ThrowExceptionForHR(codec.SetValue(new Guid(key), ref value));

    /// <summary>The exact BT.709 NV12 bytes the encoder is fed (see <see cref="Nv12Converter"/>; even rasters only).</summary>
    internal static byte[] ToNv12(PixelFrame frame, bool fullRange = false)
    {
        var data = new byte[checked(frame.Width * frame.Height * 3 / 2)];
        Nv12Converter.Convert(frame.Pixels, frame.Width, frame.Height, fullRange, data);
        return data;
    }

    private static PixelFrame FromNv12(byte[] data, int w, int h, bool fullRange)
    {
        if (data.Length < w * h * 3L / 2) throw new InvalidDataException("Short decoded H.264 raster.");
        var frame = new PixelFrame(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var luminance = data[y * w + x]; var c = luminance - 16; var uv = w * h + y / 2 * w + (x & ~1);
            var u = data[uv] - 128; var v = data[uv + 1] - 128; var i = (y * w + x) * 4;
            frame.Pixels[i] = fullRange ? (byte)Math.Clamp(luminance + ((475 * u + 128) >> 8), 0, 255)
                : (byte)Math.Clamp((298 * c + 541 * u + 128) >> 8, 0, 255);
            frame.Pixels[i + 1] = fullRange ? (byte)Math.Clamp(luminance - ((48 * u + 120 * v + 128) >> 8), 0, 255)
                : (byte)Math.Clamp((298 * c - 55 * u - 136 * v + 128) >> 8, 0, 255);
            frame.Pixels[i + 2] = fullRange ? (byte)Math.Clamp(luminance + ((403 * v + 128) >> 8), 0, 255)
                : (byte)Math.Clamp((298 * c + 459 * v + 128) >> 8, 0, 255);
            frame.Pixels[i + 3] = 255;
        }
        return frame;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.ReleaseComObject(_codecOwner); _encoder.Dispose(); MFShutdown().CheckError();
    }
}

internal static class H264AnnexB
{
    public static IReadOnlyList<int> Types(ReadOnlySpan<byte> bytes)
    {
        var types = new List<int>();
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] != 0 || bytes[i + 1] != 0) continue;
            var prefix = bytes[i + 2] == 1 ? 3 : i + 4 < bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue;
            types.Add(bytes[i + prefix] & 31); i += prefix;
        }
        return types;
    }
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfCodecApi
{
    [PreserveSig] int IsSupported(in Guid key);
    [PreserveSig] int IsModifiable(in Guid key);
    [PreserveSig] int GetParameterRange(in Guid key, [MarshalAs(UnmanagedType.Struct)] out object min, [MarshalAs(UnmanagedType.Struct)] out object max, [MarshalAs(UnmanagedType.Struct)] out object step);
    [PreserveSig] int GetParameterValues(in Guid key, out IntPtr values, out int count);
    [PreserveSig] int GetDefaultValue(in Guid key, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetValue(in Guid key, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int SetValue(in Guid key, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

internal static partial class MfCodecNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOutputBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    public static unsafe void ProcessOutput(IntPtr transform, IntPtr sample)
    {
        var buffer = new NativeOutputBuffer { Sample = sample };
        uint status = 0;
        // IUnknown (3) + IMFTransform's 23 methods: ProcessOutput is vtable slot 25, per mftransform.h.
        var function = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, NativeOutputBuffer*, uint*, int>)(*(IntPtr**)transform)[25];
        var hr = function(transform, 0, 1, &buffer, &status);
        try
        {
            Marshal.ThrowExceptionForHR(hr);
            if (buffer.Sample != sample) throw new InvalidDataException("The H.264 transform replaced its caller-owned output sample.");
        }
        finally
        {
            if (buffer.Events != IntPtr.Zero) Marshal.Release(buffer.Events);
            if (buffer.Sample != IntPtr.Zero && buffer.Sample != sample) Marshal.Release(buffer.Sample);
        }
    }

    public static IMFTransform Create(Guid clsid)
    {
        var iid = typeof(IMFTransform).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, IntPtr.Zero, 1, in iid, out var pointer));
        return new IMFTransform(pointer);
    }
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr result);
}
