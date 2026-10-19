using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>Bounded screen-only performance diagnostics on the existing trusted IPC. Never persists or changes scene content.</summary>
public interface IScreenPerformanceService
{
    Task<IReadOnlyList<ScreenPerformanceDto>> GetScreenPerformanceAsync(CancellationToken ct = default);
    Task<ScreenRateTrialDto> BeginScreenRateTrialAsync(ScreenRateTrialRequestDto request, CancellationToken ct = default);
    Task EndScreenRateTrialAsync(string trialId, CancellationToken ct = default);
}
