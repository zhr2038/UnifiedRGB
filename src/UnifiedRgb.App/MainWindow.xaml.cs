using System.Windows;
using System.Windows.Media.Animation;
using UnifiedRgb.App.ViewModels;

namespace UnifiedRgb.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Studio.PropertyChanged += (_, e) => { if (e.PropertyName == "Page" && SystemParameters.ClientAreaAnimation) PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(.4, 1, TimeSpan.FromMilliseconds(160))); };
        };
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
