using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

public sealed partial class ScreenManager : IScreenPerformanceService
{
    private ScreenRateTrial? _rateTrial;

    private ScreenRateTrial? TrialOf(string id)
    {
        var trial = Volatile.Read(ref _rateTrial);
        return trial is not null && _platform.Time.GetTimestamp() < trial.Deadline
            && trial.Info.ScreenIds.Contains(id, StringComparer.Ordinal) ? trial : null;
    }

    private void AbortTrialOf(string id)
    {
        var trial = Volatile.Read(ref _rateTrial);
        if (trial is not null && trial.Info.ScreenIds.Contains(id, StringComparer.Ordinal))
            Interlocked.CompareExchange(ref _rateTrial, null, trial); // a failed high-rate trial never repeats through backoff
    }

    public Task<IReadOnlyList<ScreenPerformanceDto>> GetScreenPerformanceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ScreenWorker[] workers;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed || _stopped, this); workers = [.. _workers.Values]; }
        return Task.FromResult<IReadOnlyList<ScreenPerformanceDto>>(workers.Select(w => w.Performance).ToArray());
    }

    public Task<ScreenRateTrialDto> BeginScreenRateTrialAsync(ScreenRateTrialRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (request.Fps is < 1 or > 60 || request.DurationMs is < 1000 or > 120000 || request.TlReadTimeoutMs is < 1 or > 50 || request.ReplyQuietMs is < 1 or > 10
            || request.ScreenIds is not { Count: >= 1 and <= 5 } || request.ScreenIds.Distinct(StringComparer.Ordinal).Count() != request.ScreenIds.Count)
            throw new ArgumentException("A rate trial needs 1..5 distinct running screens, 1..60 FPS, 1..120 seconds and a TL read bound of 1..50 ms.", nameof(request));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopped, this);
            if (_paused || (_rateTrial is { } active && _platform.Time.GetTimestamp() < active.Deadline))
                throw new InvalidOperationException("Screens are paused, or another rate trial is still active.");
            foreach (var id in request.ScreenIds)
            {
                if (!_workers.TryGetValue(id, out var w) || w.Snapshot().State != ScreenStates.Running
                    || AssignmentFor(id) is not { Enabled: true })
                    throw new InvalidOperationException("A trial may only change the pacing of already-running assigned screens.");
            }
            var now = _platform.Time.GetTimestamp();
            var info = new ScreenRateTrialDto(Guid.NewGuid().ToString("N"), request.ScreenIds.ToArray(), request.Fps,
                request.DurationMs, request.LegacyPacing, request.TlReadTimeoutMs, request.ReplyQuietMs, request.CorrelatedReplies);
            Volatile.Write(ref _rateTrial, new(info, now, now + (long)(request.DurationMs * _platform.Time.TimestampFrequency / 1000.0)));
            foreach (var id in info.ScreenIds) _workers[id].Post(AssignmentFor(id), paused: false);
            return Task.FromResult(info);
        }
    }

    public Task EndScreenRateTrialAsync(string trialId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_rateTrial is { } trial && string.Equals(trial.Info.TrialId, trialId, StringComparison.Ordinal))
            {
                Volatile.Write(ref _rateTrial, null);
                foreach (var id in trial.Info.ScreenIds) if (_workers.TryGetValue(id, out var w)) w.Post(AssignmentFor(id), _paused);
            }
        }
        return Task.CompletedTask;
    }
}
