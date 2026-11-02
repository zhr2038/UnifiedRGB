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
            // Finish the complete enumeration before inspecting paths; never infer absence from a partial result.
            var snapshot = enumerateComplete().ToArray();
            return snapshot.All(c => !string.Equals(c.Path, capturedPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
}
