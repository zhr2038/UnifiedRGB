using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

/// <summary>Separate optional local control display. Never part of case blackout or scene packages.</summary>
public interface IKeyboardDisplayService
{
    Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default);
    Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default);

    // Additive local animation endpoints. An older Host has no such method (the App shows the panel as unavailable).
    Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Keyboard display animation is not supported by this Host.");
    Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default)
        => throw new NotSupportedException("Keyboard display animation is not supported by this Host.");
    Task<KeyboardDisplayAnimationPreviewDto> PreviewKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationPreviewRequestDto request, CancellationToken ct = default)
        => throw new NotSupportedException("Keyboard display animation is not supported by this Host.");
}
