using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

internal interface ICodexQuotaClient
{
    Task<KeyboardCodexUsageDto> ReadAsync(CancellationToken ct);
}

/// <summary>Only an owned child, never a process lookup or a shared Codex session.</summary>
internal interface ICodexQuotaProcess : IAsyncDisposable
{
    TextReader Output { get; }
    TextReader Error { get; }
    TextWriter Input { get; }
    Task TerminateAndJoinAsync();
    /// <summary>Abort the owned pipe handles, unblocking any pending pipe operations even when they ignored cancellation.</summary>
    void ClosePipes();
}

internal sealed class CodexQuotaProcess : ICodexQuotaProcess
{
    private readonly Process _child;
    private CodexQuotaProcess(Process child) => _child = child;
    public TextReader Output => _child.StandardOutput;
    public TextReader Error => _child.StandardError;
    public TextWriter Input => _child.StandardInput;

    public static ICodexQuotaProcess Start()
        => new CodexQuotaProcess(Process.Start(StartInfo()) ?? throw new IOException("Local Codex unavailable."));

    internal static ProcessStartInfo StartInfo()
    {
        // Deliberately no PATH, shell, configurable arguments, auth-file access or token handling.
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "bin", "codex-real.exe");
        if (!Path.IsPathFullyQualified(executable)) throw new InvalidOperationException("Local Codex executable is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        return start;
    }

    public async Task TerminateAndJoinAsync()
    {
        try { if (!_child.HasExited) _child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Exit raced termination. */ }
        await _child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    public void ClosePipes()
    {
        foreach (var pipe in new IDisposable[] { _child.StandardOutput, _child.StandardError, _child.StandardInput })
        {
            try { pipe.Dispose(); }
            catch (IOException) { /* Closing an already broken owned pipe needs no retry. */ }
        }
    }

    public ValueTask DisposeAsync() { _child.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>One bounded, read-only JSONL conversation. The only outbound methods are literal allowlisted messages below.</summary>
internal sealed class CodexQuotaClient(Func<ICodexQuotaProcess>? start = null, TimeSpan? timeout = null) : ICodexQuotaClient
{
    internal const int MaximumLine = 65536, MaximumOutput = 262144;
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(3);
    public async Task<KeyboardCodexUsageDto> ReadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
        deadline.Token.ThrowIfCancellationRequested();
        await using var child = (start ?? CodexQuotaProcess.Start)();
        var drain = DrainAsync(child.Error, deadline.Token);
        var conversation = ConversationAsync(child, deadline.Token);
        try
        {
            if (await Task.WhenAny(conversation, drain).WaitAsync(deadline.Token).ConfigureAwait(false) == drain)
                await drain.WaitAsync(deadline.Token).ConfigureAwait(false); // Never depend on a reader honoring cancellation.
            return await conversation.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            deadline.Cancel();
            var pipes = Task.WhenAll(conversation, drain);
            ObserveFault(pipes);
            try
            {
                // Only the process we started and its descendants; never discover or kill shared Codex sessions.
                var termination = child.TerminateAndJoinAsync();
                ObserveFault(termination);
                await termination.WaitAsync(CleanupLimit, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // Reap first, then abort any lingering reads/writes and join the already-owned tasks. Do not start more drains.
                try { child.ClosePipes(); }
                finally
                {
                    await JoinPipesAsync(pipes).ConfigureAwait(false);
                }
            }
        }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(static completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task JoinPipesAsync(Task pipes)
    {
        try { await pipes.WaitAsync(CleanupLimit, CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException("Local Codex pipes did not close within their cleanup bound."); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Observed canceled/closed pipe faults; never log contents. */ }
    }

    private static async Task DrainAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[1024]; var total = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            if ((total += count) > MaximumOutput) throw new InvalidDataException("Local Codex output exceeded its bound.");
    }

    private static async Task<KeyboardCodexUsageDto> ConversationAsync(ICodexQuotaProcess child, CancellationToken ct)
    {
        var reader = new BoundedLines(child.Output);
        await SendAsync(child.Input, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"unifiedrgb-quota\",\"title\":\"Keyboard quota\",\"version\":\"1.0\"}}}", ct).ConfigureAwait(false);
        using (await ResponseAsync(reader, 1, ct).ConfigureAwait(false)) { }
        await SendAsync(child.Input, "{\"method\":\"initialized\",\"params\":{}}", ct).ConfigureAwait(false);
        await SendAsync(child.Input, "{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}", ct).ConfigureAwait(false);
        using var response = await ResponseAsync(reader, 2, ct).ConfigureAwait(false);
        return Parse(response.RootElement.GetProperty("result"));
    }

    private static async Task SendAsync(TextWriter input, string line, CancellationToken ct)
    {
        await input.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await input.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ResponseAsync(BoundedLines reader, int id, CancellationToken ct)
    {
        while (true)
        {
            var document = JsonDocument.Parse(await reader.ReadAsync(ct).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var reply) && reply == id)
            {
                if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out _))
                { document.Dispose(); throw new InvalidDataException("Local Codex read was refused."); }
                return document;
            }
            document.Dispose(); // Notifications and unrelated replies are bounded, ignored and never logged.
        }
    }

    internal static KeyboardCodexUsageDto Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return new(true, "Unavailable", null, null, null, null, "配额不可用");
        JsonElement bucket;
        string label;
        if (result.TryGetProperty("rateLimitsByLimitId", out var map))
        {
            if (map.ValueKind != JsonValueKind.Object || !map.TryGetProperty("codex", out bucket))
                return new(true, "Unavailable", null, null, null, null, "无法确定 Codex 配额分组");
            label = "codex";
        }
        else if (result.TryGetProperty("rateLimits", out bucket) && bucket.ValueKind == JsonValueKind.Object)
        {
            if (bucket.TryGetProperty("limitId", out var id) && id.ValueKind != JsonValueKind.Null && (id.ValueKind != JsonValueKind.String || id.GetString() != "codex"))
                return new(true, "Unavailable", null, null, null, null, "无法确定 Codex 配额分组");
            label = "legacy";
        }
        else return new(true, "Unavailable", null, null, null, null, "配额不可用");
        var primary = Window(bucket, "primary"); var secondary = Window(bucket, "secondary");
        return new(true, primary is null && secondary is null ? "Unavailable" : "Ready", label, primary, secondary, null,
            primary is null && secondary is null ? "配额窗口不可用" : null);
    }

    private static KeyboardCodexWindowDto? Window(JsonElement bucket, string name)
    {
        if (bucket.ValueKind != JsonValueKind.Object || !bucket.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object
            || !w.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent)
            || !w.TryGetProperty("windowDurationMins", out var duration) || duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out var minutes) || minutes <= 0
            || !w.TryGetProperty("resetsAt", out var reset) || reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var seconds)) return null;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return null; }
        return new(minutes, DurationLabel(minutes), Math.Clamp(100 - percent, 0, 100), at);
    }

    internal static string DurationLabel(int minutes) => minutes % 1440 == 0
        ? (minutes / 1440).ToString(CultureInfo.InvariantCulture) + "天"
        : minutes % 60 == 0 ? (minutes / 60).ToString(CultureInfo.InvariantCulture) + "小时"
        : minutes.ToString(CultureInfo.InvariantCulture) + "分钟";

    private sealed class BoundedLines(TextReader reader)
    {
        private int _total, _lines;
        private readonly char[] _one = new char[1];
        public async Task<string> ReadAsync(CancellationToken ct)
        {
            if (++_lines > 128) throw new InvalidDataException("Local Codex output exceeded its bound.");
            var line = new StringBuilder();
            while (await reader.ReadAsync(_one.AsMemory(), ct).ConfigureAwait(false) != 0)
            {
                if (++_total > MaximumOutput || line.Length >= MaximumLine) throw new InvalidDataException("Local Codex output exceeded its bound.");
                if (_one[0] == '\n') return line.ToString();
                line.Append(_one[0]);
            }
            throw new EndOfStreamException("Local Codex ended before its reply.");
        }
    }
}
