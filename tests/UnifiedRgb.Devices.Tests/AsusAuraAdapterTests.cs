using System.Text;
using UnifiedRgb.Devices.AsusAura;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// NATIVE-2 phase A, offline against <see cref="FakeHid"/> (design §10 item 4, work order NATIVE-2): collection selection,
/// the two channel namespaces, the 20-LED split, the firewall, complete-frame budgets, read validation, read-only bring-up
/// and the hold-black corrections (overnight addendum §9). No report here reaches a board.
/// </summary>
public class AsusAuraAdapterTests
{
    private const string Firmware = "AULA3-AR32-0207";

    [Fact]
    public async Task Direct_mode_refusal_is_classified_as_blocked_with_no_following_colour_report()
    {
        var (adapter, descriptor) = await Session(new AsusAuraOptions { ControlVerified = true }, onboard: 2, addressable: 0);
        _hid.FailWritesFrom = 1;
        var open = () => adapter.OpenAsync(descriptor.Id, default);
        await open.Should().ThrowAsync<DeviceBlockedException>().WithMessage("*direct mode select*");
        _hid.Writes.Should().BeEmpty();
        await adapter.DisposeAsync();
    }
    private readonly FakeHid _hid = new();
    private long _now;

    [Fact]
    public async Task Selection_takes_the_mi02_ff72_a1_collection_and_rejects_mi00_and_wrong_usage()
    {
        _hid.Collections.AddRange(
        [
            Col(0x00),
            Col(0x02, usage: 0x00A0),
            Col(0x02, length: 64),
            Col(0x02, error: "HidP_GetCaps NTSTATUS 0xC0110001"),
            Col(0x02, vid: 0x1234),
            Col(0x02),
        ]);
        var rule = AsusAuraDefaults.Rule;
        var selected = rule.Select(_hid.Collections, out var rejected);
        selected.Should().ContainSingle().Which.Should().Be(_hid.Collections[^1]);
        rejected.Should().HaveCount(5);
        rejected[0].Should().EndWith("MI_00 is never opened");
        rejected[1].Should().Contain("usage FF72/00A0 is not FF72/00A1");
        rejected[2].Should().Contain("report lengths in 64 / out 64 are not 65");
        rejected[3].Should().Contain("caps unavailable");
        rejected[4].Should().Contain("device 1234:19AF");

        var (a, d) = await Make(new AsusAuraOptions(), addCollection: false);
        d.Serial.Should().Be("7&1a2b3c&0&0000");
        _hid.Opens.Should().Be(0, "discovery reads caps only; no handle for reports");

        var mi00 = () => HidReportTransport.Open(_hid, _hid.Collections[0], rule, 250);
        mi00.Should().Throw<InvalidOperationException>().WithMessage("*MI_00 is never opened*");
        var win = () => new WinHidReportDevices(new NoDevices()).Open(_hid.Collections[0]);
        win.Should().Throw<InvalidOperationException>().WithMessage("MI_00 is never opened");
        _hid.Opens.Should().Be(0);
        await a.DisposeAsync();
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_0b05&pid_19af&mi_02#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 2)]
    [InlineData(@"\\?\HID#VID_0B05&PID_19AF&MI_00#7&1&0&0000#{x}", 0)]
    [InlineData(@"\\?\hid#vid_0b05&pid_19af#7&1&0&0000#{x}", null)]
    public void Interface_number_is_parsed_from_the_path(string path, int? expected) =>
        HidCollectionInfo.InterfaceOf(path).Should().Be(expected);

    [Fact]
    public async Task Unmapped_board_is_one_aggregate_point_zone_at_the_class_ceiling()
    {
        var (a, d) = await Make(new AsusAuraOptions());
        d.Class.Should().Be(DeviceClass.AsusAura);
        d.DeviceType.Should().Be(DeviceType.Motherboard);
        d.Id.Value.Should().Be("asusaura:19AF:0");
        var z = d.Zones.Should().ContainSingle().Subject;
        z.Name.Should().Be("Onboard (aggregate, unmapped)");
        z.Layout.Should().Be(ZoneLayout.Point);
        z.Mapped.Should().BeFalse();
        d.Capabilities.MaxFps.Should().Be(30);
        a.IsControlVerified(d.Id).Should().BeFalse();
        a.UnverifiedReason(d.Id).Should().Be("AsusAura control not verified");
        a.Probes.Should().Equal("hid-caps", "aura-id");
        _hid.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Zone_derivation_keeps_the_effect_and_direct_namespaces_apart()
    {
        var config = new AuraConfig(Firmware, Table(onboard: 21, rgbHeaders: 1, addressable: 3));
        var t = AuraTopology.Derive(config, [0, 30, 0, 10]);
        t.EffectChannels.Should().Be(4, "onboard + three addressable headers");
        t.Channels.Should().Equal(
            new AuraChannel(0, "Onboard", 0, 0x04, 21),
            new AuraChannel(2, "ARGB header 2", 2, 0x01, 30));
        t.Notes.Should().ContainSingle().Which.Should().Contain("header 4: 10 LEDs configured but the table reports 3 addressable header(s)");
        var fw = t.FirewallTable();
        fw.EffectChannels.Should().Be(4);
        fw.DirectLeds.Should().BeEquivalentTo(new Dictionary<byte, int> { [0x04] = 21, [0x01] = 30 });

        var headless = AuraTopology.Derive(new AuraConfig(Firmware, Table(onboard: 0, rgbHeaders: 0, addressable: 2)), [5]);
        headless.EffectChannels.Should().Be(2);
        headless.Channels.Should().Equal(new AuraChannel(1, "ARGB header 1", 0, 0x00, 5));
        headless.Notes.Should().Contain("config[0x1B] = 0: no onboard channel");
    }

    [Fact]
    public void Rgb_header_count_is_clamped_when_it_exceeds_the_onboard_leds()
    {
        var config = new AuraConfig(Firmware, Table(onboard: 2, rgbHeaders: 5, addressable: 1));
        config.RgbHeadersRaw.Should().Be(5);
        config.RgbHeaders.Should().Be(0, "config[0x1B] < config[0x1D] is inconsistent: counted as 0, like the reference");
        var t = AuraTopology.Derive(config, []);
        t.Channels.Should().Equal(new AuraChannel(0, "Onboard", 0, 0x04, 2));
        t.Notes.Should().ContainSingle().Which.Should().Contain("config[0x1D] = 5 exceeds config[0x1B] = 2");
        new AuraConfig(Firmware, Table(onboard: 7, rgbHeaders: 2, addressable: 0)).RgbHeaders.Should().Be(2);
    }

    [Theory]
    [InlineData(21, 1)]
    [InlineData(40, 20)]
    public void Direct_frames_split_into_20_led_reports_with_apply_on_the_last(int leds, int lastCount)
    {
        var colors = Enumerable.Range(0, leds).Select(i => new Rgb((byte)i, (byte)(i + 100), (byte)(200 - i))).ToArray();
        var reports = AuraReports.Direct(0x04, colors);
        reports.Should().HaveCount(2);
        reports.Should().AllSatisfy(r => r.Should().HaveCount(65));
        reports[0][..5].Should().Equal(0xEC, 0x40, 0x04, 0, 20);
        reports[1][..5].Should().Equal(0xEC, 0x40, 0x84, 20, (byte)lastCount);
        for (var i = 0; i < leds; i++)
        {
            var r = reports[i / 20];
            var k = i % 20;
            r[(5 + (3 * k))..(8 + (3 * k))].Should().Equal(colors[i].R, colors[i].G, colors[i].B);
        }

        reports[1][(5 + (3 * lastCount))..].Should().AllBeEquivalentTo((byte)0, "padding stays zero");
        AuraReports.ReportsFor(leds).Should().Be(2);
    }

    [Fact]
    public void Offsets_above_255_carry_the_0x10_flag_and_oversize_frames_are_refused()
    {
        var reports = AuraReports.Direct(0x01, new Rgb[300]);
        reports.Should().HaveCount(15);
        reports[12][2..5].Should().Equal(0x01, 240, 20);
        reports[13][2..5].Should().Equal(0x11, 4, 20); // offset 260
        reports[14][2..5].Should().Equal(0x91, 24, 20); // offset 280, last
        var big = () => AuraReports.Direct(0x04, new Rgb[AuraReports.MaxDirectLeds + 1]);
        big.Should().Throw<ArgumentOutOfRangeException>();
        var empty = () => AuraReports.Direct(0x04, []);
        empty.Should().Throw<ArgumentOutOfRangeException>();
        var bad = () => AuraReports.Direct(0x10, new Rgb[1]);
        bad.Should().Throw<ArgumentOutOfRangeException>("0x10 is a flag, not a channel");
    }

    [Fact]
    public void Firewall_rejects_setgen1_commit_wrong_namespace_and_overflow()
    {
        var table = AuraTopology.Derive(new AuraConfig(Firmware, Table(onboard: 21, rgbHeaders: 1, addressable: 3)), [0, 30]).FirewallTable();
        AuraReportFirewall.Check(AuraReports.Query(0x82), null).Should().BeNull();
        AuraReportFirewall.Check(AuraReports.DirectMode(0), null).Should().Contain("before the config table");
        AuraReportFirewall.Check(AuraReports.DirectMode(0), table).Should().BeNull();
        AuraReportFirewall.Check(AuraReports.DirectMode(3), table).Should().BeNull("effect channel 3 exists (third header) even when unused");
        AuraReports.Direct(0x04, new Rgb[21]).Should().AllSatisfy(r => AuraReportFirewall.Check(r, table).Should().BeNull());
        AuraReports.Direct(0x01, new Rgb[30]).Should().AllSatisfy(r => AuraReportFirewall.Check(r, table).Should().BeNull());

        AuraReportFirewall.Check(Report(0x52, 0x53, 0x00, 0x01), table).Should().Be("command 0x52 denied", "SetGen1 is never sent");
        AuraReportFirewall.Check(Report(0x53), table).Should().Be("command 0x53 denied");
        AuraReportFirewall.Check(Report(0x3F), table).Should().Be("command 0x3F denied", "commit persists; excluded");
        AuraReportFirewall.Check(AuraReports.DirectMode(4), table).Should().Contain("effect channel 4 is not below 4", "direct channel 0x04 is not an effect channel");
        AuraReportFirewall.Check(Report(0x35, 0, 0, 0, 0x00), table).Should().Contain("is not direct (0xFF)");
        AuraReportFirewall.Check(AuraReports.Direct(0x02, new Rgb[1])[0], table).Should().Contain("direct channel 0x02 was not discovered",
            "header 2's effect channel index (2) is not its direct channel (0x01)");
        AuraReportFirewall.Check(Report(0x40, 0x84, 10, 12), table).Should().Contain("offset 10 + count 12 exceeds channel 0x04 (21 LEDs)");
        AuraReportFirewall.Check(Report(0x40, 0x84, 0, 21), table).Should().Contain("count 21 is not 1..20");
    }

    [Fact]
    public async Task Unverified_open_is_blocked_before_any_report()
    {
        var (a, d) = await Make(new AsusAuraOptions());
        var open = () => a.OpenAsync(d.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Be("AsusAura control not verified");
        var write = () => a.WriteFrame(d.Id, [[Rgb.White]]);
        write.Should().Throw<InvalidOperationException>();
        _hid.Opens.Should().Be(0);
        _hid.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Verified_open_reads_the_identity_selects_direct_on_used_channels_and_writes_complete_frames()
    {
        Identity(onboard: 21, addressable: 3);
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true, Headers = [new(), new() { LedCount = 30 }] });
        d.Zones.Select(z => z.Name).Should().Equal("Onboard (aggregate, unmapped)", "ARGB header 2");
        d.Zones[1].Layout.Should().Be(ZoneLayout.Linear(30));
        await a.OpenAsync(d.Id, default);
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.Query(0x82), AuraReports.Query(0xB0), AuraReports.DirectMode(0), AuraReports.DirectMode(2)]));

        var strip = Enumerable.Range(0, 30).Select(i => new Rgb((byte)i, 0, 0)).ToArray();
        _hid.Writes.Clear();
        a.WriteFrame(d.Id, [[new Rgb(10, 20, 30)], strip]).Should().BeTrue();
        var onboard = new Rgb[21];
        Array.Fill(onboard, new Rgb(10, 20, 30));
        Hex(_hid.Writes).Should().Equal(Hex([.. AuraReports.Direct(0x04, onboard), .. AuraReports.Direct(0x01, strip)]));
        var stats = a.FrameBudget(d.Id)!;
        (stats.Frames, stats.OperationsLastFrame).Should().Be((1, 4), "one frame = every report of every used channel");

        _now += Ms(50);
        a.WriteFrame(d.Id, [[new Rgb(10, 20, 30)], strip]).Should().BeFalse("unchanged means no native write to count");
        _hid.Writes.Should().HaveCount(4, "an unchanged frame is not resent");
        a.WriteFrame(d.Id, [[Rgb.White], strip]).Should().BeTrue();
        _hid.Writes.Should().HaveCount(8);
        a.WriteFrame(d.Id, [[Rgb.Black], strip]).Should().BeFalse("0 ms after the last frame: the 30 fps budget refuses it");
        _hid.Writes.Should().HaveCount(8);
        a.FrameBudget(d.Id)!.Skips.Should().Be(1);

        await a.CloseAsync(d.Id);
        await a.CloseAsync(d.Id);
        await a.DisposeAsync();
        _hid.Writes.Should().HaveCount(8, "Close and Host stop send nothing (no release command in evidence)");
        _hid.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task The_pacing_delay_is_the_boards_own_budget_deadline_rounded_up()
    {
        Identity(onboard: 3, addressable: 0);
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true });
        a.Should().BeAssignableTo<IFramePacingSource>("the writer reads its pacing hint");
        var pacing = a;
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.Zero, "before the first frame");
        pacing.DelayUntilNextFrame(new ControllerId("asusaura:none")).Should().Be(TimeSpan.Zero);
        await a.OpenAsync(d.Id, default);
        a.WriteFrame(d.Id, [[Rgb.White]]).Should().BeTrue();
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.FromTicks(333_334), "33.33… ms at 30 fps, rounded up to the next 100 ns");
        _now += 33_333;
        pacing.DelayUntilNextFrame(d.Id).Should().BePositive("a third of a microsecond is still due later, never zero");
        a.WriteFrame(d.Id, [[Rgb.Black]]).Should().BeFalse();
        _now += 1;
        pacing.DelayUntilNextFrame(d.Id).Should().Be(TimeSpan.Zero);
        a.WriteFrame(d.Id, [[Rgb.Black]]).Should().BeTrue();
        a.FrameBudget(d.Id)!.Skips.Should().Be(1);
    }

    [Fact]
    public async Task Aggregate_zone_gives_every_onboard_led_the_same_colour()
    {
        Identity(onboard: 33, addressable: 0);
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true });
        await a.OpenAsync(d.Id, default);
        _hid.Writes.Clear();
        a.WriteFrame(d.Id, [[new Rgb(7, 8, 9)]]).Should().BeTrue();
        var leds = _hid.Writes.SelectMany(r => r.Skip(5).Take(3 * r[4]).Chunk(3)).ToList();
        leds.Should().HaveCount(33).And.AllSatisfy(c => c.Should().Equal(7, 8, 9));
        _hid.Writes.Select(r => r[2]).Should().Equal(0x04, 0x84);
    }

    [Fact]
    public async Task Bringup_open_and_probes_are_read_only()
    {
        Identity(onboard: 21, addressable: 3, rgbHeaders: 1);
        var (a, d) = await Make(new AsusAuraOptions());
        await a.OpenForBringupAsync(d.Id, default);
        _hid.Opens.Should().Be(0, "the bring-up open sends nothing and opens no report handle");

        var caps = await a.RunProbeAsync(d.Id, "hid-caps", default);
        caps.Ok.Should().BeTrue();
        caps.Fields["collections"].Should().Be("1");
        caps.Fields["selected"].Should().Contain("MI_02 UP:FF72 U:00A1 in 65 out 65");
        _hid.Opens.Should().Be(0, "caps come from enumeration");

        var id = await a.RunProbeAsync(d.Id, "aura-id", default);
        id.Ok.Should().BeTrue(id.Summary);
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.Query(0x82), AuraReports.Query(0xB0)]));
        id.Fields["firmware"].Should().Be(Firmware);
        id.Fields["config"].Should().Be(Convert.ToHexString(Table(onboard: 21, rgbHeaders: 1, addressable: 3)));
        (id.Fields["onboardLeds"], id.Fields["rgbHeaders"], id.Fields["addressableHeaders"], id.Fields["effectChannels"], id.Fields["directChannels"])
            .Should().Be(("21", "1", "3", "4", "04:21"));
        (id.Fields["firmwareReturnedSize"], id.Fields["configReturnedSize"], id.Fields["comm"]).Should().Be(("65", "65", "ok"));
        id.Rows.Should().HaveCount(2);
        id.Rows[1].ResponseHex.Should().StartWith("EC30");

        var release = await a.ReleaseAsync(d.Id, default);
        (release.Ok, release.Fields["release"], release.Fields["comm"]).Should().Be((true, "not-applicable", "not-attempted"));
        _hid.Writes.Should().HaveCount(2, "no 0x35, no 0x40, no release report");
        _hid.Writes.Should().NotContain(r => r[1] == 0x35 || r[1] == 0x40 || r[1] == 0x52 || r[1] == 0x3F);
        var probe = () => a.RunProbeAsync(d.Id, "aura-id", default);
        await probe.Should().ThrowAsync<InvalidOperationException>("probes need a session");
    }

    [Theory]
    [InlineData(64, 0xEC, 0x02, "returned size 64, expected 65")]
    [InlineData(65, 0xEE, 0x02, "report id 0xEE is not 0xEC")]
    [InlineData(65, 0xEC, 0x03, "reply id 0x03 is not 0x02")]
    public async Task Identity_reads_validate_returned_size_and_reply_ids(int returned, byte reportId, byte replyId, string why)
    {
        var reply = FirmwareReply();
        reply[0] = reportId;
        reply[1] = replyId;
        _hid.Replies.Enqueue((reply, returned));
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true });
        await a.OpenForBringupAsync(d.Id, default);
        var r = await a.RunProbeAsync(d.Id, "aura-id", default);
        r.Ok.Should().BeFalse();
        r.Fields["invalid"].Should().Be($"0x82 reply: {why}");
        r.Fields["firmwareReturnedSize"].Should().Be(returned.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.Query(0x82)]), "nothing follows an invalid reply");
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.White), default)).Summary.Should().Contain("run probe aura-id first");
        _hid.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task Config_reply_must_be_complete_and_a_silent_board_times_out()
    {
        _hid.Replies.Enqueue((FirmwareReply(), 65));
        _hid.Replies.Enqueue((ConfigReply(Table(onboard: 21, rgbHeaders: 0, addressable: 0)), 30));
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true, HidTimeoutMs = 20 });
        var open = () => a.OpenAsync(d.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("0xB0 reply: returned size 30, expected 65");
        _hid.Writes.Should().NotContain(r => r[1] == 0x35, "no mode is selected without a valid table");

        await a.OpenForBringupAsync(d.Id, default);
        _hid.Writes.Clear();
        var r = await a.RunProbeAsync(d.Id, "aura-id", default); // no reply queued: the bounded read gives up
        r.Ok.Should().BeFalse();
        r.Rows[0].Status.Should().Be(AsusAuraAdapter.StatusTimeout);
        r.Fields["comm"].Should().Be("error(1460)");
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.Query(0x82)]));
    }

    [Fact]
    public async Task Marker_selects_direct_on_its_channel_once_and_sends_the_whole_channel()
    {
        var (a, d) = await Session(new AsusAuraOptions { Headers = [new(), new() { LedCount = 25 }] }, onboard: 21, addressable: 3);
        var gray = new Rgb(128, 128, 128);
        var m = await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 21, gray), default);
        m.Ok.Should().BeTrue(m.Summary);
        var all = new Rgb[21];
        Array.Fill(all, gray);
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.DirectMode(0), .. AuraReports.Direct(0x04, all)]));
        (m.Fields["reports"], m.Fields["effectChannel"], m.Fields["directChannel"], m.Fields["readback"]).Should().Be(("2", "0", "0x04", "none"));
        m.Summary.Should().Contain("playback unobserved");

        _hid.Writes.Clear();
        (await a.EndMarkerAsync(d.Id, default)).Ok.Should().BeTrue();
        Hex(_hid.Writes).Should().Equal(Hex(AuraReports.Direct(0x04, new Rgb[21])));

        _hid.Writes.Clear();
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(3, 1, Rgb.White), default)).Ok.Should().BeTrue();
        _hid.Writes.Should().NotContain(r => r[1] == 0x35, "direct mode is already selected on effect channel 0");

        _hid.Writes.Clear();
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(24, 1, Rgb.White, Channel: 2), default)).Ok.Should().BeTrue();
        var strip = new Rgb[25];
        strip[24] = Rgb.White;
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.DirectMode(2), .. AuraReports.Direct(0x01, strip)]));

        _hid.Writes.Clear();
        (await a.ReleaseAsync(d.Id, default)).Fields["release"].Should().Be("not-applicable");
        await a.CloseAsync(d.Id);
        await a.DisposeAsync();
        _hid.Writes.Should().BeEmpty("release, close and stop send nothing; the last frame remains");
    }

    [Theory]
    [InlineData(0, int.MaxValue, 0)]
    [InlineData(int.MaxValue, 1, 0)]
    [InlineData(20, 2, 0)]
    [InlineData(21, 1, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 1, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 1, 4)]
    [InlineData(0, 26, 2)]
    public async Task Marker_bounds_are_overflow_safe_and_checked_before_any_report(int start, int count, int channel)
    {
        var (a, d) = await Session(new AsusAuraOptions { Headers = [new(), new() { LedCount = 25 }] }, onboard: 21, addressable: 3);
        var marker = () => a.BeginMarkerAsync(d.Id, new NativeMarker(start, count, Rgb.White, channel), default);
        await marker.Should().ThrowAsync<ArgumentException>();
        _hid.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Hold_black_accepts_only_black_markers_and_a_failed_black_send_stops_everything()
    {
        var (a, d) = await Session(new AsusAuraOptions { HoldBlackOnRelease = true }, onboard: 21, addressable: 0);
        var white = () => a.BeginMarkerAsync(d.Id, new NativeMarker(0, 21, Rgb.White), default);
        await white.Should().ThrowAsync<ArgumentException>().WithMessage("Hold-black is active*");
        _hid.Writes.Should().BeEmpty("refused before the mode select");

        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 21, Rgb.Black), default)).Ok.Should().BeTrue();
        _hid.Writes.Should().HaveCount(3);
        _hid.FailWritesFrom = _hid.Writes.Count + 2; // the second report of the black end-marker fails
        var end = await a.EndMarkerAsync(d.Id, default);
        end.Ok.Should().BeFalse();
        end.Rows.Should().HaveCount(2, "stops at the first failure");
        end.Fields["comm"].Should().StartWith("error(");
        _hid.FailWritesFrom = -1;
        var sent = _hid.Writes.Count;

        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.Black), default)).Summary.Should().Contain("a previous black send failed");
        (await a.EndMarkerAsync(d.Id, default)).Ok.Should().BeTrue("the failed end-marker already cleared the marker: nothing to clear");
        var release = await a.ReleaseAsync(d.Id, default);
        release.Fields["release"].Should().Be("not-applicable");
        _hid.Writes.Should().HaveCount(sent, "no retry, no further send");
    }

    [Fact]
    public async Task Failed_black_send_under_hold_black_blocks_the_writer()
    {
        var (a, d) = await Session(new AsusAuraOptions { ControlVerified = true, HoldBlackOnRelease = true }, onboard: 21, addressable: 0);
        _hid.FailWritesFrom = 1;
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 21, Rgb.Black), default)).Ok.Should().BeFalse("the mode select failed");
        _hid.FailWritesFrom = -1;
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 21, Rgb.Black), default)).Ok.Should().BeTrue("a failed mode select is not a black send");
        _hid.FailWritesFrom = _hid.Writes.Count + 1;
        (await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 21, Rgb.Black), default)).Ok.Should().BeFalse();
        _hid.FailWritesFrom = -1;
        var open = () => a.OpenAsync(d.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("a previous black send failed");
    }

    [Fact]
    public async Task Native_map_replaces_the_aggregate_zone_and_is_checked_against_the_table_at_open()
    {
        var map = new NativeControllerMap("E3m owner names", [new("Logo", [0, 1, 2], "Linear"), new("Pin", [5], "Point")]);
        Identity(onboard: 21, addressable: 0);
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true }, maps: new MapSource(map));
        d.Zones.Select(z => (z.Name, z.Mapped)).Should().Equal(("Logo", true), ("Pin", true));
        await a.OpenAsync(d.Id, default);
        _hid.Writes.Clear();
        a.WriteFrame(d.Id, [[new Rgb(1, 1, 1), new Rgb(2, 2, 2), new Rgb(3, 3, 3)], [new Rgb(9, 9, 9)]]).Should().BeTrue();
        var expected = new Rgb[21];
        (expected[0], expected[1], expected[2], expected[5]) = (new Rgb(1, 1, 1), new Rgb(2, 2, 2), new Rgb(3, 3, 3), new Rgb(9, 9, 9));
        Hex(_hid.Writes).Should().Equal(Hex(AuraReports.Direct(0x04, expected)), "unmapped LEDs stay black");

        var wide = new NativeControllerMap("x", [new("Far", [30], "Point")]);
        var other = new FakeHid();
        other.Collections.Add(Col(0x02));
        other.Replies.Enqueue((FirmwareReply(), 65));
        other.Replies.Enqueue((ConfigReply(Table(onboard: 21, rgbHeaders: 0, addressable: 0)), 65));
        var b = new AsusAuraAdapter(other, new AsusAuraOptions { ControlVerified = true }, maps: new MapSource(wide));
        var bd = (await b.DiscoverAsync(default)).Single();
        bd.Zones.Should().ContainSingle().Which.Name.Should().Be("Far", "index 30 fits the byte range at discovery");
        var open = () => b.OpenAsync(bd.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("native map indices exceed Onboard (21 LEDs");
        other.Writes.Should().NotContain(r => r[1] == 0x35);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    [InlineData(2)]
    public async Task A_map_naming_a_channel_outside_the_configured_headers_falls_back_without_aborting_discovery(int channel)
    {
        var map = new NativeControllerMap("malformed", [new("Bad", [0], "Point", Channel: channel)]);
        var (_, d) = await Make(new AsusAuraOptions { Headers = [new() { LedCount = 10 }] }, maps: new MapSource(map));
        d.Zones.Select(z => (z.Name, z.Mapped)).Should().Equal(("Onboard (aggregate, unmapped)", false), ("ARGB header 1", true));
        _hid.Writes.Should().BeEmpty("discovery reads caps only");
        NativeMaps.Validate(map, byte.MaxValue).Should().Contain(channel < 0 ? "is negative" : "outside");
    }

    [Fact]
    public async Task Configured_header_missing_from_the_table_blocks_the_writer_rather_than_substituting()
    {
        Identity(onboard: 21, addressable: 3);
        var (a, d) = await Make(new AsusAuraOptions { ControlVerified = true, Headers = [new(), new(), new(), new() { LedCount = 10 }] });
        d.Zones.Select(z => z.Name).Should().Equal("Onboard (aggregate, unmapped)", "ARGB header 4");
        var open = () => a.OpenAsync(d.Id, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("header 4 is not a derived channel");
        Hex(_hid.Writes).Should().Equal(Hex([AuraReports.Query(0x82), AuraReports.Query(0xB0)]));
    }

    [Fact]
    public void Options_refuse_header_counts_the_offset_encoding_cannot_address()
    {
        var neg = () => new AsusAuraAdapter(_hid, new AsusAuraOptions { Headers = [new() { LedCount = -1 }] });
        neg.Should().Throw<ArgumentOutOfRangeException>();
        var big = () => new AsusAuraAdapter(_hid, new AsusAuraOptions { Headers = [new() { LedCount = 513 }] });
        big.Should().Throw<ArgumentOutOfRangeException>();
        var timeout = () => new AsusAuraAdapter(_hid, new AsusAuraOptions { HidTimeoutMs = 0 });
        timeout.Should().Throw<ArgumentOutOfRangeException>();
        new AsusAuraAdapterFactory().Class.Should().Be(DeviceClass.AsusAura);
    }

    [Fact]
    public async Task Report_transport_bounds_reads_and_checks_output_length()
    {
        var c = Col(0x02);
        using var t = HidReportTransport.Open(_hid, c, AsusAuraDefaults.Rule, 20);
        var send = async () => await t.SendAsync(new byte[64], default);
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*64 bytes*65*");
        _hid.Writes.Should().BeEmpty();

        var buffer = new byte[65];
        buffer.AsSpan().Fill(0xAA);
        var read = async () => await t.ReadAsync(buffer, default);
        await read.Should().ThrowAsync<TimeoutException>();
        buffer.Should().AllBeEquivalentTo((byte)0, "the buffer is zeroed before every read");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = async () => await t.ReadAsync(buffer, cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>("the caller's cancellation is not a timeout");

        _hid.Replies.Enqueue(([0xEC, 0x02, 0x41], 3));
        (await t.ReadAsync(buffer, default)).Should().Be(3, "the size actually delivered is returned for the caller to validate");
        var small = async () => await t.ReadAsync(new byte[10], default);
        await small.Should().ThrowAsync<ArgumentException>();

        _hid.StallWrites = true;
        var stalled = async () => await t.SendAsync(AuraReports.Query(0x82), default);
        (await stalled.Should().ThrowAsync<TimeoutException>()).WithMessage("*not written within 20 ms*");
        var callerCancelled = async () => await t.SendAsync(AuraReports.Query(0x82), cts.Token);
        await callerCancelled.Should().ThrowAsync<OperationCanceledException>("the caller's cancellation is not a timeout");
        _hid.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stalled_write_ends_the_marker_step_with_a_timeout_row()
    {
        var (a, d) = await Session(new AsusAuraOptions { HidTimeoutMs = 30 }, onboard: 21, addressable: 0);
        _hid.StallWrites = true;
        var r = await a.BeginMarkerAsync(d.Id, new NativeMarker(0, 1, Rgb.Black), default);
        r.Ok.Should().BeFalse();
        r.Rows.Should().ContainSingle("nothing follows the stalled direct-mode select").Which.Status.Should().Be(AsusAuraAdapter.StatusTimeout, "a bounded write surfaces ERROR_TIMEOUT");
        r.Fields["comm"].Should().Be("error(1460)");
        _hid.Writes.Should().BeEmpty();
    }

    internal static byte[] Table(int onboard, int rgbHeaders, int addressable)
    {
        var t = new byte[60];
        t[0x02] = (byte)addressable;
        t[0x1B] = (byte)onboard;
        t[0x1D] = (byte)rgbHeaders;
        t[0x00] = 0x1E; // unrelated bytes survive in the hex
        return t;
    }

    internal static byte[] FirmwareReply()
    {
        var r = new byte[65];
        r[0] = 0xEC;
        r[1] = 0x02;
        Encoding.ASCII.GetBytes(Firmware).CopyTo(r, 2);
        return r;
    }

    internal static byte[] ConfigReply(byte[] table)
    {
        var r = new byte[65];
        r[0] = 0xEC;
        r[1] = 0x30;
        table.CopyTo(r, 4);
        return r;
    }

    internal static HidCollectionInfo Col(int mi, ushort usage = 0x00A1, int length = 65, string? error = null, ushort vid = 0x0B05) =>
        new(FormattableString.Invariant($@"\\?\hid#vid_{vid:x4}&pid_19af&mi_{mi:x2}#7&1a2b3c&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}"),
            vid, 0x19AF, mi, error is null ? (ushort)0xFF72 : (ushort)0, error is null ? usage : (ushort)0, error is null ? length : 0, error is null ? length : 0, 0, error);

    private static byte[] Report(params byte[] head)
    {
        var r = new byte[65];
        r[0] = 0xEC;
        head.CopyTo(r, 1);
        return r;
    }

    private static long Ms(int ms) => ms * 1000L;

    private static string[] Hex(IEnumerable<byte[]> reports) => [.. reports.Select(Convert.ToHexString)];

    private void Identity(int onboard, int addressable, int rgbHeaders = 0)
    {
        _hid.Replies.Enqueue((FirmwareReply(), 65));
        _hid.Replies.Enqueue((ConfigReply(Table(onboard, rgbHeaders, addressable)), 65));
    }

    private async Task<(AsusAuraAdapter Adapter, ControllerDescriptor Descriptor)> Make(AsusAuraOptions options, INativeMapSource? maps = null, bool addCollection = true)
    {
        if (addCollection)
        {
            _hid.Collections.Add(Col(0x02));
        }

        var a = new AsusAuraAdapter(_hid, options, maps: maps, budgetClock: () => _now, budgetTicksPerSecond: 1_000_000);
        var d = (await a.DiscoverAsync(default)).Single();
        return (a, d);
    }

    /// <summary>A bring-up session with the identity read (0x82 + 0xB0 cleared from the log).</summary>
    private async Task<(AsusAuraAdapter Adapter, ControllerDescriptor Descriptor)> Session(AsusAuraOptions options, int onboard, int addressable)
    {
        Identity(onboard, addressable);
        var (a, d) = await Make(options);
        await a.OpenForBringupAsync(d.Id, default);
        (await a.RunProbeAsync(d.Id, "aura-id", default)).Ok.Should().BeTrue();
        _hid.Writes.Clear();
        return (a, d);
    }

    private sealed class MapSource(NativeControllerMap map) : INativeMapSource
    {
        public NativeControllerMap? Get(ControllerId id) => map;
    }

    private sealed class NoDevices : UnifiedRgb.Transports.WinUsb.IUsbEnumerator
    {
        public IReadOnlyList<UnifiedRgb.Transports.WinUsb.UsbDeviceInfo> Enumerate(Guid interfaceClass) => [];
    }
}

/// <summary>
/// Offline HID collections: enumeration returns <see cref="Collections"/>; an opened channel records every output report in
/// <see cref="Writes"/>, answers reads from <see cref="Replies"/> (data, returned size) and otherwise waits until the bounded
/// read gives up. Writes numbered from <see cref="FailWritesFrom"/> (1-based, -1 = never) fail with an IOException.
/// </summary>
internal sealed class FakeHid : IHidReportDevices
{
    public List<HidCollectionInfo> Collections { get; } = [];

    public List<byte[]> Writes { get; } = [];

    public Queue<(byte[] Data, int Returned)> Replies { get; } = new();

    public int Opens { get; private set; }

    public int FailWritesFrom { get; set; } = -1;

    /// <summary>Writes wait until their token is cancelled (a stalled device).</summary>
    public bool StallWrites { get; set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => Collections.Where(c => c.Vid == vid && c.Pid == pid).ToList();

    public IHidReportChannel Open(HidCollectionInfo collection)
    {
        Opens++;
        return new Channel(this);
    }

    private sealed class Channel(FakeHid hid) : IHidReportChannel
    {
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            if (hid.StallWrites)
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }

            if (hid.FailWritesFrom > 0 && hid.Writes.Count + 1 >= hid.FailWritesFrom)
            {
                throw new IOException("device not responding", unchecked((int)0x8007001F));
            }

            hid.Writes.Add(report.ToArray());
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (hid.Replies.TryDequeue(out var r))
            {
                r.Data.AsSpan(0, Math.Min(r.Data.Length, buffer.Length)).CopyTo(buffer.Span);
                return r.Returned;
            }

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }

        public void Dispose() => hid.Disposed = true;
    }
}
