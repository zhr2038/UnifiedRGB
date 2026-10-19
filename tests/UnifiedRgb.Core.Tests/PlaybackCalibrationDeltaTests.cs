using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

public sealed class PlaybackCalibrationDeltaTests
{
    [Theory]
    [InlineData(2.0, true)]
    [InlineData(0.5, true)]
    [InlineData(2.0, false)]
    [InlineData(0.5, false)]
    public void A_live_generator_observes_one_coherent_time_domain(double rate, bool mixed)
    {
        var probe = new DeltaProbe();
        using var bus = new FrameBus();
        using var loop = new RenderLoop(new EffectRegistry([probe]), bus);
        var gpu = TestSupport.Controller("gpu", ZoneLayout.Point);
        var chain = new ControllerDescriptor(new ControllerId("rf"), "test", "rf", DeviceType.Cooler,
            DeviceClass.Virtual, "rf", [new ZoneDescriptor(new ZoneId(0), "TL", ZoneLayout.Matrix(13, 2))
            { Family = "TL", Units = 1 }], Capabilities.UploadedLoopOnly(200, 20, 12288));
        loop.SetControllers(mixed ? [chain, gpu] : [gpu]);
        var target = new ZoneRef(gpu.Id, new ZoneId(0));
        ZoneRef[] targets = mixed ? [new ZoneRef(chain.Id, new ZoneId(0)), target] : [target];
        loop.SetScene(new Scene([Assignment.ForGroup("probe", targets, probe.Id,
            new EffectParams([Rgb.White])) with { Strategy = ContentStrategy.Program }]));
        if (mixed)
        {
            bus[chain.Id]!.TryConsumeLoop(out var uploaded).Should().BeTrue();
            loop.AnchorLoop(uploaded.Loop.EffectIndex, 0.25);
        }

        loop.SetPlaybackCalibration(new PlaybackCalibration(rate, 125));
        loop.RenderFrame(0.9, 0.02);
        probe.T.Should().BeApproximately(mixed ? (0.9 - 0.25 - 0.125) * rate : 0.9, 1e-12);
        probe.Dt.Should().BeApproximately(mixed ? 0.02 * rate : 0.02, 1e-12,
            "the shared generator's logical delta and T must scale together; native-only content keeps master time");
    }

    private sealed class DeltaProbe() : EffectGenerator("DeltaProbe",
        new EffectParamSchema("probe", "probe", 1, 1, true, false, [Rgb.White])), ILoopable
    {
        public double T { get; private set; }
        public double Dt { get; private set; }
        public double LoopSeconds(in EffectParams p) => 2;
        public override void Render(in RenderContext ctx, Span<Rgb> output)
        {
            T = ctx.T;
            Dt = ctx.Dt;
            output.Fill(Rgb.Black);
        }
    }
}
