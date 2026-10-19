using System.Diagnostics;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>A wireless device bound to the master (sender) dongle, addressed by its RF channel and rx slot.</summary>
public sealed record RfDevice(RfMac Mac, string Name, PacketAssembler Assembler, int LedCount, UsbDeviceInfo Sender, RfAddress Address, byte SlaveIndex);

/// <summary>
/// One opened RF sender dongle (<c>MasterDevice.RFSender</c>): the shared firewalled chain every relay frame goes through, plus
/// the keep-alive duties of <c>MasterDevice.Run</c>'s 1 s branch (USB 17 master query, RF 20 ClockSync broadcast). Sends are
/// serialised so the four chunks of a packet are never interleaved, and a keep-alive never runs inside a transaction that
/// holds <see cref="UploadGate"/> (a loop upload's headers + data, SaveConfig): like <c>MasterDevice.Run</c>, which awaits
/// <c>SyncRgbData</c> after its query/ClockSync branch, a whole upload stays contiguous on the wire.
/// </summary>
public sealed class RfSender : IDisposable
{
    public static readonly TimeSpan KeepAlivePeriod = TimeSpan.FromSeconds(1);

    private readonly ITransport _raw;
    private readonly ITransport _chain;
    private readonly byte _masterChannel;
    private readonly Func<DateTime> _now;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<long> _timestamp;
    private readonly SenderClock _clock = new(DeviceClockDomain.Next(ClockKind));
    private readonly object _gate = new();
    private readonly Func<TimeSpan> _elapsed;
    private readonly IDeviceState _state;
    private TimeSpan _lastKeepAlive = -KeepAlivePeriod; // due immediately; MinValue would overflow the subtraction. Written under UploadGate
    private uint _lastEffectIndex;
    private int _closing;
    private bool _disposed;

    /// <summary>The master counter of USB 17 replies: <c>time_tmos</c>, 0.625 ms per tick (<c>SysClock = time_tmos × 0.625</c>).</summary>
    public const double ClockTicksPerSecond = 1600;

    /// <summary><see cref="DeviceClockDomain.Kind"/> of a sender's master counter.</summary>
    public const string ClockKind = "lianli-rf-master";

    /// <param name="elapsed">Monotonic time for the keep-alive period (default: a stopwatch started here); a test seam.</param>
    public RfSender(UsbDeviceInfo info, ITransport raw, IDeviceState state, byte masterChannel, Func<DateTime>? now = null, Action<TimeSpan>? sleep = null,
        Func<long>? timestamp = null, Func<TimeSpan>? elapsed = null)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _chain = BuildChain(raw, state);
        _masterChannel = masterChannel;
        _now = now ?? (() => DateTime.Now);
        _sleep = sleep ?? Thread.Sleep;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        if (elapsed is null)
        {
            var epoch = Stopwatch.StartNew();
            elapsed = () => epoch.Elapsed;
        }

        _elapsed = elapsed;
    }

    public UsbDeviceInfo Info { get; }

    public MasterInfo? Master { get; private set; }

    /// <summary>
    /// NATIVE-RF-CLOCK: the master counter of this sender instance, from the USB 17 queries it already makes (open and the 1 s
    /// keep-alive); reading it sends nothing. Its domain is fresh per instance, so a reopened or reconnected sender never
    /// continues an old one; after <see cref="Dispose"/> it has no sample.
    /// </summary>
    public IDeviceClock Clock => _clock;

    public int RefCount { get; set; }

    /// <summary>The RF relay packet budget (ARCHITECTURE-D2 §4): 100 packets/s, burst 256, shared by every relay on this sender.</summary>
    public TokenBucket Packets { get; } = new(RateLimits.RfRelayPacketsPerSecond, RateLimits.RfRelayPacketBurst, startFull: true);

    /// <summary>
    /// Serialises loop uploads (and SaveConfig) on this sender; a queued upload waits here and may be cancelled (D2 §4).
    /// <see cref="Tick"/> only ever tries it without waiting, so a keep-alive never lands between an upload's packets.
    /// </summary>
    public SemaphoreSlim UploadGate { get; } = new(1, 1);

    /// <summary>Completed loop uploads through this sender (RF 21 is allowed only after one).</summary>
    public long CompletedUploads { get; private set; }

    /// <summary>OpcodeFirewall (with relay sequencing) → FrameValidator (64-byte frames only). The packet budget is applied in <see cref="Relay"/>.</summary>
    public static ITransport BuildChain(ITransport raw, IDeviceState state)
    {
        var rules = new FrameValidationRules
        {
            MinLength = LianLiWire.ShortFrame,
            MaxLength = LianLiWire.ShortFrame,
            AllowedLengths = new HashSet<int> { LianLiWire.ShortFrame },
        };
        var tapped = new PacketTap(raw, "LianLi", f => OpcodeLayout.LianLi.OpcodeOf(f.Span));
        return new FrameValidator(new OpcodeFirewall(tapped, OpcodeLayout.LianLi), rules, state);
    }

    /// <summary>
    /// USB 17 (<c>QuerryMasterMac</c>): the dongle's MAC, which every RF packet carries as master MAC, and its clock. A
    /// validated reply also publishes a <see cref="Clock"/> sample stamped when the reply was read (the query's send time is
    /// kept as its latency bound); a missing, malformed or zero-clock reply throws before anything is published, so the last
    /// sample keeps its old stamp and ages.
    /// </summary>
    public MasterInfo ReadMaster()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sent = _timestamp();
            Send(UsbFrames.GetMac(_masterChannel));
            var reply = new byte[LianLiWire.ShortFrame];
            var n = Wait(_raw.ReadAsync(reply, CancellationToken.None));
            var read = _timestamp();
            var info = UsbFrames.ParseMasterInfo(reply.AsSpan(0, n));
            Master = info;
            _clock.Publish(new DeviceClockSample(_clock.Domain, info.ClockTicks, read, ClockTicksPerSecond, sent));
            return info;
        }
    }

    /// <summary>One 240-byte packet as four relay frames, after taking one token of the packet budget.</summary>
    public void Relay(RfAddress to, ReadOnlySpan<byte> packet)
    {
        var frames = RfRelay.Frames(to, packet);
        while (!Packets.TryTake(out var wait))
        {
            _sleep(TimeSpan.FromSeconds(Math.Max(wait, 0.001)));
        }

        lock (_gate)
        {
            foreach (var f in frames)
            {
                Send(f);
            }
        }
    }

    /// <summary>
    /// <c>SyncRgbData</c> with a fresh effect index (ms since midnight, kept strictly increasing on this sender).
    /// </summary>
    public int Upload(RfDevice device, RgbAnimation animation, ReadOnlySpan<byte> compressed)
    {
        uint index;
        lock (_gate)
        {
            var now = RfPackets.TimeStamp(_now());
            _lastEffectIndex = now > _lastEffectIndex ? now : _lastEffectIndex + 1;
            index = _lastEffectIndex;
        }

        return Upload(device, animation, compressed, index, null);
    }

    /// <summary>
    /// <c>SyncRgbData</c>: the loop header is checked by <see cref="FrameValidator.EnsureLoopHeader"/> (D2 §7), then the header
    /// packet is sent <see cref="RfPackets.UploadHeaderSends"/> times 20 ms apart, then the data packets. All chains of one
    /// scene change share <paramref name="effectIndex"/>. <paramref name="started"/> runs just before the first header send.
    /// Returns the number of distinct packets.
    /// </summary>
    public int Upload(RfDevice device, RgbAnimation animation, ReadOnlySpan<byte> compressed, uint effectIndex, Action? started)
    {
        ArgumentNullException.ThrowIfNull(device);
        var master = RequireMaster();
        var packets = RfPackets.RgbUpload(device.Mac, master, effectIndex, animation, compressed);
        FrameValidator.EnsureLoopHeader(
            RfPackets.HeaderOf(packets),
            new LoopHeaderRules(device.LedCount, LianLiWire.MaxCompressedAnimation, LianLiWire.UploadChunk),
            _state);
        started?.Invoke();
        for (var i = 0; i < RfPackets.UploadHeaderSends; i++)
        {
            if (i > 0)
            {
                _sleep(RfPackets.UploadHeaderRepeatDelay);
            }

            Relay(device.Address, packets[0]);
        }

        foreach (var p in packets.Skip(1))
        {
            Relay(device.Address, p);
        }

        lock (_gate)
        {
            CompletedUploads++;
        }

        return packets.Count;
    }

    /// <summary>RF 21 broadcast on the master channel (one send, as <c>RFController.SaveConfig</c>). Gated by the adapter.</summary>
    public void SaveConfig() => Relay(RfAddress.BroadcastOn(_masterChannel), RfPackets.SaveConfig(RequireMaster()));

    /// <summary><c>SyncControlInfo</c> command to one device.</summary>
    public void Control(RfDevice device, RfCmd cmd, byte cmdSeq, byte value)
    {
        ArgumentNullException.ThrowIfNull(device);
        Relay(device.Address, RfPackets.Control(cmd, device.Mac, RequireMaster(), device.Address, device.SlaveIndex, cmdSeq, value));
    }

    /// <summary>
    /// Sends whatever keep-alive is due (every second: master query, then ClockSync). A failed query keeps the last known
    /// master, as <c>QuerryMasterMac</c> does. Called from the adapter's 250 ms timer and safe to call more often.
    /// </summary>
    /// <remarks>
    /// Transaction isolation: the keep-alive takes <see cref="UploadGate"/> WITHOUT waiting. While an upload, SaveConfig or
    /// another Tick holds it (including a call from inside an upload's own callback, which must never re-enter the gate) this
    /// returns at once: nothing is sent, the due time is not advanced and the clock sample is not refreshed, so it keeps aging
    /// and the next timer callback retries. After acquisition the due time is checked again, so concurrent ticks send one
    /// heartbeat. This orders transactions only; it is not a receiver ACK.
    /// </remarks>
    public void Tick()
    {
        if (Volatile.Read(ref _closing) != 0 || _elapsed() - _lastKeepAlive < KeepAlivePeriod || !UploadGate.Wait(0))
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _closing) != 0) return;
            var now = _elapsed();
            if (now - _lastKeepAlive < KeepAlivePeriod)
            {
                return; // another tick sent this period's heartbeat while we were acquiring
            }

            _lastKeepAlive = now;
            try
            {
                ReadMaster();
            }
            catch (InvalidDataException)
            {
                // no or malformed reply: keep the previous master info
            }

            if (Master is { } m)
            {
                Relay(RfAddress.BroadcastOn(_masterChannel), RfPackets.ClockSync(m.Mac, _now()));
            }
        }
        finally
        {
            UploadGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _clock.Close();
        UploadGate.Wait();
        try
        {
            lock (_gate)
            {
                _disposed = true;
                (_raw as IDisposable)?.Dispose();
            }
        }
        finally { UploadGate.Release(); }
        // Retired queued callers must be able to acquire/release and observe _disposed. No WaitHandle is allocated;
        // disposing this managed semaphore while a queued upload/timer still references it is itself a race.
    }

    /// <summary>A sender's master counter: one immutable sample swapped atomically; closed for good on Dispose.</summary>
    private sealed class SenderClock(DeviceClockDomain domain) : IDeviceClock
    {
        private readonly object _writeGate = new(); // writers only: a publish racing Close never resurrects a closed clock
        private DeviceClockSample? _latest;
        private bool _closed;

        public DeviceClockDomain Domain { get; } = domain;

        public DeviceClockSample? Latest => Volatile.Read(ref _latest);

        public void Publish(DeviceClockSample sample)
        {
            lock (_writeGate)
            {
                if (!_closed)
                {
                    Volatile.Write(ref _latest, sample);
                }
            }
        }

        public void Close()
        {
            lock (_writeGate)
            {
                _closed = true;
                Volatile.Write(ref _latest, null);
            }
        }
    }

    private static T Wait<T>(ValueTask<T> vt) => vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();

    private RfMac RequireMaster() => (Master ?? ReadMaster()).Mac;

    private void Send(byte[] frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vt = _chain.SendAsync(frame, CancellationToken.None);
        if (!vt.IsCompletedSuccessfully)
        {
            vt.AsTask().GetAwaiter().GetResult();
        }
    }
}
