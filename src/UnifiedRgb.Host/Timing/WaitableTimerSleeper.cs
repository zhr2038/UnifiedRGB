using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Core.Clock;

namespace UnifiedRgb.Host.Timing;

/// <summary>
/// Precise sleeper on a Windows high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win10 1803+):
/// kernel wait until ~0.5 ms before the deadline, then a short spin. Much cheaper than HybridSleeper's spin when the
/// system timer resolution is 15.6 ms. One instance per thread (the timer handle is not shared).
/// </summary>
public sealed partial class WaitableTimerSleeper : IPreciseSleeper, IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly long SpinTailTicks = Stopwatch.Frequency / 2000;

    private readonly SafeWaitHandle _timer;

    private WaitableTimerSleeper(SafeWaitHandle timer) => _timer = timer;

    /// <summary>High-resolution sleeper when the OS supports it, otherwise the portable HybridSleeper.</summary>
    public static IPreciseSleeper Create()
    {
        var h = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (h.IsInvalid)
        {
            h.Dispose();
            return new HybridSleeper();
        }

        return new WaitableTimerSleeper(h);
    }

    public void SleepUntil(long deadlineTicks)
    {
        var wait = deadlineTicks - Stopwatch.GetTimestamp() - SpinTailTicks;
        if (wait > 0)
        {
            // relative due time in 100 ns units is negative
            var due = -(long)(wait * 10_000_000.0 / Stopwatch.Frequency);
            if (due < 0 && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                _ = WaitForSingleObject(_timer, Infinite);
            }
        }

        var spin = default(SpinWait);
        while (Stopwatch.GetTimestamp() < deadlineTicks)
        {
            spin.SpinOnce(-1);
        }
    }

    public void Dispose() => _timer.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
