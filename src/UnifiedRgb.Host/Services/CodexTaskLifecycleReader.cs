using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Host.Services;

/// <summary>Bounded metadata-prefix scan, then incremental append reads. Non-lifecycle message bodies are discarded.</summary>
internal sealed partial class CodexTaskLifecycleReader(string root) : ICodexTaskLifecycleSource
{
    internal const int MaximumColdBytes = 64 * 1024 * 1024;
    internal const int MaximumLifecycleLine = 1024 * 1024;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CodexTaskLifecycle Read(string path, string id, CancellationToken ct)
    {
        var key = Normalize(path);
        try { return ReadCore(key, id, ct); }
        catch
        {
            // Never reuse partially parsed state after an interrupted, malformed or bounded-out read.
            if (_cache.Remove(key, out var failed)) failed.Lifecycle?.Dispose();
            throw;
        }
    }

    private CodexTaskLifecycle ReadCore(string path, string id, CancellationToken ct)
    {
        path = Normalize(path);
        var sessions = Path.GetFullPath(Path.Combine(root, "sessions")) + Path.DirectorySeparatorChar;
        var archives = Path.GetFullPath(Path.Combine(root, "archived_sessions")) + Path.DirectorySeparatorChar;
        if (!Guid.TryParse(id, out _) ||
            !(path.StartsWith(sessions, StringComparison.OrdinalIgnoreCase) || path.StartsWith(archives, StringComparison.OrdinalIgnoreCase)) ||
            !Path.GetFileName(path).EndsWith("-" + id + ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Codex rollout outside metadata scope");
        NoReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        var length = file.Length;
        var creation = File.GetCreationTimeUtc(path);
        var write = File.GetLastWriteTimeUtc(path);
        if (!_cache.TryGetValue(path, out var entry) || entry.Creation != creation || length < entry.Offset ||
            length == entry.Offset && entry.Write != write)
        {
            if (_cache.Count >= 512 && !_cache.ContainsKey(path)) throw new InvalidDataException("Codex lifecycle cache bound");
            entry = new Entry { Creation = creation, Offset = Math.Max(0, length - MaximumColdBytes) };
            entry.SkipFirst = entry.Offset != 0;
            _cache[path] = entry;
        }
        if (length - entry.Offset > MaximumColdBytes) throw new InvalidDataException("Codex append scan bound");
        file.Position = entry.Offset;
        var buffer = new byte[65536];
        while (file.Position < length)
        {
            ct.ThrowIfCancellationRequested();
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, length - file.Position));
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (entry.SkipFirst) { if (value == 10) entry.SkipFirst = false; continue; }
                if (value == 10) { FinishLine(entry); continue; }
                if (entry.PrefixCount < entry.Prefix.Length)
                {
                    entry.Prefix[entry.PrefixCount++] = value;
                    if (entry.PrefixCount == entry.Prefix.Length)
                    {
                        entry.Kind = Classify(entry.Prefix);
                        if (entry.Kind == LineKind.Lifecycle) { entry.Lifecycle = new MemoryStream(); entry.Lifecycle.Write(entry.Prefix); }
                    }
                }
                else if (entry.Kind == LineKind.Lifecycle)
                {
                    if (entry.Lifecycle!.Length >= MaximumLifecycleLine) throw new InvalidDataException("Codex lifecycle line bound");
                    entry.Lifecycle.WriteByte(value);
                }
            }
        }
        entry.Offset = file.Position; entry.Write = write;
        if (entry.PrefixCount != 0 && Classify(entry.Prefix.AsSpan(0, entry.PrefixCount)) != LineKind.Other)
            throw new IOException("Codex lifecycle record is still being appended");
        return entry.Last ?? throw new InvalidDataException("No lifecycle metadata within bounded rollout tail");
    }

    private static void FinishLine(Entry entry)
    {
        var kind = entry.PrefixCount < entry.Prefix.Length ? Classify(entry.Prefix.AsSpan(0, entry.PrefixCount)) : entry.Kind;
        try
        {
            if (kind == LineKind.Unknown) { entry.Last = null; return; }
            if (kind != LineKind.Lifecycle) return;
            var bytes = entry.Lifecycle?.ToArray() ?? entry.Prefix.AsSpan(0, entry.PrefixCount).ToArray();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var value = document.RootElement;
            if (value.GetProperty("type").GetString() != "event_msg") throw new InvalidDataException("Codex lifecycle envelope");
            var payload = value.GetProperty("payload");
            var type = payload.GetProperty("type").GetString();
            var turn = payload.TryGetProperty("turn_id", out var turnValue) ? turnValue.GetString() : null;
            if (!Guid.TryParse(turn, out _)) throw new InvalidDataException("Codex lifecycle turn identity");
            if (type == "task_started")
            {
                var started = payload.TryGetProperty("started_at", out var stamp) ? Timestamp(stamp) : Timestamp(value.GetProperty("timestamp"));
                entry.Last = new(true, turn, started);
            }
            else if (entry.Last is null || entry.Last.TurnId == turn)
                entry.Last = new(false, turn, null); // A delayed old completion never closes a newer running turn.
        }
        finally
        {
            entry.Lifecycle?.Dispose(); entry.Lifecycle = null; entry.PrefixCount = 0; entry.Kind = LineKind.Unknown;
        }
    }

    private static DateTimeOffset Timestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)) return timestamp;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0)
            return number > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number);
        throw new InvalidDataException("Codex lifecycle start time");
    }

    private static LineKind Classify(ReadOnlySpan<byte> prefix)
    {
        var matches = TypePattern().Matches(Encoding.UTF8.GetString(prefix));
        if (matches.Count == 0) return LineKind.Unknown;
        var envelope = matches[0].Groups[1].Value;
        if (envelope != "event_msg") return envelope is "session_meta" or "response_item" or "turn_context" or "compacted"
            or "world_state" or "token_usage_record" or "inter_agent_communication_metadata" ? LineKind.Other : LineKind.Unknown;
        if (matches.Count < 2) return LineKind.Unknown;
        return matches[1].Groups[1].Value is "task_started" or "task_complete" or "turn_aborted" or "task_aborted" ? LineKind.Lifecycle : LineKind.Other;
    }

    internal static string Normalize(string path) => Path.GetFullPath(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
    internal static void NoReparse(string path)
    {
        var current = Normalize(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Codex metadata reparse path");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
    private enum LineKind { Unknown, Other, Lifecycle }
    private sealed class Entry
    {
        internal readonly byte[] Prefix = new byte[4096];
        internal int PrefixCount;
        internal long Offset;
        internal DateTime Creation, Write;
        internal bool SkipFirst;
        internal LineKind Kind;
        internal MemoryStream? Lifecycle;
        internal CodexTaskLifecycle? Last;
    }
    [GeneratedRegex("\"type\"\\s*:\\s*\"([^\"\\\\]{1,80})\"", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();
}
