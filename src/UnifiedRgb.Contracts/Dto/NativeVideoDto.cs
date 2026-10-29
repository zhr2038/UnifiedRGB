namespace UnifiedRgb.Contracts.Dto;

/// <summary>Temporary shared-clock H.264 transport, 1..60 FPS/1..60 seconds; optional existing media of the SAME loop duration.</summary>
public sealed record NativeVideoTrialRequestDto(string ScreenId, int DurationMs = 10000, int Fps = 30, string? AssetId = null)
{
    /// <summary>Bounded TURZX experiment only; defaults preserve the deployed encoder and are never persisted.</summary>
    public bool SingleSlice { get; init; }
    /// <summary>Omitted means legacy95; nullable preserves that meaning with old clients and generated JSON readers.</summary>
    public int? Quality { get; init; }
    /// <summary>Explicit bounded TURZX-only lossless PNG repack experiment; never a saved/default device policy.</summary>
    public bool SimpleOverlayPng { get; init; }
    /// <summary>
    /// Explicit bounded TURZX-only lab experiment: the worker's exact overlay PNG is prepared off its thread and sent when ready
    /// (sticky inline fallback on failure or no progress). Default false keeps the inline overlay; omitted from JSON when false.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreparedOverlay { get; init; }
    /// <summary>
    /// Explicit bounded TURZX-only lab measurement: one strict queue-status query right after each worker-issued overlay PNG,
    /// reported as device-REPORTED buffer blocks and host times. Default false sends no query; omitted from JSON when false.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool MeasureQueueAfterOverlay { get; init; }
    /// <summary>
    /// Explicit bounded TURZX-only temporary trial: the live overlay is composited into the H.264 picture instead of the separate PNG
    /// layer (no per-update PNG writes, no encoded-picture cache). Default false keeps the PNG overlay; omitted from JSON when false.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BakeTelemetry { get; init; }
}
public sealed record NativeVideoTrialDto(string TrialId, string ScreenId, int DurationMs, int Fps, string? AssetId)
{
    public bool SingleSlice { get; init; }
    public int? Quality { get; init; }
    public bool SimpleOverlayPng { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreparedOverlay { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool MeasureQueueAfterOverlay { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BakeTelemetry { get; init; }
}
