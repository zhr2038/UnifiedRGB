using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>
/// Chinese per-key readiness text and the App's admission before any request that would make a per-key zone output. A Ready
/// zone is available for ordinary scenes on the shared timeline (its key count is shown); a state that is not Ready needs
/// calibration; only an actual restore failure blocks a Ready map. Aggregate (whole-keyboard) colour is never presented as
/// per-key, and nothing here claims optical acceptance.
/// </summary>
public static class PerKeyDisplay
{
    public const string NotPerKey = "—";

    /// <summary>The first per-key status of a controller's zones, or <see cref="NotPerKey"/>.</summary>
    public static string Controller(ControllerDto controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.Zones.FirstOrDefault(z => z.PerKey is not null) is { } zone ? Zone(zone) : NotPerKey;
    }

    public static string Zone(ZoneDto zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.PerKey is not { } status) return NotPerKey;
        return status.State switch
        {
            PerKeyAdmission.ReadyState => PerKeyAdmission.EvaluateStatus(status).Verdict switch
            {
                PerKeyAdmissionVerdict.RestorationFailed => "逐键不可用 · 原始灯光恢复失败，不发送逐键输出",
                _ when PerKeyAdmission.IsAggregate(zone) => "逐键未启用 · 当前分区仍为整键盘单色",
                _ => $"逐灯可用 · {status.KeyCount} 灯位",
            },
            "Disabled" => "逐键未启用 · 整键盘单色是独立能力",
            "MissingMap" => "逐键需校准 · 缺少本机物理映射",
            "InvalidMap" => "逐键需校准 · 映射无效",
            "TransportMismatch" => "逐键需校准 · 当前路由没有对应映射",
            "Unverified" => "逐键需校准 · 待物理逐键扫描",
            "ControlUnverified" => "逐键需校准 · 灯光控制待校验",
            "KeyOrderUnverified" => "逐键需校准 · 键位顺序待校验",
            _ => "不支持逐键 · 整体颜色不代表逐键能力",
        };
    }

    /// <summary>The Chinese name of a <see cref="PerKeyRestorationContract"/> diagnostic component; an unknown word is shown as it is.</summary>
    public static string Component(string component) => component switch
    {
        PerKeyRestorationContract.Effect => "原始效果",
        PerKeyRestorationContract.InactiveStaticBank => "非活动静态色库",
        PerKeyRestorationContract.Brightness => "亮度",
        PerKeyRestorationContract.Profile => "配置文件",
        PerKeyRestorationContract.RfScope => "2.4G 范围",
        _ => component,
    };

    /// <summary>
    /// Throws before any request when a target is a per-key zone that is not admitted (not Ready, or an actual restore failure);
    /// targets the list does not know are left to the Host. Called with the controllers the request was built from, so nothing
    /// is read or sent on refusal.
    /// </summary>
    public static void RequireAdmitted(IEnumerable<ZoneRefDto> targets, IEnumerable<ControllerDto> controllers)
    {
        var blocked = PerKeyAdmission.Blocked(targets, controllers);
        if (blocked.Count == 0) return;
        throw new InvalidOperationException("逐键输出未准许，未发送任何请求；草稿、当前场景与亮度保持不变。\n"
            + string.Join("\n", blocked.Select(b => $"{DisplayNames.Zone(b.Controller, b.Zone)}：{Zone(b.Zone)}")));
    }

    /// <summary>
    /// The Host applies a saved scene by name itself: its stored targets and the controllers are read first, so a refused scene
    /// sends no apply request. The Host loads the same stored profile for the apply.
    /// </summary>
    public static async Task RequireProfileAdmittedAsync(IRgbService service, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var profile = await service.LoadProfileAsync(name, ct).ConfigureAwait(false);
        RequireAdmitted(profile.Scene.Assignments.SelectMany(a => a.Targets), await service.ListControllersAsync(ct).ConfigureAwait(false));
    }
}
