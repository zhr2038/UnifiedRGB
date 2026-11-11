namespace UnifiedRgb.App.Workbench;

public sealed record ShellNavigationItem(int Page, string Title, string Icon, bool StartsSection = false);

/// <summary>Navigation order is independent of the stable page IDs used by the workspaces.</summary>
public static class ShellNavigation
{
    public static IReadOnlyList<ShellNavigationItem> Pages { get; } =
    [
        new(0, "控制中心", "\uE80F"),
        new(1, "灯光编排", "\uE790"),
        new(4, "场景库", "\uE8F1"),
        new(2, "机箱屏幕", "\uE7F4"),
        new(7, "键盘与鼠标", "\uE765"),
        new(3, "设备与校准", "\uE772", true),
        new(5, "运行诊断", "\uE9D9"),
        new(6, "偏好设置", "\uE713"),
    ];
}
