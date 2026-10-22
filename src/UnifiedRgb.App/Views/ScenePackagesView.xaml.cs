using System.Windows.Controls;
using System.Windows;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.Views;

public partial class ScenePackagesView : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];
    public ScenePackagesView()
    {
        InitializeComponent();
        Unloaded += (_, _) => { if (DataContext is ScenePackagesViewModel vm) vm.UpdateInputErrors(this, 0); _errors.Clear(); };
    }
    private void InputValidation(object sender, ValidationErrorEventArgs e)
    {
        if (e.Action == ValidationErrorEventAction.Added) _errors.Add(e.Error); else _errors.Remove(e.Error);
        if (DataContext is ScenePackagesViewModel vm) vm.UpdateInputErrors(this, _errors.Count);
    }
}
