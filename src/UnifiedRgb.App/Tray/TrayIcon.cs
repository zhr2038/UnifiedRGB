using System.Runtime.InteropServices;
using UnifiedRgb.App.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UnifiedRgb.App.Tray;

/// <summary>
/// Notification-area icon (ARCHITECTURE §9): grey = Host not connected, green = all controllers active, amber = something is
/// blocked/contended/offline. Menu: Open, Rescan, Exit. Icons are drawn at startup and released with DestroyIcon.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons;

    public TrayIcon(Action open, Action rescan, Action exit)
    {
        ArgumentNullException.ThrowIfNull(open);
        _icons = new()
        {
            [TrayState.Disconnected] = Draw(Drawing.Color.Gray),
            [TrayState.Ok] = Draw(Drawing.Color.LimeGreen),
            [TrayState.Attention] = Draw(Drawing.Color.Orange),
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
            var h = i.Handle;
            i.Dispose();
            _ = DestroyIcon(h);
        }
    }

    private static Drawing.Icon Draw(Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(16, 16);
        using (var g = Drawing.Graphics.FromImage(bmp))
        using (var b = new Drawing.SolidBrush(color))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            g.FillEllipse(b, 1, 1, 14, 14);
        }

        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
