using System.Collections.Concurrent;
using System.Text.Json;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>One bound role: a lighting zone or a screen id (exactly one of them).</summary>
public sealed record BindingEntry(string Role, string Kind, string? ControllerId, int? ZoneId, string? ScreenId)
{
    public const string Lighting = "lighting";
    public const string Screen = "screen";

    [System.Text.Json.Serialization.JsonIgnore]
    public ZoneRef? Zone => ControllerId is { } c && ZoneId is { } z ? new ZoneRef(new UnifiedRgb.Devices.ControllerId(c), new UnifiedRgb.Devices.ZoneId(z)) : null;
}

/// <summary>The immutable binding snapshot as stored; its file bytes hash to its BindingId.</summary>
public sealed record BindingSnapshotFile(int Schema, string ContentId, IReadOnlyList<BindingEntry> Bindings);

internal sealed record BindingPointerFile(int Schema, string ContentId, string BindingId);

/// <summary>A verified snapshot with its id.</summary>
public sealed record BindingSnapshot(string BindingId, string ContentId, IReadOnlyList<BindingEntry> Bindings)
{
    /// <summary>Bound lighting zones in the package's lighting order (the frame-set zone order).</summary>
    public IReadOnlyList<ZoneRef> LightingTargets(ScenePackageManifest manifest) =>
        manifest.Lighting.Select(l => Bindings.Single(b => b.Role == l.Role).Zone ?? throw new InvalidDataException($"role {l.Role} has no zone")).ToArray();
}

/// <summary>
/// Local hardware bindings, separate from the immutable package content: <c>scene-packages/bindings/&lt;contentId&gt;/
/// &lt;bindingId&gt;.json</c> (BindingId = SHA-256 of those exact bytes; never rewritten) plus <c>current.json</c>, the atomic
/// pointer to the latest good save. A saved profile pins its BindingId, so rebinding later never retargets it.
/// </summary>
public sealed class ScenePackageBindingStore
{
    public const string DirectoryName = "bindings";
    public const string PointerName = "current.json";

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<(string, string), BindingSnapshot> _verified = new();

    public ScenePackageBindingStore(ScenePackageStore packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        Root = Path.Combine(packages.Root, DirectoryName);
    }

    public string Root { get; }

    /// <summary>Writes (idempotently) the snapshot of <paramref name="bindings"/> and points current.json at it.</summary>
    public BindingSnapshot Save(string contentId, IReadOnlyList<BindingEntry> bindings)
    {
        Require(contentId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new BindingSnapshotFile(1, contentId, bindings), StoreJsonContext.Default.BindingSnapshotFile);
        var id = ScenePackageFormat.Sha256Hex(bytes);
        lock (_gate)
        {
            var dir = Path.Combine(Root, contentId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, id + ".json");
            if (!TryLoad(contentId, id, out _, out _))
            {
                if (File.Exists(path))
                {
                    File.Move(path, Path.Combine(dir, $"~corrupt-{id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"));
                }

                WriteAtomic(path, bytes);
            }

            var pointer = JsonSerializer.SerializeToUtf8Bytes(new BindingPointerFile(1, contentId, id), StoreJsonContext.Default.BindingPointerFile);
            WriteAtomic(Path.Combine(dir, PointerName), pointer);
            var snapshot = new BindingSnapshot(id, contentId, bindings.ToArray());
            _verified[(contentId, id)] = snapshot;
            return snapshot;
        }
    }

    /// <summary>The snapshot current.json points at, or null (none saved, or pointer/snapshot unreadable: <paramref name="problem"/>).</summary>
    public BindingSnapshot? Current(string contentId, out string? problem)
    {
        Require(contentId);
        problem = null;
        var pointerPath = Path.Combine(Root, contentId, PointerName);
        if (!File.Exists(pointerPath))
        {
            return null;
        }

        try
        {
            var pointer = JsonSerializer.Deserialize(File.ReadAllBytes(pointerPath), StoreJsonContext.Default.BindingPointerFile);
            if (pointer is null || pointer.Schema != 1 || pointer.ContentId != contentId || !ScenePackageFormat.IsSha256Hex(pointer.BindingId))
            {
                problem = "current.json is not a valid binding pointer";
                return null;
            }

            return TryLoad(contentId, pointer.BindingId, out var snapshot, out problem) ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"current.json unreadable ({ex.Message})";
            return null;
        }
    }

    /// <summary>A pinned snapshot (profiles/active scene), verified by hash; false and why otherwise.</summary>
    public bool TryLoad(string contentId, string bindingId, out BindingSnapshot snapshot, out string? problem)
    {
        snapshot = null!;
        if (!ScenePackageFormat.IsSha256Hex(contentId) || !ScenePackageFormat.IsSha256Hex(bindingId))
        {
            problem = "not a content/binding id";
            return false;
        }

        if (_verified.TryGetValue((contentId, bindingId), out var cached))
        {
            snapshot = cached;
            problem = null;
            return true;
        }

        var path = Path.Combine(Root, contentId, bindingId + ".json");
        try
        {
            if (!File.Exists(path))
            {
                problem = $"binding snapshot {bindingId} of package {contentId} does not exist";
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            if (ScenePackageFormat.Sha256Hex(bytes) != bindingId)
            {
                problem = $"binding snapshot {bindingId} is corrupt (hash mismatch)";
                return false;
            }

            var file = JsonSerializer.Deserialize(bytes, StoreJsonContext.Default.BindingSnapshotFile);
            if (file is null || file.Schema != 1 || file.ContentId != contentId || file.Bindings is null)
            {
                problem = $"binding snapshot {bindingId} does not describe package {contentId}";
                return false;
            }

            snapshot = new BindingSnapshot(bindingId, contentId, file.Bindings);
            _verified[(contentId, bindingId)] = snapshot;
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"binding snapshot {bindingId} unreadable ({ex.Message})";
            return false;
        }
    }

    private static void Require(string contentId)
    {
        if (!ScenePackageFormat.IsSha256Hex(contentId))
        {
            throw new ArgumentException($"'{ScenePackageFormat.Printable(contentId)}' is not a package content id.", nameof(contentId));
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
