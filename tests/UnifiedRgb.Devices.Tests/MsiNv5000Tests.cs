using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Nvapi;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>NATIVE-1 phase A, offline: NV5000 family decode, frame bytes, firewall policy and the complete-frame budget.</summary>
public class Nv5000ProtocolTests
{
    [Theory]
    [InlineData(new byte[] { 0x91, 0x50, 0x00, 0x09 }, true, true)]
    [InlineData(new byte[] { 0x81, 0x12, 0x34, 0x01 }, true, true)]
    [InlineData(new byte[] { 0x10, 0x20, 0x00, 0x05 }, true, false)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, false, false)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, false, false)]
    public void Family_decode_and_gate(byte[] data, bool valid, bool nv5000)
    {
        Nv5000Family.TryDecode(0, 4, data, out var f, out var why).Should().Be(valid, why);
        if (valid)
        {
            f.IsNv5000.Should().Be(nv5000);
            f.LedType.Should().Be(data[0]);
            f.ModuleType.Should().Be((ushort)((data[1] << 8) | data[2]));
            f.FwVersion.Should().Be(data[3]);
        }
    }

    [Theory]
    [InlineData(-5, 4, "NVAPI status -5")]
    [InlineData(0, 3, "returned size 3")]
    [InlineData(0, 5, "returned size 5")]
    [InlineData(0, 0, "returned size 0")]
    public void Family_decode_rejects_status_and_returned_size(int status, int returned, string expected)
    {
        Nv5000Family.TryDecode(status, returned, [0x91, 0x50, 0x00, 0x09], out _, out var why).Should().BeFalse();
        why.Should().StartWith(expected);
    }

    [Fact]
    public void Frames_are_byte_exact_and_bounded_to_34_records()
    {
        Nv5000Frames.Session(0).Should().Equal(0x68, 0x06, 0x00);
        Nv5000Frames.Session(1).Should().Equal(0x68, 0x06, 0x01);
        var f = Nv5000Frames.Frame([new Rgb(1, 2, 3), new Rgb(4, 5, 6)]);
        f.Should().Equal(0x68, 0x04, 0x01, 0, 1, 2, 3, 0x01, 1, 4, 5, 6, 0x01);
        Nv5000Frames.Black(34).Should().HaveCount(3 + (5 * 34)).And.HaveCount(Nv5000.WireLength(34));
        var tooMany = () => Nv5000Frames.Frame(new Rgb[35]);
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
        var none = () => Nv5000Frames.Frame([]);
        none.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Chain_admits_exactly_the_recovered_writes_and_read()
    {
        var rec = new RecordingTransport();
        var budget = new FrameBudget(20);
        var chain = MsiNv5000Adapter.BuildChain(rec, budget, 34, out var fw);
        using (budget.BeginControl())
        {
            await chain.SendAsync(Nv5000Frames.Session(0), default);
            await chain.SendAsync(Nv5000Frames.Black(34), default);
            await chain.SendAsync(Nv5000Frames.Session(1), default);
        }

        rec.SentFrames.Should().HaveCount(3);
        fw.EnsureRead(0x68, 0xEC, 4);

        byte[][] refused =
        [
            [0x68, 0x06, 0x02], // only 0x00 / 0x01
            [0x68, 0x05, 0x00], // unknown register
            [0x69, 0x06, 0x00], // other device
            [0x68, 0x04, 0x02, 0, 0, 0, 0, 1], // header not 0x01
            [0x68, 0x04, 0x01, 0, 0, 0, 0, 2], // trailer not 0x01
            [0x68, 0x04, 0x01, 34, 0, 0, 0, 1], // index 34 is outside the evidenced 0..33
            [0x68, 0x04, 0x01, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1], // indices not strictly increasing
            [0x68, 0x3F, 0x00], // never a save/flash register
        ];
        foreach (var frame in refused)
        {
            using (budget.BeginControl())
            {
                var act = async () => await chain.SendAsync(frame, default);
                await act.Should().ThrowAsync<InvalidOperationException>(Convert.ToHexString(frame));
            }
        }

        var oversized = new byte[Nv5000.WireLength(35)];
        (oversized[0], oversized[1], oversized[2]) = (0x68, 0x04, 0x01);
        using (budget.BeginControl())
        {
            var act = async () => await chain.SendAsync(oversized, default);
            await act.Should().ThrowAsync<InvalidOperationException>("35 records exceed the validator and the firewall");
        }

        var outside = async () => await chain.SendAsync(Nv5000Frames.Session(0), default);
        await outside.Should().ThrowAsync<InvalidOperationException>("no send outside a frame or control scope");

        foreach (var (reg, len) in new[] { ((byte)0xEC, 5), ((byte)0xEC, 3), ((byte)0x00, 4), ((byte)0x06, 1) })
        {
            var read = () => fw.EnsureRead(0x68, reg, len);
            read.Should().Throw<ForbiddenRegisterException>();
        }

        rec.SentFrames.Should().HaveCount(3, "nothing refused reached the bus");
        var widen = () => I2cFirewallPolicy.MsiNv5000(35);
        widen.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Smaller_configured_bound_is_enforced()
    {
        var rec = new RecordingTransport();
        var budget = new FrameBudget(20);
        var chain = MsiNv5000Adapter.BuildChain(rec, budget, 8, out _);
        using (budget.BeginControl())
        {
            await chain.SendAsync(Nv5000Frames.Black(8), default);
            var nine = async () => await chain.SendAsync(Nv5000Frames.Black(9), default);
            await nine.Should().ThrowAsync<InvalidOperationException>();
        }

        rec.SentFrames.Should().HaveCount(1);
    }
}

/// <summary>NATIVE design §9: complete-frame cap with configured ceilings and the measured EffectiveFps below the ceiling.</summary>
public class FrameBudgetTests
{
    private long _now;

    [Theory]
    [InlineData(0, 20)]
    [InlineData(40, 25)]
    [InlineData(50, 20)]
    public async Task Gpu_ceiling_follows_the_configured_budget(int budgetMs, int fps)
    {
        var a = new MsiNv5000Adapter(new FakeNvapi(), new MsiGpuOptions { FrameBudgetMs = budgetMs });
        var d = (await a.DiscoverAsync(default)).Single();
        d.Capabilities.MaxFps.Should().Be(fps);
        a.FrameBudget(d.Id)!.CeilingFps.Should().Be(fps);
        a.FrameBudget(d.Id)!.FpsSource.Should().Be(FrameBudget.ConfiguredCeiling);
    }

    [Fact]
    public void Ceiling_interval_is_strict()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        Frame(b, 1).Should().BeTrue();
        _now = 49;
        Frame(b, 1).Should().BeFalse("49 ms < 50 ms");
        _now = 50;
        Frame(b, 1).Should().BeTrue();
        b.Stats().Skips.Should().Be(1);
        b.Stats().Frames.Should().Be(2);
    }

    [Fact]
    public void In_flight_frame_refuses_the_next()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        using var first = b.TryBeginFrame()!;
        _now = 500;
        b.TryBeginFrame().Should().BeNull("the previous frame has not completed");
        first.Complete();
    }

    [Fact]
    public void Measured_effective_fps_below_the_ceiling_is_the_cap()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        for (var i = 0; i < FrameBudget.Window; i++)
        {
            Frame(b, 40).Should().BeTrue($"frame {i}");
            _now += 100;
        }

        var s = b.Stats();
        s.FpsSource.Should().Be(FrameBudget.Measured);
        s.P99Ms.Should().Be(40);
        s.EffectiveFps.Should().Be(16, "⌊1000 / (40 × 1.5)⌋");
        var start = _now;
        Frame(b, 1).Should().BeTrue();
        _now = start + 51;
        Frame(b, 1).Should().BeFalse("the ceiling interval (50 ms) is not enough once the measured cap is 16 fps");
        _now = start + 62;
        Frame(b, 1).Should().BeFalse("62 ms < 62.5 ms");
        _now = start + 63;
        Frame(b, 1).Should().BeTrue();
    }

    [Fact]
    public void Control_sequences_are_exempt_and_out_of_scope_sends_are_refused()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        using (b.BeginControl())
        {
            b.CountOperation();
            b.CountOperation();
        }

        var outside = () => b.CountOperation();
        outside.Should().Throw<InvalidOperationException>();
        var s = b.Stats();
        (s.Controls, s.OutOfScope, s.Frames).Should().Be((1, 1, 0));
    }

    [Fact]
    public void Failed_frame_is_not_sampled()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        using (b.TryBeginFrame())
        {
            b.CountOperation();
        }

        var s = b.Stats();
        (s.Failed, s.Frames, s.Samples).Should().Be((1, 0, 0));
    }

    // ---------------------------------------------------------------- NATIVE-DYNAMIC-SYNC-FIX §B: the pacing query

    [Fact]
    public void Delay_is_zero_before_the_first_start_and_creates_no_member()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        b.DelayUntilNextFrame().Should().Be(TimeSpan.Zero);
        b.DelayUntilNextFrame("0x70").Should().Be(TimeSpan.Zero);
        b.Stats().Should().Be(new FrameBudget(20, () => _now, 1000).Stats(), "nothing was counted");
        var act = () => b.DelayUntilNextFrame(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Delay_is_exactly_the_deadline_try_begin_frame_enforces()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        Frame(b, 7).Should().BeTrue();
        b.DelayUntilNextFrame().Should().Be(TimeSpan.FromMilliseconds(43), "50 ms from the start, 7 ms of it spent sending");
        for (var t = 7; t < 50; t++)
        {
            _now = t;
            b.DelayUntilNextFrame().Should().Be(TimeSpan.FromMilliseconds(50 - t));
            b.DelayUntilNextFrame().Should().BePositive();
        }

        var stats = b.Stats();
        _now = 49;
        Frame(b, 1).Should().BeFalse("positive delay → refused");
        b.Stats().Skips.Should().Be(stats.Skips + 1, "only TryBeginFrame counts skips");
        _now = 50;
        b.DelayUntilNextFrame().Should().Be(TimeSpan.Zero);
        Frame(b, 1).Should().BeTrue("zero delay → admitted");
        _now = 500;
        b.DelayUntilNextFrame().Should().Be(TimeSpan.Zero, "long overdue is zero, never negative");
    }

    [Fact]
    public void A_sub_tick_remainder_is_rounded_up_never_to_zero()
    {
        var b = new FrameBudget(30, () => _now, 1000); // 33.33… ms interval on a 1 ms clock
        Frame(b, 0).Should().BeTrue();
        _now = 33;
        b.DelayUntilNextFrame().Should().BePositive().And.BeLessThan(TimeSpan.FromMilliseconds(1));
        Frame(b, 0).Should().BeFalse("33 ms < 33.33 ms");
        _now = 34;
        b.DelayUntilNextFrame().Should().Be(TimeSpan.Zero);
        Frame(b, 0).Should().BeTrue();
    }

    [Fact]
    public void Delay_follows_the_measured_effective_interval_after_expensive_frames()
    {
        var b = new FrameBudget(20, () => _now, 1000);
        for (var i = 0; i < FrameBudget.Window; i++)
        {
            Frame(b, 40).Should().BeTrue();
            _now += 100;
        }

        b.Stats().EffectiveFps.Should().Be(16);
        var start = _now;
        Frame(b, 0).Should().BeTrue();
        b.DelayUntilNextFrame().Should().Be(TimeSpan.FromMilliseconds(62.5), "1000 / 16, not the 50 ms ceiling");
        _now = start + 62;
        b.DelayUntilNextFrame().Should().Be(TimeSpan.FromMilliseconds(0.5));
        _now = start + 63;
        b.DelayUntilNextFrame().Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Members_have_their_own_deadlines_and_an_in_flight_frame_is_still_refused()
    {
        var b = new FrameBudget(10, () => _now, 1000);
        Frame(b, 0, "0x70").Should().BeTrue();
        _now = 30;
        Frame(b, 0, "0x71").Should().BeTrue();
        b.DelayUntilNextFrame("0x70").Should().Be(TimeSpan.FromMilliseconds(70));
        b.DelayUntilNextFrame("0x71").Should().Be(TimeSpan.FromMilliseconds(100));
        b.DelayUntilNextFrame("0x72").Should().Be(TimeSpan.Zero);

        _now = 100;
        using var open = b.TryBeginFrame("0x70")!;
        _now = 250;
        b.DelayUntilNextFrame("0x70").Should().Be(TimeSpan.Zero, "the hint reads the deadline only");
        b.TryBeginFrame("0x70").Should().BeNull("admission still refuses a frame in flight");
        open.Complete();
    }

    private bool Frame(FrameBudget b, long durationMs, string member)
    {
        using var scope = b.TryBeginFrame(member);
        if (scope is null)
        {
            return false;
        }

        b.CountOperation();
        _now += durationMs;
        scope.Complete();
        scope.Dispose();
        return true;
    }

    private bool Frame(FrameBudget b, long durationMs)
    {
        using var scope = b.TryBeginFrame();
        if (scope is null)
        {
            return false;
        }

        b.CountOperation();
        _now += durationMs;
        scope.Complete();
        scope.Dispose(); // the sample is taken here, durationMs after the start
        return true;
    }
}
