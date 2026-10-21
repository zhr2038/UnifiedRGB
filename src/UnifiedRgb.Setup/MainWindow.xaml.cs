using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Setup;

public partial class MainWindow : Window
{
    private SetupReport? _report;
    private SetupConfigSnapshot? _snapshot;
    private readonly SetupConfigStore _store = new(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
    private FamilyChoice[] _choices = [];
    private bool _busy;
    private readonly bool _preview = Environment.GetCommandLineArgs().Contains("--review", StringComparer.Ordinal);

    public MainWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e) => await InspectAsync();
    private async void OnInspect(object sender, RoutedEventArgs e) => await InspectAsync();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) e.Cancel = true; // bounded inspection/publication must finish before the process exits
        base.OnClosing(e);
    }

    private async Task InspectAsync()
    {
        SetBusy(true);
        try
        {
            if (!_preview) RequireStopped();
            _snapshot = await _store.ReadAsync(CancellationToken.None);
            _report = _preview
                ? new(DateTimeOffset.UtcNow, new(AppContext.BaseDirectory), Enum.GetValues<SetupFamily>().Select(f =>
                    new SetupFamilyReport(f, SetupSupport.NotPresent, [], false, false, "界面检查示例：未访问设备。接入后会显示本机设备与前提条件。")).ToArray(),
                    new(LianLiPairingState.NoReceiver, null, null, [], [], "未连接"), [], [])
                : await Task.Run(() =>
                {
                    using var maintenance = InstallationGate.TryEnter() ?? throw new IOException("安装或更新正在进行，请稍后重试。");
                    RequireStopped();
                    return new SetupInspector(new WindowsSetupSystem(), new(AppContext.BaseDirectory)).Inspect(CancellationToken.None);
                });
            var defaults = SetupDefaults.From(_snapshot);
            _choices = _report.Families.Select(f => new FamilyChoice(f)
            {
                Enable = defaults.Families.Any(c => c.Family == f.Family && c.Enable),
                Authorize = defaults.Families.Any(c => c.Family == f.Family && c.Enable && c.AuthorizeOutput),
            }).ToArray();
            Families.ItemsSource = _choices;
            Hardware.IsChecked = defaults.Mode == SetupMode.Hardware;
            Demo.IsChecked = Hardware.IsChecked != true;
            Status.Text = _preview ? "仅界面检查，保存与设备访问均关闭。" : _snapshot.Error ?? "检查完成。保存前会显示所有配置变更；场景、校准和屏幕方向不会被改写。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _report = null;
            Status.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_preview || _report is null || _snapshot is null) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" }); Close(); }
            catch (System.ComponentModel.Win32Exception ex) { Status.Text = "未获得管理员权限：" + ex.Message; }
            return;
        }
        var selection = new SetupSelection(Demo.IsChecked == true ? SetupMode.Demo : SetupMode.Hardware,
            Demo.IsChecked == true ? [] : _choices.Select(c => new SetupFamilySelection(c.Family, c.Enable, c.Authorize)).ToArray(),
            Hardware.IsChecked == true && !SetupDefaults.HasConfiguredLianLiDevices(_snapshot) && _choices.Any(c => c.Family == SetupFamily.LianLi && c.Enable)
                ? _report.LianLi.Proposals.Select(p => p.Mac).ToArray() : null);
        var plan = SetupPlanner.BuildPlan(_report, selection, _snapshot);
        if (!plan.CanSave) { Status.Text = string.Join("\n", plan.Findings.Select(f => f.Message)); return; }
        if (!plan.HasChanges) { Status.Text = "配置没有变化。"; return; }
        var summary = string.Join("\n", plan.Changes.Select(c => c.ToString()));
        if (MessageBox.Show(summary + "\n\n将保留原配置备份。确认保存？", "光域 · 配置变更", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetBusy(true);
        try
        {
            // Use a dedicated synchronous worker: the same thread owns the OS maintenance mutex through publication.
            var saved = await Task.Run(() =>
            {
                using var maintenance = InstallationGate.TryEnter() ?? throw new IOException("安装或更新正在进行，请稍后重试。");
                RequireStopped();
                return _store.SavePlan(plan, _snapshot.Sha256, CancellationToken.None);
            });
            Status.Text = $"{saved.Status}：{saved.Detail}\n配置完成后，请从开始菜单打开光域，进行设备验证与场景绑定。";
        }
        catch (IOException ex) { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private static void RequireStopped()
    {
        if (Process.GetProcessesByName("UnifiedRgb.Host").Length != 0 || Process.GetProcessesByName("UnifiedRgb.App").Length != 0)
            throw new IOException("请先退出光域界面并停止后台，再进行设备配置。现有场景与草稿不会被强制关闭。");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Families.IsEnabled = Demo.IsEnabled = Hardware.IsEnabled = CloseButton.IsEnabled = !busy;
        InspectButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && !_preview && _report is not null;
    }
}

public sealed class FamilyChoice(SetupFamilyReport report)
{
    public SetupFamily Family => report.Family;
    public string Name => Family switch
    {
        SetupFamily.LianLi => "联力无线灯光", SetupFamily.MsiGpu => "MSI 显卡灯光", SetupFamily.AsusAura => "ASUS Aura 主板灯光",
        SetupFamily.EneDram => "ENE 内存灯光", SetupFamily.ScreenTlLcd => "联力风扇屏幕", SetupFamily.ScreenH2 => "冷头屏幕", _ => "TURZX 机箱长屏",
    };
    public string TechnicalDetail => report.Detail;
    public string Detail => (report.Support switch
    {
        SetupSupport.Supported => "已识别支持的设备身份。启用后仍需检查输出、位置和安装方向。",
        SetupSupport.Unknown => "已识别相关控制器，实际设备仍待发现与验证。",
        SetupSupport.PrerequisiteMissing => "设备依赖缺失或未通过校验。将鼠标停在此处查看具体条件。",
        SetupSupport.Unsupported => "已检测到相关设备，但其身份或连接拓扑尚未验证，不开放输出。",
        _ => "未检测到支持的设备。可先使用演示模式。",
    }) + (report.Identities.Count > 0 ? "\n" + string.Join(" · ", report.Identities) : string.Empty);
    public bool CanEnable => report.CanEnable || Enable;
    public bool CanAuthorize => report.CanAuthorizeOutput || Authorize;
    public bool Enable { get; set; }
    public bool Authorize { get; set; }
}
