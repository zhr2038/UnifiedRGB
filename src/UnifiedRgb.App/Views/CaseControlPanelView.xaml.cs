using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.Views;

public partial class CaseControlPanelView : UserControl
{
    private readonly DispatcherTimer _poll;
    public CaseControlPanelView()
    {
        InitializeComponent();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) =>
            {
                if (DataContext is not CaseControlPanelViewModel vm) return;
                vm.ExpireObservation();
                vm.ReadCommand.Execute(null);
                vm.Peripherals?.ExpireObservation();
                vm.Peripherals?.ReadCommand.Execute(null);
                vm.NativeMenu?.ExpireObservation();
                vm.NativeMenu?.ReadCommand.Execute(null);
                vm.KeyboardAnimation?.ExpireObservation();
                vm.KeyboardAnimation?.ReadCommand.Execute(null);
            }, Dispatcher);
        _poll.Stop();
        Loaded += (_, _) => UpdatePolling();
        Unloaded += (_, _) => _poll.Stop();
        IsVisibleChanged += (_, _) => UpdatePolling();
        DataContextChanged += (_, _) => UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (!IsLoaded || !IsVisible) { _poll.Stop(); return; }
        _poll.Start();
        if (DataContext is not CaseControlPanelViewModel vm) return;
        vm.ReadCommand.Execute(null);
        vm.Peripherals?.ReadCommand.Execute(null);
        vm.NativeMenu?.ReadCommand.Execute(null);
        vm.KeyboardAnimation?.ReadCommand.Execute(null);
    }
}
