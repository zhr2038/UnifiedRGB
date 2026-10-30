using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

public interface IRogWirelessOledFactory
{
    RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader);
}

/// <summary>
/// Exclusive Azoth X OLED owner on the verified OMNI receiver route. RID2/64-byte framing has 59 pixel bytes per
/// chunk; command allowlists, full-image validation, profile checks and restoration remain in the shared OLED session.
/// </summary>
public sealed class RogWirelessOledConnection : IRogOledConnection
{
    private readonly RogReceiver _receiver;
    public RogNativeControlBinding? NativeControlBinding { get; init; }
    private readonly IHidReportChannel _channel;
    private readonly string _firmwareHeader;
    private readonly Func<bool> _sameDevice;
    private readonly object _state = new();
    private Exception? _fault;
    private byte[]? _observed;
    private readonly Queue<byte[]> _completedReplies = new();
    // Only packet sizes actually written by this owner's asynchronous colour stream. No acknowledgement or pairing evidence.
    private readonly bool[] _writtenColourCounts = new bool[15];
    private const int ColourReplyDrainLimit = 128;
    internal Func<TimeSpan, CancellationToken, Task> KeyReportDelay { get; set; } = Task.Delay;
    private int _keyReportDelayMs = RogPerKeyOptions.LegacyRfReportDelayMs;

    /// <summary>
    /// Pause before every C0 81 report: 0..8 ms, default 8 (the earlier freeze mitigation, not a protocol rule). Zero writes
    /// without an intentional wait. Set only by the owning actor; bytes, order and colour-reply draining never change.
    /// </summary>
    public int KeyReportDelayMs
    {
        get => Volatile.Read(ref _keyReportDelayMs);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, RogPerKeyOptions.LegacyRfReportDelayMs);
            Volatile.Write(ref _keyReportDelayMs, value);
        }
    }

    private RogPairingEvidence? _evidence;
    private bool _busy, _disposed;

    public RogWirelessOledConnection(RogReceiver receiver, IHidReportChannel channel, string firmwareHeader, Func<bool> sameDevice)
    {
        _receiver = receiver; _channel = channel; _firmwareHeader = firmwareHeader; _sameDevice = sameDevice;
    }

    public string DeviceKey => _receiver.Key;
    public string FirmwareHeader => _firmwareHeader;
    public string ConnectionKind => "wireless";
    public int PixelPayloadCapacity => 59;
    public bool SameDevice() => _sameDevice();

    /// <summary>First receiver+keyboard identity accepted on this connection (diagnostic); null before the first accepted reply.</summary>
    public string? ObservedFirmwareHeader { get { lock (_state) return _observed is null ? null : Convert.ToHexString(_observed); } }

    /// <summary>Clock for evidence timestamps; the factory sets the same clock it stamped the pairing with.</summary>
    public TimeProvider EvidenceTime { get; init; } = TimeProvider.System;

    /// <summary>
    /// Starts from NativeControlBinding's keyboard and pairing timestamp; advances only after a validated keyboard identity or
    /// RF GYM1 capability reply to this owner's own exchange; terminally invalidated by any fault, rejection or disposal.
    /// </summary>
    public RogPairingEvidence? PairingEvidence { get { lock (_state) return CurrentEvidence(); } }

    // A valid receiver envelope with its keyboard present; every receiver/keyboard OEM byte and the configured/expected label are
    // diagnostic at first admission. It must not change generation within this connection: that fails closed and invalidates
    // the evidence; the existing owner lifecycle retires it.
    public void RequireFirmwareIdentity(byte[] identity, string expectedHeader)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            if (!RogRuntimeCompatibility.HasWirelessIdentityEnvelope(identity))
                throw new InvalidDataException("Wireless OLED identity reply envelope is invalid; no automatic retry.");
            if (!RogRuntimeCompatibility.WirelessKeyboardIdentityAvailable(identity))
                throw new InvalidDataException("Wireless keyboard identity is unavailable (possibly asleep or disconnected): receiver replied without keyboard firmware; no automatic retry.");
            var current = Generation(identity);
            lock (_state)
            {
                _observed ??= current;
                if (!current.AsSpan().SequenceEqual(_observed))
                    throw new InvalidDataException("Keyboard/receiver firmware generation changed within this wireless connection; no write or replay on it.");
            }
        }
        catch (InvalidDataException) { lock (_state) InvalidateEvidence("firmware identity rejected"); throw; }
    }

    private static byte[] Generation(byte[] identity)
    { var current = new byte[8]; identity.AsSpan(5, 4).CopyTo(current); identity.AsSpan(12, 4).CopyTo(current.AsSpan(4)); return current; }

    // Callers hold _state. Init-only binding, so the lazily built initial value is the same whenever it is first read.
    private RogPairingEvidence? CurrentEvidence() => _evidence ??= NativeControlBinding is { Keyboard: { } keyboard } binding
        ? new(keyboard, binding.PairingTimestamp, binding.PairingTimestamp, 0, null) : null;

    private void InvalidateEvidence(string reason)
    { if (CurrentEvidence() is { Invalidated: null } current) _evidence = current with { Invalidated = reason }; }

    // Only keyboard-originated content this owner asked for counts: a present-peer, same-generation identity or an RF-shaped
    // GYM1 capability. ACKs, echoes, renewals, failures and cancelled exchanges never advance; a bad reply invalidates.
    private void ObserveEvidence(byte[] request, byte[] reply)
    {
        var command = (request[1], request[2]);
        if (command is not ((0x12, 0) or (0x12, 0x7D) or (0x71, 0x7D))) return;
        string? rejected = null;
        if (command == (0x12, 0))
        {
            if (!RogRuntimeCompatibility.IsCompatibleWirelessIdentity(reply, _firmwareHeader)) rejected = "keyboard identity unavailable or envelope differs";
        }
        else
        {
            try { RogNativeMenuCodec.ParseCapability(reply, request[1], 2); }
            catch (InvalidDataException) { rejected = "native capability grammar differs"; }
        }
        lock (_state)
        {
            if (rejected is null && command == (0x12, 0) && _observed is not null && !Generation(reply).AsSpan().SequenceEqual(_observed))
                rejected = "keyboard/receiver generation changed";
            if (rejected is not null) { InvalidateEvidence(rejected); return; }
            if (CurrentEvidence() is not { Invalidated: null } current) return;
            _evidence = current with
            {
                LastObservedTimestamp = Math.Max(current.LastObservedTimestamp, EvidenceTime.GetTimestamp()),
                Observations = current.Observations + 1,
            };
        }
    }

    public async Task WriteLightingFrameAsync(IReadOnlyList<RogKeyColor> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // Validate and translate the entire frame before entering the existing exclusive owner or writing anything.
        var packets = RogPerKeyLighting.BoundaryFrame(keys.ToArray()).Select(ToWireless).ToArray();
        var spacing = KeyReportDelayMs;
        Enter(ct);
        try
        {
            foreach (var packet in packets)
            {
                // The first report also follows an earlier control command. This spacing is not delivery evidence.
                if (spacing > 0) await KeyReportDelay(TimeSpan.FromMilliseconds(spacing), ct).ConfigureAwait(false);
                using var deadline = new CancellationTokenSource(1000);
                var attempted = false;
                try
                {
                    ValueTask write;
                    lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); attempted = true; write = _channel.WriteAsync(packet, deadline.Token); }
                    // Retire an admitted write even if the caller stops. There is deliberately no mandatory RGB reply/read.
                    await write.ConfigureAwait(false);
                    _writtenColourCounts[packet[3]] = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (!attempted) throw; throw Fail(ex, packet, null, 0, 0, "colour-write", false, deadline.IsCancellationRequested); }
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { Exit(); }
    }

    private bool IsWrittenColourReply(byte[] reply) => RogPerKeyLighting.IsColourReply(reply) && _writtenColourCounts[reply[3]];

    public async Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request); request = (byte[])request.Clone();
        var packet = ToWireless(request); Enter(ct);
        var basicIdentity = !zeroAck
            && request[1] == 0x12 && request[2] == 0 && request[3] == 0 && request[4] == 0;
        using var timeout = new CancellationTokenSource(1000);
        var buffer = new byte[64]; var length = 0; var ordinal = 0; var stage = "write"; var attempted = false; byte[]? result = null;
        try
        {
            try
            {
                ValueTask write;
                lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); attempted = true; write = _channel.WriteAsync(packet, timeout.Token); }
                await write.ConfigureAwait(false);
                // Keep the existing eight ordinary-report budget, plus a separate finite allowance for streamed RGB replies.
                // The same one-second deadline bounds all reads, including late/duplicate colour replies.
                for (int n = 0, colourReplies = 0; n < 8;)
                {
                    stage = "read"; ordinal++; length = 0; Array.Clear(buffer);
                    length = await Read(buffer, timeout.Token).ConfigureAwait(false);
                    stage = "envelope"; RequireNotEof(length);
                    var reply = FromWireless(buffer, length);
                    if (reply[1] == 0xFF && reply[2] == 0xAA)
                        throw new InvalidDataException("Wireless keyboard OLED refused its envelope; no retry.");
                    if (IsWrittenColourReply(reply))
                    {
                        stage = "buffered-colour-reply";
                        if (++colourReplies > ColourReplyDrainLimit) throw new InvalidDataException("Wireless colour reply drain budget exceeded.");
                        continue;
                    }
                    n++;
                    var perKey = RogPerKeyLighting.IsFrameRequest(request);
                    if (perKey ? RogPerKeyLighting.MatchesReplyHeader(reply, request) : reply.AsSpan(0, 5).SequenceEqual(request.AsSpan(0, 5)))
                    {
                        stage = zeroAck ? "ack" : "reply";
                        try
                        {
                            if (perKey) RogPerKeyLighting.RequireReply(reply, request);
                            else if (zeroAck) RogWiredOledProtocol.RequireAck(reply, request);
                            else if ((request[1], request[2]) is (0x68, 0) or (0x51, 0x2C))
                            {
                                stage = "echo";
                                if (!reply.AsSpan().SequenceEqual(request)) throw new InvalidDataException("OLED full payload echo differs.");
                            }
                        }
                        catch (InvalidDataException) when (_completedReplies.Any(completed => completed.AsSpan().SequenceEqual(reply)))
                        {
                            // Same opcode, old payload: drain only a previously completed exact reply.
                            continue;
                        }
                        result = reply; break;
                    }
                    if (basicIdentity && reply[1] == 0x12 && reply[2] == 0x7D)
                    {
                        // A read-only CAP may still be buffered before a basic query. Validate and discard it;
                        // only the requested identity below can advance this owner's pairing evidence.
                        stage = "buffered-startup-capability";
                        RogNativeMenuCodec.ParseCapability(reply, 0x12, 2);
                        continue;
                    }
                    // RF can deliver a duplicate echo after its transaction already completed. Only exact
                    // replies witnessed on THIS connection may be discarded while awaiting another reply.
                    // This never acknowledges a pending write or treats unrelated content as fresh identity.
                    if (_completedReplies.Any(completed => completed.AsSpan().SequenceEqual(reply)))
                        continue;
                    stage = "optional-renewal"; RogWiredOledProtocol.RequireRenewalReply(reply);
                }
                if (result is null) throw new InvalidDataException("Wireless OLED optional response budget exceeded.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!attempted) throw; throw Fail(ex, packet, buffer, length, ordinal, stage, zeroAck, timeout.IsCancellationRequested); }
            ct.ThrowIfCancellationRequested(); ObserveEvidence(request, result);
            _completedReplies.Enqueue((byte[])result.Clone());
            if (_completedReplies.Count > 8) _completedReplies.Dequeue();
            return result;
        }
        finally { Exit(); }
    }

    public async Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct)
    {
        Enter(ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromSeconds(2));
        var packet = ToWireless(RogWiredOledProtocol.Renew()); Exception? failure = null; Exception? writerFailure = null;
        async Task ReaderAsync()
        {
            var buffer = new byte[64]; var length = 0; var ordinal = 0; var colourReplies = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    length = 0; Array.Clear(buffer); ordinal++;
                    length = await Read(buffer, stop.Token).ConfigureAwait(false); RequireNotEof(length);
                    var reply = FromWireless(buffer, length);
                    if (IsWrittenColourReply(reply))
                    {
                        if (++colourReplies > ColourReplyDrainLimit) throw new InvalidDataException("Wireless colour reply drain budget exceeded.");
                        continue;
                    }
                    RogWiredOledProtocol.RequireRenewalReply(reply);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { failure = Fail(ex, packet, buffer, length, ordinal, "lease-read", true, false); stop.Cancel(); }
        }
        var reader = ReaderAsync();
        try
        {
            while (!stop.IsCancellationRequested && !changed())
            {
                var at = System.Diagnostics.Stopwatch.GetTimestamp();
                using var deadline = new CancellationTokenSource(1000); var attempted = false;
                try
                {
                    ValueTask write;
                    lock (_state) { RequireUsable(); stop.Token.ThrowIfCancellationRequested(); attempted = true; write = _channel.WriteAsync(packet, deadline.Token); }
                    await write.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (!attempted) throw; throw Fail(ex, packet, null, 0, 0, "lease-write", false, deadline.IsCancellationRequested); }
                renewed();
                var delay = TimeSpan.FromMilliseconds(80) - System.Diagnostics.Stopwatch.GetElapsedTime(at);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { writerFailure = ex; }
        finally { stop.Cancel(); try { await reader.ConfigureAwait(false); } finally { Exit(); } }
        if (failure is not null) throw failure;
        if (writerFailure is not null) throw writerFailure;
        ct.ThrowIfCancellationRequested();
    }

    private void Enter(CancellationToken ct)
    { lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); if (_busy) throw new InvalidOperationException("One wireless OLED operation is already active."); _busy = true; } }
    private void Exit() { lock (_state) _busy = false; }
    private void RequireUsable()
    {
        if (_fault is not null) throw new IOException("Wireless OLED connection is poisoned; no further wire I/O. First failure: " + _fault.GetType().Name + ": " + _fault.Message, _fault);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private ValueTask<int> Read(Memory<byte> buffer, CancellationToken ct)
    { lock (_state) { RequireUsable(); return _channel.ReadAsync(buffer, ct); } }
    private Exception Fail(Exception ex, byte[] request, byte[]? reply, int length, int ordinal, string stage, bool zeroAck, bool expired)
    {
        lock (_state)
        {
        if (_fault is not null) return _fault;
        var size = reply is null ? 0 : Math.Clamp(length, 0, reply.Length);
        var header = size == 0 ? "none" : Convert.ToHexString(reply!.AsSpan(0, Math.Min(5, size)));
        var nonzero = zeroAck && size > 5 && reply!.AsSpan(5, size - 5).ContainsAnyExcept((byte)0);
        var detail = $"stage={stage}; request={Convert.ToHexString(request.AsSpan(0, 5))}; reply={header}; length={length}; read={ordinal}; zeroAck={zeroAck}; nonzeroAckBody={nonzero}; connection poisoned; no retry.";
        Exception fault = ex is OperationCanceledException && expired
            ? new RogOledTimeoutException("Wireless keyboard OLED command timed out; " + detail, ex)
            : ex is RogOledConnectionLostException || ex is IOException && Disappeared()
                ? new RogOledConnectionLostException("Pinned wireless keyboard/receiver interface disappeared or ended; " + detail, ex)
                : ex is InvalidDataException ? new InvalidDataException("Wireless OLED protocol response invalid; " + detail, ex)
                : new IOException("Wireless OLED I/O outcome is unknown; " + detail, ex);
        InvalidateEvidence("connection poisoned: " + fault.GetType().Name);
        return _fault = fault;
        }
    }

    private static void RequireNotEof(int length)
    {
        if (length == 0) throw new RogOledConnectionLostException("Wireless keyboard OLED stream ended.", new EndOfStreamException());
    }

    private bool Disappeared()
    {
        try { return !_sameDevice(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static byte[] ToWireless(byte[] wired)
    {
        ArgumentNullException.ThrowIfNull(wired);
        if (wired.Length != 65 || wired[0] != 0) throw new ArgumentException("Expected a wired RID0 OLED packet.", nameof(wired));
        if (wired[64] != 0) throw new ArgumentException("Wireless OLED framing cannot carry a nonzero 65th byte.", nameof(wired));
        var wireless = new byte[64]; wired.AsSpan(1, 63).CopyTo(wireless.AsSpan(1)); wireless[0] = 2;
        return wireless;
    }

    private static byte[] FromWireless(byte[] buffer, int length)
    {
        if (length != 64 || buffer.Length != 64 || buffer[0] != 2)
            throw new InvalidDataException("Wireless keyboard OLED changed its report envelope; no retry.");
        var wired = new byte[65]; buffer.AsSpan(1, 63).CopyTo(wired.AsSpan(1));
        return wired;
    }

    public void Dispose() { lock (_state) { if (_disposed) return; _disposed = true; InvalidateEvidence("disposed"); _channel.Dispose(); } }
}

public sealed class RogWirelessOledFactory : IRogWirelessOledFactory
{
    private readonly TimeProvider _time;

    public RogWirelessOledFactory() : this(TimeProvider.System) { }

    /// <summary>Stamps the pairing and the connection's evidence with this one clock (the Host's shared TimeProvider).</summary>
    public RogWirelessOledFactory(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
    {
        // The selected receiver key is the pin; the header is a diagnostic note (a wireless-form label is kept verbatim).
        if (receiverKey.Length != 64 || !receiverKey.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Wireless OLED identity pins are invalid.");
        var label = RogRuntimeCompatibility.RouteLabel("wireless", firmwareHeader)!;
        var devices = new WinHidReportDevices();
        var matches = RogReceiver.Discover(devices).Where(r => r.ProductId == RogKeyboardInputContract.ReceiverProductId
            && string.Equals(r.Key, receiverKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidOperationException("Ambiguous pinned ROG wireless receiver.");
        var receiver = matches[0];
        RogPairedDevice keyboard;
        long pairingTimestamp;
        var session = new RogReceiverSession(devices, receiver, 1000);
        try
        {
            var paired = session.QueryPairingAsync(default).GetAwaiter().GetResult();
            pairingTimestamp = _time.GetTimestamp();
            keyboard = paired.SingleOrDefault(RogKeyboardInputContract.IsVerifiedTarget)
                ?? throw new InvalidDataException("Pinned receiver does not currently name the verified Azoth X route.");
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var channel = WinHidReportDevices.OpenExclusiveWriter(receiver.Channels[keyboard.ReportId]);
        return new(receiver, channel, label, () => RogReceiver.Discover(devices).Any(r => r.ProductId == 0x1ACE
            && string.Equals(r.Key, receiverKey, StringComparison.OrdinalIgnoreCase) && r.Channels.ContainsKey(keyboard.ReportId)))
        { NativeControlBinding = new(receiver.Channels.Values.ToArray(), keyboard, pairingTimestamp), EvidenceTime = _time };
    }
}
