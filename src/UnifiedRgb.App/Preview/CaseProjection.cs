using System.Text.Json;
using System.Windows;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Preview;

/// <summary>Illustrative physical structure. Never writes or replaces native-index/mounting calibration.</summary>
public static class CaseProjection
{
    private static readonly Dictionary<string, double[][]> Points = ReadGeometry();
    private static Dictionary<string, double[][]> ReadGeometry()
    {
        using var stream = typeof(CaseProjection).Assembly.GetManifestResourceStream("UnifiedRgb.CaseGeometry.json")
            ?? throw new InvalidOperationException("Case geometry resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, double[][]>>(stream) ?? [];
    }

    public static string? Slot(ZoneRefDto? zone, string role)
    {
        var id = zone?.ControllerId.ToUpperInvariant() ?? "";
        if (id.Contains("1B-7B-F1", StringComparison.Ordinal)) return "top-fans";
        if (id.Contains("06-A8-F0", StringComparison.Ordinal)) return "side-fans";
        if (id.Contains("35-F7-EF", StringComparison.Ordinal)) return "bottom-fans";
        if (id.Contains("EF-7E-63", StringComparison.Ordinal)) return "waterblock-ring";
        if (id.StartsWith("MSIGPU:", StringComparison.Ordinal)) return zone?.ZoneId == 0 ? "gpu-front" : "gpu-top";
        if (id.StartsWith("ASUSAURA:", StringComparison.Ordinal)) return "motherboard-logo";
        if (id.StartsWith("ENEDRAM:", StringComparison.Ordinal)) return id.EndsWith("0X71", StringComparison.Ordinal) ? "ram-inner" : "ram-outer";
        return Points.ContainsKey(role) ? role : null;
    }

    public static Point[] LightingPoints(string slot, int count)
    {
        if (!Points.TryGetValue(slot, out var source) || count < 1) return [];
        return Enumerable.Range(0, count).Select(i =>
        {
            var index = count == 1 ? 0 : (int)Math.Round(i * (source.Length - 1.0) / (count - 1));
            return new Point(source[index][0] * 776 / 760, source[index][1] * 420 / 430);
        }).ToArray();
    }

    public static (Rect Bounds, bool Circle)? ScreenSlot(string role, string? screenId)
    {
        if (role == "main-panel" || screenId?.Contains(":turzx:", StringComparison.OrdinalIgnoreCase) == true) return (new(334 * 776.0 / 760, 105 * 420.0 / 430, 168 * 776.0 / 760, 41 * 420.0 / 430), false);
        if (role == "waterblock-face" || screenId?.Contains(":h2:", StringComparison.OrdinalIgnoreCase) == true) return (new((263 - 33) * 776.0 / 760, (174 - 33) * 420.0 / 430, 66 * 776.0 / 760, 66 * 420.0 / 430), true);
        var x = role switch { "bottom-left" => 209, "bottom-middle" => 327, "bottom-right" => 445, _ => -1 };
        return x < 0 ? null : (new((x - 22) * 776.0 / 760, 332 * 420.0 / 430, 44 * 776.0 / 760, 44 * 420.0 / 430), true);
    }
}
