using System.Globalization;

namespace UnifiedRgb.Core.Engine;

/// <summary>
/// NATIVE-PLAYBACK-CALIBRATION C1–C3: the manually calibrated host reference clock of streamed members that share an uploaded
/// Lian Li loop's epoch, referenced to the uploaded RF domain. Such a companion samples its shared plan at
/// <c>elapsed = (t − T0 − PhaseTrimMs / 1000) × StreamedPlaybackRate</c>, wrapped by the plan period, where T0 is the raw host
/// instant the loop's first header went out. Both values are owner-observed calibrations of the host model, never a hardware,
/// bus or packet parameter and never a measurement of the firmware's playback clock; the defaults 1 / 0 are the nominal
/// header model (frames × header interval from the first header). Pure streamed assignments, local frame-set epochs and the
/// independent per-device Vendor routes do not use it.
/// </summary>
public sealed record PlaybackCalibration(double StreamedPlaybackRate = 1, double PhaseTrimMs = 0)
{
    public const double MinRate = 0.25;

    public const double MaxRate = 4;

    public const double MinPhaseTrimMs = -2000;

    public const double MaxPhaseTrimMs = 2000;

    /// <summary>The nominal model: rate 1, offset 0 ms (source default).</summary>
    public static PlaybackCalibration Default { get; } = new();

    /// <summary>
    /// Null when both values are acceptable, else why not: the rate must be finite in [0.25, 4] and the offset finite in
    /// [−2000, 2000] ms. Nothing is clamped or rounded.
    /// </summary>
    public string? Validate()
    {
        if (!(StreamedPlaybackRate >= MinRate && StreamedPlaybackRate <= MaxRate))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"StreamedPlaybackRate {StreamedPlaybackRate} is not a finite rate in [{MinRate}, {MaxRate}]");
        }

        if (!(PhaseTrimMs >= MinPhaseTrimMs && PhaseTrimMs <= MaxPhaseTrimMs))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"PhaseTrimMs {PhaseTrimMs} is not a finite offset in [{MinPhaseTrimMs}, {MaxPhaseTrimMs}] ms");
        }

        return null;
    }
}
