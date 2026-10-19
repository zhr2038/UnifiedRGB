using System.Globalization;
using System.Windows.Data;

namespace UnifiedRgb.App.Views;

/// <summary>Only the screen pilot may compact the existing shell; other pages retain their layout.</summary>
public sealed class ScreenShellCompactConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [int page, double width] && page == 2 && width < 1056;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
