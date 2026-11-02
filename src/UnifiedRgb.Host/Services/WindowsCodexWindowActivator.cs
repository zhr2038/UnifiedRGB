using System.Runtime.InteropServices;

namespace UnifiedRgb.Host.Services;

/// <summary>Fixed Codex activation, using the existing medium desktop Explorer when the Host is elevated.
/// IID/CLSID/vtable verified against Microsoft's WinSDK ShObjIdl_core.idl (win32metadata).
/// Success means requested, not a visible/foreground window. Construction performs no OS calls.</summary>
internal sealed class WindowsCodexWindowActivator : ICodexWindowActivator
{
    internal const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    internal const uint NoErrorUi = 2;
    internal static readonly Guid ClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    internal const string ShellArgument = @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App";
    private readonly ICodexActivationPlatform _platform;

    public WindowsCodexWindowActivator() : this(new WindowsCodexActivationPlatform()) { }
    internal WindowsCodexWindowActivator(ICodexActivationPlatform platform) => _platform = platform;

    public Task<CodexActivationResult> RequestAsync() => CodexStaActivationWorker.RunAsync(RequestOnCurrentThread);

    private CodexActivationResult RequestOnCurrentThread()
    {
        var stage = "Caller.Identity";
        var dispatchAttempted = false;
        try { return Request(ref stage, ref dispatchAttempted); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = CodexActivationDiagnostics.Failure(stage, ex, dispatchAttempted);
            CodexActivationDiagnostics.Result(failure);
            return failure;
        }
    }

    private CodexActivationResult Request(ref string stage, ref bool dispatchAttempted)
    {
        CodexActivationDiagnostics.Stage(stage);
        var caller = _platform.Caller();
        if (caller is null || !caller.Interactive || caller.Session == 0 || caller.System || caller.Integrity < 0x2000 || caller.Integrity >= 0x4000)
            return new("Unsupported", Stage: stage);
        if (!caller.Elevated && caller.Integrity == 0x2000)
        {
            stage = "Direct.ActivateApplication"; CodexActivationDiagnostics.Stage(stage); dispatchAttempted = true;
            var direct = _platform.ActivateApplication(AppUserModelId, null, NoErrorUi);
            return direct with { Stage = direct.Stage ?? stage };
        }
        if (!caller.Elevated) return new("Unsupported", Stage: stage);
        stage = "Desktop.Acquire"; CodexActivationDiagnostics.Stage(stage);
        using var desktop = _platform.AcquireDesktop();
        stage = "Desktop.ValidateIdentity"; CodexActivationDiagnostics.Stage(stage);
        if (desktop is null || !DesktopEligible(caller, desktop.Identity, _platform.ExplorerPath)) return new("Unsupported", Stage: stage);
        stage = "Desktop.OpenAutomation"; CodexActivationDiagnostics.Stage(stage);
        using var automation = desktop.OpenAutomation();
        stage = "Desktop.ValidateWindows"; CodexActivationDiagnostics.Stage(stage);
        if (automation is null || !desktop.OwnsWindow(automation.BrowserWindow) || !desktop.OwnsWindow(automation.ViewWindow)
            || !desktop.Revalidate() || !DesktopEligible(caller, desktop.Identity, _platform.ExplorerPath)) return new("Unsupported", Stage: stage);
        // All payload is fixed. No Shell.Application is created in the high process, no fallback.
        stage = "Desktop.ShellExecute"; CodexActivationDiagnostics.Stage(stage); dispatchAttempted = true;
        var result = automation.ShellExecute(_platform.ExplorerPath, ShellArgument, "", "", 1);
        return result with { ProcessId = null, Stage = result.Stage ?? stage, DispatchAttempted = true };
    }

    internal static bool DesktopEligible(CodexProcessIdentity caller, CodexProcessIdentity desktop, string explorerPath) =>
        desktop.ProcessId != 0 && desktop.Session == caller.Session && desktop.Session != 0 && desktop.Sid == caller.Sid
        && !desktop.System && !desktop.Elevated && desktop.Integrity == 0x2000
        && string.Equals(desktop.Image, explorerPath, StringComparison.OrdinalIgnoreCase);

    internal static CodexActivationResult Activate(string aumid, string? arguments, uint options)
    {
        using var apartment = new CodexComApartment();
        IApplicationActivationManager? manager = null;
        try
        {
            var clsid = ClassId; var iid = typeof(IApplicationActivationManager).GUID;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4 /* CLSCTX_LOCAL_SERVER */, ref iid, out manager);
            if (hr < 0 || manager is null) return new("Failed", hr, Stage: "Direct.CreateActivationManager");
            CodexActivationDiagnostics.Stage("Direct.ActivateApplication.Invoke");
            hr = manager.ActivateApplication(aumid, arguments, options, out var pid);
            return hr == 0 ? new("Requested", hr, pid, "Direct.ActivateApplication.Invoke", DispatchAttempted: true)
                : new(hr < 0 ? "Failed" : "Unknown", hr, Stage: "Direct.ActivateApplication.Invoke", DispatchAttempted: true);
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items, out uint processId);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager? instance);
}

/// <summary>The returned task owns both the callback and the actual STA thread's exit. The coordinator
/// retains that task through completion; a blocked COM call is never detached or retried.</summary>
internal static class CodexStaActivationWorker
{
    internal static Task<CodexActivationResult> RunAsync(Func<CodexActivationResult> callback)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(new CodexActivationResult("Unsupported", Stage: "Activation.Apartment"));
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return Task.FromResult(callback());
        var answer = new TaskCompletionSource<CodexActivationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CodexActivationDiagnostics.Stage("Activation.STAWorker.Callback");
                answer.TrySetResult(callback());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { answer.TrySetResult(CodexActivationDiagnostics.Failure("Activation.STAWorker.Callback", ex)); }
        }) { IsBackground = false, Name = "UnifiedRgb Codex activation" };
        try
        {
            CodexActivationDiagnostics.Stage("Activation.STAWorker.Start");
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Task.FromResult(CodexActivationDiagnostics.Failure("Activation.STAWorker.Start", ex)); }
        return JoinedAsync();

        async Task<CodexActivationResult> JoinedAsync()
        {
            var result = await answer.Task.ConfigureAwait(false);
            // Callback completion can precede the thread epilogue. Join it before reporting completion.
            await Task.Run(() => thread.Join()).ConfigureAwait(false);
            return result;
        }
    }
}

internal sealed class CodexActivationStageException : Exception
{
    internal string Stage { get; }
    internal bool DispatchAttempted { get; }
    internal CodexActivationStageException(string stage, Exception inner, bool dispatchAttempted = false)
        : base("Codex activation native stage failed.", inner)
    { Stage = stage; DispatchAttempted = dispatchAttempted; HResult = inner.HResult; }
}

/// <summary>Only fixed stage names, exception type and HRESULT are logged; never exception messages, arguments or tokens.</summary>
internal static class CodexActivationDiagnostics
{
    internal static void Stage(string stage) => Serilog.Log.Information("Codex activation stage: {Stage}", stage);
    internal static CodexActivationResult Failure(string stage, Exception exception, bool dispatchAttempted = false)
    {
        if (exception is CodexActivationStageException native)
        { stage = native.Stage; dispatchAttempted |= native.DispatchAttempted; exception = native.InnerException ?? native; }
        return new("Unknown", exception.HResult, Stage: stage, ExceptionType: exception.GetType().FullName,
            DispatchAttempted: dispatchAttempted);
    }
    internal static void Result(CodexActivationResult result) => Serilog.Log.Information(
        "Codex activation result: status={Status}, stage={Stage}, hresult={HResult}, exceptionType={ExceptionType}, dispatchAttempted={DispatchAttempted}",
        result.Status, result.Stage, result.HResult is { } hr ? $"0x{unchecked((uint)hr):X8}" : null,
        result.ExceptionType, result.DispatchAttempted);
}
