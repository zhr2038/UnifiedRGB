using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Transports.Hid;

/// <summary>A short, owned HID read cancellation window. Dispose only after the actual read has settled.</summary>
public sealed partial class HidReadWindow : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private TimerWaitHandle? _timer;
    private RegisteredWaitHandle? _registration;
    private Task _cancelling = Task.CompletedTask;
    private bool _disposed;

    private HidReadWindow(CancellationToken ct) { _stop = CancellationTokenSource.CreateLinkedTokenSource(ct); _token = _stop.Token; }
    public CancellationToken Token => _token;
    public bool HighResolutionUsed { get; private set; }

    public static HidReadWindow Start(TimeSpan duration, CancellationToken ct)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        var window = new HidReadWindow(ct);
        try
        {
            if (!window.StartHighResolution(duration)) window._stop.CancelAfter(duration);
            return window;
        }
        catch { window.Dispose(); throw; }
    }

    private bool StartHighResolution(TimeSpan duration)
    {
        if (!OperatingSystem.IsWindows()) return false;
        SafeWaitHandle handle;
        try { handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x1F0003); }
        catch (EntryPointNotFoundException) { return false; }
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError(); handle.Dispose();
            if (error is 50 or 87 or 120) return false; // OS does not support high-resolution waitable timers.
            throw new Win32Exception(error, "Cannot create the HID read-window timer.");
        }
        _timer = new TimerWaitHandle(handle);
        var due = -Math.Max(1, duration.Ticks); // relative100ns units
        if (!SetWaitableTimer(handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot arm the HID read-window timer.");
        _registration = ThreadPool.RegisterWaitForSingleObject(_timer, static (state, _) => ((HidReadWindow)state!).Expire(),
            this, Timeout.InfiniteTimeSpan, executeOnlyOnce: true);
        HighResolutionUsed = true;
        return true;
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Cancellation callbacks run asynchronously, so a resumed read can dispose this window without waiting on its
            // own timer callback. Retain the CTS until those callbacks finish, including the disposal/cancellation race.
            _cancelling = _stop.CancelAsync();
        }
    }

    public void Dispose()
    {
        Task cancelling;
        lock (_gate) { if (_disposed) return; _disposed = true; cancelling = _cancelling; }
        _registration?.Unregister(null); // Any already queued callback sees _disposed under the same gate and touches no CTS.
        _timer?.Dispose();
        if (cancelling.IsCompleted) { _ = cancelling.Exception; _stop.Dispose(); }
        else _ = cancelling.ContinueWith(static (task, state) => { _ = task.Exception; ((CancellationTokenSource)state!).Dispose(); },
            _stop, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class TimerWaitHandle : WaitHandle
    { public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion,
        IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
