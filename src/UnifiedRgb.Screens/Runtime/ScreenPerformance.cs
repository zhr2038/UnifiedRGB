using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

internal sealed record ScreenRateTrial(ScreenRateTrialDto Info, long Started, long Deadline);

internal readonly record struct ScreenFrameWork(double Compose, double Encode, double Packet, double Write, double Read,
    double Wait, double Interval, double Work, int ImageBytes, int PacketBytes, long RawReads, long Timeouts, long Zlp,
    double FirstRead = 0, double Drain = 0, int Matched = 0, bool Correlated = false);

/// <summary>Fixed-size numeric ring. Sorting/DTO allocation only occurs on an explicit diagnostic read, never per-frame.</summary>
internal sealed class ScreenPerformance
{
    private readonly object _gate = new();
    private readonly ScreenFrameWork[] _ring = new ScreenFrameWork[256];
    private int _count;
    private int _next;
    private long _skipped;
    private int _target;
    private string? _trial;
    private int? _speed;

    public void Add(ScreenFrameWork sample, int target, string? trial, int? speed, long skipped)
    {
        lock (_gate)
        {
            _ring[_next] = sample; _next = (_next + 1) % _ring.Length; _count = Math.Min(_count + 1, _ring.Length);
            _skipped += skipped; _target = target; _trial = trial; _speed = speed;
        }
    }

    public ScreenPerformanceDto Snapshot(ScreenDeviceDto device)
    {
        ScreenFrameWork[] samples;
        long skipped; int target; string? trial; int? speed;
        lock (_gate) { samples = _ring.AsSpan(0, _count).ToArray(); skipped = _skipped; target = _target; trial = _trial; speed = _speed; }
        var stages = new Dictionary<string, ScreenTimingDto>(StringComparer.Ordinal)
        {
            ["compose"] = Of(samples.Select(s => s.Compose)), ["encode"] = Of(samples.Select(s => s.Encode)),
            ["packet"] = Of(samples.Select(s => s.Packet)), ["write"] = Of(samples.Select(s => s.Write)),
            ["readDrain"] = Of(samples.Select(s => s.Read)), ["wait"] = Of(samples.Select(s => s.Wait)),
            ["interval"] = Of(samples.Select(s => s.Interval).Where(v => v > 0)), ["work"] = Of(samples.Select(s => s.Work)),
            ["firstRead"] = Of(samples.Select(s => s.FirstRead)), ["replyDrain"] = Of(samples.Select(s => s.Drain)),
        };
        return new(device.Id, device.State, device.FramesWritten, device.Dropped, device.EffectiveFps, target, trial, speed,
            samples.Length, skipped, stages, samples.Sum(s => (long)s.ImageBytes), samples.Sum(s => (long)s.PacketBytes),
            samples.Sum(s => s.RawReads), samples.Sum(s => s.Timeouts), samples.Sum(s => s.Zlp), samples.Sum(s => (long)s.Matched), samples.Count(s => s.Correlated));
    }

    private static ScreenTimingDto Of(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 ? new(0, 0, 0, 0) : new(a.Average(), a[(a.Length - 1) / 2], a[(int)Math.Ceiling(a.Length * .95) - 1], a[^1]);
    }
}
