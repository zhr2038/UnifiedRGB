using System.Windows;
using System.Runtime.InteropServices;
using UnifiedRgb.Contracts;
namespace UnifiedRgb.Setup;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        using var maintenance = InstallationGate.TryEnter();
        if (maintenance is null) { Shutdown(3); return; }
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("UnifiedRgb.Setup"));
        base.OnStartup(e);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string id);
}
