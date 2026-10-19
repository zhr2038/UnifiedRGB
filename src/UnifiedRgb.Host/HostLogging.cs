using Microsoft.Extensions.Configuration;
using Serilog;

namespace UnifiedRgb.Host;

/// <summary>
/// Host log (ARCHITECTURE §10): async rolling file <c>logs\host-.log</c>, daily and on size, 5 MB × 10 by default; levels
/// from the "Serilog" configuration section.
/// </summary>
public static class HostLogging
{
    public const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration configuration, UnifiedRgbOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        var dir = Path.Combine(options.ResolvedDataDirectory, "logs");
        Directory.CreateDirectory(dir);
        return logger
            .ReadFrom.Configuration(configuration)
            .WriteTo.Async(a => a.File(
                Path.Combine(dir, "host-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: options.LogFileSizeLimitBytes,
                retainedFileCountLimit: options.LogRetainedFileCount,
                outputTemplate: OutputTemplate,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture));
    }
}
