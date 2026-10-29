using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UnifiedRgb.App.Workbench;

/// <summary>208x64 monochrome software artwork. Rendering does not enable any external screen transport.</summary>
public sealed class CaseControlDisplay : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(CaseControlDisplayState),
        typeof(CaseControlDisplay), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public CaseControlDisplayState? State { get => (CaseControlDisplayState?)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(208, 64);
    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var state = State ?? new(CaseControlState.Unknown, "状态未读取", "本地预览 · 未发送");
        dc.PushTransform(new ScaleTransform(ActualWidth / 208, ActualHeight / 64));
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 208, 64));
        CaseControlBrandMark.Draw(dc, new Rect(6, 1, 17, 17));
        Text(dc, "光域", 27, 2, 12, 80);
        Text(dc, "控制屏预览", 126, 3, 10, 78);
        dc.DrawLine(new Pen(Brushes.White, 1), new Point(6, 20.5), new Point(202, 20.5));
        Text(dc, state.Title, 6, 23, 16, 196);
        Text(dc, state.Detail, 6, 47, 10, 196);
        dc.Pop();
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, Brushes.White, 1)
        { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, new Point(x, y));
    }
}
