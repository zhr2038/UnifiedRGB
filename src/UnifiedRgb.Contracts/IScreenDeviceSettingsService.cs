using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>
/// Local per-screen device settings, an additional target on the same trusted pipe as <see cref="IRgbService"/> (method names on
/// the wire drop "Async"). Settings are owned by this machine, keyed by screen id, and independent from scene content, profiles and
/// scene packages: package/profile/scene applications and restarts show their content in the saved local rotation. Reads never
/// touch a device. A save changes only the matching active assignment's orientation/encoder policy; it never adds an assignment,
/// starts an unassigned screen, resumes a StopScreens pause or changes output gates, RGB or brightness.
/// </summary>
public interface IScreenDeviceSettingsService
{
    /// <summary>The saved settings of <paramref name="screenId"/>, or the defaults (0, "legacy") when it has none.</summary>
    Task<ScreenDeviceSettingsDto> GetScreenDeviceSettingsAsync(string screenId, CancellationToken ct = default);

    /// <summary>Validates and saves the settings; invalid input throws ArgumentException and nothing changes.</summary>
    Task<ScreenDeviceSettingsDto> SetScreenDeviceSettingsAsync(ScreenDeviceSettingsUpdateDto settings, CancellationToken ct = default);
}
