namespace UnifiedRgb.Contracts;

/// <summary>
/// Shared startup/maintenance gate. The installer owns it across inspection, task switching and cleanup;
/// App and Host own it only during synchronous startup. Hardware ownership is a separate lifetime lease.
/// </summary>
public static class InstallationGate
{
    public const string Name = @"Global\UnifiedRgb.Installation";

    public static IDisposable? EnterWhenAvailable(TimeSpan timeout, string name = Name)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var lease = TryEnter(name);
            if (lease is not null) return lease;
            Thread.Sleep(50);
        }
        while (deadline.Elapsed < timeout);
        return null;
    }

    public static IDisposable? TryEnter(string name = Name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool entered;
            try { entered = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { entered = true; }
            if (entered) return new Lease(mutex);
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    // OS mutex ownership is thread-affine: callers must dispose on the entering thread, before awaiting.
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _mutex, null);
            if (held is null) return;
            try { held.ReleaseMutex(); }
            finally { held.Dispose(); }
        }
    }
}
