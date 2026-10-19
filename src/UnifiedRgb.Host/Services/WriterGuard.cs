using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Services;

public enum GuardState
{
    Clear,
    Blocked,
    Contended,
}

public readonly record struct GuardVerdict(GuardState State, string? Writer)
{
    public static GuardVerdict Clear { get; } = new(GuardState.Clear, null);

    public static GuardVerdict Blocked(string writer) => new(GuardState.Blocked, writer);

    public static GuardVerdict Contended(string writer) => new(GuardState.Contended, writer);
}

/// <summary>What a takeover did. Services are restored on clean exit; processes are never killed, only named.</summary>
public sealed record TakeoverResult(DeviceClass Class, bool Enabled, IReadOnlyList<string> StoppedServices, IReadOnlyList<string> ProcessesToClose, string? Error);

public interface IWriterGuard
{
    GuardVerdict Check(DeviceClass deviceClass);

    bool IsTakeoverEnabled(DeviceClass deviceClass);

    /// <summary>Persists the per-class toggle. Enabling runs <see cref="TakeoverAsync"/>; disabling restores the services no other enabled class still needs.</summary>
    Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct);

    /// <summary>Stops the vendor services writing to this class (only when its toggle is on) and records how to restore them.</summary>
    Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct);

    /// <summary>Restores every service recorded by Takeover (start type, then running state). Called on clean exit.</summary>
    Task RestoreAsync(CancellationToken ct);

    /// <summary>Running programs that are not confirmed writers (NATIVE design §8); informational, never in a verdict.</summary>
    IReadOnlyList<WriterCandidate> RunningCandidates() => [];
}

/// <summary>One service Takeover changed, with what it was before.</summary>
public sealed record StoppedService(string Name, string StartType, bool WasRunning);

/// <summary>Persisted Takeover state: the toggles and the restore bookkeeping (written before each SCM change).</summary>
public sealed record TakeoverState(IReadOnlyList<string> Enabled, IReadOnlyList<StoppedService> Stopped)
{
    public static TakeoverState Empty { get; } = new([], []);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(TakeoverState))]
[JsonSerializable(typeof(NativeMapFile))]
[JsonSerializable(typeof(ProbeLogEntry))]
internal sealed partial class HostJsonContext : JsonSerializerContext
{
}

/// <summary><c>&lt;DataDirectory&gt;\takeover.json</c>, written atomically. A null path keeps the state in memory (tests).</summary>
public sealed class TakeoverStore(string? path)
{
    public TakeoverStore(UnifiedRgbOptions options)
        : this(System.IO.Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).ResolvedDataDirectory, "takeover.json"))
    {
    }

    private TakeoverState _memory = TakeoverState.Empty;

    public string? Path { get; } = path;

    public TakeoverState Load()
    {
        if (Path is null)
        {
            return _memory;
        }

        if (!File.Exists(Path))
        {
            return TakeoverState.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(Path), HostJsonContext.Default.TakeoverState) ?? TakeoverState.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path} is corrupt; fix or delete it (it records services to restore).", ex);
        }
    }

    public void Save(TakeoverState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Path is null)
        {
            _memory = state;
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = $"{Path}.{Guid.NewGuid():N}.tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(JsonSerializer.SerializeToUtf8Bytes(state, HostJsonContext.Default.TakeoverState));
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>
/// Single-writer policy (ARCHITECTURE §7), default deny: a class with a known vendor writer present is Blocked and no
/// controller of that class is opened. Only the Virtual class is exempt (it never reaches hardware). Takeover is a
/// persisted per-class opt-in: it stops the vendor <i>services</i> through SCM (watchers first, start type set to Disabled so
/// they are not restarted), records their original start type and running state before each change, and restores them on
/// clean exit. GUI processes are never killed; they are reported so the user can close them. Vendor files are never touched.
/// </summary>
public sealed class WriterGuard : IWriterGuard, IDisposable
{
    public static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(15);

    private readonly ProcessScanner _scanner;
    private readonly IServiceControl? _services;
    private readonly TakeoverStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WriterGuard(ProcessScanner scanner)
        : this(scanner, null, new TakeoverStore((string?)null))
    {
    }

    public WriterGuard(ProcessScanner scanner, IServiceControl? services, TakeoverStore store)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _services = services;
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public GuardVerdict Check(DeviceClass deviceClass)
    {
        if (deviceClass == DeviceClass.Virtual)
        {
            return GuardVerdict.Clear;
        }

        if (!Enum.IsDefined(deviceClass))
        {
            return GuardVerdict.Blocked("unknown device class");
        }

        IReadOnlyList<DetectedWriter> found;
        try
        {
            found = _scanner.ScanFor(deviceClass);
        }
#pragma warning disable CA1031 // fail closed: if we cannot tell, we do not write
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return GuardVerdict.Blocked($"writer scan failed: {ex.Message}");
        }

        return found.Count == 0 ? GuardVerdict.Clear : GuardVerdict.Blocked(found[0].Writer.Name);
    }

    public bool IsTakeoverEnabled(DeviceClass deviceClass)
    {
        try
        {
            return _store.Load().Enabled.Contains(deviceClass.ToString(), StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidDataException)
        {
            return false; // corrupt settings: default policy (Refuse)
        }
    }

    public async Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct)
    {
        RequireHardwareClass(deviceClass);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _store.Load();
            var set = s.Enabled.Where(c => !string.Equals(c, deviceClass.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (enabled)
            {
                set.Add(deviceClass.ToString());
            }

            _store.Save(s with { Enabled = set });
            if (!enabled)
            {
                var still = set.Select(c => Enum.TryParse<DeviceClass>(c, true, out var v) ? v : (DeviceClass?)null).OfType<DeviceClass>().ToArray();
                var error = await Task.Run(() => RestoreWhere(name => !NeededBy(name, still)), ct).ConfigureAwait(false);
                return new TakeoverResult(deviceClass, false, [], [], error);
            }
        }
        finally
        {
            _gate.Release();
        }

        return await TakeoverAsync(deviceClass, ct).ConfigureAwait(false);
    }

    public async Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct)
    {
        RequireHardwareClass(deviceClass);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsTakeoverEnabled(deviceClass))
            {
                return new TakeoverResult(deviceClass, false, [], [], "takeover is off for this device class");
            }

            return await Task.Run(() => DoTakeover(deviceClass), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var error = await Task.Run(() => RestoreWhere(_ => true), ct).ConfigureAwait(false);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<WriterCandidate> RunningCandidates()
    {
        try
        {
            return _scanner.ScanCandidates();
        }
#pragma warning disable CA1031 // informational only: a failed scan shows no candidates and changes no verdict
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    public void Dispose() => _gate.Dispose();

    private static void RequireHardwareClass(DeviceClass c)
    {
        if (c == DeviceClass.Virtual || !Enum.IsDefined(c))
        {
            throw new ArgumentOutOfRangeException(nameof(c), c, "Takeover applies to hardware device classes only.");
        }
    }

    private static bool NeededBy(string service, IReadOnlyCollection<DeviceClass> classes) =>
        KnownWriters.All.Any(w => string.Equals(w.ScmName, service, StringComparison.OrdinalIgnoreCase) && w.Affects.Any(classes.Contains));

    private TakeoverResult DoTakeover(DeviceClass deviceClass)
    {
        var found = _scanner.ScanFor(deviceClass);
        var serviceWriters = found.Where(f => f.FoundAs == WriterKind.Service).Select(f => f.Writer)
            .Distinct()
            .OrderBy(w => w.ScmName.Contains("Watcher", StringComparison.OrdinalIgnoreCase) ? 0 : 1) // a watcher would restart its service
            .ToArray();
        var serviceNames = serviceWriters.Select(w => w.ScmName).ToArray();
        var stopped = new List<string>();
        string? error = null;
        if (serviceNames.Length > 0 && _services is null)
        {
            error = "service control unavailable";
        }
        else
        {
            foreach (var name in serviceNames)
            {
                try
                {
                    var info = _services!.Query(name);
                    if (info is null)
                    {
                        continue;
                    }

                    // bookkeeping first: if we crash after this line, the record still says how to restore
                    var s = _store.Load();
                    if (!s.Stopped.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _store.Save(s with { Stopped = [.. s.Stopped, new StoppedService(info.Name, info.StartType, info.Running)] });
                    }

                    _services.SetStartType(name, "Disabled");
                    _services.Stop(name, ServiceTimeout);
                    stopped.Add(name);
                }
#pragma warning disable CA1031 // report and continue: remaining writers keep the class Blocked
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    error = $"{name}: {ex.Message}";
                }
            }
        }

        var processes = found.Where(f => f.FoundAs == WriterKind.Process)
            .Where(f => !serviceWriters.Contains(f.Writer)) // a service's own process exits with it (names may differ)
            .Select(f => f.Writer.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TakeoverResult(deviceClass, true, stopped, processes, error);
    }

    /// <returns>Null on success, otherwise the failures (their records are kept for the next attempt).</returns>
    private string? RestoreWhere(Func<string, bool> select)
    {
        TakeoverState s;
        try
        {
            s = _store.Load();
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }

        if (s.Stopped.Count == 0)
        {
            return null;
        }

        var keep = new List<StoppedService>();
        var errors = new List<string>();
        foreach (var r in s.Stopped.Reverse())
        {
            if (!select(r.Name))
            {
                keep.Insert(0, r);
                continue;
            }

            try
            {
                if (_services is null)
                {
                    throw new InvalidOperationException("service control unavailable");
                }

                _services.SetStartType(r.Name, r.StartType);
                if (r.WasRunning)
                {
                    _services.Start(r.Name, ServiceTimeout);
                }
            }
#pragma warning disable CA1031 // keep the record and report; never lose restore information
            catch (Exception ex)
#pragma warning restore CA1031
            {
                keep.Insert(0, r);
                errors.Add($"{r.Name}: {ex.Message}");
            }
        }

        _store.Save(s with { Stopped = keep });
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }
}
