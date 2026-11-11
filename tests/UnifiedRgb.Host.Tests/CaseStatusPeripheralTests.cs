using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

public sealed class CaseStatusPeripheralTests
{
    [Theory]
    [InlineData("Mouse", "mouse:rf", "鼠标灯光未连接")]
    [InlineData("Mouse", "mouse:usb", "鼠标灯光未连接")]
    [InlineData("Keyboard", "keyboard:rf", "键盘灯光未连接")]
    [InlineData("Keyboard", "keyboard:usb", "键盘灯光未连接")]
    public void Sole_unavailable_peripheral_is_named_without_relaxing_whole_scene_readiness(string type, string id, string title)
    {
        var scene = new SceneDto([new([new("case:one", 0), new(id, 0)], "StaticColor", ["#000000"], Brightness: 0)]);
        var controllers = new[] { Device("case:one", "Fan", "Active"), Device(id, type, "Offline") };
        var status = CaseStatusSummary.Describe(scene, controllers, new([]), []);
        status.State.Should().Be(CaseStatusState.Error);
        status.Title.Should().Be(title);
        CaseStatusSummary.Describe(scene, [controllers[0], controllers[1] with { State = "Active" }], new([]), []).State
            .Should().Be(CaseStatusState.Blackout);
    }

    [Fact]
    public void Unknown_or_multiple_failures_do_not_get_attributed_to_one_peripheral()
    {
        var scene = new SceneDto([new([new("case:one", 0), new("mouse:rf", 0)], "StaticColor", ["#000000"], Brightness: 0)]);
        CaseStatusSummary.Describe(scene, [Device("mouse:rf", "Mouse", "Offline")], new([]), []).Title.Should().Be("灯光设备待检查");
        CaseStatusSummary.Describe(scene, [Device("mouse:rf", "Mouse", "Active")], new([]), []).State.Should().Be(CaseStatusState.Error);
    }

    private static ControllerDto Device(string id, string type, string state) =>
        new(id, "test", id, "Virtual", type, "", [new(0, "zone", "Linear", 1, 0, 0)], 10, state, null, true);
}
