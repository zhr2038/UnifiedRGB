using System.Collections.ObjectModel;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.ViewModels;

public sealed record ControllerItem(ControllerDto Dto)
{
    public string Id => Dto.Id;

    public string Name => Dto.Name;
    private bool NeedsNativeVerification => !Dto.ControlVerified && NativeBringupViewModel.NativeClasses.Contains(Dto.DeviceClass, StringComparer.OrdinalIgnoreCase);
    public string ChineseName => DisplayNames.Controller(Dto.Id, Dto.Name);
    public string ChineseState => DisplayNames.IsDiscoveryPort(Dto) ? "发现端口" : DisplayNames.State(Dto.State);
    public string ChineseZones => DisplayNames.IsDiscoveryPort(Dto) ? "只用于探测 · 不参与灯效" : $"{Dto.Zones.Count} 个分区 · {Dto.Zones.Sum(z => z.LedCount)} 个逻辑灯位";
    public string ChineseHint => Dto.State switch
    {
        ControllerStates.Active => "", ControllerStates.BringUp => "正在进行独立硬件校验，暂不接收场景帧。",
        ControllerStates.Contended => $"其他程序正在写入：{Dto.BlockedBy}", ControllerStates.Offline => "设备未打开，详情见诊断。",
        _ => !NeedsNativeVerification ? $"未接管：{Dto.BlockedBy}" : $"控制待实机校验：{Dto.BlockedBy}",
    };

    public string Vendor => Dto.Vendor;

    public string State => Dto.State;

    /// <summary>Zone and LED counts; native aggregate zones (one colour on every record, no spatial claim) are counted as unmapped.</summary>
    public string Zones => Dto.Zones.Count(z => !z.Mapped) is var unmapped and > 0
        ? $"{Dto.Zones.Count} zone(s), {Dto.Zones.Sum(z => z.LedCount)} LEDs, {unmapped} unmapped"
        : $"{Dto.Zones.Count} zone(s), {Dto.Zones.Sum(z => z.LedCount)} LEDs";

    /// <summary>
    /// What the user can do about a non-active controller (ARCHITECTURE §7.2: name the process to close). A native controller
    /// whose control is not verified, or whose class takeover is off, is blocked by a gate, not by a process (NATIVE design §2).
    /// </summary>
    public string Hint => Dto.State switch
    {
        ControllerStates.BringUp => $"Native bring-up session: {Dto.BlockedBy}. No scene frames reach it until the session ends.",
        ControllerStates.Blocked when Dto.BlockedBy?.StartsWith("takeover off", StringComparison.OrdinalIgnoreCase) == true => !NeedsNativeVerification
            ? $"Takeover is off for {Dto.DeviceClass}: turn it on below."
            : $"Takeover is off for {Dto.DeviceClass}: a bring-up session needs it on (below). Control not verified: no scene frames are sent.",
        ControllerStates.Blocked when NeedsNativeVerification =>
            $"Control not verified ({Dto.BlockedBy}). No scene frames are sent; only an explicit Diagnostics › Native bring-up session opens it.",
        ControllerStates.Blocked => $"Blocked by {Dto.BlockedBy}: close it, or enable Takeover for {Dto.DeviceClass}.",
        ControllerStates.Contended => $"Paused: {Dto.BlockedBy} is writing to this device again.",
        ControllerStates.Offline => "Offline: not opened (see Host log).",
        _ => string.Empty,
    };
}

/// <summary>Per device class Takeover switch. Null = unknown (the API reports the state only when it is set).</summary>
public sealed class TakeoverItem(string deviceClass, Func<TakeoverItem, Task> apply) : ObservableObject
{
    private bool? _enabled;
    private string _message = string.Empty;

    public string DeviceClass { get; } = deviceClass;
    public string ChineseName => DeviceClass switch { "LianLi" => "联力无线设备", "MsiGpu" => "微星显卡", "AsusAura" => "华硕主板", "EneDram" => "内存灯光", _ => "OpenRGB 兼容接口" };
    public string StateText => Enabled is null ? "状态未读取" : Enabled == true ? "已开启接管" : "已关闭接管";

    public bool? Enabled
    {
        get => _enabled;
        set
        {
            if (value is not null && SetProperty(ref _enabled, value))
            {
                _ = apply(this);
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    internal void SetFromHost(bool enabled)
    {
        _enabled = enabled;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(StateText));
    }
}

public sealed class DevicesViewModel : ObservableObject
{
    public static readonly string[] HardwareClasses = ["LianLi", "MsiGpu", "OpenRgb", "AsusAura", "EneDram"];

    private readonly PipeClient _client;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _onError;
    private readonly Func<string, bool> _confirm;
    private string _lianLiMessage = string.Empty;

    /// <param name="confirm">Asks the user before the flash write of "Save current lighting to fans" (null: no prompt).</param>
    public DevicesViewModel(PipeClient client, IUiDispatcher ui, Action<Exception> onError, Func<string, bool>? confirm = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _confirm = confirm ?? (_ => true);
        Takeover = new(HardwareClasses.Select(c => new TakeoverItem(c, ApplyTakeoverAsync)));
        RescanCommand = new AsyncCommand(RescanAsync, onError);
        SaveToFansCommand = new AsyncCommand(SaveToFansAsync, onError);
        ResyncLianLiCommand = new AsyncCommand(ResyncLianLiAsync, onError);
        EnableTakeoverCommand = new AsyncCommand(p => SetTakeoverAsync(p as TakeoverItem, true), onError);
        DisableTakeoverCommand = new AsyncCommand(p => SetTakeoverAsync(p as TakeoverItem, false), onError);
    }

    public const string SaveToFansQuestion =
        "将当前灯效写入联力设备闪存，作为后台启动前的开机灯效？每分钟最多写入一次，闪存写入次数有限。";

    public ObservableCollection<ControllerItem> Controllers { get; } = [];
    public IEnumerable<ControllerItem> VisibleControllers => Controllers.Where(c => !DisplayNames.IsDiscoveryPort(c.Dto));
    public AsyncCommand EnableTakeoverCommand { get; }
    public AsyncCommand DisableTakeoverCommand { get; }

    /// <summary>Mounting editors of the discovered Lian Li devices (WP-11).</summary>
    public ObservableCollection<MountingItem> Mountings { get; } = [];

    public ObservableCollection<TakeoverItem> Takeover { get; }

    public AsyncCommand RescanCommand { get; }

    /// <summary>"Save current lighting to fans" (RF 21, ARCHITECTURE-D2 §5): explicit only, Host-gated.</summary>
    public AsyncCommand SaveToFansCommand { get; }

    /// <summary>"Re-sync Lian Li": re-uploads the current loops with a fresh EffectIndex (D2 §2).</summary>
    public AsyncCommand ResyncLianLiCommand { get; }

    /// <summary>Outcome of the last Save/Re-sync.</summary>
    public string LianLiMessage
    {
        get => _lianLiMessage;
        private set => SetProperty(ref _lianLiMessage, value);
    }

    /// <summary>True when an active Lian Li controller exists (the buttons are only meaningful then).</summary>
    public bool HasLianLi => Controllers.Any(c => c.Dto.State == ControllerStates.Active
        && string.Equals(c.Dto.DeviceClass, "LianLi", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ControllerDto> Snapshot => Controllers.Select(c => c.Dto).ToArray();

    public event EventHandler? Changed;

    public async Task RefreshAsync()
    {
        var controllers = await _client.Require().ListControllersAsync().ConfigureAwait(true);
        Update(controllers);
        if (controllers.Any(c => string.Equals(c.DeviceClass, "LianLi", StringComparison.OrdinalIgnoreCase)))
        {
            await RefreshMountingsAsync().ConfigureAwait(true);
        }
    }

    public async Task RefreshMountingsAsync()
    {
        var list = await _client.Require().ListMountingsAsync().ConfigureAwait(true);
        _ui.Post(() =>
        {
            Mountings.Clear();
            foreach (var m in list)
            {
                Mountings.Add(new MountingItem(m, _client, _ui, _onError));
            }
        });
    }

    public void Update(IReadOnlyList<ControllerDto> controllers) => _ui.Post(() =>
    {
        Controllers.Clear();
        foreach (var c in controllers)
        {
            Controllers.Add(new ControllerItem(c));
        }

        OnPropertyChanged(nameof(HasLianLi));
        OnPropertyChanged(nameof(VisibleControllers));
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private async Task RescanAsync() => Update(await _client.Require().RescanAsync().ConfigureAwait(true));

    internal async Task SaveToFansAsync()
    {
        if (!_confirm(SaveToFansQuestion))
        {
            return;
        }

        var n = await _client.Require().SaveLianLiConfigAsync().ConfigureAwait(true);
        _ui.Post(() => LianLiMessage = $"Saved to fans through {n} dongle(s).");
    }

    internal async Task ResyncLianLiAsync()
    {
        var n = await _client.Require().ResyncLianLiAsync().ConfigureAwait(true);
        _ui.Post(() => LianLiMessage = $"Re-sync: {n} loop(s) re-uploaded.");
    }

    internal async Task ApplyTakeoverAsync(TakeoverItem item)
    {
        try
        {
            var r = await _client.Require().SetTakeoverAsync(item.DeviceClass, item.Enabled == true).ConfigureAwait(true);
            _ui.Post(() =>
            {
                item.SetFromHost(r.Enabled);
                item.Message = Describe(r);
            });
            await RefreshAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // UI boundary
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _onError(ex);
        }
    }

    private async Task SetTakeoverAsync(TakeoverItem? item, bool enabled)
    {
        if (item is null) return;
        var result = await _client.Require().SetTakeoverAsync(item.DeviceClass, enabled).ConfigureAwait(true);
        item.SetFromHost(result.Enabled);
        item.Message = Describe(result);
        await RefreshAsync().ConfigureAwait(true);
    }

    public static string Describe(TakeoverDto r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var parts = new List<string>();
        if (r.StoppedServices.Count > 0)
        {
            parts.Add($"Stopped {string.Join(", ", r.StoppedServices)} (restored when the Host exits).");
        }

        if (r.ProcessesToClose.Count > 0)
        {
            parts.Add($"Please close: {string.Join(", ", r.ProcessesToClose)}.");
        }

        if (r.Error is not null)
        {
            parts.Add($"Error: {r.Error}");
        }

        return parts.Count == 0 ? (r.Enabled ? "Takeover on; no vendor writer running." : "Takeover off.") : string.Join(" ", parts);
    }
}
