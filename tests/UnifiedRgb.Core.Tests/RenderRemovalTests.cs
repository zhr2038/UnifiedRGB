using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

public sealed class RenderRemovalTests
{
    [Fact]
    public void A_retired_controller_cannot_crash_a_still_active_render_plan_or_stop_its_other_controllers()
    {
        var gpu = TestSupport.Controller("gpu:1", ZoneLayout.Point);
        var fan = TestSupport.Controller("fan", ZoneLayout.Point);
        using var bus = new FrameBus();
        using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([gpu, fan]);
        render.SetScene(new Scene([Assignment.ForGroup("all", [new(gpu.Id, new(0)), new(fan.Id, new(0))],
            StaticColorEffect.EffectId, new EffectParams([new(200, 60, 20)]))]));
        bus.Unregister(gpu.Id); // adapter teardown happens before the next discovery plan is published
        for (var i = 0; i < 100; i++) render.RenderFrame(i / 60.0, 1 / 60.0);
        bus[fan.Id]!.Published.Should().Be(100);
        bus[fan.Id]!.TryConsume(out var frame).Should().BeTrue();
        frame.Zones[0][0].Should().Be(new Rgb(200, 60, 20));
    }

    [Fact]
    public void A_reconnected_id_cannot_receive_the_previous_mailbox_frame_before_recompilation()
    {
        var gpu = TestSupport.Controller("gpu:1", ZoneLayout.Point);
        using var bus = new FrameBus();
        using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([gpu]);
        render.SetScene(new Scene([Assignment.ForZone(new(gpu.Id, new(0)), StaticColorEffect.EffectId)]));
        bus.Unregister(gpu.Id);
        var replacement = bus.Register(gpu);
        for (var i = 0; i < 100; i++) render.RenderFrame(i / 60.0, 1 / 60.0);
        replacement.Published.Should().Be(0, "old render-plan buffers belong to the retired mailbox");
        render.SetControllers([gpu]);
        render.RenderFrame(2, 1 / 60.0);
        replacement.Published.Should().Be(1);
    }

    [Fact]
    public void A_retired_uploaded_controller_is_not_reported_as_a_successful_loop_publication()
    {
        var chain = new ControllerDescriptor(new("chain"), "Test", "chain", DeviceType.Cooler, DeviceClass.Virtual, "chain",
            [new(new(0), "ring", ZoneLayout.Ring(24))], Capabilities.UploadedLoopOnly(200, 20, 12288));
        using var bus = new FrameBus();
        using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([chain]);
        render.SetScene(new Scene([Assignment.ForZone(new(chain.Id, new(0)), StaticColorEffect.EffectId)]));
        bus.Unregister(chain.Id);
        var replacement = bus.Register(chain);
        render.RepublishLoops()!.Published.Should().Be(0);
        replacement.LoopsPublished.Should().Be(0);
        render.SetControllers([chain]);
        replacement.LoopsPublished.Should().BeGreaterThan(0);
    }
}
