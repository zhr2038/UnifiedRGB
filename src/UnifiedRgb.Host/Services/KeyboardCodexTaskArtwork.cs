using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UnifiedRgb.Host.Services;

internal static class KeyboardCodexTaskArtwork
{
    internal const double NameWidth = 196;
    internal const double MarqueePixelsPerSecond = 10;
    internal const double MarqueeGap = 24;
    internal static readonly TimeSpan InitialHold = TimeSpan.FromSeconds(1);

    internal static byte[] Render(CodexTaskView view) => KeyboardStatusArtwork.Rasterize((dc, _) =>
    {
        if (view.Kind == CodexTaskViewKind.Task)
        {
            KeyboardStatusArtwork.Text(dc, string.Create(CultureInfo.InvariantCulture, $"任务 {view.Index}/{view.Total}"), 12, 6, 0, 196);
            dc.DrawLine(new Pen(Brushes.DimGray, 1), new Point(6, 17), new Point(202, 17));
            var title = new FormattedText(view.Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                17, Brushes.White, 1) { MaxLineCount = 1, Trimming = TextTrimming.None };
            var width = title.WidthIncludingTrailingWhitespace;
            var offset = Offset(width, view.SelectedFor);
            dc.PushClip(new RectangleGeometry(new Rect(6, 20, NameWidth, 23)));
            dc.DrawText(title, new Point(6 - offset, 20));
            if (width > NameWidth) dc.DrawText(title, new Point(6 - offset + width + MarqueeGap, 20));
            dc.Pop();
            KeyboardStatusArtwork.Text(dc, view.Duration == "时长未知" ? "运行时长未知" : "已运行 " + view.Duration, 14, 6, 45, 196);
        }
        else
        {
            KeyboardStatusArtwork.Text(dc, "Codex · 任务", 12, 6, 0, 196);
            dc.DrawLine(new Pen(Brushes.DimGray, 1), new Point(6, 17), new Point(202, 17));
            KeyboardStatusArtwork.Text(dc, view.Title, view.Kind == CodexTaskViewKind.Empty ? 22 : 17, 6, 25, 196);
        }
    });

    internal static double Offset(double textWidth, TimeSpan selectedFor)
    {
        if (!double.IsFinite(textWidth) || textWidth <= NameWidth || selectedFor <= InitialHold) return 0;
        return (selectedFor - InitialHold).TotalSeconds * MarqueePixelsPerSecond % (textWidth + MarqueeGap);
    }
}
