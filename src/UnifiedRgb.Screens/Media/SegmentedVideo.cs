using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace UnifiedRgb.Screens.Media;

/// <summary>One independently bounded original MP4 and its authored duration; no client filesystem path.</summary>
public sealed record ScreenVideoSegment(byte[] Data, int DurationMs)
{
    /// <summary>Content identity shared by package-reference protection and the original-media store.</summary>
    public static string SequenceId(IReadOnlyList<(string Sha256, int DurationMs)> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Count is < 2 or > SegmentedVideo.MaxClips || clips.Sum(c => (long)c.DurationMs) > SegmentedVideo.MaxDurationMs) throw new ArgumentException("Invalid sequence identity bounds.", nameof(clips));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("UnifiedRgb.Mp4Sequence.v1\n"u8);
        foreach (var clip in clips)
        {
            if (clip.DurationMs is < 1 or > 30_000 || clip.Sha256 is not { Length: 64 }
                || !clip.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("Invalid sequence clip identity.", nameof(clips));
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes(clip.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(Convert.FromHexString(clip.Sha256));
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
    }
}

internal sealed record VideoClipMeta(string File, string Sha256, int[] DelaysMs, long[] Positions100ns);
internal sealed record VideoSequenceMeta(int Version, string Id, string Name, int Width, int Height, VideoClipMeta[] Clips);
internal sealed record VideoClipProbe(int Width, int Height, int[] DelaysMs, long[] Positions100ns, byte[] Thumbnail)
{
    public int DurationMs => DelaysMs.Sum();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VideoSequenceMeta))]
internal sealed partial class SequenceJsonContext : JsonSerializerContext;

internal static class SegmentedVideo
{
    public const string Format = "mp4-sequence";
    public const int MaxClips = 32;
    public const int MaxDurationMs = 600_000;
    public const int MaxFrames = 36_000;
    public const int MaxMetaBytes = 512 * 1024;
    public const int MaxSourceBytes = 64 * 1024 * 1024;

    public static string IdOf(IReadOnlyList<ScreenVideoSegment> clips)
    {
        ValidateInputs(clips);
        return ScreenVideoSegment.SequenceId(clips.Select(c => (Convert.ToHexString(SHA256.HashData(c.Data)).ToLowerInvariant(), c.DurationMs)).ToArray());
    }

    public static void ValidateInputs(IReadOnlyList<ScreenVideoSegment> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Count is < 2 or > MaxClips) throw new ArgumentException($"A video sequence needs 2..{MaxClips} clips.", nameof(clips));
        long totalMs = 0, bytes = 0;
        foreach (var clip in clips)
        {
            if (clip?.Data is not { Length: > 0 } || clip.Data.Length > ImportLimits.Default.MaxBytes
                || clip.DurationMs is < 1 or > 30_000 || !Mp4Importer.IsMp4(clip.Data))
                throw new ArgumentException("Every sequence clip must be a bounded H.264 MP4 of at most 30 seconds.", nameof(clips));
            totalMs += clip.DurationMs; bytes += clip.Data.Length;
        }
        if (totalMs > MaxDurationMs || bytes > MaxSourceBytes) throw new ArgumentException("The video sequence exceeds its 600 second/64 MiB source budget.", nameof(clips));
    }

    public static VideoClipProbe Probe(byte[] data, string? scratch, CancellationToken ct)
    {
        if (data.Length is < 1 || data.Length > ImportLimits.Default.MaxBytes) throw new ArgumentException("MP4 exceeds the per-clip upload budget.", nameof(data));
        var directory = Path.Combine(scratch ?? Path.Combine(Path.GetTempPath(), "UnifiedRgb-video-probe"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "clip.mp4");
        try
        {
            File.WriteAllBytes(path, data);
            using var reader = new Mp4FrameReader(path, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
            var positions = new List<long>();
            byte[]? thumb = null; long origin = 0, finalHold = 0;
            var watch = Stopwatch.StartNew();
            for (var reads = 0; reads <= Mp4Importer.MaxFrames * 3; reads++)
            {
                ct.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new ArgumentException("Sequence clip probe exceeded its 90 second work budget.");
                var sample = reader.ReadNext();
                if (sample is null) break;
                if (positions.Count == 0) origin = sample.Timestamp;
                var relative = sample.Timestamp - origin;
                if (positions.Count >= Mp4Importer.MaxFrames || relative < 0 || relative > 300_000_000L
                    || (positions.Count > 0 && relative <= positions[^1])) throw new ArgumentException("A sequence clip requires increasing PTS and at most 1800 frames/30 seconds.");
                positions.Add(relative); finalHold = sample.Duration;
                if (thumb is null)
                {
                    var (w, h) = PixelOps.FitSize(reader.Width, reader.Height, 160, 160, cover: false);
                    thumb = WpfCodec.EncodePng(PixelOps.Resize(sample.Pixels, w, h));
                }
            }
            if (positions.Count < 2) throw new ArgumentException("A sequence clip needs at least two decoded pictures.");
            if (finalHold <= 0) finalHold = positions[^1] - positions[^2];
            var end = checked(positions[^1] + finalHold);
            if (end > 300_000_000L) throw new ArgumentException("A sequence clip lasts more than 30 seconds.");
            var delays = new int[positions.Count];
            for (var i = 0; i < delays.Length; i++)
            {
                delays[i] = checked((int)(Math.Round((i + 1 < positions.Count ? positions[i + 1] : end) / 10_000.0) - Math.Round(positions[i] / 10_000.0)));
                if (delays[i] is < 1 or > 30_000) throw new ArgumentException("Unsupported clip presentation interval.");
            }
            return new(reader.Width, reader.Height, delays, [.. positions], thumb!);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            throw new ArgumentException("Windows could not decode a sequence H.264 SDR clip.", nameof(data), ex);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    public static string? MetadataProblem(VideoSequenceMeta m)
    {
        if (m.Version != 1 || !AssetStore.IsValidId(m.Id) || string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 64 || m.Name.Any(char.IsControl)
            || m.Width is < 48 or > 1920 || m.Height is < 48 or > 1920 || m.Clips is not { Length: >= 2 and <= MaxClips }) return "invalid sequence metadata";
        long total = 0; var frames = 0;
        for (var i = 0; i < m.Clips.Length; i++)
        {
            var c = m.Clips[i];
            if (c is null || c.File != ClipFile(i) || c.Sha256 is not { Length: 64 } || !c.Sha256.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')
                || c.DelaysMs is not { Length: >= 2 and <= Mp4Importer.MaxFrames } || c.Positions100ns is null || c.Positions100ns.Length != c.DelaysMs.Length
                || c.DelaysMs.Any(d => d is < 1 or > 30_000) || c.DelaysMs.Sum(d => (long)d) > 30_000 || c.Positions100ns[0] != 0) return "invalid clip metadata";
            for (var j = 1; j < c.Positions100ns.Length; j++)
                if (c.Positions100ns[j] <= c.Positions100ns[j - 1] || c.Positions100ns[j] > 300_000_000L
                    || Math.Round(c.Positions100ns[j] / 10_000.0) - Math.Round(c.Positions100ns[j - 1] / 10_000.0) != c.DelaysMs[j - 1]) return "invalid clip PTS";
            total += c.DelaysMs.Sum(d => (long)d); frames += c.DelaysMs.Length;
        }
        if (total > MaxDurationMs || frames > MaxFrames) return "sequence duration/frame budget exceeded";
        return ScreenVideoSegment.SequenceId(m.Clips.Select(c => (c.Sha256, c.DelaysMs.Sum())).ToArray()) != m.Id ? "sequence identity differs from clip order/duration" : null;
    }

    public static string ClipFile(int i) => "c" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".mp4";
}

internal sealed record VideoSample(PixelFrame Pixels, long Timestamp, long Duration);

/// <summary>STA-owned Windows reader. Holds one decoded raster; originals remain hash-pinned and write-locked while open.</summary>
internal sealed class Mp4FrameReader : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly FileStream _lease;
    private IMFSourceReader? _reader;
    private bool _startup, _ended, _havePicture;
    private int _stride;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Mp4FrameReader(string path, string expectedHash)
    {
        _lease = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (_lease.Length is < 16 || _lease.Length > ImportLimits.Default.MaxBytes) throw new InvalidDataException("Sequence source exceeds its file budget.");
            var bytes = new byte[checked((int)_lease.Length)]; _lease.ReadExactly(bytes);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Sequence source hash mismatch.");
            Mp4Importer.ValidateContainer(bytes);
            MFStartup().CheckError(); _startup = true;
            using var attributes = MFCreateAttributes(2);
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
            _reader = MFCreateSourceReaderFromURL(path, attributes);
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using var native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            if (native.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.H264) throw new ArgumentException("Sequence clips require H.264 SDR.");
            var size = native.GetUInt64(MediaTypeAttributeKeys.FrameSize); Width = checked((int)(size >> 32)); Height = checked((int)(size & uint.MaxValue));
            if (Width is < 48 or > 1920 || Height is < 48 or > 1920) throw new ArgumentException("Sequence clips must be 48..1920 pixels per axis; export at the device canvas size.");
            if ((native.GetUInt32(MediaTypeAttributeKeys.VideoRotation, out var rotation).Success && rotation != 0)
                || (native.GetUInt64(MediaTypeAttributeKeys.PixelAspectRatio, out var aspect).Success && ((aspect >> 32) == 0 || (uint)aspect == 0 || (aspect >> 32) != (uint)aspect))) throw new ArgumentException("Sequence clips require upright square pixels.");
            if ((native.GetUInt32(MediaTypeAttributeKeys.VideoPrimaries, out var p).Success && p == 9)
                || (native.GetUInt32(MediaTypeAttributeKeys.TransferFunction, out var t).Success && t is 15 or 16 or 18)
                || (native.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile, out var profile).Success && profile is not (0 or 66 or 77 or 88 or 100))) throw new ArgumentException("HDR/high-bit-depth video is not supported.");
            using var output = MFCreateMediaType(); output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError(); output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
            using var actual = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            _stride = actual.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var value).Success ? unchecked((int)value) : checked(Width * 4);
            if (Math.Abs((long)_stride) < Width * 4L || Math.Abs((long)_stride) > Width * 4L + 4096) throw new ArgumentException("Unsupported sequence decoder stride.");
        }
        catch { Dispose(); throw; }
    }

    public VideoSample? ReadNext()
    {
        AssertOwner();
        if (_ended) return null;
        for (var reads = 0; reads < 8; reads++)
        {
            using var sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                // Some decoders finish their RGB output negotiation on the
                // first read (also after seeking). That notification alone is
                // not a changed source canvas. Accept only the same validated
                // RGB32 geometry/stride before any picture; later changes block.
                using var actual = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                var size = actual.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                var stride = actual.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var value).Success ? unchecked((int)value) : checked(Width * 4);
                if (_havePicture || actual.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.Rgb32
                    || (int)(size >> 32) != Width || (int)(size & uint.MaxValue) != Height || stride != _stride)
                    throw new InvalidDataException("Sequence clip changes its validated RGB32 format.");
            }
            if ((flags & SourceReaderFlag.EndOfStream) != 0) _ended = true;
            if (sample is null) { if (_ended) return null; continue; }
            using var buffer = sample.ConvertToContiguousBuffer(); buffer.Lock(out var pointer, out _, out var length);
            var raster = new PixelFrame(Width, Height);
            try
            {
                if (length < Math.Abs((long)_stride) * (Height - 1) + Width * 4L) throw new InvalidDataException("Sequence decoder returned a short raster.");
                for (var y = 0; y < Height; y++) Marshal.Copy(pointer + (_stride < 0 ? (Height - 1 - y) * -_stride : y * _stride), raster.Pixels, y * raster.Stride, raster.Stride);
            }
            finally { buffer.Unlock(); }
            PixelOps.Flatten(raster);
            _havePicture = true;
            return new(raster, timestamp, sample.SampleDuration);
        }
        throw new InvalidDataException("Sequence decoder returned too many empty samples.");
    }

    public void Seek(long timestamp100ns)
    {
        AssertOwner(); _reader!.SetCurrentPosition(timestamp100ns); _ended = false; _havePicture = false;
    }

    private void AssertOwner()
    {
        ObjectDisposedException.ThrowIf(_reader is null, this);
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("A video decoder must stay on its owner STA.");
    }

    public void Dispose()
    {
        _reader?.Dispose(); _reader = null;
        if (_startup) { MFShutdown().CheckError(); _startup = false; }
        _lease.Dispose();
    }
}
