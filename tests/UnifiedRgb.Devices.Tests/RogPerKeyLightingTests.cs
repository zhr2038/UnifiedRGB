using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Fake-only C0 81 per-key colour on the shared keyboard control route (wired 1C24 RID0/65 and 2.4 GHz RID2/64), its bridge
/// queue, the reference-derived candidate map and the readiness plan. Nothing here opens a device or proves optical output,
/// the physical key order or a restore after direct mode.
/// </summary>
public sealed class RogPerKeyLightingTests
{
    private static RogKeyColor[] Keys(int count) =>
        Enumerable.Range(0, count).Select(i => new RogKeyColor((byte)(i * 2 + 1), new((byte)i, (byte)(255 - i), (byte)(i * 3)))).ToArray();

    private static IRogOledConnection Open(HeaderEchoFake io, bool rf) => rf
        ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => true)
        : new RogWiredOledConnection(io, () => true);

    [Fact]
    public void Fourteen_records_fill_one_complete_report_and_the_fifteenth_starts_the_next()
    {
        var keys = Keys(15);
        var reports = RogPerKeyLighting.BoundaryFrame(keys);

        reports.Should().HaveCount(2);
        reports.Should().OnlyContain(r => r.Length == 65 && r[0] == 0 && r[64] == 0);
        reports[0].AsSpan(0, 5).ToArray().Should().Equal(0x00, 0xC0, 0x81, 14, 0x00);
        for (var k = 0; k < 14; k++)
            reports[0].AsSpan(5 + k * 4, 4).ToArray().Should().Equal(keys[k].LedId, keys[k].Color.R, keys[k].Color.G, keys[k].Color.B);
        reports[0].AsSpan(61).ToArray().Should().OnlyContain(b => b == 0, "14 records end at byte 60; the 64-byte RF report has no room for a 15th");
        reports[1].AsSpan(0, 9).ToArray().Should().Equal(0x00, 0xC0, 0x81, 1, 0x00, keys[14].LedId, keys[14].Color.R, keys[14].Color.G, keys[14].Color.B);
        reports[1].AsSpan(9).ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void The_boundary_frame_is_the_established_rf_framing_with_only_the_report_id_moved()
    {
        var keys = Keys(81);
        var boundary = RogPerKeyLighting.BoundaryFrame(keys);
        var rf = RogProtocol.KeyboardFrame(2, keys);

        boundary.Should().HaveCount(6).And.HaveCount(rf.Count);
        for (var i = 0; i < rf.Count; i++) boundary[i].AsSpan(1, 63).ToArray().Should().Equal(rf[i].AsSpan(1, 63).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Frames_outside_one_to_128_keys_return_nothing_to_send(int count) =>
        ((Action)(() => RogPerKeyLighting.BoundaryFrame(Keys(count)))).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public async Task A_duplicate_led_rejects_the_whole_frame_before_any_exchange()
    {
        var keys = Keys(20); keys[17] = keys[3] with { Color = new(1, 2, 3) };
        var exchanges = 0;
        await ((Func<Task>)(() => RogPerKeyLighting.WriteAsync((_, _, _) => { exchanges++; return Task.FromResult(new byte[65]); }, keys, default)))
            .Should().ThrowAsync<ArgumentException>();
        exchanges.Should().Be(0);
    }

    [Theory]
    [InlineData("wired", RogPerKeyLighting.WiredTransport)]
    [InlineData("wireless", RogPerKeyLighting.WirelessTransport)]
    [InlineData("usb", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_the_two_owned_connection_kinds_have_a_per_key_transport(string? kind, string? transport) =>
        RogPerKeyLighting.TransportFor(kind).Should().Be(transport);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_routes_carry_every_distinct_key_colour_through_the_existing_connection(bool rf)
    {
        using var io = new HeaderEchoFake(rf); using var connection = Open(io, rf);
        var keys = Keys(81);

        await RogPerKeyLighting.WriteAsync(connection.ExchangeAsync, keys, default);

        io.Written.Should().HaveCount(6);
        io.Written.Should().OnlyContain(w => w.Length == (rf ? 64 : 65) && w[0] == (rf ? 2 : 0) && w[1] == 0xC0 && w[2] == 0x81);
        if (rf) io.Written.Select(w => Convert.ToHexString(w)).Should().Equal(RogProtocol.KeyboardFrame(2, keys).Select(Convert.ToHexString));
        var decoded = io.Written.SelectMany(w => Enumerable.Range(0, w[3]).Select(k => new RogKeyColor(w[5 + k * 4], new(w[6 + k * 4], w[7 + k * 4], w[8 + k * 4]))));
        decoded.Should().Equal(keys);
        keys.Select(k => k.Color).Distinct().Should().HaveCount(81);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_report_ends_the_frame_without_retry_and_poisons_the_owner(bool rf)
    {
        using var io = new HeaderEchoFake(rf) { RefuseAt = 1 }; using var connection = Open(io, rf);

        await ((Func<Task>)(() => RogPerKeyLighting.WriteAsync(connection.ExchangeAsync, Keys(40), default)))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*connection poisoned; no retry*");

        io.Written.Should().HaveCount(2, "the third report is never sent and the refused one is not repeated");
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<Exception>().WithMessage("*poisoned*");
        io.Written.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_reply_that_does_not_echo_the_header_is_refused()
    {
        var keys = Keys(3);
        var bad = new byte[65]; bad[1] = 0xC0; bad[2] = 0x81; bad[3] = 2;
        await ((Func<Task>)(() => RogPerKeyLighting.WriteAsync((_, _, _) => Task.FromResult(bad), keys, default)))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(false, "count")]
    [InlineData(true, "count")]
    [InlineData(false, "tail")]
    [InlineData(true, "tail")]
    [InlineData(false, "echo81")]
    [InlineData(true, "echo81")]
    public async Task Only_the_stock_C082_count_and_zero_tail_are_accepted_for_a_pending_C081(bool rf, string defect)
    {
        using var io = new HeaderEchoFake(rf) { ReplyDefect = defect }; using var connection = Open(io, rf);
        await ((Func<Task>)(() => RogPerKeyLighting.WriteAsync(connection.ExchangeAsync, Keys(1), default)))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*connection poisoned; no retry*");
        io.Written.Should().HaveCount(1);
    }

    [Fact]
    public async Task Key_frames_are_never_queued_for_an_owner_that_does_not_service_that_transport()
    {
        var bridge = new RogWiredLightingBridge();
        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(4), RogPerKeyLighting.WiredTransport, default))).Should().ThrowAsync<InvalidOperationException>();

        var owner = bridge.Attach();
        bridge.KeyFrameTransport.Should().BeNull();
        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(4), RogPerKeyLighting.WiredTransport, default))).Should().ThrowAsync<InvalidOperationException>();
        owner.HasWork.Should().BeFalse("an actor that does not take key frames must never be woken by one");

        owner.AcceptKeyFrames(RogPerKeyLighting.WirelessTransport);
        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(4), RogPerKeyLighting.WiredTransport, default))).Should().ThrowAsync<InvalidOperationException>();
        owner.HasWork.Should().BeFalse();
        ((Action)(() => owner.AcceptKeyFrames(RogPerKeyLighting.WiredTransport))).Should().Throw<InvalidOperationException>();
        owner.AcceptKeyFrames(RogPerKeyLighting.WirelessTransport);

        var duplicate = Keys(4); duplicate[1] = duplicate[0];
        await ((Func<Task>)(() => bridge.WriteKeysAsync(duplicate, RogPerKeyLighting.WirelessTransport, default))).Should().ThrowAsync<ArgumentException>();
        owner.HasWork.Should().BeFalse();
    }

    [Fact]
    public async Task A_pending_key_frame_is_taken_only_as_a_key_frame_and_completes_its_writer()
    {
        var bridge = new RogWiredLightingBridge();
        var owner = bridge.Attach();
        owner.AcceptKeyFrames(RogPerKeyLighting.WiredTransport);
        bridge.KeyFrameTransport.Should().Be(RogPerKeyLighting.WiredTransport);
        var keys = Keys(16);

        var write = bridge.WriteKeysAsync(keys, RogPerKeyLighting.WiredTransport, default);
        owner.HasWork.Should().BeTrue();
        owner.TryTake(out var colour).Should().BeFalse(); colour.Should().BeNull();
        owner.HasWork.Should().BeTrue("a colour-only actor leaves the key frame where it is");
        owner.TryTakeKeys(out var frame).Should().BeTrue();
        frame!.Keys.Should().Equal(keys);
        frame.Transport.Should().Be(RogPerKeyLighting.WiredTransport);
        owner.HasWork.Should().BeFalse();
        frame.Completion.TrySetResult();
        await write;

        var colourWrite = bridge.WriteAsync(new(1, 2, 3), default);
        owner.TryTakeKeys(out _).Should().BeFalse();
        owner.TryTake(out colour).Should().BeTrue();
        colour!.Colour.Should().Be(new Rgb(1, 2, 3));
        colour.Completion.TrySetResult();
        await colourWrite;
    }

    [Fact]
    public async Task Detaching_fails_a_pending_key_frame()
    {
        var bridge = new RogWiredLightingBridge();
        var owner = bridge.Attach(); owner.AcceptKeyFrames(RogPerKeyLighting.WirelessTransport);
        var write = bridge.WriteKeysAsync(Keys(2), RogPerKeyLighting.WirelessTransport, default);
        bridge.Detach(owner);
        await ((Func<Task>)(() => write)).Should().ThrowAsync<IOException>();
        bridge.KeyFrameTransport.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_actor_turn_sends_a_queued_key_frame_on_its_own_connection(bool rf)
    {
        // The servicing the session delta adds: take the key frame, write it on the owner's exchange, complete the writer.
        using var io = new HeaderEchoFake(rf); using var connection = Open(io, rf);
        var bridge = new RogWiredLightingBridge();
        var owner = bridge.Attach();
        owner.AcceptKeyFrames(RogPerKeyLighting.TransportFor(connection.ConnectionKind)!);
        var keys = Keys(30);
        var write = bridge.WriteKeysAsync(keys, rf ? RogPerKeyLighting.WirelessTransport : RogPerKeyLighting.WiredTransport, default);

        owner.TryTakeKeys(out var work).Should().BeTrue();
        await RogPerKeyLighting.WriteAsync(connection.ExchangeAsync, work!.Keys, default);
        work.Completion.TrySetResult();
        await write;
        io.Written.Should().HaveCount(3);
    }

    [Fact]
    public void The_reference_candidate_is_a_valid_unverified_scan_seed_and_never_ready()
    {
        var wired = RogAzothCandidateMap.For(RogPerKeyLighting.WiredTransport);
        var rf = RogAzothCandidateMap.For(RogPerKeyLighting.WirelessTransport);

        wired.Map.Keys.Should().HaveCount(81);
        PerKeyMaps.Validate(wired.Map).Should().BeNull();
        wired.Map.Complete.Should().BeFalse(); wired.Map.Verified.Should().BeFalse(); wired.Map.Evidence.Should().BeNull();
        wired.Provenance.Should().Contain("37f8ebdd2a3ccb5b62b0765a5627cbdc116c5bee21e440fe547776650894990f").And.Contain("not a physical scan");
        rf.Map.Keys.Should().Equal(wired.Map.Keys);
        wired.Map.Keys.Take(3).Select(k => (k.LedId, k.Key)).Should().Equal((0x00, "Esc"), (0x08, "F1"), (0x10, "F2"));
        wired.Map.Keys[^1].Should().Be(new PerKeyCell(0x7D, "→", 5, 124));
        wired.Map.Keys.Single(k => k.Key == "Space").Column.Should().Be(55, "6.25-key space centred 6.875 keys from the left edge");
        PerKeyMaps.Evaluate(true, wired.Map, RogPerKeyLighting.WiredTransport, true, true).State.Should().Be(PerKeyState.Unverified);
    }

    private static RogPerKeyMapOptions MapOptions(string transport, bool verified = true, bool keyOrder = true, int skip = 0) => new()
    {
        Model = "Azoth X local scan",
        Transport = transport,
        Complete = verified,
        Verified = verified,
        Evidence = verified ? "synthetic scan id" : null,
        KeyOrderVerified = keyOrder,
        Keys = RogAzothCandidateMap.For(transport).Map.Keys.Skip(skip)
            .Select(k => new RogPerKeyCellOptions { LedId = k.LedId, Key = k.Key, Row = k.Row, Column = k.Column }).ToArray(),
    };

    private static readonly string[] Both = [RogPerKeyLighting.WiredTransport, RogPerKeyLighting.WirelessTransport];

    [Fact]
    public void Nothing_configured_has_no_plan_and_each_gap_names_its_state()
    {
        RogPerKeyPlanner.Plan(null, Both).Should().BeNull();
        RogPerKeyPlanner.Plan(new(), Both).Should().BeNull();

        State(new() { Enabled = true }, Both).Should().Be(PerKeyState.MissingMap);
        State(new() { Maps = [MapOptions(Both[0])] }, [Both[0]]).Should().Be(PerKeyState.Disabled);
        State(new() { Enabled = true, Maps = [MapOptions(Both[0], verified: false)] }, [Both[0]]).Should().Be(PerKeyState.Unverified);
        State(new() { Enabled = true, Maps = [MapOptions(Both[0], keyOrder: false)] }, [Both[0]]).Should().Be(PerKeyState.KeyOrderUnverified);
        RogPerKeyPlanner.Plan(new() { Enabled = true, Maps = [MapOptions(Both[0])] }, Both, Both[1])!.Capability.State
            .Should().Be(PerKeyState.TransportMismatch, "the active 2.4 GHz route has no map");
        State(new() { Enabled = true, Maps = [MapOptions(Both[0]), MapOptions(Both[0])] }, [Both[0]]).Should().Be(PerKeyState.InvalidMap);
        State(new() { Enabled = true, Maps = [MapOptions(Both[0])] }, []).Should().Be(PerKeyState.Unsupported);
        RogPerKeyPlanner.Plan(new() { Enabled = true, Maps = [MapOptions(Both[0], keyOrder: false)] }, [Both[0]])!.Map.Should().BeNull();
    }

    [Fact]
    public void Verified_routes_are_independent_and_the_active_route_can_have_its_own_led_ids_and_order()
    {
        var plan = RogPerKeyPlanner.Plan(new() { Enabled = true, Maps = [MapOptions(Both[1]), MapOptions(Both[0])] }, Both)!;

        plan.Ready.Should().BeTrue();
        plan.Capability.KeyCount.Should().Be(81);
        plan.Capability.Transport.Should().Be("rog-usb65-c081-v1+rog-rf64-c081-v1");
        plan.Transports.Should().Equal(Both);
        plan.Map!.Keys.Should().Equal(RogAzothCandidateMap.For(Both[0]).Map.Keys);

        var wiredOnly = RogPerKeyPlanner.Plan(new() { Enabled = true, Maps = [MapOptions(Both[0])] }, Both)!;
        wiredOnly.Ready.Should().BeTrue();
        wiredOnly.Capability.Transport.Should().Be(RogPerKeyLighting.WiredTransport);
        wiredOnly.Transports.Should().Equal(Both[0]);

        var rfMap = MapOptions(Both[1]);
        rfMap = rfMap with { Keys = rfMap.Keys.Reverse().Select(k => k with { LedId = 255 - k.LedId }).ToArray() };
        var independent = RogPerKeyPlanner.Plan(new() { Enabled = true, Maps = [MapOptions(Both[0]), rfMap] }, Both, Both[1])!;
        independent.Ready.Should().BeTrue();
        independent.Map!.Keys.Should().Equal(rfMap.ToMap().Keys);
        independent.RouteMaps[Both[0]].Keys.Should().Equal(MapOptions(Both[0]).ToMap().Keys);
    }

    private static PerKeyState State(RogPerKeyOptions options, string[] routes) => RogPerKeyPlanner.Plan(options, routes)!.Capability.State;

    /// <summary>Replies C0/82 to C0/81 (RID0/65 wired, RID2/64 RF); the zero-based write <see cref="RefuseAt"/> gets FF AA instead.</summary>
    private sealed class HeaderEchoFake(bool rf) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Written { get; } = [];
        public int RefuseAt { get; init; } = -1;
        public string? ReplyDefect { get; init; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var p = report.ToArray();
            Written.Add(p);
            var reply = new byte[rf ? 64 : 65];
            if (Written.Count - 1 == RefuseAt) { reply[1] = 0xFF; reply[2] = 0xAA; }
            else if (p[1] == 0x12) { reply[1] = 0x12; }
            else
            {
                p.AsSpan(1, 4).CopyTo(reply.AsSpan(1));
                if (p[1] == 0xC0 && p[2] == 0x81)
                {
                    reply[2] = 0x82;
                    reply.AsSpan(5, 23).Fill(0xA5); // The firmware's opaque bytes are not required to be zero.
                    if (ReplyDefect == "count") reply[3]++;
                    if (ReplyDefect == "tail") reply[28] = 1;
                    if (ReplyDefect == "echo81") reply[2] = 0x81;
                }
            }
            reply[0] = rf ? (byte)2 : (byte)0;
            _replies.Writer.TryWrite(reply);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() => _replies.Writer.TryComplete();
    }
}
