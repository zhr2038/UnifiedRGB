using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>
/// Screen-only JSON-RPC surface, an additional target on the same trusted pipe as <see cref="IRgbService"/> (method names on
/// the wire drop "Async"). Output is gated off by default; discovery, telemetry and preview work while it is off.
/// </summary>
public interface IScreenService
{
    Task<IReadOnlyList<ScreenDeviceDto>> ListScreensAsync(CancellationToken ct = default);

    Task<PcTelemetryDto> GetPcTelemetryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ScreenAssetDto>> ListScreenAssetsAsync(CancellationToken ct = default);

    /// <summary>Validates, decodes and stores an image or GIF; an identical upload returns the existing asset.</summary>
    Task<ScreenAssetDto> UploadScreenAssetAsync(ScreenAssetUploadDto asset, CancellationToken ct = default);

    /// <summary>Removes an asset; refused (InvalidOperationException) while the current screen scene uses it.</summary>
    Task DeleteScreenAssetAsync(string assetId, CancellationToken ct = default);

    /// <summary>Composes one frame of <paramref name="assignment"/> exactly as it would be encoded, without any USB I/O.</summary>
    Task<ScreenPreviewDto> PreviewScreenAsync(ScreenAssignmentDto assignment, CancellationToken ct = default);

    Task<ScreenSceneDto> GetScreenSceneAsync(CancellationToken ct = default);

    /// <summary>Replaces the whole screen scene (persisted) and keeps the RGB scene; clears a StopScreens pause.</summary>
    Task SetScreenSceneAsync(ScreenSceneDto scene, CancellationToken ct = default);

    /// <summary>Stops sending new frames until the next screen scene is set. No reset, reboot or RGB change.</summary>
    Task StopScreensAsync(CancellationToken ct = default);

    /// <summary>
    /// Manual restart of the configured Lian Li fan-LCD group (UnifiedRgb:Screens:FanLcdRestart) through the RF LCD-group command;
    /// see <see cref="ScreenRestartDto"/>. Only the mapped screens are quiesced; the saved scene, assets, StopScreens pause and RGB
    /// are unchanged. One at a time, at least 60 s after the previous actual send; refusals return Attempts 0. Bounded (&lt; 30 s).
    /// </summary>
    Task<ScreenRestartDto> RestartFanLcdAsync(CancellationToken ct = default);
}
