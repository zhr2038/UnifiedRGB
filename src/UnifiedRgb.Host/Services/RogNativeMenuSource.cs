using System.Collections.Concurrent;
using System.Security.Cryptography;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// The physical source of Pack C's typed seam. It borrows the existing keyboard OLED/RGB actor; it never opens a control
/// connection or dispatches a case action. Only the exact input-only vendor collection is opened, only after opt-in and
/// current GYM1 capability/physical identity admission. All control transactions occur inside Actor.Step/Stop, after the
/// existing owner's previous upload/renew reader has joined. No timer, independent animation clock, or RPC injection seam.
/// Control (ARM, input reader, cookie, genuine-neutral admission) does not depend on Host artwork: a step without artwork keeps
/// the lever live while the keyboard shows its own content; <see cref="IsPresent"/> reports that control availability.
/// </summary>
public sealed class RogNativeMenuSource(KeyboardNativeMenuOptions options, IHidInputReportDevices? inputs = null,
    IHidContainerResolver? containers = null, TimeProvider? time = null, Func<ulong>? cookie = null,
    KeyboardDisplayOptions? displayOptions = null, Action<int>? codexTaskBrowse = null) : INativeMenuSource
{
    private readonly KeyboardNativeMenuOptions[] _routes = options.QualifiedRoutes(displayOptions);
    private readonly bool _verboseCodexCommit = options.DiagnoseCodexPostUploadCapability;
    private readonly Action<int>? _codexTaskBrowse = codexTaskBrowse;
    private readonly object _sync = new();
    private readonly IHidInputReportDevices? _inputs = inputs;
    private readonly IHidContainerResolver? _containers = containers;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<ulong> _cookie = cookie ?? (() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));
    private Action<NativeMenuSourceEvent>? _deliver;
    private Actor? _actor;
    private int _requested, _codexRequested, _present, _dedicatedSupport;
    private long _requestRevision;
    private CodexLaunchCoordinator? _codexLaunch;
    internal void SetLaunchCoordinator(CodexLaunchCoordinator coordinator) => _codexLaunch = coordinator;
    internal string? OpenActionStatus => _codexLaunch?.Status;

    public bool IsPresent => Volatile.Read(ref _present) != 0;
    /// <summary>The pinned installed policy owns OLED output even while native operation is disabled or outside mode9.</summary>
    public bool OwnsDisplayOutput => options.Admitted;
    internal bool ControlsRequested => OwnsDisplayOutput && Volatile.Read(ref _requested) != 0;
    internal bool Requested => OwnsDisplayOutput && (ControlsRequested || Volatile.Read(ref _codexRequested) != 0);
    internal bool? DedicatedPageSupported => Volatile.Read(ref _dedicatedSupport) switch { 1 => false, 2 => true, _ => null };
    internal bool DedicatedPageActive => Volatile.Read(ref _actor)?.DedicatedPageActive == true;
    private bool PageRequested(RogNativeCapability capability) => RogNativeMenuCodec.IsAdmittedPage(capability)
        && (capability.Mode == RogNativeMenuCodec.NativeMode ? ControlsRequested : Volatile.Read(ref _codexRequested) != 0);
    internal long RequestRevision => Interlocked.Read(ref _requestRevision);
    internal IReadOnlyList<KeyboardNativeMenuOptions> QualifiedRoutes => _routes;
    internal bool AcceptsScope(string key, string header, string kind) => _routes.Any(route => route.MatchesScope(key, header, kind));

    public IDisposable Connect(Action<NativeMenuSourceEvent> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        lock (_sync)
        {
            if (_deliver is not null) throw new InvalidOperationException("Native source already has its Host service owner.");
            _deliver = deliver;
            return new Registration(this, deliver);
        }
    }

    public void RequestEnabled(bool enabled)
    {
        Interlocked.Increment(ref _requestRevision);
        Volatile.Write(ref _requested, enabled ? 1 : 0);
        if (!enabled && Volatile.Read(ref _actor)?.IsCodexContext != true) Volatile.Read(ref _actor)?.Retire("disabled", disconnected: false);
    }

    internal void RequestCodexEnabled(bool enabled)
    {
        if (Interlocked.Exchange(ref _codexRequested, enabled ? 1 : 0) == (enabled ? 1 : 0)) return;
        _codexLaunch?.Invalidate();
        Interlocked.Increment(ref _requestRevision);
        Volatile.Read(ref _actor)?.Retire("dedicated page preference changed", disconnected: false);
    }

    /// <summary>Called once by the current KeyboardDisplayService connection owner; constructor/Attach perform no I/O.</summary>
    public Actor Attach(IRogOledConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var actor = new Actor(this, connection);
        if (Interlocked.CompareExchange(ref _actor, actor, null) is not null)
            throw new InvalidOperationException("Native source cannot borrow two keyboard control owners.");
        Volatile.Write(ref _dedicatedSupport, 0);
        return actor;
    }

    private void Emit(NativeMenuSourceEvent value)
    {
        Action<NativeMenuSourceEvent>? deliver;
        lock (_sync) deliver = _deliver;
        deliver?.Invoke(value);
    }

    private sealed class Registration(RogNativeMenuSource source, Action<NativeMenuSourceEvent> deliver) : IDisposable
    {
        public void Dispose()
        {
            source.RequestEnabled(false);
            source.RequestCodexEnabled(false);
            lock (source._sync) if (ReferenceEquals(source._deliver, deliver)) source._deliver = null;
        }
    }

    /// <summary>Methods with wire I/O are called exclusively from the existing OLED/RGB actor boundary.</summary>
    public sealed class Actor : IAsyncDisposable
    {
        public const int MaximumPendingNotifications = 16;
        private readonly RogNativeMenuSource _source;
        private readonly IRogOledConnection _connection;
        private readonly string _key, _firmware, _kind, _session = Guid.NewGuid().ToString("N");
        private readonly int _capacity;
        private readonly byte _transport;
        private readonly object _sync = new();
        private readonly RogNativeMenuState _state = new();
        private readonly ConcurrentQueue<Received> _queue = new();
        private CancellationTokenSource? _readerStop;
        private HidVendorInputTransport? _input;
        private Task? _reader;
        private RogNativeCapability? _armed;
        private (uint Epoch, ulong Cookie)? _disarmAttempted;
        private byte[]? _sent;
        private Func<RogNativeCapability, byte[]?>? _latest;
        private bool _rearmIdle;
        private HidPhysicalContainer? _physical;
        public bool PhysicalScopeLost { get; private set; }
        private bool _joinFailed;
        public bool RecoveryBlocked => PhysicalScopeLost || _joinFailed || _wireFault is not null;
        private ulong _lastCookie;
        private (long Observations, long At)? _lastEvidence;
        private ushort _neutral;
        private Exception? _wireFault;
        private bool _artworkRefused;
        private bool _retired, _rejected, _stopped, _observedRequested;
        private int _busy;
        private long _receivedNotifications;
        // Only an actually received, validated readonly CAP can bound a quiet tick. This never refreshes RF evidence.
        private RogNativeCapability? _lastReadCapability;
        private long? _lastReadCapabilityAt;
        private long _outsideCapabilityRequestRevision = -1;
        private long _observedRequestRevision = -1;
        internal long ReceivedNotifications => Interlocked.Read(ref _receivedNotifications);
        internal Action<byte>? PageObserved { get; set; }
        internal bool IsCodexContext { get { lock (_sync) return _state.Scope?.Mode == RogNativeMenuCodec.CodexMode; } }

        internal Actor(RogNativeMenuSource source, IRogOledConnection connection)
        {
            _source = source; _connection = connection;
            _key = connection.DeviceKey; _firmware = connection.FirmwareHeader; _kind = connection.ConnectionKind;
            _capacity = connection.PixelPayloadCapacity;
            // Owner route + captured control descriptors, confirmed by the live CAP transport byte; the header is diagnostic only.
            _transport = NativeInputDiscovery.Route(_kind, _capacity, _key, connection.NativeControlBinding);
        }

        public bool HasChanges => _source.RequestRevision != _observedRequestRevision || _source.Requested != _observedRequested || !_queue.IsEmpty
            || (_armed is not null && (_retired || _reader?.IsCompleted == true
                || _latest is not null && (!CanSkipCapability()
                    || ArtworkChanged(_latest(_lastReadCapability!.Value), _sent))));

        public bool Armed { get { lock (_sync) return _state.Scope is not null && !_retired && !_stopped; } }

        /// <summary>The native context (input reader, cookie, genuine-neutral admission) is live, with or without Host artwork.</summary>
        public bool ControlArmed { get { lock (_sync) return Armed && _source.ControlsRequested && _state.Scope?.Mode == RogNativeMenuCodec.NativeMode; } }
        public bool DedicatedPageActive { get { lock (_sync) return ArtworkActive && _state.Scope?.Mode == RogNativeMenuCodec.CodexMode; } }

        /// <summary>Host artwork is this live context's acknowledged overlay; false while the keyboard shows its own content.</summary>
        public bool ArtworkActive { get { lock (_sync) return _state.Scope is not null && !_retired && !_stopped && _sent is not null; } }

        /// <summary>The previous context ended only because Host artwork ended for device idle; the next step ARMs a fresh context
        /// without Host artwork. Not a native-mode exit, so the presentation keeps its entry (no second Logo).</summary>
        public bool RearmingForDeviceIdle => Volatile.Read(ref _rearmIdle);

        /// <summary>
        /// Runs after the previous owner lease/reader joined; returns whether Host artwork is active (mode9 owns this tick's artwork).
        /// A null <paramref name="latest"/> answer asks for no Host artwork: the context is still ARMed (or kept) with its input
        /// reader and neutral admission, and nothing is uploaded. When Host artwork was active, a null answer retires, joins and
        /// DISARMs that context through the existing lifecycle; the next step ARMs a fresh context without artwork.
        /// </summary>
        public Task<bool> StepAsync(Func<byte[]?> latest, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(latest);
            return StepAsync(_ => latest(), ct);
        }

        public async Task<bool> StepAsync(Func<RogNativeCapability, byte[]?> latest, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(latest);
            Enter();
            try
            {
                if (_joinFailed) throw new IOException("Native reader did not join; this owner remains retired.");
                _latest = latest;
                var requestRevision = _source.RequestRevision;
                var requestChanged = _observedRequested != _source.Requested || requestRevision != _observedRequestRevision;
                _observedRequestRevision = requestRevision;
                _observedRequested = _source.Requested;
                if (!_observedRequested || _stopped || _rejected || _transport == 0 || !_source.AcceptsScope(_key, _firmware, _kind))
                {
                    Volatile.Write(ref _rearmIdle, false);
                    if (_armed is not null || _reader is not null) await LeaveAsync("disabled or unavailable", ct).ConfigureAwait(false);
                    return false;
                }
                // Outside native mode there is no artwork selector or input context to service. Keep this separately from
                // the armed artwork cache, and do no enumeration, control I/O or callback during its actual observation age.
                if (!requestChanged && CanSkipOutsideCapability()) { ct.ThrowIfCancellationRequested(); return false; }
                RequireConnection();
                ValidateCurrentPhysicalScope();
                byte[]? selectedPixels = null;
                var selected = false;
                if (!requestChanged && CanSkipCapability())
                {
                    // Select once: the callback may advance an animation's gate/measurement state. If it changed, retain
                    // these bytes through the fresh CAP below instead of calling it a second time.
                    selectedPixels = latest(_lastReadCapability!.Value); selected = true;
                    if (selectedPixels is not null && selectedPixels.Length != RogWiredOledProtocol.PixelBytes)
                        throw new InvalidDataException("Native artwork must be exactly6656 bytes.");
                    if (!ArtworkChanged(selectedPixels, _sent) && !_retired && _source.Requested && CanSkipCapability())
                        return ArtworkActive;
                }
                // Only this actual CAP refreshes observation state; queued input and every mutation still use this fresh path.
                var reply = await ReadStepCapabilityAsync(ct).ConfigureAwait(false);
                var observedAt = _source._time.GetTimestamp();
                RogNativeCapability capability;
                try { capability = RogNativeMenuCodec.ParseCapability(reply, 0x12, _transport); }
                catch (InvalidDataException)
                {
                    _rejected = true; Volatile.Write(ref _source._present, 0);
                    await LeaveAsync("firmware/build capability differs", ct, noWire: true).ConfigureAwait(false);
                    return false; // A well-framed unsupported build is native-unavailable, with no automatic probe loop.
                }
                if (_armed is not null && PairingStale(_connection.NativeControlBinding) is { } stale)
                {
                    await LeaveAsync("connection-generation pairing proof expired: " + stale, ct, capability).ConfigureAwait(false);
                    _rejected = true; Volatile.Write(ref _source._present, 0);
                    return false;
                }
                if (_source._verboseCodexCommit && _lastReadCapability?.Mode != capability.Mode)
                    Serilog.Log.Information("Codex diagnostic observed page: mode={Mode}, profile={Profile}, transport={Transport}, epoch={Epoch}, flags={Flags}, status={Status}",
                        capability.Mode, capability.Profile, capability.Transport, capability.Epoch, capability.Flags, capability.Status);
                Volatile.Write(ref _source._dedicatedSupport, RogNativeMenuCodec.SupportsDedicatedPage(capability) ? 2 : 1);
                PageObserved?.Invoke(capability.Mode); // includes stock pages: no artwork callback is needed to observe an exit
                if (!_source.PageRequested(capability))
                {
                    await LeaveAsync("native mode exited", ct, capability).ConfigureAwait(false);
                    _lastReadCapability = capability;
                    _lastReadCapabilityAt = observedAt;
                    Interlocked.Exchange(ref _outsideCapabilityRequestRevision, requestRevision);
                    return false;
                }
                if (_armed is { } prior && (!Matches(capability) || _retired))
                {
                    await LeaveAsync("epoch/profile/transport/input scope changed", ct, capability).ConfigureAwait(false);
                    // A normal menu/profile change ends this epoch, not the keyboard owner. The next tick must
                    // establish a fresh CAP/ARM/neutral context; no event from the retired reader is replayed.
                    return false;
                }
                ValidateCurrentPhysicalScope();
                _lastReadCapability = capability;
                _lastReadCapabilityAt = observedAt;
                Drain();
                if (_retired && _armed is not null)
                {
                    await LeaveAsync("input invalidated", ct, capability).ConfigureAwait(false);
                    // A lost input sequence can recover on a fresh epoch after this reader has joined. Unknown
                    // control writes, physical changes and failed joins remain latched by their existing paths.
                    return false;
                }
                var pixels = selected ? selectedPixels : latest(capability);
                if (pixels is not null && pixels.Length != RogWiredOledProtocol.PixelBytes)
                    throw new InvalidDataException("Native artwork must be exactly6656 bytes.");
                if (pixels is null && _armed is not null && _sent is not null)
                {
                    // Host artwork ended for device idle: retire/join/DISARM through the existing lifecycle. The session then runs
                    // its existing artwork-to-idle restore; the next step ARMs a fresh context without artwork (clearing the
                    // overlay), which again needs a genuine neutral. No held frame stands in for the keyboard's own content.
                    await LeaveAsync("host artwork ended for device idle", ct, capability).ConfigureAwait(false);
                    Volatile.Write(ref _rearmIdle, true);
                    return false;
                }
                if (_armed is null)
                {
                    // The first step establishes an epoch/input context, with or without Host artwork; replacements keep it and
                    // queued neutral/presses.
                    Retire("initial native epoch", disconnected: false);
                    await JoinReaderAsync().ConfigureAwait(false);
                    if (!_source.PageRequested(capability)) { await LeaveAsync("disabled", ct).ConfigureAwait(false); return false; }
                    var prepared = PrepareInput(); // exact positive descriptor + same physical container; no control open
                    _input = prepared;
                    var nextCookie = _source._cookie();
                    if (nextCookie == 0 || nextCookie == _lastCookie) throw new InvalidDataException("Fresh native Host cookie unavailable.");
                    _lastCookie = nextCookie;
                    if (!_source.PageRequested(capability) || requestRevision != _source.RequestRevision)
                    { await LeaveAsync("page consumer changed before ARM", ct).ConfigureAwait(false); return false; }
                    var armReply = await ExchangeAsync(RogNativeMenuCodec.ArmRequest(capability, nextCookie), false, ct).ConfigureAwait(false);
                    RogNativeCapability armed;
                    try
                    {
                        armed = RogNativeMenuCodec.ParseCapability(armReply, 0x71, _transport);
                        if (!RogNativeMenuCodec.SupportsInEpochArtwork(armed)) throw new InvalidDataException("In-epoch native artwork was not declared.");
                        lock (_sync)
                        {
                            _state.Arm(capability, armed, nextCookie);
                            _armed = armed; _neutral = 0; _retired = false; _sent = null;
                            while (_queue.TryDequeue(out _)) { }
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { LatchUnknown(ex, "unvalidated native ARM mutation reply"); throw; }
                    Volatile.Write(ref _rearmIdle, false);
                    Volatile.Write(ref _source._present, armed.Mode == RogNativeMenuCodec.NativeMode ? 1 : 0);
                    _source.Emit(new NativeMenuContextBegun(_session, armed.Epoch, armed.Profile, 0, armed.Mode));
                    _readerStop = new CancellationTokenSource();
                    _reader = ReadAsync(prepared, _readerStop.Token);
                }
                if (pixels is not null && (_sent is null || !pixels.AsSpan().SequenceEqual(_sent)))
                {
                    using var upload = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    upload.CancelAfter(TimeSpan.FromSeconds(8));
                    foreach (var packet in RogWiredOledProtocol.Pack(pixels, _capacity))
                    {
                        if (!_source.PageRequested(capability) || _retired) { await LeaveAsync("admission closed during artwork", ct).ConfigureAwait(false); return false; }
                        RequireConnection();
                        RogWiredOledProtocol.RequireAck(await ExchangeAsync(packet, true, upload.Token).ConfigureAwait(false), packet);
                    }
                    if (!_source.PageRequested(capability) || _retired) { await LeaveAsync("admission closed before frame commit", ct).ConfigureAwait(false); return false; }
                    RequireConnection();
                    // Each complete Codex frame must prove that the firmware committed these bytes in the exact ARM epoch.
                    // This is a post-mutation barrier, not an optional probe or a recoverable standalone observation.
                    var frameReadyProof = _armed is { Mode: RogNativeMenuCodec.CodexMode } expected
                        ? await RequireCodexFrameReadyAsync(expected, requestRevision, upload.Token).ConfigureAwait(false) : null;
                    // Force one renewal on this owner, then cancel/join its optional reply reader; no mandatory ACK.
                    var didRenew = false;
                    try
                    {
                        await _connection.LeaseAsync(() => didRenew || !_source.PageRequested(capability) || _retired, () => didRenew = true, upload.Token).ConfigureAwait(false);
                        if (!didRenew && _source.PageRequested(capability) && !_retired) throw new IOException("Native display renewal was not sent.");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        if (frameReadyProof is not null)
                        {
                            var diagnosed = new IOException("Native first renewal failed after complete pixel upload; " + frameReadyProof + "; no retry. " + ex.Message, ex);
                            LatchUnknown(diagnosed, "native renewal failed after commit readiness"); throw diagnosed;
                        }
                        LatchUnknown(ex, "native renewal failed"); throw;
                    }
                    if (!_source.PageRequested(capability) || _retired) { await LeaveAsync("admission closed after native renewal", ct).ConfigureAwait(false); return false; }
                    _sent = (byte[])pixels.Clone();
                }
                return ArtworkActive;
            }
            catch (RogNativeImageNackException ex)
            {
                if (!await AbortImageAsync(ct).ConfigureAwait(false)) return false;
                throw new RogNativeImageContinuationException("Native image abandoned; same control owner awaits a fresh native context/current frame.", ex);
            }
            finally { Exit(); }
        }

        private async Task<bool> AbortImageAsync(CancellationToken ct)
        {
            var prior = _armed ?? throw new InvalidDataException("Native image NACK has no armed scope.");
            Volatile.Write(ref _source._present, 0);
            Retire("ephemeral image negative; joined native context for same-owner resynchronization", false);
            try
            {
                await JoinReaderAsync().ConfigureAwait(false);
                if (RecoveryBlocked) throw new IOException("Native image retirement is blocked; no resynchronization I/O.");
                ct.ThrowIfCancellationRequested(); RequireConnection();
                if (!_source.Requested) { await LeaveAsync("disabled after image negative", ct, noWire: true).ConfigureAwait(false); return false; }
                var cap = RogNativeMenuCodec.ParseCapability(await ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false), 0x12, _transport);
                if (!_source.PageRequested(cap) || cap.Mode != prior.Mode || cap.Profile != prior.Profile || cap.Transport != prior.Transport)
                {
                    await LeaveAsync("mode/profile/transport changed after image negative", ct, noWire: true).ConfigureAwait(false);
                    return false;
                }
                if (cap.Status != 0)
                {
                    if (cap.Epoch != prior.Epoch || cap.Cookie != prior.Cookie)
                        throw new InvalidDataException("Different native owner after image negative; no DISARM or scope replay.");
                    await LeaveAsync("same native scope image reset", ct, cap).ConfigureAwait(false); // one matching DISARM clears shared RX
                    cap = RogNativeMenuCodec.ParseCapability(await ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false), 0x12, _transport);
                }
                else await LeaveAsync("native image context already cleared", ct, noWire: true).ConfigureAwait(false);
                if (!_source.PageRequested(cap) || cap.Mode != prior.Mode || cap.Profile != prior.Profile || cap.Transport != prior.Transport
                    || cap.Status != 0 || cap.Cookie != 0 || cap.Epoch < prior.Epoch)
                    throw new InvalidDataException("Native image reset was not verified; no new ARM.");
                _lastReadCapability = cap; _lastReadCapabilityAt = _source._time.GetTimestamp();
                Volatile.Write(ref _rearmIdle, true);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { LatchUnknown(ex, "native image resynchronization failed"); throw; }
        }

        private static bool ArtworkChanged(byte[]? latest, byte[]? sent) =>
            latest is null ? sent is not null : sent is null || !latest.AsSpan().SequenceEqual(sent);

        private bool CanSkipCapability()
        {
            if (_armed is null || _retired || _stopped || RecoveryBlocked || _reader is not { IsCompleted: false }
                || !_queue.IsEmpty || _lastReadCapability is not { } observed || _lastReadCapabilityAt is not { } at
                || !_source.PageRequested(observed) || !Matches(observed)) return false;
            var now = _source._time.GetTimestamp();
            if (now < at || _source._time.GetElapsedTime(at, now) >= TimeSpan.FromMilliseconds(100)) return false;
            // Checking an immutable owner snapshot is not a new observation. Stale/future/inconsistent proof forces the
            // actual CAP path and its existing fail-closed checks; no input is drained and no mutation uses this shortcut.
            return PairingStale(_connection.NativeControlBinding) is null;
        }

        private bool CanSkipOutsideCapability()
        {
            if (_armed is not null || _reader is not null || _input is not null || !_queue.IsEmpty || _stopped || _rejected
                || RecoveryBlocked || _artworkRefused || _lastReadCapability is not { } observed
                || _source.PageRequested(observed) || _lastReadCapabilityAt is not { } at
                || Interlocked.Read(ref _outsideCapabilityRequestRevision) != _source.RequestRevision
                || _connection.DeviceKey != _key || _connection.FirmwareHeader != _firmware
                || _connection.ConnectionKind != _kind || _connection.PixelPayloadCapacity != _capacity) return false;
            var now = _source._time.GetTimestamp();
            return now >= at && _source._time.GetElapsedTime(at, now) < TimeSpan.FromMilliseconds(100)
                && PairingStale(_connection.NativeControlBinding) is null;
        }

        /// <summary>
        /// Null when this RF route's pairing proof is current; wired routes carry none. Owner evidence keeps the initial
        /// provenance and is current only through a validated same-owner observation within 3 minutes; an owner without
        /// evidence keeps the initial-observation rule. Invalidated, relabelled, replayed/backwards or future evidence is never
        /// current. Container, input collection, profile, epoch, cookie and sequence are still checked separately.
        /// </summary>
        private string? PairingStale(RogNativeControlBinding? binding)
        {
            if (_transport != 2) return null;
            var now = _source._time.GetTimestamp();
            if (binding?.Keyboard is not { } target || !RogKeyboardInputContract.IsVerifiedTarget(target)) return "verified RF keyboard route unavailable";
            if (_connection.PairingEvidence is not { } evidence) return Current(binding.PairingTimestamp, now, "initial pairing observation");
            if (evidence.Invalidated is { } why) return "same-owner evidence invalidated (" + why + ")";
            if (evidence.Keyboard != target || evidence.PairingTimestamp != binding.PairingTimestamp)
                return "same-owner evidence route or initial provenance differs";
            if (evidence.Observations < 0 || evidence.LastObservedTimestamp < evidence.PairingTimestamp
                || evidence.Observations == 0 && evidence.LastObservedTimestamp != evidence.PairingTimestamp
                || _lastEvidence is { } last && (evidence.Observations < last.Observations || evidence.LastObservedTimestamp < last.At
                    || evidence.Observations == last.Observations && evidence.LastObservedTimestamp != last.At))
                return "same-owner evidence is inconsistent, replayed or went backwards";
            _lastEvidence = (evidence.Observations, evidence.LastObservedTimestamp);
            return Current(evidence.LastObservedTimestamp, now, "last same-owner observation");
        }

        private string? Current(long at, long now, string what) => at > now ? what + " is dated in the future"
            : _source._time.GetElapsedTime(at, now) > TimeSpan.FromMinutes(3) ? what + " is older than 3 minutes" : null;

        private HidVendorInputTransport PrepareInput()
        {
            var binding = _connection.NativeControlBinding ?? throw new InvalidDataException("This control owner has no proven native input binding.");
            var devices = _source._inputs ?? new WinHidInputReportDevices();
            var containers = _source._containers ?? new WinHidContainerResolver();
            if (PairingStale(binding) is { } stale)
                throw new InvalidDataException("Fresh connection-generation RF pairing proof unavailable (" + stale + "); no second pairing/control session is opened.");
            if (binding.Controls.Count == 0) throw new InvalidDataException("Native control container unavailable.");
            if (_transport == 1 && binding.Keyboard is not null) throw new InvalidDataException("Captured native control descriptor differs.");
            HidPhysicalContainer? physical = null;
            foreach (var control in binding.Controls)
            {
                if (_transport == 1 ? !RogWiredOledProtocol.Matches(control) || !string.Equals(RogWiredOledProtocol.DeviceKey(control), _key, StringComparison.OrdinalIgnoreCase)
                    : control.Vid != 0x0B05 || control.Pid != 0x1ACE || control.Interface != 2)
                    throw new InvalidDataException("Captured native control descriptor differs.");
                var current = RogKeyboardInputContract.Fresh(containers.Resolve(control), _source._time, TimeSpan.FromMinutes(3), "Native control");
                if (physical is not null && !physical.SameContainer(current)) throw new InvalidDataException("Native control containers conflict.");
                physical = current;
            }
            // Interface derived from the one exact, fresh, same-container descriptor (actual: wired MI02, RF MI03); never a fixed MI03.
            var shape = NativeInputDiscovery.ShapeFor(_transport);
            var selected = NativeInputDiscovery.Select(shape, devices.Enumerate(shape.Vid, shape.Pid), binding.Controls, physical!,
                containers, _source._time, TimeSpan.FromMinutes(3));
            var input = HidVendorInputTransport.Open(devices, selected.Collection, selected.Rule, TimeSpan.FromMilliseconds(250), _source._time);
            try
            {
                RequireConnection();
                var after = RogKeyboardInputContract.Fresh(containers.Resolve(selected.Collection), _source._time, TimeSpan.FromMinutes(3), "Opened native event");
                if (!physical!.SameContainer(after)) throw new InvalidDataException("Opened native event physical container changed.");
                foreach (var control in binding.Controls)
                    if (!physical.SameContainer(RogKeyboardInputContract.Fresh(containers.Resolve(control), _source._time, TimeSpan.FromMinutes(3), "Opened native control")))
                        throw new InvalidDataException("Control physical container changed while binding native input.");
                _physical = physical;
                return input;
            }
            catch { input.Dispose(); throw; }
        }

        private void ValidateCurrentPhysicalScope()
        {
            if (_armed is null || _physical is null || _input is null) return;
            try
            {
                var resolver = _source._containers ?? new WinHidContainerResolver();
                var binding = _connection.NativeControlBinding ?? throw new InvalidDataException("Control binding lost.");
                foreach (var collection in binding.Controls.Append(_input.Collection))
                    if (!_physical.SameContainer(RogKeyboardInputContract.Fresh(resolver.Resolve(collection), _source._time,
                        TimeSpan.FromMinutes(3), "Current native scope"))) throw new InvalidDataException("Physical native container changed.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PhysicalScopeLost = true; Volatile.Write(ref _source._present, 0);
                Retire("physical native scope lost", true);
                throw new InvalidDataException("Native physical scope lost; no cross-scope restore or further control I/O.", ex);
            }
        }

        private async Task ReadAsync(HidVendorInputTransport input, CancellationToken ct)
        {
            var report = new byte[21];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await input.ReadAsync(report, ct).ConfigureAwait(false)) continue;
                    // UTC is assigned HERE at Host read admission; no device tick or later queue-drain conversion.
                    var received = _source._time.GetUtcNow();
                    var kind = RogNativeMenuCodec.ParseNotice(report, _transport, out var notice);
                    // Physical page changes revoke service admission immediately, before any queued mode9 work can drain.
                    if (report[1] == 0x91 && _armed is { } scope && report[4] != scope.Mode)
                    { Retire("physical native page changed", false); return; }
                    if (_queue.Count >= MaximumPendingNotifications) { Retire("native input queue overflow", false); return; }
                    _queue.Enqueue(new(kind, notice, received));
                    Interlocked.Increment(ref _receivedNotifications);
                    if (kind is RogNativeNoticeKind.Malformed or RogNativeNoticeKind.OutsideNative)
                    { Retire("native input mode/descriptor invalidation", false); return; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Retire("native input fault: " + ex.GetType().Name, false); }
            finally { Array.Clear(report); input.Dispose(); }
        }

        private void Drain()
        {
            while (_queue.TryDequeue(out var item))
            {
                RogNativeDecision decision;
                NativeMenuCaseIntent? intent = null;
                NativeMenuSceneBrowse? browse = null;
                CodexLaunchIntent? launch = null;
                (CodexLaunchIntent Scope, int Direction, long Revision)? taskBrowse = null;
                lock (_sync)
                {
                    if (_retired || _stopped || !_source.Requested) continue;
                    var neutralBefore = _state.Neutral;
                    var armedNeutral = _neutral;
                    var sequenceBefore = _state.Sequence;
                    decision = _state.Accept(item.Kind, item.Notice);
                    if (decision == RogNativeDecision.Ignore && item.Kind == RogNativeNoticeKind.Tagged && item.Notice.Mask is 1 or 2 or 4
                        && neutralBefore && armedNeutral != 0 && item.Notice.Sequence == armedNeutral + 1
                        && item.Notice.Sequence == sequenceBefore + 1
                        && _state.Sequence == item.Notice.Sequence && _state.Scope is { Mode: RogNativeMenuCodec.CodexMode } codex
                        && item.Notice.Epoch == codex.Epoch && item.Notice.Cookie == codex.Cookie && item.Notice.Profile == codex.Profile
                        && item.Notice.Mode == codex.Mode && (item.Notice.Flags & 1) != 0
                        && _source._time.GetUtcNow() - item.At is var age && age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(750)
                        && RogNativeMenuCodec.SupportsDedicatedPage(codex) && _source.PageRequested(codex))
                    {
                        var codexEdge = new CodexLaunchIntent(_session, codex.Epoch, codex.Cookie, codex.Profile, item.Notice.Sequence,
                            armedNeutral, item.At, codex.Mode, codex.Flags);
                        if (item.Notice.Mask == 2) launch = codexEdge;
                        else taskBrowse = (codexEdge, item.Notice.Mask == 1 ? -1 : 1, _source.RequestRevision);
                    }
                    if (decision != RogNativeDecision.Disarmed && item.Kind == RogNativeNoticeKind.Tagged
                        && item.Notice.Mask == 0 && _state.Neutral && _state.Sequence == item.Notice.Sequence) _neutral = item.Notice.Sequence;
                    if (_source.ControlsRequested && decision is RogNativeDecision.EnsureOn or RogNativeDecision.EnsureOff or RogNativeDecision.Toggle && _state.Scope is { Mode: RogNativeMenuCodec.NativeMode } scope)
                        intent = new(_session, scope.Epoch, scope.Profile, item.Notice.Sequence, item.At,
                            decision == RogNativeDecision.Toggle ? DesiredAction.Toggle
                                : decision == RogNativeDecision.EnsureOn ? DesiredAction.EnsureOn : DesiredAction.EnsureOff, _neutral);
                    // Up (72/01) and Down (72/04): only the fresh edge the decoder just accepted in this scope directly after a
                    // validated neutral (same rule as center). Held repeats, pre-neutral presses and ordinary 72 never browse.
                    if (_source.ControlsRequested && decision == RogNativeDecision.Ignore && item.Kind == RogNativeNoticeKind.Tagged && item.Notice.Mask is 1 or 4
                        && neutralBefore && armedNeutral != 0 && item.Notice.Sequence == armedNeutral + 1
                        && _state.Sequence == item.Notice.Sequence && _state.Scope is { Mode: RogNativeMenuCodec.NativeMode } browseScope)
                        browse = new(_session, browseScope.Epoch, browseScope.Profile, item.Notice.Sequence, item.At,
                            item.Notice.Mask == 1 ? SceneCycleMove.Previous : SceneCycleMove.Next, armedNeutral);
                }
                if (decision == RogNativeDecision.Disarmed) { Retire("native sequence/cookie/profile invalidation", false); return; }
                if (intent is not null && _source.ControlsRequested && !_retired) _source.Emit(new NativeMenuPress(intent));
                if (browse is not null && _source.ControlsRequested && !_retired) _source.Emit(browse);
                if (taskBrowse is { } navigation && _source._codexTaskBrowse is { } callback
                    && TaskBrowseAdmitted(navigation.Scope, navigation.Revision))
                {
                    // Pure display selection only. The normal frame provider observes it after Drain on this same step;
                    // no case action, launch, wire transaction or alternate keyboard owner is introduced here.
                    try { callback(navigation.Direction); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { Serilog.Log.Warning("Codex task browse callback failed: {ExceptionType}", ex.GetType().Name); }
                }
                if (launch is not null)
                {
                    var captured = launch;
                    _source._codexLaunch?.Submit(captured, () => LaunchAdmitted(captured));
                }
            }
        }

        private bool LaunchAdmitted(CodexLaunchIntent intent)
        {
            lock (_sync) return !_retired && !_stopped && intent.Session == _session && _state.Scope is { Mode: RogNativeMenuCodec.CodexMode } scope
                && scope.Epoch == intent.Epoch && scope.Cookie == intent.Cookie && scope.Profile == intent.Profile
                && _source.PageRequested(scope) && RogNativeMenuCodec.SupportsDedicatedPage(scope)
                // Accept already validated presses across strictly decoded held/release reports. Sequence is monotonic
                // only inside this exact live scope; malformed/replayed/gapped input disarms it instead of advancing it.
                && _state.Sequence >= intent.Sequence;
        }

        private bool TaskBrowseAdmitted(CodexLaunchIntent intent, long requestRevision)
        {
            lock (_sync) return requestRevision == _source.RequestRevision && LaunchAdmitted(intent)
                && _state.Sequence == intent.Sequence && intent.NeutralSequence != 0
                && intent.Sequence == intent.NeutralSequence + 1
                && _source._time.GetUtcNow() - intent.ReceivedAt is var age
                && age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(750);
        }

        internal void Retire(string reason, bool disconnected)
        {
            Interlocked.Exchange(ref _outsideCapabilityRequestRevision, -1);
            NativeMenuContextEnded? ended = null;
            lock (_sync)
            {
                if (!_retired && _armed is { } scope) ended = new(_session, scope.Epoch, disconnected, reason);
                _state.Disarm(); _neutral = 0; _retired = true;
                Volatile.Write(ref _source._present, 0);
                _readerStop?.Cancel();
            }
            if (ended is not null) _source.Emit(ended);
            _source._codexLaunch?.Invalidate();
        }

        private bool Matches(RogNativeCapability capability)
        { lock (_sync) return _state.Matches(capability); }

        private async Task JoinReaderAsync()
        {
            _readerStop?.Cancel(); _input?.Dispose();
            if (_reader is { } reader) await reader.ConfigureAwait(false);
            if (_input is { } input)
            {
                try { await input.JoinAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _joinFailed = true; Volatile.Write(ref _source._present, 0);
                    Retire("underlying input read did not join", true); throw;
                }
            }
            _readerStop?.Dispose(); _readerStop = null; _reader = null; _input = null;
            while (_queue.TryDequeue(out _)) { }
        }

        private async Task LeaveAsync(string reason, CancellationToken ct, RogNativeCapability? observed = null, bool noWire = false)
        {
            Volatile.Write(ref _rearmIdle, false);
            Retire(reason, false);
            await JoinReaderAsync().ConfigureAwait(false);
            var old = _armed;
            _armed = null; _sent = null;
            if (noWire || _artworkRefused || RecoveryBlocked || old is not { } prior || _wireFault is not null || _disarmAttempted == (prior.Epoch, prior.Cookie)) return;
            RequireConnection();
            var now = observed ?? RogNativeMenuCodec.ParseCapability(await ExchangeAsync(
                RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false), 0x12, _transport);
            if (!RogNativeMenuCodec.IsAdmittedPage(now) || now.Mode != prior.Mode || now.Epoch != prior.Epoch || now.Cookie != prior.Cookie || now.Profile != prior.Profile || now.Transport != prior.Transport) return;
            _disarmAttempted = (prior.Epoch, prior.Cookie); // Set BEFORE write: an unknown outcome is never replayed.
            var disarmReply = await ExchangeAsync(RogNativeMenuCodec.ArmRequest(now, 0, false), false, ct).ConfigureAwait(false);
            try
            {
                var result = RogNativeMenuCodec.ParseCapability(disarmReply, 0x71, _transport);
                if (result.Status != 0 || result.Cookie != 0 || result.Epoch != now.Epoch + 1 || result.Mode != now.Mode || result.Profile != now.Profile || result.Transport != now.Transport)
                    throw new InvalidDataException("Native DISARM reply differs; no retry.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { LatchUnknown(ex, "unvalidated native DISARM mutation reply"); throw; }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            Enter();
            try
            {
                if (_stopped) return;
                _stopped = true;
                await LeaveAsync("owner stopping or handing off", ct).ConfigureAwait(false);
                Volatile.Write(ref _source._present, 0);
            }
            finally { Exit(); }
        }

        /// <summary>Joins input without wire I/O after a specifically classified fault; the poisoned connection is never reused.</summary>
        internal async Task RetireAfterRecoverableFaultAsync(CancellationToken ct)
        {
            Enter();
            try
            {
                if (_stopped) return;
                _stopped = true;
                await LeaveAsync("classified fault; owner retired without wire", ct, noWire: true).ConfigureAwait(false);
                Volatile.Write(ref _source._present, 0);
            }
            finally { Exit(); }
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                // An unjoined read must not permit a replacement native owner. Retain this failed generation until exit.
                if (!_joinFailed) Interlocked.CompareExchange(ref _source._actor, null, this);
            }
        }

        private async Task<byte[]> ExchangeAsync(byte[] packet, bool ack, CancellationToken ct)
        {
            if (_artworkRefused) throw new IOException("Native artwork owner was retired after a negative envelope; no further I/O.");
            if (_wireFault is not null) throw new IOException("Native connection outcome unknown; no further native I/O.", _wireFault);
            try
            {
                var reply = ack && packet[1] == 0x67 && packet[2] == 1
                    ? await _connection.ExchangeNativePixelAsync(packet, ct).ConfigureAwait(false)
                    : await _connection.ExchangeAsync(packet, ack, ct).ConfigureAwait(false);
                if (ack) RogWiredOledProtocol.RequireAck(reply, packet);
                return reply;
            }
            catch (RogNativeImageNackException) { throw; }
            catch (RogOledEnvelopeRefusedException ex) when (ack && packet[1] == 0x67 && packet[2] == 1)
            {
                // This says WHEN the envelope was received, not which command produced it. Only ephemeral pixel chunks
                // use this path; do not mark the frame sent, replay a chunk, or reinterpret an unknown settings mutation.
                _artworkRefused = true;
                Volatile.Write(ref _source._present, 0);
                Retire("negative envelope during native artwork; fresh owner required", false);
                throw new RogNativeArtworkUploadRefusedException(_key, _kind, _armed, ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LatchUnknown(ex, "unknown or failed control exchange");
                throw;
            }
        }

        // Only this standalone Step GET is recoverable. Leave/Stop, ARM replies, pixels and every other exchange keep the
        // existing unknown-outcome latch. The connection itself remains poisoned and must never be reused.
        private async Task<byte[]> ReadStepCapabilityAsync(CancellationToken ct)
        {
            if (_artworkRefused) throw new IOException("Native artwork owner was retired after a negative envelope; no further I/O.");
            if (_wireFault is not null) throw new IOException("Native connection outcome unknown; no further native I/O.", _wireFault);
            try { return await _connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false); }
            catch (RogOledTimeoutException ex)
            { throw new RogReadOnlyObservationTimeoutException(RogReadOnlyOperation.NativeStepCapability, _key, _kind, null, ex); }
            catch (RogOledUnexpectedReplyException ex)
            { throw new RogReadOnlyObservationUnexpectedReplyException(RogReadOnlyOperation.NativeStepCapability, _key, _kind, null, ex); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { LatchUnknown(ex, "unknown or failed control exchange"); throw; }
        }

        // This follows native pixel mutations. It MUST NOT use ReadStepCapabilityAsync's recoverable observation path.
        private async Task<string> RequireCodexFrameReadyAsync(RogNativeCapability expected, long requestRevision, CancellationToken ct)
        {
            static string Describe(string label, RogNativeCapability value) =>
                $"{label}=[mode={value.Mode}; profile={value.Profile}; transport={value.Transport}; epoch={value.Epoch}; cookie={value.Cookie:X16}; status={value.Status}; flags={value.Flags:X2}; ready={(value.Flags & 8) != 0}; rx={(value.Flags & 4) != 0}]";
            var detail = "operation=CodexFrameReady; pixelsAcknowledged=6656; " + Describe("armed", expected) + "; observed=unavailable";
            try
            {
                RequireConnection();
                var reply = await _connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false);
                var observed = RogNativeMenuCodec.ParseCapability(reply, 0x12, _transport);
                detail = "operation=CodexFrameReady; pixelsAcknowledged=6656; " + Describe("armed", expected) + "; " + Describe("observed", observed);
                if (_source._verboseCodexCommit) Serilog.Log.Information("Codex frame commit readiness: {Proof}", detail);
                // 0x02 means neutral, not READY. READY is0x08 and active RX is0x04; Status1 and2 are both legitimate here.
                if (observed.Mode != expected.Mode || observed.Profile != expected.Profile || observed.Transport != expected.Transport ||
                    observed.Epoch != expected.Epoch || observed.Cookie != expected.Cookie || observed.Status is not (1 or 2) ||
                    (observed.Flags & 1) == 0 || (observed.Flags & 8) == 0 || (observed.Flags & 4) != 0 ||
                    !RogNativeMenuCodec.IsAdmittedPage(observed) || !_source.PageRequested(observed) || _retired || _source.RequestRevision != requestRevision)
                    throw new InvalidDataException("Post-upload Codex scope or READY state differs; renewal was not sent.");
                return detail;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Even a read timeout here follows mutations: terminal owner retirement, never automatic observation recovery.
                var failure = new IOException(detail + "; terminalAfterPixelWrites; renewalNotSent; noRetry. " + ex.Message, ex);
                Serilog.Log.Warning("Codex frame commit readiness failed: {Proof}", failure.Message);
                LatchUnknown(failure, "Codex frame commit readiness failed"); throw failure;
            }
        }

        private void LatchUnknown(Exception failure, string reason)
        {
            _wireFault ??= failure; Volatile.Write(ref _source._present, 0);
            Retire(reason, true);
        }

        private void RequireConnection()
        {
            if (!_connection.SameDevice() || _connection.DeviceKey != _key || _connection.FirmwareHeader != _firmware
                || _connection.ConnectionKind != _kind || _connection.PixelPayloadCapacity != _capacity)
            {
                PhysicalScopeLost = true;
                Retire("physical connection scope changed", true);
                throw new InvalidDataException("Native connection identity changed; no scope replay.");
            }
        }

        private void Enter()
        { if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("Native calls must use the one existing keyboard actor."); }
        private void Exit() => Volatile.Write(ref _busy, 0);
        private sealed record Received(RogNativeNoticeKind Kind, RogNativeNotice Notice, DateTimeOffset At);
    }
}

/// <summary>An exact negative RF envelope arrived inside native67/1; its origin is not inferred and the old owner is unusable.</summary>
internal sealed class RogNativeArtworkUploadRefusedException(string deviceKey, string connectionKind, RogNativeCapability? armed, Exception inner)
    : IOException("operation=NativeArtworkChunk; " + Describe("expectedArmed", armed)
        + "; negative envelope during ephemeral upload; old owner must be replaced. " + inner.Message, inner)
{
    public string DeviceKey { get; } = deviceKey;
    public string ConnectionKind { get; } = connectionKind;
    internal static string Describe(string label, RogNativeCapability? capability) => capability is { } c
        ? $"{label}=[epoch={c.Epoch}; profile={c.Profile}; cookie={c.Cookie:X16}; flags={c.Flags:X2}; mode={c.Mode}; status={c.Status}]"
        : label + "=none";
}
