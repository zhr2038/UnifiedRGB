using System.Diagnostics;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Services;

public sealed partial class WiredKeyboardAdapter
{
    internal const int MaximumOverlayKeys = 32;
    private readonly object _overlayGate = new();
    private KeyOverlay? _keyOverlay;

    private sealed record KeyOverlay(string Owner, int Start, Rgb[] Colors, long CreatedAt)
    {
        public TaskCompletionSource<string> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private KeyOverlay? CurrentOverlay()
    {
        var overlay = Volatile.Read(ref _keyOverlay);
        if (overlay is not null && Stopwatch.GetElapsedTime(overlay.CreatedAt) >= TimeSpan.FromSeconds(5))
        {
            Interlocked.CompareExchange(ref _keyOverlay, null, overlay);
            return null;
        }
        return overlay;
    }

    internal async Task<string> SetOverlayAsync(string owner, int start, Rgb[] colors, CancellationToken ct, string[]? expectedKeys = null)
    {
        ct.ThrowIfCancellationRequested();
        var transport = _bridge.KeyFrameTransport;
        if (!_open || _perKey is not { Ready: true, Map: { } map } plan ||
            transport is null || !plan.RouteMaps.ContainsKey(transport))
            throw new DeviceBlockedException("Keyboard overlay requires the active verified key map.");
        if (start < 0 || colors.Length > MaximumOverlayKeys || start > map.Keys.Count - colors.Length)
            throw new ArgumentOutOfRangeException(nameof(start), "Overlay must fit within the verified key map.");

        if (expectedKeys is not null && (expectedKeys.Length == 0 || expectedKeys.Length > MaximumOverlayKeys
                || expectedKeys.Length < colors.Length || start > map.Keys.Count - expectedKeys.Length
                || !map.Keys.Skip(start).Take(expectedKeys.Length).Select(key => key.Key)
                    .SequenceEqual(expectedKeys, StringComparer.Ordinal)))
            throw new ArgumentException("Overlay keys do not match the verified logical map.", nameof(expectedKeys));

        var next = new KeyOverlay(owner, start, colors, Stopwatch.GetTimestamp());
        lock (_overlayGate)
        {
            ct.ThrowIfCancellationRequested();
            if (CurrentOverlay() is { } previous && previous.Owner != owner)
                throw new InvalidOperationException("The keyboard already has an active overlay.");
            Volatile.Write(ref _keyOverlay, next);
        }
        try
        {
            return await next.Written.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(ref _keyOverlay, null, next);
            throw;
        }
    }

    internal void ReleaseOverlay(string owner)
    {
        lock (_overlayGate)
        {
            if (Volatile.Read(ref _keyOverlay) is { } overlay && overlay.Owner == owner)
                Interlocked.CompareExchange(ref _keyOverlay, null, overlay);
        }
    }
}
