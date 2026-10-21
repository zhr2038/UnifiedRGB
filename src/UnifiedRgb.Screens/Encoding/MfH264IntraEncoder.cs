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
    private readonly IMFTransform _encoder;
    private readonly object _codecOwner;
    private readonly IMfCodecApi _codec;
    private long _index;
    private bool _disposed;

    public MfH264IntraEncoder(int width, int height)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 || (long)width * height > 1920L * 1920)
            throw new ArgumentOutOfRangeException(nameof(width), "H.264 needs an even, bounded raster.");
        _width = width; _height = height;
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
            Set(codec, "fcbf57a3-7ea5-4b0c-9644-69b40c39c391", (uint)95); // AVEncCommonQuality
            using var output = Type(VideoFormatGuids.H264, width, height);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)24_000_000).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)66).CheckError();
            transform.SetOutputType(0, output, 0);
            using var input = Type(VideoFormatGuids.NV12, width, height);
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
        var nv12 = ToNv12(frame);
        using var sample = Sample(nv12, _index++);
        Set(_codec, "398c1b98-8353-475a-9ef2-8f265d260345", (uint)1); // ForceKeyFrame is VT_UI4, NOT VT_BOOL
        _encoder.ProcessInput(0, sample, 0);
        var bytes = Output(_encoder, MaxEncodedBytes);
        var kinds = H264AnnexB.Types(bytes);
        if (!kinds.Contains(7) || !kinds.Contains(8) || !kinds.Contains(5) || kinds.Any(t => t is >= 1 and <= 4))
            throw new InvalidDataException("H.264 encoder did not produce an independently decodable SPS/PPS/IDR access unit.");
        return bytes;
    }

    /// <summary>Preview-only decode of the exact encoded access unit, through the Windows inbox decoder.</summary>
    public static PixelFrame Decode(byte[] data, int width, int height)
    {
        if (data.Length > MaxEncodedBytes || width < 2 || height < 2 || (long)width * height > 1920L * 1920)
            throw new ArgumentException("H.264 preview exceeds its bounds.");
        MFStartup().CheckError();
        try
        {
            using var decoder = MfCodecNative.Create(new("62ce7e72-4c71-4d20-b15d-452831a87d9d"));
            using var input = Type(VideoFormatGuids.H264, width, height);
            decoder.SetInputType(0, input, 0);
            using var output = Type(VideoFormatGuids.NV12, width, height);
            decoder.SetOutputType(0, output, 0);
            Start(decoder);
            using var sample = Sample(data, 0);
            decoder.ProcessInput(0, sample, 0);
            decoder.ProcessMessage((TMessageType)0x10000002, 0); // NOTIFY_END_OF_STREAM
            decoder.ProcessMessage((TMessageType)1, 0); // COMMAND_DRAIN: the single preview picture needs no successor
            var bytes = Output(decoder, checked(width * height * 2));
            return FromNv12(bytes, width, height);
        }
        finally { MFShutdown().CheckError(); }
    }

    private static IMFMediaType Type(Guid subtype, int width, int height)
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
            type.Set(MediaTypeAttributeKeys.TransferFunction, (uint)5).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)2).CheckError(); // 16..235
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    private static void Start(IMFTransform transform)
    {
        transform.ProcessMessage((TMessageType)0x10000000, 0); // NOTIFY_BEGIN_STREAMING
        transform.ProcessMessage((TMessageType)0x10000003, 0); // NOTIFY_START_OF_STREAM
    }

    private static IMFSample Sample(byte[] bytes, long index)
    {
        var sample = MFCreateSample();
        try
        {
            using var buffer = MFCreateMemoryBuffer(bytes.Length);
            buffer.Lock(out var pointer, out _, out _);
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = bytes.Length; sample.AddBuffer(buffer);
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

    internal static byte[] ToNv12(PixelFrame frame)
    {
        var w = frame.Width; var h = frame.Height; var p = frame.Pixels;
        var data = new byte[checked(w * h * 3 / 2)];
        for (var y = 0; y < h; y += 2)
        for (var x = 0; x < w; x += 2)
        {
            int sumR = 0, sumG = 0, sumB = 0;
            for (var yy = 0; yy < 2; yy++)
            for (var xx = 0; xx < 2; xx++)
            {
                var i = ((y + yy) * w + x + xx) * 4; int b = p[i], g = p[i + 1], r = p[i + 2];
                data[(y + yy) * w + x + xx] = (byte)Math.Clamp(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8), 16, 235);
                sumR += r; sumG += g; sumB += b;
            }
            var r4 = (sumR + 2) / 4; var g4 = (sumG + 2) / 4; var b4 = (sumB + 2) / 4;
            var uv = w * h + y / 2 * w + x;
            data[uv] = (byte)Math.Clamp(128 + ((-26 * r4 - 87 * g4 + 112 * b4 + 128) >> 8), 16, 240);
            data[uv + 1] = (byte)Math.Clamp(128 + ((112 * r4 - 102 * g4 - 10 * b4 + 128) >> 8), 16, 240);
        }
        return data;
    }

    private static PixelFrame FromNv12(byte[] data, int w, int h)
    {
        if (data.Length < w * h * 3L / 2) throw new InvalidDataException("Short decoded H.264 raster.");
        var frame = new PixelFrame(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = data[y * w + x] - 16; var uv = w * h + y / 2 * w + (x & ~1);
            var u = data[uv] - 128; var v = data[uv + 1] - 128; var i = (y * w + x) * 4;
            frame.Pixels[i] = (byte)Math.Clamp((298 * c + 541 * u + 128) >> 8, 0, 255);
            frame.Pixels[i + 1] = (byte)Math.Clamp((298 * c - 55 * u - 136 * v + 128) >> 8, 0, 255);
            frame.Pixels[i + 2] = (byte)Math.Clamp((298 * c + 459 * v + 128) >> 8, 0, 255);
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
