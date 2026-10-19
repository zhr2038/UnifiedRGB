namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// Where a shared playback timeline stands (OPUS-GENERIC-SCENE-PACKAGES), as the Host's timeline provider reports it: authored
/// physical milliseconds within the loop, the labelled source ("device-clock", "upload-anchor: ...", "local-epoch",
/// "apply-epoch") and, when the timeline's content cannot play, why (the screen then sends an opaque black frame without overlay,
/// labelled BLOCKED; never a held content frame, a background substitute or a local clock).
/// </summary>
public sealed record ScreenTimelineSample(double PositionMs, string Source, string? Blocked = null);

/// <summary>
/// The optional shared clock of screen assignments that carry a <c>TimelineId</c>. Implementations read cached state only:
/// a worker calls it once per composed frame and it must never perform USB or other device I/O. Null = timeline unknown.
/// </summary>
public interface IScreenTimeline
{
    ScreenTimelineSample? Position(string timelineId);
}
