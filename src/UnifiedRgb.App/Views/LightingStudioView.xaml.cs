using System.Windows.Controls;
using System.Windows;
using UnifiedRgb.App.Workbench;
namespace UnifiedRgb.App.Views;
public partial class LightingStudioView : UserControl
{
    public LightingStudioView() => InitializeComponent();
    private void ColorClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not StudioViewModel vm) return;
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(vm.Draft.PrimaryColor) };
        if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) vm.Draft.SetPrimaryColor($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
    }
}
