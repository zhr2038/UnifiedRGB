using System.Diagnostics;
using System.IO.Pipes;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Ipc;

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>
/// Keeps one JSON-RPC session to the Host pipe alive (ARCHITECTURE §9): connect, attach the IRgbService proxy, forward its
/// notifications through client-level events (so view models subscribe once, across reconnects), and on loss reconnect
/// with capped exponential backoff (250 ms → 5 s). Never throws to the UI.
/// </summary>
public sealed class PipeClient : IHostConnection, IAsyncDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly PipeTrust _trust;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile IRgbService? _service;
    private volatile IScreenService? _screens;
    private volatile IScenePackageService? _packages;
    private volatile ISceneLibraryService? _library;
    private volatile IScreenDeviceSettingsService? _screenSettings;
    private volatile IScreenPerformanceService? _screenPerformance;
    private volatile IScreenVideoService? _screenVideo;
    private ConnectionState _state = ConnectionState.Disconnected;
    private volatile ConnectionDiagnostic? _diagnostic;
    private string? _uiFault;
    private int _disposed;

    public PipeClient(string pipeName, PipeTrust trust = PipeTrust.ElevatedOwner)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? throw new ArgumentException("Pipe name required.", nameof(pipeName)) : pipeName;
        _trust = trust;
    }

    public event EventHandler<ConnectionState>? StateChanged;

    public event EventHandler<IReadOnlyList<ControllerDto>>? ControllersChanged;

    public event EventHandler<StatusDto>? StatusChanged;

    public event EventHandler<IReadOnlyList<TelemetryDto>>? TelemetryTick;

    public ConnectionState State => _state;

    /// <summary>
    /// Why the last connection attempt failed or the last session ended (null after a successful connect). Bounded,
    /// sanitized operator text; never contains RPC payloads (see <see cref="PipeClientDiagnostics"/>).
    /// </summary>
    public string? LastError => _diagnostic?.Text;

    /// <summary>Category matching <see cref="LastError"/>; <see cref="HostConnectionFailure.None"/> after a successful connect.</summary>
    public HostConnectionFailure LastFailure => _diagnostic?.Kind ?? HostConnectionFailure.None;

    /// <summary>True once <see cref="BlockForUiFault"/> was called; Require* accessors then refuse every Host operation.</summary>
    public bool IsBlockedForUiFault => Volatile.Read(ref _uiFault) is not null;

    /// <summary>The live proxy, or null while disconnected.</summary>
    public IRgbService? Service => IsBlockedForUiFault ? null : _service;

    /// <summary>Number of successful connections so far (a Host restart increments it).</summary>
    public int Connections { get; private set; }

    /// <summary>Delay before reconnect attempt <paramref name="attempt"/> (0-based): 250 ms × 2^n, capped at 5 s.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var ms = 250.0 * Math.Pow(2, Math.Min(attempt, 10));
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxBackoff.TotalMilliseconds));
    }

    /// <summary>
    /// The proxy, or <see cref="HostNotConnectedException"/> (an InvalidOperationException) while disconnected, or
    /// <see cref="HostAccessBlockedException"/> after <see cref="BlockForUiFault"/>.
    /// </summary>
    public IRgbService Require() => Gate(_service);
    public IScreenDeviceSettingsService RequireScreenSettings() => Gate(_screenSettings);
    public IScreenService RequireScreens() => Gate(_screens);
    public IScreenPerformanceService RequireScreenPerformance() => Gate(_screenPerformance);
    public IScreenVideoService RequireScreenVideo() => Gate(_screenVideo);
    public IScenePackageService RequirePackages() => Gate(_packages);
    public ISceneLibraryService RequireLibrary() => Gate(_library);

    /// <summary>
    /// Local, one-way gate used when the UI may hold inconsistent state: every later Require* call throws
    /// <see cref="HostAccessBlockedException"/>. Closes this UI session so cached proxies also cannot issue later requests;
    /// the Host process and its active scene remain independent. Requests already admitted by the Host may still finish.
    /// The first reason wins; this client never reconnects after an unsafe UI fault.
    /// </summary>
    public void BlockForUiFault(string reason)
    {
        if (Interlocked.CompareExchange(ref _uiFault, string.IsNullOrWhiteSpace(reason) ? new HostAccessBlockedException().Message : reason, null) is null)
            _ = StopForUiFaultAsync();
    }

    private async Task StopForUiFaultAsync()
    {
        try { await _stop.CancelAsync().ConfigureAwait(false); }
#pragma warning disable CA1031 // one-way fault barrier: observe cancellation callback errors without re-entering the dispatcher
        catch (Exception ex) { Trace.TraceError("UI RPC stop failed: {0}", ex.GetType().Name); }
#pragma warning restore CA1031
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    public Task<bool> WaitConnectedAsync(TimeSpan timeout) => WaitConnectedAsync(timeout, CancellationToken.None);

    /// <summary>
    /// Waits (monotonic clock) until a session is up. Returns false on timeout or once the client is disposed;
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without a deadline. Caller cancellation throws OperationCanceledException.
    /// </summary>
    public async Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var infinite = timeout == Timeout.InfiniteTimeSpan;
        if (!infinite)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        }

        var poll = TimeSpan.FromMilliseconds(50);
        var clock = Stopwatch.StartNew();
        while (_service is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || IsBlockedForUiFault)
            {
                return false;
            }

            var remaining = infinite ? poll : timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(remaining < poll ? remaining : poll, cancellationToken).ConfigureAwait(false);
        }

        return !IsBlockedForUiFault;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
#pragma warning disable VSTHRD003 // our own loop task
            await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var phase = PipeConnectPhase.Connect;
            string? disconnectReason = null;
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
                phase = PipeConnectPhase.Verify;
                PipeServerIdentity.Verify(pipe, _trust);
                phase = PipeConnectPhase.Session;
                using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, CreateFormatter()));
                rpc.Disconnected += (_, e) => disconnectReason = e.Reason.ToString();
                var proxy = rpc.Attach<IRgbService>();
                var screens = rpc.Attach<IScreenService>();
                var packages = rpc.Attach<IScenePackageService>();
                var library = rpc.Attach<ISceneLibraryService>();
                var screenSettings = rpc.Attach<IScreenDeviceSettingsService>();
                var screenPerformance = rpc.Attach<IScreenPerformanceService>();
                proxy.ControllersChanged += (s, e) => ControllersChanged?.Invoke(this, e);
                proxy.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
                proxy.TelemetryTick += (s, e) => TelemetryTick?.Invoke(this, e);
                rpc.StartListening();
                _service = proxy;
                _screens = screens;
                _packages = packages;
                _library = library;
                _screenSettings = screenSettings;
                _screenPerformance = screenPerformance;
                _screenVideo = rpc.Attach<IScreenVideoService>();
                Connections++;
                attempt = 0;
                _diagnostic = null;
                SetState(ConnectionState.Connected);
                using (ct.Register(rpc.Dispose))
                {
                    await rpc.Completion.ConfigureAwait(false);
                }

                _diagnostic = ct.IsCancellationRequested ? PipeClientDiagnostics.Stopped() : PipeClientDiagnostics.SessionEnded(disconnectReason, null);
            }
#pragma warning disable CA1031 // any connect/session failure is recorded, then reconnect with backoff (or stop)
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _diagnostic = PipeClientDiagnostics.FromException(phase, ex, ct.IsCancellationRequested, disconnectReason);
            }
            finally
            {
                _service = null;
                _screens = null;
                _packages = null;
                _library = null;
                _screenSettings = null;
                _screenPerformance = null;
                _screenVideo = null;
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            SetState(ConnectionState.Disconnected);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(Backoff(attempt++), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _diagnostic = PipeClientDiagnostics.Stopped();
                break;
            }
        }
    }

    private T Gate<T>(T? service)
        where T : class
    {
        var fault = Volatile.Read(ref _uiFault);
        if (fault is not null)
        {
            throw new HostAccessBlockedException(fault);
        }

        return service ?? throw new HostNotConnectedException();
    }

    private static SystemTextJsonFormatter CreateFormatter()
    {
        var f = new SystemTextJsonFormatter();
        ContractsJson.Configure(f.JsonSerializerOptions);
        return f;
    }

    private void SetState(ConnectionState s)
    {
        if (_state == s)
        {
            return;
        }

        _state = s;
        StateChanged?.Invoke(this, s);
    }
}
