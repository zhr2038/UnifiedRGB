using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Ipc;

/// <summary>
/// StreamJsonRpc over <c>\\.\pipe\&lt;PipeName&gt;</c> with the PipeAcl DACL, System.Text.Json (source-generated DTO
/// metadata), header-delimited framing. The first instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE so the Host
/// refuses to start if someone else already owns the name (pipe squatting). Each connection also exposes the shared
/// <see cref="IScreenService"/> target when one is registered (additive; IRgbService is unchanged), and likewise the
/// <see cref="IScenePackageService"/> target (OPUS-GENERIC-SCENE-PACKAGES) and the separate local
/// <see cref="IScreenDeviceSettingsService"/> target (IScreenService and its fakes are unchanged).
/// </summary>
public sealed partial class PipeServer(
    UnifiedRgbOptions options, IRgbService service, ILogger<PipeServer> logger, IScreenService? screens = null, IScenePackageService? packages = null,
    IScreenDeviceSettingsService? deviceSettings = null, IScreenPerformanceService? performance = null)
    : BackgroundService
{
    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;

    public const int MaxClients = 8;

    private readonly ConcurrentDictionary<int, Task> _sessions = new();
    private int _nextSession;

    public string PipeName => options.PipeName;

    /// <summary>Completes once the first pipe instance exists (tests wait on this before connecting).</summary>
    public Task Listening => _listening.Task;

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? _reserved;

    internal void ReserveEndpoint()
    {
        try { _reserved ??= CreatePipe(first: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _listening.TrySetException(ex); throw; }
    }

    private NamedPipeServerStream CreatePipe(bool first) => NamedPipeServerStreamAcl.Create(
        options.PipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, PipeAcl.Create());

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable VSTHRD003 // the readiness task belongs to this service's own ExecuteAsync
        await Listening.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _reserved, null)?.Dispose();
        _listening.TrySetCanceled();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = (first ? Interlocked.Exchange(ref _reserved, null) : null) ?? CreatePipe(first);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    if (first)
                    {
                        LogSquatted(options.PipeName, ex);
                        _listening.TrySetException(ex);
                        throw;
                    }

                    // all instances busy: wait for a session to end
                    await Task.Delay(200, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (first)
                {
                    first = false;
                    _listening.TrySetResult();
                    LogListening(options.PipeName);
                }

                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var id = Interlocked.Increment(ref _nextSession);
                _sessions[id] = RunSessionAsync(id, pipe, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            _listening.TrySetCanceled(CancellationToken.None);
            await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        }
    }

    /// <summary>Formatter both pipe ends must use.</summary>
    public static SystemTextJsonFormatter CreateFormatter()
    {
        var f = new SystemTextJsonFormatter();
        ContractsJson.Configure(f.JsonSerializerOptions);
        return f;
    }

    /// <summary>Owner name of connection <paramref name="id"/> for connection-scoped state (bring-up sessions, NATIVE design §6).</summary>
    public static string OwnerOf(int id) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pipe:{id}");

    private async Task RunSessionAsync(int id, NamedPipeServerStream pipe, CancellationToken ct)
    {
        await Task.Yield();
        var scope = service is Services.IConnectionScopedService scoped ? scoped.ForConnection(OwnerOf(id)) : null;
        try
        {
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, CreateFormatter()));
            rpc.AddLocalRpcTarget<IRgbService>(scope?.Service ?? service, null);
            if (performance is not null) rpc.AddLocalRpcTarget<IScreenPerformanceService>(performance, null);
            if (screens is not null)
            {
                rpc.AddLocalRpcTarget<IScreenService>(screens, null);
            }

            if (packages is not null)
            {
                rpc.AddLocalRpcTarget<IScenePackageService>(packages, null);
            }

            if (deviceSettings is not null)
            {
                rpc.AddLocalRpcTarget<IScreenDeviceSettingsService>(deviceSettings, null);
            }

            rpc.StartListening();
            LogClientConnected(id);
            using (ct.Register(rpc.Dispose))
            {
                await rpc.Completion.ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // a broken client session must never take the Host down
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!ct.IsCancellationRequested)
            {
                LogSessionFailed(id, ex);
            }
        }
        finally
        {
            if (scope is not null)
            {
                try
                {
                    await scope.DisposeAsync().ConfigureAwait(false); // ends this connection's bring-up sessions only
                }
#pragma warning disable CA1031 // the release outcome is journaled; a failure must not take the Host down
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogScopeFailed(id, ex);
                }
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            _sessions.TryRemove(id, out _);
            LogClientDisconnected(id);
        }
    }

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "Client session {Id}: ending its bring-up sessions failed")]
    private partial void LogScopeFailed(int id, Exception ex);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Pipe server listening on named pipe {Name}")]
    private partial void LogListening(string name);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Critical, Message = "Pipe {Name} already exists (another instance or squatter); IPC disabled")]
    private partial void LogSquatted(string name, Exception ex);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Debug, Message = "Client session {Id} connected")]
    private partial void LogClientConnected(int id);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Debug, Message = "Client session {Id} ended")]
    private partial void LogClientDisconnected(int id);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "Client session {Id} failed")]
    private partial void LogSessionFailed(int id, Exception ex);
}
