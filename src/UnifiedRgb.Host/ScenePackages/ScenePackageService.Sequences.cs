using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Host.ScenePackages;

public sealed partial class ScenePackageService
{
    private static ScreenVideoSegment[] SequenceClips(ScreenTrack track, Func<string, byte[]> read)
        => track.Segments.Select(s => new ScreenVideoSegment(read(s.File), s.DurationMs)).ToArray();

    private async Task<(ScenePackageMediaRecord, byte[])> ProbeSequenceAsync(ScreenTrack track, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct)
    {
        if (_media is null) throw ScenePackageFormat.Bad("this Host has no screen subsystem");
        try
        {
            var info = await _media.ProbeSequenceAsync(SequenceClips(track, path => files[path]), ct).ConfigureAwait(false);
            if (info.DurationMs != track.DurationMs) throw ScenePackageFormat.Bad($"screen '{track.Role}' sequence duration differs from its decoded clips (never retimed)");
            return (new ScenePackageMediaRecord(track.Role, info.Format, info.Width, info.Height, info.Frames, info.DurationMs), info.ThumbnailPng);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw ScenePackageFormat.Bad($"screen '{track.Role}' segmented media is not accepted ({ex.Message})");
        }
    }
}
