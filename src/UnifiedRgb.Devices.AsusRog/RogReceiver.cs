using System.Security.Cryptography;
using System.Text;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Three vendor collections of ONE physical receiver; never a keyboard/mouse input collection.</summary>
public sealed record RogReceiver(string Key, ushort ProductId, IReadOnlyDictionary<byte, HidCollectionInfo> Channels)
{
    public static IReadOnlyList<RogReceiver> Discover(IHidReportDevices devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return Group(devices.Enumerate(RogProtocol.VendorId, 0x1ACE)
            .Concat(devices.Enumerate(RogProtocol.VendorId, 0x1AD0)));
    }

    public static IReadOnlyList<RogReceiver> Group(IEnumerable<HidCollectionInfo> collections)
    {
        ArgumentNullException.ThrowIfNull(collections);
        var groups = new Dictionary<string, List<(byte Rid, HidCollectionInfo Info)>>(StringComparer.Ordinal);
        foreach (var c in collections)
        {
            if (c.Vid != RogProtocol.VendorId || c.Pid is not (0x1ACE or 0x1AD0) || c.Interface != 2
                || c.Error is not null || c.Usage != 1 || c.FeatureReportLength != 0
                || c.InputReportLength != RogProtocol.ReportLength || c.OutputReportLength != RogProtocol.ReportLength)
                continue;
            var rid = c.UsagePage switch { 0xFF02 => (byte)1, 0xFF00 => (byte)2, 0xFF01 => (byte)3, _ => (byte)0 };
            if (rid == 0 || ParentKey(c.Path) is not { } key) continue;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add((rid, c));
        }
        return groups.OrderBy(g => g.Key, StringComparer.Ordinal)
            .Where(g => g.Value.Select(c => c.Rid).Distinct().Count() == g.Value.Count && g.Value.Any(c => c.Rid == 1))
            .Select(g => new RogReceiver(g.Key, g.Value[0].Info.Pid,
                new System.Collections.ObjectModel.ReadOnlyDictionary<byte, HidCollectionInfo>(g.Value.ToDictionary(c => c.Rid, c => c.Info))))
            .ToArray();
    }

    /// <summary>Opaque route pin, no instance path/serial in UI or logs. Windows HID collection suffix is removed.</summary>
    public static string? ParentKey(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.ToUpperInvariant().Split('#');
        if (parts.Length != 4 || !parts[0].EndsWith("HID", StringComparison.Ordinal)) return null;
        var collection = parts[1].IndexOf("&COL", StringComparison.Ordinal);
        var last = parts[2].LastIndexOf('&');
        if (collection < 0 || last < 1 || parts[2].Length - last != 5) return null;
        var parent = parts[1][..collection] + "#" + parts[2][..last];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parent)));
    }

    public HidCollectionRule Rule(byte reportId)
    {
        var channel = Channels.TryGetValue(reportId, out var c) ? c : throw new InvalidOperationException("ROG paired vendor channel is unavailable.");
        return new HidCollectionRule(channel.Vid, channel.Pid, channel.UsagePage, 1, RogProtocol.ReportLength);
    }
}
