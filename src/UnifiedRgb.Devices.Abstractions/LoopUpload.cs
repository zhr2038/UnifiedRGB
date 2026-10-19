namespace UnifiedRgb.Devices;

/// <summary>
/// A pre-rendered animation loop for one UploadedLoop zone (ARCHITECTURE-D2 §1): <see cref="Frames"/> frames of
/// <see cref="LedCount"/> LEDs, frame-major, R,G,B per LED (as L-Connect's <c>Util.GetRgbData</c>), played every
/// <see cref="IntervalMs"/>. All zones compiled from one scene change share <see cref="EffectIndex"/>.
/// Lives here rather than in Core/Engine because adapters may not reference Core (SELF_AUDIT M9 dev. 1).
/// </summary>
public sealed record LoopFrames
{
    public LoopFrames(int frames, int ledCount, double intervalMs, uint effectIndex, ReadOnlyMemory<byte> rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledCount);
        if (!(intervalMs > 0) || double.IsInfinity(intervalMs))
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMs));
        }

        if (rgb.Length != frames * ledCount * 3)
        {
            throw new ArgumentException($"Loop data must be frames × LEDs × 3 = {frames * ledCount * 3} bytes.", nameof(rgb));
        }

        Frames = frames;
        LedCount = ledCount;
        IntervalMs = intervalMs;
        EffectIndex = effectIndex;
        Rgb = rgb;
    }

    public int Frames { get; }

    public int LedCount { get; }

    public double IntervalMs { get; }

    public uint EffectIndex { get; }

    public ReadOnlyMemory<byte> Rgb { get; }

    public double PeriodSeconds => Frames * IntervalMs / 1000.0;

    /// <summary>
    /// WP-14 (H2-VERTICAL-FILL §5): how the engine placed this loop on a ring zone ("ring", "vertical-fill offset=+2
    /// half=false bottom=High", "ring (effect is rotational)", "calibration"); null for non-ring zones. Diagnostics only.
    /// </summary>
    public string? Geometry { get; init; }

    /// <summary>
    /// OPUS-GENERIC-SCENE-PACKAGES: strict authored content (an installed scene package). An adapter must upload it exactly as
    /// given or refuse it: no size decimation, no static fallback. False (the default) keeps every historical policy.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Same frames, interval and colours (the effect index is ignored).</summary>
    public bool SameContent(LoopFrames? other) =>
        other is not null && other.Frames == Frames && other.LedCount == LedCount && other.IntervalMs.Equals(IntervalMs)
        && other.Rgb.Span.SequenceEqual(Rgb.Span);
}

/// <summary>What the last loop upload to a controller cost (Diagnostics, D2 §7).</summary>
/// <param name="StartedTimestamp">Stopwatch timestamp of the first header send (the loop's epoch on the host).</param>
public sealed record LoopUploadStats(
    DateTimeOffset At,
    long StartedTimestamp,
    uint EffectIndex,
    int Frames,
    double IntervalMs,
    int RawBytes,
    int CompressedBytes,
    int Packets,
    TimeSpan Duration,
    int Decimations,
    string CodecSha256)
{
    /// <summary>Wire period (frames × header interval, 0.01 ms resolution) minus the engine's period; non-zero after a halving (WP-11).</summary>
    public double PeriodErrorMs { get; init; }

    /// <summary>The mounting map applied (adapter-defined, e.g. "identity", "rev 3: 180°"); null when the adapter has none.</summary>
    public string? Mapping { get; init; }

    /// <summary>Frames the engine compiled (before any size halving); 0 when the adapter does not report it (WP-12).</summary>
    public int SourceFrames { get; init; }

    /// <summary>
    /// True when the compressed loop did not fit even after every halving and only one still frame was sent: the animation
    /// did not reach the device (WP-12: reported loudly, never implied by the preview).
    /// </summary>
    public bool StaticFallback { get; init; }

    /// <summary>The engine's ring geometry tag of the uploaded loop (<see cref="LoopFrames.Geometry"/>), or null (WP-14).</summary>
    public string? Geometry { get; init; }

    public RfDeliveryStatus? ReceiverDelivery { get; init; }
}

/// <summary>Receiver generation evidence only; no state here certifies physical light playback.</summary>
public sealed record RfDeliveryStatus(string State, uint ExpectedEffectIndex, uint? ReportedEffectIndex,
    uint? ReceiverCounter, DateTimeOffset? ObservedAt, int Retries, string Detail);

/// <summary>Autonomous loop recovery must obey the SAME writer ownership/pause boundary.
/// Revoking permission waits for an already committed recovery; newly opened sessions default to denied.</summary>
public interface ILoopRecoveryControl
{
    void SetRecoveryAllowed(ControllerId id, bool allowed);
}

/// <summary>Adapter for <see cref="Delivery.UploadedLoop"/> controllers. Called only by the controller's DeviceWriter.</summary>
public interface ILoopUploadAdapter : IDeviceAdapter
{
    /// <summary>
    /// Uploads one loop. <paramref name="ct"/> aborts an upload still queued behind another on the same sender; once its
    /// first packet is sent the upload completes.
    /// </summary>
    Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct);

    /// <summary>The last completed upload to <paramref name="id"/>, or null.</summary>
    LoopUploadStats? LastUpload(ControllerId id);
}

/// <summary>OpenAsync failure that is a standing block, not a transient error (e.g. "yuz.dll missing"): the Host shows Blocked(reason).</summary>
public sealed class DeviceBlockedException : InvalidOperationException
{
    public DeviceBlockedException()
    {
        Reason = string.Empty;
    }

    public DeviceBlockedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public DeviceBlockedException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>The wire cost of one loop through an adapter's actual transform and codec, measured without any device I/O.</summary>
/// <param name="CompressedBytes">Encoded size of the loop exactly as it would be uploaded (mounting/wire order applied).</param>
/// <param name="MaxBytes">The largest encoded loop the device accepts without decimation.</param>
/// <param name="Problem">Why the loop cannot be uploaded unchanged (over budget, LED/frame/interval limits); null when it can.</param>
public sealed record LoopBudget(int CompressedBytes, int MaxBytes, string? Problem)
{
    public bool Fits => Problem is null;
}

/// <summary>
/// Optional adapter seam (OPUS-GENERIC-SCENE-PACKAGES): measures a loop through the same transform/codec path the upload uses,
/// on the CPU only, so strict content is validated before anything is committed. Never touches a device.
/// </summary>
public interface ILoopBudgetProbe
{
    LoopBudget MeasureLoop(ControllerId id, ZoneId zone, LoopFrames loop);
}
