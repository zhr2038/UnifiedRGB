using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

public interface IPeripheralConfigurationService
{
    Task<PeripheralConfigurationStatusDto> GetPeripheralConfigurationStatusAsync(CancellationToken ct = default);
    Task<PeripheralConfigurationStatusDto> EnterPeripheralConfigurationAsync(CancellationToken ct = default);
    Task<PeripheralConfigurationStatusDto> ExitPeripheralConfigurationAsync(CancellationToken ct = default);
}
