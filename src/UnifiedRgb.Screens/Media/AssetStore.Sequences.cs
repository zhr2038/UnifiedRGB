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

/// <summary>One compositor's STA-owned current clip decoder and bounded 8-frame sliding ring. Clip/loop/seek never changes the scene epoch.</summary>
internal sealed class SequenceFrameReader(AssetStore store) : IDisposable
{
    internal const int RecentRingCapacity = 8;

    private sealed class RingSlot
    {
        public string? AssetId;
        public int GlobalFrame = -1;
        public PixelFrame? Pixels;
    }

    private readonly RingSlot[] _ring = [.. Enumerable.Range(0, RecentRingCapacity).Select(_ => new RingSlot())];
    private int _ringWrite;
    private Mp4FrameReader? _reader;
    private string? _asset;
    private int _clip = -1, _frame = -1;
    private long _origin;

    internal long SeekCount { get; private set; }
    internal long RingHits { get; private set; }

    public PixelFrame Read(StoredAsset asset, int frame)
    {
        if (asset.Sequence is not { } meta || frame < 0 || frame >= asset.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame));

        if (TryGetRecent(asset.Id, frame, out var cached))
        {
            RingHits++;
            return cached;
        }

        var local = frame;
        var clip = 0;
        while (local >= meta.Clips[clip].DelaysMs.Length)
        {
            local -= meta.Clips[clip++].DelaysMs.Length;
        }

        var clipGlobalStart = frame - local;
        var source = meta.Clips[clip];

        try
        {
            if (_reader is null || _asset != asset.Id || _clip != clip)
            {
                var sameAsset = string.Equals(_asset, asset.Id, StringComparison.Ordinal);
                CloseReader();
                if (!sameAsset)
                {
                    ResetRing(asset.Width, asset.Height);
                }

                _reader = new(store.SequencePath(asset, source), source.Sha256);
                if (_reader.Width != asset.Width || _reader.Height != asset.Height)
                {
                    throw new InvalidDataException("Sequence dimensions changed.");
                }

                var slot0 = AcquireRingBuffer(asset.Width, asset.Height);
                var first = _reader.ReadNext(long.MinValue, slot0) ?? throw new InvalidDataException("Empty sequence source.");
                CommitRingSlot(asset.Id, clipGlobalStart, slot0);
                _origin = first.Timestamp;
                _frame = 0;
                _asset = asset.Id;
                _clip = clip;
                if (local == 0)
                {
                    return slot0;
                }
            }

            if (local < _frame || local > _frame + 150)
            {
                // Media positions are relative to the clip origin; decoded PTS may
                // include the codec/edit-list offset and use a different domain.
                _reader.Seek(source.Positions100ns[local]);
                _frame = -1;
                SeekCount++;
            }

            var expected = checked(_origin + source.Positions100ns[local]);
            if (_frame >= 0 && local == _frame + 1)
            {
                var targetBuf = AcquireRingBuffer(asset.Width, asset.Height);
                var sample = _reader.ReadNext(expected, targetBuf) ?? throw new InvalidDataException("Sequence ended before its declared picture.");
                if (sample.Timestamp == expected)
                {
                    CommitRingSlot(asset.Id, frame, targetBuf);
                    _frame = local;
                    return targetBuf;
                }

                if (sample.Timestamp > expected)
                {
                    throw new InvalidDataException("Sequence decoder PTS differs from its validated index.");
                }
            }

            var captureStartLocal = Math.Max(0, local - (RecentRingCapacity - 1));
            var minCopyPts = checked(_origin + source.Positions100ns[captureStartLocal]);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var reads = 0; reads <= Mp4Importer.MaxFrames; reads++)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(2))
                {
                    throw new InvalidDataException("Sequence seek exceeded its work budget.");
                }

                var sample = _reader.ReadNext(minCopyPts) ?? throw new InvalidDataException("Sequence ended before its declared picture.");
                if (sample.Timestamp < minCopyPts)
                {
                    continue;
                }

                if (sample.Timestamp < expected)
                {
                    var sampleLocal = Array.BinarySearch(source.Positions100ns, sample.Timestamp - _origin);
                    if (sampleLocal >= 0)
                    {
                        var intermediateBuf = AcquireRingBuffer(asset.Width, asset.Height);
                        intermediateBuf.CopyFrom(sample.Pixels);
                        CommitRingSlot(asset.Id, clipGlobalStart + sampleLocal, intermediateBuf);
                    }

                    continue;
                }

                if (sample.Timestamp != expected)
                {
                    throw new InvalidDataException("Sequence decoder PTS differs from its validated index.");
                }

                var finalBuf = AcquireRingBuffer(asset.Width, asset.Height);
                finalBuf.CopyFrom(sample.Pixels);
                CommitRingSlot(asset.Id, frame, finalBuf);
                _frame = local;
                return finalBuf;
            }

            throw new InvalidDataException("Sequence seek exceeded its sample budget.");
        }
        catch
        {
            CloseReader();
            ResetRing(0, 0);
            throw;
        }
    }

    private bool TryGetRecent(string assetId, int globalFrame, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PixelFrame? pixels)
    {
        foreach (var slot in _ring)
        {
            if (slot.GlobalFrame == globalFrame && slot.Pixels is not null && string.Equals(slot.AssetId, assetId, StringComparison.Ordinal))
            {
                pixels = slot.Pixels;
                return true;
            }
        }

        pixels = null;
        return false;
    }

    private PixelFrame AcquireRingBuffer(int width, int height)
    {
        var slot = _ring[_ringWrite];
        slot.AssetId = null;
        slot.GlobalFrame = -1;
        if (slot.Pixels is not { Width: var w, Height: var h } || w != width || h != height)
        {
            slot.Pixels = new PixelFrame(width, height);
        }

        return slot.Pixels;
    }

    private void CommitRingSlot(string assetId, int globalFrame, PixelFrame pixels)
    {
        var slot = _ring[_ringWrite];
        slot.AssetId = assetId;
        slot.GlobalFrame = globalFrame;
        slot.Pixels = pixels;
        _ringWrite = (_ringWrite + 1) % RecentRingCapacity;
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
        _asset = null;
        _clip = _frame = -1;
    }

    private void ResetRing(int keepWidth, int keepHeight)
    {
        foreach (var slot in _ring)
        {
            slot.AssetId = null;
            slot.GlobalFrame = -1;
            if (keepWidth <= 0 || keepHeight <= 0 || slot.Pixels is not { Width: var w, Height: var h } || w != keepWidth || h != keepHeight)
            {
                slot.Pixels = null;
            }
        }
    }

    public void Dispose()
    {
        CloseReader();
        ResetRing(0, 0);
    }
}
