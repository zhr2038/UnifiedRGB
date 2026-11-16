using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.Views;

public partial class CaseControlPanelView : UserControl
{
    private readonly DispatcherTimer _poll;
    private readonly WindowActivityWatcher _activity;
    public CaseControlPanelView()
    {
        InitializeComponent();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => PollNow(), Dispatcher);
        _poll.Stop();
        _activity = new WindowActivityWatcher(this, OnActivityChanged);
        DataContextChanged += (_, _) => { if (_activity.IsActive) PollNow(expire: false); };
    }

    private void OnActivityChanged(bool active, bool _)
    {
        if (!active) { _poll.Stop(); return; }
        _poll.Start();
        PollNow(expire: false);
    }

    private void PollNow(bool expire = true)
    {
        if (DataContext is not CaseControlPanelViewModel vm) return;
        if (expire)
        {
            vm.ExpireObservation();
            vm.Peripherals?.ExpireObservation();
            vm.NativeMenu?.ExpireObservation();
            vm.KeyboardAnimation?.ExpireObservation();
        }
        vm.ReadCommand.Execute(null);
        vm.Peripherals?.ReadCommand.Execute(null);
        vm.NativeMenu?.ReadCommand.Execute(null);
        vm.KeyboardAnimation?.ReadCommand.Execute(null);
        vm.KeyboardAnimation?.Codex.ReadCommand.Execute(null);
    }
}
