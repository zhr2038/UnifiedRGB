using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Workbench;

public static class DisplayNames
{
    public static string Scene(string name) => name.StartsWith("Accepted full stacking", StringComparison.OrdinalIgnoreCase) ? "全机堆积 · 波浪回退"
        : name == StudioViewModel.BeforeLightsOffProfile ? "熄灯前自动备份"
        : name.StartsWith("Morning 14", StringComparison.OrdinalIgnoreCase) ? "全机堆积 · 3 秒波浪回退"
        : name.StartsWith("Morning 13", StringComparison.OrdinalIgnoreCase) ? "全机堆积 · 快速回退试验"
        : name.StartsWith("Morning 12", StringComparison.OrdinalIgnoreCase) ? "全机堆积 · 波浪回退"
        : name.StartsWith("Morning 11", StringComparison.OrdinalIgnoreCase) ? "全机堆积 · 已验证版本"
        : name.StartsWith("Morning 00", StringComparison.OrdinalIgnoreCase) ? "全机熄灯" : name;
    public static string Controller(string id, string? fallback = null) => id.ToUpperInvariant() switch
    {
        var s when s.Contains("06-A8-F0", StringComparison.Ordinal) => "侧排风扇 · TL140 × 3",
        var s when s.Contains("1B-7B-F1", StringComparison.Ordinal) => "顶排风扇 · TL120 × 3",
        var s when s.Contains("EF-7E-63", StringComparison.Ordinal) => "水冷头 · H2 灯环",
        var s when s.Contains("35-F7-EF", StringComparison.Ordinal) => "底排风扇 · TL140 LCD × 3",
        var s when s.StartsWith("MSIGPU:", StringComparison.Ordinal) => "显卡 · MSI RTX 5090",
        var s when s.StartsWith("ASUSAURA:", StringComparison.Ordinal) => "主板 · ROG 标志",
        var s when s.EndsWith(":0X71", StringComparison.Ordinal) => "内存 · 靠近冷头",
        var s when s.EndsWith(":0X73", StringComparison.Ordinal) => "内存 · 外侧",
        _ => fallback ?? id,
    };

    public static string Zone(ControllerDto c, ZoneDto z) => c.DeviceClass == "MsiGpu"
        ? z.Id == 0 ? "显卡 · 正面双灯条" : "显卡 · 横条与菱形联动"
        : Controller(c.Id, c.Name);

    public static bool IsDiscoveryPort(ControllerDto c) => c.Zones.Count == 0 &&
        (c.Id == "enedram:p0" || c.Id == "enedram:p1");

    public static string State(string? state) => state switch
    {
        "Active" => "运行中", "Blocked" => "未接管", "Contended" => "控制被占用",
        "Offline" => "离线", "BringUp" => "校验中", _ => state ?? "状态未知",
    };

    public static string Strategy(string? id) => id switch
    {
        "Vendor" => "原厂曲线", "Program" => "程序生成", "Frames" => "帧动画", _ => id ?? "原厂曲线",
    };

    public static string Effect(string id) => id switch
    {
        "StaticColor" => "静态光", "Breathing" => "呼吸", "ColorCycle" => "整区换色",
        "Rainbow" => "彩虹", "Runway" => "跑道", "Stacking" => "堆积", "Meteor" => "流星",
        "Wave" => "波浪", "Spectrum" => "光谱", "Taichi" => "太极", "Lottery" => "轮盘",
        "Scan" => "扫描", "DoubleMeteor" => "双流星", "MeteorContest" => "流星竞逐",
        "MeteorMix" => "流星混色", "ReturnArc" => "回旋光弧", "DoubleArc" => "双光弧",
        "HeartBeatRunway" => "心跳跑道", "GradientRibbon" => "渐变光带", "Drumming" => "鼓点",
        "CandyBox" => "糖果盒", "Burst" => "迸发", "Tide" => "潮汐", "Mixing" => "混色",
        "Firework" => "烟花", "Ripple" => "涟漪", "Staggered" => "交错", "Warning" => "警示",
        _ => EffectRegistry.Default.TryGet(id, out var generator) ? generator.Schema.LocalName : id,
    };
}
