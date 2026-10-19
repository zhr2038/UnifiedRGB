namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// A single-slot mailbox: posting replaces any message not yet taken, so a slow display never builds a queue and always
/// converges on the newest command. One consumer waits; any thread posts.
/// </summary>
internal sealed class LatestWins<T> : IDisposable
    where T : class
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private T? _value;

    public void Post(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Interlocked.Exchange(ref _value, value);
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled: the consumer will take the newest value
        }
        catch (ObjectDisposedException)
        {
            // consumer gone
        }
    }

    /// <summary>The newest message, or null; never blocks.</summary>
    public T? TryTake() => Interlocked.Exchange(ref _value, null);

    /// <summary>Waits up to <paramref name="timeoutMs"/> (-1 = forever) for a post; true when one may be waiting.</summary>
    public bool Wait(int timeoutMs) => _signal.Wait(timeoutMs);

    public void Dispose() => _signal.Dispose();
}

/// <summary>Reopen delay after failures: initial, doubling per consecutive failure, capped; reset by a success.</summary>
internal sealed class Backoff(int initialMs, int maxMs)
{
    private readonly int _initial = Math.Max(1, initialMs);
    private readonly int _max = Math.Max(Math.Max(1, initialMs), maxMs);

    public int Failures { get; private set; }

    /// <summary>Records a failure and returns the delay before the next attempt.</summary>
    public int Fail()
    {
        Failures++;
        var shift = Math.Min(Failures - 1, 20);
        return (int)Math.Min(_max, (long)_initial << shift);
    }

    public void Reset() => Failures = 0;
}
