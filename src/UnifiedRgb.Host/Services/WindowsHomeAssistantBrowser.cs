using System.Diagnostics;

namespace UnifiedRgb.Host.Services;

/// <summary>Opens the configured HA address in the interactive user's default browser.</summary>
internal sealed class WindowsHomeAssistantBrowser(ICodexActivationPlatform platform, Func<Uri, bool> openDirectly)
{
    public WindowsHomeAssistantBrowser() : this(new WindowsCodexActivationPlatform(), OpenDirectly) { }

    public async Task<bool> OpenAsync(Uri endpoint, Func<bool> stillAdmitted, CancellationToken ct)
    {
        var result = await CodexStaActivationWorker.RunAsync(() => Request(endpoint, stillAdmitted, ct)).ConfigureAwait(false);
        Serilog.Log.Information("HA keyboard browser activation: {Status}", result.Status);
        return result.Status == "Requested";
    }

    internal CodexActivationResult Request(Uri endpoint, Func<bool> stillAdmitted, CancellationToken ct)
    {
        if (!endpoint.IsAbsoluteUri || KeyboardHomeAssistantOptions.ParseEndpoint(endpoint.AbsoluteUri) is null)
            return new("Unsupported");
        var caller = platform.Caller();
        if (caller is null || !caller.Interactive || caller.Session == 0 || caller.System || caller.Integrity < 0x2000 || caller.Integrity >= 0x4000)
            return new("Unsupported");
        if (!caller.Elevated && caller.Integrity == 0x2000)
            return Invoke(openDirectly);
        if (!caller.Elevated) return new("Unsupported");
        using var desktop = platform.AcquireDesktop();
        if (desktop is null || !WindowsCodexWindowActivator.DesktopEligible(caller, desktop.Identity, platform.ExplorerPath))
            return new("Unsupported");
        using var automation = desktop.OpenAutomation();
        if (automation is null || !desktop.OwnsWindow(automation.BrowserWindow) || !desktop.OwnsWindow(automation.ViewWindow)
            || !desktop.Revalidate() || !WindowsCodexWindowActivator.DesktopEligible(caller, desktop.Identity, platform.ExplorerPath))
            return new("Unsupported");
        return Invoke(automation.OpenHomeAssistant);

        CodexActivationResult Invoke(Func<Uri, bool> open)
        {
            if (ct.IsCancellationRequested || !stillAdmitted()) return new("Cancelled");
            return open(endpoint) ? new("Requested") : new("Failed");
        }
    }

    private static bool OpenDirectly(Uri endpoint)
    {
        using var process = Process.Start(new ProcessStartInfo(endpoint.AbsoluteUri) { UseShellExecute = true });
        return true;
    }
}
