using UnifiedRgb.App.Branding;
using UnifiedRgb.App.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UnifiedRgb.App.Tray;

/// <summary>
/// Notification-area icon: gold check = controllers active; gold triangle = attention; muted-gold minus = disconnected.
/// Badge shapes and tooltip text distinguish status within the shared black/gold palette. Menu: Open, Rescan, Exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons;

    public TrayIcon(Action open, Action rescan, Action exit)
    {
        ArgumentNullException.ThrowIfNull(open);
        _icons = new()
        {
            [TrayState.Disconnected] = BrandAssets.LoadTrayIcon(TrayState.Disconnected),
            [TrayState.Ok] = BrandAssets.LoadTrayIcon(TrayState.Ok),
            [TrayState.Attention] = BrandAssets.LoadTrayIcon(TrayState.Attention),
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开光域", null, (_, _) => open());
        menu.Items.Add("重新发现设备", null, (_, _) => rescan());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出界面", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
        Set(TrayState.Disconnected);
    }

    public static string TextOf(TrayState state) => state switch
    {
        TrayState.Ok => "光域 · 灯光设备运行正常",
        TrayState.Attention => "光域 · 有设备需要检查",
        _ => "光域 · 后台未连接",
    };

    public void Set(TrayState state)
    {
        _icon.Icon = _icons[state];
        _icon.Text = TextOf(state);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        foreach (var i in _icons.Values)
        {
            i.Dispose();
        }
    }

}
