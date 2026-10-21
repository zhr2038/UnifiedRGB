using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Services;

/// <summary>Writes <c>status.json</c> atomically (temp file + rename) every second and raises StatusChanged / TelemetryTick.</summary>
public sealed partial class StatusWriter(UnifiedRgbOptions options, LightingEngine engine, RgbNotifier notifier, ILogger<StatusWriter> logger) : BackgroundService
{
    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;

    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions { WriteIndented = true });
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> WriteGates = new(StringComparer.OrdinalIgnoreCase);

    public string StatusPath { get; } = Path.Combine(options.ResolvedDataDirectory, "status.json");

    public void WriteNow() => Write(engine.BuildStatus(diagnostics: false));

    private void Write(Contracts.Dto.StatusDto status)
    {
        lock (WriteGates.GetOrAdd(StatusPath, static _ => new object())) WriteCore(status);
    }

    private void WriteCore(Contracts.Dto.StatusDto status)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
        var tmp = StatusPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(status, Json));
        // The replace fails while a reader (or AV/indexer) holds status.json without FILE_SHARE_DELETE; retry briefly,
        // otherwise the previous complete file stays in place and the next tick tries again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, StatusPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(10 * attempt);
            }
        }
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private const int MoveAttempts = 5;

    /// <summary>
    /// Reads a file written by <see cref="StatusWriter"/> without blocking its replace-rename: opens with ReadWrite|Delete
    /// sharing (the rename may proceed while the old file is held) and retries the short window in which the rename
    /// itself holds the name. A successful read always returns one complete document, old or new.
    /// </summary>
    public static async Task<string> ReadSharedAsync(string path, int attempts = 50, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && ex is not FileNotFoundException and not DirectoryNotFoundException
                                       && attempt < attempts)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var status = engine.BuildStatus(diagnostics: false);
                notifier.PublishStatus(status);
                notifier.PublishTelemetry(engine.ReadTelemetry());
                try
                {
                    Write(status);
                }
                catch (IOException ex)
                {
                    LogWriteFailed(ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogWriteFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "status.json write failed")]
    private partial void LogWriteFailed(Exception ex);
}
