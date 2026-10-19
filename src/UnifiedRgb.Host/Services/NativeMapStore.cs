using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.Services;

/// <summary>One mapped native controller in <c>native-maps.json</c>.</summary>
public sealed record NativeMapEntry(string ControllerId, string? Evidence, IReadOnlyList<NativeZoneMap> Zones);

/// <summary>The <c>native-maps.json</c> file (schema 1).</summary>
public sealed record NativeMapFile(int SchemaVersion, IReadOnlyList<NativeMapEntry> Controllers)
{
    public const int CurrentSchema = 1;
}

/// <summary>
/// <c>&lt;DataDirectory&gt;\native-maps.json</c> (NATIVE design §3.5, §7.6): the owner-confirmed zone maps of native
/// controllers, written by Codex after E3m and read by the adapters at discovery. Absent, unreadable or unmapped → null and
/// the adapter keeps its aggregate (unmapped) zone; an unreadable file is logged, never guessed around. mounting.json is not
/// touched. Written atomically (unique temp file, flushed, renamed). A null path keeps the maps in memory (tests).
/// </summary>
public sealed partial class NativeMapStore : INativeMapSource
{
    public const string FileName = "native-maps.json";
    public const long MaxFileBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private NativeMapFile _memory = new(NativeMapFile.CurrentSchema, []);

    public NativeMapStore(string? path, ILogger? logger = null)
    {
        Path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public NativeMapStore(UnifiedRgbOptions options, ILogger<NativeMapStore>? logger = null)
        : this(System.IO.Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).ResolvedDataDirectory, FileName), logger)
    {
    }

    public string? Path { get; }

    /// <summary>The map of <paramref name="id"/>, or null (unmapped, or the file is absent/unreadable).</summary>
    public NativeControllerMap? Get(ControllerId id)
    {
        NativeMapFile file;
        try
        {
            file = Load();
        }
        catch (InvalidDataException ex)
        {
            LogUnreadable(ex.Message);
            return null;
        }

        var e = file.Controllers.FirstOrDefault(c => c is not null && string.Equals(c.ControllerId, id.Value, StringComparison.Ordinal));
        return e?.Zones is null ? null : new NativeControllerMap(e.Evidence, e.Zones);
    }

    /// <summary>The whole file; empty when absent. Throws <see cref="InvalidDataException"/> when unreadable.</summary>
    public NativeMapFile Load()
    {
        lock (_gate)
        {
            if (Path is null)
            {
                return _memory;
            }

            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                return new NativeMapFile(NativeMapFile.CurrentSchema, []);
            }

            if (info.Length > MaxFileBytes)
            {
                throw new InvalidDataException($"{FileName} exceeds {MaxFileBytes} bytes.");
            }

            NativeMapFile? file;
            try
            {
                file = JsonSerializer.Deserialize(File.ReadAllBytes(Path), HostJsonContext.Default.NativeMapFile);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
            }

            if (file?.Controllers is null || file.SchemaVersion != NativeMapFile.CurrentSchema)
            {
                throw new InvalidDataException($"{FileName} is not a schema {NativeMapFile.CurrentSchema} native map file.");
            }

            return file;
        }
    }

    /// <summary>Sets (or with null removes) one controller's map and rewrites the file atomically.</summary>
    public void Save(string controllerId, NativeControllerMap? map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        lock (_gate)
        {
            var all = Load().Controllers.Where(c => !string.Equals(c.ControllerId, controllerId, StringComparison.Ordinal)).ToList();
            if (map is not null)
            {
                all.Add(new NativeMapEntry(controllerId, map.Evidence, map.Zones));
            }

            Write(new NativeMapFile(NativeMapFile.CurrentSchema, [.. all.OrderBy(c => c.ControllerId, StringComparer.Ordinal)]));
        }
    }

    private void Write(NativeMapFile file)
    {
        if (Path is null)
        {
            _memory = file;
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, HostJsonContext.Default.NativeMapFile);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [LoggerMessage(EventId = 2011, Level = LogLevel.Warning, Message = "native-maps.json unreadable ({Why}); native controllers stay unmapped")]
    private partial void LogUnreadable(string why);
}
