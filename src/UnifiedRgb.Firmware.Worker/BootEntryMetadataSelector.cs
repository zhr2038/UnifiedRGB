using System.Text.Json;
using BootReadOnly;

namespace UnifiedRgb.Firmware.Worker;

/// <summary>Pure metadata selection: choose the vendor role, but NEVER filter evidence supplied to R6.
/// Readable keyboard collections are not vendor candidates; only R6's positive same-physical OS-mouse
/// exclusion may account for an uninspectable collection. No device APIs or report ownership here.</summary>
public static class BootEntryMetadataSelector
{
    public static BootObservation? Select(IReadOnlyList<Collection> rows, string normalPhysicalLocation, DateTimeOffset observedAtUtc)
    {
        if (rows.Count == 0) return null;
        var eligible = rows.Where(row => row.Eligible).ToArray();
        if (eligible.Length != 1) throw new InvalidDataException("Exactly one eligible boot vendor collection required");
        var boot = eligible[0];
        if (!boot.LocationPaths.Contains(normalPhysicalLocation, StringComparer.Ordinal))
            throw new InvalidDataException("Boot vendor collection is on a different physical port");
        var pin = new Pin { HidPath = boot.Path, HidInstanceId = boot.InstanceId, UsbAncestor = boot.UsbAncestor,
            ContainerId = boot.ContainerId, PhysicalLocation = normalPhysicalLocation, CapsSha256 = boot.CapsSha256 };
        _ = Inventory.Select(rows, pin); // FULL rows: preserve unchanged R6 positive mouse exclusion and all error checks
        if (rows.Any(row => row.Vid != 0x0B05 || row.Pid != 0x1C26 ||
            !row.UsbAncestor.Equals(boot.UsbAncestor, StringComparison.OrdinalIgnoreCase) ||
            !row.ContainerId.Equals(boot.ContainerId, StringComparison.OrdinalIgnoreCase) ||
            !row.LocationPaths.Contains(normalPhysicalLocation, StringComparer.Ordinal)))
            throw new InvalidDataException("Foreign/incomplete boot collection physical identity; no aliases");
        return new(boot.Path, boot.InstanceId, boot.UsbAncestor, boot.ContainerId, normalPhysicalLocation,
            boot.CapsSha256, "02000200", observedAtUtc, JsonSerializer.SerializeToUtf8Bytes(new { at = observedAtUtc, rows }), true, true);
    }
}
