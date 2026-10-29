using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>
/// Chinese per-key readiness text and the App's admission before any request that would make a per-key zone output. A Ready
/// map is not a working keyboard: without the complete, verified original-lighting restoration capability (effect, inactive
/// static bank, brightness, profile, 2.4G scope) the request is refused before it is sent, the draft and the running scene stay
/// as they are, and aggregate (whole-keyboard) colour is never presented as per-key.
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
                PerKeyAdmissionVerdict.Admitted when PerKeyAdmission.IsAggregate(zone) => "逐键未启用 · 当前分区仍为整键盘单色",
                PerKeyAdmissionVerdict.Admitted => $"逐键已准许 · {status.KeyCount} 键（实机效果未验收）",
                PerKeyAdmissionVerdict.RestorationFailed => "逐键未准许 · 原始灯光快照或恢复预检失败，不发送逐键输出",
                PerKeyAdmissionVerdict.RestorationIncomplete => $"逐键未准许 · 原始灯光完整恢复未证实（待证实：{Unproven(status)}），不发送逐键输出",
                PerKeyAdmissionVerdict.RestorationMismatch => "逐键未准许 · 原始灯光恢复能力与当前传输、版本或设备范围不一致，不发送逐键输出",
                _ => "逐键未准许 · 映射门控已就绪，但原始灯光完整恢复未证实，不发送逐键输出",
            },
            "Disabled" => "逐键未启用 · 整键盘单色是独立能力",
            "MissingMap" => "逐键不可用 · 缺少本机物理映射",
            "InvalidMap" => "逐键不可用 · 映射无效",
            "TransportMismatch" => "逐键不可用 · 映射与传输不匹配",
            "Unverified" => "逐键不可用 · 待物理逐键扫描",
            "ControlUnverified" => "逐键不可用 · 灯光控制待校验",
            "KeyOrderUnverified" => "逐键不可用 · 键位顺序待校验",
            _ => "不支持逐键 · 整体颜色不代表逐键能力",
        };
    }

    /// <summary>The Chinese name of a <see cref="PerKeyRestorationContract"/> component; an unknown word is shown as it is.</summary>
    public static string Component(string component) => component switch
    {
        PerKeyRestorationContract.Effect => "原始效果",
        PerKeyRestorationContract.InactiveStaticBank => "非活动静态色库",
        PerKeyRestorationContract.Brightness => "亮度",
        PerKeyRestorationContract.Profile => "配置文件",
        PerKeyRestorationContract.RfScope => "2.4G 范围",
        _ => component,
    };

    private static string Unproven(PerKeyStatusDto status) =>
        PerKeyAdmission.UnprovenComponents(status) is { Count: > 0 } names ? string.Join("、", names.Select(Component)) : "恢复能力整体状态";

    /// <summary>
    /// Throws before any request when a target is a per-key zone that is not admitted; targets the list does not know are left
    /// to the Host. Called with the controllers the request was built from, so nothing is read or sent on refusal.
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
