using Microsoft.Extensions.Hosting;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>Starts the screen subsystem with the Host (nothing runs unless UnifiedRgb:Screens:Enabled) and stops its workers.</summary>
internal sealed class ScreenHostedService(ScreenManager screens) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        screens.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => screens.StopAsync();
}
