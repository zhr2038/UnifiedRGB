using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// NATIVE-PLAYBACK-CALIBRATION: the manual streamed reference-clock seam. Every case drives a calibrated RenderLoop and an
/// uncalibrated reference (1 / 0) over the same live Lian Li chains plus one streamed GPU point and compares what the GPU
/// receives: a calibrated uploaded companion at host time t shows what the reference shows at T0 + rate × (t − T0 − trim);
/// everything else shows exactly what the reference shows at t. Sample times are binary-exact (T0 + j / 64) or frame centres,
/// so the comparisons are exact. Uploaded loop bytes, effect indices and intervals and the frame's master time never change.
/// </summary>
public class PlaybackCalibrationTests
{
    private const double T0 = 7.25;
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly ControllerId Gpu = new("native:gpu");

    [Fact]
    public void The_source_default_is_the_identity_rate_one_offset_zero()
    {
        PlaybackCalibration.Default.Should().Be(new PlaybackCalibration(1, 0));
        new PlaybackCalibration().Should().Be(PlaybackCalibration.Default);
        PlaybackCalibration.Default.Validate().Should().BeNull();
        (PlaybackCalibration.MinRate, PlaybackCalibration.MaxRate).Should().Be((0.25, 4.0));
        (PlaybackCalibration.MinPhaseTrimMs, PlaybackCalibration.MaxPhaseTrimMs).Should().Be((-2000.0, 2000.0));
        using var bus = new FrameBus();
        using var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.PlaybackCalibration.Should().BeSameAs(PlaybackCalibration.Default);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void A_vendor_played_slice_runs_at_the_calibrated_rate_from_the_raw_anchor(double rate)
    {
        using var reference = new Rig(VendorBreathing);
        using var rig = new Rig(VendorBreathing, calibration: new PlaybackCalibration(rate, 0));
        rig.Loop.PlayedSliceCount.Should().Be(1, "the GPU plays its slice of the vendor body");
        AssertSameLoops(rig, reference, "the rate never reaches the compiled RF loops");
        reference.Anchor(T0);
        rig.Anchor(T0);
        var period = rig.Loops[0].Frames * rig.Loops[0].IntervalMs / 1000.0;
        var differs = 0;
        for (var j = -64; j < 64 * 24; j++)
        {
            var t = T0 + (j / 64.0);
            var px = rig.At(t);
            px.Should().Be(reference.At(T0 + (rate * (t - T0))), $"t = T0 + {j}/64");
            rig.Player.Times[^1].Should().BeApproximately(Wrap(rate * (t - T0), period), 1e-9, "the played slice's loop time");
            differs += px != reference.At(t) ? 1 : 0;
        }

        if (rate == 1)
        {
            differs.Should().Be(0, "1 / 0 is the identity");
        }
        else
        {
            differs.Should().BeGreaterThan(100, "a different rate shows different frames");
        }

        rig.LoopsPublished.Should().Be(reference.LoopsPublished, "nothing is re-uploaded");
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public void A_program_live_slice_sharing_the_group_epoch_runs_at_the_calibrated_rate(double rate)
    {
        using var reference = new Rig(ProgramBreathing);
        using var rig = new Rig(ProgramBreathing, calibration: new PlaybackCalibration(rate, 0));
        rig.Loop.HasPlayedSlices.Should().BeFalse("the Program GPU slice renders live");
        AssertSameLoops(rig, reference, "the generic Lian Li loops are unchanged");
        reference.Anchor(T0);
        rig.Anchor(T0);
        var differs = 0;
        for (var j = 0; j < 64 * 12; j++)
        {
            var t = T0 + (j / 64.0);
            var px = rig.At(t);
            px.Should().Be(reference.At(T0 + (rate * (t - T0))), $"t = T0 + {j}/64");
            differs += px != reference.At(t) ? 1 : 0;
        }

        differs.Should().BeGreaterThan(100);
    }

    [Theory]
    [InlineData(2.0, 0.0)]
    [InlineData(0.5, 0.0)]
    [InlineData(2.0, 120.0)]
    [InlineData(0.5, -300.0)]
    public void A_shared_frame_set_plays_its_frames_at_the_calibrated_rate(double rate, double trimMs)
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var reference = new Rig(SharedFrames, sets);
        using var rig = new Rig(SharedFrames, sets, new PlaybackCalibration(rate, trimMs));
        AssertSameLoops(rig, reference, "the authored frames are uploaded unchanged");
        rig.Loops.Should().OnlyContain(l => l.Frames == 20 && l.IntervalMs == 40);
        rig.Anchor(T0);
        for (var k = 0; k < 60; k++)
        {
            // the centre of calibrated frame k: T0 + trim + (k + ½) × 40 ms / rate
            var t = T0 + (trimMs / 1000.0) + ((k + 0.5) * 0.040 / rate);
            FrameOf(rig.At(t)).Should().Be(k % 20, $"calibrated frame {k}");
        }
    }

    [Fact]
    public void A_local_frame_set_keeps_its_own_epoch_whatever_the_calibration()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("gpu", [1]));
        using var rig = new Rig(LocalFrames, sets, new PlaybackCalibration(2, 500), compileAt: T0);
        rig.Loop.PlayedSliceCount.Should().Be(1);
        rig.Anchor(T0 + 3); // no uploaded companion: an adapter report cannot move a local epoch
        for (var k = 0; k < 60; k++)
        {
            FrameOf(rig.At(T0 + ((k + 0.5) * 0.040))).Should().Be(k % 20, "frame k at its own uncalibrated centre");
        }
    }

    [Fact]
    public void Pure_streamed_content_and_per_device_routes_are_unchanged()
    {
        Func<ZoneRef[], ZoneRef, Assignment>[] routes = [StreamedOnly, PerDeviceRoute];
        foreach (var assign in routes)
        {
            using var reference = new Rig(assign);
            using var rig = new Rig(assign, calibration: new PlaybackCalibration(2, -750));
            reference.Anchor(T0);
            rig.Anchor(T0);
            for (var j = 0; j < 64 * 6; j++)
            {
                var t = T0 + (j / 64.0);
                rig.At(t).Should().Be(reference.At(t), $"{assign.Method.Name}: t = T0 + {j}/64 renders at master time t");
            }
        }

        using var morph = new Rig(PerDeviceRoute);
        morph.Loop.LastLoopCompile!.Variants!.Single().Should().Contain("Vendor:RainbowMorph+program(1)");
    }

    [Theory]
    [InlineData(1.0, 250.0)]
    [InlineData(1.0, -250.0)]
    [InlineData(2.0, 250.0)]
    [InlineData(0.5, -500.0)]
    public void The_offset_is_applied_once_never_accumulated_across_reports_setters_and_recompiles(double rate, double trimMs)
    {
        using var reference = new Rig(VendorBreathing);
        using var rig = new Rig(VendorBreathing);
        var calibration = new PlaybackCalibration(rate, trimMs);
        rig.Loop.SetPlaybackCalibration(calibration);
        reference.Anchor(T0);
        rig.Anchor(T0);
        AssertMapped(rig, reference, rate, trimMs, T0, "a positive offset shows the reference's earlier frame");

        // repeated reports of the same and of a later first header, repeated setters, and a recompile of the same scene
        rig.Anchor(T0);
        rig.Anchor(T0);
        rig.Anchor(T0 + 1);
        rig.Loop.SetPlaybackCalibration(calibration);
        rig.Loop.SetPlaybackCalibration(new PlaybackCalibration(rate, trimMs));
        var published = rig.LoopsPublished;
        rig.Loop.SetScene(new Scene([VendorBreathing(ChainRefs(rig), new ZoneRef(Gpu, new ZoneId(0)))]));
        rig.LoopsPublished.Should().Be(published, "unchanged content keeps its loops and anchor");
        AssertMapped(rig, reference, rate, trimMs, T0, "still one offset");

        // an earlier report moves the raw anchor; the offset still applies once on top of it
        rig.Anchor(T0 - 1);
        reference.Anchor(T0 - 1);
        AssertMapped(rig, reference, rate, trimMs, T0 - 1, "after an earlier first header");

        // a forced re-upload starts a new anchor; its late raw report is calibrated once as well
        rig.Loop.RepublishLoops();
        reference.Loop.RepublishLoops();
        rig.Loops = rig.DrainLoops();
        reference.Loops = reference.DrainLoops();
        rig.Anchor(T0 + 3);
        reference.Anchor(T0 + 3);
        AssertMapped(rig, reference, rate, trimMs, T0 + 3, "after the re-upload's late callback");
        rig.Loop.PlaybackCalibration.Should().Be(calibration);
    }

    [Fact]
    public void A_live_set_takes_a_new_calibration_on_the_next_frame_without_a_reupload()
    {
        using var reference = new Rig(VendorBreathing);
        using var rig = new Rig(VendorBreathing);
        reference.Anchor(T0);
        rig.Anchor(T0);
        rig.At(T0 + 1).Should().Be(reference.At(T0 + 1));
        var compile = rig.Loop.LastLoopCompile;
        var published = rig.LoopsPublished;

        rig.Loop.SetPlaybackCalibration(new PlaybackCalibration(2, 0)).Should().Be(new PlaybackCalibration(2, 0));
        AssertMapped(rig, reference, 2, 0, T0, "the next frame uses the new rate");
        rig.Loop.SetPlaybackCalibration(PlaybackCalibration.Default);
        for (var j = 0; j < 64 * 3; j++)
        {
            rig.At(T0 + (j / 64.0)).Should().Be(reference.At(T0 + (j / 64.0)), "back to 1 / 0: the identity again, no anchor reset");
        }

        rig.LoopsPublished.Should().Be(published, "no re-upload");
        rig.AnyLoopPending.Should().BeFalse();
        rig.Loop.LastLoopCompile.Should().BeSameAs(compile, "no recompile");
        rig.Loop.PlayedSliceCount.Should().Be(1);
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.2499, 0.0)]
    [InlineData(4.0001, 0.0)]
    [InlineData(1.0, double.NaN)]
    [InlineData(1.0, double.PositiveInfinity)]
    [InlineData(1.0, double.NegativeInfinity)]
    [InlineData(1.0, 2000.1)]
    [InlineData(1.0, -2000.1)]
    [InlineData(2.0, 3000.0)]
    [InlineData(5.0, 100.0)]
    public void An_invalid_calibration_is_refused_whole_and_the_previous_snapshot_stays(double rate, double trimMs)
    {
        var invalid = new PlaybackCalibration(rate, trimMs);
        invalid.Validate().Should().NotBeNull();
        using var bus = new FrameBus();
        using var loop = new RenderLoop(EffectRegistry.Default, bus);
        var before = loop.SetPlaybackCalibration(new PlaybackCalibration(1.5, -100));
        var act = () => loop.SetPlaybackCalibration(invalid);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("calibration");
        loop.PlaybackCalibration.Should().BeSameAs(before, "neither value of a refused snapshot is applied");
    }

    [Theory]
    [InlineData(0.25, -2000.0)]
    [InlineData(4.0, 2000.0)]
    [InlineData(1.0, 0.0)]
    public void The_bounds_themselves_are_accepted(double rate, double trimMs)
    {
        using var bus = new FrameBus();
        using var loop = new RenderLoop(EffectRegistry.Default, bus);
        loop.SetPlaybackCalibration(new PlaybackCalibration(rate, trimMs)).Should().Be(new PlaybackCalibration(rate, trimMs));
        var act = () => loop.SetPlaybackCalibration(null!);
        act.Should().Throw<ArgumentNullException>();
        loop.PlaybackCalibration.Should().Be(new PlaybackCalibration(rate, trimMs));
    }

    // ---------------------------------------------------------------- harness

    private static EffectParams P() => new([Red], 1f, 1f, Direction.Forward);

    private static Assignment VendorBreathing(ZoneRef[] chains, ZoneRef gpu) => Assignment.ForGroup("g", [.. chains, gpu], "Breathing", P());

    private static Assignment ProgramBreathing(ZoneRef[] chains, ZoneRef gpu) => VendorBreathing(chains, gpu) with { Strategy = ContentStrategy.Program };

    private static Assignment SharedFrames(ZoneRef[] chains, ZoneRef gpu) =>
        new([.. chains, gpu], "StaticColor", P(), "g", ContentStrategy.Frames) { FrameSetId = "shared" };

    private static Assignment LocalFrames(ZoneRef[] chains, ZoneRef gpu) =>
        new([gpu], "StaticColor", P(), null, ContentStrategy.Frames) { FrameSetId = "gpu" };

    private static Assignment StreamedOnly(ZoneRef[] chains, ZoneRef gpu) => Assignment.ForZone(gpu, "Breathing", P()) with { Strategy = ContentStrategy.Program };

    private static Assignment PerDeviceRoute(ZoneRef[] chains, ZoneRef gpu) => Assignment.ForGroup("g", [.. chains, gpu], "RainbowMorph", P());

    private static ZoneRef[] ChainRefs(Rig rig) => rig.Chains.Select(c => new ZoneRef(c.Id, new ZoneId(0))).ToArray();

    /// <summary>20 frames × 40 ms; the GPU zone's red channel is 10 + the frame number, every other zone is dark.</summary>
    private static FrameSet FrameSetFor(string id, int[] leds) =>
        new(id, 20, 40, leds, leds.Select((n, i) =>
        {
            var rgb = new byte[20 * n * 3];
            if (i == leds.Length - 1)
            {
                for (var k = 0; k < 20; k++)
                {
                    rgb[k * 3] = (byte)(10 + k);
                }
            }

            return rgb;
        }).ToArray());

    private static int FrameOf(Rgb px) => px.R - 10;

    private static double Wrap(double x, double period)
    {
        var r = x % period;
        return r < 0 ? r + period : r;
    }

    private static void AssertSameLoops(Rig rig, Rig reference, string because)
    {
        for (var z = 0; z < rig.Loops.Length; z++)
        {
            var (a, b) = (rig.Loops[z], reference.Loops[z]);
            (a.Frames, a.LedCount, a.IntervalMs).Should().Be((b.Frames, b.LedCount, b.IntervalMs), because);
            a.Rgb.ToArray().Should().Equal(b.Rgb.ToArray(), because);
        }

        rig.Loops.Select(l => l.EffectIndex).Distinct().Should().ContainSingle("one common effect index");
    }

    /// <summary>At every sample t the calibrated rig shows what the reference shows at t0 + rate × (t − t0 − trim).</summary>
    private static void AssertMapped(Rig rig, Rig reference, double rate, double trimMs, double t0, string because)
    {
        var trim = trimMs / 1000.0; // ±250 / ±500 ms: binary-exact
        for (var j = -64; j < 64 * 12; j++)
        {
            var t = t0 + (j / 64.0);
            rig.At(t).Should().Be(reference.At(t0 + (rate * (t - t0 - trim))), $"{because}: t = t0 + {j}/64");
        }
    }

    /// <summary>The live chains (as in the stacking tests) plus one streamed GPU point, compiled with one assignment.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly FrameBus _bus = new();

        public Rig(Func<ZoneRef[], ZoneRef, Assignment> assign, IFrameSetSource? sets = null, PlaybackCalibration? calibration = null, double compileAt = 0)
        {
            Loop = new RenderLoop(EffectRegistry.Default, _bus)
            {
                ChainReversed = id => id.Value == "lianli:rf:1B-7B-F1-72-AB-3C",
                Ring = id => id.Value == "lianli:rf:EF-7E-63-62-32-E1" ? new RingGeometry(true, false, 0) : default,
                FrameSets = sets,
                Player = Player,
            };
            Loop.SetControllers([.. Chains, TestSupport.Controller(Gpu.Value, ZoneLayout.Point)]);
            if (calibration is not null)
            {
                Loop.SetPlaybackCalibration(calibration);
            }

            if (compileAt != 0)
            {
                At(compileAt); // the local epoch starts at the last rendered master time
            }

            Loop.SetScene(new Scene([assign(ChainRefs(this), new ZoneRef(Gpu, new ZoneId(0)))]));
            Loops = DrainLoops();
        }

        public ControllerDescriptor[] Chains { get; } =
        [
            Chain("lianli:rf:35-F7-EF-72-AB-3C", "TL", 3, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:06-A8-F0-72-AB-3C", "TL", 3, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:1B-7B-F1-72-AB-3C", "TL", 3, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:EF-7E-63-62-32-E1", "H2", 1, ZoneLayout.Ring(24)),
        ];

        public RecordingPlayer Player { get; } = new();

        public RenderLoop Loop { get; }

        public LoopFrames[] Loops { get; set; }

        public long LoopsPublished => Chains.Sum(c => _bus[c.Id]!.LoopsPublished);

        public bool AnyLoopPending => Chains.Any(c => _bus[c.Id]!.HasPendingLoop);

        public LoopFrames[] DrainLoops() => Chains.Select(c =>
        {
            _bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            return pending.Loop;
        }).ToArray();

        public void Anchor(double t0) => Loop.AnchorLoop(Loops[0].EffectIndex, t0);

        public Rgb At(double t)
        {
            Loop.RenderFrame(t, 0);
            _bus[Gpu]!.TryConsume(out var frame).Should().BeTrue();
            frame.T.Should().Be(t, "the master time is never calibrated");
            return frame.Zones[0][0];
        }

        public void Dispose()
        {
            Loop.Dispose();
            _bus.Dispose();
        }

        private static ControllerDescriptor Chain(string id, string family, int units, ZoneLayout layout) =>
            new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
                [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));
    }

    /// <summary>The default player, recording the loop time each played slice is sampled at.</summary>
    private sealed class RecordingPlayer : IStreamedLoopPlayer
    {
        public List<double> Times { get; } = [];

        public int FrameAt(double tt, double intervalMs, int frames)
        {
            Times.Add(tt);
            return StreamedLoopPlayer.Default.FrameAt(tt, intervalMs, frames);
        }
    }
}
