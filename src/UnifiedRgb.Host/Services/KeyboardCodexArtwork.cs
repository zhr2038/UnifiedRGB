using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

internal static class KeyboardCodexArtwork
{
    public static byte[] Render(KeyboardCodexUsageDto status) => KeyboardStatusArtwork.Rasterize((dc, _) =>
    {
        var windows = new[] { status.Primary, status.Secondary }.Where(w => w is not null).ToArray();
        if (windows.Length == 0) KeyboardStatusArtwork.Text(dc, "额度暂不可用", 17, 6, 22, 196);
        if (windows.Length == 1)
        {
            var w = windows[0]!;
            var fill = new Rect(7, 7, 194 * w.RemainingPercent / 100, 24);
            dc.DrawRectangle(Brushes.DimGray, new Pen(Brushes.White, 1), new Rect(6, 6, 196, 26));
            dc.DrawRectangle(Brushes.White, null, fill);
            var percent = w.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            Percentage(dc, percent, Brushes.White);
            dc.PushClip(new RectangleGeometry(fill)); Percentage(dc, percent, Brushes.Black); dc.Pop();
            KeyboardStatusArtwork.Text(dc, (status.State == "Stale" ? "旧数据 · " : "") + "重置 "
                + w.ResetsAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), 13, 6, 42, 196);
            return;
        }
        for (var i = 0; i < windows.Length; i++)
        {
            var w = windows[i]!; var y = i * 32;
            dc.DrawRectangle(Brushes.DimGray, new Pen(Brushes.White, 1), new Rect(4, y + 1, 200, 18));
            var fill = new Rect(5, y + 2, 198 * w.RemainingPercent / 100, 16);
            dc.DrawRectangle(Brushes.White, null, fill);
            var text = new FormattedText(w.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 13, Brushes.White, 1)
                { MaxTextWidth = 198, TextAlignment = TextAlignment.Center };
            dc.DrawText(text, new Point(5, y));
            dc.PushClip(new RectangleGeometry(fill)); text.SetForegroundBrush(Brushes.Black);
            dc.DrawText(text, new Point(5, y)); dc.Pop();
            KeyboardStatusArtwork.Text(dc, (status.State == "Stale" ? "旧数据 · " : "") + "重置 "
                + w.ResetsAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), 9, 4, y + 21, 200);
        }
    });

    private static void Percentage(DrawingContext dc, string value, Brush brush) => dc.DrawText(new FormattedText(value,
        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        21, brush, 1) { MaxTextWidth = 196, MaxLineCount = 1, TextAlignment = TextAlignment.Center }, new Point(6, 4));
}
