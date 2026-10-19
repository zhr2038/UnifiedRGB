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
    private volatile IScreenDeviceSettingsService? _screenSettings;
    private volatile IScreenPerformanceService? _screenPerformance;
    private ConnectionState _state = ConnectionState.Disconnected;

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

    /// <summary>Why the last session attempt failed or ended (null after a successful connect).</summary>
    public string? LastError { get; private set; }

    /// <summary>The live proxy, or null while disconnected.</summary>
    public IRgbService? Service => _service;

    /// <summary>Number of successful connections so far (a Host restart increments it).</summary>
    public int Connections { get; private set; }

    /// <summary>Delay before reconnect attempt <paramref name="attempt"/> (0-based): 250 ms × 2^n, capped at 5 s.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var ms = 250.0 * Math.Pow(2, Math.Min(attempt, 10));
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxBackoff.TotalMilliseconds));
    }

    /// <summary>The proxy, or InvalidOperationException("Host not connected").</summary>
    public IRgbService Require() => _service ?? throw new InvalidOperationException("Host not connected.");
    public IScreenDeviceSettingsService RequireScreenSettings() => _screenSettings ?? throw new InvalidOperationException("后台未连接。");
    public IScreenService RequireScreens() => _screens ?? throw new InvalidOperationException("后台未连接。");
    public IScreenPerformanceService RequireScreenPerformance() => _screenPerformance ?? throw new InvalidOperationException("后台未连接。");
    public IScenePackageService RequirePackages() => _packages ?? throw new InvalidOperationException("后台未连接。");

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public async Task<bool> WaitConnectedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_service is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
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
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
                PipeServerIdentity.Verify(pipe, _trust);
                LastError = null;
                using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, CreateFormatter()));
                var proxy = rpc.Attach<IRgbService>();
                var screens = rpc.Attach<IScreenService>();
                var packages = rpc.Attach<IScenePackageService>();
                var screenSettings = rpc.Attach<IScreenDeviceSettingsService>();
                var screenPerformance = rpc.Attach<IScreenPerformanceService>();
                proxy.ControllersChanged += (s, e) => ControllersChanged?.Invoke(this, e);
                proxy.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
                proxy.TelemetryTick += (s, e) => TelemetryTick?.Invoke(this, e);
                rpc.StartListening();
                _service = proxy;
                _screens = screens;
                _packages = packages;
                _screenSettings = screenSettings;
                _screenPerformance = screenPerformance;
                Connections++;
                attempt = 0;
                SetState(ConnectionState.Connected);
                using (ct.Register(rpc.Dispose))
                {
                    await rpc.Completion.ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // any connect/session failure → reconnect with backoff
            catch (Exception ex) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LastError = ex is UnauthorizedAccessException ? ex.Message : null;
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            finally
            {
                _service = null;
                _screens = null;
                _packages = null;
                _screenSettings = null;
                _screenPerformance = null;
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
                break;
            }
        }
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
