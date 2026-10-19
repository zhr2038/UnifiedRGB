using UnifiedRgb.Devices.EneDram;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// NATIVE-3 phase A, offline against <see cref="FakeSmbus"/> (design §5, work order NATIVE-3, overnight addendum §4/§9):
/// passive port targets, the reference detect sequence and bootstrap, the firewalled direct path, complete-frame budgets,
/// release/close/stop semantics and the hold-black corrections. No compound here reaches a bus.
/// </summary>
public sealed class EneDramAdapterTests : IDisposable
{
    private const string V2 = "AUMA0-E6K5-0104";
    private static readonly ControllerId P0 = EneDramAdapter.PortId(0);
    private static readonly ControllerId S70 = EneDramAdapter.StickId(0, 0x70);
    private readonly FakeEneProvider _provider = new();
    private long _now;

    private FakeSmbus Bus => _provider.Bus(0);

    [Fact]
    public async Task Passive_discovery_lists_both_port_targets_and_touches_nothing()
    {
        var a = Make(new EneDramOptions());
        var list = await a.DiscoverAsync(default);
        list.Select(d => d.Id.Value).Should().Equal("enedram:p0", "enedram:p1");
        list.Should().AllSatisfy(d =>
        {
            d.Zones.Should().BeEmpty();
            d.DeviceType.Should().Be(DeviceType.Dram);
            d.Class.Should().Be(DeviceClass.EneDram);
            d.Capabilities.MaxFps.Should().Be(10);
        });
        list[0].Name.Should().Be("ENE SMBus port 0 (bring-up target)");
        a.IsControlVerified(P0).Should().BeFalse();
        a.UnverifiedReason(P0).Should().Be("enedram:p0 is a bring-up target (SMBus port 0), never a writer");
        a.Probes.Should().Equal("pawnio-id", "ene-scan", "ene-remap");
        var open = () => a.OpenAsync(P0, default);
        await open.Should().ThrowAsync<DeviceBlockedException>();
        _provider.Opens.Should().Be(0, "nothing is loaded or opened before a probe asks for a port");
    }

    [Fact]
    public async Task Probes_need_a_session_and_opening_one_sends_nothing()
    {
        var a = Make(new EneDramOptions());
        var outside = () => a.RunProbeAsync(P0, EneDramAdapter.ProbeEneScan, default);
        await outside.Should().ThrowAsync<InvalidOperationException>().WithMessage("*need a bring-up session*");
        await a.OpenForBringupAsync(P0, default);
        _provider.Opens.Should().Be(0);
        var unknown = () => a.RunProbeAsync(P0, "spd-dump", default);
        await unknown.Should().ThrowAsync<ArgumentException>();
        var stranger = () => a.RunProbeAsync(new ControllerId("enedram:p7"), EneDramAdapter.ProbeEneScan, default);
        await stranger.Should().ThrowAsync<ArgumentException>();
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Pawnio_id_reports_the_handle_identity_without_smbus_traffic()
    {
        var a = Make(new EneDramOptions());
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);
        r.Ok.Should().BeTrue();
        r.Fields["pci"].Should().Be("1022:790B");
        r.Fields["amd790B"].Should().Be("true");
        r.Fields["moduleVerifiedGate"].Should().Be("false");
        r.Fields["moduleSha256"].Should().Be("pinned-module");
        r.Fields["readback"].Should().Be("none");
        (r.Fields["libApiExpected"], r.Fields["libApiActual"], r.Fields["libFileRelease"], r.Fields["driverVersion"]).Should().Be(("2.0.0", "2.0.0", "2.2.0", "not-observed"));
        r.Rows.Select(x => x.Step).Should().Equal("pawnio_version", "pawnio open + ioctl_identity + ioctl_piix4_port_sel");
        _provider.Opens.Should().Be(1);
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Pawnio_id_keeps_the_configured_sleep_mode_apart_from_an_unobserved_applied_one()
    {
        var a = Make(new EneDramOptions { SleepMode = 1 });
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);
        r.Ok.Should().BeTrue();
        r.Fields["sleepModeConfigured"].Should().Be("1");
        r.Fields["sleepModeApplied"].Should().Be(EneDramAdapter.SleepModeNotObserved, "a scripted port does not report the ioctl; nothing is inferred from configuration");
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_stick_paces_by_its_own_member_of_the_one_shared_port_budget()
    {
        Bus.Add(0x70, V2, leds: 2);
        Bus.Add(0x71, V2, leds: 2);
        var a = Make(Verified() with { CandidateAddresses = [0x70, 0x71] });
        a.Should().BeAssignableTo<IFramePacingSource>("the writer reads its pacing hint");
        var pacing = a;
        var s71 = EneDramAdapter.StickId(0, 0x71);
        pacing.DelayUntilNextFrame(S70).Should().Be(TimeSpan.Zero, "unknown before discovery");
        await a.DiscoverAsync(default);
        await a.OpenAsync(S70, default);
        await a.OpenAsync(s71, default);
        pacing.DelayUntilNextFrame(S70).Should().Be(TimeSpan.Zero, "before the first frame");

        a.WriteFrame(S70, [[Rgb.White]]).Should().BeTrue();
        pacing.DelayUntilNextFrame(S70).Should().Be(TimeSpan.FromMilliseconds(100), "one 10 fps interval of 0x70");
        pacing.DelayUntilNextFrame(s71).Should().Be(TimeSpan.Zero, "0x71 has its own member");
        _now += 40_000;
        a.WriteFrame(s71, [[Rgb.White]]).Should().BeTrue();
        pacing.DelayUntilNextFrame(S70).Should().Be(TimeSpan.FromMilliseconds(60));
        pacing.DelayUntilNextFrame(s71).Should().Be(TimeSpan.FromMilliseconds(100));
        pacing.DelayUntilNextFrame(P0).Should().Be(TimeSpan.Zero, "the port target takes no frames");
        pacing.DelayUntilNextFrame(EneDramAdapter.StickId(0, 0x72)).Should().Be(TimeSpan.Zero);

        var before = a.FrameBudget(P0)!;
        for (var i = 0; i < 5; i++)
        {
            pacing.DelayUntilNextFrame(S70);
        }

        a.FrameBudget(P0).Should().Be(before, "the query creates no member, skip or sample");
        _now += 59_999;
        pacing.DelayUntilNextFrame(S70).Should().Be(TimeSpan.FromTicks(10), "1 µs left is rounded up, never to zero");
        a.WriteFrame(S70, [[Rgb.Black]]).Should().BeFalse("a hint is not admission: the budget still refuses");
        _now += 1;
        pacing.DelayUntilNextFrame(S70).Should().Be(TimeSpan.Zero);
        a.WriteFrame(S70, [[Rgb.Black]]).Should().BeTrue("admitted exactly when the delay reaches zero");
        a.FrameBudget(P0)!.Skips.Should().Be(1);
    }

    [Fact]
    public async Task A_blocked_provider_is_a_refused_probe_with_nothing_sent()
    {
        _provider.Blocked = "PawnIO module not configured (EneDram:ModulePath)";
        var a = Make(new EneDramOptions());
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneScan, default);
        r.Ok.Should().BeFalse();
        r.Fields["comm"].Should().Be("blocked");
        r.Fields["blocked"].Should().Be(_provider.Blocked);
        r.Rows.Should().ContainSingle().Which.Status.Should().Be(EneDramAdapter.StatusRefused);
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_runs_the_reference_detect_sequence_and_never_writes()
    {
        Bus.Add(0x70, V2, leds: 8);
        Bus.Add(0x71, "AUMA0-E6K5-0107", leds: 10, receiveByteAck: false);
        Bus.Add(0x72, V2, leds: 8).SignatureFailAt = 5;
        Bus.Add(0x73, V2, leds: 8).Micron = true;
        var a = Make(new EneDramOptions { CandidateAddresses = [0x74, 0x73, 0x72, 0x71, 0x70] });
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneScan, default);

        r.Ok.Should().BeTrue();
        r.Fields["label"].Should().Be("E2 with pointer writes");
        r.Fields["candidates"].Should().Be("0x70,0x71,0x72,0x73,0x74", "configuration narrows, in the reference order");
        r.Fields["a0x70"].Should().Be($"ACK (receive byte); signature pass; name '{V2}'");
        r.Fields["a0x71"].Should().Be("ACK (read byte data 0x00); signature pass; name 'AUMA0-E6K5-0107'");
        r.Fields["a0x72"].Should().Be("ACK (receive byte); signature fail at 0xA5 (expected 0x05, got 0x55)");
        r.Fields["a0x73"].Should().Be("ACK (receive byte); signature pass; Micron device: skipped (reference)");
        r.Fields["a0x74"].Should().Be("NACK");
        r.Fields["leds0x70"].Should().Be("8");
        r.Fields["leds0x71"].Should().Be("10", "0107 keeps its LED count at config[0x03]");
        r.Fields["controllers"].Should().Be("0x70,0x71");
        r.Fields["moduleCount"].Should().StartWith("unknown");
        r.Fields["comm"].Should().Be("ok");
        r.Summary.Should().Contain("2 usable ENE controller(s) at 0x70,0x71").And.Contain("module count behind each address unknown");

        Bus.Sent.Should().OnlyContain(c => c.Kind == SmbusCompoundKind.Probe || c.Kind == SmbusCompoundKind.ReadSig || c.Kind == SmbusCompoundKind.RegRead);
        var at70 = Bus.Sent.Where(c => c.Address == 0x70).ToList();
        var expected = new List<SmbusCompound> { SmbusCompound.Probe(0x70) };
        expected.AddRange(Enumerable.Range(0, 16).Select(i => SmbusCompound.ReadSig(0x70, (byte)(0xA0 + i))));
        expected.AddRange(Enumerable.Range(0, 6).Select(i => SmbusCompound.RegRead(0x70, (ushort)(0x1030 + i))));
        expected.AddRange(Enumerable.Range(0, 16).Select(i => SmbusCompound.RegRead(0x70, (ushort)(0x1000 + i))));
        expected.AddRange(Enumerable.Range(0, 64).Select(i => SmbusCompound.RegRead(0x70, (ushort)(0x1C00 + i))));
        at70.Should().Equal(expected);
        Bus.Sent.Where(c => c.Address == 0x74).Should().Equal(SmbusCompound.Probe(0x74), SmbusCompound.ReadSig(0x74, 0x00));
        Bus.Sent.Where(c => c.Address == 0x72).Should().HaveCount(7, "the signature stops at the first mismatch");

        var list = await a.DiscoverAsync(default);
        list.Select(d => d.Id.Value).Should().Equal("enedram:p0", "enedram:p0:0x70", "enedram:p0:0x71", "enedram:p1");
        var d70 = list[1];
        d70.Name.Should().Be($"DRAM @0x70 ({V2}, 8 LEDs)");
        d70.Serial.Should().Be("p0:0x70");
        d70.Zones.Should().ContainSingle().Which.Name.Should().Be("DRAM @0x70 (aggregate, unmapped)");
        a.IsControlVerified(S70).Should().BeFalse();
        a.UnverifiedReason(S70).Should().Be("EneDram PawnIO module not verified");
        Make(new EneDramOptions { PawnIoModuleVerified = true }).UnverifiedReason(P0).Should().Contain("never a writer");
    }

    [Theory]
    [InlineData("AUMA0-E6K5-9999", 8, "ENE name 'AUMA0-E6K5-9999' not in table")]
    [InlineData(V2, 0, "config[0x02] reports no LEDs")]
    [InlineData("LED-0116", 6, "config LED count 6 exceeds the 5-LED direct range of LED-0116")]
    [InlineData(V2, 33, "config LED count 33 exceeds the 32-LED direct range of AUMA0-E6K5-0104")]
    public async Task Unusable_controllers_are_listed_blocked_with_no_zones_and_refuse_markers(string name, int leds, string why)
    {
        Bus.Add(0x70, name, leds);
        var a = await Scanned(new EneDramOptions());
        var d = (await a.DiscoverAsync(default)).Single(x => x.Id == S70);
        d.Name.Should().Be($"DRAM @0x70 (blocked: {why})");
        d.Zones.Should().BeEmpty();
        a.IsControlVerified(S70).Should().BeFalse();
        a.UnverifiedReason(S70).Should().Be(why);
        var m = await a.BeginMarkerAsync(P0, new NativeMarker(0, 1, Rgb.White, 0x70), default);
        m.Ok.Should().BeFalse();
        m.Summary.Should().Be(why);
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Port_marker_arguments_are_validated_before_any_compound()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        foreach (var bad in new[]
        {
            new NativeMarker(0, 1, Rgb.White),
            new NativeMarker(0, 1, Rgb.White, 0x74),
            new NativeMarker(0, 1, Rgb.White, 0x150),
            new NativeMarker(8, 1, Rgb.White, 0x70),
            new NativeMarker(7, 2, Rgb.White, 0x70),
            new NativeMarker(1, int.MaxValue, Rgb.White, 0x70),
            new NativeMarker(0, 0, Rgb.White, 0x70),
            new NativeMarker(-1, 1, Rgb.White, 0x70),
        })
        {
            var act = () => a.BeginMarkerAsync(P0, bad, default);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Marker_is_byte_exact_then_black_then_one_direct_off()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        var m = await a.BeginMarkerAsync(P0, new NativeMarker(2, 3, new Rgb(0x10, 0x20, 0x30), 0x70), default);
        m.Ok.Should().BeTrue();
        m.Summary.Should().Be("marker frame sent (transport accepted; playback unobserved)");
        m.Fields["controller"].Should().Be("0x70");
        m.Fields["directBase"].Should().Be("0x8100");
        m.Fields["byteFallback"].Should().Be("false");
        m.Fields["marker"].Should().Be("2+3 #102030");
        m.Fields["comm"].Should().Be("ok");
        m.Fields["readback"].Should().Be("none");
        var expected = new List<SmbusCompound> { SmbusCompound.RegWrite(0x70, 0x8020, 1), SmbusCompound.RegWrite(0x70, 0x80A0, 1) };
        expected.AddRange(Enumerable.Range(0, 8).Select(i => SmbusCompound.RegBlock(0x70, (ushort)(0x8100 + (3 * i)), i is >= 2 and <= 4 ? new byte[] { 0x10, 0x30, 0x20 } : new byte[3])));
        Bus.Sent.Should().Equal(expected, "direct on, then every LED in R,B,G");

        Bus.Sent.Clear();
        (await a.EndMarkerAsync(P0, default)).Summary.Should().Be("black frame sent");
        Bus.Sent.Should().Equal(Enumerable.Range(0, 8).Select(i => SmbusCompound.RegBlock(0x70, (ushort)(0x8100 + (3 * i)), new byte[3])));
        Bus.Sent.Clear();
        (await a.EndMarkerAsync(P0, default)).Summary.Should().Be("no marker to clear");
        Bus.Sent.Should().BeEmpty();

        var r = await a.ReleaseAsync(P0, default);
        r.Ok.Should().BeTrue();
        r.Fields["release"].Should().Be("done");
        r.Fields["controllers"].Should().Be("0x70");
        r.Summary.Should().Contain("unobserved");
        Bus.Sent.Should().Equal(SmbusCompound.RegWrite(0x70, 0x8020, 0), SmbusCompound.RegWrite(0x70, 0x80A0, 1));
        Bus.Sent.Clear();
        (await a.ReleaseAsync(P0, default)).Fields["release"].Should().Be("not-applicable");
        await a.DisposeAsync();
        Bus.Sent.Should().BeEmpty("release already sent direct off; stop sends nothing more");
    }

    [Fact]
    public async Task Release_without_a_marker_sends_nothing()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        var r = await a.ReleaseAsync(P0, default);
        r.Fields["release"].Should().Be("not-applicable");
        r.Fields["comm"].Should().Be("not-attempted");
        await a.DisposeAsync();
        Bus.Sent.Should().BeEmpty("a scan never switches direct mode, so there is nothing to undo");
        Bus.Disposed.Should().BeTrue();
        _provider.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Hold_black_accepts_black_markers_only_and_defers_every_direct_off()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions { HoldBlackOnRelease = true });
        var white = () => a.BeginMarkerAsync(P0, new NativeMarker(0, 8, Rgb.White, 0x70), default);
        await white.Should().ThrowAsync<ArgumentException>().WithMessage("*only black*");
        Bus.Sent.Should().BeEmpty("refused before any compound");

        (await a.BeginMarkerAsync(P0, new NativeMarker(0, 8, Rgb.Black, 0x70), default)).Ok.Should().BeTrue();
        Bus.Sent.Should().HaveCount(10);
        Bus.Sent.Clear();
        var r = await a.ReleaseAsync(P0, default);
        r.Ok.Should().BeTrue();
        r.Fields["release"].Should().Be("deferred-hold-black");
        r.Fields["directOn"].Should().Be("0x70");
        r.Fields["comm"].Should().Be("not-attempted");
        a.UnverifiedReason(S70).Should().EndWith("; hold-black active");
        await a.DisposeAsync();
        Bus.Sent.Should().BeEmpty("no direct off at release or Host stop under hold-black");
    }

    [Fact]
    public async Task A_failed_black_send_under_hold_black_stops_all_further_sends()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions { HoldBlackOnRelease = true });
        Bus.Fail = c => c.Kind == SmbusCompoundKind.RegBlock && c.Register == 0x8109 ? Nack("pointer") : null;
        var m = await a.BeginMarkerAsync(P0, new NativeMarker(0, 8, Rgb.Black, 0x70), default);
        m.Ok.Should().BeFalse();
        m.Fields["comm"].Should().StartWith("error(");
        Bus.Sent.Should().HaveCount(2 + 4, "direct on, three blocks, then the failed one; no retry");
        Bus.Fail = null;
        Bus.Sent.Clear();
        (await a.BeginMarkerAsync(P0, new NativeMarker(0, 8, Rgb.Black, 0x70), default)).Summary.Should().Contain("a previous black send failed");
        (await a.EndMarkerAsync(P0, default)).Summary.Should().Contain("a previous black send failed");
        (await a.ReleaseAsync(P0, default)).Fields["release"].Should().Be("deferred-hold-black");
        await a.DisposeAsync();
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_hold_black_a_failed_marker_is_not_latched()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        Bus.Fail = c => c.Kind == SmbusCompoundKind.RegBlock && c.Register == 0x8100 ? Nack("pointer") : null;
        (await a.BeginMarkerAsync(P0, new NativeMarker(0, 8, Rgb.Black, 0x70), default)).Ok.Should().BeFalse();
        Bus.Fail = null;
        (await a.BeginMarkerAsync(P0, new NativeMarker(0, 8, Rgb.Black, 0x70), default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_block_transaction_falls_back_to_bytes_for_good()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        Bus.Fail = c => c.Kind == SmbusCompoundKind.RegBlock ? Nack("block") : null;
        var m = await a.BeginMarkerAsync(P0, new NativeMarker(0, 1, new Rgb(1, 2, 3), 0x70), default);
        m.Ok.Should().BeTrue();
        m.Fields["byteFallback"].Should().Be("true");
        var expected = new List<SmbusCompound>
        {
            SmbusCompound.RegWrite(0x70, 0x8020, 1), SmbusCompound.RegWrite(0x70, 0x80A0, 1),
            SmbusCompound.RegBlock(0x70, 0x8100, [1, 3, 2]),
            SmbusCompound.RegWrite(0x70, 0x8100, 1), SmbusCompound.RegWrite(0x70, 0x8101, 3), SmbusCompound.RegWrite(0x70, 0x8102, 2),
        };
        expected.AddRange(Enumerable.Range(3, 21).Select(r => SmbusCompound.RegWrite(0x70, (ushort)(0x8100 + r), 0)));
        Bus.Sent.Should().Equal(expected);
        Bus.Sent.Clear();
        (await a.EndMarkerAsync(P0, default)).Ok.Should().BeTrue();
        Bus.Sent.Should().HaveCount(24).And.OnlyContain(c => c.Kind == SmbusCompoundKind.RegWrite, "latched: no block is tried again");
    }

    [Fact]
    public async Task A_dropped_compound_makes_the_address_inconclusive()
    {
        Bus.Add(0x70, V2, leds: 8);
        Bus.Add(0x71, V2, leds: 8);
        var a = Make(new EneDramOptions { CandidateAddresses = [0x70, 0x71] });
        await a.OpenForBringupAsync(P0, default);
        Bus.Fail = c => c == SmbusCompound.Probe(0x71) ? new SmbusResult(false, -1, 0, "SMBus mutex not acquired within 200 ms; compound dropped", Dropped: true) : null;
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneScan, default);
        r.Ok.Should().BeFalse();
        r.Fields["a0x71"].Should().StartWith("inconclusive: compound dropped");
        r.Fields["dropped"].Should().Be("1");
        r.Fields["mutexTimeouts"].Should().Be("1");
        r.Fields["comm"].Should().Be("error(258)");
        r.Fields["controllers"].Should().Be("0x70");
        r.Rows.Should().Contain(x => x.Status == EneDramAdapter.StatusDropped);
        Bus.Sent.Should().NotContain(c => c.Address == 0x71, "a dropped compound is never sent and the address is not continued");
    }

    [Fact]
    public async Task Remap_is_refused_without_the_gate_on_a_controller_target_and_a_second_time()
    {
        Bus.AddUnmapped(slot: 0, V2, leds: 8);
        var off = Make(new EneDramOptions());
        await off.OpenForBringupAsync(P0, default);
        var r = await off.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default);
        r.Ok.Should().BeFalse();
        r.Summary.Should().Contain("RemapOnOpen is off");
        _provider.Opens.Should().Be(0, "refused before the bus is opened");

        var on = Make(new EneDramOptions { RemapOnOpen = true });
        await on.OpenForBringupAsync(P0, default);
        (await on.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default)).Ok.Should().BeTrue();
        Bus.Sent.Clear();
        (await on.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default)).Summary.Should().Contain("already ran");
        var stick = EneDramAdapter.StickId(0, 0x70);
        await on.OpenForBringupAsync(stick, default);
        (await on.RunProbeAsync(stick, EneDramAdapter.ProbeEneRemap, default)).Summary.Should().Contain("runs on the port target");
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Remap_moves_modules_sharing_0x77_to_distinct_addresses_then_rescans()
    {
        Bus.AddUnmapped(slot: 0, V2, leds: 8);
        Bus.AddUnmapped(slot: 1, V2, leds: 8);
        Bus.AddUnmapped(slot: 2, "AUMA0-E6K5-0107", leds: 10);
        var a = Make(new EneDramOptions { RemapOnOpen = true });
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default);

        r.Ok.Should().BeTrue();
        r.Fields["remap"].Should().Be("slot 0->0x70,slot 1->0x71,slot 2->0x72");
        r.Fields["persistence"].Should().StartWith("unknown (RQ-E7)");
        r.Fields["controllers"].Should().Be("0x70,0x71,0x72");
        r.Fields.Should().NotContainKey("abort");
        r.Summary.Should().Contain("3 slot(s) assigned").And.EndWith("persistence across a reboot unknown");
        Bus.Sent.Where(c => c.Kind == SmbusCompoundKind.RegWrite).Should().Equal(
            SmbusCompound.RegWrite(0x77, 0x80F8, 0), SmbusCompound.RegWrite(0x77, 0x80F9, 0xE0),
            SmbusCompound.RegWrite(0x77, 0x80F8, 1), SmbusCompound.RegWrite(0x77, 0x80F9, 0xE2),
            SmbusCompound.RegWrite(0x77, 0x80F8, 2), SmbusCompound.RegWrite(0x77, 0x80F9, 0xE4));
        Bus.Sent.Should().NotContain(c => c.Kind == SmbusCompoundKind.RegBlock);
        Bus.Sent.Should().NotContain(c => c.Address >= 0x50 && c.Address <= 0x57, "SPD is never addressed");
        (await a.DiscoverAsync(default)).Select(d => d.Id.Value).Should().Contain(new[] { "enedram:p0:0x70", "enedram:p0:0x71", "enedram:p0:0x72" });
    }

    [Fact]
    public async Task Remap_stops_at_a_dropped_probe_without_writing()
    {
        Bus.AddUnmapped(slot: 0, V2, leds: 8);
        var a = Make(new EneDramOptions { RemapOnOpen = true, CandidateAddresses = [0x70, 0x77] });
        await a.OpenForBringupAsync(P0, default);
        Bus.Fail = c => c == SmbusCompound.Probe(0x70) ? new SmbusResult(false, -1, 0, "dropped", Dropped: true) : null;
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default);
        r.Ok.Should().BeFalse();
        r.Fields["remap"].Should().Be("none");
        r.Fields["abort"].Should().StartWith("inconclusive: compound dropped");
        Bus.Sent.Should().NotContain(c => c.Kind == SmbusCompoundKind.RegWrite);
    }

    [Fact]
    public async Task Unverified_controllers_never_get_a_writer()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions { PawnIoModuleVerified = true });
        a.UnverifiedReason(S70).Should().Be("EneDram control not verified");
        var open = () => a.OpenAsync(S70, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Be("EneDram control not verified");
        _provider.Bus(1).Add(0x70, V2, leds: 8);
        var gateOnly = await Scanned(new EneDramOptions { ControlVerified = true }, port: _provider.Bus(1));
        gateOnly.UnverifiedReason(EneDramAdapter.StickId(1, 0x70)).Should().Be("EneDram PawnIO module not verified");
        Bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Verified_discovery_scans_once_and_the_writer_sends_complete_frames_within_the_budget()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = Make(Verified());
        var list = await a.DiscoverAsync(default);
        list.Select(d => d.Id.Value).Should().Equal("enedram:p0", "enedram:p0:0x70", "enedram:p1");
        _provider.Bus(1).Sent.Should().HaveCount(32, "port 1: probe + read 0x00 per candidate, nothing answers");
        Bus.Sent.Should().NotContain(c => c.Kind == SmbusCompoundKind.RegWrite || c.Kind == SmbusCompoundKind.RegBlock);
        Bus.Sent.Clear();
        _provider.Bus(1).Sent.Clear();
        await a.DiscoverAsync(default);
        Bus.Sent.Should().BeEmpty("one scan per port per process");
        _provider.Bus(1).Sent.Should().BeEmpty();
        a.IsControlVerified(S70).Should().BeTrue();

        await a.OpenAsync(S70, default);
        Bus.Sent.Should().Equal(SmbusCompound.RegWrite(0x70, 0x8020, 1), SmbusCompound.RegWrite(0x70, 0x80A0, 1));
        Bus.Sent.Clear();
        a.WriteFrame(S70, [[new Rgb(9, 8, 7)]]).Should().BeTrue();
        Bus.Sent.Should().Equal(Enumerable.Range(0, 8).Select(i => SmbusCompound.RegBlock(0x70, (ushort)(0x8100 + (3 * i)), new byte[] { 9, 7, 8 })), "the aggregate colour on every LED");
        Bus.Sent.Clear();
        a.WriteFrame(S70, [[new Rgb(9, 8, 7)]]).Should().BeFalse("unchanged means no native write to count");
        Bus.Sent.Should().BeEmpty("an unchanged frame sends nothing");
        a.WriteFrame(S70, [[new Rgb(1, 1, 1)]]).Should().BeFalse("inside the 100 ms complete-frame interval at 10 fps");
        Bus.Sent.Should().BeEmpty();
        _now += 99_999;
        a.WriteFrame(S70, [[new Rgb(1, 1, 1)]]).Should().BeFalse();
        _now += 1;
        a.WriteFrame(S70, [[new Rgb(1, 1, 1)]]).Should().BeTrue();
        Bus.Sent.Should().HaveCount(8);
        var stats = a.FrameBudget(S70)!;
        stats.Frames.Should().Be(2);
        stats.Skips.Should().Be(2);
        stats.CeilingFps.Should().Be(10);
        stats.OperationsLastFrame.Should().Be(8);
        a.FrameBudget(P0).Should().Be(stats, "the sticks of a port share its budget");

        Bus.Sent.Clear();
        await a.CloseAsync(S70);
        Bus.Sent.Should().Equal(SmbusCompound.RegWrite(0x70, 0x8020, 0), SmbusCompound.RegWrite(0x70, 0x80A0, 1));
        Bus.Sent.Clear();
        await a.CloseAsync(S70);
        await a.DisposeAsync();
        Bus.Sent.Should().BeEmpty("close is idempotent and stop has nothing left to undo");
        (await a.DiscoverAsync(default)).Should().BeEmpty();
        await a.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_frame_throws_and_the_next_one_is_resent_whole()
    {
        Bus.Add(0x70, V2, leds: 2);
        var a = Make(Verified() with { CandidateAddresses = [0x70] });
        await a.DiscoverAsync(default);
        await a.OpenAsync(S70, default);
        Bus.Sent.Clear();
        Bus.Fail = c => c.Kind == SmbusCompoundKind.RegBlock && c.Register == 0x8103 ? Nack("pointer") : null;
        var write = () => a.WriteFrame(S70, [[Rgb.White]]);
        write.Should().Throw<IOException>().WithMessage("*frame failed*");
        Bus.Fail = null;
        Bus.Sent.Clear();
        _now += 100_000;
        a.WriteFrame(S70, [[Rgb.White]]).Should().BeTrue();
        Bus.Sent.Should().HaveCount(2, "the failed frame was not recorded as sent");
        a.FrameBudget(S70)!.Failed.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_direct_on_is_retried_by_the_next_open_and_undone_at_stop()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = Make(Verified() with { CandidateAddresses = [0x70] });
        await a.DiscoverAsync(default);
        Bus.Fail = c => c == SmbusCompound.RegWrite(0x70, 0x80A0, 1) ? Nack("write") : null;
        var open = () => a.OpenAsync(S70, default);
        await open.Should().ThrowAsync<IOException>().WithMessage("*direct on failed at '0x70 apply: 0x80A0 <- 01'*");
        Bus.Fail = null;
        Bus.Sent.Clear();
        await a.CloseAsync(S70);
        Bus.Sent.Should().BeEmpty("the writer never opened");
        await a.OpenAsync(S70, default);
        Bus.Sent.Should().Equal([SmbusCompound.RegWrite(0x70, 0x8020, 1), SmbusCompound.RegWrite(0x70, 0x80A0, 1)], "an enable that did not fully succeed is sent again");
        Bus.Sent.Clear();
        await a.DisposeAsync();
        Bus.Sent.Should().Equal(SmbusCompound.RegWrite(0x70, 0x8020, 0), SmbusCompound.RegWrite(0x70, 0x80A0, 1));
    }

    [Fact]
    public async Task Port_sessions_and_writers_never_drive_the_same_controller()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = Make(Verified() with { CandidateAddresses = [0x70] });
        await a.DiscoverAsync(default);
        await a.OpenAsync(S70, default);
        await a.OpenForBringupAsync(P0, default);
        Bus.Sent.Clear();
        var m = await a.BeginMarkerAsync(P0, new NativeMarker(0, 1, Rgb.White, 0x70), default);
        m.Ok.Should().BeFalse();
        m.Summary.Should().Contain("open by a writer");
        (await a.ReleaseAsync(P0, default)).Fields["release"].Should().Be("not-applicable", "the writer's direct mode is not the session's");
        Bus.Sent.Should().BeEmpty();

        await a.CloseAsync(S70);
        await a.OpenForBringupAsync(P0, default);
        Bus.Sent.Clear();
        (await a.BeginMarkerAsync(P0, new NativeMarker(0, 1, Rgb.White, 0x70), default)).Ok.Should().BeTrue();
        var open = () => a.OpenAsync(S70, default);
        (await open.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("bring-up session");
        (await a.ReleaseAsync(P0, default)).Fields["release"].Should().Be("done");
        Bus.Sent.Clear();
        await a.OpenAsync(S70, default);
        Bus.Sent.Should().Equal(SmbusCompound.RegWrite(0x70, 0x8020, 1), SmbusCompound.RegWrite(0x70, 0x80A0, 1));
    }

    [Fact]
    public async Task A_port_marker_owns_its_address_so_the_stick_id_is_refused_until_the_port_session_releases()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        (await a.BeginMarkerAsync(P0, new NativeMarker(0, 1, Rgb.Black, 0x70), default)).Ok.Should().BeTrue();
        Bus.Sent.Clear();

        var alias = () => a.OpenForBringupAsync(S70, default);
        (await alias.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("marker drives 0x70");
        (await a.ReleaseAsync(S70, default)).Fields["release"].Should().Be("not-applicable", "the refused open owns nothing of the stick");
        Bus.Sent.Should().BeEmpty("refused before any packet; the cleanup of the refused open sends nothing either");
        var probe = () => a.RunProbeAsync(S70, EneDramAdapter.ProbeEneScan, default);
        await probe.Should().ThrowAsync<InvalidOperationException>("the stick id never entered a session");

        (await a.EndMarkerAsync(P0, default)).Summary.Should().Be("black frame sent", "the port session still drives the stick");
        (await a.ReleaseAsync(P0, default)).Fields["release"].Should().Be("done");
        Bus.Sent.Clear();

        await a.OpenForBringupAsync(S70, default);
        Bus.Sent.Should().BeEmpty("reacquired after the port session released; opening sends nothing");
        (await a.BeginMarkerAsync(S70, new NativeMarker(0, 1, Rgb.Black), default)).Ok.Should().BeTrue();
        await a.OpenForBringupAsync(P0, default);
        Bus.Sent.Clear();
        var back = await a.BeginMarkerAsync(P0, new NativeMarker(0, 1, Rgb.Black, 0x70), default);
        back.Ok.Should().BeFalse();
        back.Summary.Should().Contain("its own bring-up session");
        (await a.ReleaseAsync(P0, default)).Fields["release"].Should().Be("not-applicable");
        Bus.Sent.Should().BeEmpty("the reverse direction is refused too");
        (await a.ReleaseAsync(S70, default)).Fields["release"].Should().Be("done");
    }

    [Fact]
    public async Task Remap_is_refused_while_another_session_holds_a_stick_even_read_only()
    {
        Bus.Add(0x70, V2, leds: 8);
        Bus.AddUnmapped(slot: 0, V2, leds: 8);
        var a = await Scanned(new EneDramOptions { RemapOnOpen = true });
        await a.OpenForBringupAsync(S70, default);
        Bus.Sent.Clear();

        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default);
        r.Ok.Should().BeFalse();
        r.Summary.Should().Contain("bring-up session");
        r.Fields["comm"].Should().Be("not-attempted");
        Bus.Sent.Should().BeEmpty("a read-only stick lease (no marker, no direct mode) still blocks the address bootstrap");

        (await a.ReleaseAsync(S70, default)).Fields["release"].Should().Be("not-applicable");
        Bus.Sent.Should().BeEmpty();
        var after = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneRemap, default);
        after.Fields.Should().ContainKey("remap", "admitted once the stick lease has ended (fake bus only)");
    }

    [Fact]
    public async Task A_controller_session_marks_only_its_own_stick_and_releases_it()
    {
        Bus.Add(0x70, V2, leds: 8);
        var a = await Scanned(new EneDramOptions());
        await a.ReleaseAsync(P0, default);
        await a.OpenForBringupAsync(S70, default);
        Bus.Sent.Clear();
        var wrong = () => a.BeginMarkerAsync(S70, new NativeMarker(0, 1, Rgb.White, 0x71), default);
        await wrong.Should().ThrowAsync<ArgumentException>();
        (await a.BeginMarkerAsync(S70, new NativeMarker(7, 1, Rgb.White), default)).Ok.Should().BeTrue();
        (await a.EndMarkerAsync(S70, default)).Ok.Should().BeTrue();
        (await a.ReleaseAsync(S70, default)).Fields["release"].Should().Be("done");
        Bus.Sent.Should().HaveCount(2 + 8 + 8 + 2);
        var probe = () => a.RunProbeAsync(S70, EneDramAdapter.ProbeEneScan, default);
        await probe.Should().ThrowAsync<InvalidOperationException>("the session ended");
    }

    [Fact]
    public async Task Native_map_places_zones_and_an_invalid_map_keeps_the_aggregate()
    {
        Bus.Add(0x70, V2, leds: 8);
        var map = new NativeControllerMap("E3m", [new("Bar", [0, 1, 2, 3], "Linear"), new("Dot", [7], "Point")]);
        var a = Make(Verified() with { CandidateAddresses = [0x70] }, new MapSource(map));
        var d = (await a.DiscoverAsync(default)).Single(x => x.Id == S70);
        d.Zones.Select(z => (z.Name, z.Mapped)).Should().Equal(("Bar", true), ("Dot", true));
        await a.OpenAsync(S70, default);
        Bus.Sent.Clear();
        a.WriteFrame(S70, [[new Rgb(1, 0, 0), new Rgb(2, 0, 0), new Rgb(3, 0, 0), new Rgb(4, 0, 0)], [new Rgb(0, 0, 9)]]).Should().BeTrue();
        Bus.Sent.Select(c => Convert.ToHexString(c.Data)).Should().Equal("010000", "020000", "030000", "040000", "000000", "000000", "000000", "000900");

        var other = new FakeEneProvider();
        other.Bus(0).Add(0x70, V2, leds: 8);
        var wide = new NativeControllerMap("x", [new("Far", [8], "Point")]);
        var b = new EneDramAdapter(other, Verified() with { CandidateAddresses = [0x70] }, maps: new MapSource(wide));
        (await b.DiscoverAsync(default)).Single(x => x.Id == S70).Zones.Should().ContainSingle()
            .Which.Name.Should().Be("DRAM @0x70 (aggregate, unmapped)", "index 8 is outside the 8 LEDs");
    }

    [Fact]
    public void Construction_validates_options_and_never_widens_candidates()
    {
        var wait = () => new EneDramAdapter(_provider, new EneDramOptions { MutexWaitMs = 0 });
        wait.Should().Throw<ArgumentOutOfRangeException>();
        var budget = () => new EneDramAdapter(_provider, new EneDramOptions { FrameBudgetMs = -1 });
        budget.Should().Throw<ArgumentOutOfRangeException>();
        var spd = () => new EneDramAdapter(_provider, new EneDramOptions { CandidateAddresses = [0x50] });
        spd.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*SPD 0x50..0x57 never*");
        var foreign = () => new EneDramAdapter(_provider, new EneDramOptions { CandidateAddresses = [0x40] });
        foreign.Should().Throw<ArgumentOutOfRangeException>();
        EneDramDefaults.Candidates([0x3A, 0x77, 0x70]).Should().Equal((byte)0x70, (byte)0x77, (byte)0x3A);
        EneDramDefaults.Candidates([]).Should().BeSameAs(EneRegisters.PhysicalCandidates);
        new EneDramAdapter(_provider, new EneDramOptions { FrameBudgetMs = 250 }).FrameBudget(P0)!.CeilingFps.Should().Be(4);
    }

    [Fact]
    public void Version_table_and_name_decoding()
    {
        EneVersions.Table.Should().HaveCount(10);
        EneVersions.Find("AUMA0-E6K5-0104")!.DirectBase.Should().Be(EneRegisters.DirectV2);
        EneVersions.Find("LED-0116")!.MaxLeds.Should().Be(5);
        EneVersions.Find("AUMA0-E6K5-1110")!.LedCountOffset.Should().Be(0x03);
        EneVersions.Find("auma0-e6k5-0104").Should().BeNull("an exact match only");
        EneVersions.NameOf("LED-0116\0garbage"u8).Should().Be("LED-0116");
        EneVersions.NameOf([0x41, 0x01, 0x42]).Should().Be("A?B");
        EneVersions.IsMicron("Micron\0\0"u8).Should().BeTrue();
        EneVersions.IsMicron("Micro"u8).Should().BeFalse();
    }

    [Fact]
    public async Task Factory_with_no_module_path_blocks_before_loading_anything()
    {
        var options = new EneDramOptions();
        var a = (EneDramAdapter)new EneDramAdapterFactory().Create(new Services(options));
        a.Class.Should().Be(DeviceClass.EneDram);
        (await a.DiscoverAsync(default)).Should().HaveCount(2);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);
        r.Ok.Should().BeFalse();
        r.Fields["blocked"].Should().Be("PawnIO module not configured (EneDram:ModulePath)");
        r.Fields["modulePath"].Should().BeEmpty();
        r.Fields["libSha256"].Should().Be(EneDramDefaults.PawnIoLibSha256);
        (r.Fields["libApiExpected"], r.Fields["libFileRelease"]).Should().Be(("2.0.0", "2.2.0"));
        r.Fields.Should().NotContainKey("libVersion");
        r.Fields.Should().NotContainKey("libApiActual", "the loader never reached pawnio_version");
        r.Rows.Should().ContainSingle().Which.Step.Should().Be("pawnio open");
        await a.DisposeAsync();

        PawnIoSmbusProvider.ResolveModulePath(" ").Should().BeEmpty();
        PawnIoSmbusProvider.ResolveModulePath(@"C:\x\SmbusPIIX4.bin").Should().Be(@"C:\x\SmbusPIIX4.bin");
        PawnIoSmbusProvider.ResolveModulePath(@"tools\pawnio\SmbusPIIX4.bin").Should().Be(Path.Combine(AppContext.BaseDirectory, @"tools\pawnio\SmbusPIIX4.bin"));
    }

    public void Dispose() => _provider.Dispose();

    private static EneDramOptions Verified() => new() { PawnIoModuleVerified = true, ControlVerified = true };

    [Fact]
    public async Task Cached_native_frames_do_not_inflate_writer_counts_or_budget_samples()
    {
        Bus.Add(0x70, V2, leds: 8);
        await using var adapter = Make(Verified());
        var descriptor = (await adapter.DiscoverAsync(default)).Single(d => d.Id == S70);
        await adapter.OpenAsync(S70, default);
        Bus.Sent.Clear();
        using var frames = new UnifiedRgb.Core.Engine.FrameBus();
        var box = frames.Register(descriptor);
        using var writer = new UnifiedRgb.Core.Engine.DeviceWriter(box, adapter);

        void Publish(Rgb color)
        {
            box.Back.Zones[0][0] = color;
            frames.Publish(S70, box.Back);
        }

        Publish(new Rgb(9, 8, 7));
        writer.PacedDrainOnce().Should().BeTrue();
        writer.GetStats().Written.Should().Be(1);
        Bus.Sent.Should().HaveCount(8);
        Bus.Sent.Clear();
        var samples = adapter.FrameBudget(S70)!.Samples;
        for (var i = 0; i < 20; i++)
        {
            _now += 100_000; // every cache hit is already past its native deadline
            Publish(new Rgb(9, 8, 7));
            writer.PacedDrainOnce().Should().BeTrue("the mailbox frame was consumed");
        }

        Bus.Sent.Should().BeEmpty();
        writer.GetStats().Written.Should().Be(1, "only an actual native send counts");
        writer.GetStats().Errors.Should().Be(0);
        adapter.FrameBudget(S70)!.Samples.Should().Be(samples);
        adapter.FrameBudget(S70)!.Skips.Should().Be(0);

        Publish(Rgb.White);
        writer.PacedDrainOnce().Should().BeTrue();
        writer.GetStats().Written.Should().Be(2);
        adapter.FrameBudget(S70)!.Samples.Should().Be(samples + 1);
        Bus.Sent.Should().HaveCount(8);
    }

    private static SmbusResult Nack(string step) => new(false, unchecked((int)0x8007045D), 0, $"{step} failed: HRESULT 0x8007045D", Step: step);

    private EneDramAdapter Make(EneDramOptions options, INativeMapSource? maps = null) =>
        new(_provider, options, maps: maps, budgetClock: () => _now, budgetTicksPerSecond: 1_000_000);

    /// <summary>A port session with one ene-scan done and the bus log cleared (port 0 unless <paramref name="port"/>).</summary>
    private async Task<EneDramAdapter> Scanned(EneDramOptions options, FakeSmbus? port = null)
    {
        var bus = port ?? Bus;
        var a = Make(options);
        var id = EneDramAdapter.PortId(bus.Port);
        await a.OpenForBringupAsync(id, default);
        (await a.RunProbeAsync(id, EneDramAdapter.ProbeEneScan, default)).Ok.Should().BeTrue();
        bus.Sent.Clear();
        return a;
    }

    private sealed class MapSource(NativeControllerMap map) : INativeMapSource
    {
        public NativeControllerMap? Get(ControllerId id) => id.Value.Contains("0x", StringComparison.Ordinal) ? map : null;
    }

    private sealed class Services(EneDramOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(EneDramOptions) ? options : null;
    }
}

/// <summary>Opens <see cref="FakeSmbus"/> ports; <see cref="Blocked"/> makes every open fail like a pin/identity refusal.</summary>
internal sealed class FakeEneProvider : IEneSmbusProvider
{
    private readonly Dictionary<int, FakeSmbus> _buses = [];

    public int Opens { get; private set; }

    public string? Blocked { get; set; }

    public bool Disposed { get; private set; }

    public IReadOnlyDictionary<string, string> Pins { get; } = new Dictionary<string, string>
    {
        ["libPath"] = "pinned-lib", ["libSha256"] = "pinned-lib-sha", ["libApiExpected"] = "2.0.0", ["libFileRelease"] = "2.2.0",
        ["modulePath"] = "module", ["moduleSha256"] = "pinned-module",
    };

    /// <summary>Like the real provider: the admitted library's evidence once a port opened, none while blocked.</summary>
    public PawnIoVersionEvidence? Version => Blocked is null && Opens > 0 ? new("2.0.0", 0, 0x00020000, PawnIoVersionEvidence.Unavailable) : null;

    public FakeSmbus Bus(int port)
    {
        if (!_buses.TryGetValue(port, out var b))
        {
            b = new FakeSmbus(port);
            _buses[port] = b;
        }

        return b;
    }

    public IPawnIoPort Open(int port)
    {
        Opens++;
        return Blocked is null ? Bus(port) : throw new DeviceBlockedException(Blocked);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// A scripted PIIX4 port with ENE modules: per-module name/config tables, the 0xA0..0xAF signature, receive-byte or
/// read-0x00 acknowledgement, the Micron string, modules that share 0x77 until the slot/address bootstrap moves them, and
/// direct/colour registers. <see cref="Fail"/> overrides the outcome of chosen compounds; a dropped one is not sent.
/// </summary>
internal sealed class FakeSmbus(int port) : IPawnIoPort
{
    private readonly List<Module> _modules = [];
    private long _timeouts;

    public int Port { get; } = port;

    public PawnIoIdentity Identity { get; } = new("PIIX4", 0xB00, 0x1022, 0x790B, 0x1043, 0x8877);

    public long MutexTimeouts => _timeouts;

    public List<SmbusCompound> Sent { get; } = [];

    public Func<SmbusCompound, SmbusResult?>? Fail { get; set; }

    public bool Disposed { get; private set; }

    public Module Add(byte address, string name, int leds, bool receiveByteAck = true)
    {
        var m = new Module(address, -1, name, leds, receiveByteAck);
        _modules.Add(m);
        return m;
    }

    /// <summary>A module still on the shared default address 0x77, answering the bootstrap for <paramref name="slot"/>.</summary>
    public Module AddUnmapped(int slot, string name, int leds)
    {
        var m = new Module(EneRegisters.DefaultDramAddress, slot, name, leds, true);
        _modules.Add(m);
        return m;
    }

    public SmbusResult Execute(SmbusCompound c)
    {
        if (Fail?.Invoke(c) is { } forced)
        {
            if (forced.Dropped)
            {
                _timeouts++;
                return forced;
            }

            Sent.Add(c);
            return forced;
        }

        Sent.Add(c);
        var here = _modules.Where(m => m.Address == c.Address).ToList();
        var m = here.FirstOrDefault();
        var nack = new SmbusResult(false, unchecked((int)0x8007045D), 0, "NACK", Step: c.Kind is SmbusCompoundKind.Probe or SmbusCompoundKind.ReadSig ? "read" : "pointer");
        if (m is null)
        {
            return nack;
        }

        switch (c.Kind)
        {
            case SmbusCompoundKind.Probe:
                return m.ReceiveByteAck ? SmbusResult.Success(0, 0) : nack with { Step = "read" };
            case SmbusCompoundKind.ReadSig:
            {
                if (c.Command == 0x00)
                {
                    return SmbusResult.Success(0, 0);
                }

                var i = c.Command - 0xA0;
                return SmbusResult.Success((byte)(i == m.SignatureFailAt ? 0x55 : i), 0);
            }

            case SmbusCompoundKind.RegRead:
                return SmbusResult.Success(m.Read(c.Register), 0);
            case SmbusCompoundKind.RegWrite:
                if (c.Address == 0x77 && c.Register == EneRegisters.SlotIndex)
                {
                    here.ForEach(x => x.SelectedSlot = c.Command);
                }
                else if (c.Address == 0x77 && c.Register == EneRegisters.I2cAddress)
                {
                    var target = here.FirstOrDefault(x => x.Slot == x.SelectedSlot);
                    target?.MoveTo((byte)(c.Command >> 1));
                }
                else
                {
                    m.Registers[c.Register] = c.Command;
                }

                return SmbusResult.Success(0, 0);
            default:
                for (var k = 0; k < c.Data.Length; k++)
                {
                    m.Registers[(ushort)(c.Register + k)] = c.Data[k];
                }

                return SmbusResult.Success(0, 0);
        }
    }

    public void Dispose() => Disposed = true;

    internal sealed class Module(byte address, int slot, string name, int leds, bool receiveByteAck)
    {
        public byte Address { get; private set; } = address;

        public int Slot { get; } = slot;

        public string Name { get; } = name;

        public int Leds { get; } = leds;

        public bool ReceiveByteAck { get; } = receiveByteAck;

        public int SelectedSlot { get; set; } = -1;

        public int SignatureFailAt { get; set; } = -1;

        public bool Micron { get; set; }

        public Dictionary<ushort, byte> Registers { get; } = [];

        public void MoveTo(byte address) => Address = address;

        public byte Read(ushort register)
        {
            if (register is >= EneRegisters.DeviceName and < EneRegisters.DeviceName + 16)
            {
                var i = register - EneRegisters.DeviceName;
                return i < Name.Length ? (byte)Name[i] : (byte)0;
            }

            if (register is >= EneRegisters.MicronCheck and < EneRegisters.MicronCheck + 6)
            {
                return Micron ? (byte)"Micron"[register - EneRegisters.MicronCheck] : (byte)0;
            }

            if (register is >= EneRegisters.ConfigTable and < EneRegisters.ConfigTable + 64)
            {
                var off = register - EneRegisters.ConfigTable;
                var countAt = Name is "AUMA0-E6K5-0107" or "AUMA0-E6K5-1110" ? 0x03 : 0x02;
                return off == countAt ? (byte)Leds : off is 0x02 or 0x03 ? (byte)0x7E : (byte)off;
            }

            return Registers.GetValueOrDefault(register);
        }
    }
}
