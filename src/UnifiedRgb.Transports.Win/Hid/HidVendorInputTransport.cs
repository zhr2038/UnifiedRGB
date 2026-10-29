using System.ComponentModel;

namespace UnifiedRgb.Transports.Hid;

/// <summary>
/// One opened read-only HID collection (Win32: GENERIC_READ, shared, overlapped). There is deliberately no write, output,
/// feature or report-setter member; a caller that needs output uses the separate <see cref="IHidReportDevices"/>.
/// </summary>
public interface IHidInputReportHandle : IDisposable
{
    /// <summary>Attributes and caps of THIS opened handle; throws when they cannot be read.</summary>
    HidInputDescriptor Describe();

    /// <summary>One input report into <paramref name="buffer"/>; honours cancellation. 0 means the stream ended.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>Vendor HID input collections of the system (Win32) or a fake (tests).</summary>
public interface IHidInputReportDevices
{
    /// <summary>Access-0 metadata of <paramref name="vid"/>:<paramref name="pid"/> collections; nothing is read or sent.</summary>
    IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid);

    /// <summary>
    /// Opens <paramref name="collection"/> read-only and shared. Implementations refuse MI_00 and every non-vendor usage page.
    /// Callers go through <see cref="HidVendorInputTransport.Open"/>, which applies the exact rule before and after opening.
    /// </summary>
    IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection);
}

/// <summary>
/// Read-only transport for exactly one vendor-defined, input-only HID collection. It is opened only after
/// <see cref="HidVendorInputRule"/> admitted the enumeration entry AND the descriptor of the opened handle. Each read is
/// bounded by a read window: no report within the window is an idle result, not an error and never a synthesized report.
/// Any end of stream, I/O error, read that ignores cancellation, native length other than the rule's exact length, or
/// report id the rule does not admit closes the handle permanently (detach / descriptor drift fail closed). Caller
/// cancellation also closes the handle. Reads go through a private buffer that is cleared after every read; the caller
/// receives only a copy of a framed report. One read at a time.
/// </summary>
public sealed class HidVendorInputTransport : IDisposable
{
    public static readonly TimeSpan MinimumReadWindow = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan MaximumReadWindow = TimeSpan.FromSeconds(2);

    /// <summary>A read still pending this long after its window was cancelled is treated as hung; the handle is closed.</summary>
    public static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(1);

    private readonly IHidInputReportHandle _handle;
    private readonly byte[] _buffer;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private Task<int>? _pendingRead;
    private int _closed;
    private int _reading;

    private HidVendorInputTransport(IHidInputReportHandle handle, HidCollectionInfo collection, HidVendorInputRule rule,
        HidInputDescriptor descriptor, TimeSpan window, TimeProvider time)
    {
        _handle = handle;
        Collection = collection;
        Rule = rule;
        Descriptor = descriptor;
        _window = window;
        _time = time;
        _buffer = new byte[rule.InputReportLength];
    }

    public HidCollectionInfo Collection { get; }
    public HidVendorInputRule Rule { get; }

    /// <summary>The verified descriptor of the opened handle.</summary>
    public HidInputDescriptor Descriptor { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Opens <paramref name="collection"/> only when <paramref name="rule"/> admits it before and after opening.</summary>
    public static HidVendorInputTransport Open(IHidInputReportDevices devices, HidCollectionInfo collection, HidVendorInputRule rule,
        TimeSpan readWindow, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfLessThan(readWindow, MinimumReadWindow);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readWindow, MaximumReadWindow);
        if (rule.Reject(collection) is { } why) throw new InvalidOperationException($"HID input collection refused: {why}");
        var handle = devices.OpenReadOnly(collection);
        try
        {
            var descriptor = handle.Describe();
            if (rule.Reject(descriptor) is { } drift) throw new InvalidDataException($"Opened HID input collection refused: {drift}");
            return new HidVendorInputTransport(handle, collection, rule, descriptor, readWindow, time ?? TimeProvider.System);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when one framed report (exactly <see cref="HidVendorInputRule.InputReportLength"/> bytes, admitted report id) was
    /// copied into <paramref name="destination"/>; false when the read window elapsed without a report. Throws after closing
    /// the handle on detach, I/O error, descriptor drift, a hung read or caller cancellation.
    /// </summary>
    public async ValueTask<bool> ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (destination.Length < Rule.InputReportLength)
            throw new ArgumentException("The destination is shorter than one input report.", nameof(destination));
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Only one HID input read may be pending.");
        try
        {
            destination.Span.Clear();
            int length;
            using (var window = new CancellationTokenSource(_window, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, window.Token))
            {
                try
                {
                    var pending = _handle.ReadAsync(_buffer, linked.Token).AsTask();
                    Volatile.Write(ref _pendingRead, pending);
                    try
                    {
                        length = await pending.WaitAsync(_window + CancellationGrace, _time, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!pending.IsCompleted)
                        {
                            linked.Cancel(); // Cancel the actual read before disposing this linked registration.
                            Close();
                            // A non-cooperative read may finish after cancellation/close. This buffer is never reused
                            // on that closed transport; erase it again at real completion and observe any late fault.
                            _ = pending.ContinueWith(static (task, state) =>
                            {
                                Array.Clear((byte[])state!);
                                _ = task.Exception;
                            }, _buffer, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                    }
                    ct.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(IsClosed, this);
                    if (window.IsCancellationRequested)
                    {
                        Close();
                        throw new TimeoutException("HID report completed outside its read window; the generation was closed.");
                    }
                }
                catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested && !IsClosed)
                {
                    return false; // idle window: the handle stays open; no release or report is invented
                }
                catch (OperationCanceledException)
                {
                    Close();
                    throw;
                }
                catch (TimeoutException ex)
                {
                    Close();
                    throw new TimeoutException("HID input read ignored cancellation; the handle was closed.", ex);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or Win32Exception)
                {
                    Close();
                    throw;
                }
            }

            if (length == 0)
            {
                Close();
                throw new IOException("HID input stream ended (device detached); the handle was closed.");
            }

            if (length != Rule.InputReportLength || !Rule.Admits(_buffer[0]))
            {
                Close();
                throw new InvalidDataException("HID input report length or report id differs from the verified descriptor; the handle was closed.");
            }

            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsClosed, this);
            _buffer.AsSpan(0, length).CopyTo(destination.Span);
            return true;
        }
        finally
        {
            Array.Clear(_buffer);
            Volatile.Write(ref _reading, 0);
        }
    }

    /// <summary>
    /// Close and join the actual underlying read, including a read whose outer wait was cancelled first. A caller must not
    /// admit another owner/ARM while this fails to join. No drain, retry, new read or handle open is performed.
    /// </summary>
    public async Task JoinAsync(CancellationToken ct = default)
    {
        Close();
        var pending = Volatile.Read(ref _pendingRead);
        if (pending is null) return;
        try { await pending.WaitAsync(CancellationGrace, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (pending.IsCompleted && !ct.IsCancellationRequested) { }
        catch (Exception) when (pending.IsCompleted && !ct.IsCancellationRequested) { } // Observed terminal read fault: it is quiescent.
        finally { if (pending.IsCompleted) Array.Clear(_buffer); }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _handle.Dispose();
    }

    public void Dispose() => Close();
}
