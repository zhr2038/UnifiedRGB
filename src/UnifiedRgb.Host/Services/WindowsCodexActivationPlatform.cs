using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Host.Services;

internal sealed record CodexProcessIdentity(uint ProcessId, string Sid, uint Session, uint Integrity,
    bool Elevated, bool System, bool Interactive, string Image);

internal interface ICodexActivationPlatform
{
    CodexProcessIdentity? Caller();
    string ExplorerPath { get; }
    ICodexDesktopLease? AcquireDesktop();
    CodexActivationResult ActivateApplication(string aumid, string? arguments, uint options);
}

internal interface ICodexDesktopLease : IDisposable
{
    CodexProcessIdentity Identity { get; }
    bool OwnsWindow(nint window);
    bool Revalidate();
    ICodexDesktopAutomation? OpenAutomation();
}

internal interface ICodexDesktopAutomation : IDisposable
{
    nint BrowserWindow { get; }
    nint ViewWindow { get; }
    CodexActivationResult ShellExecute(string file, string arguments, string directory, string verb, int show);
    bool OpenHomeAssistant(Uri endpoint) => false;
}

/// <summary>Query-only process/token access. No impersonation, token duplication, process creation or live calls at construction.</summary>
internal sealed class WindowsCodexActivationPlatform : ICodexActivationPlatform
{
    public string ExplorerPath
    {
        get
        {
            var path = new char[32768];
            var length = GetWindowsDirectory(path, (uint)path.Length);
            if (length == 0 || length >= path.Length) throw new Win32Exception();
            return Path.Combine(new string(path, 0, (int)length), "explorer.exe");
        }
    }

    public CodexProcessIdentity? Caller()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return null;
        // Process token, not an impersonating thread identity or administrator-role membership.
        using var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, (uint)Environment.ProcessId);
        return ReadIdentity(process, (uint)Environment.ProcessId);
    }

    public CodexActivationResult ActivateApplication(string aumid, string? arguments, uint options) =>
        WindowsCodexWindowActivator.Activate(aumid, arguments, options);

    public ICodexDesktopLease? AcquireDesktop()
    {
        var window = GetShellWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0) return null;
        var process = OpenProcess(0x1000, false, pid);
        try
        {
            var identity = ReadIdentity(process, pid);
            if (identity is null) { process.Dispose(); return null; }
            return new DesktopLease(process, window, identity);
        }
        catch { process.Dispose(); throw; }
    }

    private sealed class DesktopLease(SafeProcessHandle process, nint window, CodexProcessIdentity identity) : ICodexDesktopLease
    {
        public CodexProcessIdentity Identity { get; } = identity;
        public bool OwnsWindow(nint candidate) => candidate != 0 && GetWindowThreadProcessId(candidate, out var pid) != 0 && pid == Identity.ProcessId;
        public bool Revalidate() => GetShellWindow() == window && OwnsWindow(window)
            && GetExitCodeProcess(process, out var code) && code == 259
            && ReadIdentity(process, Identity.ProcessId) == Identity;
        public ICodexDesktopAutomation? OpenAutomation() => new CodexDesktopAutomation(this);
        public void Dispose() => process.Dispose();
    }

    private static CodexProcessIdentity? ReadIdentity(SafeProcessHandle process, uint pid)
    {
        if (process.IsInvalid || !GetExitCodeProcess(process, out var code) || code != 259) return null;
        var path = new char[32768]; var size = (uint)path.Length;
        if (!QueryFullProcessImageName(process, 0, path, ref size) || !OpenProcessToken(process, 8 /* TOKEN_QUERY */, out var token)) return null;
        using (token)
        {
            var sid = TokenValue(token, 1 /* TokenUser */, p => new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value);
            var integrity = TokenValue(token, 25 /* TokenIntegrityLevel */, p =>
            {
                var integritySid = Marshal.ReadIntPtr(p);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(integritySid));
                if (count == 0) throw new Win32Exception();
                return unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(integritySid, (uint)(count - 1))));
            });
            return new(pid, sid, TokenValue(token, 12 /* TokenSessionId */, p => unchecked((uint)Marshal.ReadInt32(p))),
                integrity, TokenValue(token, 20 /* TokenElevation */, p => Marshal.ReadInt32(p) != 0),
                sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20", Environment.UserInteractive, new string(path, 0, (int)size));
        }
    }

    private static T TokenValue<T>(SafeAccessTokenHandle token, int kind, Func<nint, T> read)
    {
        GetTokenInformation(token, kind, 0, 0, out var size);
        if (size <= 0) throw new Win32Exception();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, kind, memory, size, out _)) throw new Win32Exception();
            return read(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] image, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetWindowsDirectory([Out] char[] path, uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint memory, int size, out int required);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}

/// <summary>COM stays synchronous on the one tracked coordinator worker. Out-of-process automation is marshalled
/// from its existing desktop apartment; both STA callers and MTA workers balance their own initialization.</summary>
internal sealed class CodexComApartment : IDisposable
{
    private bool _disposed;
    public CodexComApartment() => Marshal.ThrowExceptionForHR(CoInitializeEx(0, Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? 2u : 0u));
    public void Dispose() { if (!_disposed) { _disposed = true; CoUninitialize(); } }
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint concurrency);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
