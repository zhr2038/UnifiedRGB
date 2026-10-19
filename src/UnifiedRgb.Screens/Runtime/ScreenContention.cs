using System.Diagnostics;
using System.ComponentModel;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Who else owns a family's screens right now; null when nobody does.</summary>
public interface IScreenContentionProbe
{
    string? BlockedBy(ScreenFamily family);
}

/// <summary>
/// Read-only process-name check (no handle to the process, nothing stopped). L-Connect drives the TL-LCD and H2 screens; TURZX.exe
/// drives the case screen. A running owner blocks its family: our Host never takes a screen over automatically. Results are cached
/// for one second so several workers share one process scan.
/// </summary>
public sealed class VendorProcessProbe(TimeProvider time) : IScreenContentionProbe
{
    public static IReadOnlyList<string> LianLiScreenOwners { get; } = ["L-Connect 3", "L-Connect-Service", "L-Connect-Service-Watcher"];

    public static IReadOnlyList<string> TurzxScreenOwners { get; } = ["TURZX"];

    private readonly object _gate = new();
    private HashSet<string>? _running;
    private long _scannedAt;

    public VendorProcessProbe()
        : this(TimeProvider.System)
    {
    }

    public static IReadOnlyList<string> OwnersOf(ScreenFamily family) =>
        family == ScreenFamily.Turzx ? TurzxScreenOwners : LianLiScreenOwners;

    public string? BlockedBy(ScreenFamily family)
    {
        HashSet<string> running;
        lock (_gate)
        {
            if (_running is null || time.GetElapsedTime(_scannedAt) > TimeSpan.FromSeconds(1))
            {
                _running = Scan();
                _scannedAt = time.GetTimestamp();
            }

            running = _running;
        }

        var hits = OwnersOf(family).Where(running.Contains).ToArray();
        return hits.Length == 0 ? null : string.Join(", ", hits);
    }

    private static HashSet<string> Scan()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { names.Add(p.ProcessName); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception { NativeErrorCode: 87 or 1168 })
                {
                    // A process may exit between enumeration and querying its name. Other names remain authoritative.
                }
            }
        }

        return names;
    }
}
