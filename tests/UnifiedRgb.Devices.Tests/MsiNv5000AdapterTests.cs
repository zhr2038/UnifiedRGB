using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Transports.Nvapi;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// NATIVE-1 phase A, offline against <see cref="FakeNvapi"/>: the family gate, whole frames, release and the overnight
/// hold-black corrections (addendum §4, §9). No packet here reaches a GPU.
/// </summary>
public class MsiNv5000AdapterTests
{
    [Fact]
    public async Task A_due_gpu_keepalive_is_still_a_real_frame_after_cache_hits_are_not_counted()
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (adapter, descriptor) = await Make(new MsiGpuOptions { ControlVerified = true, KeepAliveMs = 1 });
        await adapter.OpenAsync(descriptor.Id, default);
        adapter.WriteFrame(descriptor.Id, [[new Rgb(1, 2, 3)]]).Should().BeTrue();
        var before = _nv.Log.Count;
        await Task.Delay(5);
        _now += 100_000;
        adapter.WriteFrame(descriptor.Id, [[new Rgb(1, 2, 3)]]).Should().BeTrue("a due keep-alive sends the cached contents");
        _nv.Log.Should().HaveCount(before + 1);
        adapter.FrameBudget(descriptor.Id)!.Frames.Should().Be(2);
        await adapter.DisposeAsync();
    }

    private const string Enter = "W 1000 68 06 00";
    private const string Release = "W 1000 68 06 01";
    private const string FamilyRead = "R 1000 68 EC 4";
    private static readonly string Black34 = "W 1000 68 04 " + Hex(new Rgb[34]);

    private readonly FakeNvapi _nv = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<TimeSpan> _sleeps = [];
    private long _now;

    [Fact]
    public async Task Unmapped_gpu_is_one_aggregate_point_zone_at_the_class_ceiling()
    {
        var (a, d) = await Make(new MsiGpuOptions());
        d.Class.Should().Be(DeviceClass.MsiGpu);
        d.DeviceType.Should().Be(DeviceType.Gpu);
        var z = d.Zones.Should().ContainSingle().Subject;
        z.Name.Should().Be("GPU (aggregate, unmapped)");
        z.Layout.Should().Be(ZoneLayout.Point, "Point N = 1, independent of the 34 replicated UI slots");
        z.Mapped.Should().BeFalse();
        d.Capabilities.MaxFps.Should().Be(20);
        a.IsControlVerified(d.Id).Should().BeFalse();
        a.Probes.Should().Equal("fwinfo", "fwinfo-legacy-profile");
        _nv.Log.Should().BeEmpty("discovery sends nothing");
    }

    [Fact]
    public async Task Unverified_open_is_blocked_before_any_packet()
    {
        var (a, d) = await Make(new MsiGpuOptions());
        var open = () => a.OpenAsync(d.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Be("MsiGpu control not verified");
        var write = () => a.WriteFrame(d.Id, [[Rgb.White]]);
        write.Should().Throw<InvalidOperationException>();
        _nv.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Verified_open_reads_the_family_then_enters_and_writes_whole_frames()
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true });
        await a.OpenAsync(d.Id, default);
        _nv.Log.Should().Equal(FamilyRead, Enter);
        _nv.Profiles.Should().AllSatisfy(p => p.Should().Be(NvI2cProfile.MsiNv5000));

        a.WriteFrame(d.Id, [[new Rgb(1, 2, 3)]]).Should().BeTrue();
        var expected = new Rgb[34];
        Array.Fill(expected, new Rgb(1, 2, 3));
        _nv.Log[^1].Should().Be("W 1000 68 04 " + Hex(expected), "the aggregate colour is replicated to every evidenced index");

        _now += Ms(50);
        a.WriteFrame(d.Id, [[new Rgb(1, 2, 3)]]).Should().BeFalse("unchanged means no native write to count");
        _nv.Log.Should().HaveCount(3, "an unchanged frame is not resent while KeepAliveMs is 0");
        a.WriteFrame(d.Id, [[new Rgb(9, 9, 9)]]).Should().BeTrue();
        _nv.Log.Should().HaveCount(4);
        a.WriteFrame(d.Id, [[new Rgb(8, 8, 8)]]).Should().BeFalse("0 ms after the last frame: the 20 fps budget refuses it");
        _nv.Log.Should().HaveCount(4);
        a.FrameBudget(d.Id)!.Skips.Should().Be(1);

        await a.CloseAsync(d.Id);
        _sleeps.Should().Equal(TimeSpan.FromMilliseconds(100));
        _nv.Log[^1].Should().Be(Release);
        await a.CloseAsync(d.Id);
        _nv.Log.Count(l => l == Release).Should().Be(1, "release is never repeated");
    }

    [Fact]
    public async Task The_pacing_delay_is_the_cards_own_budget_deadline_and_never_admission()
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true });
        a.Should().BeAssignableTo<IFramePacingSource>("the writer reads its pacing hint");
        var pacing = a;
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.Zero, "before the first frame");
        pacing.DelayUntilNextFrame(new ControllerId("msigpu:none")).Should().Be(TimeSpan.Zero);
        await a.OpenAsync(d.Id, default);
        a.WriteFrame(d.Id, [[new Rgb(1, 2, 3)]]).Should().BeTrue();
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.FromMilliseconds(50), "one 20 fps interval");
        _now += Ms(30);
        var before = a.FrameBudget(d.Id)!;
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.FromMilliseconds(20));
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.FromMilliseconds(20));
        a.FrameBudget(d.Id).Should().Be(before, "side-effect free");
        a.WriteFrame(d.Id, [[new Rgb(9, 9, 9)]]).Should().BeFalse("still inside the interval");
        _now += Ms(20);
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.Zero);
        a.WriteFrame(d.Id, [[new Rgb(9, 9, 9)]]).Should().BeTrue("due when the delay reaches zero");
        a.FrameBudget(d.Id)!.Skips.Should().Be(1);
        await a.CloseAsync(d.Id);
    }

    [Theory]
    [InlineData(new byte[] { 0x10, 0x20, 0x00, 0x05 }, 0, 4, "GPU family not NV5000*")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, 4, "*not established*")]
    [InlineData(new byte[] { 0x91, 0x50, 0x00, 0x09 }, 0, 3, "*not established*")]
    [InlineData(new byte[] { 0x91, 0x50, 0x00, 0x09 }, 0, 8, "*not established*")]
    [InlineData(new byte[] { 0x91, 0x50, 0x00, 0x09 }, -5, 4, "*not established*")]
    public async Task Family_gate_failure_blocks_open_without_any_write(byte[] data, int status, int returned, string reason)
    {
        _nv.Reads.Enqueue((status, data, returned));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true });
        var open = () => a.OpenAsync(d.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Match(reason);
        _nv.Log.Should().Equal(FamilyRead);
    }

    [Fact]
    public async Task Mapped_zones_fill_their_indices_and_leave_the_rest_black()
    {
        var maps = new MapSource(new NativeControllerMap("E3m fixture", [
            new NativeZoneMap("Upper", [0, 2, 4]),
            new NativeZoneMap("Lower", [1, 3]),
            new NativeZoneMap("Badge", [33], "Point"),
        ]));
        _nv.Reads.Enqueue((0, [0x81, 0x12, 0x34, 0x01], 4));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true }, maps);
        d.Zones.Select(z => (z.Name, z.Mapped, z.Layout.LedCount)).Should().Equal(("Upper", true, 3), ("Lower", true, 2), ("Badge", true, 1));
        await a.OpenAsync(d.Id, default);
        Rgb r = new(255, 0, 0), b = new(0, 0, 255), g = new(0, 255, 0);
        a.WriteFrame(d.Id, [[r, r, r], [b, b], [g]]).Should().BeTrue();
        var expected = new Rgb[34];
        (expected[0], expected[2], expected[4], expected[1], expected[3], expected[33]) = (r, r, r, b, b, g);
        _nv.Log[^1].Should().Be("W 1000 68 04 " + Hex(expected));
        var perZone = () => a.WriteFrame(d.Id, new ZoneId(0), [r]);
        perZone.Should().Throw<ArgumentException>("multi-zone GPUs take whole frames only");
    }

    [Theory]
    [InlineData(34)] // index outside 0..33
    [InlineData(-1)]
    public async Task Invalid_map_falls_back_to_the_aggregate_zone(int index)
    {
        var maps = new MapSource(new NativeControllerMap(null, [new NativeZoneMap("Bad", [0, index])]));
        var (_, d) = await Make(new MsiGpuOptions(), maps);
        d.Zones.Single().Mapped.Should().BeFalse();
    }

    [Fact]
    public async Task Bringup_open_sends_nothing_and_probes_need_the_session()
    {
        var (a, d) = await Make(new MsiGpuOptions());
        var outside = () => a.RunProbeAsync(d.Id, "fwinfo", default);
        await outside.Should().ThrowAsync<InvalidOperationException>();
        var marker = () => a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.White), default);
        await marker.Should().ThrowAsync<InvalidOperationException>();
        await a.OpenForBringupAsync(d.Id, default);
        _nv.Log.Should().BeEmpty();
        var unknown = () => a.RunProbeAsync(d.Id, "flash", default);
        await unknown.Should().ThrowAsync<ArgumentException>();
        _nv.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Fwinfo_probe_reports_raw_fields_and_sets_the_gate()
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions());
        await a.OpenForBringupAsync(d.Id, default);
        var r = await a.RunProbeAsync(d.Id, "fwinfo", default);
        r.Ok.Should().BeTrue();
        r.Fields.Should().Contain(new Dictionary<string, string>
        {
            ["profile"] = NvI2cProfile.MsiNv5000.Name,
            ["status"] = "0",
            ["returnedSize"] = "4",
            ["raw"] = "91500009",
            ["expected"] = "91500009",
            ["comm"] = "ok",
            ["readback"] = "none",
            ["LEDType"] = "0x91",
            ["ModuleType"] = "0x5000",
            ["FWVersion"] = "0x09",
            ["gate"] = "pass",
        });
        r.Rows.Single().ResponseHex.Should().Be("91500009");
        a.IsControlVerified(d.Id).Should().BeFalse("a passing family read never sets ControlVerified");
        _nv.Log.Should().Equal(FamilyRead);
    }

    [Theory]
    [InlineData(0, 3, "returned size 3, expected 4")]
    [InlineData(0, 6, "returned size 6, expected 4")]
    [InlineData(-9, 4, "NVAPI status -9")]
    [InlineData(0, 4, "implausible bytes FFFFFFFF")]
    public async Task Fwinfo_probe_validates_status_and_returned_size(int status, int returned, string invalid)
    {
        _nv.Reads.Enqueue((status, status == 0 && invalid.StartsWith("impl", StringComparison.Ordinal) ? [0xFF, 0xFF, 0xFF, 0xFF] : [0x91, 0x50, 0x00, 0x09], returned));
        var (a, d) = await Make(new MsiGpuOptions());
        await a.OpenForBringupAsync(d.Id, default);
        var r = await a.RunProbeAsync(d.Id, "fwinfo", default);
        r.Ok.Should().BeFalse();
        r.Fields["invalid"].Should().Be(invalid);
        r.Fields["gate"].Should().Be("invalid");
        r.Fields["returnedSize"].Should().Be(returned.ToString(System.Globalization.CultureInfo.InvariantCulture));
        r.Fields.Should().NotContainKey("ModuleType");
        var m = await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.White), default);
        m.Ok.Should().BeFalse();
        m.Summary.Should().Contain("family gate not passed");
        _nv.Log.Should().Equal(FamilyRead);
    }

    [Fact]
    public async Task Legacy_profile_probe_never_sets_the_gate()
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions());
        await a.OpenForBringupAsync(d.Id, default);
        var r = await a.RunProbeAsync(d.Id, "fwinfo-legacy-profile", default);
        r.Fields["gate"].Should().Be("not-applied (pass; diagnosis only)");
        _nv.Profiles.Should().Equal(NvI2cProfile.LegacyOpenRgb);
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.White), default)).Ok.Should().BeFalse();
        _nv.Log.Should().ContainSingle();
    }

    [Fact]
    public async Task Marker_is_a_full_bounded_frame_and_release_follows_the_delay()
    {
        var (a, d) = await Gated(new MsiGpuOptions());
        var m = await a.BeginMarkerAsync(d.Id, new NativeMarker(5, 3, new Rgb(255, 255, 255)), default);
        m.Ok.Should().BeTrue();
        m.Summary.Should().Contain("playback unobserved");
        m.Fields["comm"].Should().Be("ok");
        m.Fields["records"].Should().Be("34");
        var expected = new Rgb[34];
        expected.AsSpan(5, 3).Fill(Rgb.White);
        _nv.Log.Skip(1).Should().Equal(Enter, "W 1000 68 04 " + Hex(expected));

        (await a.EndMarkerAsync(d.Id, default)).Ok.Should().BeTrue();
        _nv.Log[^1].Should().Be(Black34);
        (await a.EndMarkerAsync(d.Id, default)).Summary.Should().Be("no marker to clear");

        var rel = await a.ReleaseAsync(d.Id, default);
        rel.Fields["release"].Should().Be("done");
        _sleeps.Should().Equal(TimeSpan.FromMilliseconds(100));
        _nv.Log[^1].Should().Be(Release);
        var again = await a.ReleaseAsync(d.Id, default);
        again.Fields["release"].Should().Be("not-applicable");
        _nv.Log.Count(l => l == Release).Should().Be(1);
    }

    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(33, 2, 0)]
    [InlineData(34, 1, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(int.MaxValue, 1, 0)] // Start + Count wraps negative: refused before the enter (Codex review)
    [InlineData(1, int.MaxValue, 0)]
    [InlineData(33, int.MaxValue, 0)]
    [InlineData(0, 35, 0)]
    public async Task Markers_outside_the_evidenced_range_are_refused_before_any_packet(int start, int count, int channel)
    {
        var (a, d) = await Gated(new MsiGpuOptions());
        var act = () => a.BeginMarkerAsync(d.Id, new NativeMarker(start, count, Rgb.White, channel), default);
        await act.Should().ThrowAsync<ArgumentException>();
        _nv.Log.Should().Equal(FamilyRead);
    }

    [Fact]
    public async Task Configured_smaller_frame_is_the_marker_bound()
    {
        var (a, d) = await Gated(new MsiGpuOptions { MaxIndices = 8 });
        var act = () => a.BeginMarkerAsync(d.Id, new NativeMarker(8, 1, Rgb.White), default);
        await act.Should().ThrowAsync<ArgumentException>();
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(7, 1, Rgb.White), default)).Ok.Should().BeTrue();
        _nv.Log[^1].Should().HaveLength("W 1000 68 04 ".Length + (2 * (1 + (5 * 8))));
    }

    [Fact]
    public async Task Never_entered_gpu_gets_nothing_on_close_release_or_dispose()
    {
        foreach (var hold in new[] { false, true })
        {
            var nv = new FakeNvapi();
            var a = new MsiNv5000Adapter(nv, new MsiGpuOptions { HoldBlackOnRelease = hold }, time: _time, sleep: _sleeps.Add);
            var d = (await a.DiscoverAsync(default)).Single();
            await a.OpenForBringupAsync(d.Id, default);
            var r = await a.ReleaseAsync(d.Id, default);
            r.Fields["release"].Should().Be("not-applicable");
            r.Fields["comm"].Should().Be("not-attempted");
            await a.CloseAsync(d.Id);
            await a.DisposeAsync();
            nv.Log.Should().BeEmpty($"hold-black {hold}");
        }

        _sleeps.Should().BeEmpty();
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task Hold_black_release_sends_one_black_frame_and_never_releases()
    {
        var (a, d) = await Gated(new MsiGpuOptions { HoldBlackOnRelease = true, KeepAliveMs = 1000 });
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 34, Rgb.Black), default)).Ok.Should().BeTrue();
        var before = _nv.Log.Count;
        var r = await a.ReleaseAsync(d.Id, default);
        r.Ok.Should().BeTrue();
        r.Fields["release"].Should().Be("deferred-hold-black");
        _nv.Log.Skip(before).Should().Equal(Black34);
        _sleeps.Should().BeEmpty();
        a.UnverifiedReason(d.Id).Should().Be("MsiGpu control not verified; hold-black active");

        var second = await a.ReleaseAsync(d.Id, default);
        second.Fields["comm"].Should().Be("not-attempted");
        await a.CloseAsync(d.Id);
        _nv.Log.Skip(before).Should().Equal(Black34);

        _time.ActiveTimers.Should().Be(1);
        _time.Advance(TimeSpan.FromMilliseconds(2500));
        _nv.Log.Skip(before).Should().Equal(Black34, Black34, Black34);

        await a.DisposeAsync();
        _time.ActiveTimers.Should().Be(0);
        _nv.Log.Skip(before).Should().Equal(Black34, Black34, Black34, Black34);
        await a.DisposeAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        _nv.Log.Skip(before).Should().HaveCount(4, "the final black is sent once and keep-alive stops");
        _nv.Log.Should().NotContain(Release);
    }

    [Fact]
    public async Task Hold_black_rejects_non_black_markers_before_any_packet()
    {
        var (a, d) = await Gated(new MsiGpuOptions { HoldBlackOnRelease = true });
        var act = () => a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, new Rgb(1, 0, 0)), default);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only black*");
        _nv.Log.Should().Equal(FamilyRead);
    }

    [Fact]
    public async Task Failed_black_send_is_never_retried()
    {
        var (a, d) = await Gated(new MsiGpuOptions { HoldBlackOnRelease = true, KeepAliveMs = 1000 });
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.Black), default)).Ok.Should().BeTrue();
        _nv.WriteStatus = -3;
        var before = _nv.Log.Count;
        var r = await a.ReleaseAsync(d.Id, default);
        r.Ok.Should().BeFalse();
        r.Fields["comm"].Should().Be("error(-3)");
        r.Summary.Should().Contain("not retried");
        _nv.WriteStatus = 0;

        (await a.ReleaseAsync(d.Id, default)).Fields["comm"].Should().Be("not-attempted");
        await a.OpenForBringupAsync(d.Id, default);
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.Black), default)).Fields["comm"].Should().Be("not-attempted");
        (await a.EndMarkerAsync(d.Id, default)).Fields["comm"].Should().Be("not-attempted", "the marker cannot be cleared after a failed black");
        _time.Advance(TimeSpan.FromSeconds(10));
        await a.DisposeAsync();
        _nv.Log.Skip(before).Should().ContainSingle("only the one failed black frame was attempted");
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task Keep_alive_failure_stops_the_timer()
    {
        var (a, d) = await Gated(new MsiGpuOptions { HoldBlackOnRelease = true, KeepAliveMs = 500 });
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.Black), default)).Ok.Should().BeTrue();
        (await a.ReleaseAsync(d.Id, default)).Ok.Should().BeTrue();
        var before = _nv.Log.Count;
        _nv.WriteStatus = -1;
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _time.ActiveTimers.Should().Be(0);
        _nv.WriteStatus = 0;
        _time.Advance(TimeSpan.FromSeconds(5));
        await a.DisposeAsync();
        _nv.Log.Skip(before).Should().ContainSingle("no retry and no final black after a failed black send");
    }

    [Fact]
    public async Task Hold_black_writer_close_keeps_host_mode_and_refuses_frames()
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true, HoldBlackOnRelease = true });
        await a.OpenAsync(d.Id, default);
        a.WriteFrame(d.Id, [[Rgb.White]]).Should().BeTrue();
        await a.CloseAsync(d.Id);
        _nv.Log[^1].Should().Be(Black34);
        await a.OpenAsync(d.Id, default);
        _now += Ms(1000);
        a.WriteFrame(d.Id, [[Rgb.White]]).Should().BeFalse("hold-black owns the card until a reviewed restart");
        _nv.Log.Should().NotContain(Release);
        _nv.Log.Count(l => l == Enter).Should().Be(1);
    }

    [Fact]
    public async Task Failed_writer_black_blocks_later_frames_and_reopens()
    {
        // addendum §9: after a failed black send nothing more is sent in this process, by the writer or a reopen
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true, HoldBlackOnRelease = true });
        await a.OpenAsync(d.Id, default);
        a.WriteFrame(d.Id, [[Rgb.White]]).Should().BeTrue();
        _nv.WriteStatus = -3;
        await a.CloseAsync(d.Id);
        _nv.WriteStatus = 0;
        var before = _nv.Log.Count;

        var reopen = () => a.OpenAsync(d.Id, default);
        (await reopen.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("black send failed");
        _now += Ms(1000);
        var write = () => a.WriteFrame(d.Id, [[Rgb.White]]);
        write.Should().Throw<InvalidOperationException>("the writer was closed");
        await a.DisposeAsync();
        _nv.Log.Should().HaveCount(before, "no reopen, frame, release or final black follows the failed black send");
    }

    [Fact]
    public async Task Failed_marker_black_under_hold_black_refuses_the_writer_frames_that_follow()
    {
        // the §9 latch is the hold-black (darkness) rule; daytime failures are reported per step as before
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(new MsiGpuOptions { ControlVerified = true, HoldBlackOnRelease = true });
        await a.OpenAsync(d.Id, default);
        await a.OpenForBringupAsync(d.Id, default);
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.Black), default)).Ok.Should().BeTrue();
        _nv.WriteStatus = -1;
        (await a.EndMarkerAsync(d.Id, default)).Ok.Should().BeFalse();
        _nv.WriteStatus = 0;
        var before = _nv.Log.Count;
        _now += Ms(1000);
        a.WriteFrame(d.Id, [[Rgb.White]]).Should().BeFalse("a failed black send stops every later frame");
        _nv.Log.Should().HaveCount(before);
    }

    [Fact]
    public async Task Normal_dispose_releases_an_entered_gpu_once()
    {
        var (a, d) = await Gated(new MsiGpuOptions());
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.White), default)).Ok.Should().BeTrue();
        await a.DisposeAsync();
        await a.DisposeAsync();
        _nv.Log.Count(l => l == Release).Should().Be(1);
        _sleeps.Should().Equal(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Options_cannot_widen_the_evidenced_range_or_shorten_the_release_delay()
    {
        var wide = () => new MsiNv5000Adapter(_nv, new MsiGpuOptions { MaxIndices = 35 });
        wide.Should().Throw<ArgumentOutOfRangeException>();
        var none = () => new MsiNv5000Adapter(_nv, new MsiGpuOptions { MaxIndices = 0 });
        none.Should().Throw<ArgumentOutOfRangeException>();
        var fast = () => new MsiNv5000Adapter(_nv, new MsiGpuOptions { ReleaseDelayMs = 50 });
        fast.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Factory_selects_by_profile_and_never_substitutes()
    {
        MsiGpuAdapterFactory.Create(_nv, new MsiGpuOptions()).Should().BeOfType<MsiNv5000Adapter>();
        MsiGpuAdapterFactory.Create(_nv, new MsiGpuOptions { Profile = MsiGpuOptions.LegacyGlobal }).Should().BeOfType<MsiLegacyAdapter>();
        var bogus = () => MsiGpuAdapterFactory.Create(_nv, new MsiGpuOptions { Profile = "Bogus" });
        bogus.Should().Throw<ArgumentException>().WithMessage("*no adapter is substituted*");
    }

    private static string Hex(Rgb[] records) => Convert.ToHexString(Nv5000Frames.Frame(records).AsSpan(2));

    private static long Ms(int ms) => ms * 1000L;

    private async Task<(MsiNv5000Adapter Adapter, ControllerDescriptor Descriptor)> Make(MsiGpuOptions options, INativeMapSource? maps = null)
    {
        var a = new MsiNv5000Adapter(_nv, options, maps: maps, time: _time, sleep: _sleeps.Add, budgetClock: () => _now, budgetTicksPerSecond: 1_000_000);
        var d = (await a.DiscoverAsync(default)).Single();
        return (a, d);
    }

    /// <summary>A bring-up session with the family gate passed (one 0xEC read in the log).</summary>
    private async Task<(MsiNv5000Adapter Adapter, ControllerDescriptor Descriptor)> Gated(MsiGpuOptions options)
    {
        _nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var (a, d) = await Make(options);
        await a.OpenForBringupAsync(d.Id, default);
        (await a.RunProbeAsync(d.Id, "fwinfo", default)).Ok.Should().BeTrue();
        return (a, d);
    }

    private sealed class MapSource(NativeControllerMap map) : INativeMapSource
    {
        public NativeControllerMap? Get(ControllerId id) => map;
    }
}
