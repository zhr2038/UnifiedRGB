namespace UnifiedRgb.Core.Engine;

/// <summary>The reference clock an assignment's streamed members were sampled from on the last rendered frame (NATIVE-RF-CLOCK).</summary>
public enum StreamedClockMode
{
    /// <summary>Master time t: pure streamed content, or a per-device route whose streamed members render independently.</summary>
    MasterTime,

    /// <summary>A local frame-set epoch (played frames with no uploaded companion).</summary>
    LocalEpoch,

    /// <summary>The legacy upload anchor (first-header T0 with the manual calibration); also every device-clock fallback.</summary>
    UploadAnchor,

    /// <summary>The phase of the uploaded loops' device counter (experimental vendor-bridge model; not a playback ACK).</summary>
    DeviceClock,

    /// <summary>Only uploaded loops: no streamed member samples a clock.</summary>
    LoopsOnly,
}

/// <summary>
/// Diagnostics of one assignment's streamed reference clock. <see cref="FallbackReason"/> is set whenever the mode is
/// <see cref="StreamedClockMode.UploadAnchor"/>. Sample fields describe the first uploaded loop's bound clock (null when there
/// is none): <see cref="RawTicks"/> is the last validated reading, <see cref="ExtrapolatedTicks"/> a host extrapolation.
/// <see cref="NominalPeriodMs"/> is the old header model (frames × interval as ms); <see cref="DerivedPeriodMs"/> is the period
/// the device-clock model derives (frames × interval ticks ÷ ticks/s), not a measured receiver period. <see cref="ReferenceFrame"/>
/// is the frame the model predicts the reference loop shows, set only in <see cref="StreamedClockMode.DeviceClock"/>.
/// <see cref="UploadedFrames"/> / <see cref="UploadedInterval"/> are from the adapter's completed-upload report.
/// </summary>
public sealed record StreamedClockReport(string Assignment, StreamedClockMode Mode, string? FallbackReason)
{
    public string? Domain { get; init; }

    public uint? RawTicks { get; init; }

    public double? SampleAgeSeconds { get; init; }

    public double? QueryLatencyMs { get; init; }

    public double TicksPerSecond { get; init; }

    public double? ExtrapolatedTicks { get; init; }

    public int ReferenceFrames { get; init; }

    public double ReferenceInterval { get; init; }

    public double NominalPeriodMs { get; init; }

    public double? DerivedPeriodMs { get; init; }

    public bool WirePeriodsDiffer { get; init; }

    public int? ReferenceFrame { get; init; }

    /// <summary>
    /// The reference loop's frames / wire interval as the completed upload of its current effect index actually transmitted
    /// (after adapter decimation); null until reported. DeviceClock mode requires them to equal the compiled reference.
    /// </summary>
    public int? UploadedFrames { get; init; }

    public double? UploadedInterval { get; init; }
}
