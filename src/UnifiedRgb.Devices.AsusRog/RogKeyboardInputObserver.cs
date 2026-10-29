using System.Threading.Channels;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Only a typed observation. This is NOT an authorized input-to-action intent or optical acknowledgement.</summary>
public sealed record RogInputObservation(long Generation, long Sequence, TimeSpan Elapsed,
    RogControlNotificationKind Kind, byte Value, RogLeverDirection? PressEdge);

public sealed record RogInputObservationResult(long Generation, long Reports, long Discarded, long Queued, long Delivered, string Stop, string? ErrorType);

/// <summary>
/// Explicit read-only monitor for the owner-verified RF keyboard. No constructor I/O, startup integration, normal-key
/// decoding, configuration write or case-power action. The supplied pairing delegate must make a fresh query on the
/// selected receiver session; it must not return a cached inventory. A live read handle and a bounded typed-notification
/// queue are owned by one ObserveAsync invocation. Slow callbacks, overflow, detach or malformed control data stop it.
/// Callbacks are observation-only and must honour cancellation; an already invoked callback cannot be undone.
/// </summary>
public sealed class RogKeyboardInputObserver(IHidInputReportDevices devices, IHidContainerResolver containers, TimeProvider? time = null) : IDisposable
{
    public const int NotificationCapacity = 16;
    public static readonly TimeSpan MaximumCallbackTime = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumMetadataAge = TimeSpan.FromMinutes(3);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lifetime = new();
    private CancellationTokenSource? _stop;
    private HidVendorInputTransport? _input;
    private bool _disposed;
    private static long _generationSequence;

    public async Task<RogInputObservationResult> ObserveAsync(RogReceiver receiver, RogPairedDevice target,
        Func<CancellationToken, Task<IReadOnlyList<RogPairedDevice>>> freshPairing,
        Func<RogInputObservation, CancellationToken, ValueTask> observe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(freshPairing);
        ArgumentNullException.ThrowIfNull(observe);
        if (!ct.CanBeCanceled) throw new ArgumentException("The observation owner must supply a cancellable lifetime.", nameof(ct));
        ct.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is not null) throw new InvalidOperationException("One input observation is already active.");
            _stop = stop;
        }
        var generation = Interlocked.Increment(ref _generationSequence);
        long received = 0, discarded = 0, queued = 0, delivered = 0;
        string reason = "cancelled";
        string? error = null;
        var queue = Channel.CreateBounded<RogInputObservation>(new BoundedChannelOptions(NotificationCapacity)
        { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        try
        {
            var pairing = await freshPairing(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            var all = devices.Enumerate(RogProtocol.VendorId, RogKeyboardInputContract.ReceiverProductId);
            var binding = RogKeyboardInputContract.Bind(receiver, target, pairing, all, containers, _time, MaximumMetadataAge);
            var input = HidVendorInputTransport.Open(devices, binding.EventCollection, RogKeyboardInputContract.EventRule,
                TimeSpan.FromMilliseconds(250), _time);
            lock (_lifetime)
            {
                if (_disposed || stop.IsCancellationRequested) { input.Dispose(); stop.Token.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(RogKeyboardInputObserver)); }
                _input = input;
            }
            // Recheck platform identity after opening. A same-PID replacement during binding is not accepted.
            var after = RogKeyboardInputContract.Bind(receiver, target, pairing, all, containers, _time, MaximumMetadataAge);
            if (!after.Container.SameContainer(binding.Container) || after.EventCollection != binding.EventCollection)
                throw new InvalidDataException("Keyboard physical identity changed while opening the input collection.");
            using var closeOnStop = stop.Token.Register(input.Dispose);
            var started = _time.GetTimestamp();
            var edges = new RogLeverDecoder(); edges.Begin(generation);
            async Task ProduceAsync()
            {
                var buffer = new byte[RogControlNotificationParser.NativeLength];
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (!await input.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) continue;
                        stop.Token.ThrowIfCancellationRequested();
                        received = checked(received + 1);
                        var notice = RogControlNotificationParser.Classify(buffer);
                        if (notice.Kind == RogControlNotificationKind.Dropped) { discarded = checked(discarded + 1); continue; }
                        if (notice.Kind == RogControlNotificationKind.Malformed) throw new InvalidDataException("Control notification layout changed.");
                        var elapsed = _time.GetElapsedTime(started);
                        var edge = notice.Kind == RogControlNotificationKind.LeverLevel ? edges.Read(buffer, generation, elapsed) : null;
                        if (edges.Readiness == RogLeverReadiness.Faulted) throw new InvalidDataException("Lever edge sequence lost a physical neutral.");
                        var next = new RogInputObservation(generation, checked(queued + 1), elapsed, notice.Kind, notice.Value, edge);
                        if (!queue.Writer.TryWrite(next)) throw new InvalidDataException("Typed observation backlog overflowed; session closed.");
                        queued++;
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or OverflowException or System.ComponentModel.Win32Exception)
                { reason = "input_fault"; error = ex.GetType().Name; }
                finally
                {
                    Array.Clear(buffer); edges.Disarm(); input.Dispose();
                    queue.Writer.TryComplete(); stop.Cancel();
                }
            }
            var producer = ProduceAsync();
            try
            {
                await foreach (var notice in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    await observe(notice, stop.Token).AsTask().WaitAsync(MaximumCallbackTime, _time, stop.Token).ConfigureAwait(false);
                    delivered = checked(delivered + 1);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            { reason = "consumer_fault"; error = ex.GetType().Name; }
            finally
            {
                stop.Cancel(); input.Dispose();
                await producer.ConfigureAwait(false);
            }
            return new(generation, received, discarded, queued, delivered, reason, error);
        }
        finally
        {
            lock (_lifetime)
            {
                _input?.Dispose(); _input = null; _stop = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true; _stop?.Cancel(); _input?.Dispose();
        }
    }
}
