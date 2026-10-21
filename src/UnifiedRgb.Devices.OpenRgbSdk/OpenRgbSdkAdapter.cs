using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.OpenRgb;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Devices.OpenRgbSdk;

public sealed class OpenRgbSdkOptions
{
    public string ClientName { get; set; } = "UnifiedRgb";

    /// <summary>Health check / rediscovery period; zero disables the background timer (tests drive <see cref="OpenRgbSdkAdapter.RediscoverAsync"/>).</summary>
    public TimeSpan RediscoverInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to keep retrying the SDK connection after (re)launching OpenRGB.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public OpenRgbSupervisorOptions Supervisor { get; set; } = new();
}

/// <summary>
/// ASUS Aura motherboard and ENE DRAM through our supervised headless OpenRGB. Only <see cref="DetectorAllowList"/>
/// controllers are exposed; opening forces the controller's Direct mode (session-only UpdateMode); each zone frame is one
/// UpdateZoneLeds through FrameValidator → RateLimiter(60/s) → OpenRgbPacketFirewall. Every 2 s the connection is checked:
/// a dead process is relaunched, a lost connection is re-established and the device list is re-read, with ids kept stable.
/// </summary>
public sealed partial class OpenRgbSdkAdapter : IDeviceAdapter, IActiveDiscovery, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ITransport>> _connect;
    private readonly OpenRgbProcessSupervisor? _supervisor;
    private readonly OpenRgbSdkOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _linkGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<ControllerId, Known> _known = [];
    private readonly Dictionary<ControllerId, Session> _open = [];
    private OpenRgbSdkClient? _client;
    private int _generation;
    private volatile bool _broken;
    private Timer? _timer;
    private Task? _tickTask;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private Task<ITransport>? _pendingConnection;
    private int _rawCount = -1;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _disposed;

    public OpenRgbSdkAdapter(
        Func<CancellationToken, Task<ITransport>> connect,
        OpenRgbSdkOptions? options = null,
        OpenRgbProcessSupervisor? supervisor = null,
        ILogger? logger = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _options = options ?? new OpenRgbSdkOptions();
        _supervisor = supervisor;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((period, token) => Task.Delay(period, _time, token));
        _lifetimeToken = _lifetime.Token;
        if (_options.ConnectTimeout < TimeSpan.Zero || _options.ConnectTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public event Action<HotplugEvent>? Hotplug;

    public string Vendor => "OpenRGB";

    public DeviceClass Class => DeviceClass.OpenRgb;

    /// <summary>Number of (re)connections made; for diagnostics and tests.</summary>
    public int Connections { get; private set; }

    public async Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        ct = linked.Token;
        await _linkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _supervisor?.EnsureRunning();
            if (_client is null || _broken)
            {
                await ReconnectAsync(ct).ConfigureAwait(false);
            }

            await EnumerateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _linkGate.Release();
        }

        lock (_gate)
        {
            if (!_disposed && _options.RediscoverInterval > TimeSpan.Zero && _timer is null)
                _timer = new Timer(_ => StartTick(), null, _options.RediscoverInterval, _options.RediscoverInterval);
        }

        lock (_gate)
        {
            return _known.Values.Where(k => k.Index >= 0).Select(k => k.Descriptor).ToArray();
        }
    }

    public async Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        Known k;
        lock (_gate)
        {
            k = _known.TryGetValue(id, out var x) && x.Index >= 0 ? x : throw new ArgumentException($"Unknown OpenRGB controller {id}", nameof(id));
        }

        var direct = FindDirect(k.Data) ?? throw new NotSupportedException($"{id} has no Direct mode; it is not opened.");
        await ApplyDirectAsync(k, direct, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _open[id] = new Session(id);
        }
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        Session s;
        Known k;
        lock (_gate)
        {
            s = _open.TryGetValue(id, out var x) ? x : throw new InvalidOperationException($"{id} is not open");
            k = _known[id];
        }

        if ((uint)zone.Value >= (uint)k.ZoneMap.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(zone));
        }

        if (colors.Length != k.Descriptor.Zones[zone.Value].LedCount)
        {
            throw new ArgumentException($"Zone {zone} expects {k.Descriptor.Zones[zone.Value].LedCount} colors.", nameof(colors));
        }

        var chain = s.ChainFor(this, k);
        var len = OpenRgbProtocol.WriteUpdateZoneLeds(s.Buffer, (uint)k.Index, (uint)k.ZoneMap[zone.Value], colors);
        try
        {
            var vt = chain.SendAsync(s.Buffer.AsMemory(0, len), CancellationToken.None);
            if (!vt.IsCompletedSuccessfully)
            {
                vt.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ObjectDisposedException)
        {
            _broken = true;
            throw;
        }
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) =>
        throw new NotSupportedException("OpenRGB controllers are driven in Direct mode only.");

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    public Task CloseAsync(ControllerId id)
    {
        lock (_gate)
        {
            _open.Remove(id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One health/rediscovery pass: relaunch a dead process, reconnect a broken link, re-read the device list when it
    /// changed, re-assert Direct on open controllers after a reconnect, and raise Hotplug for ids that vanished or returned.
    /// </summary>
    public async Task RediscoverAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        ct = linked.Token;
        await _linkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var relaunched = _supervisor?.EnsureRunning() ?? false;
            var reconnect = relaunched || _broken || _client is null;
            if (!reconnect)
            {
                try
                {
                    var count = await _client!.GetControllerCountAsync(ct).ConfigureAwait(false);
                    if (count == _rawCount && !_client.DeviceListChanged)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidDataException or ObjectDisposedException)
                {
                    reconnect = true;
                }
            }

            if (reconnect)
            {
                await ReconnectAsync(ct).ConfigureAwait(false);
            }

            var arrived = await EnumerateAsync(ct).ConfigureAwait(false);
            if (reconnect || arrived > 0)
            {
                await ReassertDirectAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _linkGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Timer? timer;
        lock (_gate) { if (_disposed) return; _disposed = true; timer = _timer; _timer = null; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        await _linkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _linkGate.Release();
        }

        _supervisor?.Dispose();
        _linkGate.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Stable id from what the controller is and where it sits, independent of OpenRGB's enumeration order.</summary>
    public static string StableId(OpenRgbControllerData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var where = string.IsNullOrWhiteSpace(d.Location) ? d.Serial : d.Location;
        return $"openrgb:{Slug(d.Name)}@{Slug(where)}";
    }

    public static ZoneLayout MapZone(OpenRgbZone z)
    {
        ArgumentNullException.ThrowIfNull(z);
        var n = checked((int)z.LedsCount);
        return z.Type switch
        {
            2 when z.MatrixWidth * z.MatrixHeight == z.LedsCount && n > 0 => ZoneLayout.Matrix((int)z.MatrixWidth, (int)z.MatrixHeight),
            0 when n == 1 => ZoneLayout.Point,
            _ => ZoneLayout.Linear(n),
        };
    }

    public static (int Index, OpenRgbMode Mode)? FindDirect(OpenRgbControllerData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        for (var i = 0; i < d.Modes.Count; i++)
        {
            if (string.Equals(d.Modes[i].Name, "Direct", StringComparison.OrdinalIgnoreCase))
            {
                return (i, d.Modes[i]);
            }
        }

        return null;
    }

    /// <summary>Firewall (inside the client) → FrameValidator → RateLimiter(60/s), largest frame = biggest zone.</summary>
    public static ITransport BuildChain(ITransport firewalled, IDeviceState state, int maxZoneLeds)
    {
        var rules = new FrameValidationRules
        {
            MinLength = OpenRgbProtocol.UpdateZoneLedsLength(1),
            MaxLength = OpenRgbProtocol.UpdateZoneLedsLength(Math.Max(1, maxZoneLeds)),
        };
        // the SDK firewall sits inside the client transport, so the tap here still only sees frames it allowed
        var tapped = new PacketTap(firewalled, "OpenRgb", f => f.Length >= OpenRgbHeader.Size ? (int)OpenRgbHeader.Parse(f.Span).PacketId : -1);
        return new RateLimiter(new FrameValidator(tapped, rules, state), RateLimits.OpenRgbSdkFps);
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return sb.ToString();
    }

    private void StartTick()
    {
        lock (_gate)
        {
            if (_disposed || _tickTask is { IsCompleted: false }) return;
            _tickTask = TickAsync();
        }
    }

    private async Task TickAsync()
    {
        try
        {
            await RediscoverAsync(_lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
#pragma warning disable CA1031 // a failed pass is retried on the next tick
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _broken = true;
            LogRediscoverFailed(ex);
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = null;
        if (_pendingConnection is { IsCompleted: false })
            throw new IOException("A previous timed-out OpenRGB connection is still pending; a second connection is refused.");
        var start = _time.GetTimestamp();
        Exception? last = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = _options.ConnectTimeout - _time.GetElapsedTime(start);
            if (last is not null && remaining <= TimeSpan.Zero)
                throw new IOException("OpenRGB reconnect deadline elapsed.", last);
            var budget = _options.ConnectTimeout == TimeSpan.Zero ? TimeSpan.FromSeconds(10) : remaining;
            OpenRgbSdkClient? candidate = null;
            try
            {
                using var deadline = new CancellationTokenSource(budget, _time);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                var pending = _pendingConnection = _connect(attempt.Token);
                ITransport transport;
                try { transport = await pending.WaitAsync(budget, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                { _ = DisposeLateConnectionAsync(pending); throw new TimeoutException("OpenRGB connection deadline elapsed.", ex); }
                catch { _ = DisposeLateConnectionAsync(pending); throw; }
                _pendingConnection = null;
                candidate = new OpenRgbSdkClient(transport, _logger);
                var handshakeBudget = _options.ConnectTimeout == TimeSpan.Zero ? budget : _options.ConnectTimeout - _time.GetElapsedTime(start);
                if (handshakeBudget <= TimeSpan.Zero) throw new TimeoutException("OpenRGB handshake deadline elapsed.");
                try { await candidate.HandshakeAsync(_options.ClientName, attempt.Token).WaitAsync(handshakeBudget, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new TimeoutException("OpenRGB handshake deadline elapsed.", ex); }
                _client = candidate;
                candidate = null;
                break;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ObjectDisposedException
                && !ct.IsCancellationRequested && _time.GetElapsedTime(start) < _options.ConnectTimeout)
            {
                last = ex;
                remaining = _options.ConnectTimeout - _time.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero) await _delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            finally { candidate?.Dispose(); }
        }

        _broken = false;
        Connections++;
        Interlocked.Increment(ref _generation);
        LogConnected(_client.Protocol);
    }

    private static async Task DisposeLateConnectionAsync(Task<ITransport> pending)
    {
        try { (await pending.ConfigureAwait(false) as IDisposable)?.Dispose(); }
#pragma warning disable CA1031 // observe a timed-out connection and release any eventual transport, never retry its writes
        catch (Exception) { }
#pragma warning restore CA1031
    }

    /// <summary>Re-reads the device list; returns how many known controllers came back.</summary>
    private async Task<int> EnumerateAsync(CancellationToken ct)
    {
        var all = await _client!.GetAllControllersAsync(ct).ConfigureAwait(false);
        var seen = new Dictionary<ControllerId, Known>();
        for (var i = 0; i < all.Count; i++)
        {
            var d = all[i];
            if (!DetectorAllowList.IsAllowed(d))
            {
                LogIgnored(d.Name, d.Type);
                continue;
            }

            var baseId = StableId(d);
            var id = new ControllerId(baseId);
            for (var n = 2; seen.ContainsKey(id); n++)
            {
                id = new ControllerId($"{baseId}#{n}");
            }

            seen[id] = Build(id, i, d);
        }

        var events = new List<HotplugEvent>();
        lock (_gate)
        {
            foreach (var (id, k) in _known)
            {
                if (k.Index >= 0 && !seen.ContainsKey(id))
                {
                    k.Index = -1;
                    k.State.IsOnline = false;
                    events.Add(new HotplugEvent(id, HotplugKind.Removed));
                }
            }

            foreach (var (id, fresh) in seen)
            {
                if (_known.TryGetValue(id, out var k))
                {
                    var wasGone = k.Index < 0;
                    k.Update(fresh);
                    k.State.IsOnline = true;
                    if (wasGone)
                    {
                        events.Add(new HotplugEvent(id, HotplugKind.Arrived));
                    }
                }
                else
                {
                    _known[id] = fresh;
                }
            }
        }

        _rawCount = all.Count;
        foreach (var e in events)
        {
            Hotplug?.Invoke(e);
        }

        return events.Count(e => e.Kind == HotplugKind.Arrived);
    }

    private async Task ReassertDirectAsync(CancellationToken ct)
    {
        List<Known> open;
        lock (_gate)
        {
            open = _open.Keys.Select(id => _known[id]).Where(k => k.Index >= 0).ToList();
        }

        foreach (var k in open)
        {
            if (FindDirect(k.Data) is { } direct)
            {
                await ApplyDirectAsync(k, direct, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyDirectAsync(Known k, (int Index, OpenRgbMode Mode) direct, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("OpenRGB is not connected.");
        await client.UpdateModeAsync(k.Index, direct.Index, direct.Mode, ct).ConfigureAwait(false);
    }

    private static Known Build(ControllerId id, int index, OpenRgbControllerData d)
    {
        var zones = new List<ZoneDescriptor>();
        var map = new List<int>();
        for (var z = 0; z < d.Zones.Count; z++)
        {
            if (d.Zones[z].LedsCount == 0)
            {
                continue;
            }

            zones.Add(new ZoneDescriptor(new ZoneId(zones.Count), d.Zones[z].Name, MapZone(d.Zones[z])));
            map.Add(z);
        }

        var type = Enum.IsDefined((DeviceType)d.Type) ? (DeviceType)d.Type : DeviceType.Unknown;
        var descriptor = new ControllerDescriptor(
            id, d.Vendor, d.Name, type, DeviceClass.OpenRgb, d.Serial, zones, Capabilities.DirectOnly((int)RateLimits.OpenRgbSdkFps));
        return new Known(descriptor, index, d, map.ToArray());
    }

    private sealed class Known(ControllerDescriptor descriptor, int index, OpenRgbControllerData data, int[] zoneMap)
    {
        public ControllerDescriptor Descriptor { get; private set; } = descriptor;

        public int Index { get; set; } = index;

        public OpenRgbControllerData Data { get; private set; } = data;

        public int[] ZoneMap { get; private set; } = zoneMap;

        public DeviceState State { get; } = new();

        public void Update(Known fresh)
        {
            Descriptor = fresh.Descriptor;
            Index = fresh.Index;
            Data = fresh.Data;
            ZoneMap = fresh.ZoneMap;
        }
    }

    private sealed class Session(ControllerId id)
    {
        private ITransport? _chain;
        private int _generation = -1;

        public ControllerId Id { get; } = id;

        public byte[] Buffer { get; private set; } = [];

        public ITransport ChainFor(OpenRgbSdkAdapter owner, Known k)
        {
            var gen = Volatile.Read(ref owner._generation);
            if (_chain is null || gen != _generation)
            {
                var client = owner._client ?? throw new InvalidOperationException("OpenRGB is not connected.");
                var max = k.Descriptor.Zones.Count == 0 ? 1 : k.Descriptor.Zones.Max(z => z.LedCount);
                _chain = BuildChain(client.Transport, k.State, max);
                Buffer = new byte[OpenRgbProtocol.UpdateZoneLedsLength(max)];
                _generation = gen;
            }

            return _chain;
        }
    }

    [LoggerMessage(EventId = 4010, Level = LogLevel.Information, Message = "Connected to headless OpenRGB (protocol {Protocol})")]
    private partial void LogConnected(uint protocol);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Information, Message = "Ignoring OpenRGB controller '{Name}' (type {Type}): not on the detector allow-list")]
    private partial void LogIgnored(string name, int type);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning, Message = "OpenRGB rediscovery failed; retrying")]
    private partial void LogRediscoverFailed(Exception ex);
}

public sealed class OpenRgbSdkAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.OpenRgb;

    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var options = sp.GetService(typeof(OpenRgbSdkOptions)) as OpenRgbSdkOptions ?? new OpenRgbSdkOptions();
        var loggers = sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var supervisor = new OpenRgbProcessSupervisor(
            options.Supervisor,
            new SystemProcessLauncher(),
            sp.GetService(typeof(ProcessScanner)) as ProcessScanner,
            loggers?.CreateLogger<OpenRgbProcessSupervisor>());
        var endpoint = new IPEndPoint(IPAddress.Loopback, options.Supervisor.Port);
        return new OpenRgbSdkAdapter(
            async ct => await TcpSdkTransport.ConnectAsync(endpoint, ct).ConfigureAwait(false),
            options,
            supervisor,
            loggers?.CreateLogger<OpenRgbSdkAdapter>());
    }
}
