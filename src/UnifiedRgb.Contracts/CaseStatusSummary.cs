using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Contracts;

public enum CaseStatusState { Unknown, Reading, Requesting, Blackout, Active, Pending, Error }

public sealed record CaseStatusObservation(CaseStatusState State, string Title, string Detail);

/// <summary>Read-only case observation shared by UI and keyboard output. No transport or control side effects.</summary>
public static class CaseStatusSummary
{
    public static CaseStatusObservation Describe(SceneDto scene, IReadOnlyList<ControllerDto> controllers,
        ScreenSceneDto? screenScene, IReadOnlyList<ScreenDeviceDto>? screens)
    {
        var blackout = IsBlackout(scene);
        var assigned = scene.Assignments.SelectMany(a => a.Targets).Select(t => t.ControllerId).Distinct(StringComparer.Ordinal).ToArray();
        if (assigned.Length == 0) return new(CaseStatusState.Unknown, "没有灯光编排", "不会自动恢复旧场景");
        var unavailable = assigned.Where(id => !controllers.Any(c => c.Id == id && c.State == ControllerStates.Active)).ToArray();
        if (unavailable.Length != 0)
        {
            // Keep the whole-scene readiness verdict strict, including package-bound peripherals. Name a sole peripheral
            // failure so a mouse control-channel fault is not presented as an unspecified case/controller outage.
            var sole = unavailable.Length == 1 ? controllers.FirstOrDefault(c => c.Id == unavailable[0]) : null;
            var device = sole?.DeviceType switch { "Mouse" => "鼠标", "Keyboard" => "键盘", _ => null };
            return new(CaseStatusState.Error, device is null ? "灯光设备待检查" : device + "灯光未连接",
                device is null ? "存在离线或未接管分区" : device + "灯光通道未就绪，场景未确认");
        }
        if (screenScene is null || screens is null) return new(CaseStatusState.Unknown, blackout ? "熄灯模式" : "场景已设置", "屏幕状态未读取");
        // Only configured case screens participate. A future keyboard/control display is explicitly excluded.
        var targets = screenScene.Assignments.Where(a => a.Enabled && a.ScreenId.StartsWith("screen:", StringComparison.Ordinal))
            .Select(a => a.ScreenId).Distinct(StringComparer.Ordinal).ToArray();
        var caseScreens = screens.Where(s => s.Family is "H2" or "TlLcd" or "Turzx").ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (targets.Length == 0) return new(blackout ? CaseStatusState.Blackout : CaseStatusState.Active,
            blackout ? "熄灯模式" : "场景已设置", "未启用机箱屏幕");
        if (targets.Any(id => !caseScreens.ContainsKey(id))) return new(CaseStatusState.Unknown, "屏幕尚未就绪", "配置中的屏幕缺失或未知");
        var selected = targets.Select(id => caseScreens[id]).ToArray();
        if (selected.Any(s => s.LastError is not null || s.State is "Error" or "Blocked" or "Disconnected" or "Unsupported"))
            return new(CaseStatusState.Error, "屏幕设备待检查", "请查看屏幕工作区");
        var expected = blackout ? "Sleeping" : "Running";
        var ready = selected.Count(s => s.State == expected);
        var detail = $"屏幕 {ready}/{selected.Length} 上报{(blackout ? "休眠" : "运行")}";
        return ready == selected.Length ? new(blackout ? CaseStatusState.Blackout : CaseStatusState.Active, blackout ? "熄灯模式" : "场景运行中", detail)
            : new(CaseStatusState.Pending, blackout ? "等待屏幕休眠" : "等待屏幕运行", detail);
    }

    public static bool IsBlackout(SceneDto scene) => scene.Package is null && scene.Assignments.Count > 0
        && scene.Assignments.All(a => a.EffectId == "StaticColor" && a.FrameSetId is null
            && (a.Brightness == 0 || a.Colors.Count > 0 && a.Colors.All(c => c.Equals("#000000", StringComparison.OrdinalIgnoreCase))));
}
