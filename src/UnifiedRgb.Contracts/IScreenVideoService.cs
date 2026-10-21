using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>Explicit, bounded native-video trials on one already-running TURZX. Never persists or changes the RGB/screen scene.</summary>
public interface IScreenVideoService
{
    Task<NativeVideoTrialDto> BeginNativeVideoTrialAsync(NativeVideoTrialRequestDto request, CancellationToken ct = default);
    Task EndNativeVideoTrialAsync(string trialId, CancellationToken ct = default);
}

