using System.Windows.Controls;
using UnifiedRgb.App.ViewModels;

namespace UnifiedRgb.App.Views;

public partial class EffectsView : UserControl
{
    public EffectsView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is EffectsViewModel vm) vm.IsMounted = true; };
        Unloaded += (_, _) => { if (DataContext is EffectsViewModel vm) vm.IsMounted = false; };
    }
}
