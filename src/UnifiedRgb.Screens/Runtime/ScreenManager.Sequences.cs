using System.Security.Cryptography;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

public sealed partial class ScreenManager
{
    // STA-owned facts only; source bytes and decoded rasters are never retained here.
    private readonly Dictionary<string, VideoClipProbe> _sequenceProbes = new(StringComparer.Ordinal);
    private long _sequenceProbeBytes;
    private const long SequenceProbeCacheBytes = 16 * 1024 * 1024;

    public async Task<ScreenMediaInfo> ProbeSequenceAsync(IReadOnlyList<ScreenVideoSegment> clips, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frozen = FreezeSequence(clips);
        var facts = await ProbeSequenceClipsAsync(frozen, ct).ConfigureAwait(false);
        return new(SegmentedVideo.Format, facts[0].Width, facts[0].Height, facts.Sum(p => p.DelaysMs.Length), facts.Sum(p => p.DurationMs), facts[0].Thumbnail);
    }

    public async Task<string> InstallPackageSequenceAsync(string name, IReadOnlyList<ScreenVideoSegment> clips, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Any(char.IsControl)) throw new ArgumentException("Invalid sequence asset name.", nameof(name));
        var frozen = FreezeSequence(clips);
        var id = SegmentedVideo.IdOf(frozen);
        if (_assets.Find(id) is { Sequence: not null }) return id;
        _assets.RequireRoomForOne();
        var facts = await ProbeSequenceClipsAsync(frozen, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var asset = await Task.Run(() => _assets.CommitSequence(name, frozen, facts), ct).ConfigureAwait(false);
        LogUploaded(_log, asset.Id, asset.Kind, asset.Width, asset.Height, asset.FrameCount, asset.Bytes);
        return asset.Id;
    }

    private static ScreenVideoSegment[] FreezeSequence(IReadOnlyList<ScreenVideoSegment> clips)
    {
        SegmentedVideo.ValidateInputs(clips);
        return clips.Select(c => new ScreenVideoSegment((byte[])c.Data.Clone(), c.DurationMs)).ToArray();
    }

    private Task<VideoClipProbe[]> ProbeSequenceClipsAsync(ScreenVideoSegment[] clips, CancellationToken ct)
        => _importThread.Value.InvokeAsync(() =>
        {
            var result = new VideoClipProbe[clips.Length];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var scratch = Path.Combine(_stateRoot, "screens", "scratch", "sequence-probe");
            for (var i = 0; i < clips.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromMinutes(2)) throw new ArgumentException("Sequence validation exceeded its two minute work budget.");
                var hash = Convert.ToHexString(SHA256.HashData(clips[i].Data));
                if (!_sequenceProbes.TryGetValue(hash, out var probe))
                {
                    probe = SegmentedVideo.Probe(clips[i].Data, scratch, ct);
                    var size = probe.DelaysMs.Length * 12L + probe.Thumbnail.Length;
                    if (_sequenceProbes.Count < 128 && size <= SequenceProbeCacheBytes - _sequenceProbeBytes)
                    {
                        _sequenceProbes.Add(hash, probe); _sequenceProbeBytes += size;
                    }
                }
                if (probe.DurationMs != clips[i].DurationMs) throw new ArgumentException($"Clip {i} decodes to {probe.DurationMs} ms, not {clips[i].DurationMs} ms (never retimed).");
                if (i > 0 && (probe.Width != result[0].Width || probe.Height != result[0].Height)) throw new ArgumentException("Sequence clip dimensions differ.");
                result[i] = probe;
            }
            if (result.Sum(p => (long)p.DelaysMs.Length) > SegmentedVideo.MaxFrames) throw new ArgumentException("Sequence frame count exceeds its bounded index.");
            return result;
        }, ct);
}
