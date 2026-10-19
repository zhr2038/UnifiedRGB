using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UnifiedRgb.App.Workbench;

public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i && i.ToString(CultureInfo.InvariantCulture) == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#5DDAC8")); }
        catch (FormatException) { return Brushes.Transparent; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DisplayNames.State(value?.ToString());
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class SceneLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DisplayNames.Scene(value?.ToString() ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class ControllerLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DisplayNames.Controller(value?.ToString() ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RfDeliveryLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not UnifiedRgb.Contracts.Dto.RfDeliveryDto r) { return "尚无无线接收核对数据（USB 发送成功不代表无线接收成功）"; }
        var state = r.State switch
        {
            "HostWritten" => "已发送，等待接收器上报",
            "ReportedMatch" => "接收器上报编号一致",
            "ReportedMismatch" => "接收器仍是其他动画，正在核对",
            "RetryPending" => "已补发，等待再次核对",
            "RetryExhausted" => "补发后仍未确认，请检查设备",
            _ => "接收状态暂无法确认",
        };
        return $"{state} · 补发 {r.Retries}/2 次\n期望 {r.ExpectedEffectIndex} / 上报 {r.ReportedEffectIndex?.ToString(CultureInfo.InvariantCulture) ?? "—"}\n编号核对不等于实际灯效播放验收";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
