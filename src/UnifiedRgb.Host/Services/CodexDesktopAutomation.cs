using System.Runtime.InteropServices;

namespace UnifiedRgb.Host.Services;

/// <summary>Existing desktop automation only. SDK declarations verified from Microsoft's win32metadata
/// ExDisp.h/ExDisp.Idl, ShObjIdl_core.h, ShlDisp.h, ShlGuid.h and um/servprov.h.
/// Dual interfaces use CLR's IDispatch prefix; browser/view explicitly include IOleWindow's two slots.</summary>
internal sealed class CodexDesktopAutomation : ICodexDesktopAutomation
{
    private readonly CodexComApartment? _apartment;
    private readonly List<object> _objects = [];
    private readonly ICodexDesktopLease _desktop;
    private IShellDispatch2? _shell;
    private bool _disposed;
    public nint BrowserWindow { get; private set; }
    public nint ViewWindow { get; private set; }

    public CodexDesktopAutomation(ICodexDesktopLease desktop)
    {
        _desktop = desktop;
        var stage = "Desktop.ComInitialize";
        try
        {
            CodexActivationDiagnostics.Stage(stage);
            _apartment = new();
            var clsid = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
            var iid = typeof(IShellWindows).GUID;
            stage = "Desktop.CreateShellWindows"; CodexActivationDiagnostics.Stage(stage);
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 4, ref iid, out var collection));
            var windows = (IShellWindows)Track(collection);
            // Documented desktop recipe: location=CSIDL_DESKTOP (VT_I4); reserved root=VT_EMPTY.
            var (location, root) = DesktopLookupArguments();
            stage = "Desktop.FindWindowSW"; CodexActivationDiagnostics.Stage(stage);
            var dispatch = Track(windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out var hwnd, 1 /* SWFO_NEEDDISPATCH */));
            if (!desktop.OwnsWindow((nint)(uint)hwnd)) throw new InvalidOperationException("Desktop window mismatch");
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            iid = typeof(IShellBrowser).GUID;
            stage = "Desktop.QueryServiceBrowser"; CodexActivationDiagnostics.Stage(stage);
            Marshal.ThrowExceptionForHR(((IComServiceProvider)dispatch).QueryService(ref service, ref iid, out var browserObject));
            var browser = (IShellBrowser)Track(browserObject);
            stage = "Desktop.BrowserGetWindow"; CodexActivationDiagnostics.Stage(stage);
            Marshal.ThrowExceptionForHR(browser.GetWindow(out var browserWindow));
            BrowserWindow = browserWindow;
            stage = "Desktop.QueryActiveShellView"; CodexActivationDiagnostics.Stage(stage);
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out var viewObject));
            var view = (IShellView)Track(viewObject);
            stage = "Desktop.ViewGetWindow"; CodexActivationDiagnostics.Stage(stage);
            Marshal.ThrowExceptionForHR(view.GetWindow(out var viewWindow));
            ViewWindow = viewWindow;
            if (!desktop.OwnsWindow(BrowserWindow) || !desktop.OwnsWindow(ViewWindow) || !desktop.Revalidate())
                throw new InvalidOperationException("Desktop view mismatch");
            iid = new Guid("00020400-0000-0000-C000-000000000046"); // IID_IDispatch
            stage = "Desktop.GetBackgroundItemObject"; CodexActivationDiagnostics.Stage(stage);
            Marshal.ThrowExceptionForHR(view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iid, out var background));
            var folder = (IShellFolderViewDual)Track(background);
            stage = "Desktop.ApplicationShellDispatchQI"; CodexActivationDiagnostics.Stage(stage);
            _shell = (IShellDispatch2)Track(folder.Application);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { Dispose(); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            { CodexActivationDiagnostics.Result(CodexActivationDiagnostics.Failure("Desktop.DisposeAfterFailure", cleanup)); }
            throw new CodexActivationStageException(stage, ex);
        }
    }

    public CodexActivationResult ShellExecute(string file, string arguments, string directory, string verb, int show)
    {
        // Defense in depth at the last native invoke boundary. No caller-controlled payload reaches automation.
        var protectedExplorer = new WindowsCodexActivationPlatform().ExplorerPath;
        if (_shell is null || file != protectedExplorer || arguments != WindowsCodexWindowActivator.ShellArgument
            || directory != "" || verb != "" || show != 1 || !_desktop.Revalidate()
            || !_desktop.OwnsWindow(BrowserWindow) || !_desktop.OwnsWindow(ViewWindow)) return new("Unsupported");
        try
        {
            CodexActivationDiagnostics.Stage("Desktop.ShellExecute.Invoke");
            // IDispatch by the documented name, not a guessed IShellDispatch2 vtable slot.
            ((dynamic)_shell).ShellExecute(protectedExplorer, WindowsCodexWindowActivator.ShellArgument, "", "", 1);
            return new("Requested", 0, Stage: "Desktop.ShellExecute.Invoke", DispatchAttempted: true); // no visibility claim
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return CodexActivationDiagnostics.Failure("Desktop.ShellExecute.Invoke", ex, true); } // Never retry an uncertain invoke.
    }

    internal static (object? Location, object? Root) DesktopLookupArguments() => (0 /* CSIDL_DESKTOP */, null);

    private object Track(object? value)
    {
        // S_FALSE may return no desktop object during shell replacement. Never add null to the release list.
        if (value is null) throw new InvalidOperationException("Desktop automation object is unavailable");
        _objects.Add(value); return value;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shell = null;
        try { for (var i = _objects.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(_objects[i]); }
        finally { _objects.Clear(); _apartment?.Dispose(); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object dispatch, int hwnd, int windowClass, out int cookie);
        void RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)] object? FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object? location,
            [MarshalAs(UnmanagedType.Struct)] ref object? root, int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int InsertMenusSB(nint menu, nint widths);
        [PreserveSig] int SetMenuSB(nint menu, nint holeMenu, nint active);
        [PreserveSig] int RemoveMenusSB(nint menu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint window);
        [PreserveSig] int SendControlMsg(uint id, uint message, nuint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int TranslateAccelerator(nint message);
        [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(nint previous, nint settings, nint browser, nint rect, out nint window);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(nint settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, nint callback, nint parameter);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(nint pidl, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
    }

    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellFolderViewDual
    {
        object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }

    [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch2 { } // QI check; invocation uses IDispatch names from ShlDisp.h.
}
