using System.Diagnostics;
using FluentAssertions;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

public sealed class RenderLoopConcurrencyAndWrapTests
{
    private static readonly ControllerDescriptor LoopFan = new(
        new ControllerId("lianli:wrap"), "Loop", "L1", DeviceType.Cooler, DeviceClass.LianLi, "c",
        [new ZoneDescriptor(new ZoneId(0), "ring", ZoneLayout.Ring(12))],
        Capabilities.UploadedLoopOnly(maxLoopFrames: 400, minIntervalMs: 40.0, maxUploadBytes: 4096));

    private static readonly ControllerDescriptor StreamFan = new(
        new ControllerId("virtual:stream"), "Stream", "S1", DeviceType.Cooler, DeviceClass.Virtual, "s",
        [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Linear(4))],
        Capabilities.DirectOnly(60));

    [Fact]
    public void Phase_trim_wraps_inside_32bit_device_clock_modulus()
    {
        var probe = new PhaseRampProbe(loopSeconds: 1.0); // 25 frames @ 40 ms -> RefPeriodTicks = 1600 (2^32 % 1600 = 896 != 0)
        var effects = new EffectRegistry([.. EffectRegistry.CreateBuiltIns(), probe]);

        var nowTicks = 1_000_000_000L;
        using var bus = new FrameBus();
        bus.Register(LoopFan);
        var streamBox = bus.Register(StreamFan);
        using var loop = new RenderLoop(effects, bus) { Timestamp = () => nowTicks };

        var clock = new MutableDeviceClock();
        loop.SetDeviceClocks(new Dictionary<ControllerId, IDeviceClock> { [LoopFan.Id] = clock });
        loop.DeviceClockEnabled.Should().BeTrue();
        loop.SetControllers([LoopFan, StreamFan]);

        var scene = new Scene(
        [
            Assignment.ForGroup(
                "sync",
                [new ZoneRef(LoopFan.Id, new ZoneId(0)), new ZoneRef(StreamFan.Id, new ZoneId(0))],
                probe.Id) with { Strategy = ContentStrategy.Program },
        ]);
        loop.SetScene(scene);

        bus[LoopFan.Id]!.TryConsumeLoop(out var pl).Should().BeTrue();
        pl.Loop.Frames.Should().Be(25);
        loop.ReportLoopTiming(LoopFan.Id, new ZoneId(0), pl.Loop.EffectIndex, pl.Loop.Frames, pl.Loop.IntervalMs, staticFallback: false);

        // RefPeriodTicks = 25 * 40.0 = 1000 (header units / ticks; 2^32 % 1000 = 296 != 0).
        // Positive trim = 150 ms (240 ticks @ 1600 Hz). Samples are 120 ticks apart across 2^32 wrap:
        // Pre-wrap:  raw = 2^32 - 40  -> Wrap(raw - 240, 2^32) = 2^32 - 280 -> % 1000 = 016 (phase 0.016 -> R = 4)
        // Post-wrap: raw = 80         -> Wrap(raw - 240, 2^32) = 2^32 - 160 -> % 1000 = 136 (phase 0.136 -> R = 34)
        // Buggy non-wrapped subtraction would evaluate -160 % 1000 = 840 (phase 0.840 -> R = 210, jumping backwards).
        loop.SetPlaybackCalibration(new PlaybackCalibration(StreamedPlaybackRate: 1.0, PhaseTrimMs: 150.0));
        var domain = new DeviceClockDomain("lianli-rf", 1);

        clock.Latest = new DeviceClockSample(domain, uint.MaxValue - 39u, nowTicks, 1600.0, nowTicks);
        loop.RenderFrame(0.05, 1.0 / 60.0);
        streamBox.TryConsume(out var preWrap).Should().BeTrue();
        var preR = preWrap.Zones[0][0].R;

        clock.Latest = new DeviceClockSample(domain, 80u, nowTicks, 1600.0, nowTicks);
        loop.RenderFrame(0.07, 1.0 / 60.0);
        streamBox.TryConsume(out var postWrap).Should().BeTrue();
        var postR = postWrap.Zones[0][0].R;

        loop.ClockReport().Should().ContainSingle().Which.Mode.Should().Be(StreamedClockMode.DeviceClock);
        preR.Should().BeInRange((byte)2, (byte)8);
        postR.Should().BeInRange((byte)30, (byte)38);
        (postR - preR).Should().BeInRange(25, 35);
    }

    [Fact]
    public async Task SetScene_compile_and_beforePublish_run_outside_RenderFrame_gate()
    {
        using var compileEntered = new ManualResetEventSlim(false);
        using var compileRelease = new ManualResetEventSlim(false);
        var probe = new SlowCompileProbe(loopSeconds: 1.0, onRender: () =>
        {
            compileEntered.Set();
            compileRelease.Wait(TimeSpan.FromSeconds(5));
        });
        var effects = new EffectRegistry([.. EffectRegistry.CreateBuiltIns(), probe]);

        using var bus = new FrameBus();
        bus.Register(LoopFan);
        bus.Register(StreamFan);
        using var loop = new RenderLoop(effects, bus);
        loop.SetControllers([LoopFan, StreamFan]);
        loop.SetScene(new Scene([Assignment.ForZone(new ZoneRef(StreamFan.Id, new ZoneId(0)), "Rainbow")]));

        using var publishEntered = new ManualResetEventSlim(false);
        using var publishRelease = new ManualResetEventSlim(false);

        var setTask = Task.Run(() =>
        {
            var next = new Scene(
            [
                Assignment.ForZone(new ZoneRef(LoopFan.Id, new ZoneId(0)), probe.Id),
                Assignment.ForZone(new ZoneRef(StreamFan.Id, new ZoneId(0)), "Rainbow"),
            ]);
            loop.SetScene(next, allowMissingZones: false, beforePublish: () =>
            {
                publishEntered.Set();
                publishRelease.Wait(TimeSpan.FromSeconds(5));
            });
        });

        try
        {
            compileEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("Compile should run without blocking RenderFrame");
            var sw = Stopwatch.StartNew();
            loop.RenderFrame(0.1, 1.0 / 60.0);
            sw.ElapsedMilliseconds.Should().BeLessThan(150);
            compileRelease.Set();

            publishEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("beforePublish callback runs outside _gate");
            sw.Restart();
            loop.RenderFrame(0.12, 1.0 / 60.0);
            sw.ElapsedMilliseconds.Should().BeLessThan(150);
            publishRelease.Set();

            await setTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            compileRelease.Set();
            publishRelease.Set();
        }
    }

    private sealed class MutableDeviceClock : IDeviceClock
    {
        public DeviceClockSample? Latest { get; set; }
    }

    private sealed class PhaseRampProbe(double loopSeconds)
        : EffectGenerator("PhaseRampProbe_" + Guid.NewGuid().ToString("N"), new EffectParamSchema("p", "p", 0, 1, false, false, [Rgb.White])), ILoopable
    {
        public double LoopSeconds(in EffectParams p) => loopSeconds;

        public override void Render(in RenderContext ctx, Span<Rgb> output)
        {
            var phase = (ctx.T % loopSeconds) / loopSeconds;
            if (phase < 0) phase += 1.0;
            var v = (byte)Math.Clamp((int)Math.Round(phase * 250.0), 0, 250);
            output.Fill(new Rgb(v, v, v));
        }
    }

    private sealed class SlowCompileProbe(double loopSeconds, Action? onRender)
        : EffectGenerator("SlowCompileProbe_" + Guid.NewGuid().ToString("N"), new EffectParamSchema("p", "p", 0, 1, false, false, [Rgb.White])), ILoopable
    {
        public double LoopSeconds(in EffectParams p) => loopSeconds;

        public override void Render(in RenderContext ctx, Span<Rgb> output)
        {
            onRender?.Invoke();
            output.Fill(Rgb.White);
        }
    }
}
