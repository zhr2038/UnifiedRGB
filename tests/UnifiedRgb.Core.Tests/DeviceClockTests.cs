using System.Diagnostics;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// NATIVE-RF-CLOCK: the cached device-clock sample and its 1600 ticks/s extrapolation, uint32 wrap and age, independent of
/// any device.
/// </summary>
public class DeviceClockSampleTests
{
    private static readonly DeviceClockDomain Domain = DeviceClockDomain.Next("test");

    private static long Ts(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    [Fact]
    public void A_sample_extrapolates_at_its_rate_and_reports_age_and_query_latency()
    {
        var s = new DeviceClockSample(Domain, 1_000_000, Ts(10), 1600, Ts(9.996));
        s.AgeSeconds(Ts(12.5)).Should().BeApproximately(2.5, 1e-9);
        s.TicksAt(Ts(12.5)).Should().BeApproximately(1_000_000 + 4000, 1e-6, "2.5 s × 1600 ticks/s");
        s.TicksAt(Ts(10)).Should().Be(1_000_000, "no age: the raw reading");
        s.QueryLatencySeconds.Should().BeApproximately(0.004, 1e-9);
        new DeviceClockSample(Domain, 1, Ts(10), 1600, Ts(11)).QueryLatencySeconds.Should().Be(0, "the send stamp never follows the reply stamp");
    }

    [Fact]
    public void The_counter_wraps_as_an_unsigned_32_bit_value_never_as_a_negative_number()
    {
        var s = new DeviceClockSample(Domain, uint.MaxValue - 99, Ts(0), 1600, Ts(0));
        s.TicksAt(Ts(1)).Should().BeApproximately(1500, 1e-6, "2³² − 100 + 1600 wraps to 1500");
        var early = new DeviceClockSample(Domain, 10, Ts(5), 1600, Ts(5));
        early.TicksAt(Ts(4)).Should().BeApproximately(DeviceClockSample.CounterModulus - 1590, 1e-6, "an older 'now' wraps below zero to the top");
        early.TicksAt(Ts(4)).Should().BeInRange(0, DeviceClockSample.CounterModulus);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_invalid_rate_is_refused(double tps)
    {
        var act = () => new DeviceClockSample(Domain, 1, 0, tps, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Every_domain_is_fresh_and_equal_only_to_itself()
    {
        var a = DeviceClockDomain.Next("lianli-rf-master");
        var b = DeviceClockDomain.Next("lianli-rf-master");
        a.Should().NotBe(b, "a reopened source is a different clock even of the same kind");
        a.Should().Be(a with { });
        b.Generation.Should().BeGreaterThan(a.Generation);
        a.ToString().Should().Be($"lianli-rf-master#{a.Generation}");
    }
}

/// <summary>
/// NATIVE-RF-CLOCK in RenderLoop: with clocks bound, the streamed members of a mixed assignment follow the phase of the
/// reference uploaded loop in the vendor bridge model (frame floor(C / I) mod N, C the 1600 ticks/s master counter, I the
/// wire interval). Every case drives the live Lian Li chains plus one streamed GPU point, with a fake clock at large raw
/// counters and an injected host time. Without clocks (or on any fallback) the legacy upload-anchor timing is unchanged.
/// </summary>
public class DeviceClockRenderTests
{
    private const double T0 = 7.25;
    private const double Tps = 1600;
    private const uint Big = 3_900_000_000; // ≈ 28 days of uptime
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly ControllerId Gpu = new("native:gpu");
    private static readonly ControllerId Gpu2 = new("native:gpu2");

    [Fact]
    public void A_vendor_breathing_slice_shows_the_uploaded_reference_rgb_of_the_counter_frame()
    {
        using var rig = new Rig(VendorBreathing35, clocks: Clocks.Shared);
        var (n, iw) = rig.Reference;
        rig.Player.Calls.Clear();
        var levels = new HashSet<byte>();
        for (var m = 0; m < 2 * n; m++)
        {
            var c = Base(n * iw) + ((m + 0.5) * iw);
            var px = rig.AtCounter(c);
            var call = rig.Player.Calls[^1];
            (call.Frames, call.IntervalMs).Should().Be((n, rig.Loops[0].IntervalMs), "the GPU plays its slice of the same 170-frame body");
            px.Should().Be(rig.LoopPixel(0, m % n), $"counter frame {m}: the native pixel is the uploaded reference loop's RGB");
            levels.Add(px.R);
        }

        levels.Should().HaveCountGreaterThan(20, "a real breathing ramp, not a still colour");
        levels.Max().Should().BeGreaterThan((byte)(levels.Min() + 40));
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
    }

    [Fact]
    public void A_vendor_played_slice_shows_the_counter_frame_of_the_reference_loop_with_the_same_hold()
    {
        using var rig = new Rig(VendorBreathing, clocks: Clocks.Shared);
        rig.Loop.PlayedSliceCount.Should().Be(1);
        var (n, iw) = rig.Reference;
        var seen = new HashSet<Rgb>();
        for (var m = 0; m < 3 * n; m += 7)
        {
            var c = Base(n * iw) + ((m + 0.5) * iw); // centre of reference frame m, at a large raw counter
            var px = rig.AtCounter(c);
            seen.Add(px);
            var call = rig.Player.Calls[^1];
            var phase = Wrap(c, n * iw) / (n * iw);
            call.Result.Should().Be((int)Math.Floor(phase * call.Frames), $"reference frame {m}");
            if (call.Frames == n && call.IntervalMs == rig.Loops[0].IntervalMs)
            {
                call.Result.Should().Be(m % n, "the slice and the reference loop are the same body timing");
            }
        }

        seen.Should().HaveCountGreaterThan(5, "the breathing slice moves");
        var report = rig.Report();
        report.Mode.Should().Be(StreamedClockMode.DeviceClock);
        report.FallbackReason.Should().BeNull();
        report.ReferenceFrames.Should().Be(n);
        report.ReferenceInterval.Should().Be(iw);
        report.NominalPeriodMs.Should().BeApproximately(n * iw, 1e-9, "the old header model: frames × interval as ms");
        report.DerivedPeriodMs.Should().BeApproximately(n * iw * 1000 / Tps, 1e-9, "the device-clock model's period");
        report.TicksPerSecond.Should().Be(Tps);
        report.WirePeriodsDiffer.Should().Be(rig.Loops.Select(l => l.Frames * Math.Round(l.IntervalMs * 100) / 100).Distinct().Count() > 1);
        report.Domain.Should().Be(rig.Domain.ToString());
    }

    [Fact]
    public void A_shared_frame_set_follows_the_counter_and_one_host_second_is_1600_ticks_with_no_extra_factor()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        rig.Loops.Should().OnlyContain(l => l.Frames == 20 && l.IntervalMs == 40);
        var start = (double)(Big - (Big % 800)) + 20; // centre of frame 0
        rig.Publish(start, ageSeconds: 0);
        FrameOf(rig.Render()).Should().Be(0);
        for (var k = 1; k <= 80; k++)
        {
            // k frames later in host time: k × 40 ticks / 1600 ticks/s = k × 25 ms (rate 1: no implicit 1.6 on top)
            rig.Now = rig.SampleAt + Ts(k * 0.025);
            FrameOf(rig.Render()).Should().Be(k % 20, $"{k} × 25 ms after the sample");
        }

        rig.Now = rig.SampleAt + Ts(1.0);
        FrameOf(rig.Render()).Should().Be(0, "one host second = 1600 ticks = exactly two 800-tick loops");
    }

    [Theory]
    [InlineData(450.0, 720.0)]
    [InlineData(-450.0, -720.0)]
    [InlineData(125.0, 200.0)]
    public void The_trim_is_a_host_time_delay_converted_at_1600_ticks_per_second(double trimMs, double ticks)
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, new PlaybackCalibration(1, trimMs), Clocks.Shared);
        for (var m = 0; m < 40; m++)
        {
            var c = (double)(Big - (Big % 800)) + 4000 + (m * 40) + 20;
            FrameOf(rig.AtCounter(c)).Should().Be((int)Math.Floor(Wrap(c - ticks, 800) / 40), "positive trim shows the earlier frame");
        }
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.6)]
    public void The_rate_is_a_residual_factor_on_the_counter(double rate)
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, new PlaybackCalibration(rate, 0), Clocks.Shared);
        for (var j = 0; j < 400; j++)
        {
            var c = 123_456_789.0 + (j * 3.25);
            var expected = Wrap(c * rate, 800) / 40;
            if (Math.Abs(expected - Math.Round(expected)) < 1e-3)
            {
                continue; // a boundary
            }

            FrameOf(rig.AtCounter(c)).Should().Be((int)Math.Floor(expected), $"counter {c} at rate {rate}");
        }
    }

    [Fact]
    public void A_program_live_slice_samples_the_wire_period_so_long_uptime_never_drifts()
    {
        var probe = new Probe(2.0003); // 100 frames × round(20.003, 2) = 20.00 ms on the wire; natural 2.0003 s
        using var rig = new Rig((chains, gpu) => Assignment.ForGroup("g", [.. chains, gpu], probe.Id, P()) with { Strategy = ContentStrategy.Program },
            clocks: Clocks.Shared, registry: new EffectRegistry([probe]));
        rig.Loops.Should().OnlyContain(l => l.Frames == 100 && l.IntervalMs == 20);
        var naturalFrameMisses = 0;
        foreach (var c in new[] { 1000.0, 2_000_000.0, 1_000_000_000.0, 4_000_000_000.0, 4_294_000_000.0 })
        {
            var at = c + 10; // centre of a 20-tick frame
            rig.AtCounter(at, dt: 1 / 60.0);
            var expectedFrame = (int)(Math.Floor(at / 20) % 100);
            Math.Floor(probe.T / (2.0003 / 100) + 1e-9).Should().Be(expectedFrame, $"counter {at}: the generator's timeline frame is the device frame");
            probe.Dt.Should().BeApproximately(1 / 60.0 * Tps * 2.0003 / 2000, 1e-12, "dTt/dt = rate × ticks/s × live period / wire period");

            // the regression: wrapping the generator's natural period (2.0003 s) in the header model's time (counter / 1000, one
            // tick = 1 ms, so the 1.6 scale is NOT involved) differs from the device only by natural-versus-wire rounding, and
            // still picks a different frame at long uptime
            var naturalTime = Wrap(at / 1000.0, 2.0003);
            naturalFrameMisses += (int)Math.Floor(naturalTime / (2.0003 / 100)) != expectedFrame ? 1 : 0;
        }

        naturalFrameMisses.Should().BeGreaterThan(0, "the natural period is not the wire period");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void A_live_generator_observes_one_coherent_scaled_time_domain(double rate)
    {
        var probe = new Probe(2);
        using var rig = new Rig((chains, gpu) => Assignment.ForGroup("g", [.. chains, gpu], probe.Id, P()) with { Strategy = ContentStrategy.Program },
            calibration: new PlaybackCalibration(rate, 0), clocks: Clocks.Shared, registry: new EffectRegistry([probe]));
        rig.Publish(Big, ageSeconds: 0);
        rig.Render(0.02);
        var t1 = probe.T;
        probe.Dt.Should().BeApproximately(0.02 * rate * Tps * 2 / 2000, 1e-12, "the chain rule: 1.6 × rate per host second at this period");
        rig.Now += Ts(0.02);
        rig.Render(0.02);
        Wrap(probe.T - t1, 2).Should().BeApproximately(probe.Dt, 1e-6, "T advanced by exactly the reported Dt");
    }

    [Fact]
    public void A_uniform_rgb_color_cycle_shows_the_uploaded_reference_rgb_at_every_frame_boundary()
    {
        using var rig = new Rig(ProgramColorCycle, clocks: Clocks.Shared);
        var (n, iw) = rig.Reference;
        (n, iw).Should().Be((200, 47.43), "the live uniform R/G/B scene: 200 × 47.43 ms");
        var (red, green, blue, mixed) = (0, 0, 0, 0);
        for (var m = 0; m < 2 * n; m++)
        {
            var c = Base(n * iw) + (m * iw); // the boundary where reference frame m starts
            var px = rig.AtCounter(c);
            var reference = rig.LoopPixel(0, m % n);
            px.Should().Be(reference, $"frame boundary {m}: the live native RGB is the uploaded reference loop's RGB");
            var top = Math.Max(px.R, Math.Max(px.G, px.B));
            red += px.R == top && px.R > 2 * Math.Max(px.G, px.B) ? 1 : 0;
            green += px.G == top && px.G > 2 * Math.Max(px.R, px.B) ? 1 : 0;
            blue += px.B == top && px.B > 2 * Math.Max(px.R, px.G) ? 1 : 0;
            mixed += Math.Min(px.R, px.G) > 10 || Math.Min(px.G, px.B) > 10 || Math.Min(px.R, px.B) > 10 ? 1 : 0;
        }

        red.Should().BeGreaterThan(10, "red dominates part of the cycle");
        green.Should().BeGreaterThan(10, "green dominates part of the cycle");
        blue.Should().BeGreaterThan(10, "blue dominates part of the cycle");
        mixed.Should().BeGreaterThan(10, "and the transitions between them are visited");
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
    }

    [Fact]
    public void A_uniform_program_color_cycle_matches_the_legacy_render_at_the_same_loop_time()
    {
        using var rig = new Rig(ProgramColorCycle, clocks: Clocks.Shared);
        using var legacy = new Rig(ProgramColorCycle);
        legacy.Anchor(T0);
        var (n, iw) = rig.Reference;
        var live = rig.Loop.ClockReport()[0];
        live.Mode.Should().Be(StreamedClockMode.UploadAnchor);
        var distinct = new HashSet<Rgb>();
        for (var m = 0; m < n; m += Math.Max(1, n / 25))
        {
            var c = Base(n * iw) + (m * iw) + (iw / 2);
            var px = rig.AtCounter(c);
            var phase = Wrap(c, n * iw) / (n * iw);
            var reference = legacy.At(T0 + (phase * n * rig.Loops[0].IntervalMs / 1000.0));
            distinct.Add(px);
            Math.Abs(px.R - reference.R).Should().BeLessThanOrEqualTo(1);
            Math.Abs(px.G - reference.G).Should().BeLessThanOrEqualTo(1);
            Math.Abs(px.B - reference.B).Should().BeLessThanOrEqualTo(1);
            var later = rig.AtCounter(c + (n * iw * 1000));
            Math.Abs(later.R - px.R).Should().BeLessThanOrEqualTo(1, "periodic in the wire period at any uptime");
            Math.Abs(later.G - px.G).Should().BeLessThanOrEqualTo(1);
            Math.Abs(later.B - px.B).Should().BeLessThanOrEqualTo(1);
        }

        distinct.Should().HaveCountGreaterThan(5, "the R/G/B cycle is time-varying, so these comparisons are not vacuous");
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
    }

    [Fact]
    public void Scene_reapply_resync_late_callbacks_and_a_new_effect_index_never_reset_the_phase()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        double[] counters = [Big + 20.0, Big + 333.0, Big + 777.0, 3_000.5];
        int[] Frames() => counters.Select(c => FrameOf(rig.AtCounter(c))).ToArray();
        var before = Frames();
        before.Should().Equal(counters.Select(c => (int)Math.Floor(Wrap(c, 800) / 40)));

        rig.Anchor(T0);
        rig.Anchor(T0 + 3); // late upload callbacks
        Frames().Should().Equal(before, "an upload callback never moves a device-clock phase");

        var published = rig.LoopsPublished;
        rig.Loop.SetScene(new Scene([SharedFrames(rig.ChainRefs(), new ZoneRef(Gpu, new ZoneId(0)))]));
        rig.LoopsPublished.Should().Be(published, "identical content");
        Frames().Should().Equal(before, "an identical scene apply");
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock, "an identical apply keeps the index and its transmitted-timing verdict");

        var index = rig.Loops[0].EffectIndex;
        rig.Loop.RepublishLoops();
        rig.Loops = rig.DrainLoops();
        rig.Loops[0].EffectIndex.Should().NotBe(index, "a resync uploads with a new effect index");
        rig.AtCounter(Big + 20.0);
        rig.Report().FallbackReason.Should().Contain("not reported", "the resync's uploads have not completed yet");
        rig.ReportUploads();
        rig.Anchor(T0 + 9);
        Frames().Should().Equal(before, "a new effect index is a change token, not an epoch");
    }

    [Fact]
    public void A_new_sample_or_domain_is_followed_at_once_and_a_closed_session_falls_back()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        using var legacy = new Rig(SharedFrames, sets);
        rig.Anchor(T0);
        legacy.Anchor(T0);
        FrameOf(rig.AtCounter(Big + 100.0)).Should().Be((int)Math.Floor(Wrap(Big + 100.0, 800) / 40));

        // a reconnect: the handle now reports a fresh domain and a fresh counter
        rig.Domain = DeviceClockDomain.Next("test");
        FrameOf(rig.AtCounter(5_000.0 + 20)).Should().Be(5, "the new session's counter, not the old sample");
        rig.Report().Domain.Should().Be(rig.Domain.ToString());

        rig.Clock.Latest = null; // closed
        rig.Render(t: T0 + 1).Should().Be(legacy.At(T0 + 1), "no sample: the legacy anchor (same master time as the reference)");
        rig.Report().Mode.Should().Be(StreamedClockMode.UploadAnchor);
        rig.Report().FallbackReason.Should().Contain("no validated device-clock sample");
    }

    [Fact]
    public void A_stale_sample_falls_back_to_the_upload_anchor_with_its_reason()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        using var legacy = new Rig(SharedFrames, sets);
        rig.Anchor(T0);
        legacy.Anchor(T0);
        rig.Publish(Big, ageSeconds: 2.99);
        rig.Render(t: T0 + 0.5);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock, "2.99 s is fresh");
        rig.Publish(Big, ageSeconds: 3.01);
        for (var j = 0; j < 64; j++)
        {
            rig.Render(t: T0 + (j / 64.0)).Should().Be(legacy.At(T0 + (j / 64.0)), "stale: the legacy timing exactly");
        }

        var report = rig.Report();
        report.Mode.Should().Be(StreamedClockMode.UploadAnchor);
        report.FallbackReason.Should().Contain("stale");
        report.SampleAgeSeconds.Should().BeApproximately(3.01, 1e-6);
        report.ReferenceFrame.Should().BeNull("a predicted frame is reported only in DeviceClock mode");
    }

    [Fact]
    public void Two_clock_domains_or_a_missing_clock_are_never_presented_as_one_lock()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var legacy = new Rig(SharedFrames, sets);
        legacy.Anchor(T0);
        foreach (var (mode, reason) in new[] { (Clocks.TwoDomains, "different clock domains"), (Clocks.MissingH2, "no device clock for lianli:rf:EF-7E-63-62-32-E1") })
        {
            using var rig = new Rig(SharedFrames, sets, clocks: mode);
            rig.Anchor(T0);
            rig.Publish(Big, ageSeconds: 0);
            for (var j = 0; j < 64; j++)
            {
                rig.Render(t: T0 + (j / 64.0)).Should().Be(legacy.At(T0 + (j / 64.0)), $"{mode}: legacy timing");
            }

            var report = rig.Report();
            report.Mode.Should().Be(StreamedClockMode.UploadAnchor);
            report.FallbackReason.Should().Contain(reason);
        }
    }

    [Fact]
    public void Unbound_clocks_keep_the_legacy_timing_and_report_disabled()
    {
        using var reference = new Rig(VendorBreathing);
        using var rig = new Rig(VendorBreathing, clocks: Clocks.Shared);
        rig.Loop.DeviceClockEnabled.Should().BeTrue();
        rig.Loop.SetDeviceClocks(null);
        rig.Loop.DeviceClockEnabled.Should().BeFalse();
        reference.Loop.DeviceClockEnabled.Should().BeFalse("the default is off");
        reference.Anchor(T0);
        rig.Anchor(T0);
        rig.Publish(Big, ageSeconds: 0);
        for (var j = -64; j < 64 * 6; j++)
        {
            rig.Render(t: T0 + (j / 64.0)).Should().Be(reference.At(T0 + (j / 64.0)), $"t = T0 + {j}/64");
        }

        rig.Report().FallbackReason.Should().Contain("disabled");
        reference.Loop.ClockReport().Should().ContainSingle().Which.Mode.Should().Be(StreamedClockMode.UploadAnchor);
    }

    [Fact]
    public void Local_frames_pure_streamed_and_per_device_routes_ignore_a_bound_clock()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("gpu", [1]));
        (Func<ZoneRef[], ZoneRef, Assignment> Assign, StreamedClockMode Mode)[] cases =
            [(LocalFrames, StreamedClockMode.LocalEpoch), (StreamedOnly, StreamedClockMode.MasterTime), (PerDeviceRoute, StreamedClockMode.MasterTime)];
        foreach (var (assign, mode) in cases)
        {
            using var reference = new Rig(assign, sets);
            using var rig = new Rig(assign, sets, new PlaybackCalibration(2, 450), Clocks.Shared);
            reference.Loop.SetPlaybackCalibration(new PlaybackCalibration(2, 450));
            reference.Anchor(T0);
            rig.Anchor(T0);
            for (var j = 0; j < 64 * 4; j++)
            {
                rig.Publish(Big + (j * 97.0), ageSeconds: 0);
                rig.Render(t: T0 + (j / 64.0)).Should().Be(reference.At(T0 + (j / 64.0)), $"{assign.Method.Name}: t = T0 + {j}/64");
            }

            rig.Report().Mode.Should().Be(mode, assign.Method.Name);
            rig.Report().FallbackReason.Should().BeNull();
        }
    }

    [Fact]
    public void A_hotplug_rebinds_off_the_hot_path_from_the_new_map()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
        rig.Loop.SetDeviceClocks(rig.Map(Clocks.MissingH2));
        rig.Report().FallbackReason.Should().Be("no frame rendered since the last compile", "a rebind clears what the last frame used");
        rig.AtCounter(Big + 20.0);
        rig.Report().FallbackReason.Should().Contain("no device clock for");
        rig.Loop.SetDeviceClocks(rig.Map(Clocks.Shared));
        rig.Loop.SetControllers(rig.Controllers);
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock, "the recompiled plan binds the current map");
    }

    [Fact]
    public void Without_the_transmitted_timing_of_every_companion_the_clock_is_not_used()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared, report: false);
        using var legacy = new Rig(SharedFrames, sets);
        rig.Anchor(T0);
        legacy.Anchor(T0);
        rig.Render(t: T0 + 0.3).Should().Be(legacy.At(T0 + 0.3));
        rig.Report().FallbackReason.Should().Be("transmitted timing of lianli:rf:35-F7-EF-72-AB-3C not reported for its current effect index");
        rig.Report().UploadedFrames.Should().BeNull();
        for (var i = 0; i < 3; i++)
        {
            rig.Loop.ReportLoopTiming(rig.Chains[i].Id, new ZoneId(0), rig.Loops[i].EffectIndex, 20, 40, false);
        }

        rig.Render(t: T0 + 0.3).Should().Be(legacy.At(T0 + 0.3), "three of four companions reported");
        rig.Report().FallbackReason.Should().Contain("lianli:rf:EF-7E-63-62-32-E1").And.Contain("not reported");
        rig.Loop.ReportLoopTiming(rig.Chains[3].Id, new ZoneId(0), rig.Loops[3].EffectIndex, 20, 40, false);
        FrameOf(rig.AtCounter(Big - (Big % 800) + 100.0)).Should().Be(2);
        var report = rig.Report();
        report.Mode.Should().Be(StreamedClockMode.DeviceClock);
        (report.UploadedFrames, report.UploadedInterval).Should().Be((20, 40.0), "from the callback data");
    }

    [Theory]
    [InlineData(0, 10, 80.0, false, "sent 10 x 80, compiled 20 x 40")] // decimated, same nominal period: frames differ
    [InlineData(0, 20, 40.01, false, "sent 20 x 40.01, compiled 20 x 40")] // a tiny period rounding: 800.2 vs 800 ticks
    [InlineData(3, 1, 40.0, true, "sent 1 x 40 static, compiled 20 x 40")] // a static fallback on a companion
    public void A_transmitted_timing_that_differs_from_the_compiled_reference_falls_back_truthfully(int chain, int frames, double interval, bool still, string detail)
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        using var legacy = new Rig(SharedFrames, sets);
        rig.Anchor(T0);
        legacy.Anchor(T0);
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);

        rig.Loop.ReportLoopTiming(rig.Chains[chain].Id, new ZoneId(0), rig.Loops[chain].EffectIndex, frames, interval, still);
        for (var j = 0; j < 64; j++)
        {
            rig.Render(t: T0 + (j / 64.0)).Should().Be(legacy.At(T0 + (j / 64.0)), "no lock against a period the device does not play");
        }

        var report = rig.Report();
        report.Mode.Should().Be(StreamedClockMode.UploadAnchor);
        report.FallbackReason.Should().Contain(detail).And.Contain("unsupported").And.Contain(rig.Chains[chain].Id.Value);
        report.ReferenceFrame.Should().BeNull();
        if (chain == 0)
        {
            (report.UploadedFrames, report.UploadedInterval).Should().Be((frames, interval), "the status shows what was actually sent");
        }

        // a later correct report for the same index (e.g. a re-upload of the same content) restores the lock
        rig.Loop.ReportLoopTiming(rig.Chains[chain].Id, new ZoneId(0), rig.Loops[chain].EffectIndex, 20, 40, false);
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
    }

    [Fact]
    public void A_late_completion_of_a_superseded_index_never_poisons_the_newer_plan()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        var old = rig.Loops.Select(l => l.EffectIndex).ToArray();
        rig.Loop.RepublishLoops();
        rig.Loops = rig.DrainLoops();
        rig.Loops[0].EffectIndex.Should().NotBe(old[0]);

        rig.Loop.ReportLoopTiming(rig.Chains[0].Id, new ZoneId(0), old[0], 10, 80, false); // the old upload completes late, decimated
        rig.AtCounter(Big + 20.0);
        rig.Report().FallbackReason.Should().Contain("not reported", "the old index's report is ignored, not taken for the new one");
        rig.ReportUploads();
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
        rig.Loop.ReportLoopTiming(rig.Chains[0].Id, new ZoneId(0), old[0], 10, 80, false); // and once more after the new one
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock, "a superseded completion never replaces the newer verdict");
    }

    [Fact]
    public void A_changed_scene_and_a_reconnect_need_the_new_indices_reported()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        sets.Add(FrameSetFor("other", [78, 78, 78, 24, 1]) with { IntervalMs = 60 });
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);

        rig.Loop.SetScene(new Scene([SharedFrames(rig.ChainRefs(), new ZoneRef(Gpu, new ZoneId(0))) with { FrameSetId = "other" }]));
        rig.Loops = rig.DrainLoops();
        rig.AtCounter(Big + 20.0);
        rig.Report().FallbackReason.Should().Contain("not reported", "a changed scene's loops are new uploads");
        rig.ReportUploads();
        rig.AtCounter(Big + 30.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
        rig.Report().ReferenceInterval.Should().Be(60);

        // the H2 chain disconnects and returns: its loop is re-sent under a new index
        rig.Loop.SetControllers(rig.Controllers.Where(c => c.Id != rig.Chains[3].Id).ToArray());
        rig.Loop.SetControllers(rig.Controllers);
        var bus = rig.DrainLoopsWhereAvailable();
        bus.Should().ContainKey(rig.Chains[3].Id);
        rig.AtCounter(Big + 30.0);
        rig.Report().FallbackReason.Should().Contain("not reported", "the re-planned group's loops are new uploads");
        foreach (var (id, loop) in bus)
        {
            loop.EffectIndex.Should().NotBe(rig.Loops[Array.FindIndex(rig.Chains, c => c.Id == id)].EffectIndex);
            rig.Loop.ReportLoopTiming(id, new ZoneId(0), loop.EffectIndex, loop.Frames, loop.IntervalMs, false);
        }
        rig.AtCounter(Big + 30.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
    }

    [Fact]
    public void A_ring_calibration_marker_withdraws_the_lock_until_the_scene_loop_is_back()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        rig.AtCounter(Big + 20.0);
        rig.Report().Mode.Should().Be(StreamedClockMode.DeviceClock);
        rig.Loop.PublishCalibration(rig.Chains[3].Id, new byte[24 * 3]);
        rig.AtCounter(Big + 20.0);
        rig.Report().FallbackReason.Should().Contain("lianli:rf:EF-7E-63-62-32-E1", "the H2 now plays a one-frame marker");
    }

    [Fact]
    public void Two_shared_assignments_sample_one_coherent_phase_across_a_sample_published_mid_frame()
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("a", [78, 78, 1]));
        sets.Add(FrameSetFor("b", [78, 24, 1]));
        var racing = new RacingClock();
        using var rig = new Rig(null, sets, clocks: Clocks.Shared, custom: racing, scene: r => new Scene(
        [
            new([r.ChainRefs()[0], r.ChainRefs()[1], new ZoneRef(Gpu, new ZoneId(0))], "StaticColor", P(), "a", ContentStrategy.Frames) { FrameSetId = "a" },
            new([r.ChainRefs()[2], r.ChainRefs()[3], new ZoneRef(Gpu2, new ZoneId(0))], "StaticColor", P(), "b", ContentStrategy.Frames) { FrameSetId = "b" },
        ]));
        var domain = DeviceClockDomain.Next("race");
        for (var k = 0; k < 40; k++)
        {
            // S1 read at t1; S2, consistent with it, is published 1.02 s later, after this frame's host time t1 + 1.0125 s + k × 25 ms
            var t1 = rig.Now;
            var b0 = (uint)(Big - (Big % 800));
            racing.Arm(
                new DeviceClockSample(domain, b0, t1, Tps, t1),
                new DeviceClockSample(domain, b0 + 1632 + (uint)(k * 40), t1 + Ts(1.02 + (k * 0.025)), Tps, t1 + Ts(1.02 + (k * 0.025))));
            rig.Now = t1 + Ts(1.0125 + (k * 0.025));
            var (a, b) = rig.RenderBoth();
            var expected = (int)Math.Floor(Wrap(1620.0 + (k * 40), 800) / 40);
            (FrameOf(a), FrameOf(b)).Should().Be((expected, expected), $"step {k}: one coherent phase for both assignments");
            var reports = rig.Loop.ClockReport();
            reports.Should().HaveCount(2).And.OnlyContain(r => r.Mode == StreamedClockMode.DeviceClock, "a slightly newer sample is no fallback flicker");
        }
    }

    [Theory]
    [InlineData(-0.49, true)]
    [InlineData(-0.6, false)]
    public void A_sample_far_in_the_future_is_invalid_metadata(double ageSeconds, bool accepted)
    {
        var sets = new InMemoryFrameSetSource();
        sets.Add(FrameSetFor("shared", [78, 78, 78, 24, 1]));
        using var rig = new Rig(SharedFrames, sets, clocks: Clocks.Shared);
        rig.Publish(Big - (Big % 800) + 820.0, ageSeconds);
        var frame = FrameOf(rig.Render());
        var report = rig.Report();
        if (accepted)
        {
            report.Mode.Should().Be(StreamedClockMode.DeviceClock);
            frame.Should().Be((int)Math.Floor(Wrap(820 + (ageSeconds * Tps), 800) / 40), "extrapolated backwards");
        }
        else
        {
            report.Mode.Should().Be(StreamedClockMode.UploadAnchor);
            report.FallbackReason.Should().Contain("in the future");
        }
    }

    // ---------------------------------------------------------------- harness

    public enum Clocks
    {
        None,
        Shared,
        TwoDomains,
        MissingH2,
    }

    private static long Ts(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    /// <summary>The largest multiple of <paramref name="period"/> ticks not above <see cref="Big"/>.</summary>
    private static double Base(double period) => Big - Wrap(Big, period);

    private static EffectParams P() => new([Red], 1f, 1f, Direction.Forward);

    private static Assignment VendorBreathing(ZoneRef[] chains, ZoneRef gpu) => Assignment.ForGroup("g", [.. chains, gpu], "Breathing", P());

    /// <summary>The live uniform contrast scene: Program ColorCycle over R / G / B, speed .5, brightness .35.</summary>
    private static Assignment ProgramColorCycle(ZoneRef[] chains, ZoneRef gpu) =>
        Assignment.ForGroup("g", [.. chains, gpu], "ColorCycle", new EffectParams([Red, Green, Blue], 0.5f, 0.35f, Direction.Forward))
            with { Strategy = ContentStrategy.Program };

    private static Assignment VendorBreathing35(ZoneRef[] chains, ZoneRef gpu) =>
        Assignment.ForGroup("g", [.. chains, gpu], "Breathing", new EffectParams([Red], 1f, 0.35f, Direction.Forward));

    private static Assignment SharedFrames(ZoneRef[] chains, ZoneRef gpu) =>
        new([.. chains, gpu], "StaticColor", P(), "g", ContentStrategy.Frames) { FrameSetId = "shared" };

    private static Assignment LocalFrames(ZoneRef[] chains, ZoneRef gpu) =>
        new([gpu], "StaticColor", P(), null, ContentStrategy.Frames) { FrameSetId = "gpu" };

    private static Assignment StreamedOnly(ZoneRef[] chains, ZoneRef gpu) => Assignment.ForZone(gpu, "Breathing", P()) with { Strategy = ContentStrategy.Program };

    private static Assignment PerDeviceRoute(ZoneRef[] chains, ZoneRef gpu) => Assignment.ForGroup("g", [.. chains, gpu], "RainbowMorph", P());

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

    private sealed class FakeClock : IDeviceClock
    {
        public DeviceClockSample? Latest { get; set; }
    }

    /// <summary>Returns <c>first</c> once, then <c>second</c>: a sample published while a frame is being rendered.</summary>
    private sealed class RacingClock : IDeviceClock
    {
        private DeviceClockSample? _first;
        private DeviceClockSample? _second;

        public DeviceClockSample? Latest
        {
            get
            {
                var s = _first ?? _second;
                _first = null;
                return s;
            }
        }

        public void Arm(DeviceClockSample first, DeviceClockSample second) => (_first, _second) = (first, second);
    }

    /// <summary>The live chains plus one streamed GPU point; every bound chain shares <see cref="Clock"/> unless stated.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly FrameBus _bus = new();
        private readonly FakeClock _other = new();
        private readonly IDeviceClock? _custom;

        public Rig(Func<ZoneRef[], ZoneRef, Assignment>? assign, IFrameSetSource? sets = null, PlaybackCalibration? calibration = null,
            Clocks clocks = Clocks.None, EffectRegistry? registry = null, bool report = true, Func<Rig, Scene>? scene = null, IDeviceClock? custom = null)
        {
            _custom = custom;
            Loop = new RenderLoop(registry ?? EffectRegistry.Default, _bus)
            {
                ChainReversed = id => id.Value == "lianli:rf:1B-7B-F1-72-AB-3C",
                Ring = id => id.Value == "lianli:rf:EF-7E-63-62-32-E1" ? new RingGeometry(true, false, 0) : default,
                FrameSets = sets,
                Player = Player,
                Timestamp = () => Now,
            };
            if (clocks != Clocks.None)
            {
                Loop.SetDeviceClocks(Map(clocks));
            }

            Loop.SetControllers(Controllers);
            if (calibration is not null)
            {
                Loop.SetPlaybackCalibration(calibration);
            }

            Loop.SetScene(scene?.Invoke(this) ?? new Scene([assign!(ChainRefs(), new ZoneRef(Gpu, new ZoneId(0)))]));
            Loops = DrainLoops();
            if (report)
            {
                ReportUploads();
            }

            Publish(Big, ageSeconds: 0);
        }

        /// <summary>The chains plus two streamed GPU points (the second only used by two-assignment cases).</summary>
        public ControllerDescriptor[] Controllers =>
            [.. Chains, TestSupport.Controller(Gpu.Value, ZoneLayout.Point), TestSupport.Controller(Gpu2.Value, ZoneLayout.Point)];

        /// <summary>The adapter's completed-upload reports: each drained loop transmitted exactly as compiled (no decimation).</summary>
        public void ReportUploads()
        {
            for (var i = 0; i < Chains.Length; i++)
            {
                Loop.ReportLoopTiming(Chains[i].Id, new ZoneId(0), Loops[i].EffectIndex, Loops[i].Frames, Loops[i].IntervalMs, false);
            }
        }

        /// <summary>LED 0 of frame <paramref name="frame"/> of uploaded loop <paramref name="loop"/>, as published to the adapter.</summary>
        public Rgb LoopPixel(int loop, int frame)
        {
            var l = Loops[loop];
            var o = frame * l.LedCount * 3;
            var rgb = l.Rgb.Span;
            return new Rgb(rgb[o], rgb[o + 1], rgb[o + 2]);
        }

        public ControllerDescriptor[] Chains { get; } =
        [
            Chain("lianli:rf:35-F7-EF-72-AB-3C", "TL", 3, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:06-A8-F0-72-AB-3C", "TL", 3, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:1B-7B-F1-72-AB-3C", "TL", 3, ZoneLayout.Matrix(39, 2)),
            Chain("lianli:rf:EF-7E-63-62-32-E1", "H2", 1, ZoneLayout.Ring(24)),
        ];

        public FakeClock Clock { get; } = new();

        public DeviceClockDomain Domain { get; set; } = DeviceClockDomain.Next("test");

        public RecordingPlayer Player { get; } = new();

        public RenderLoop Loop { get; }

        public LoopFrames[] Loops { get; set; }

        /// <summary>Host time (Stopwatch ticks) the render loop reads.</summary>
        public long Now { get; set; } = Ts(1000);

        public long SampleAt { get; private set; }

        /// <summary>The reference (first) loop's frames and wire interval.</summary>
        public (int Frames, double Interval) Reference => (Loops[0].Frames, Math.Round(Loops[0].IntervalMs * 100) / 100);

        public long LoopsPublished => Chains.Sum(c => _bus[c.Id]!.LoopsPublished);

        public ZoneRef[] ChainRefs() => Chains.Select(c => new ZoneRef(c.Id, new ZoneId(0))).ToArray();

        public Dictionary<ControllerId, IDeviceClock> Map(Clocks clocks)
        {
            var map = new Dictionary<ControllerId, IDeviceClock>();
            foreach (var c in Chains)
            {
                var h2 = c.Id.Value == "lianli:rf:EF-7E-63-62-32-E1";
                if (clocks == Clocks.MissingH2 && h2)
                {
                    continue;
                }

                map[c.Id] = _custom ?? (clocks == Clocks.TwoDomains && h2 ? _other : Clock);
            }

            map[Gpu] = new FakeClock(); // a streamed controller's entry is never consulted
            return map;
        }

        public Dictionary<ControllerId, LoopFrames> DrainLoopsWhereAvailable() =>
            Chains.Where(c => _bus[c.Id]!.HasPendingLoop).ToDictionary(c => c.Id, c =>
            {
                _bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
                return pending.Loop;
            });

        public LoopFrames[] DrainLoops() => Chains.Select(c =>
        {
            _bus[c.Id]!.TryConsumeLoop(out var pending).Should().BeTrue();
            return pending.Loop;
        }).ToArray();

        /// <summary>Publishes a sample of <paramref name="counter"/> (mod 2³²) read <paramref name="ageSeconds"/> before <see cref="Now"/>.</summary>
        public void Publish(double counter, double ageSeconds)
        {
            SampleAt = Now - Ts(ageSeconds);
            var raw = (uint)(long)Wrap(Math.Floor(counter), DeviceClockSample.CounterModulus);
            Clock.Latest = new DeviceClockSample(Domain, raw, SampleAt, Tps, SampleAt - Ts(0.002));
            _other.Latest = new DeviceClockSample(DeviceClockDomain.Next("other"), raw, SampleAt, Tps, SampleAt);
            var fraction = counter - Math.Floor(counter);
            if (fraction != 0)
            {
                Now = SampleAt + Ts(ageSeconds + (fraction / Tps)); // the sub-tick part as host time
            }
        }

        /// <summary>The GPU pixel when the extrapolated counter is <paramref name="counter"/> (a fresh sample, its fraction as age).</summary>
        public Rgb AtCounter(double counter, double dt = 0)
        {
            Publish(counter, ageSeconds: 0);
            return Render(dt);
        }

        public Rgb Render(double dt = 0, double t = 100)
        {
            Loop.RenderFrame(t, dt);
            _bus[Gpu]!.TryConsume(out var frame).Should().BeTrue();
            return frame.Zones[0][0];
        }

        /// <summary>One frame; both GPU points' pixels.</summary>
        public (Rgb A, Rgb B) RenderBoth(double t = 100)
        {
            Loop.RenderFrame(t, 0);
            _bus[Gpu]!.TryConsume(out var a).Should().BeTrue();
            _bus[Gpu2]!.TryConsume(out var b).Should().BeTrue();
            return (a.Zones[0][0], b.Zones[0][0]);
        }

        public Rgb At(double t) => Render(0, t);

        public void Anchor(double t0) => Loop.AnchorLoop(Loops[0].EffectIndex, t0);

        public StreamedClockReport Report() => Loop.ClockReport().Should().ContainSingle().Subject;

        public DeviceClockDomain NewDomain() => Domain = DeviceClockDomain.Next("test");

        public void Dispose()
        {
            Loop.Dispose();
            _bus.Dispose();
        }

        private static ControllerDescriptor Chain(string id, string family, int units, ZoneLayout layout) =>
            new(new ControllerId(id), "Lian Li", id, DeviceType.Cooler, DeviceClass.Virtual, id,
                [new ZoneDescriptor(new ZoneId(0), "chain", layout) { Family = family, Units = units }], Capabilities.UploadedLoopOnly(200, 20, 12288));
    }

    private sealed class RecordingPlayer : IStreamedLoopPlayer
    {
        public List<(double Tt, double IntervalMs, int Frames, int Result)> Calls { get; } = [];

        public int FrameAt(double tt, double intervalMs, int frames)
        {
            var k = StreamedLoopPlayer.Default.FrameAt(tt, intervalMs, frames);
            Calls.Add((tt, intervalMs, frames, k));
            return k;
        }
    }

    /// <summary>A loopable probe recording the T and Dt it is rendered with.</summary>
    private sealed class Probe(double loopSeconds) : EffectGenerator("ClockProbe",
        new EffectParamSchema("probe", "probe", 1, 1, true, false, [Rgb.White])), ILoopable
    {
        public double T { get; private set; }

        public double Dt { get; private set; }

        public double LoopSeconds(in EffectParams p) => loopSeconds;

        public override void Render(in RenderContext ctx, Span<Rgb> output)
        {
            T = ctx.T;
            Dt = ctx.Dt;
            output.Fill(Rgb.Black);
        }
    }
}
