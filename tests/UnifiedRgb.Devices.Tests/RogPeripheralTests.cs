using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogPeripheralTests
{
    [Fact]
    public async Task Lever_getters_use_existing_serialized_session_and_known_query_only()
    {
        var hid = new FakeDevices { LeverMode = 7 };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var target = (await session.QueryPairingAsync(default)).Single(p => p.IsAzothX);
        (await session.QueryCurrentLeverAsync(target, default)).Function.Should().Be(RogLeverFunction.Custom);
        (await session.QueryEnabledLeverModesAsync(target, default)).Modes.Should().Equal(0, 1, 3, 7);
        hid.Writes.Select(r => (r[1], r[2])).Should().Equal(((byte)0xA0, (byte)0), ((byte)0x22, (byte)1), ((byte)0x25, (byte)0x0B));
        session.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Lever_FFAA_stops_after_one_report_invalidates_generation_and_never_retries()
    {
        var hid = new FakeDevices { RefuseLever = true };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var target = (await session.QueryPairingAsync(default)).Single(p => p.IsAzothX);
        var generation = session.Generation; var readStart = hid.Reads;
        var query = () => session.QueryCurrentLeverAsync(target, default);
        await query.Should().ThrowAsync<InvalidDataException>();
        hid.Reads.Should().Be(readStart + 1); hid.Writes.Should().HaveCount(2);
        session.IsAvailable.Should().BeFalse(); session.Generation.Should().BeGreaterThan(generation);
        await query.Should().ThrowAsync<DeviceBlockedException>(); hid.Writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Lever_getter_refuses_unpaired_or_wrong_notify_route_before_output()
    {
        var hid = new FakeDevices();
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var unpaired = () => session.QueryCurrentLeverAsync(new(0x1C25, 2, 4), default);
        await unpaired.Should().ThrowAsync<DeviceBlockedException>(); hid.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(128, 10)]
    public void Keyboard_frame_preserves_every_blue_byte_and_zero_pads_64_byte_packets(int count, int packets)
    {
        var keys = Enumerable.Range(0, count).Select(i => new RogKeyColor((byte)i, new Rgb((byte)(i + 1), 0x34, 0xFE))).ToArray();
        var frames = RogProtocol.KeyboardFrame(2, keys);
        frames.Should().HaveCount(packets);
        var index = 0;
        foreach (var frame in frames)
        {
            frame.Should().HaveCount(64);
            frame.Take(3).Should().Equal(2, 0xC0, 0x81);
            frame[4].Should().Be(0);
            frame[3].Should().BeInRange(1, 14);
            for (var k = 0; k < frame[3]; k++, index++)
                frame.Skip(5 + 4 * k).Take(4).Should().Equal(keys[index].LedId, keys[index].Color.R, 0x34, 0xFE);
            frame.Skip(5 + 4 * frame[3]).Should().OnlyContain(b => b == 0);
        }
        index.Should().Be(count);
    }

    [Fact]
    public void Keyboard_golden_vector_does_not_enter_a_save_or_configuration_path()
    {
        var packet = RogProtocol.KeyboardFrame(3, [new(0x2A, new(1, 2, 3))]).Single();
        Convert.ToHexString(packet[..9]).Should().Be("03C08101002A010203");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Invalid_frame_count_is_rejected_before_output(int count)
    {
        var keys = Enumerable.Range(0, count).Select(i => new RogKeyColor((byte)i, Rgb.White)).ToArray();
        var act = () => RogProtocol.KeyboardFrame(2, keys);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Duplicate_LED_ids_and_reserved_routes_are_rejected()
    {
        var duplicate = () => RogProtocol.KeyboardFrame(2, [new(7, Rgb.Black), new(7, Rgb.White)]);
        duplicate.Should().Throw<ArgumentException>();
        foreach (var rid in new byte[] { 0, 1, 4, 255 })
        {
            var invalid = () => RogProtocol.KeyboardFrame(rid, [new(1, Rgb.Black)]);
            invalid.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [Fact]
    public void Known_queries_match_fixed_golden_vectors()
    {
        RogProtocol.PairingQuery()[..5].Should().Equal(1, 0xA0, 0, 0, 0);
        RogProtocol.KeyboardLayoutQuery(2)[..5].Should().Equal(2, 0x12, 0x12, 0, 0);
        RogProtocol.KeyboardVersionQuery(2)[..5].Should().Equal(2, 0x12, 0, 0, 0);
        RogProtocol.MouseLightingQuery(3, 0x1234)[..5].Should().Equal(3, 0x12, 3, 0x34, 0x12);
    }

    [Fact]
    public void Pairing_identifies_logical_products_not_receiver_names()
    {
        var paired = RogProtocol.ParsePairing(Paired());
        paired.Should().HaveCount(2);
        paired[0].IsAzothX.Should().BeTrue();
        paired[1].IsHarpeEdition20.Should().BeTrue();
        paired[0].NotifyReportId.Should().Be(4);
        paired[1].NotifyReportId.Should().Be(5);
        paired[0].ReportId.Should().Be(2);
        paired[1].ReportId.Should().Be(3);
        var unknown = Paired();
        unknown[5] = 0x34; unknown[6] = 0x12;
        RogProtocol.ParsePairing(unknown)[0].IsAzothX.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(3, 3)]
    [InlineData(4, 1)]
    [InlineData(5, 0)]
    [InlineData(7, 1)]
    [InlineData(8, 5)]
    [InlineData(11, 2)]
    public void Malformed_pairing_tables_fail_closed(int index, byte value)
    {
        var reply = Paired();
        reply[index] = value;
        if (index == 5) reply[6] = 0;
        var act = () => RogProtocol.ParsePairing(reply);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Truncated_pairing_is_not_partially_accepted()
    {
        var act = () => RogProtocol.ParsePairing(Paired().AsSpan(0, 12));
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Receiver_grouping_keeps_two_receivers_separate_and_ignores_input_collections()
    {
        var all = ReceiverCollections("A").Concat(ReceiverCollections("B")).ToList();
        all.Add(Collection("A", 1) with { UsagePage = 1 });
        all.Add(Collection("A", 1) with { Interface = 0 });
        all.Add(Collection("A", 1) with { InputReportLength = 65 });
        var groups = RogReceiver.Group(all);
        groups.Should().HaveCount(2);
        groups.Should().OnlyContain(r => r.Channels.Count == 3);
        groups[0].Key.Should().NotBe(groups[1].Key);
        groups[0].Key.Should().HaveLength(64);
        RogReceiver.Group(ReceiverCollections("A").Append(Collection("A", 1))).Should().BeEmpty("duplicate routes are ambiguous");
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData(@"\\?\hid#vid_0b05&pid_1ace&mi_02#instance#{x}")]
    [InlineData(@"\\?\hid#vid_0b05&pid_1ace&mi_02&col01#instance#{x}")]
    public void Ungroupable_paths_cannot_select_a_receiver(string path) => RogReceiver.ParentKey(path).Should().BeNull();

    [Fact]
    public async Task Passive_adapter_discovery_and_bringup_open_send_and_open_nothing()
    {
        var hid = new FakeDevices();
        await using var adapter = new RogAdapter(hid, new());
        var controller = (await adapter.DiscoverAsync(default)).Single();
        controller.Zones.Should().BeEmpty();
        adapter.IsControlVerified(controller.Id).Should().BeFalse();
        await adapter.OpenForBringupAsync(controller.Id, default);
        await adapter.ReleaseAsync(controller.Id, default);
        hid.Opens.Should().Be(0);
        hid.Writes.Should().BeEmpty();
        var act = () => adapter.OpenAsync(controller.Id, default);
        await act.Should().ThrowAsync<DeviceBlockedException>();
    }

    [Fact]
    public async Task Leased_identity_probe_names_devices_and_does_not_modify_lighting()
    {
        var hid = new FakeDevices();
        await using var adapter = new RogAdapter(hid, new());
        var controller = (await adapter.DiscoverAsync(default)).Single();
        var blocked = () => adapter.RunProbeAsync(controller.Id, "receiver-pairing", default);
        await blocked.Should().ThrowAsync<DeviceBlockedException>();
        await adapter.OpenForBringupAsync(controller.Id, default);
        var result = await adapter.RunProbeAsync(controller.Id, "receiver-pairing", default);
        result.Fields["route2"].Should().Contain("Azoth X");
        result.Fields["route3"].Should().Contain("P725");
        hid.Writes.Should().ContainSingle().Which[1].Should().Be(0xA0);
        await adapter.ReleaseAsync(controller.Id, default);
        hid.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task No_output_before_verified_identity_and_control_gates()
    {
        var hid = new FakeDevices();
        var receiver = RogReceiver.Discover(hid).Single();
        await using var session = new RogReceiverSession(hid, receiver);
        var keyboard = new RogPairedDevice(0x1C25, 2, 4);
        var unverified = () => session.WriteKeyboardAsync(keyboard, [new(1, Rgb.White)], false, session.Generation, default);
        await unverified.Should().ThrowAsync<DeviceBlockedException>();
        var unidentified = () => session.WriteKeyboardAsync(keyboard, [new(1, Rgb.White)], true, session.Generation, default);
        await unidentified.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Should().BeEmpty();
        hid.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Targeted_queries_cannot_be_sent_to_the_other_device_kind()
    {
        var hid = new FakeDevices();
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var paired = await session.QueryPairingAsync(default);
        var wrongKeyboard = () => session.QueryKeyboardAsync(paired[1], true, default);
        await wrongKeyboard.Should().ThrowAsync<InvalidOperationException>();
        var wrongMouse = () => session.QueryMouseLightingAsync(paired[0], 0, default);
        await wrongMouse.Should().ThrowAsync<InvalidOperationException>();
        hid.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Whole_frame_and_queries_never_interleave_on_shared_receiver()
    {
        var hid = new FakeDevices { DelayReads = true };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var paired = await session.QueryPairingAsync(default);
        var keys = Enumerable.Range(0, 30).Select(i => new RogKeyColor((byte)i, Rgb.White)).ToArray();
        var frame = session.WriteKeyboardAsync(paired[0], keys, true, session.Generation, default);
        var query = session.QueryMouseLightingAsync(paired[1], 0, default);
        await Task.WhenAll(frame, query);
        hid.Writes.Select(r => r[1]).Should().Equal(0xA0, 0xC0, 0xC0, 0xC0, 0x12);
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("opcode")]
    [InlineData("key")]
    [InlineData("short")]
    [InlineData("timeout")]
    public async Task Failed_exchange_invalidates_generation_and_never_retries(string failure)
    {
        var hid = new FakeDevices();
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single(), 20);
        var paired = await session.QueryPairingAsync(default);
        var generation = session.Generation;
        hid.Failure = failure;
        var act = () => session.WriteKeyboardAsync(paired[0], [new(1, Rgb.White)], true, generation, default);
        await act.Should().ThrowAsync<Exception>();
        session.Generation.Should().BeGreaterThan(generation);
        hid.Writes.Should().HaveCount(2);
        var stale = () => session.WriteKeyboardAsync(paired[0], [new(1, Rgb.White)], true, generation, default);
        await stale.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Sleep_layout_response_is_not_accepted_as_awake_keyboard()
    {
        var hid = new FakeDevices { ZeroLayout = true };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var paired = await session.QueryPairingAsync(default);
        var act = () => session.QueryKeyboardAsync(paired[0], true, default);
        await act.Should().ThrowAsync<InvalidDataException>();
        hid.Writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Opt_in_adapter_uses_shared_frame_colours_without_firmware_free_running_or_save()
    {
        var hid = new FakeDevices();
        var pin = RogReceiver.Discover(hid).Single().Key;
        var binding = new RogKeyboardBinding { ReceiverKey = pin, ReportId = 2, LedIds = [1, 5, 7], ControlVerified = true };
        await using var adapter = new RogAdapter(hid, new RogOptions { Keyboards = [binding] });
        var descriptors = await adapter.DiscoverAsync(default);
        var keyboard = descriptors.Single(d => d.DeviceType == DeviceType.Keyboard);
        keyboard.Zones.Single().Mapped.Should().BeFalse();
        await adapter.OpenAsync(keyboard.Id, default);
        adapter.WriteFrame(keyboard.Id, new Rgb[][] { [new(7, 9, 11)] }).Should().BeTrue();
        var direct = hid.Writes.Last();
        direct[3].Should().Be(3);
        direct.Skip(5).Take(12).Should().Equal(1, 7, 9, 11, 5, 7, 9, 11, 7, 7, 9, 11);
        await adapter.CloseAsync(keyboard.Id);
        hid.Writes.Select(p => p[1]).Should().Equal(0xA0, 0x12, 0xC0);
        adapter.FrameBudget(keyboard.Id)!.Frames.Should().Be(1);
        adapter.FrameBudget(keyboard.Id)!.OperationsLastFrame.Should().Be(1);
    }

    [Fact]
    public async Task Changed_pairing_cannot_open_a_pinned_keyboard_as_a_mouse()
    {
        var hid = new FakeDevices { MouseOnRoute2 = true };
        var pin = RogReceiver.Discover(hid).Single().Key;
        await using var adapter = new RogAdapter(hid, new RogOptions { Keyboards = [new() { ReceiverKey = pin, ReportId = 2, LedIds = [1], ControlVerified = true }] });
        var keyboard = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Keyboard);
        var act = () => adapter.OpenAsync(keyboard.Id, default);
        await act.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancellation_and_close_send_no_release_save_or_power_commands()
    {
        var hid = new FakeDevices();
        var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        await session.QueryPairingAsync(default);
        await session.DisposeAsync();
        await session.DisposeAsync();
        hid.Writes.Should().ContainSingle();
        var act = () => session.QueryPairingAsync(default);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Failed_receiver_requires_a_new_session_and_can_recover_by_passive_rescan()
    {
        var hid = new FakeDevices { Failure = "opcode" };
        await using var adapter = new RogAdapter(hid, new());
        var id = (await adapter.DiscoverAsync(default)).Single().Id;
        await adapter.OpenForBringupAsync(id, default);
        var fail = () => adapter.RunProbeAsync(id, "receiver-pairing", default);
        await fail.Should().ThrowAsync<InvalidDataException>();
        hid.Failure = null;
        await adapter.DiscoverAsync(default);
        await adapter.OpenForBringupAsync(id, default);
        (await adapter.RunProbeAsync(id, "receiver-pairing", default)).Ok.Should().BeTrue();
        hid.Opens.Should().Be(2);
        hid.Writes.Should().HaveCount(2);
    }

    [Fact]
    public void Mouse_static_snapshot_round_trips_exact_colour_and_brightness_without_save()
    {
        var reply = new byte[64];
        new byte[] { 3, 0x12, 3, 0, 0, 0, 100, 255, 135, 20 }.CopyTo(reply, 0);
        var state = RogProtocol.ParseMouseStatic(reply, 3);
        state.Should().Be(new RogMouseStaticState(100, new(255, 135, 20)));
        var restore = RogProtocol.MouseStaticReport(3, state);
        restore[..10].Should().Equal(3, 0x51, 0x28, 0, 0, 0, 100, 255, 135, 20);
        restore.Skip(10).Should().OnlyContain(b => b == 0);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 101)]
    [InlineData(10, 1)]
    [InlineData(13, 1)]
    [InlineData(63, 1)]
    public void Animated_or_unknown_mouse_fields_are_never_silently_lost_during_restore(int index, byte value)
    {
        var reply = new byte[64];
        reply[0] = 3; reply[1] = 0x12; reply[2] = 3;
        reply[index] = value;
        var act = () => RogProtocol.ParseMouseStatic(reply, 3);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Mouse_output_requires_explicit_gate_pairing_and_current_static_readback()
    {
        var hid = new FakeDevices();
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var paired = await session.QueryPairingAsync(default);
        var mouse = paired[1];
        var disabled = () => session.WriteMouseStaticAsync(mouse, new(50, Rgb.White), false, session.Generation, default);
        await disabled.Should().ThrowAsync<DeviceBlockedException>();
        var unread = () => session.WriteMouseStaticAsync(mouse, new(50, Rgb.White), true, session.Generation, default);
        await unread.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Should().ContainSingle();
        await session.QueryMouseLightingAsync(mouse, 0, default);
        var reads = hid.Reads;
        await session.WriteMouseStaticAsync(mouse, new(50, Rgb.White), true, session.Generation, default);
        hid.Reads.Should().Be(reads, "mouse setEffect has no ACK; an ACK wait would incorrectly fault the session");
        hid.Writes.Select(r => r[1]).Should().Equal(0xA0, 0x12, 0x51);
        hid.Writes.Last()[2].Should().Be(0x28);
    }

    [Fact]
    public async Task Mouse_adapter_streams_one_aggregate_colour_through_the_existing_writer_contract()
    {
        var hid = new FakeDevices();
        var pin = RogReceiver.Discover(hid).Single().Key;
        await using var adapter = new RogAdapter(hid, new RogOptions { Mice = [new() { ReceiverKey = pin, ReportId = 3, ControlVerified = true }] });
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        mouse.TotalLeds.Should().Be(1);
        mouse.Zones.Single().Mapped.Should().BeFalse();
        await adapter.OpenAsync(mouse.Id, default);
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [new(8, 9, 10)] }).Should().BeTrue();
        hid.Writes.Last().Take(10).Should().Equal(3, 0x51, 0x28, 0, 0, 0, 100, 8, 9, 10);
        adapter.FrameBudget(mouse.Id)!.OperationsLastFrame.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(63)]
    [InlineData(100)]
    public async Task Mouse_scene_frames_preserve_the_freshly_observed_hardware_brightness(byte brightness)
    {
        var hid = new FakeDevices { MouseState = new(brightness, new(255, 135, 20)) };
        var pin = RogReceiver.Discover(hid).Single().Key;
        await using var adapter = new RogAdapter(hid, new RogOptions { Mice = [new() { ReceiverKey = pin, ReportId = 3, ControlVerified = true }] });
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(mouse.Id, default);
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [new(12, 24, 36)] }).Should().BeTrue();
        hid.MouseState.Should().Be(new RogMouseStaticState(brightness, new(12, 24, 36)));
        hid.Writes.Should().NotContain(w => w[1] == 0x50, "scene frames never save a device profile");
    }

    [Fact]
    public async Task Mouse_reopen_uses_the_new_snapshot_rather_than_retaining_an_old_brightness()
    {
        var hid = new FakeDevices { MouseState = new(25, Rgb.White) };
        var pin = RogReceiver.Discover(hid).Single().Key;
        await using var adapter = new RogAdapter(hid, new RogOptions { Mice = [new() { ReceiverKey = pin, ReportId = 3, ControlVerified = true }] });
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(mouse.Id, default);
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [Rgb.Black] }).Should().BeTrue();
        var writes = hid.Writes.Count;
        await adapter.CloseAsync(mouse.Id);
        hid.Writes.Count.Should().Be(writes, "close has no invented restore command");
        hid.MouseState = new(60, Rgb.White);
        await adapter.OpenAsync(mouse.Id, default);
        await Task.Delay(adapter.DelayUntilNextFrame(mouse.Id) + TimeSpan.FromMilliseconds(2));
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [new(30, 50, 70)] }).Should().BeTrue();
        hid.MouseState.Should().Be(new RogMouseStaticState(60, new(30, 50, 70)));
    }

    [Fact]
    public void Same_receiver_route_cannot_be_configured_as_both_keyboard_and_mouse()
    {
        var key = new string('A', 64);
        var act = () => new RogAdapter(new FakeDevices(), new RogOptions
        {
            Keyboards = [new() { ReceiverKey = key, ReportId = 2, LedIds = [1] }],
            Mice = [new() { ReceiverKey = key, ReportId = 2 }],
        });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Mouse_idle_change_preserves_the_full_current_power_configuration()
    {
        var target = new RogPairedDevice(0x1D7C, 3, 5);
        var reply = new byte[64];
        new byte[] { 3, 0x12, 7, 0, 0, 95, 2, 20, 0, 0, 0, 1, 1, 1, 255 }.CopyTo(reply, 0);
        var snapshot = RogPowerProtocol.Parse(target, reply);
        snapshot.IdleCode.Should().Be(2);
        RogPowerProtocol.IdleReport(target, snapshot, RogPowerProtocol.Never).Take(12)
            .Should().Equal(3, 0x51, 0x37, 0, 0, 255, 0, 20, 0, 1, 1, 255);
    }

    [Fact]
    public void Keyboard_idle_change_does_not_send_the_low_battery_or_saving_mode_commands()
    {
        var target = new RogPairedDevice(0x1C25, 2, 4);
        var reply = new byte[64];
        new byte[] { 2, 0x12, 1, 0, 0, 0, 90, 3, 1, 0, 20 }.CopyTo(reply, 0);
        var snapshot = RogPowerProtocol.Parse(target, reply);
        var report = RogPowerProtocol.IdleReport(target, snapshot, RogPowerProtocol.Never);
        report.Take(6).Should().Equal(2, 0x51, 0x38, 0, 0, 255);
        report.Skip(6).Should().OnlyContain(b => b == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_sleep_action_requires_snapshot_and_verifies_only_idle_changed(bool mouse)
    {
        var hid = new FakeDevices();
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var target = (await session.QueryPairingAsync(default))[mouse ? 1 : 0];
        var unauthorized = () => session.SetIdleTimeoutAsync(target, 255, false, default);
        await unauthorized.Should().ThrowAsync<DeviceBlockedException>();
        var unread = () => session.SetIdleTimeoutAsync(target, 255, true, default);
        await unread.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Should().ContainSingle();
        var before = await session.QueryPowerAsync(target, default);
        var result = await session.SetIdleTimeoutAsync(target, 255, true, default);
        result.Before.Should().Be(before);
        result.After.IdleCode.Should().Be(255);
        before.SameOtherSettings(result.After).Should().BeTrue();
        hid.Writes.Select(p => p[1]).Should().Equal(0xA0, 0x12, 0x51, 0x12);
        hid.Writes.Should().NotContain(p => p[1] == 0x50);
    }

    [Fact]
    public async Task Unexpected_non_idle_power_change_is_a_failed_operation_not_success()
    {
        var hid = new FakeDevices { CorruptPowerOnWrite = true };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var mouse = (await session.QueryPairingAsync(default))[1];
        await session.QueryPowerAsync(mouse, default);
        var act = () => session.SetIdleTimeoutAsync(mouse, 255, true, default);
        await act.Should().ThrowAsync<InvalidDataException>();
        session.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Unsolicited_vendor_status_is_discarded_without_resending_or_accepting_it_as_reply()
    {
        var hid = new FakeDevices { OneStatusFirst = true };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        (await session.QueryPairingAsync(default)).Should().HaveCount(2);
        hid.Writes.Should().ContainSingle();
        hid.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Four_queued_lighting_echoes_do_not_cause_a_false_query_failure_or_a_resend()
    {
        var hid = new FakeDevices { QueuedLightingEchoes = 4 };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        (await session.QueryPairingAsync(default)).Should().HaveCount(2);
        hid.Writes.Should().ContainSingle();
        hid.Reads.Should().Be(5);
    }

    [Fact]
    public void Keyboard_static_snapshot_preserves_direction_width_secondary_colour_and_background()
    {
        var reply = new byte[64];
        new byte[] { 2, 0x25, 0x0E, 0, 0, 30, 70, 0, 255, 255, 1, 2, 3, 4, 5, 6, 7, 8, 9 }.CopyTo(reply, 0);
        var state = RogProtocol.ParseKeyboardStatic(reply, 2);
        var restore = RogProtocol.KeyboardStaticReport(2, state);
        restore.Take(3).Should().Equal(2, 0x51, 0x2C);
        restore.Skip(3).Should().Equal(reply.Skip(3));
    }

    [Fact]
    public async Task Keyboard_static_setter_requires_current_effect_snapshot_and_has_no_ACK_wait()
    {
        var hid = new FakeDevices();
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var keyboard = (await session.QueryPairingAsync(default))[0];
        var unread = () => session.WriteKeyboardStaticAsync(keyboard, hid.KeyboardState, true, session.Generation, default);
        await unread.Should().ThrowAsync<DeviceBlockedException>();
        var original = await session.QueryKeyboardStaticAsync(keyboard, default);
        var reads = hid.Reads;
        await session.WriteKeyboardStaticAsync(keyboard, original with { Color = Rgb.White }, true, session.Generation, default);
        hid.Reads.Should().Be(reads);
        (await session.QueryKeyboardStaticAsync(keyboard, default)).Color.Should().Be(Rgb.White);
        await session.WriteKeyboardStaticAsync(keyboard, original, true, session.Generation, default);
        (await session.QueryKeyboardStaticAsync(keyboard, default)).Should().Be(original);
        hid.Writes.Should().NotContain(w => w[1] == 0x50);
    }

    [Fact]
    public async Task Keyboard_animated_current_effect_cannot_be_misrepresented_as_a_static_restore_point()
    {
        var hid = new FakeDevices { KeyboardEffect = 1 };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var keyboard = (await session.QueryPairingAsync(default))[0];
        var act = () => session.QueryKeyboardStaticAsync(keyboard, default);
        await act.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Select(p => p[1]).Should().Equal(0xA0, 0x12);
    }

    [Fact]
    public async Task Whole_keyboard_colour_binding_does_not_need_or_invent_LED_indices()
    {
        var hid = new FakeDevices();
        var pin = RogReceiver.Discover(hid).Single().Key;
        await using var adapter = new RogAdapter(hid, new RogOptions { Keyboards = [new() { ReceiverKey = pin, ReportId = 2, WholeColorOnly = true, ControlVerified = true }] });
        var keyboard = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Keyboard);
        keyboard.TotalLeds.Should().Be(1);
        await adapter.OpenAsync(keyboard.Id, default);
        adapter.WriteFrame(keyboard.Id, new Rgb[][] { [new(4, 8, 12)] }).Should().BeTrue();
        hid.KeyboardState.Color.Should().Be(new Rgb(4, 8, 12));
        hid.Writes.Should().NotContain(w => w[1] == 0xC0 || w[1] == 0x50);
    }

    [Fact]
    public async Task Existing_single_packet_keyboard_effect_can_be_snapshotted_then_restored_after_temporary_static()
    {
        var hid = new FakeDevices { KeyboardEffect = 6, KeyboardFlags = 1 };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var keyboard = (await session.QueryPairingAsync(default))[0];
        var original = await session.QueryKeyboardEffectAsync(keyboard, default);
        original.EffectType.Should().Be(6);
        original.Flags.Should().Be(1);
        var white = new RogKeyboardEffectState(0, 0, original.Parameters with { Color = Rgb.White, Brightness = 40 });
        await session.WriteKeyboardEffectAsync(keyboard, white, true, session.Generation, default);
        (await session.QueryKeyboardEffectAsync(keyboard, default)).Should().Be(white);
        await session.WriteKeyboardEffectAsync(keyboard, original, true, session.Generation, default);
        (await session.QueryKeyboardEffectAsync(keyboard, default)).Should().Be(original);
        hid.Writes.Should().NotContain(w => w[1] == 0x50);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(255)]
    public void Unknown_or_multi_packet_keyboard_restore_is_rejected_before_sending(byte effectType)
    {
        var act = () => RogProtocol.KeyboardEffectQuery(2, effectType);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Ripple_uses_all_three_descending_chunks_and_restores_the_complete_palette()
    {
        var hid = new FakeDevices { KeyboardEffect = 5 };
        await using var session = new RogReceiverSession(hid, RogReceiver.Discover(hid).Single());
        var keyboard = (await session.QueryPairingAsync(default))[0];
        var original = await session.QueryKeyboardNativeAsync(keyboard, default);
        original.EffectType.Should().Be(5);
        original.Payloads.Should().HaveCount(3);
        var white = RogProtocol.KeyboardColourFrame(Rgb.White, 40);
        await session.WriteKeyboardNativeAsync(keyboard, white, true, session.Generation, default);
        (await session.QueryKeyboardNativeAsync(keyboard, default)).SameContent(white).Should().BeTrue();
        var start = hid.Writes.Count;
        await session.WriteKeyboardNativeAsync(keyboard, original, true, session.Generation, default);
        hid.Writes.Skip(start).Select(p => p[4]).Should().Equal(2, 1, 0);
        (await session.QueryKeyboardNativeAsync(keyboard, default)).SameContent(original).Should().BeTrue();
        hid.Writes.Should().NotContain(p => p[1] == 0x50);
    }

    [Fact]
    public void Missing_palette_chunks_or_wrong_chunk_indices_fail_before_output()
    {
        var incomplete = new RogKeyboardNativeState(5, ["1E500001040700FF000014FF8714"]);
        var act = () => RogProtocol.KeyboardNativeReports(2, incomplete);
        act.Should().Throw<ArgumentException>();
        var reply = new byte[64]; reply[0] = 2; reply[1] = 0x25; reply[2] = 0x0E; reply[3] = 5; reply[4] = 0;
        var wrong = () => RogProtocol.ParseKeyboardNativePayload(reply, 2, 5, 0);
        wrong.Should().Throw<InvalidDataException>();
    }

    internal static HidCollectionInfo Collection(string instance, byte rid) => new(
        $@"\\?\hid#vid_0b05&pid_1ace&mi_02&col0{rid}#8&{instance}&0&000{rid - 1}#{{00111111-2222-3333-4444-555555555555}}",
        0x0B05, 0x1ACE, 2, rid switch { 1 => (ushort)0xFF02, 2 => (ushort)0xFF00, _ => (ushort)0xFF01 }, 1, 64, 64, 0);

    internal static HidCollectionInfo[] ReceiverCollections(string instance) => [Collection(instance, 1), Collection(instance, 2), Collection(instance, 3)];

    private static byte[] Paired()
    {
        var reply = new byte[64];
        new byte[] { 1, 0xA0, 0, 2, 0, 0x25, 0x1C, 2, 4, 0x7C, 0x1D, 3, 5 }.CopyTo(reply, 0);
        return reply;
    }

    internal sealed class FakeDevices : IHidReportDevices
    {
        public List<byte[]> Writes { get; } = [];
        public int Opens { get; private set; }
        public int Reads { get; private set; }
        public int Disposes { get; private set; }
        public bool StallWrites { get; set; }
        public bool Absent { get; set; }
        public TaskCompletionSource? ReadStarted { get; set; }
        public TaskCompletionSource? ReadRelease { get; set; }
        public string? Failure { get; set; }
        public bool DelayReads { get; init; }
        public bool ZeroLayout { get; init; }
        public bool MouseOnRoute2 { get; init; }
        public bool CorruptPowerOnWrite { get; init; }
        public byte KeyboardIdle { get; set; } = 2;
        public byte MouseIdle { get; set; } = 2;
        public byte MouseLowPower { get; set; } = 20;
        public RogMouseStaticState MouseState { get; set; } = new(100, new(255, 135, 20));
        public bool OneStatusFirst { get; init; }
        public byte LeverMode { get; init; }
        public bool RefuseLever { get; init; }
        public int QueuedLightingEchoes { get; init; }
        public byte KeyboardEffect { get; set; }
        public byte KeyboardFlags { get; set; }
        public RogKeyboardStaticState KeyboardState { get; set; } = new(30, 80, 255, 255, new(255, 135, 20), Rgb.Black, Rgb.Black);
        public RogKeyboardNativeState MultiEffect { get; set; } = new(5, new[]
        {
            Convert.ToHexString(new byte[] { 30, 80, 0, 1, 4, 7, 0, 255, 0, 0, 20, 255, 135, 20 }),
            Convert.ToHexString(new byte[] { 33, 0, 255, 0, 46, 0, 0, 255, 60, 255, 0, 255, 80, 255, 255, 0 }),
            Convert.ToHexString(new byte[] { 100, 255, 0, 13, 0, 0, 0 }),
        });
        private Dictionary<byte, string> PendingChunks { get; } = [];
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => pid == 0x1ACE && !Absent ? ReceiverCollections("A") : [];
        public IHidReportChannel Open(HidCollectionInfo collection) { Opens++; return new Channel(this); }

        private sealed class Channel(FakeDevices owner) : IHidReportChannel
        {
            private byte[]? _request;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.StallWrites) return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, ct));
                _request = report.ToArray();
                owner.Writes.Add(_request);
                if (_request[1] == 0x51 && _request[2] == 0x28)
                {
                    var state = (byte[])_request.Clone(); state[1] = 0x12; state[2] = 3;
                    owner.MouseState = RogProtocol.ParseMouseStatic(state, _request[0]);
                }
                if (_request[1] == 0x51 && _request[2] == 0x38) owner.KeyboardIdle = _request[5];
                if (_request[1] == 0x51 && _request[2] == 0x37)
                {
                    owner.MouseIdle = _request[5];
                    if (owner.CorruptPowerOnWrite) owner.MouseLowPower = 30;
                }
                if (_request[1] == 0x51 && _request[2] == 0x2C)
                {
                    var layout = RogProtocol.KeyboardPayloadLengths(_request[3]);
                    if (layout.Count > 1)
                    {
                        var index = layout.Count - 1 - _request[4];
                        owner.PendingChunks[_request[4]] = Convert.ToHexString(_request.AsSpan(5, layout[index]));
                        if (_request[4] == 0 && owner.PendingChunks.Count == layout.Count)
                        {
                            owner.MultiEffect = new(_request[3], Enumerable.Range(0, layout.Count).Select(i => owner.PendingChunks[(byte)(layout.Count - 1 - i)]).ToArray());
                            owner.KeyboardEffect = _request[3];
                            owner.PendingChunks.Clear();
                        }
                    }
                    else
                    {
                        owner.KeyboardEffect = _request[3]; owner.KeyboardFlags = _request[7];
                        var record = (byte[])_request.Clone(); record[1] = 0x25; record[2] = 0x0E; record[3] = 0; record[7] = 0;
                        owner.KeyboardState = RogProtocol.ParseKeyboardStatic(record, 2);
                    }
                }
                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                owner.Reads++;
                owner.ReadStarted?.TrySetResult();
                if (owner.ReadRelease is { } release) await release.Task.WaitAsync(ct);
                if (owner.DelayReads) await Task.Delay(2, ct);
                if (owner.Failure == "timeout") await Task.Delay(Timeout.Infinite, ct);
                var reply = (byte[])_request!.Clone();
                if (reply[1] == 0x12 && reply[2] == 3)
                {
                    reply = RogProtocol.MouseStaticReport(reply[0], owner.MouseState); reply[1] = 0x12; reply[2] = 3;
                }
                if (reply[1] == 0x22 && reply[2] == 1) reply[5] = owner.LeverMode;
                if (reply[1] == 0x25 && reply[2] == 0x0B)
                    foreach (var mode in new[] { 0, 1, 3, 7 }) reply[5 + mode] = 1;
                if (owner.RefuseLever && reply[1] == 0x22) { reply[1] = 0xFF; reply[2] = 0xAA; }
                if (reply[1] == 0xA0)
                {
                    reply = Paired();
                    if (owner.MouseOnRoute2) { BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(5), 0x1D7C); reply[8] = 5; }
                }
                if (reply[1] == 0x12 && reply[2] == 0x12 && !owner.ZeroLayout) reply[5] = 1;
                if (reply[1] == 0x12 && reply[2] == 0) { reply[9] = 6; reply[10] = owner.KeyboardEffect; reply[11] = 1; }
                if (reply[1] == 0x25 && reply[2] == 0x0E)
                {
                    if (RogProtocol.KeyboardPayloadLengths(owner.KeyboardEffect).Count > 1)
                    {
                        var index = owner.MultiEffect.Payloads.Count - 1 - reply[4];
                        Convert.FromHexString(owner.MultiEffect.Payloads[index]).CopyTo(reply.AsSpan(5));
                    }
                    else
                    {
                        reply = RogProtocol.KeyboardStaticReport(2, owner.KeyboardState); reply[1] = 0x25; reply[2] = 0x0E;
                        reply[3] = owner.KeyboardEffect; reply[7] = owner.KeyboardFlags;
                    }
                }
                if (reply[1] == 0x12 && reply[2] == 1) { reply[6] = 90; reply[7] = owner.KeyboardIdle; reply[8] = 1; }
                if (reply[1] == 0x12 && reply[2] == 7)
                {
                    reply[5] = 95; reply[6] = owner.MouseIdle; reply[7] = owner.MouseLowPower;
                    reply[12] = 1; reply[13] = 1; reply[14] = 255;
                }
                if (owner.Failure == "rid") reply[0] = 3;
                if (owner.Failure == "opcode") reply[1] = 0x50;
                if (owner.Failure == "key") reply[2] = 0x77;
                if (owner.OneStatusFirst && owner.Reads == 1) { reply[1] = 0xFF; reply[2] = 0xAA; }
                if (owner.Reads <= owner.QueuedLightingEchoes) { reply[1] = 0x51; reply[2] = 0x2C; }
                reply.CopyTo(buffer);
                return owner.Failure == "short" ? 4 : 64;
            }

            public void Dispose() { owner.Disposes++; }
        }
    }
}
