using UnifiedRgb.Core.Color;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

[Collection("Timing")]
public sealed class ZoneFollowerTests
{
    private static readonly ControllerDescriptor Source = TestSupport.Controller("source", ZoneLayout.Linear(8));
    private static readonly ControllerDescriptor Keyboard = TestSupport.Controller("keyboard", ZoneLayout.Point);
    private static readonly ControllerDescriptor Mouse = TestSupport.Controller("mouse", ZoneLayout.Point);
    private static ZoneRef Z(ControllerDescriptor c) => new(c.Id, new(0));
    private static ZoneFollower Follow(ControllerDescriptor target) => new(Z(Source), 3, Z(target));
    private static Scene Solid(ControllerDescriptor c, Rgb color) => new([Assignment.ForZone(Z(c), StaticColorEffect.EffectId, new([color]))]);
    private static Frame Consume(FrameBus bus, ControllerDescriptor c)
    {
        bus[c.Id]!.TryConsume(out var result).Should().BeTrue();
        return result;
    }

    // Synthetic 85-LED key zone shaped like the adapter's Ready per-key descriptor; every LED is independent, no aliases.
    private static PerKeyCapability ReadyKeys(int keys) => new(PerKeyState.Ready, "synthetic", "synthetic-transport", keys, 128);
    private static ControllerDescriptor Keys(PerKeyCapability? perKey, bool mapped = true)
    {
        var map = new PerKeyMap("synthetic", "synthetic-transport",
            Enumerable.Range(0, 85).Select(i => new PerKeyCell(i, $"k{i}", i / 17, i % 17)).ToArray(), true, true, "synthetic");
        return Keyboard with { Zones = [new(new ZoneId(0), "keys", ZoneLayout.Linear(85))
            { PerKey = perKey, PerKeyPoints = perKey is null ? null : PerKeyProjection.Project(map), Mapped = mapped }] };
    }

    [Fact]
    public void Defaults_do_not_attach_new_devices_to_old_scenes()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]); render.SetScene(Solid(Source, Rgb.White)); render.RenderFrame(0, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black);
        render.FollowerStatus.Should().BeEmpty();
    }

    [Fact]
    public void Two_aggregate_peripherals_copy_the_current_tick_and_time_without_changing_scene()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetFollowers([Follow(Keyboard), Follow(Mouse)]);
        render.SetControllers([Mouse, Source, Keyboard]); // unrelated enumeration order
        var scene = new Scene([Assignment.ForZone(Z(Source), BreathingEffect.EffectId, new([new(200, 80, 20)]))])
            { Package = new("content", "binding", "contract") };
        render.SetScene(scene);
        for (var i = 0; i < 180; i++)
        {
            render.RenderFrame(i / 60d, 1d / 60);
            var source = Consume(bus, Source); var keyboard = Consume(bus, Keyboard); var mouse = Consume(bus, Mouse);
            keyboard.Zones[0][0].Should().Be(source.Zones[0][3]); mouse.Zones[0][0].Should().Be(source.Zones[0][3]);
            keyboard.Index.Should().Be(source.Index); mouse.T.Should().Be(source.T);
        }
        render.Scene.Should().BeSameAs(scene); render.Scene.Package.Should().BeSameAs(scene.Package);
    }

    [Fact]
    public void Explicit_target_assignment_wins_then_removal_restores_local_following()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]); render.SetFollowers([Follow(Keyboard)]);
        var source = Solid(Source, new(10, 20, 30)); var target = Solid(Keyboard, new(200, 0, 100));
        render.SetScene(new(source.Assignments.Concat(target.Assignments).ToArray())); render.RenderFrame(0, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(new Rgb(200, 0, 100));
        render.FollowerStatus.Single().State.Should().Be("SceneAssigned");
        render.SetScene(source); render.RenderFrame(1, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(new Rgb(10, 20, 30));
    }

    [Fact]
    public void Scene_blackout_and_restore_reach_peripherals_in_the_same_frame()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetFollowers([Follow(Keyboard)]); render.SetControllers([Source, Keyboard]);
        var on = Solid(Source, new(30, 60, 90));
        foreach (var scene in new[] { on, Solid(Source, Rgb.Black), on })
        {
            render.SetScene(scene); render.RenderFrame(1, 1d / 60);
            Consume(bus, Keyboard).Zones[0][0].Should().Be(Consume(bus, Source).Zones[0][3]);
        }
    }

    [Fact]
    public void Missing_source_is_black_until_a_fresh_controller_set_is_available()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetFollowers([Follow(Keyboard)]); render.SetControllers([Keyboard]); render.SetScene(Solid(Source, Rgb.White), true);
        render.RenderFrame(0, 1d / 60); Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black);
        render.FollowerStatus.Single().State.Should().Be("MissingController");
        render.SetControllers([Source, Keyboard]); render.RenderFrame(1, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.White);
        render.SetControllers([Keyboard]); render.RenderFrame(2, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black);
    }

    [Fact]
    public void Native_bringup_does_not_leak_a_source_marker_or_new_frame_into_its_follower()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]); render.SetFollowers([Follow(Keyboard)]); render.SetScene(Solid(Source, Rgb.White));
        render.RenderFrame(0, 1d / 60); Consume(bus, Keyboard);
        render.Hold(Source.Id); var count = bus[Keyboard.Id]!.Published;
        render.SetScene(Solid(Source, Rgb.Black)); render.RenderFrame(1, 1d / 60);
        bus[Keyboard.Id]!.Published.Should().Be(count);
        render.ReleaseHold(Source.Id); render.RenderFrame(2, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black);
        render.Hold(Keyboard.Id); count = bus[Keyboard.Id]!.Published; render.RenderFrame(3, 1d / 60);
        bus[Keyboard.Id]!.Published.Should().Be(count);
    }

    [Fact]
    public void Retired_source_mailbox_cannot_feed_a_current_target_generation()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]); render.SetFollowers([Follow(Keyboard)]); render.SetScene(Solid(Source, Rgb.White));
        render.RenderFrame(0, 1d / 60); Consume(bus, Keyboard);
        bus.Unregister(Source.Id); var replacement = bus.Register(Source);
        render.RenderFrame(1, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black); replacement.Published.Should().Be(0);
        render.SetControllers([Source, Keyboard]); render.RenderFrame(2, 1d / 60);
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.White);
    }

    [Fact]
    public void Old_target_generation_does_not_publish_into_a_replacement()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]); render.SetFollowers([Follow(Keyboard)]); render.SetScene(Solid(Source, Rgb.White));
        bus.Unregister(Keyboard.Id); var replacement = bus.Register(Keyboard);
        render.RenderFrame(0, 1d / 60); replacement.Published.Should().Be(0);
    }

    [Fact]
    public void Local_target_colour_correction_is_not_silently_overridden()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard], id => id == Keyboard.Id ? new OutputStage(gamma: 2) : OutputStage.Identity);
        render.SetFollowers([Follow(Keyboard)]); render.SetScene(Solid(Source, Rgb.White)); render.RenderFrame(0, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("TargetRequiresIdentityColourStage");
        Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black);
    }

    [Fact]
    public void Unsupported_LED_dimensions_are_not_resampled_or_guessed()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]);
        render.SetFollowers([new(Z(Source), 8, Z(Keyboard))]); render.RenderFrame(0, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("SourcePixelUnavailable");
        render.SetFollowers([new(Z(Keyboard), 0, Z(Source))]); render.RenderFrame(1, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("TargetMustBeSingleAggregatePoint");
    }

    [Fact]
    public void Ready_per_key_keyboard_takes_the_source_colour_on_every_LED_in_the_same_tick()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        var keys = Keys(ReadyKeys(85));
        render.SetControllers([Source, keys]); render.SetFollowers([Follow(keys)]);
        render.SetScene(new([Assignment.ForZone(Z(Source), BreathingEffect.EffectId, new([new(200, 80, 20)]))]));
        render.FollowerStatus.Single().State.Should().Be("Ready");
        var seen = new HashSet<Rgb>();
        for (var i = 0; i < 120; i++)
        {
            render.RenderFrame(i / 60d, 1d / 60);
            var source = Consume(bus, Source); var keyboard = Consume(bus, keys); var colour = source.Zones[0][3];
            keyboard.Zones[0].Should().HaveCount(85).And.OnlyContain(c => c == colour);
            keyboard.Index.Should().Be(source.Index); keyboard.T.Should().Be(source.T); seen.Add(colour);
        }
        seen.Count.Should().BeGreaterThan(10, "the source really changes while every key follows it");
        foreach (var expected in new[] { Rgb.Black, new Rgb(30, 60, 90), Rgb.Black })
        {
            render.SetScene(Solid(Source, expected)); render.RenderFrame(3, 1d / 60);
            Consume(bus, Source).Zones[0][3].Should().Be(expected); Consume(bus, keys).Zones[0].Should().OnlyContain(c => c == expected);
        }
    }

    [Fact]
    public void Explicit_authored_assignment_gives_each_per_key_LED_its_own_colour_and_removal_restores_the_fallback()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        var keys = Keys(ReadyKeys(85));
        var authored = Enumerable.Range(0, 85).Select(i => new Rgb((byte)i, (byte)(255 - i), (byte)(i * 3))).ToArray();
        var frames = new InMemoryFrameSetSource(); frames.Add(new("keys", 1, 100, [85], [TestSupport.ToBytes(authored)]));
        render.FrameSets = frames; render.SetControllers([Source, keys]); render.SetFollowers([Follow(keys)]);
        var source = Solid(Source, new(10, 20, 30));
        var target = Assignment.ForZone(Z(keys), StaticColorEffect.EffectId) with { Strategy = ContentStrategy.Frames, FrameSetId = "keys" };
        render.SetScene(new([.. source.Assignments, target])); render.RenderFrame(0, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("SceneAssigned");
        var keyboard = Consume(bus, keys).Zones[0];
        keyboard.Should().Equal(authored); keyboard.Distinct().Should().HaveCount(85);
        render.SetScene(source); render.RenderFrame(1, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("Ready");
        Consume(bus, keys).Zones[0].Should().OnlyContain(c => c == new Rgb(10, 20, 30));
    }

    [Theory]
    [InlineData("not-ready")] [InlineData("count-mismatch")] [InlineData("unmapped")] [InlineData("ordinary")]
    public void Multi_LED_targets_without_a_matching_Ready_per_key_capability_are_not_filled(string kind)
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        var keys = kind switch
        {
            "not-ready" => Keys(ReadyKeys(85) with { State = PerKeyState.KeyOrderUnverified }),
            "count-mismatch" => Keys(ReadyKeys(84)),
            "unmapped" => Keys(null, mapped: false),
            _ => Keys(null),
        };
        render.SetControllers([Source, keys]); render.SetFollowers([Follow(keys)]); render.SetScene(Solid(Source, Rgb.White));
        render.RenderFrame(0, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("TargetMustBeSingleAggregatePoint");
        Consume(bus, keys).Zones[0].Should().OnlyContain(c => c == Rgb.Black);
    }

    [Fact]
    public void Aggregate_point_with_a_not_ready_per_key_capability_keeps_following()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        var point = Keyboard with { Zones = [AggregateZone.Descriptor("Keyboard") with { PerKey = ReadyKeys(85) with { State = PerKeyState.Disabled } }] };
        render.SetControllers([Source, point]); render.SetFollowers([Follow(point)]); render.SetScene(Solid(Source, new(40, 50, 60)));
        render.RenderFrame(0, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("Ready"); Consume(bus, point).Zones[0].Should().Equal(new Rgb(40, 50, 60));
    }

    [Fact]
    public void Invalid_configuration_does_not_replace_a_valid_local_route()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard]); render.SetFollowers([Follow(Keyboard)]); render.SetScene(Solid(Source, Rgb.White));
        var duplicate = () => render.SetFollowers([Follow(Keyboard), Follow(Keyboard)]); duplicate.Should().Throw<ArgumentException>();
        var chain = () => render.SetFollowers([Follow(Keyboard), new(Z(Keyboard), 0, Z(Mouse))]); chain.Should().Throw<ArgumentException>();
        var negative = () => render.SetFollowers([new(Z(Source), -1, Z(Keyboard))]); negative.Should().Throw<ArgumentException>();
        render.RenderFrame(0, 1d / 60); Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.White);
    }

    [Fact]
    public void Follower_path_allocates_nothing_per_tick()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        render.SetControllers([Source, Keyboard, Mouse]); render.SetFollowers([Follow(Keyboard), Follow(Mouse)]);
        render.SetScene(Solid(Source, new(11, 22, 33)));
        for (var i = 0; i < 100; i++) render.RenderFrame(i / 60d, 1d / 60);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) render.RenderFrame(i / 60d, 1d / 60);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Theory]
    [InlineData(FrameSetTiming.Nominal)]
    [InlineData(FrameSetTiming.PhysicalMs)]
    public void Authored_frames_keep_their_bytes_timing_and_package_binding(FrameSetTiming timing)
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        var pixels = new byte[2 * 8 * 3];
        for (var i = 0; i < 8; i++) { pixels[i * 3] = 200; pixels[24 + i * 3 + 1] = 180; }
        var before = (byte[])pixels.Clone();
        var frames = new InMemoryFrameSetSource();
        frames.Add(new("authored", 2, 100, [8], [pixels]) { Timing = timing }); render.FrameSets = frames;
        render.SetControllers([Source, Keyboard]); render.SetFollowers([Follow(Keyboard)]);
        var a = Assignment.ForZone(Z(Source), StaticColorEffect.EffectId) with { Strategy = ContentStrategy.Frames, FrameSetId = "authored" };
        var scene = new Scene([a]) { Package = new("content", "binding", "original-contract") }; render.SetScene(scene);
        foreach (var t in new[] { 0d, .11, .23, .37, .52 })
        {
            render.RenderFrame(t, 1d / 60);
            Consume(bus, Keyboard).Zones[0][0].Should().Be(Consume(bus, Source).Zones[0][3]);
        }
        pixels.Should().Equal(before); render.Scene.Should().BeSameAs(scene); scene.Assignments.Should().ContainSingle().Which.Should().BeSameAs(a);
    }

    [Fact]
    public void Uploaded_loop_only_source_is_not_silently_approximated_or_reuploaded()
    {
        using var bus = new FrameBus(); using var render = new RenderLoop(EffectRegistry.Default, bus);
        var uploaded = Source with { Capabilities = Capabilities.UploadedLoopOnly(200, 20, 12288) };
        render.SetControllers([uploaded, Keyboard]); render.SetScene(Solid(uploaded, Rgb.White));
        var uploads = bus[uploaded.Id]!.LoopsPublished;
        render.SetFollowers([Follow(Keyboard)]); render.RenderFrame(0, 1d / 60);
        render.FollowerStatus.Single().State.Should().Be("RequiresStreamedSourceAndTarget");
        bus[uploaded.Id]!.LoopsPublished.Should().Be(uploads); Consume(bus, Keyboard).Zones[0][0].Should().Be(Rgb.Black);
    }
}
