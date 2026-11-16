using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Positive absence requires a complete fresh path enumeration, never a failed shape or container match.</summary>
internal static class RogHidRoutePresence
{
    internal static bool Probe(IHidReportDevices devices, ushort vid, ushort pid, string capturedPath)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return Probe(() => devices is WinHidReportDevices platform
            ? platform.EnumerateComplete(vid, pid) : devices.Enumerate(vid, pid), capturedPath);
    }

    internal static bool Probe(Func<IReadOnlyList<HidCollectionInfo>> enumerateComplete, string capturedPath)
    {
        if (string.IsNullOrWhiteSpace(capturedPath)) return false;
        try
        {
            // Absence is established only after visiting the entire returned snapshot successfully.
            foreach (var collection in enumerateComplete())
                if (string.Equals(collection.Path, capturedPath, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
}
