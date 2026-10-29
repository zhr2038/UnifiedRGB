using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Where the live overlay of a native TURZX picture goes.</summary>
internal enum VideoTelemetry
{
    /// <summary>The separate PNG layer: the default, and every picture without an overlay or outside native TURZX.</summary>
    PngLayer,

    /// <summary>Composited into the H.264 picture by an explicit BakeTelemetry trial.</summary>
    BakedTrial,

    /// <summary>Composited into the H.264 picture by <see cref="ScreensOptions.TurzxCompositeTelemetry"/> (verified screen, no trial).</summary>
    BakedConfigured,
}

internal sealed partial class ScreenWorker
{
    /// <summary>A baked picture reuses one immutable display reading for at most this long (monotonic time).</summary>
    internal static readonly TimeSpan TelemetrySamplePeriod = TimeSpan.FromSeconds(2);

    /// <summary>The overlay note of a baked trial picture: its telemetry is in the access unit and the PNG layer stays transparent.</summary>
    internal const string BakedOverlayNote = "none: telemetry composited into the H.264 picture (BakeTelemetry trial; 2 s telemetry sample)";

    /// <summary>The overlay note of a configured (normal, verified) composited picture.</summary>
    internal const string CompositedOverlayNote = "none: telemetry composited into the H.264 picture (2 s telemetry sample)";

    private sealed record TelemetrySample(ScreenAssignmentDto Assignment, VideoTelemetry Mode, string? TrialId, PcTelemetryDto? Reading, long Taken);

    private TelemetrySample? _telemetrySample;

    /// <summary>
    /// The one rule of worker and preview: an active explicit trial's own BakeTelemetry flag (so a non-baked trial can still A/B against
    /// the configured mode), else <see cref="ScreensOptions.TurzxCompositeTelemetry"/> for an individually verified screen. Only native
    /// TURZX pictures with an overlay are ever baked.
    /// </summary>
    internal static VideoTelemetry TelemetryModeOf(ScreensOptions options, ScreenFamily family, ScreenAssignmentDto assignment,
        NativeVideoTrialDto? trial, bool verified)
    {
        if (family != ScreenFamily.Turzx || assignment.Overlay == "none") return VideoTelemetry.PngLayer;
        if (trial is not null) return trial.BakeTelemetry ? VideoTelemetry.BakedTrial : VideoTelemetry.PngLayer;
        return options.TurzxCompositeTelemetry && verified ? VideoTelemetry.BakedConfigured : VideoTelemetry.PngLayer;
    }

    /// <summary>
    /// The display reading of a baked picture: taken from the unchanged collector at most once per <see cref="TelemetrySamplePeriod"/>
    /// and reused as is (its own timestamp; the renderer still gets the actual wall time, so a missing or stale reading shows an em
    /// dash). A new assignment, mode, trial or session (<see cref="ResetTelemetrySample"/>) takes a fresh reading.
    /// </summary>
    private PcTelemetryDto? SampledTelemetry(ScreenAssignmentDto assignment, VideoTelemetry mode, string? trialId)
    {
        var now = _ctx.Time.GetTimestamp();
        if (_telemetrySample is not { } sample || sample.Assignment != assignment || sample.Mode != mode
            || !string.Equals(sample.TrialId, trialId, StringComparison.Ordinal) || _ctx.Time.GetElapsedTime(sample.Taken, now) >= TelemetrySamplePeriod)
        {
            sample = new TelemetrySample(assignment, mode, trialId, _ctx.Telemetry(), now);
            _telemetrySample = sample;
        }

        return sample.Reading;
    }

    private void ResetTelemetrySample() => _telemetrySample = null;

    /// <summary>The TURZX encoding-note suffix: where this picture's telemetry is.</summary>
    private static string TelemetryNote(VideoTelemetry mode) => mode switch
    {
        VideoTelemetry.BakedTrial => "telemetry composited before encoding (BakeTelemetry trial; 2 s telemetry sample; no PNG overlay)",
        VideoTelemetry.BakedConfigured => "telemetry composited before encoding (2 s telemetry sample; no PNG overlay)",
        _ => "live PNG overlay",
    };
}
