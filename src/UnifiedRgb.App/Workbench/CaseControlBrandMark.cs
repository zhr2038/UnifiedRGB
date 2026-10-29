using System.Windows;
using System.Windows.Media;

namespace UnifiedRgb.App.Workbench;

/// <summary>Monochrome rendering of the existing project brand geometry, shared by control-page artwork.</summary>
public static class CaseControlBrandMark
{
    private static readonly Lazy<Drawing> Mark = new(Load);

    public static void Draw(DrawingContext drawingContext, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        drawingContext.PushTransform(new MatrixTransform(bounds.Width / 256, 0, 0, bounds.Height / 256, bounds.X, bounds.Y));
        drawingContext.DrawDrawing(Mark.Value);
        drawingContext.Pop();
    }

    private static Drawing Load()
    {
        var resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/UnifiedRgb.App;component/Assets/Brand/Brand.xaml", UriKind.Relative));
        var drawing = ((Drawing)resources["Brand.MarkDrawing"]).CloneCurrentValue();
        Monochrome(drawing);
        drawing.Freeze();
        return drawing;
    }

    private static Brush? White(Brush? brush) => brush is null or SolidColorBrush { Color.A: 0 } ? brush : Brushes.White;

    private static void Monochrome(Drawing drawing)
    {
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children) Monochrome(child);
        else if (drawing is GeometryDrawing geometry)
        {
            geometry.Brush = White(geometry.Brush);
            if (geometry.Pen is not null) geometry.Pen.Brush = White(geometry.Pen.Brush);
        }
    }
}
