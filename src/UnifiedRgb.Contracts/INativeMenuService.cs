using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>
/// Optional native-menu case control: a persisted local preference (default off) and its Host status. The preference is a
/// request only; the status says whether a verified source actually armed it. An older Host answers with method-not-found.
/// </summary>
public interface INativeMenuService
{
    Task<NativeMenuStatusDto> GetNativeMenuStatusAsync(CancellationToken ct = default);
    Task<NativeMenuStatusDto> SetNativeMenuEnabledAsync(bool enabled, CancellationToken ct = default);
}
