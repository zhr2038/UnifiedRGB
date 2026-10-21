namespace UnifiedRgb.Host.Services;

/// <summary>A process-wide hardware lease acquired before dependency creation. A dedicated thread owns the OS mutex across awaits.</summary>
public sealed class HardwareHostLease : IDisposable
{
    public const string Name = @"Global\UnifiedRgb.HardwareHost";
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _owner;
    private int _disposed;
    private int _retainUntilProcessExit;

    public static bool TryAcquire([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HardwareHostLease? lease,
        out string? failure, string name = Name)
    {
        try { lease = new HardwareHostLease(name); failure = null; return true; }
        catch (InvalidOperationException ex) { lease = null; failure = ex.Message; return false; }
    }

    /// <summary>An unclean shutdown must retain hardware ownership until the OS actually ends this process.</summary>
    public void RetainUntilProcessExit() => Volatile.Write(ref _retainUntilProcessExit, 1);

    public HardwareHostLease(string name = Name)
    {
        var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _owner = new Thread(() => Hold(name, ready)) { IsBackground = true, Name = "UnifiedRgb hardware ownership" };
        _owner.Start();
#pragma warning disable VSTHRD002 // synchronous entry gate, before any async Host or device work exists
        var failure = ready.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        if (failure is not null)
        {
            Dispose();
            throw new InvalidOperationException("Another Host owns the hardware, or its machine ownership lock is unavailable. No hardware was opened.", failure);
        }
    }

    private void Hold(string name, TaskCompletionSource<Exception?> ready)
    {
        var owned = false;
        try
        {
            using var mutex = new Mutex(false, name);
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { ready.TrySetResult(new InvalidOperationException("Hardware ownership is already held.")); return; }
            ready.TrySetResult(null);
            try { _release.Wait(); }
            finally { mutex.ReleaseMutex(); }
        }
#pragma warning disable CA1031 // report ownership errors to the entry thread; never escape a background thread
        catch (Exception ex) { ready.TrySetResult(ex); }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Volatile.Read(ref _retainUntilProcessExit) != 0) return; // background mutex owner remains rooted until process exit
        _release.Set();
        _owner.Join();
        _release.Dispose();
    }
}
