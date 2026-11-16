using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UnifiedRgb.App.ViewModels;

namespace UnifiedRgb.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _healthPoll;
    private bool _hasShownActive;
    private bool _wasInactive;

    public MainWindow()
    {
        InitializeComponent();
        _healthPoll = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => { if (DataContext is MainViewModel vm) _ = vm.PollPeripheralAndScreenHealthAsync(); }, Dispatcher);
        _healthPoll.Stop();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            if (Width > area.Width || Height > area.Height) { Width = Math.Max(MinWidth, area.Width); Height = Math.Max(MinHeight, area.Height); Left = area.Left; Top = area.Top; }
            TrackActivity();
        };
        Closed += (_, _) => _healthPoll.Stop();
        IsVisibleChanged += (_, _) => TrackActivity();
        StateChanged += (_, _) => TrackActivity();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Studio.PropertyChanged += (_, e) => { if (e.PropertyName == "Page" && SystemParameters.ClientAreaAnimation) PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(.4, 1, TimeSpan.FromMilliseconds(160))); };
        };
    }

    private void TrackActivity()
    {
        var active = IsLoaded && IsVisible && WindowState != WindowState.Minimized;
        if (active)
        {
            _healthPoll.Start();
            if (_hasShownActive && _wasInactive && DataContext is MainViewModel vm)
            {
                _ = vm.RestoreRefreshCommand.ExecuteAsync();
            }

            _hasShownActive = true;
            _wasInactive = false;
        }
        else
        {
            _healthPoll.Stop();
            if (_hasShownActive)
            {
                _wasInactive = true;
            }
        }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Independent of normal error bindings/filters, so even a partially failed view model has a visible exit path.</summary>
    public void ShowFaultState(string message)
    {
        Title = "光域 · 界面需要重启";
        PageHost.IsEnabled = false;
        FaultNoticeText.Text = message;
        FaultNotice.Visibility = Visibility.Visible;
    }
}
