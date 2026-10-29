using System.Threading.Channels;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// One generation-owned pending lighting request. Completion of a colour means the actor received the exact wired payload
/// echo; completion of a key frame means every C0 81 report received the stock C0/82 reply on the owner's connection. Key frames
/// are queued only for an owner whose actor declared that it services them (<see cref="Owner.AcceptKeyFrames"/>) on exactly
/// the requested transport, so an actor that does not know them is never woken by work it cannot take.
/// </summary>
public sealed class RogWiredLightingBridge
{
    private Owner? _owner;
    public event Action<bool>? AvailabilityChanged;
    public bool Available => Volatile.Read(ref _owner) is not null;

    /// <summary>The per-key transport the current owner services; null when there is no owner or it services no key frames.</summary>
    public string? KeyFrameTransport => Volatile.Read(ref _owner)?.KeyFrameTransport;

    public Owner Attach()
    {
        var owner = new Owner();
        if (Interlocked.CompareExchange(ref _owner, owner, null) is not null) throw new InvalidOperationException("Wired lighting already has an owner.");
        AvailabilityChanged?.Invoke(true); return owner;
    }

    public void Detach(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _owner, null, owner), owner)) return;
        owner.Close(); AvailabilityChanged?.Invoke(false);
    }

    public async Task WriteAsync(Rgb color, CancellationToken ct)
    {
        var owner = Volatile.Read(ref _owner) ?? throw new InvalidOperationException("Wired keyboard owner not ready.");
        var request = new ColourRequest(color);
        if (!owner.Queue.Writer.TryWrite(request)) throw new InvalidOperationException("Wired colour queue unavailable.");
        try { await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
        finally { request.Cancel(); }
    }

    /// <summary>
    /// Queues one complete per-key frame for the current owner's actor. Refused before anything is queued when there is no
    /// owner or the owner does not service key frames on <paramref name="requiredTransport"/>; the frame is validated first.
    /// </summary>
    public async Task WriteKeysAsync(IReadOnlyList<RogKeyColor> keys, string requiredTransport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrEmpty(requiredTransport);
        var owner = Volatile.Read(ref _owner) ?? throw new InvalidOperationException("Keyboard lighting owner not ready.");
        if (owner.KeyFrameTransport != requiredTransport)
            throw new InvalidOperationException($"Keyboard lighting owner does not service per-key frames on {requiredTransport}.");
        var frame = keys.ToArray();
        _ = RogPerKeyLighting.BoundaryFrame(frame);
        var request = new KeyFrameRequest(frame, requiredTransport);
        if (!owner.Queue.Writer.TryWrite(request)) throw new InvalidOperationException("Keyboard lighting queue unavailable.");
        try { await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
        finally { request.Cancel(); }
    }

    public sealed class Owner
    {
        private string? _keyFrameTransport;
        internal Channel<LightingRequest> Queue { get; } = Channel.CreateBounded<LightingRequest>(1);
        public string? KeyFrameTransport => Volatile.Read(ref _keyFrameTransport);
        public bool HasWork => Queue.Reader.TryPeek(out _);

        /// <summary>The actor that owns this generation services per-key frames on <paramref name="transport"/>. Set once.</summary>
        public void AcceptKeyFrames(string transport)
        {
            ArgumentException.ThrowIfNullOrEmpty(transport);
            var previous = Interlocked.CompareExchange(ref _keyFrameTransport, transport, null);
            if (previous is not null && previous != transport)
                throw new InvalidOperationException("This lighting owner already services another per-key transport.");
        }

        /// <summary>Takes the pending request only when it is a colour; a pending key frame is left for <see cref="TryTakeKeys"/>.</summary>
        public bool TryTake(out ColourRequest? request) => TryTake<ColourRequest>(out request);

        public bool TryTakeKeys(out KeyFrameRequest? request) => TryTake<KeyFrameRequest>(out request);

        public ValueTask<bool> WaitAsync(CancellationToken ct) => Queue.Reader.WaitToReadAsync(ct);

        // The actor is the only reader and the queue holds one request, so a peeked request is the one read.
        private bool TryTake<T>(out T? request) where T : LightingRequest
        {
            request = null;
            if (!Queue.Reader.TryPeek(out var head) || head is not T || !Queue.Reader.TryRead(out var taken)) return false;
            request = (T)taken;
            return true;
        }

        internal void Close()
        {
            Queue.Writer.TryComplete();
            while (Queue.Reader.TryRead(out var request)) request.Completion.TrySetException(new IOException("Wired keyboard generation ended."));
        }
    }

    public abstract class LightingRequest
    {
        private int _cancelled;
        private protected LightingRequest() { }
        public bool Cancelled => Volatile.Read(ref _cancelled) != 0;
        public void Cancel() => Volatile.Write(ref _cancelled, 1);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ColourRequest(Rgb colour) : LightingRequest
    {
        public Rgb Colour { get; } = colour;
    }

    /// <summary>One complete, already validated per-key frame and the transport it was mapped for.</summary>
    public sealed class KeyFrameRequest : LightingRequest
    {
        internal KeyFrameRequest(RogKeyColor[] keys, string transport) { Keys = Array.AsReadOnly(keys); Transport = transport; }
        public IReadOnlyList<RogKeyColor> Keys { get; }
        public string Transport { get; }
    }
}
