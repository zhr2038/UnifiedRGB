using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// The screen side of the local keyboard OLED animation: the current scene references (a volatile read, no lock, no I/O) and
/// bounded off-path preparation of packed frames from an existing immutable asset. Nothing here opens a device or changes a
/// scene, asset or package. Each request's task completes only after its actual decode/encode has ended: cancellation is
/// observed by the work and never completes the task early, so a caller can bound the live work by the tasks it holds.
/// </summary>
public interface IKeyboardOledMedia
{
    /// <summary>The applied screen scene (null before it is loaded). Nonblocking; safe on the keyboard owner's path.</summary>
    ScreenSceneDto? CurrentReferences();

    /// <summary>Every segment of the asset's loop, or <see cref="KeyboardOledPrepareException"/>; never a partial result.</summary>
    Task<KeyboardOledFrames> PrepareKeyboardOledAsync(string assetId, string fit, string encoding, KeyboardOledLimits limits, CancellationToken ct);

    /// <summary>A few frames on the preparation grid, through the same compositor and encoder (the encoded-result preview).</summary>
    Task<IReadOnlyList<KeyboardOledSample>> SampleKeyboardOledAsync(string assetId, string fit, string encoding, int gridFloorMs, int count,
        int timeoutMs, CancellationToken ct);
}

public sealed partial class ScreenManager : IKeyboardOledMedia
{
    public Task<KeyboardOledFrames> PrepareKeyboardOledAsync(string assetId, string fit, string encoding, KeyboardOledLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return OnKeyboardThreadAsync(() => KeyboardOledFramePreparer.Prepare(_assets, assetId, fit, encoding, limits, ct), ct);
    }

    public Task<IReadOnlyList<KeyboardOledSample>> SampleKeyboardOledAsync(string assetId, string fit, string encoding, int gridFloorMs, int count,
        int timeoutMs, CancellationToken ct) =>
        OnKeyboardThreadAsync(() => KeyboardOledFramePreparer.Sample(_assets, assetId, fit, encoding, gridFloorMs, count, timeoutMs, ct), ct);

    /// <summary>
    /// One short-lived STA per request (its dispatcher is shut down when the thread ends): a long keyboard preparation never
    /// queues behind or ahead of screen previews. The work observes <paramref name="ct"/> between frames and after the last; the
    /// returned task completes only after the work item has ended (a cancelled item that has not started is skipped) and the
    /// thread was joined (at most 10 s).
    /// </summary>
    private async Task<T> OnKeyboardThreadAsync<T>(Func<T> work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var sta = new StaThread("keyboard oled");
        return await sta.InvokeAsync(work, ct).ConfigureAwait(false);
    }
}
