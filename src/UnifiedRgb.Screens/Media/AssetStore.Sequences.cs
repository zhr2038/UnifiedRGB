using System.Security.Cryptography;
using System.Text.Json;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Media;

internal sealed partial class AssetStore
{
    internal const string SequenceMetaFile = "sequence.json";

    public StoredAsset CommitSequence(string name, IReadOnlyList<ScreenVideoSegment> clips, IReadOnlyList<VideoClipProbe> probes)
    {
        SegmentedVideo.ValidateInputs(clips);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl) || probes.Count != clips.Count) throw new ArgumentException("Invalid sequence name/probe count.", nameof(name));
        var id = SegmentedVideo.IdOf(clips);
        var width = probes[0].Width; var height = probes[0].Height;
        if (probes.Any(p => p.Width != width || p.Height != height)) throw new ArgumentException("All sequence clips must have the same pixel dimensions.", nameof(clips));
        for (var i = 0; i < clips.Count; i++)
            if (probes[i].DurationMs != clips[i].DurationMs) throw new ArgumentException("A clip's decoded duration differs from the authored duration.", nameof(clips));
        var meta = new VideoSequenceMeta(1, id, name, width, height, clips.Select((c, i) => new VideoClipMeta(SegmentedVideo.ClipFile(i),
            Convert.ToHexString(SHA256.HashData(c.Data)).ToLowerInvariant(), probes[i].DelaysMs, probes[i].Positions100ns)).ToArray());
        if (SegmentedVideo.MetadataProblem(meta) is { } problem) throw new ArgumentException(problem, nameof(clips));
        var metadata = JsonSerializer.SerializeToUtf8Bytes(meta, SequenceJsonContext.Default.VideoSequenceMeta);
        if (metadata.Length > SegmentedVideo.MaxMetaBytes) throw new ArgumentException("Sequence metadata exceeds its byte budget.", nameof(clips));
        var bytes = clips.Sum(c => (long)c.Data.Length) + metadata.Length;
        lock (_gate)
        {
            EnsureLoaded();
            if (_assets.TryGetValue(id, out var old)) return MatchingSequence(old, meta);
            RequireSequenceRoom(bytes);
        }
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, "~tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string? stale = null;
        try
        {
            File.WriteAllBytes(Path.Combine(temp, SequenceMetaFile), metadata);
            for (var i = 0; i < clips.Count; i++) File.WriteAllBytes(Path.Combine(temp, meta.Clips[i].File), clips[i].Data);
            var asset = SequenceAsset(meta, bytes);
            BeforeCommit?.Invoke();
            lock (_gate)
            {
                if (_assets.TryGetValue(id, out var raced)) return MatchingSequence(raced, meta);
                RequireSequenceRoom(bytes);
                var target = Path.Combine(_root, id);
                if (Directory.Exists(target))
                {
                    stale = Path.Combine(_root, "~del-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(target, stale);
                }
                Directory.Move(temp, target); _assets[id] = asset;
                return asset;
            }
        }
        finally
        {
            TryDeleteDirectory(temp);
            if (stale is not null) TryDeleteDirectory(stale);
        }
    }

    private void RequireSequenceRoom(long bytes)
    {
        if (_assets.Count >= _maxCount || bytes > _maxBytes - _assets.Values.Sum(a => a.Bytes)) throw new InvalidOperationException("The screen asset store has insufficient count/byte budget for this sequence.");
    }

    private static StoredAsset MatchingSequence(StoredAsset asset, VideoSequenceMeta expected)
    {
        if (asset.Sequence is not { } actual || actual.Clips.Length != expected.Clips.Length
            || !actual.Clips.Zip(expected.Clips).All(p => p.First.Sha256 == p.Second.Sha256 && p.First.DelaysMs.SequenceEqual(p.Second.DelaysMs))) throw new InvalidDataException("Sequence content id collides with different media.");
        return asset;
    }

    private static StoredAsset SequenceAsset(VideoSequenceMeta meta, long bytes) =>
        new(meta.Id, meta.Name, SegmentedVideo.Format, meta.Width, meta.Height, meta.Clips.SelectMany(c => c.DelaysMs).ToArray(), bytes) { Sequence = meta };

    private StoredAsset LoadSequence(string directory, string id)
    {
        var metaBytes = ReadBounded(Path.Combine(directory, SequenceMetaFile), SegmentedVideo.MaxMetaBytes);
        var meta = JsonSerializer.Deserialize(metaBytes, SequenceJsonContext.Default.VideoSequenceMeta);
        if (meta is null || meta.Id != id || SegmentedVideo.MetadataProblem(meta) is { }) throw new InvalidDataException("Invalid video sequence metadata.");
        var owned = SequenceOwned(meta);
        _ = OwnedLibraryPaths.Inventory(_root, directory, owned);
        long bytes = metaBytes.Length;
        foreach (var c in meta.Clips)
        {
            var size = new FileInfo(Path.Combine(directory, c.File)).Length;
            if (size is < 16 || size > ImportLimits.Default.MaxBytes) throw new InvalidDataException("Invalid sequence source size.");
            bytes += size;
        }
        if (bytes - metaBytes.Length > SegmentedVideo.MaxSourceBytes) throw new InvalidDataException("Sequence sources exceed their total byte budget.");
        return SequenceAsset(meta, bytes);
    }

    internal string SequencePath(StoredAsset asset, VideoClipMeta clip)
    {
        if (asset.Sequence is not { } meta || !meta.Clips.Contains(clip) || !IsValidId(asset.Id)) throw new ArgumentException("Not a stored sequence clip.", nameof(asset));
        var directory = Path.Combine(_root, asset.Id);
        _ = OwnedLibraryPaths.Inventory(_root, directory, SequenceOwned(meta));
        return Path.Combine(directory, clip.File);
    }

    private static HashSet<string> SequenceOwned(VideoSequenceMeta meta) => meta.Clips.Select(c => c.File).Append(SequenceMetaFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One compositor's STA-owned current clip decoder and one raster. Clip/loop/seek never changes the scene epoch.</summary>
internal sealed class SequenceFrameReader(AssetStore store) : IDisposable
{
    private Mp4FrameReader? _reader;
    private string? _asset;
    private int _clip = -1, _frame = -1;
    private long _origin;
    private VideoSample? _sample;

    public PixelFrame Read(StoredAsset asset, int frame)
    {
        if (asset.Sequence is not { } meta || frame < 0 || frame >= asset.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        var local = frame; var clip = 0;
        while (local >= meta.Clips[clip].DelaysMs.Length) local -= meta.Clips[clip++].DelaysMs.Length;
        var source = meta.Clips[clip];
        if (_reader is null || _asset != asset.Id || _clip != clip)
        {
            Dispose();
            _reader = new(store.SequencePath(asset, source), source.Sha256);
            if (_reader.Width != asset.Width || _reader.Height != asset.Height) { Dispose(); throw new InvalidDataException("Sequence dimensions changed."); }
            _sample = _reader.ReadNext() ?? throw new InvalidDataException("Empty sequence source.");
            _origin = _sample.Timestamp; _frame = 0; _asset = asset.Id; _clip = clip;
        }
        if (_frame == local) return _sample!.Pixels;
        var expected = checked(_origin + source.Positions100ns[local]);
        if (local < _frame || local > _frame + 120)
        {
            // Media positions are relative to the clip origin; decoded PTS may
            // include the codec/edit-list offset and use a different domain.
            _reader.Seek(source.Positions100ns[local]); _frame = -1; _sample = null;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var reads = 0; reads <= Mp4Importer.MaxFrames; reads++)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidDataException("Sequence seek exceeded its work budget.");
            _sample = _reader.ReadNext() ?? throw new InvalidDataException("Sequence ended before its declared picture.");
            if (_sample.Timestamp < expected) continue;
            if (_sample.Timestamp != expected) throw new InvalidDataException("Sequence decoder PTS differs from its validated index.");
            _frame = local; return _sample.Pixels;
        }
        throw new InvalidDataException("Sequence seek exceeded its sample budget.");
    }

    public void Dispose()
    {
        _reader?.Dispose(); _reader = null; _sample = null; _asset = null; _clip = _frame = -1;
    }
}
