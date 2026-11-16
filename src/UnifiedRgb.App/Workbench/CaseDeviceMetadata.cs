using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

internal enum CaseSlotKind
{
    Unknown,
    TopFans,
    SideFans,
    BottomFans,
    WaterblockRing,
    GpuFront,
    GpuTop,
    MotherboardLogo,
    RamInner,
    RamOuter,
}

/// <summary>
/// Centralized descriptor-first metadata and minimal hardware fallback mapping shared across
/// display labels, case topology rendering, and package geometry projection.
/// </summary>
internal static class CaseDeviceMetadata
{
    private readonly record struct SlotBinding(string IdToken, CaseSlotKind Slot, string ProjectionKey, string DisplayName);

    // Minimal fallback bindings where Host descriptors alone cannot distinguish physical case placement.
    private static readonly SlotBinding[] KnownSlotBindings =
    [
        new("1B-7B-F1", CaseSlotKind.TopFans, "top-fans", "顶排风扇 · TL120 × 3"),
        new("06-A8-F0", CaseSlotKind.SideFans, "side-fans", "侧排风扇 · TL140 × 3"),
        new("35-F7-EF", CaseSlotKind.BottomFans, "bottom-fans", "底排风扇 · TL140 LCD × 3"),
        new("EF-7E-63", CaseSlotKind.WaterblockRing, "waterblock-ring", "水冷头 · H2 灯环"),
        new(":0X71", CaseSlotKind.RamInner, "ram-inner", "内存 · 靠近冷头"),
        new(":0X73", CaseSlotKind.RamOuter, "ram-outer", "内存 · 外侧"),
    ];

    public static bool IsDiscoveryPort(ControllerDto controller) =>
        controller.Zones.Count == 0
        && (controller.Id is "enedram:p0" or "enedram:p1"
            || string.Equals(controller.DeviceClass, "AsusRog", StringComparison.OrdinalIgnoreCase));

    public static bool IsDiscoveryPortId(string id) =>
        id is "enedram:p0" or "enedram:p1"
        || (id.StartsWith("asusr:", StringComparison.OrdinalIgnoreCase)
            && id.EndsWith(":receiver", StringComparison.OrdinalIgnoreCase));

    public static bool IsKeyboardDevice(DeviceStatusDto device, IReadOnlyList<ControllerDto>? controllers = null)
    {
        if (IsDiscoveryPortId(device.Id)) return false;
        var controller = controllers?.FirstOrDefault(c => string.Equals(c.Id, device.Id, StringComparison.OrdinalIgnoreCase));
        if (controller is not null)
        {
            return string.Equals(controller.DeviceType, "Keyboard", StringComparison.OrdinalIgnoreCase);
        }

        if (device.Name.Contains("Harpe", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("Mouse", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("鼠标", StringComparison.Ordinal))
        {
            return false;
        }

        return device.Name.Contains("夜魔", StringComparison.Ordinal)
            || device.Name.Contains("Azoth", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)
            || (device.Id.StartsWith("asusr:", StringComparison.OrdinalIgnoreCase)
                && !device.Id.EndsWith(":1", StringComparison.Ordinal));
    }

    public static string ResolveControllerName(ControllerDto controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return ResolveControllerName(controller.Id, controller.Name, controller.DeviceClass, controller.DeviceType, controller.Zones);
    }

    public static string ResolveControllerName(
        string id,
        string? fallback = null,
        string? deviceClass = null,
        string? deviceType = null,
        IReadOnlyList<ZoneDto>? zones = null)
    {
        var upper = id.ToUpperInvariant();
        if (TryFindKnownBinding(upper, out var binding))
        {
            return binding.DisplayName;
        }

        if (IsDiscoveryPortId(id) && upper.StartsWith("ASUSR:", StringComparison.Ordinal))
        {
            return "ROG 接收器 · 身份探测";
        }

        if (string.Equals(deviceClass, "LianLi", StringComparison.OrdinalIgnoreCase)
            && zones?.Any(z => string.Equals(z.Family, "H2", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(z.Layout, "Ring", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return "水冷头 · H2 灯环";
        }

        if (string.Equals(deviceClass, "MsiGpu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceType, "Gpu", StringComparison.OrdinalIgnoreCase)
            || upper.StartsWith("MSIGPU:", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(fallback) || fallback.Contains("5090", StringComparison.OrdinalIgnoreCase) || upper.StartsWith("MSIGPU:", StringComparison.Ordinal)
                ? "显卡 · MSI RTX 5090"
                : $"显卡 · {fallback}";
        }

        if (string.Equals(deviceClass, "AsusAura", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceType, "Motherboard", StringComparison.OrdinalIgnoreCase)
            || upper.StartsWith("ASUSAURA:", StringComparison.Ordinal))
        {
            return "主板 · ROG 标志";
        }

        if (string.Equals(deviceClass, "AsusRog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceType, "Mouse", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceType, "Keyboard", StringComparison.OrdinalIgnoreCase)
            || upper.StartsWith("ASUSR:", StringComparison.Ordinal))
        {
            if (string.Equals(deviceType, "Mouse", StringComparison.OrdinalIgnoreCase)
                || fallback?.Contains("Harpe", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "鼠标 · ROG 20 周年（本机绑定）";
            }

            if (string.Equals(deviceType, "Keyboard", StringComparison.OrdinalIgnoreCase)
                || upper.StartsWith("ASUSR:", StringComparison.Ordinal))
            {
                return "键盘 · ROG 夜魔 X（本机绑定）";
            }
        }

        return fallback ?? id;
    }

    public static CaseSlotKind ResolveSlot(ControllerDto? controller, ZoneDto? zone, ZoneRefDto? zoneRef = null)
    {
        var id = (controller?.Id ?? zoneRef?.ControllerId ?? string.Empty).ToUpperInvariant();
        var zoneId = zone?.Id ?? zoneRef?.ZoneId ?? 0;

        if (TryFindKnownBinding(id, out var binding))
        {
            return binding.Slot;
        }

        if ((string.Equals(controller?.DeviceClass, "LianLi", StringComparison.OrdinalIgnoreCase) || id.StartsWith("LIANLI:", StringComparison.Ordinal))
            && (string.Equals(zone?.Family, "H2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(zone?.Layout, "Ring", StringComparison.OrdinalIgnoreCase)))
        {
            return CaseSlotKind.WaterblockRing;
        }

        if (string.Equals(controller?.DeviceClass, "MsiGpu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controller?.DeviceType, "Gpu", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("MSIGPU:", StringComparison.Ordinal))
        {
            return zoneId == 0 ? CaseSlotKind.GpuFront : CaseSlotKind.GpuTop;
        }

        if (string.Equals(controller?.DeviceClass, "AsusAura", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controller?.DeviceType, "Motherboard", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("ASUSAURA:", StringComparison.Ordinal))
        {
            return CaseSlotKind.MotherboardLogo;
        }

        if (string.Equals(controller?.DeviceClass, "EneDram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controller?.DeviceType, "Dram", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("ENEDRAM:", StringComparison.Ordinal))
        {
            return IsDiscoveryPortId(id) ? CaseSlotKind.Unknown : CaseSlotKind.RamOuter;
        }

        return CaseSlotKind.Unknown;
    }

    public static string? ResolveProjectionKey(ZoneRefDto? zoneRef, string role, ControllerDto? controller = null, ZoneDto? zone = null)
    {
        var slot = ResolveSlot(controller, zone, zoneRef);
        return slot switch
        {
            CaseSlotKind.TopFans => "top-fans",
            CaseSlotKind.SideFans => "side-fans",
            CaseSlotKind.BottomFans => "bottom-fans",
            CaseSlotKind.WaterblockRing => "waterblock-ring",
            CaseSlotKind.GpuFront => "gpu-front",
            CaseSlotKind.GpuTop => "gpu-top",
            CaseSlotKind.MotherboardLogo => "motherboard-logo",
            CaseSlotKind.RamInner => "ram-inner",
            CaseSlotKind.RamOuter => "ram-outer",
            _ => role,
        };
    }

    private static bool TryFindKnownBinding(string upperId, out SlotBinding binding)
    {
        foreach (var entry in KnownSlotBindings)
        {
            if (upperId.Contains(entry.IdToken, StringComparison.Ordinal))
            {
                binding = entry;
                return true;
            }
        }

        binding = default;
        return false;
    }
}
