using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.Views;

public enum ScreenWorkspaceLayout { Wide, Split, Compact }

/// <summary>Page-local layout and palette only. No device, file-save or RPC policy lives here.</summary>
public partial class ScreenWorkspaceView : UserControl
{
    private readonly DispatcherTimer _poll;
    private bool _showProperties;
    private ScreenWorkspaceLayout? _appliedLayout;
    private bool _appliedAuxiliaryCondensed;

    public ScreenWorkspaceView()
    {
        InitializeComponent();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => { if (DataContext is ScreensViewModel vm && vm.Available) vm.PollCommand.Execute(null); }, Dispatcher);
        _poll.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _poll.Start(); else _poll.Stop(); };
        Unloaded += (_, _) => _poll.Stop();
        Loaded += (_, _) => { ApplyAppearance(); ApplyLayout(); };
        SizeChanged += (_, _) => ApplyLayout();
    }

    public ScreenWorkspaceLayout LayoutMode { get; private set; }
    public string AppearanceName => AppearancePicker.SelectedIndex switch { 1 => "Light", 2 => "System", _ => "Dark" };
    public static ScreenWorkspaceLayout LayoutFor(double pageWidth) => pageWidth >= 1120 ? ScreenWorkspaceLayout.Wide
        : pageWidth >= 800 ? ScreenWorkspaceLayout.Split : ScreenWorkspaceLayout.Compact;

    private void ApplyLayout()
    {
        if (!IsInitialized) return;
        LayoutMode = LayoutFor(ActualWidth);
        var auxiliaryCondensed = LayoutMode == ScreenWorkspaceLayout.Compact || ActualHeight < 500;
        if (_appliedLayout == LayoutMode && _appliedAuxiliaryCondensed == auxiliaryCondensed) return;
        _appliedLayout = LayoutMode;
        _appliedAuxiliaryCondensed = auxiliaryCondensed;
        var wide = LayoutMode == ScreenWorkspaceLayout.Wide;
        var compact = LayoutMode == ScreenWorkspaceLayout.Compact;
        DeviceRegion.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        DeviceStrip.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        CompactTabs.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        DevicesWidth.Width = new GridLength(wide ? 184 : 0);
        DevicesGap.Width = new GridLength(wide ? 16 : 0);
        PropertiesGap.Width = new GridLength(compact ? 0 : 16);
        PropertiesWidth.Width = new GridLength(compact ? 0 : 320);
        Grid.SetColumn(PropertiesRegion, compact ? 2 : 4);
        PropertiesRegion.Visibility = !compact || _showProperties ? Visibility.Visible : Visibility.Collapsed;
        PreviewRegion.Visibility = !compact || !_showProperties ? Visibility.Visible : Visibility.Collapsed;
        PreviewFrame.Padding = new Thickness(compact ? 8 : 16);
        PreviewHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        EncodingDetails.Visibility = auxiliaryCondensed ? Visibility.Collapsed : Visibility.Visible;
        TelemetryDetails.Visibility = auxiliaryCondensed ? Visibility.Collapsed : Visibility.Visible;
        FooterLog.Visibility = auxiliaryCondensed ? Visibility.Collapsed : Visibility.Visible;
        ErrorBanner.Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(12);
        ErrorHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ErrorBody.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactErrorSummary.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactErrorDetails.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactErrorDetails.IsExpanded = false;
        PreviewTab.Tag = !_showProperties ? "Selected" : null;
        PropertiesTab.Tag = _showProperties ? "Selected" : null;
    }

    private void ShowPreview(object sender, RoutedEventArgs e) { _showProperties = false; _appliedLayout = null; ApplyLayout(); }
    private void ShowProperties(object sender, RoutedEventArgs e) { _showProperties = true; _appliedLayout = null; ApplyLayout(); }
    private void AppearanceChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) ApplyAppearance(); }
    private void ApplyAppearance()
    {
        var light = AppearancePicker.SelectedIndex == 1;
        if (AppearancePicker.SelectedIndex == 2)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }

        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/UnifiedRgb.App;component/Themes/ScreenPilot/Colors.{(light ? "Light" : "Dark")}.xaml", UriKind.Relative),
        };
    }

    private void ShowTools(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = DataContext; menu.PlacementTarget = button; menu.IsOpen = true;
        }
    }
}
