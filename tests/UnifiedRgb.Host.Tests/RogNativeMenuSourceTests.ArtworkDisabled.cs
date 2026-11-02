using System.Reflection;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task ArtworkDisabled_control_and_genuine_center_action_and_RGB_continue_without_any_Host_image()
    {
        new KeyboardDisplayOptions().HostArtworkEnabled.Should().BeTrue();
        await using var rig = new NativeIdleMenuRig(false);
        var options = (UnifiedRgbOptions)typeof(KeyboardDisplayService).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Owner)!;
        typeof(KeyboardDisplayOptions).GetProperty(nameof(KeyboardDisplayOptions.HostArtworkEnabled))!.SetValue(options.KeyboardDisplay, false);
        await rig.StartAsync();
        rig.Input.Send(Notice(1, 0, 3)); rig.Input.Send(Notice(2, 2, 1));
        await Until(() => rig.Menu.Trace.Contains("press #2 Toggle accepted"));
        await Until(() => rig.Menu.Trace.Any(e => e.StartsWith("artwork #2", StringComparison.Ordinal)));
        var bridge = (RogWiredLightingBridge)typeof(KeyboardDisplayService).GetField("_lighting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Owner)!;
        await bridge.WriteAsync(new(7, 8, 9), default).WaitAsync(Patience);
        rig.Control.Colours.Should().Contain(new UnifiedRgb.Devices.Rgb(7, 8, 9));
        rig.Control.ImageBegins.Should().Be(0); rig.Control.RejectedImages.Should().Be(0);
        rig.Source.IsPresent.Should().BeTrue(); rig.Control.Arms.Should().Be(1);
        var status = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        status.State.Should().Be(KeyboardDisplayAnimationStates.Fallback); status.Detail.Should().Contain("已暂停").And.Contain("偏好均已保留");
    }
}
