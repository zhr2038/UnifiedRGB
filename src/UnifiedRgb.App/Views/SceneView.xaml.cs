using System.Windows.Controls;
using UnifiedRgb.App.ViewModels;

namespace UnifiedRgb.App.Views;

public partial class SceneView : UserControl
{
    public SceneView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is SceneViewModel vm) vm.IsMounted = true; };
        Unloaded += (_, _) => { if (DataContext is SceneViewModel vm) vm.IsMounted = false; };
    }
}
