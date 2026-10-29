using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

public interface IRogWiredOledFactory
{
    RogWiredOledConnection? TryOpen(string deviceKey);

    // Existing implementations keep their one-argument route and label. The header is a diagnostic route label, so it neither
    // vetoes the selected device nor is adopted by a connection that never received it.
    RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader) => TryOpen(deviceKey);
}

/// <summary>One exclusive vendor-write owner; typing and consumer/input collections are never opened.</summary>
public sealed class RogWiredOledConnection : IRogOledConnection
{
    private readonly IHidReportChannel _channel;
    public RogNativeControlBinding? NativeControlBinding { get; init; }
    private readonly Func<bool> _sameDevice;
    private readonly Func<string> _deviceKey;
    private readonly Func<string> _firmwareHeader;
    private readonly object _state = new();
    private Exception? _fault;
    private byte[]? _observed;
    private bool _busy, _disposed;

    public RogWiredOledConnection(IHidReportChannel channel, Func<bool> sameDevice)
        : this(channel, sameDevice, () => string.Empty, () => RogWiredOledProtocol.LegacyFirmwareHeader) { }

    public RogWiredOledConnection(IHidReportChannel channel, Func<bool> sameDevice, Func<string> deviceKey, Func<string> firmwareHeader)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _sameDevice = sameDevice ?? throw new ArgumentNullException(nameof(sameDevice));
        _deviceKey = deviceKey ?? throw new ArgumentNullException(nameof(deviceKey));
        _firmwareHeader = firmwareHeader ?? throw new ArgumentNullException(nameof(firmwareHeader));
    }

    public string DeviceKey => _deviceKey();
    public string FirmwareHeader => _firmwareHeader();
    public string ConnectionKind => "wired";
    public int PixelPayloadCapacity => 60;
    public bool SameDevice() => _sameDevice();

    /// <summary>First identity accepted on this connection (diagnostic); null before the first accepted reply.</summary>
    public string? ObservedFirmwareHeader { get { lock (_state) return _observed is null ? null : Convert.ToHexString(_observed); } }

    // Identity must be the wired BasicInfo grammar; every OEM byte and the configured/expected label are diagnostic at first
    // admission. It must not change generation within this connection: that fails closed and the owner lifecycle retires it.
    public void RequireFirmwareIdentity(byte[] identity, string expectedHeader)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!RogRuntimeCompatibility.IsCompatibleWiredIdentity(identity, expectedHeader))
            throw new InvalidDataException("Keyboard identity reply is not the wired BasicInfo grammar; no write.");
        lock (_state)
        {
            _observed ??= identity[..9];
            if (!identity.AsSpan(0, 9).SequenceEqual(_observed))
                throw new InvalidDataException("Keyboard firmware generation changed within this wired connection; no write or replay on it.");
        }
    }

    public async Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Length != 65 || request[0] != 0) throw new ArgumentException("Expected a wired RID0 OLED packet.", nameof(request));
        request = (byte[])request.Clone();
        Enter(ct);
        using var timeout = new CancellationTokenSource(1000);
        var b = new byte[65]; var length = 0; var ordinal = 0; var stage = "write"; var attempted = false; var complete = false;
        try
        {
            try
            {
                ValueTask write;
                lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); attempted = true; write = _channel.WriteAsync(request, timeout.Token); }
                await write.ConfigureAwait(false);
                for (var n = 0; n < 8; n++)
                {
                    stage = "read"; ordinal = n + 1; length = 0; Array.Clear(b);
                    length = await Read(b, timeout.Token).ConfigureAwait(false);
                    stage = "envelope"; RequireNotEof(length);
                    if (length != 65 || b[0] != 0 || b[1] == 0xFF && b[2] == 0xAA)
                        throw new InvalidDataException("Keyboard OLED refused or changed its envelope; no retry.");
                    var perKey = RogPerKeyLighting.IsFrameRequest(request);
                    if (perKey ? RogPerKeyLighting.MatchesReplyHeader(b, request) : b.AsSpan(0, 5).SequenceEqual(request.AsSpan(0, 5)))
                    {
                        stage = zeroAck ? "ack" : "reply";
                        if (perKey) RogPerKeyLighting.RequireReply(b, request);
                        else if (zeroAck) RogWiredOledProtocol.RequireAck(b, request);
                        else if ((request[1], request[2]) is (0x68, 0) or (0x51, 0x2C))
                        {
                            stage = "echo";
                            if (!b.AsSpan().SequenceEqual(request)) throw new InvalidDataException("OLED full payload echo differs.");
                        }
                        complete = true; break;
                    }
                    stage = "optional-renewal"; RogWiredOledProtocol.RequireRenewalReply(b);
                }
                if (!complete) throw new InvalidDataException("OLED optional response budget exceeded.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!attempted) throw; throw Fail(ex, request, b, length, ordinal, stage, zeroAck, timeout.IsCancellationRequested); }
            // Cancellation is a command boundary, never permission to leave its reply for the next command.
            ct.ThrowIfCancellationRequested(); return b;
        }
        finally { Exit(); }
    }

    public async Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct)
    {
        Enter(ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromSeconds(2));
        var packet = RogWiredOledProtocol.Renew(); Exception? failure = null; Exception? writerFailure = null;
        async Task ReaderAsync()
        {
            var b = new byte[65]; var length = 0; var ordinal = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    length = 0; Array.Clear(b); ordinal++;
                    length = await Read(b, stop.Token).ConfigureAwait(false); RequireNotEof(length);
                    RogWiredOledProtocol.RequireRenewalReply(b.AsSpan(0, length));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { failure = Fail(ex, packet, b, length, ordinal, "lease-read", true, false); stop.Cancel(); }
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
    { lock (_state) { RequireUsable(); ct.ThrowIfCancellationRequested(); if (_busy) throw new InvalidOperationException("One wired OLED operation is already active."); _busy = true; } }
    private void Exit() { lock (_state) _busy = false; }
    private void RequireUsable()
    {
        if (_fault is not null) throw new IOException("Wired OLED connection is poisoned; no further wire I/O. First failure: " + _fault.GetType().Name + ": " + _fault.Message, _fault);
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
            ? new RogOledTimeoutException("Wired keyboard OLED command timed out; " + detail, ex)
            : ex is RogOledConnectionLostException || ex is IOException && Disappeared()
                ? new RogOledConnectionLostException("Pinned wired keyboard interface disappeared or ended; " + detail, ex)
                : ex is InvalidDataException ? new InvalidDataException("Wired OLED protocol response invalid; " + detail, ex)
                : new IOException("Wired OLED I/O outcome is unknown; " + detail, ex);
        return _fault = fault;
        }
    }

    private static void RequireNotEof(int length)
    {
        if (length == 0) throw new RogOledConnectionLostException("Wired keyboard OLED stream ended.", new EndOfStreamException());
    }

    private bool Disappeared()
    {
        try { return !_sameDevice(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    public void Dispose() { lock (_state) { if (_disposed) return; _disposed = true; _channel.Dispose(); } }
}

public sealed class RogWiredOledFactory : IRogWiredOledFactory
{
    private readonly IHidReportDevices? _devices;
    private readonly IHidContainerResolver? _resolver;
    private readonly Func<HidCollectionInfo, IHidReportChannel>? _openExclusive;

    public RogWiredOledFactory() { }

    internal RogWiredOledFactory(IHidReportDevices devices, IHidContainerResolver resolver,
        Func<HidCollectionInfo, IHidReportChannel> openExclusive)
    {
        _devices = devices; _resolver = resolver; _openExclusive = openExclusive;
    }

    public RogWiredOledConnection? TryOpen(string deviceKey) => TryOpen(deviceKey, RogWiredOledProtocol.LegacyFirmwareHeader);

    public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
    {
        // The header is a diagnostic note: a wired-form label is kept verbatim, anything else becomes the neutral wired label.
        // Only the exact selected collection/container decides what opens.
        var label = RogRuntimeCompatibility.RouteLabel("wired", firmwareHeader)!;
        var devices = _devices ?? new WinHidReportDevices();
        var candidates = devices.Enumerate(0x0B05, 0x1C24).Where(RogWiredOledProtocol.Matches)
            .Where(c => string.Equals(RogWiredOledProtocol.DeviceKey(c), deviceKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return null;
        if (candidates.Length != 1) throw new InvalidOperationException("Ambiguous pinned keyboard control collection.");
        var c = candidates[0];
        var resolver = _resolver ?? new WinHidContainerResolver();
        var container = resolver.Resolve(c).Container ?? throw new InvalidOperationException("Keyboard physical identity unavailable.");
        var channel = _openExclusive is null ? WinHidReportDevices.OpenExclusiveWriter(c) : _openExclusive(c);
        return new(channel, () =>
        {
            var fresh = devices.Enumerate(0x0B05, 0x1C24).SingleOrDefault(x => x.Path == c.Path && RogWiredOledProtocol.Matches(x));
            return fresh is not null && resolver.Resolve(fresh).Container is { } current && current.SameContainer(container);
        }, () => deviceKey, () => label) { NativeControlBinding = new([c], null, 0) };
    }
}
