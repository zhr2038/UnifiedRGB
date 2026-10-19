using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using UnifiedRgb.App.Views;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.UiQa;

/// <summary>Live WPF component checks. These are neither desktop screenshots nor physical keyboard tests.</summary>
internal static class UiQaChecks
{
    internal static async Task RunAsync(Window window, ScreenWorkspaceView page, ScreensViewModel vm, FakeScreenRpc rpc, string path, bool verifyWrites)
    {
        var results = new List<object>();
        void Check(string name, bool passed, string detail) => results.Add(new { name, passed, detail });
        var visibleText = Program.Descendants(page).OfType<TextBlock>().Where(x => x.IsVisible).Select(x => x.Text).ToArray();
        Check("display-member-labels", !visibleText.Any(x => x.Contains("Dto =", StringComparison.Ordinal) || x.Contains("ValueChoice {", StringComparison.Ordinal)), string.Join(" | ", visibleText.Where(x => x.Contains("Choice", StringComparison.Ordinal))));
        Check("explicit-unsent-preview", !((FrameworkElement)page.FindName("PreviewRegion")).IsVisible || visibleText.Contains("实际编码预览，未发送"), "The preview region, when shown, labels actual encoding as unsent.");
        Check("responsive-mode", page.LayoutMode == ScreenWorkspaceView.LayoutFor(page.ActualWidth), $"Page {page.ActualWidth:0.##} DIP => {page.LayoutMode}");
        var apply = Program.Descendants(page).OfType<Button>().Single(x => Equals(x.Content, "应用到此屏幕"));
        var bounds = apply.TransformToAncestor(window).TransformBounds(new Rect(apply.RenderSize));
        Check("primary-action-within-client", bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1 && apply.ActualHeight is >= 36 and <= 60,
            $"{bounds}; client {window.ActualWidth} x {window.ActualHeight} DIP");
        Check("write-availability", apply.IsEnabled == vm.CanApplyContent, $"Enabled={apply.IsEnabled}; Available={vm.Available}; Busy={vm.EditorBusy}");
        if (page.LayoutMode == ScreenWorkspaceLayout.Compact)
        {
            var preview = (FrameworkElement)page.FindName("PreviewRegion"); var properties = (FrameworkElement)page.FindName("PropertiesRegion");
            var readingError = vm.HasError && ((Expander)page.FindName("CompactErrorDetails")).IsExpanded;
            if (readingError)
                Check("compact-error-reading-mode", !((FrameworkElement)page.FindName("WorkAreas")).IsVisible && apply.IsVisible, "Expanded error reading hides clipped work areas; the footer remains reachable.");
            else
            {
            Check("compact-one-region", preview.IsVisible != properties.IsVisible, $"Preview={preview.IsVisible}; properties={properties.IsVisible}");
            var selectedTab = (Button)page.FindName(preview.IsVisible ? "PreviewTab" : "PropertiesTab");
            Check("compact-active-region-cue", AutomationProperties.GetHelpText(selectedTab) == "当前显示的区域", "The active region has the selected style and accessible help.");
            Check("compact-usable-region", Math.Max(preview.IsVisible ? preview.ActualHeight : 0, properties.IsVisible ? properties.ActualHeight : 0) >= 80,
                $"Preview={preview.ActualHeight:0.##}, properties={properties.ActualHeight:0.##} DIP");
            }
        }
        Check("no-startup-writes", rpc.Writes.Count == 0, string.Join(",", rpc.Writes));
        if (vm.CanEditContent && vm.Available)
        {
            var fps = (TextBox)page.FindName("FpsEditor"); var before = vm.Fps;
            fps.Text = "不是数字"; BindingOperations.GetBindingExpression(fps, TextBox.TextProperty)!.UpdateSource();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("invalid-numeric-input-blocks-actions", Validation.GetHasError(fps) && !apply.IsEnabled && !((Button)page.FindName("GeneratePreview")).IsEnabled && vm.Fps == before && rpc.Writes.Count == 0,
                "Invalid text must not silently apply the previous FPS value.");
            fps.Text = before.ToString(System.Globalization.CultureInfo.InvariantCulture); BindingOperations.GetBindingExpression(fps, TextBox.TextProperty)!.UpdateSource();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("corrected-input-recovers-actions", !Validation.GetHasError(fps) && apply.IsEnabled == vm.CanApplyContent, "Correcting the field restores actions without sending.");
        }
        if (verifyWrites)
        {
            var editor = (TextBox)page.FindName("BackgroundEditor");
            editor.Text = "#345678"; BindingOperations.GetBindingExpression(editor, TextBox.TextProperty)!.UpdateSource();
            ((ComboBox)page.FindName("RotationPicker")).SelectedItem = 90;
            await vm.SaveDeviceSettingsCommand.ExecuteAsync();
            Check("local-save-only", rpc.Writes.SequenceEqual(["SetScreenDeviceSettings"]) && vm.Dirty && !vm.DeviceSettingsDirty, string.Join(",", rpc.Writes));
            vm.Rotation = 270;
            await vm.ApplyCommand.ExecuteAsync();
            var scene = await rpc.GetScreenSceneAsync();
            Check("apply-content-only", rpc.Writes.SequenceEqual(["SetScreenDeviceSettings", "SetScreenScene"]) && !vm.Dirty && vm.DeviceSettingsDirty && scene.Assignments.Single(x => x.ScreenId == vm.Selected!.Id).Rotation == 90, string.Join(",", rpc.Writes));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
