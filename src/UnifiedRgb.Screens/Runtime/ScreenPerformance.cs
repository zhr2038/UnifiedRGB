using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

internal sealed record ScreenRateTrial(ScreenRateTrialDto Info, long Started, long Deadline);

internal readonly record struct ScreenFrameWork(double Compose, double Encode, double Packet, double Write, double Read,
    double Wait, double Interval, double Work, int ImageBytes, int PacketBytes, long RawReads, long Timeouts, long Zlp,
    double FirstRead = 0, double Drain = 0, int Matched = 0, bool Correlated = false,
    NativeVideoWork Native = default, double OverlayCompose = 0, double OverlayEncode = 0);

/// <summary>
/// Fixed-size numeric rings. Sorting/DTO allocation only occurs on an explicit diagnostic read, never per-frame. The source trace
/// records the SAME successful submission as its timing sample, under one lock; a change of trial identity empties its row window
/// and starts a new continuity generation, so trial rows never mix with or are compared against other rows.
/// </summary>
internal sealed class ScreenPerformance
{
    private readonly object _gate = new();
    private readonly ScreenFrameWork[] _ring = new ScreenFrameWork[256];
    private readonly ScreenSourceTrace _source = new();
    private int _count;
    private int _next;
    private long _skipped;
    private int _target;
    private string? _trial;
    private int? _speed;

    public void Add(ScreenFrameWork sample, int target, string? trial, int? speed, long skipped, ScreenSourceSample source = default)
    {
        lock (_gate)
        {
            _ring[_next] = sample; _next = (_next + 1) % _ring.Length; _count = Math.Min(_count + 1, _ring.Length);
            if (!string.Equals(trial, _trial, StringComparison.Ordinal))
            {
                _source.Clear();
                source = source with { NewGeneration = true };
            }
            _source.Record(source, sample);
            _skipped += skipped; _target = target; _trial = trial; _speed = speed;
        }
    }

    /// <summary>
    /// Direct-test seam (unchanged): the per-submission rows of the last sampled trial whenever one was sampled, whether or not that
    /// trial is still active. Production reads use <see cref="Snapshot(ScreenDeviceDto, string?)"/>.
    /// </summary>
    public ScreenPerformanceDto Snapshot(ScreenDeviceDto device) => Read(device, null, anyTrial: true);

    /// <summary>
    /// The production read: the rows only while the sampled trial is still the screen's ACTIVE one (<paramref name="activeTrialId"/>,
    /// the worker's own native-video-then-rate precedence), so an ended, expired or replaced trial's rows disappear at once, before
    /// any further sample. TrialId, the cumulative selection summary and the timing window are unchanged; reading clears nothing.
    /// </summary>
    public ScreenPerformanceDto Snapshot(ScreenDeviceDto device, string? activeTrialId) => Read(device, activeTrialId, anyTrial: false);

    private ScreenPerformanceDto Read(ScreenDeviceDto device, string? activeTrialId, bool anyTrial)
    {
        ScreenFrameWork[] samples;
        long skipped; int target; string? trial; int? speed;
        ScreenSourceSelectionDto? selection; ScreenSourceRow[]? rows;
        lock (_gate)
        {
            samples = _ring.AsSpan(0, _count).ToArray(); skipped = _skipped; target = _target; trial = _trial; speed = _speed;
            selection = _source.Submissions == 0 ? null : _source.Summary();
            rows = trial is null || !(anyTrial || string.Equals(trial, activeTrialId, StringComparison.Ordinal))
                ? null : _source.Rows(); // the large per-submission trace only for an explicit (active) lab trial
        }
        var stages = new Dictionary<string, ScreenTimingDto>(StringComparer.Ordinal)
        {
            ["compose"] = Of(samples.Select(s => s.Compose)), ["encode"] = Of(samples.Select(s => s.Encode)),
            ["packet"] = Of(samples.Select(s => s.Packet)), ["write"] = Of(samples.Select(s => s.Write)),
            ["readDrain"] = Of(samples.Select(s => s.Read)), ["wait"] = Of(samples.Select(s => s.Wait)),
            ["interval"] = Of(samples.Select(s => s.Interval).Where(v => v > 0)), ["work"] = Of(samples.Select(s => s.Work)),
            ["firstRead"] = Of(samples.Select(s => s.FirstRead)), ["replyDrain"] = Of(samples.Select(s => s.Drain)),
            ["videoDataWrite"] = Of(samples.Select(s => s.Native.DataWrite)), ["videoDataRead"] = Of(samples.Select(s => s.Native.DataRead)),
            ["videoOverlayWrite"] = Of(samples.Select(s => s.Native.OverlayWrite)), ["videoOverlayRead"] = Of(samples.Select(s => s.Native.OverlayRead)),
            ["videoControlWrite"] = Of(samples.Select(s => s.Native.ControlWrite)), ["videoControlRead"] = Of(samples.Select(s => s.Native.ControlRead)),
            ["videoQueueSleep"] = Of(samples.Select(s => s.Native.QueueSleep)), ["videoOverlayCompose"] = Of(samples.Select(s => s.OverlayCompose)),
            ["videoOverlayEncode"] = Of(samples.Select(s => s.OverlayEncode)),
        };
        return new(device.Id, device.State, device.FramesWritten, device.Dropped, device.EffectiveFps, target, trial, speed,
            samples.Length, skipped, stages, samples.Sum(s => (long)s.ImageBytes), samples.Sum(s => (long)s.PacketBytes),
            samples.Sum(s => s.RawReads), samples.Sum(s => s.Timeouts), samples.Sum(s => s.Zlp), samples.Sum(s => (long)s.Matched), samples.Count(s => s.Correlated))
        {
            SourceSelection = selection,
            SourceFrames = rows is null ? null : Array.ConvertAll(rows, ScreenSourceTrace.ToDto),
        };
    }

    private static ScreenTimingDto Of(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 ? new(0, 0, 0, 0) : new(a.Average(), a[(a.Length - 1) / 2], a[(int)Math.Ceiling(a.Length * .95) - 1], a[^1]);
    }
}
