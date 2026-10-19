using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Per-mailbox one-shot high-resolution wait, interruptible by commands. No busy wait or global timer-resolution change.</summary>
internal sealed unsafe partial class ScreenWaitTimer : IDisposable
{
    private readonly SafeWaitHandle _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x100002);

    public bool TryWait(SemaphoreSlim signal, int milliseconds, out bool signaled)
    {
        signaled = false;
        if (_timer.IsInvalid || milliseconds is < 1 or > 1000) return false;
        var due = -(long)milliseconds * 10_000;
        if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return false;
        var handles = stackalloc IntPtr[2] { signal.AvailableWaitHandle.SafeWaitHandle.DangerousGetHandle(), _timer.DangerousGetHandle() };
        var result = WaitForMultipleObjects(2, handles, false, uint.MaxValue);
        if (result > 1) return false;
        signaled = result == 0 && signal.Wait(0); // the OS wait does not consume SemaphoreSlim's managed count
        return true;
    }

    public void Dispose() => _timer.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr completion, IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForMultipleObjects(uint count, IntPtr* handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint milliseconds);
}
