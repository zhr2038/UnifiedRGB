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

public sealed class WindowsSystemSnapshot : ISystemSnapshot
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
}
