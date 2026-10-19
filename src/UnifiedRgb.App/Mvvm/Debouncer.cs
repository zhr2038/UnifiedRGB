namespace UnifiedRgb.App.Mvvm;

/// <summary>
/// Runs <c>action</c> once <c>delay</c> has passed without another <see cref="Trigger"/> (ARCHITECTURE-D2 §4: sliders are
/// debounced at 300 ms, because every Lian Li parameter change is a full loop re-upload). Errors go to <c>onError</c>.
/// </summary>
public sealed class Debouncer(TimeSpan delay, Func<Task> action, Action<Exception> onError) : IDisposable
{
    private CancellationTokenSource? _pending;
    private int _fired;

    public TimeSpan Delay { get; } = delay;

    /// <summary>How many times the action actually ran (tests, diagnostics).</summary>
    public int Fired => Volatile.Read(ref _fired);

    public void Trigger()
    {
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _pending, cts);
        old?.Cancel();
        old?.Dispose();
        _ = RunAsync(cts.Token);
    }

    /// <summary>Drops a pending run.</summary>
    public void Cancel()
    {
        var old = Interlocked.Exchange(ref _pending, null);
        old?.Cancel();
        old?.Dispose();
    }

    public void Dispose() => Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Delay, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a later Trigger
        }

        try
        {
            Interlocked.Increment(ref _fired);
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // UI boundary: reported, never thrown into the dispatcher
        catch (Exception ex)
#pragma warning restore CA1031
        {
            onError(ex);
        }
    }
}
