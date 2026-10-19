using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

/// <summary>One line of a probe journal: a session event, probe, marker or release with every row and decoded field.</summary>
public sealed record ProbeLogEntry(
    DateTimeOffset At,
    string ControllerId,
    string Class,
    string Owner,
    string Step,
    bool Ok,
    string Summary,
    IReadOnlyList<ProbeRowDto> Rows,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Append-only probe journal (NATIVE design §6): <c>&lt;DataDirectory&gt;\probes\&lt;class&gt;-&lt;yyyyMMdd&gt;.jsonl</c>, one JSON object
/// per line. Write failures are logged and never fail the step. The last <see cref="Capacity"/> entries are also kept in
/// memory; a null directory keeps them only there (tests).
/// </summary>
public sealed partial class ProbeLog
{
    public const int Capacity = 256;

    private readonly object _gate = new();
    private readonly Queue<ProbeLogEntry> _recent = new();
    private readonly ILogger _logger;

    public ProbeLog(string? directory, ILogger? logger = null)
    {
        Directory = directory;
        _logger = logger ?? NullLogger.Instance;
    }

    public string? Directory { get; }

    public IReadOnlyList<ProbeLogEntry> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    public static ProbeLog For(UnifiedRgbOptions options, ILogger? logger = null) =>
        new(Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).ResolvedDataDirectory, "probes"), logger);

    public void Append(ProbeLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > Capacity)
            {
                _recent.Dequeue();
            }

            if (Directory is null)
            {
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = Path.Combine(Directory, string.Create(CultureInfo.InvariantCulture, $"{entry.Class}-{entry.At:yyyyMMdd}.jsonl"));
                var line = JsonSerializer.Serialize(entry, HostJsonContext.Default.ProbeLogEntry) + "\n";
                File.AppendAllText(file, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogAppendFailed(ex.Message);
            }
        }
    }

    [LoggerMessage(EventId = 2012, Level = LogLevel.Warning, Message = "Probe journal append failed: {Why}")]
    private partial void LogAppendFailed(string why);
}
