using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

/// <summary>
/// Where a shared playback timeline (OPUS-GENERIC-SCENE-PACKAGES) stands: <see cref="PositionMs"/> in [0, <see cref="PeriodMs"/>)
/// of authored physical time, read from the same source the timeline's RGB uses. <see cref="Source"/> labels it:
/// "device-clock" (the cached RF master counter), "upload-anchor: &lt;reason&gt;" (labelled device-clock loss: the host instant the
/// loop's first header went out), "local-epoch" (streamed-only RGB) or "apply-epoch" (no RGB carrier, screens only).
/// <see cref="Blocked"/> carries the reason the timeline's content cannot play (position 0 is then a hold, not playback).
/// </summary>
public sealed record TimelinePosition(double PositionMs, double PeriodMs, string Source, string? Blocked = null)
{
    public bool IsDeviceClock => Source == TimelineSources.DeviceClock;
}

public static class TimelineSources
{
    public const string DeviceClock = "device-clock";
    public const string UploadAnchor = "upload-anchor";
    public const string LocalEpoch = "local-epoch";
    public const string ApplyEpoch = "apply-epoch";
    public const string Blocked = "blocked";
}

/// <summary>A strict loop exactly as the activation would publish it (before any adapter transform).</summary>
public sealed record StrictLoop(ControllerId Controller, ZoneId Zone, LoopFrames Loop);

/// <summary>
/// The dry-run compilation of a scene's strict content (<see cref="RenderLoop.PreflightStrict"/>): the loops it would upload and
/// every blocked reason. Nothing is published and the running scene is untouched.
/// </summary>
public sealed record StrictPreflight(IReadOnlyList<StrictLoop> Loops, IReadOnlyList<string> Blocked);
