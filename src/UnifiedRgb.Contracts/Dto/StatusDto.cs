namespace UnifiedRgb.Contracts.Dto;

public sealed record ClockStatusDto(int TargetFps, double Fps, long Ticks, long Overruns, double JitterP50Ms, double JitterP99Ms, double JitterMaxMs);

/// <summary>
/// Per-device writer statistics. <c>Dropped</c> counts frames the engine failed to deliver (write errors);
/// <c>Superseded</c> counts frames replaced in the latest-wins mailbox before the writer took them (by design when MaxFps &lt; clock fps).
/// Native classes (NATIVE design §9) add complete-frame timing: <c>FrameMsP50/P99</c>, <c>EffectiveFps</c> (the cap the send path
/// enforces), <c>FpsSource</c> ("measured" or "configured ceiling") and <c>BudgetSkips</c> (frames refused by that cap).
/// </summary>
public sealed record DeviceStatusDto(
    string Id,
    string Name,
    string State,
    string? BlockedBy,
    double Fps,
    int MaxFps,
    long Written,
    long Dropped,
    long Superseded,
    string? LastError,
    LoopUploadDto? LastUpload = null,
    double FrameMsP50 = 0,
    double FrameMsP99 = 0,
    int EffectiveFps = 0,
    string? FpsSource = null,
    long BudgetSkips = 0);

/// <summary>
/// The last loop upload of an UploadedLoop controller (Lian Li, ARCHITECTURE-D2 §7/§8): size, packet count, duration and the
/// codec's identity (managed version, or the optional vendor DLL's SHA-256; legacy field name retained). For these
/// controllers <c>Written</c> counts uploads and <c>Superseded</c> coalesced loops.
/// </summary>
public sealed record LoopUploadDto(
    DateTimeOffset At,
    uint EffectIndex,
    int Frames,
    double IntervalMs,
    int RawBytes,
    int CompressedBytes,
    int Packets,
    double DurationMs,
    int Decimations,
    string CodecSha256,
    double PeriodErrorMs = 0,
    string? Mapping = null,
    int SourceFrames = 0,
    bool StaticFallback = false,
    string? Geometry = null,
    RfDeliveryDto? ReceiverDelivery = null)
{
    /// <summary>Wire period actually played: frames × header interval.</summary>
    public double PeriodMs => Math.Round(Frames * IntervalMs, 2);

    /// <summary>
    /// Composition of the host-written payload (WP-12), not a receiver ACK: "complete", "decimated N→M" (period kept, frames dropped) or the loud
    /// "STATIC FALLBACK: N frames → 1 still frame" when even the halved loop did not fit the compressed budget.
    /// </summary>
    public string Fidelity => StaticFallback
        ? $"STATIC FALLBACK: {SourceFrames} frames → 1 still frame"
        : SourceFrames > Frames ? $"decimated {SourceFrames}→{Frames}" : "complete";
}

/// <summary>Receiver generation evidence, separate from USB writes and optical playback acceptance.</summary>
public sealed record RfDeliveryDto(string State, uint ExpectedEffectIndex, uint? ReportedEffectIndex,
    uint? ReceiverCounter, DateTimeOffset? ObservedAt, int Retries, string Detail);

/// <summary>One packet in the diagnostics ring buffer (opcode and length only; never payload).</summary>
public sealed record PacketDto(DateTimeOffset Timestamp, string Controller, int Opcode, int Length);

/// <summary>
/// Host status. <c>PlaybackCalibration</c> is the manual streamed reference-clock calibration in force (see
/// <see cref="PlaybackCalibrationDto"/>): a manually entered host model, not a measurement; the nominal loop Frames / Interval /
/// Period of <see cref="LoopUploadDto"/> are unchanged by it. Optional: null from an older host, ignored by an older client.
/// <c>RfClock</c> (NATIVE-RF-CLOCK) reports the streamed reference clock per assignment; equally optional.
/// <c>BlockedContent</c> (OPUS-GENERIC-SCENE-PACKAGES) lists why strict package content of the running scene cannot play (missing
/// or corrupt package, missing binding snapshot, ...): those zones stay dark, never a substitute show. Null (not written) when
/// nothing is blocked, so the legacy JSON is unchanged.
/// </summary>
public sealed record StatusDto(
    DateTimeOffset Timestamp,
    string Version,
    double UptimeSeconds,
    long FramesRendered,
    ClockStatusDto Clock,
    IReadOnlyList<DeviceStatusDto> Devices,
    IReadOnlyList<PacketDto>? Packets = null,
    IReadOnlyList<WriterCandidateDto>? WriterCandidates = null,
    PlaybackCalibrationDto? PlaybackCalibration = null,
    RfClockStatusDto? RfClock = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BlockedContent = null);
