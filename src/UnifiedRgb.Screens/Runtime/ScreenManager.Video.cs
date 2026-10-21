using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

internal sealed record NativeVideoTrial(NativeVideoTrialDto Info, long Deadline);

public sealed partial class ScreenManager : IScreenVideoService
{
    private NativeVideoTrial? _videoTrial;
    private bool NativeVideoVerified(string id) => ScreenIds.TryParse(id, out var family, out _)
        && family.Family is ScreenFamily.Turzx or ScreenFamily.H2 && (_options.NativeH264VerifiedScreenIds ?? []).Contains(id, StringComparer.Ordinal);

    private NativeVideoTrial? VideoTrialOf(string id)
    {
        var trial = Volatile.Read(ref _videoTrial);
        return trial is not null && trial.Info.ScreenId == id && _platform.Time.GetTimestamp() < trial.Deadline ? trial : null;
    }

    public async Task<NativeVideoTrialDto> BeginNativeVideoTrialAsync(NativeVideoTrialRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request); ct.ThrowIfCancellationRequested();
        if (request.DurationMs is < 1000 or > 60000 || request.Fps is < 1 or > 60
            || !ScreenIds.TryParse(request.ScreenId, out var family, out _) || family.Family is not (ScreenFamily.Turzx or ScreenFamily.H2))
            throw new ArgumentException("A video trial needs one H2/TURZX id, 1..60 FPS and 1..60 seconds.", nameof(request));
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopped, this);
            var id = request.ScreenId;
            if (_paused || _workerContext.WholeCaseBlackout() || (_rateTrial is { } rate && _platform.Time.GetTimestamp() < rate.Deadline)
                || (_videoTrial is { } active && _platform.Time.GetTimestamp() < active.Deadline)
                || !_options.OutputAllowed(family.Family) || !_workers.TryGetValue(id, out var worker)
                || worker.Snapshot().State != ScreenStates.Running || AssignmentFor(id) is not { Enabled: true } assignment)
                throw new InvalidOperationException("A video trial needs an already-running H2/TURZX, open output gates and no blackout/other trial.");
            if (request.AssetId is { } assetId)
            {
                var asset = _assets.Find(assetId) ?? throw new ArgumentException("Trial media is missing.", nameof(request));
                if (assignment.TimelineId is not null && (_assets.Find(assignment.BackgroundAssetId!) is not { } current || current.DurationMs != asset.DurationMs))
                    throw new ArgumentException("Trial media must match the current shared loop duration exactly.", nameof(request));
            }
            var info = new NativeVideoTrialDto(Guid.NewGuid().ToString("N"), id, request.DurationMs, request.Fps, request.AssetId);
            Volatile.Write(ref _videoTrial, new(info, _platform.Time.GetTimestamp() + request.DurationMs * _platform.Time.TimestampFrequency / 1000));
            worker.Post(assignment, false); // identity, output state, TimelineId, local settings and persistence are untouched
            return info;
        }
        }
        finally { _mutation.Release(); }
    }

    public Task EndNativeVideoTrialAsync(string trialId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_videoTrial is { } trial && trial.Info.TrialId == trialId)
            {
                Volatile.Write(ref _videoTrial, null);
                if (_workers.TryGetValue(trial.Info.ScreenId, out var worker)) worker.Post(AssignmentFor(trial.Info.ScreenId), _paused);
            }
        }
        return Task.CompletedTask;
    }

    private void AbortVideoTrialOf(string id)
    {
        if (Volatile.Read(ref _videoTrial) is { } trial && trial.Info.ScreenId == id)
            Interlocked.CompareExchange(ref _videoTrial, null, trial);
    }
}
