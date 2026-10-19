using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// ARCHITECTURE §7.4–7.5: every <see cref="UnifiedRgbOptions.GuardRescanSeconds"/> re-runs WriterGuard (pause/resume,
/// open newly cleared controllers); a hotplug event triggers a full rescan on the next 1 s tick.
/// </summary>
public sealed partial class GuardMonitor(UnifiedRgbOptions options, LightingEngine engine, ILogger<GuardMonitor> logger) : BackgroundService
{
    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = Math.Max(1, options.GuardRescanSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                tick++;
                try
                {
                    if (engine.Adapters.RescanRequested)
                    {
                        await engine.RescanAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (tick % every == 0)
                    {
                        await engine.RecheckAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
#pragma warning disable CA1031 // the monitor must keep running; a failed pass is retried on the next tick
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogPassFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "WriterGuard re-scan failed")]
    private partial void LogPassFailed(Exception ex);
}
