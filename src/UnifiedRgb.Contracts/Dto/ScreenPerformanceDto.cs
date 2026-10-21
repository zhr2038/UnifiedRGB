namespace UnifiedRgb.Contracts.Dto;

/// <summary>Completion timings, not optical FPS. A rolling window of at most 256 successful content transactions.</summary>
public sealed record ScreenPerformanceDto(string ScreenId, string State, long FramesWritten, long Dropped, double EffectiveFps,
    int TargetFps, string? TrialId, int? DeviceSpeedCode, int Samples, long SkippedScheduleSlots,
    IReadOnlyDictionary<string, ScreenTimingDto> Stages, long ImageBytes, long PacketBytes, long RawReads, long ReadTimeouts, long ZeroLengthReplies,
    long MatchedReplies = 0, long CorrelatedTransactions = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] NativeVideoPerformanceDto? NativeVideo = null);

/// <summary>Host picture selection and acknowledged native transfers, NOT device presentation timestamps or optical sync proof.</summary>
public sealed record NativeVideoPerformanceDto(long AccessUnits, long OverlayWrites, int DeviceBufferStatus,
    long SourcePositionMs, int SourceFrameIndex, string SourceClock, long LoopWraps);

public sealed record ScreenTimingDto(double MeanMs, double P50Ms, double P95Ms, double MaxMs);

/// <summary>
/// Explicit temporary lab setting: the SAME currently assigned content, 1..60 transactions/s, 1..120 seconds, at most five
/// running screens. TL read timeout 1..50ms; other families keep their full reply/drain protocol. LegacyPacing is an A/B control.
/// No saved settings, package contract, brightness, RGB or image codec changes. Expiry is monotonic and automatic.
/// </summary>
public sealed record ScreenRateTrialRequestDto(IReadOnlyList<string> ScreenIds, int Fps, int DurationMs,
    bool LegacyPacing = false, int TlReadTimeoutMs = 50, int ReplyQuietMs = 10, bool? CorrelatedReplies = null);

public sealed record ScreenRateTrialDto(string TrialId, IReadOnlyList<string> ScreenIds, int Fps, int DurationMs,
    bool LegacyPacing, int TlReadTimeoutMs, int ReplyQuietMs = 10, bool? CorrelatedReplies = null);
