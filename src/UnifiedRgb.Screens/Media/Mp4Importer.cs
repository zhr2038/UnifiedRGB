using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace UnifiedRgb.Screens.Media;

/// <summary>
/// Windows H.264/MP4 import, normalized to lossless frames with their presentation times. No audio playback, URLs, external
/// executables or vendor codecs. One importer STA owns its reader; only one decoded raster and the bounded PNG list are held.
/// The old GIF limits/short-delay rules are unchanged. MP4 sample delays are timestamps, not GIF centiseconds.
/// </summary>
internal static class Mp4Importer
{
    public const int MaxFrames = 1800;
    public static bool IsMp4(ReadOnlySpan<byte> data) => data.Length >= 16 && data.Slice(4, 4).SequenceEqual("ftyp"u8);

    public static ImportResult Import(byte[] data, ImportLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsMp4(data) || data.Length > limits.MaxBytes) throw new ArgumentException("An MP4 must fit the upload byte budget.", nameof(data));
        ValidateContainer(data);
        var directory = Path.Combine(Path.GetTempPath(), "UnifiedRgb-video-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "input.mp4");
        try
        {
            File.WriteAllBytes(path, data);
            return Decode(path, limits, ct);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException or DllNotFoundException)
        {
            throw new ArgumentException("Windows Media Foundation could not decode this H.264 SDR MP4. Check the media file and the Windows media components.", nameof(data), ex);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static ImportResult Decode(string path, ImportLimits limits, CancellationToken ct)
    {
        MFStartup().CheckError();
        try
        {
            using var attributes = MFCreateAttributes(2);
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
            using var reader = MFCreateSourceReaderFromURL(path, attributes);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            if (native.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.H264)
                throw new ArgumentException("MP4 currently supports H.264/AVC SDR video only; audio is not played.");
            // Content orientation is not the local screen mounting transform. Do not silently drop metadata or rotate twice.
            if (native.GetUInt32(MediaTypeAttributeKeys.VideoRotation, out var rotation).Success && rotation != 0)
                throw new ArgumentException("MP4 rotation metadata is not supported yet; export upright pixels with rotation metadata cleared. Local screen orientation is unchanged.");
            if (native.GetUInt64(MediaTypeAttributeKeys.PixelAspectRatio, out var aspect).Success
                && ((aspect >> 32) == 0 || (uint)aspect == 0 || (aspect >> 32) != (uint)aspect))
                throw new ArgumentException("MP4 needs square pixels; export a 1:1 pixel aspect ratio.");
            var size = native.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            var width = checked((int)(size >> 32)); var height = checked((int)(size & uint.MaxValue));
            if (width < 48 || height < 48 || (long)width * height > limits.MaxRasterPixels)
                throw new ArgumentException("MP4 needs at least 48x48 pixels and must fit the decode raster budget.");
            // BT.2020/PQ/HLG and high-bit-depth profiles need explicit colour management, not a silent SDR conversion.
            if ((native.GetUInt32(MediaTypeAttributeKeys.VideoPrimaries, out var primaries).Success && primaries == 9)
                || (native.GetUInt32(MediaTypeAttributeKeys.TransferFunction, out var transfer).Success && transfer is 15 or 16 or 18)
                || (native.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile, out var profile).Success && profile is not (0 or 66 or 77 or 88 or 100)))
                throw new ArgumentException("HDR/wide-gamut or high-bit-depth MP4 is not supported; export H.264 SDR 8-bit.");
            using var output = MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
            using var actual = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            var stride = actual.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var strideValue).Success ? unchecked((int)strideValue) : checked(width * 4);
            if (Math.Abs((long)stride) < (long)width * 4 || Math.Abs((long)stride) > (long)width * 4 + 4096)
                throw new ArgumentException("Unsupported MP4 decoder stride.");
            var fit = Math.Min(1.0, limits.NormalizeBox / (double)Math.Max(width, height));
            var targetW = Math.Max(1, (int)Math.Round(width * fit)); var targetH = Math.Max(1, (int)Math.Round(height * fit));
            var frames = new List<byte[]>(); var positions = new List<long>();
            long bytes = 0, finalDuration = 0; long? origin = null; var clock = Stopwatch.StartNew();
            for (var reads = 0; reads <= MaxFrames * 3; reads++)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(90)) throw new ArgumentException("MP4 normalization exceeded the 90 second work budget.");
                using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
                if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0) throw new ArgumentException("MP4 changes its video format mid-stream.");
                if (sample is not null)
                {
                    origin ??= timestamp; // MF can expose the codec/edit-list origin; preserve relative PTS, start the first picture at zero
                    var position = checked(timestamp - origin.Value);
                    if (frames.Count >= MaxFrames || position < 0 || position > limits.MaxDurationMs * 10_000L
                        || (positions.Count > 0 && position <= positions[^1]))
                        throw new ArgumentException("MP4 needs increasing presentation times, at most 30 seconds and 1800 frames.");
                    using var buffer = sample.ConvertToContiguousBuffer();
                    buffer.Lock(out var pointer, out _, out var length);
                    var raster = new PixelFrame(width, height);
                    try
                    {
                        if (length < (long)Math.Abs(stride) * (height - 1) + width * 4L) throw new InvalidDataException("MP4 decoder returned a short raster.");
                        for (var y = 0; y < height; y++)
                        {
                            var offset = stride < 0 ? (height - 1 - y) * -stride : y * stride;
                            Marshal.Copy(pointer + offset, raster.Pixels, y * raster.Stride, raster.Stride);
                        }
                    }
                    finally { buffer.Unlock(); }
                    PixelOps.Flatten(raster); // RGB32's unused alpha byte is not transparency
                    var png = WpfCodec.EncodePng(width == targetW && height == targetH ? raster : PixelOps.Resize(raster, targetW, targetH));
                    bytes += png.Length;
                    if (bytes > limits.MaxNormalizedBytes) throw new ArgumentException("MP4 normalized frames exceed the 256 MiB asset budget.");
                    frames.Add(png); positions.Add(position);
                    finalDuration = sample.SampleDuration;
                }
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (reads == MaxFrames * 3) throw new ArgumentException("MP4 source exceeded the bounded read count.");
            }
            if (frames.Count < 2) throw new ArgumentException("An MP4 needs at least two decoded frames.");
            if (finalDuration <= 0) finalDuration = positions[^1] - positions[^2];
            var end = checked(positions[^1] + finalDuration);
            if (end > limits.MaxDurationMs * 10_000L) throw new ArgumentException("MP4 lasts more than 30 seconds.");
            var delays = new int[frames.Count];
            for (var i = 0; i < delays.Length; i++)
            {
                var startMs = (long)Math.Round(positions[i] / 10_000.0);
                var endMs = (long)Math.Round((i + 1 < positions.Count ? positions[i + 1] : end) / 10_000.0);
                delays[i] = checked((int)(endMs - startMs));
                if (delays[i] is < 1 or > 30000) throw new ArgumentException("Unsupported MP4 presentation interval.");
            }
            return new("mp4", targetW, targetH, frames, delays);
        }
        finally { MFShutdown().CheckError(); }
    }

    // A local MP4 may still contain external data references. Never let the Windows resolver follow a network/file URL.
    private static void ValidateContainer(ReadOnlySpan<byte> data)
    {
        Walk(data, 0);
        static void Walk(ReadOnlySpan<byte> boxes, int depth)
        {
            if (depth > 10) throw new ArgumentException("MP4 box nesting exceeds the import budget.");
            for (var offset = 0; offset < boxes.Length;)
            {
                if (boxes.Length - offset < 8) throw new ArgumentException("Truncated MP4 box.");
                var header = 8; var size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(boxes[offset..]);
                if (size == 1)
                {
                    if (boxes.Length - offset < 16) throw new ArgumentException("Truncated MP4 extended box.");
                    size = checked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(boxes[(offset + 8)..])); header = 16;
                }
                else if (size == 0) size = boxes.Length - offset;
                if (size < header || size > boxes.Length - offset) throw new ArgumentException("Invalid MP4 box size.");
                var type = boxes.Slice(offset + 4, 4); var payload = boxes.Slice(offset + header, (int)size - header);
                if (type.SequenceEqual("tkhd"u8))
                {
                    // Some Windows MP4 sources omit MF_MT_VIDEO_ROTATION even with a non-identity track matrix.
                    // Refuse that container transform before decoding rather than displaying silently misoriented pixels.
                    if (payload.Length < 4 || payload[0] > 1) throw new ArgumentException("Unsupported MP4 track header.");
                    var matrixOffset = payload[0] == 0 ? 40 : 52;
                    if (payload.Length < matrixOffset + 36) throw new ArgumentException("Truncated MP4 track matrix.");
                    ReadOnlySpan<int> identity = [65536, 0, 0, 0, 65536, 0, 0, 0, 1073741824];
                    for (var i = 0; i < identity.Length; i++)
                        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload[(matrixOffset + i * 4)..]) != identity[i])
                            throw new ArgumentException("MP4 rotation metadata or track transform is not supported yet; export upright pixels with the track matrix cleared. Local screen orientation is unchanged.");
                }
                else if (type.SequenceEqual("url "u8) || type.SequenceEqual("urn "u8))
                {
                    if (payload.Length != 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload) != 1)
                        throw new ArgumentException("MP4 external data references are refused.");
                }
                else if (type.SequenceEqual("dref"u8))
                {
                    if (payload.Length < 8) throw new ArgumentException("Truncated MP4 data references.");
                    Walk(payload[8..], depth + 1);
                }
                else if (type.SequenceEqual("moov"u8) || type.SequenceEqual("trak"u8) || type.SequenceEqual("mdia"u8)
                    || type.SequenceEqual("minf"u8) || type.SequenceEqual("dinf"u8) || type.SequenceEqual("stbl"u8)) Walk(payload, depth + 1);
                offset += (int)size;
            }
        }
    }
}
