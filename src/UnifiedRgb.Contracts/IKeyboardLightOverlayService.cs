namespace UnifiedRgb.Contracts;

/// <summary>
/// Temporary keyboard-only lighting. Colors address a contiguous run in the verified logical map.
/// An empty array clears the overlay. The Host releases it on disconnect or five seconds without renewal.
/// The response names the transport that acknowledged the frame.
/// </summary>
public interface IKeyboardLightOverlayService
{
    Task<string> SetKeyboardLightOverlayAsync(string controllerId, int start, string[] colors,
        string[]? expectedKeys = null, CancellationToken ct = default);
}
