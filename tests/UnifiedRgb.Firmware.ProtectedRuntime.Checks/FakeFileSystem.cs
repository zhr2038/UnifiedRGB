using UnifiedRgb.Firmware;
using UnifiedRgb.Firmware.ProtectedRuntime;

namespace UnifiedRgb.Firmware.ProtectedRuntime.Checks;

internal sealed class FakeFileSystem : IFirmwareRuntimeFileSystem, IFirmwareRuntimeElevation
{
    internal sealed record Node(RuntimeEntry Entry, byte[] Bytes);
    internal Dictionary<string, Node> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<string> Reads { get; } = [];
    internal List<string> Writes { get; } = [];
    internal int Calls { get; private set; }
    internal int ElevationReads { get; private set; }
    internal bool Admin { get; set; } = true;
    internal bool RootRace { get; set; }
    internal int FailAtFile { get; set; } = -1;
    internal Action<string>? AfterRead { get; set; }
    internal Action<string>? AfterCreate { get; set; }
    internal bool DuplicateAlias { get; set; }
    internal string? DeniedPath { get; set; }
    internal Dictionary<string, int> ReadBounds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsAdministratorOrSystem { get { ElevationReads++; return Admin; } }
    internal static string Key(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    internal void Put(string path, byte[]? bytes = null, bool secure = false, bool reparse = false)
    {
        var directory = bytes is null;
        Nodes[Key(path)] = new(new(directory, reparse, secure ? FirmwareRuntimeSecurity.Required(directory) : null), bytes ?? []);
    }
    internal void ReplaceEntry(string path, Func<RuntimeEntry, RuntimeEntry> update)
    {
        var key = Key(path); Nodes[key] = Nodes[key] with { Entry = update(Nodes[key].Entry) };
    }
    public RuntimeEntry? Inspect(string path, bool security)
    {
        Calls++;
        if (DeniedPath is not null && Key(path) == Key(DeniedPath)) throw new UnauthorizedAccessException("Fake access denied; not absence");
        return Nodes.TryGetValue(Key(path), out var node) ? node.Entry : null;
    }
    public IEnumerable<string> EnumerateChildren(string directory)
    {
        Calls++;
        var key = Key(directory);
        var children = Nodes.Keys.Where(x => Path.GetDirectoryName(x) == key).ToArray();
        foreach (var child in children) yield return child;
        if (DuplicateAlias && children.Length > 0) yield return children[0].ToUpperInvariant();
    }
    public byte[] ReadLockedSnapshot(string path, int maximum)
    {
        Calls++; Reads.Add(Key(path)); ReadBounds[Key(path)] = maximum;
        var bytes = Nodes[Key(path)].Bytes;
        if (bytes.Length > maximum) throw new InvalidDataException("Fake locked snapshot bound");
        var result = bytes.ToArray(); AfterRead?.Invoke(Key(path)); return result;
    }
    public void CreateProtectedDirectory(string path)
    {
        Calls++;
        if (RootRace && Key(path) == Key(FirmwareRuntimeResolver.LiveDirectory))
        {
            Put(path); throw new IOException("Fake exclusive root creation race");
        }
        RequireNew(path);
        if (Key(path) != Key(FirmwareRuntimeResolver.LiveDirectory)) RequireProtectedParent(path);
        Put(path, secure: true); Writes.Add(Key(path)); AfterCreate?.Invoke(Key(path));
    }
    public void CreateProtectedFile(string path, byte[] verifiedBytes)
    {
        Calls++;
        RequireNew(path); RequireProtectedParent(path);
        if (Writes.Count(x => !Nodes[x].Entry.Directory) == FailAtFile) throw new IOException("Fake interrupted first copy");
        Put(path, verifiedBytes.ToArray(), true); Writes.Add(Key(path)); AfterCreate?.Invoke(Key(path));
    }
    private void RequireNew(string path)
    {
        var relative = Path.GetRelativePath(FirmwareRuntimeResolver.LiveDirectory, path).Replace('\\', '/');
        if (relative != ".") FirmwareFiles.Relative(relative);
        if (Nodes.ContainsKey(Key(path))) throw new IOException("Fake CreateNew collision");
    }
    private void RequireProtectedParent(string path) => FirmwareRuntimeSecurity.Validate(Nodes[Key(Path.GetDirectoryName(path)!)].Entry);
}
