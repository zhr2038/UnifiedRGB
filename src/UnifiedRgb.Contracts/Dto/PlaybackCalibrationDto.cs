namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// The manual streamed reference-clock calibration (NATIVE-PLAYBACK-CALIBRATION C5): streamed members that share an uploaded
/// Lian Li loop's epoch sample their plan at <c>(t − T0 − PhaseTrimMs / 1000) × StreamedPlaybackRate</c>, wrapped by the plan
/// period, from the raw first-header T0. Owner-observed, manually entered host parameters referenced to the uploaded RF domain:
/// not a hardware, bus or packet setting, not a measured firmware playback clock, and not applied to pure streamed scenes,
/// local frame sets or per-device routes. Rate is finite in [0.25, 4], offset finite in [−2000, 2000] ms; defaults 1 / 0 are
/// the nominal header model (missing fields deserialize to them). A set is runtime-only and is never written to disk.
/// </summary>
public sealed record PlaybackCalibrationDto(double StreamedPlaybackRate = 1, double PhaseTrimMs = 0);
