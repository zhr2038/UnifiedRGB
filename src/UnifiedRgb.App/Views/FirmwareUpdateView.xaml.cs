using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.App.Views;

public partial class FirmwareUpdateView : UserControl
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    public FirmwareUpdateView()
    {
        InitializeComponent();
        _poll.Tick += Poll;
        Loaded += (_, _) => _poll.Start();
        Unloaded += (_, _) => _poll.Stop(); // deliberately no cancel/finish on close
    }
    private async void Poll(object? sender, EventArgs e)
    {
        if (DataContext is FirmwareUpdateViewModel vm) await vm.RefreshAsync().ConfigureAwait(true);
    }
    private void OpenGuideClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Docs", "Firmware", "键盘固件开发规范.md");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 2 * 1024 * 1024) throw new IOException("开发规范文件超过读取限制");
            using var reader = new StreamReader(stream);
            var supplement = Path.Combine(AppContext.BaseDirectory, "Docs", "Firmware", "自动进入升级模式.md");
            using var supplementStream = new FileStream(supplement, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (supplementStream.Length > 128 * 1024) throw new IOException("自动进入说明超过读取限制");
            using var supplementReader = new StreamReader(supplementStream);
            var guide = supplementReader.ReadToEnd() + "\n\n---\n\n以下为保留的开发来源规范；进入升级流程以本页前述新说明为准。\n\n" + reader.ReadToEnd();
            new Window { Title = "键盘固件开发规范", Width = 900, Height = 700, Content = new TextBox
            { Text = guide, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) } }.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show("无法读取已打包的开发规范：" + ex.Message, "固件开发规范", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    public static string? PickPackage()
    {
        var picker = new OpenFileDialog { Filter = "准备好的固件包 (*.urgbfw)|*.urgbfw", CheckFileExists = true, Multiselect = false };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }
    public static bool Confirm(string text) => MessageBox.Show(text, "确认固件操作", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static void Export(FirmwareJobStatus status)
    {
        var picker = new SaveFileDialog { Filter = "诊断摘要 (*.txt)|*.txt", FileName = "固件任务-" + status.JobId.ToString("N") + ".txt" };
        if (picker.ShowDialog() != true) return;
        try { ExportNewFiles(status, picker.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show("导出未完成，请选择未使用的新文件名。原始备份与回执未改变。\n" + ex.Message, "诊断导出", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private static void ExportNewFiles(FirmwareJobStatus status, string path)
    {
        // No ledger/original is overwritten; exported copies never become restore admission inputs.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file))
            writer.Write($"任务：{status.JobId}\n版本：{status.Package.Title} {status.Package.Version}\n阶段：{status.Phase}\n说明：{status.Detail}\n包 SHA-256：{status.Package.BundleSha256}\n回执：{status.ReceiptDirectory}\n备份：{status.BackupPath}\n");
        if (status.BackupPath is not null && File.Exists(status.BackupPath)) File.Copy(status.BackupPath, path + ".before-image.bin", overwrite: false);
    }
}
