namespace UnifiedRgb.Host.Services;

public sealed class PeripheralConfigurationGate : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _acquisitionOperations = new(1, 1);
    private int _acquisitionAllowed;
    private int _freshResumeAllowed;

    internal PeripheralConfigurationGate(PeripheralConfigurationMarker.LoadResult initial)
    {
        InitialError = initial.Error;
        _acquisitionAllowed = initial.Requested ? 0 : 1;
    }

    internal string? InitialError { get; }
    internal bool RogAcquisitionAllowed => Volatile.Read(ref _acquisitionAllowed) != 0;
    internal bool FreshResumeAllowed => Volatile.Read(ref _freshResumeAllowed) != 0;

    /// <summary>Raised after every Block/Allow; handlers re-read <see cref="RogAcquisitionAllowed"/>, must not block and must not throw.</summary>
    internal event Action? RogAcquisitionChanged;

    internal void BlockRogAcquisition() { Volatile.Write(ref _acquisitionAllowed, 0); RogAcquisitionChanged?.Invoke(); }
    internal void AllowRogAcquisition() { Volatile.Write(ref _acquisitionAllowed, 1); RogAcquisitionChanged?.Invoke(); }
    internal void BeginFreshResume() => Volatile.Write(ref _freshResumeAllowed, 1);
    internal void EndFreshResume() => Volatile.Write(ref _freshResumeAllowed, 0);

    internal async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try { return await operation(ct).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    internal async Task<T> RunAcquisitionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await _acquisitionOperations.WaitAsync(ct).ConfigureAwait(false);
        try { return await operation(ct).ConfigureAwait(false); }
        finally { _acquisitionOperations.Release(); }
    }

    public void Dispose()
    {
        _operations.Dispose();
        _acquisitionOperations.Dispose();
    }
}
