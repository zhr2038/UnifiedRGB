using Microsoft.Extensions.Hosting;

namespace UnifiedRgb.Host.Ipc;

/// <summary>Reserves IPC before hardware startup; normal PipeServer remains last so RPC stops before devices on shutdown.</summary>
internal sealed class PipeReservation(PipeServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        server.ReserveEndpoint();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
