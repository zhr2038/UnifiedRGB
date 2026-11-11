using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnifiedRgb.Host.Services;

/// <summary>All task-store access and rasterization belongs to this worker. The HID actor only
/// reads immutable pixel snapshots or publishes a pure navigation intent.</summary>
internal sealed class CodexTaskViewProvider(
    ICodexTaskReader reader,
    CodexTaskDisplay display,
    Func<byte[]?> quotaPixels,
    Func<bool> enabled,
    TimeProvider time,
    ILogger<CodexTaskViewProvider>? logger = null) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> DisplayUnavailable = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1901, "CodexTaskDisplayUnavailable"), "Codex task display unavailable: {FailureType}");
    private static readonly Action<ILogger, string, Exception?> MetadataUnavailable = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1902, "CodexTaskMetadataUnavailable"), "Codex task metadata unavailable: {FailureType}");
    private readonly SemaphoreSlim _wake = new(0, 1);
    private byte[]? _taskPixels;
    private long? _lastRead;
    private bool _wasEnabled;
    private int _disposed;

    public byte[]? Pixels => display.IsTaskView ? Volatile.Read(ref _taskPixels) ?? quotaPixels() : quotaPixels();

    public void Browse(int direction)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        display.Browse(direction);
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* A pending wake already observes the latest selected task. */ }
        catch (ObjectDisposedException) { /* A retired input callback cannot restart a stopped worker. */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                display.PublishSnapshot(new("Unavailable", [], time.GetUtcNow()));
                Volatile.Write(ref _taskPixels, null);
                if (logger is not null) DisplayUnavailable(logger, ex.GetType().Name, null);
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!enabled())
        {
            if (_wasEnabled) display.ReturnToQuota();
            _wasEnabled = false; _lastRead = null;
            Volatile.Write(ref _taskPixels, null);
            return;
        }
        _wasEnabled = true;
        var now = time.GetTimestamp();
        if (_lastRead is null || time.GetElapsedTime(_lastRead.Value, now) >= TimeSpan.FromSeconds(5))
        {
            _lastRead = now;
            CodexTaskSnapshot snapshot;
            try { snapshot = await reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                snapshot = new("Unavailable", [], time.GetUtcNow());
                if (logger is not null) MetadataUnavailable(logger, ex.GetType().Name, null);
            }
            display.PublishSnapshot(snapshot);
        }
        ct.ThrowIfCancellationRequested();
        if (display.RenderTaskFrame(time.GetUtcNow()) is { } pixels)
            Volatile.Write(ref _taskPixels, pixels);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        base.Dispose();
        _wake.Dispose();
    }
}
