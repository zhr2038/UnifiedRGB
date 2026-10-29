using System.Diagnostics;
using System.Globalization;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Keyboard;

/// <summary>Bounds of one keyboard frame preparation. Every bound is checked before, between or after frames.</summary>
/// <param name="MaxBytes">Packed bytes the whole loop may occupy; checked before any decode.</param>
/// <param name="GridFloorMs">A source frame starting less than this after the current segment start is merged into it.</param>
/// <param name="TimeoutMs">Wall-clock budget; checked between frames and after the last (one running decode is never aborted).</param>
public sealed record KeyboardOledLimits(long MaxBytes, int GridFloorMs, int TimeoutMs)
{
    public const long MaxCacheBytes = 32L * 1024 * 1024;

    public string? Problem => MaxBytes < KeyboardOledFormat.PackedBytes || MaxBytes > MaxCacheBytes ? "cache bytes out of range"
        : GridFloorMs is < 20 or > 1000 ? "grid floor out of range"
        : TimeoutMs is < 500 or > 120_000 ? "preparation timeout out of range" : null;
}

/// <summary>One packed frame at a loop position (milliseconds within the asset loop).</summary>
public sealed record KeyboardOledSample(int PositionMs, byte[] Packed);

/// <summary>Refusal of a preparation: nothing was cached, and the reason is stated (Chinese, user-visible).</summary>
public sealed class KeyboardOledPrepareException : Exception
{
    public KeyboardOledPrepareException() : base("键盘动画准备失败") { }
    public KeyboardOledPrepareException(string message) : base(message) { }
    public KeyboardOledPrepareException(string message, Exception inner) : base(message + "：" + inner?.Message, inner) { }
}

/// <summary>
/// Prepares packed keyboard frames from one stored asset through the existing compositor (decode, cover/contain fit, black
/// background, flatten), then the pure encoder. STA only. All frames or a refusal; never a partial or prefix result.
/// </summary>
internal static class KeyboardOledFramePreparer
{
    /// <summary>The compositor reads only Width, Height and Family (rectangular overlay shape; the overlay is always "none").</summary>
    internal static readonly ScreenFamilyInfo Canvas = new(ScreenFamily.Turzx, "keyboard-oled", 0, KeyboardOledFormat.Width,
        KeyboardOledFormat.Height, ScreenCodec.TurzxPng, 0, 0, null, null, "键盘 OLED");

    public static bool IsFit(string? fit) => fit is "contain" or "cover";

    /// <summary>Segment starts: frame 0 at 0, then each source frame start at least <paramref name="gridFloorMs"/> after the last.</summary>
    internal static List<int> SegmentStarts(IReadOnlyList<int> delaysMs, int gridFloorMs)
    {
        var starts = new List<int> { 0 };
        if (delaysMs.Count <= 1) return starts;
        var at = 0L;
        for (var i = 0; i < delaysMs.Count - 1; i++)
        {
            at += delaysMs[i];
            if (at - starts[^1] >= gridFloorMs) starts.Add(checked((int)at));
        }
        return starts;
    }

    public static KeyboardOledFrames Prepare(AssetStore assets, string assetId, string fit, string encoding, KeyboardOledLimits limits,
        CancellationToken ct, Func<TimeSpan>? elapsed = null)
    {
        var asset = Require(assets, assetId, fit, encoding);
        if (limits.Problem is { } problem) throw new ArgumentException(problem, nameof(limits));
        var starts = SegmentStarts(asset.DelaysMs, limits.GridFloorMs);
        var bytes = (long)starts.Count * KeyboardOledFormat.PackedBytes;
        if (bytes > limits.MaxBytes)
            throw new KeyboardOledPrepareException(string.Create(CultureInfo.InvariantCulture,
                $"需要 {starts.Count} 帧 × {KeyboardOledFormat.PackedBytes} B = {bytes} B，超过缓存上限 {limits.MaxBytes} B；未缓存任何帧"));
        var keptStarts = new List<int>(starts.Count);
        var kept = new List<byte[]>(starts.Count);
        Compose(assets, asset, fit, encoding, starts, limits.TimeoutMs, elapsed, (start, packed) =>
        {
            if (kept.Count > 0 && kept[^1].AsSpan().SequenceEqual(packed)) return; // the previous segment already shows it
            keptStarts.Add(start); kept.Add(packed);
        }, ct);
        return new KeyboardOledFrames(asset.Id, fit, encoding, asset.DurationMs, asset.FrameCount, [.. keptStarts], [.. kept]);
    }

    /// <summary>
    /// Up to <paramref name="count"/> evenly spaced segment starts of the same grid as <see cref="Prepare"/>, composed and encoded
    /// by the same path, so each sample equals the prepared frame at its position.
    /// </summary>
    public static IReadOnlyList<KeyboardOledSample> Sample(AssetStore assets, string assetId, string fit, string encoding, int gridFloorMs,
        int count, int timeoutMs, CancellationToken ct, Func<TimeSpan>? elapsed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 8);
        var asset = Require(assets, assetId, fit, encoding);
        var starts = SegmentStarts(asset.DelaysMs, Math.Clamp(gridFloorMs, 20, 1000));
        var picked = Enumerable.Range(0, Math.Min(count, starts.Count)).Select(i => starts[(int)((long)i * starts.Count / Math.Min(count, starts.Count))])
            .Distinct().ToList();
        var result = new List<KeyboardOledSample>(picked.Count);
        Compose(assets, asset, fit, encoding, picked, timeoutMs, elapsed, (position, packed) => result.Add(new(position, packed)), ct);
        return result;
    }

    private static StoredAsset Require(AssetStore assets, string assetId, string fit, string encoding)
    {
        if (!AssetStore.IsValidId(assetId)) throw new ArgumentException("Invalid asset id.", nameof(assetId));
        if (!IsFit(fit)) throw new ArgumentException("Fit must be contain or cover.", nameof(fit));
        if (!KeyboardDisplayAnimationEncodings.IsKnown(encoding)) throw new ArgumentException("Unknown keyboard encoding.", nameof(encoding));
        return assets.Find(assetId) ?? throw new KeyboardOledPrepareException("所选素材不在素材库中");
    }

    private static void Compose(AssetStore assets, StoredAsset asset, string fit, string encoding, IReadOnlyList<int> positionsMs,
        int timeoutMs, Func<TimeSpan>? elapsed, Action<int, byte[]> add, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        elapsed ??= () => watch.Elapsed;
        var assignment = new ScreenAssignmentDto("keyboard:oled", asset.Id, Fit: fit); // black background, no overlay, rotation 0, brightness 1
        using var compositor = new ScreenCompositor(assets, 0);
        foreach (var position in positionsMs)
        {
            Within();
            ComposeResult composed;
            try { composed = compositor.Compose(Canvas, assignment, position, null, DateTimeOffset.UnixEpoch); }
            catch (Exception ex) when (WpfCodec.IsDecodeFailure(ex)) { throw new KeyboardOledPrepareException("素材解码失败", ex); }
            if (composed.Warning is { } warning) throw new KeyboardOledPrepareException("素材无法完整呈现：" + warning);
            add(position, KeyboardOledEncoder.Encode(composed.Canvas.Pixels, encoding));
        }
        // The result is accepted only within the same bounds after the last compose/encode: a cancellation, an expiry or the
        // source leaving the library meanwhile refuses all of it.
        Within();
        if (assets.Find(asset.Id) is null) throw new KeyboardOledPrepareException("所选素材已从素材库移除；未缓存任何帧");

        void Within()
        {
            ct.ThrowIfCancellationRequested();
            if (elapsed() > TimeSpan.FromMilliseconds(timeoutMs))
                throw new KeyboardOledPrepareException(string.Create(CultureInfo.InvariantCulture, $"准备超过 {timeoutMs} ms 时限；未缓存任何帧"));
        }
    }
}
