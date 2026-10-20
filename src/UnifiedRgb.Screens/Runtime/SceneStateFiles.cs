using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Internal application state only; never package content, configuration, calibration or native maps.</summary>
public sealed record SceneStateFile(string RelativePath, byte[] Bytes);

/// <summary>
/// A small write-ahead transaction for the three existing scene-state files. A prepared record restores the old version;
/// a committed record finishes the new version. Original file formats/bytes are unchanged. Recovery precedes device startup.
/// The process uses one configured root; per-root locks also serialize independently constructed stores and offline fixtures.
/// </summary>
public static class SceneStateFiles
{
    public const string JournalName = ".scene-state-transaction.json";
    private const int MaxJournalBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Action<IReadOnlyList<SceneStateFile>>> Validations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Coordinate library reference checks with the durable scene commit point, never the render thread.</summary>
    public static T Coordinate<T>(string root, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (Gates.GetOrAdd(root, static _ => new object())) return action();
    }

    /// <summary>One Host-local validation hook. Recovery intentionally does not validate availability of old package content.</summary>
    public static IDisposable RegisterValidation(string root, Action<IReadOnlyList<SceneStateFile>> validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Coordinate(root, () =>
        {
            if (!Validations.TryAdd(root, validation)) throw new InvalidOperationException("Scene reference validation already registered for this data root.");
            return 0;
        });
        return new ValidationRegistration(root, validation);
    }

    private sealed class ValidationRegistration(string root, Action<IReadOnlyList<SceneStateFile>> validation) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Coordinate(root, () =>
            {
                if (Validations.TryGetValue(root, out var current) && ReferenceEquals(current, validation)) Validations.TryRemove(root, out _);
                return 0;
            });
        }
    }

    public static void Recover(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (Gates.GetOrAdd(root, static _ => new object())) { RecoverHeld(root); }
    }

    public static void Commit(string root, IReadOnlyList<SceneStateFile> files) => CommitCore(root, files, null);

    internal static void CommitCore(string root, IReadOnlyList<SceneStateFile> files, Action<int>? afterFile)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (Gates.GetOrAdd(root, static _ => new object()))
        {
            RecoverHeld(root);
            if (files.Count is < 1 or > 3 || files.Select(f => f.RelativePath).Distinct(StringComparer.Ordinal).Count() != files.Count)
                throw new ArgumentException("A scene-state transaction needs one to three distinct state files.", nameof(files));
            var changes = files.Select(f =>
            {
                Check(f.RelativePath, f.Bytes);
                var path = Resolve(root, f.RelativePath);
                if (Directory.Exists(path)) throw new IOException($"Scene-state target is a directory: {f.RelativePath}");
                var before = File.Exists(path) ? ReadBounded(path, Limit(f.RelativePath)) : null;
                return new SceneStateChange(f.RelativePath, before, f.Bytes, Hash(before), Hash(f.Bytes));
            }).ToArray();
            if (Validations.TryGetValue(root, out var validate)) validate(files);
            var record = new SceneStateRecord(1, false, changes);
            var journal = Path.Combine(root, JournalName);
            WriteRecord(journal, record); // no target changed until the complete recovery record is durable
            try
            {
                for (var i = 0; i < changes.Length; i++)
                {
                    AtomicWrite(Resolve(root, changes[i].Path), changes[i].After);
                    afterFile?.Invoke(i);
                }
                WriteRecord(journal, record with { Committed = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { Apply(root, record, committed: false); File.Delete(journal); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Scene state was not published. Disk recovery is pending; the next start must recover before opening devices.", new AggregateException(ex, rollback));
                }
                throw;
            }
            TryDelete(journal); // a retained committed record is harmless and completed by the next recovery
        }
    }

    private static void RecoverHeld(string root)
    {
        var path = Path.Combine(root, JournalName);
        if (!File.Exists(path)) return;
        SceneStateRecord record;
        try
        {
            record = JsonSerializer.Deserialize(ReadBounded(path, MaxJournalBytes), SceneStateJsonContext.Default.SceneStateRecord)
                ?? throw new InvalidDataException("Empty scene-state recovery record.");
            if (record.Schema != 1 || record.Files is not { Length: >= 1 and <= 3 } || record.Files.Any(f => f is null)
                || record.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != record.Files.Length)
                throw new InvalidDataException("Invalid scene-state recovery record.");
            foreach (var f in record.Files)
            {
                Check(f.Path, f.After);
                if (f.Before is not null) Check(f.Path, f.Before);
                if (Hash(f.Before) != f.BeforeHash || Hash(f.After) != f.AfterHash)
                    throw new InvalidDataException("Scene-state recovery bytes do not match their hashes.");
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new InvalidDataException("Scene-state recovery record is invalid; no device may start.", ex); }
        Apply(root, record, record.Committed);
        File.Delete(path);
    }

    private static void Apply(string root, SceneStateRecord record, bool committed)
    {
        foreach (var f in record.Files)
        {
            var target = Resolve(root, f.Path);
            var bytes = committed ? f.After : f.Before;
            if (bytes is null) File.Delete(target);
            else if (!File.Exists(target) || !ReadBounded(target, Limit(f.Path)).AsSpan().SequenceEqual(bytes)) AtomicWrite(target, bytes);
        }
    }

    private static void Check(string path, byte[] bytes)
    {
        if (bytes is null || bytes.Length > Limit(path)) throw new ArgumentException($"Scene-state file '{path}' exceeds its read limit.");
    }
    private static int Limit(string path) => path switch
    {
        "profiles/active.json" => 1024 * 1024,
        "screens/scene.json" => ScreenSceneStore.MaxBytes,
        "screens/device-settings.json" => ScreenDeviceSettingsStore.MaxBytes,
        _ => throw new ArgumentException("Only the three scene-state files may participate in recovery.", nameof(path)),
    };
    private static string Resolve(string root, string relative) { _ = Limit(relative); return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); }
    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] ReadBounded(string path, int limit)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length > limit) throw new InvalidDataException($"Scene-state file '{Path.GetFileName(path)}' is too large.");
        var bytes = new byte[(int)fs.Length]; fs.ReadExactly(bytes); return bytes;
    }
    private static void WriteRecord(string path, SceneStateRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, SceneStateJsonContext.Default.SceneStateRecord);
        if (bytes.Length > MaxJournalBytes) throw new ArgumentException("Scene-state recovery record is too large.");
        AtomicWrite(path, bytes);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(bytes); fs.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { TryDelete(temp); }
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* recovery data remains available */ }
    }
}

internal sealed record SceneStateChange(string Path, byte[]? Before, byte[] After, string? BeforeHash, string? AfterHash);
internal sealed record SceneStateRecord(int Schema, bool Committed, SceneStateChange[] Files);
[JsonSerializable(typeof(SceneStateRecord))]
internal sealed partial class SceneStateJsonContext : JsonSerializerContext;
