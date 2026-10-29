using System.Globalization;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>One key of a software per-key frame, placed by its map row and column for review.</summary>
public sealed record PerKeyReviewKey(string Key, byte LedId, int Row, int Column, double Left, double Top, double Size, string Color)
{
    public string ToolTip => $"{Key} · LED 0x{LedId:X2} · 行 {Row} 列 {Column} · {Color}";
}

/// <summary>
/// Read-only keyboard view of the last software per-key frame the Host generated on the shared scene timeline. Keys keep their
/// map order and colour; it shows what the scene computed for each key, never that the keyboard displayed it.
/// </summary>
public sealed class PerKeyFrameReview
{
    public const double DefaultWidth = 640;
    public const string Caption = "软件逐键帧 · 仅审查共享时间轴计算结果，非实机证明";

    public PerKeyFrameReview(PerKeyFrameDto frame, double width = DefaultWidth)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        Index = frame.Index;
        var pixels = frame.Keys ?? [];
        if (pixels.Count == 0) { Keys = []; return; }

        var minColumn = pixels.Min(p => p.Column);
        var minRow = pixels.Min(p => p.Row);
        var span = Math.Max(1, pixels.Max(p => p.Column) - minColumn);
        // The closest two keys of one row bound the key size, so neighbouring keys never overlap.
        var pitch = pixels.GroupBy(p => p.Row)
            .SelectMany(row => row.Select(p => p.Column).Order().Zip(row.Select(p => p.Column).Order().Skip(1), (a, b) => b - a))
            .Where(d => d > 0).DefaultIfEmpty(span).Min();
        var scale = width / (span + pitch);
        var size = Math.Clamp(pitch * scale * 0.9, 6, 48);
        Keys = pixels.Select(p => new PerKeyReviewKey(p.Key, p.LedId, p.Row, p.Column,
            (p.Column - minColumn) * scale, (p.Row - minRow) * (size + 4), size, p.Color)).ToArray();
        Width = Keys.Max(k => k.Left + k.Size);
        Height = Keys.Max(k => k.Top + k.Size);
    }

    public long Index { get; }
    public IReadOnlyList<PerKeyReviewKey> Keys { get; }
    public double Width { get; }
    public double Height { get; }
    public string Summary => string.Create(CultureInfo.InvariantCulture, $"帧 #{Index} · {Keys.Count} 键 · {Keys.Select(k => k.Color).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 种颜色");
}
