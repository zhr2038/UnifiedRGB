using System.IO;
using UnifiedRgb.App.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UnifiedRgb.App.Branding;

/// <summary>The notification-area brand keeps its identity while a separate corner badge communicates connection state.</summary>
internal static class BrandAssets
{
    internal static Drawing.Icon LoadTrayIcon(TrayState state)
    {
        var token = state switch { TrayState.Ok => "ok", TrayState.Attention => "attention", _ => "disconnected" };
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream($"UnifiedRgb.App.Brand.tray-{token}.ico")
            ?? throw new InvalidDataException("Missing embedded Guangyu tray icon: " + token);
        using var source = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Drawing.Icon)source.Clone(); // the returned icon owns its handle and outlives the resource stream
    }
}
