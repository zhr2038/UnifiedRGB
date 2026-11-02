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
    private readonly bool[] _nativePixelWrites = new bool[113];
    private bool _nativeBeginCompleted;
    private const int ColourReplyDrainLimit = 128;
    // Passive, allocation-free report history. Only fault formatting emits it; no payload, timer or independent reader.
    private const int FlightHistoryCapacity = 128;
    private readonly FlightRecord[] _flight = new FlightRecord[FlightHistoryCapacity];
    private long _flightCount, _colourWritesAdmitted, _colourWritesCompleted, _colourRepliesObserved;
    private long _drainHighResolutionWindows, _drainCoarseWindows;
    private long? _flightOrigin, _lastColourWriteAt;
    private long _lastFlightAt;
    private enum FlightOperation : byte { RGB, Exchange, Lease, PreDrain, ImageAbort }
    private enum FlightStage : byte { TxAdmit, TxDone, RxDone, Start, Quiet, Cap, CapBudget }
    private readonly record struct FlightRecord(long At, ulong Header, int Length, FlightOperation Operation, FlightStage Stage);
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
    private bool _busy, _disposed, _hasWireCommand;

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
                    lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); attempted = true;
                        RecordFlight(FlightOperation.RGB, FlightStage.TxAdmit, packet, packet.Length);
                        write = _channel.WriteAsync(packet, deadline.Token); }
                    // Retire an admitted write even if the caller stops. There is deliberately no mandatory RGB reply/read.
                    await write.ConfigureAwait(false);
                    RecordFlight(FlightOperation.RGB, FlightStage.TxDone, packet, packet.Length);
                    _writtenColourCounts[packet[3]] = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (!attempted) throw; throw Fail(ex, packet, null, 0, 0, "colour-write", false, deadline.IsCancellationRequested); }
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { Exit(); }
    }

    private bool IsWrittenColourReply(byte[] reply)
    {
        if (!RogPerKeyLighting.IsColourReply(reply)) return false;
        lock (_state) _colourRepliesObserved++; // Includes duplicates; this is not a count of uniquely acknowledged writes.
        return _writtenColourCounts[reply[3]];
    }

    public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct) => ExchangeCoreAsync(request, zeroAck, false, ct);

    public Task<byte[]> ExchangeNativePixelAsync(byte[] request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Length != 65 || request[1] != 0x67 || request[2] != 1 || request[4] != 0 || request[3] >= 113)
            throw new ArgumentException("Only native67/1 RF pixel data uses this exchange role.", nameof(request));
        return ExchangeCoreAsync(request, true, true, ct);
    }

    private async Task<byte[]> ExchangeCoreAsync(byte[] request, bool zeroAck, bool nativePixel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request); request = (byte[])request.Clone();
        var packet = ToWireless(request); Enter(ct);
        var basicIdentity = !zeroAck
            && request[1] == 0x12 && request[2] == 0 && request[3] == 0 && request[4] == 0;
        using var timeout = new CancellationTokenSource();
        var buffer = new byte[64]; var length = 0; var ordinal = 0; var stage = "write"; var attempted = false; byte[]? result = null;
        var seen = new byte[8 * 5]; var seenLengths = new int[8]; var seenCount = 0;
        try
        {
            try
            {
                // Only control transitions get a bounded pre-write drain. Keep the strict contiguous OLED data batch and
                // RGB packets unpaced. A reply received before TX cannot be a fresh answer to the proposed command.
                // Preserve first-command startup semantics: the new connection has no own earlier TX to de-correlate.
                var drained = !_hasWireCommand || request[1] == 0x67 && request[2] == 1 || RogPerKeyLighting.IsFrameRequest(request)
                    ? (Ordinary: 0, Colour: 0) : await DrainBeforeWriteAsync(packet, ct).ConfigureAwait(false);
                timeout.CancelAfter(1000); // The strict response deadline starts after the optional drain has joined.
                ValueTask write;
                lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); attempted = true;
                    RecordFlight(FlightOperation.Exchange, FlightStage.TxAdmit, packet, packet.Length);
                    write = _channel.WriteAsync(packet, timeout.Token); }
                await write.ConfigureAwait(false);
                RecordFlight(FlightOperation.Exchange, FlightStage.TxDone, packet, packet.Length);
                if (nativePixel) _nativePixelWrites[request[3]] = true;
                // Keep the existing eight ordinary-report budget, plus a separate finite allowance for streamed RGB replies.
                // The same one-second deadline bounds all reads, including late/duplicate colour replies.
                for (int n = drained.Ordinary, colourReplies = drained.Colour; n < 8;)
                {
                    stage = "read"; ordinal++; length = 0; Array.Clear(buffer);
                    length = await Read(buffer, timeout.Token).ConfigureAwait(false);
                    RecordFlight(FlightOperation.Exchange, FlightStage.RxDone, buffer, length);
                    if (length > 0)
                    {
                        var slot = seenCount++ % 8; seenLengths[slot] = length;
                        seen.AsSpan(slot * 5, 5).Clear();
                        buffer.AsSpan(0, Math.Min(5, Math.Min(length, buffer.Length))).CopyTo(seen.AsSpan(slot * 5, 5));
                    }
                    stage = "envelope"; RequireNotEof(length);
                    var reply = FromWireless(buffer, length);
                    if (nativePixel && ExactNegative(reply))
                    {
                        await DrainAbortedImageAsync(packet, ct).ConfigureAwait(false);
                        string flight; lock (_state) flight = FlightFaultDetail();
                        throw new RogNativeImageNackException("Native image negative envelope; originating opcode unknown; image abandoned, control owner retained; request="
                            + Convert.ToHexString(packet.AsSpan(0, 5)) + "; " + flight);
                    }
                    RequireNotRefused(reply);
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
                    stage = "optional-renewal";
                    // Framing, refusal, expected reply and known buffered grammars were checked above. Preserve strict
                    // renewal body validation: only an unmatched HEADER gets this transport classification. It accepts
                    // nothing and authorizes no retry; only a caller's standalone-read role may retire the owner.
                    if (!reply.AsSpan(0, 5).SequenceEqual(RogWiredOledProtocol.Renew().AsSpan(0, 5)))
                        throw new RogOledUnexpectedReplyException("Unmatched wireless OLED reply at optional-renewal boundary.",
                            new InvalidDataException("Reply is neither the requested header nor an optional renewal."));
                    RogWiredOledProtocol.RequireRenewalReply(reply);
                }
                if (result is null) throw new InvalidDataException("Wireless OLED optional response budget exceeded.");
            }
            catch (RogNativeImageNackException) when (nativePixel) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!attempted) throw; throw Fail(ex, packet, buffer, length, ordinal, stage, zeroAck, timeout.IsCancellationRequested,
                string.Join(",", Enumerable.Range(Math.Max(0, seenCount - 8), Math.Min(8, seenCount))
                    .Select(n => Convert.ToHexString(seen.AsSpan(n % 8 * 5, 5)) + "/" + seenLengths[n % 8]))); }
            ct.ThrowIfCancellationRequested(); ObserveEvidence(request, result);
            _completedReplies.Enqueue((byte[])result.Clone());
            if (_completedReplies.Count > 8) _completedReplies.Dequeue();
            if (request[1] == 0x67 && request[2] == 0) { Array.Clear(_nativePixelWrites); _nativeBeginCompleted = true; }
            return result;
        }
        finally { Exit(); }
    }

    private static bool ExactNegative(byte[] reply) => reply[1] == 0xFF && reply[2] == 0xAA
        && !reply.AsSpan(3).ContainsAnyExcept((byte)0);

    private async Task DrainAbortedImageAsync(byte[] request, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp(); var buffer = new byte[64];
        RecordFlight(FlightOperation.ImageAbort, FlightStage.Start, request, request.Length);
        for (var n = 0; n < 128; n++)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = TimeSpan.FromMilliseconds(32) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { RecordFlight(FlightOperation.ImageAbort, FlightStage.Cap, request, request.Length); return; }
            using var window = HidReadWindow.Start(remaining < TimeSpan.FromMilliseconds(2) ? remaining : TimeSpan.FromMilliseconds(2), ct);
            int length; Array.Clear(buffer);
            try { length = await Read(buffer, window.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (window.Token.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (elapsed.TotalMilliseconds < 16) continue; // failed-image RF tails may arrive11–12ms after the write
                RecordFlight(FlightOperation.ImageAbort, elapsed.TotalMilliseconds >= 32
                    ? FlightStage.Cap : FlightStage.Quiet, request, request.Length); return;
            }
            RecordFlight(FlightOperation.ImageAbort, FlightStage.RxDone, buffer, length);
            RequireNotEof(length); var reply = FromWireless(buffer, length);
            if (ExactNegative(reply) || IsWrittenColourReply(reply)
                || _completedReplies.Any(done => done.AsSpan().SequenceEqual(reply))) continue;
            if (reply[1] == 0x67 && reply[4] == 0 && !reply.AsSpan(5).ContainsAnyExcept((byte)0)
                && (reply[2] == 1 && reply[3] < 113 && _nativePixelWrites[reply[3]]
                    || reply[2] == 0 && reply[3] == 0 && _nativeBeginCompleted)) continue;
            RogWiredOledProtocol.RequireRenewalReply(reply);
        }
        throw new InvalidDataException("Aborted native image reply drain budget exceeded.");
    }

    private async Task<(int Ordinary, int Colour)> DrainBeforeWriteAsync(byte[] request, CancellationToken ct)
    {
        var buffer = new byte[64]; var length = 0; var reads = 0; var ordinary = 0; var colour = 0;
        var started = EvidenceTime.GetTimestamp();
        // Windows write completion precedes the RF burst tail: the captured tail arrived12ms later, after a2ms quiet
        // window had already admitted a lost control query. These bounded16/32ms values are a measured mitigation,
        // not a protocol minimum or delivery proof. Missing/duplicate C082 cannot uniquely acknowledge a burst.
        var colourAt = _lastColourWriteAt;
        var recentColour = colourAt is { } last && EvidenceTime.GetElapsedTime(last, started) < TimeSpan.FromMilliseconds(16);
        var budget = TimeSpan.FromMilliseconds(recentColour ? 32 : 8);
        RecordFlight(FlightOperation.PreDrain, FlightStage.Start, request, request.Length);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = budget - EvidenceTime.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    RecordFlight(FlightOperation.PreDrain, FlightStage.Cap, request, request.Length);
                    return (ordinary, colour); // Capped mitigation only: this does not establish a quiet/empty device queue.
                }
                var readBudgetUsed = colour >= ColourReplyDrainLimit || ordinary >= 7;
                if (readBudgetUsed && (!recentColour || colourAt is not { } colourStart
                    || EvidenceTime.GetElapsedTime(colourStart) >= TimeSpan.FromMilliseconds(16)))
                {
                    // Reserve one of the existing eight ordinary reads for the proposed command's own strict reply.
                    RecordFlight(FlightOperation.PreDrain, FlightStage.CapBudget, request, request.Length);
                    return (ordinary, colour);
                }
                using var quiet = HidReadWindow.Start(remaining < TimeSpan.FromMilliseconds(2) ? remaining : TimeSpan.FromMilliseconds(2), ct);
                if (quiet.HighResolutionUsed) _drainHighResolutionWindows++; else _drainCoarseWindows++;
                length = 0; Array.Clear(buffer); reads++;
                try
                {
                    // An exhausted read allowance must not cut the RGB settle floor short. In that rare case use the same
                    // bounded high-resolution window without consuming another report; keep the existing report budget.
                    if (readBudgetUsed) await Task.Delay(Timeout.InfiniteTimeSpan, quiet.Token).ConfigureAwait(false);
                    else length = await Read(buffer, quiet.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (quiet.Token.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    var now = EvidenceTime.GetTimestamp();
                    var capped = EvidenceTime.GetElapsedTime(started, now) >= budget;
                    // A short quiet read inside the observed RGB tail is only a gap, not queue-settled evidence.
                    // Keep the same one reader; even no RGB replies can hold control only up to this finite budget.
                    if (!capped && recentColour && colourAt is { } at
                        && EvidenceTime.GetElapsedTime(at, now) < TimeSpan.FromMilliseconds(16)) continue;
                    RecordFlight(FlightOperation.PreDrain, capped ? FlightStage.Cap : FlightStage.Quiet, request, request.Length);
                    return (ordinary, colour);
                }
                RecordFlight(FlightOperation.PreDrain, FlightStage.RxDone, buffer, length);
                RequireNotEof(length);
                var reply = FromWireless(buffer, length);
                RequireNotRefused(reply);
                if (IsWrittenColourReply(reply)) { colour++; continue; }
                ordinary++;
                if (_completedReplies.Any(completed => completed.AsSpan().SequenceEqual(reply))) continue;
                // Existing optional-renewal grammar only; a validated old67/2 acknowledges no current command or input scope.
                RogWiredOledProtocol.RequireRenewalReply(reply);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw Fail(ex, request, buffer, length, reads, "pre-drain", false, false); }
    }

    private static void RequireNotRefused(byte[] reply)
    {
        if (reply[1] != 0xFF || reply[2] != 0xAA) return;
        if (reply.AsSpan(3).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Wireless keyboard OLED refusal envelope changed; no retry.");
        throw new RogOledEnvelopeRefusedException("Wireless keyboard OLED refused an envelope; originating opcode is unavailable; no packet retry.");
    }

    public async Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct)
    {
        Enter(ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromSeconds(2));
        var packet = ToWireless(RogWiredOledProtocol.Renew()); Exception? failure = null; Exception? writerFailure = null;
        async Task ReaderAsync()
        {
            var buffer = new byte[64]; var length = 0; var ordinal = 0; var colourReplies = 0; var completedReplies = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    length = 0; Array.Clear(buffer); ordinal++;
                    length = await Read(buffer, stop.Token).ConfigureAwait(false);
                    RecordFlight(FlightOperation.Lease, FlightStage.RxDone, buffer, length);
                    RequireNotEof(length);
                    var reply = FromWireless(buffer, length);
                    if (IsWrittenColourReply(reply))
                    {
                        if (++colourReplies > ColourReplyDrainLimit) throw new InvalidDataException("Wireless colour reply drain budget exceeded.");
                        continue;
                    }
                    // The same RF duplicate echoes accepted by ExchangeAsync can arrive after it returns, during
                    // the optional renewal reader. Drain only exact replies completed on THIS owner, with a finite
                    // budget. They acknowledge no renewal and never refresh identity/pairing evidence.
                    if (_completedReplies.Any(completed => completed.AsSpan().SequenceEqual(reply)))
                    {
                        if (++completedReplies > 8) throw new InvalidDataException("Wireless completed reply drain budget exceeded.");
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
                    lock (_state) { RequireUsable(); stop.Token.ThrowIfCancellationRequested(); attempted = true;
                        RecordFlight(FlightOperation.Lease, FlightStage.TxAdmit, packet, packet.Length);
                        write = _channel.WriteAsync(packet, deadline.Token); }
                    await write.ConfigureAwait(false);
                    RecordFlight(FlightOperation.Lease, FlightStage.TxDone, packet, packet.Length);
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

    private void RecordFlight(FlightOperation operation, FlightStage stage, ReadOnlySpan<byte> report, int length)
    {
        lock (_state)
        {
            var at = EvidenceTime.GetTimestamp();
            if (_flightOrigin is not null) at = Math.Max(at, _lastFlightAt);
            _flightOrigin ??= at; _lastFlightAt = at;
            ulong header = 0;
            for (var i = 0; i < 5; i++) header = (header << 8) | (i < Math.Min(length, report.Length) ? report[i] : 0u);
            _flight[(int)(_flightCount++ % FlightHistoryCapacity)] = new(at, header, length, operation, stage);
            if (stage == FlightStage.TxAdmit) _hasWireCommand = true;
            if (operation == FlightOperation.RGB)
            {
                if (stage == FlightStage.TxAdmit) _colourWritesAdmitted++;
                else if (stage == FlightStage.TxDone) { _colourWritesCompleted++; _lastColourWriteAt = at; }
            }
        }
    }

    // Caller holds _state. Relative monotonic host times describe invocation/completion, not radio or panel completion.
    private string FlightFaultDetail()
    {
        if (_flightOrigin is not { } origin) return "rfFlight=[]";
        var now = Math.Max(EvidenceTime.GetTimestamp(), _lastFlightAt);
        var records = new string[(int)Math.Min(_flightCount, FlightHistoryCapacity)];
        for (var i = 0; i < records.Length; i++)
        {
            var record = _flight[(int)((Math.Max(0, _flightCount - FlightHistoryCapacity) + i) % FlightHistoryCapacity)];
            records[i] = FormattableString.Invariant($"{EvidenceTime.GetElapsedTime(origin, record.At).TotalMilliseconds:F3}:{record.Operation}/{record.Stage}:{record.Header:X10}/{record.Length}");
        }
        var lastColour = _lastColourWriteAt is { } last
            ? FormattableString.Invariant($"{EvidenceTime.GetElapsedTime(origin, last).TotalMilliseconds:F3}") : "none";
        return FormattableString.Invariant($"rfElapsedMs={EvidenceTime.GetElapsedTime(origin, now).TotalMilliseconds:F3}; colourReportsAdmitted={_colourWritesAdmitted}; colourReportsCompleted={_colourWritesCompleted}; c082Observed={_colourRepliesObserved}; lastColourDoneMs={lastColour}; drainHighResWindows={_drainHighResolutionWindows}; drainCoarseWindows={_drainCoarseWindows}; flightRecords={records.Length}/{_flightCount}; rfFlight=[{string.Join('|', records)}]");
    }
    private Exception Fail(Exception ex, byte[] request, byte[]? reply, int length, int ordinal, string stage, bool zeroAck, bool expired, string? seenHeaders = null)
    {
        lock (_state)
        {
        if (_fault is not null) return _fault;
        var size = reply is null ? 0 : Math.Clamp(length, 0, reply.Length);
        var header = size == 0 ? "none" : Convert.ToHexString(reply!.AsSpan(0, Math.Min(5, size)));
        var nonzero = zeroAck && size > 5 && reply!.AsSpan(5, size - 5).ContainsAnyExcept((byte)0);
        var detail = $"stage={stage}; request={Convert.ToHexString(request.AsSpan(0, 5))}; reply={header}; length={length}; read={ordinal}; zeroAck={zeroAck}; nonzeroAckBody={nonzero}; seenReplyHeaders=[{seenHeaders}]; {FlightFaultDetail()}; connection poisoned; no retry.";
        Exception fault = ex is OperationCanceledException && expired
            ? new RogOledTimeoutException("Wireless keyboard OLED command timed out; " + detail, ex)
             : ex is RogOledEnvelopeRefusedException
                 ? new RogOledEnvelopeRefusedException("Wireless OLED negative envelope; " + detail, ex)
             : ex is RogOledUnexpectedReplyException
                 ? new RogOledUnexpectedReplyException("Wireless OLED unexpected protocol reply; " + detail, ex)
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
