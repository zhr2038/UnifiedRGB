using System.Windows.Controls;
using System.Windows.Threading;
using UnifiedRgb.App.Workbench;
namespace UnifiedRgb.App.Views;
public partial class ScreenWorkspaceView : UserControl
{
    private readonly DispatcherTimer _poll;
    public ScreenWorkspaceView()
    {
        InitializeComponent();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { if (DataContext is ScreensViewModel vm && vm.Available) vm.PollCommand.Execute(null); }, Dispatcher);
        _poll.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _poll.Start(); else _poll.Stop(); };
        Unloaded += (_, _) => _poll.Stop();
    }
}
