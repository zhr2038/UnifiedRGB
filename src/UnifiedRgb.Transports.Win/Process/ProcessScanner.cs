using System.Diagnostics;
using System.ServiceProcess;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Process;

public sealed record ProcessInfo(int Pid, string Name);

public sealed record ServiceInfo(string Name, bool Running, string StartType);

/// <summary>Snapshot source so WriterGuard can be tested without real vendor processes.</summary>
public interface ISystemSnapshot
{
    IReadOnlyList<ProcessInfo> Processes();

    IReadOnlyList<ServiceInfo> Services();
}

/// <summary>Optional snapshot invalidation hook after local SCM mutations.</summary>
public interface IInvalidatableSystemSnapshot
{
    void Invalidate();
}

/// <summary>Caches process and service enumeration for a short TTL so multi-class guard polls avoid repeated full scans.</summary>
public sealed class CachedSystemSnapshot : ISystemSnapshot, IInvalidatableSystemSnapshot
{
    private readonly ISystemSnapshot _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private IReadOnlyList<ProcessInfo>? _processes;
    private long _processesAt;
    private IReadOnlyList<ServiceInfo>? _services;
    private long _servicesAt;

    public CachedSystemSnapshot(ISystemSnapshot inner, TimeSpan? ttl = null, TimeProvider? time = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl ?? TimeSpan.FromSeconds(1);
        if (_ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<ProcessInfo> Processes()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_processes is not null && _time.GetElapsedTime(_processesAt, now) < _ttl)
            {
                return _processes;
            }
        }

        var fresh = _inner.Processes();
        lock (_gate)
        {
            _processes = fresh;
            _processesAt = _time.GetTimestamp();
            return fresh;
        }
    }

    public IReadOnlyList<ServiceInfo> Services()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_services is not null && _time.GetElapsedTime(_servicesAt, now) < _ttl)
            {
                return _services;
            }
        }

        var fresh = _inner.Services();
        lock (_gate)
        {
            _services = fresh;
            _servicesAt = _time.GetTimestamp();
            return fresh;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _processes = null;
            _services = null;
        }
    }
}

public sealed class WindowsSystemSnapshot : ISystemSnapshot, IInvalidatableSystemSnapshot
{
    private readonly CachedSystemSnapshot _cache;

    public WindowsSystemSnapshot()
        : this(TimeSpan.FromSeconds(1))
    {
    }

    public WindowsSystemSnapshot(TimeSpan cacheTtl, TimeProvider? time = null)
    {
        _cache = new CachedSystemSnapshot(new LiveSnapshot(), cacheTtl, time);
    }

    public IReadOnlyList<ProcessInfo> Processes() => _cache.Processes();

    public IReadOnlyList<ServiceInfo> Services() => _cache.Services();

    public void Invalidate() => _cache.Invalidate();

    private sealed class LiveSnapshot : ISystemSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes()
        {
            var all = System.Diagnostics.Process.GetProcesses();
            var list = new List<ProcessInfo>(all.Length);
            foreach (var p in all)
            {
                using (p)
                {
                    try
                    {
                        list.Add(new ProcessInfo(p.Id, p.ProcessName));
                    }
                    catch (InvalidOperationException)
                    {
                        // exited while enumerating
                    }
                }
            }

            return list;
        }

        public IReadOnlyList<ServiceInfo> Services()
        {
            var all = ServiceController.GetServices();
            var list = new List<ServiceInfo>(all.Length);
            foreach (var s in all)
            {
                using (s)
                {
                    try
                    {
                        list.Add(new ServiceInfo(s.ServiceName, s.Status == ServiceControllerStatus.Running, s.StartType.ToString()));
                    }
                    catch (InvalidOperationException)
                    {
                        // service removed while enumerating
                    }
                }
            }

            return list;
        }
    }
}

public sealed record DetectedWriter(KnownWriter Writer, WriterKind FoundAs, int? Pid);

/// <summary>Finds known lighting writers among running processes and services (read-only).</summary>
public sealed class ProcessScanner
{
    private readonly ISystemSnapshot _snapshot;
    private readonly IReadOnlyList<KnownWriter> _writers;
    private readonly HashSet<int> _ownedPids = [];
    private readonly object _gate = new();

    public ProcessScanner(ISystemSnapshot snapshot, IReadOnlyList<KnownWriter>? writers = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _writers = writers ?? KnownWriters.All;
    }

    /// <summary>Marks a PID as ours (e.g. the supervised headless OpenRGB) so it is not reported.</summary>
    public void RegisterOwnedProcess(int pid)
    {
        lock (_gate)
        {
            _ownedPids.Add(pid);
        }
    }

    public void UnregisterOwnedProcess(int pid)
    {
        lock (_gate)
        {
            _ownedPids.Remove(pid);
        }
    }

    public IReadOnlyList<DetectedWriter> Scan()
    {
        var found = new List<DetectedWriter>();
        var procs = _snapshot.Processes();
        var services = _snapshot.Services();
        int[] owned;
        lock (_gate)
        {
            owned = [.. _ownedPids];
        }

        foreach (var w in _writers)
        {
            if (w.Kind.HasFlag(WriterKind.Process))
            {
                foreach (var p in procs)
                {
                    if (string.Equals(p.Name, w.Name, StringComparison.OrdinalIgnoreCase) && Array.IndexOf(owned, p.Pid) < 0)
                    {
                        found.Add(new DetectedWriter(w, WriterKind.Process, p.Pid));
                    }
                }
            }

            if (w.Kind.HasFlag(WriterKind.Service))
            {
                foreach (var s in services)
                {
                    if (s.Running && string.Equals(s.Name, w.ScmName, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new DetectedWriter(w, WriterKind.Service, null));
                    }
                }
            }
        }

        return found;
    }

    public IReadOnlyList<DetectedWriter> ScanFor(DeviceClass cls) =>
        Scan().Where(d => d.Writer.Affects.Contains(cls)).ToArray();

    /// <summary>
    /// Running processes or services named in <see cref="KnownWriters.Candidates"/> (NATIVE design §8): shown as "running, not a
    /// confirmed writer"; never part of a verdict and never stopped.
    /// </summary>
    public IReadOnlyList<WriterCandidate> ScanCandidates()
    {
        var procs = _snapshot.Processes();
        var services = _snapshot.Services();
        return KnownWriters.Candidates
            .Where(c => procs.Any(p => string.Equals(p.Name, c.Name, StringComparison.OrdinalIgnoreCase))
                || services.Any(s => s.Running && string.Equals(s.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public void InvalidateSnapshot() => (_snapshot as IInvalidatableSystemSnapshot)?.Invalidate();
}
