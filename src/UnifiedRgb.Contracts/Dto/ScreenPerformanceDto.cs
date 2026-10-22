namespace UnifiedRgb.Contracts.Dto;

/// <summary>Completion timings, not optical FPS. A rolling window of at most 256 successful content transactions.</summary>
public sealed record ScreenPerformanceDto(string ScreenId, string State, long FramesWritten, long Dropped, double EffectiveFps,
    int TargetFps, string? TrialId, int? DeviceSpeedCode, int Samples, long SkippedScheduleSlots,
    IReadOnlyDictionary<string, ScreenTimingDto> Stages, long ImageBytes, long PacketBytes, long RawReads, long ReadTimeouts, long ZeroLengthReplies,
    long MatchedReplies = 0, long CorrelatedTransactions = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] NativeVideoPerformanceDto? NativeVideo = null)
{
    /// <summary>
    /// HOST source-frame selection counters of every successful content submission since this screen's worker started; omitted
    /// before the first submission and by older Hosts. Independent of <see cref="Dropped"/>, which counts transport/encode failures.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScreenSourceSelectionDto? SourceSelection { get; init; }

    /// <summary>
    /// The latest (at most 256) successful submissions recorded during the current explicit lab trial (<see cref="TrialId"/>), oldest
    /// first by Sequence; omitted outside trials, and from the Host's reads as soon as that trial is no longer the screen's active
    /// one (ended, expired or replaced), even before the next submission. HOST selection/submission facts: no device presentation
    /// timestamps or optical FPS.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScreenSourceFrameDto>? SourceFrames { get; init; }
}

/// <summary>
/// Cumulative HOST source-index selection counters of one screen worker (a new worker restarts them). Each successful content
/// submission is exactly one of: NoSource (no animated source: still image, no asset, blocked timeline, reused static picture),
/// Starts (first animated selection of a continuity Generation; nothing is claimed), Advances (the next index), Repeats (the same
/// index again, e.g. a 30 FPS source sent at 60), Skips (k &gt;= 2 index steps while the source position advanced consistently with
/// host time; SkippedIndices adds k-1) or Discontinuities (backward, seek/clock jump, a source advance or host gap that may span a
/// whole loop, since a shared position is modulo the loop: the missing count is unknown and NOT added). LoopWraps: advances/skips that crossed the loop end, e.g. 959 to 0. Not device presentation.
/// </summary>
public sealed record ScreenSourceSelectionDto(long Generation, long Submissions, long NoSource, long Starts, long Advances, long Repeats,
    long Skips, long SkippedIndices, long Discontinuities, long LoopWraps);

/// <summary>
/// One successful content submission, recorded after its transfer was validated, as the HOST selected and sent it. Selection: "none",
/// "start", "advance", "repeat", "skip" or "discontinuity" (Discontinuity: "backward", "jump", "loop-gap" or "unknown"). SourceIndex
/// -1 means no source asset; SourcePositionMs is the position that selected it (shared timeline or local elapsed; 0 for a reused
/// static picture). HostIntervalMs is host monotonic time since the previous row's frame start (0 for the first). The timings are
/// this submission's host work. NOT a device presentation timestamp, decoder completion or optical FPS.
/// </summary>
public sealed record ScreenSourceFrameDto(long Sequence, long Generation, string Selection,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Discontinuity,
    bool LoopWrap, int SkippedIndices, int SourceIndex, int SourceCount, int SourceFrameMs, long SourcePositionMs, int SourceLoopMs,
    bool CacheHit, double HostIntervalMs, double PacingWaitMs, double ComposeMs, double EncodeMs, double WriteMs, double ReadMs,
    double QueueWaitMs, double OverlayComposeMs, double OverlayEncodeMs, double OverlayWriteMs, double OverlayReadMs, double WorkMs,
    int ImageBytes);

/// <summary>Host picture selection and acknowledged native transfers, NOT device presentation timestamps or optical sync proof.</summary>
public sealed record NativeVideoPerformanceDto(long AccessUnits, long OverlayWrites, int DeviceBufferStatus,
    long SourcePositionMs, int SourceFrameIndex, string SourceClock, long LoopWraps)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingNote { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OverlayEncodingNote { get; init; }

    /// <summary>Only while the active TURZX trial set PreparedOverlay; omitted otherwise and by older Hosts.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NativeVideoPreparedOverlayDto? PreparedOverlay { get; init; }

    /// <summary>Only after a measured overlay PNG of the active TURZX trial (MeasureQueueAfterOverlay); omitted otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NativeVideoOverlayQueueDto? LastOverlayQueue { get; init; }
}

/// <summary>
/// HOST facts of the explicit prepared-overlay lab path for one trial. Mode "prepared": the worker sends the exact inline overlay PNG
/// bytes once the per-worker preparer has encoded them (a newer rendered layer may be shown up to one encode later); "inline-fallback":
/// sticky for the rest of this trial after a preparer failure or no progress within 2 s, with the current overlay re-sent inline.
/// Stamps: the preparer's accepted-request stamps (LastSentStamp = newest content stamp sent). Thread/encode/allocation figures are
/// the preparer's own (managed bytes on its thread; native WIC memory excluded). Not device presentation or decode facts.
/// </summary>
public sealed record NativeVideoPreparedOverlayDto(string TrialId, string Mode,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? FallbackReason,
    long Generation, long Requests, long LastQueuedStamp, long LastSentStamp, long PreparedSends, long ClearSends, long StaleTakes,
    bool Outstanding, double OutstandingMs, string Thread, long ThreadsCreated, long Encodes, long Completed, long Failed, long Poisoned,
    long Deduplicated, long NotNewer, long JoinTimeouts, int RetainedLayers, long RetainedLayerBytes, double LastEncodeMs,
    double MaxEncodeMs, long LastAllocatedBytes, long MaxAllocatedBytes);

/// <summary>
/// The latest worker-issued overlay PNG of the native session (content or transparent clear, prepared or inline) and the ONE strict
/// queue-status query (command 122) sent right after it. ReportedBlocksBefore is the session's latest REPORTED status after its last
/// access unit (that unit's acknowledgement or, after a busy wait, its last status poll), not necessarily a raw data ACK;
/// ReportedBlocksAfter is the query's reply. Both are device-REPORTED buffer blocks; the times are HOST write/read/query times and
/// HostTimestampMs is host monotonic time since the session began. Never device pictures, decode or presentation. Queries counts
/// this session's measurement queries (their cost is control work).
/// </summary>
public sealed record NativeVideoOverlayQueueDto(long Queries, long AccessUnitsAtWrite, int ReportedBlocksBefore, int ReportedBlocksAfter,
    bool Clear, bool Prepared, int OverlayBytes, double OverlayWriteMs, double OverlayReadMs, double QueryMs, double HostTimestampMs);

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
