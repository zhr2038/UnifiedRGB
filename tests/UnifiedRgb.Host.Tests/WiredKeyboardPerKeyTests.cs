using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fake-only per-key path of the shared keyboard lighting owner: default-off aggregate shape, unverified candidate status,
/// Ready-only key records, wired versus 2.4 GHz route selection by the owner, distinct key colours and the App review layout.
/// The "verified" maps here are synthetic configuration, not a physical scan; nothing proves optical output or restoration.
/// </summary>
public sealed class WiredKeyboardPerKeyTests
{
    private const string Wired = RogPerKeyLighting.WiredTransport;
    private const string Wireless = RogPerKeyLighting.WirelessTransport;
    private static KeyboardDisplayOptions WiredRoute => new() { ControlVerified = true, LightingVerified = true, DeviceKey = new string('A', 64), FirmwareHeader = "001200000024000100" };
    private static KeyboardDisplayOptions WirelessRoute => new() { WirelessControlVerified = true, WirelessLightingVerified = true,
        WirelessReceiverKey = new string('B', 64), WirelessFirmwareHeader = "0800070024000100" };
    private static KeyboardDisplayOptions BothRoutes => WiredRoute with { WirelessControlVerified = true, WirelessLightingVerified = true,
        WirelessReceiverKey = new string('B', 64), WirelessFirmwareHeader = "0800070024000100" };

    private static RogPerKeyMapOptions Map(string transport, bool verified = true) => new()
    {
        Model = "synthetic configuration",
        Transport = transport,
        Complete = verified,
        Verified = verified,
        Evidence = verified ? "synthetic, not a physical scan" : null,
        KeyOrderVerified = verified,
        Keys = RogAzothCandidateMap.For(transport).Map.Keys
            .Select(k => new RogPerKeyCellOptions { LedId = k.LedId, Key = k.Key, Row = k.Row, Column = k.Column }).ToArray(),
    };

    private static Rgb[] Distinct(int count) => Enumerable.Range(0, count).Select(i => new Rgb((byte)(i * 3), (byte)(250 - i), (byte)(i % 7 * 30))).ToArray();

    [Fact]
    public async Task Without_per_key_configuration_the_keyboard_is_exactly_the_aggregate_zone()
    {
        var bridge = new RogWiredLightingBridge();
        using var legacy = new WiredKeyboardAdapter(bridge, WiredRoute);
        using var empty = new WiredKeyboardAdapter(new(), WiredRoute, new RogPerKeyOptions());
        var expected = AggregateZone.Descriptor("Keyboard");
        foreach (var adapter in new[] { legacy, empty })
        {
            var d = (await adapter.DiscoverAsync(default)).Single();
            d.Name.Should().Be("ROG 夜魔 X（整区）");
            d.Zones.Should().ContainSingle().Which.Should().Be(expected);
            adapter.ReadPerKeyFrame(d.Id).Should().BeNull();
        }

        var id = (await legacy.DiscoverAsync(default)).Single().Id;
        var owner = bridge.Attach(); owner.AcceptKeyFrames(Wired);
        await legacy.OpenAsync(id, default);
        var write = Task.Run(() => legacy.WriteFrame(id, [[new Rgb(9, 8, 7)]]));
        SpinWait.SpinUntil(() => owner.HasWork, TimeSpan.FromSeconds(5)).Should().BeTrue();
        owner.TryTakeKeys(out _).Should().BeFalse("the aggregate zone only ever sends one whole-keyboard colour");
        owner.TryTake(out var colour).Should().BeTrue();
        colour!.Colour.Should().Be(new Rgb(9, 8, 7));
        colour.Completion.TrySetResult();
        (await write).Should().BeTrue();
    }

    [Fact]
    public async Task An_unverified_candidate_only_reports_its_state_on_the_unchanged_aggregate_zone()
    {
        var bridge = new RogWiredLightingBridge();
        using var adapter = new WiredKeyboardAdapter(bridge, WiredRoute, new() { Enabled = true, Maps = [Map(Wired, verified: false)] });
        var d = (await adapter.DiscoverAsync(default)).Single();
        var zone = d.Zones.Single();

        d.Name.Should().Be("ROG 夜魔 X（整区）");
        zone.Layout.Should().Be(ZoneLayout.Point);
        zone.Mapped.Should().BeFalse();
        zone.PerKeyPoints.Should().BeNull();
        zone.PerKey!.State.Should().Be(PerKeyState.Unverified);
        var dto = DtoMapper.ToDto(zone);
        PerKeyAdmission.IsAggregate(dto).Should().BeTrue();
        PerKeyAdmission.Evaluate(dto).AllowsOutput.Should().BeTrue("aggregate colour is never per-key output");
        PerKeyDisplay.Zone(dto).Should().Be("逐键不可用 · 待物理逐键扫描");

        var owner = bridge.Attach(); owner.AcceptKeyFrames(Wired);
        await adapter.OpenAsync(d.Id, default);
        ((Action)(() => adapter.WriteFrame(d.Id, [Distinct(81)]))).Should().Throw<ArgumentException>();
        owner.HasWork.Should().BeFalse();
    }

    [Fact]
    public async Task A_ready_wired_map_sends_every_distinct_key_colour_only_through_an_owner_on_that_route()
    {
        var bridge = new RogWiredLightingBridge();
        using var adapter = new WiredKeyboardAdapter(bridge, WiredRoute, new() { Enabled = true, Maps = [Map(Wired)] });
        var d = (await adapter.DiscoverAsync(default)).Single();
        var zone = d.Zones.Single();
        var cells = RogAzothCandidateMap.For(Wired).Map.Keys;

        d.Name.Should().Be("ROG 夜魔 X（逐键）");
        zone.Layout.Should().Be(ZoneLayout.Linear(81));
        zone.Mapped.Should().BeTrue();
        zone.PerKey!.State.Should().Be(PerKeyState.Ready);
        zone.PerKey.Transport.Should().Be(Wired);
        zone.PerKeyPoints.Should().HaveCount(81);
        var dto = DtoMapper.ToDto(zone);
        PerKeyAdmission.IsPerKeyTarget(dto).Should().BeTrue();
        PerKeyAdmission.Evaluate(dto).Verdict.Should().Be(PerKeyAdmissionVerdict.RestorationUnproven, "the App still refuses per-key output until restoration is proven");

        var owner = bridge.Attach();
        await adapter.OpenAsync(d.Id, default);
        var colours = Distinct(81);
        ((Action)(() => adapter.WriteFrame(d.Id, [colours]))).Should().Throw<DeviceBlockedException>("this owner's actor does not service key frames");
        owner.AcceptKeyFrames(Wired);
        ((Action)(() => adapter.WriteFrame(d.Id, [colours.Take(80).ToArray()]))).Should().Throw<ArgumentException>();
        ((Action)(() => adapter.WriteFrame(d.Id, [[colours[0]]]))).Should().Throw<ArgumentException>("a per-key zone never falls back to one colour");
        owner.HasWork.Should().BeFalse();

        var write = Task.Run(() => adapter.WriteFrame(d.Id, [colours]));
        SpinWait.SpinUntil(() => owner.HasWork, TimeSpan.FromSeconds(5)).Should().BeTrue();
        owner.TryTake(out _).Should().BeFalse();
        owner.TryTakeKeys(out var work).Should().BeTrue();
        work!.Transport.Should().Be(Wired);
        work.Keys.Should().Equal(cells.Select((c, i) => new RogKeyColor((byte)c.LedId, colours[i])));
        work.Keys.Select(k => k.Color).Distinct().Should().HaveCount(81);
        adapter.ReadPerKeyFrame(d.Id).Should().BeNull("nothing is reported before the owner completed the frame");
        work.Completion.TrySetResult();
        (await write).Should().BeTrue();

        var frame = adapter.ReadPerKeyFrame(d.Id)!;
        frame.Index.Should().Be(1);
        frame.Keys.Select(k => (k.LedId, k.Key, k.Color)).Should().Equal(cells.Select((c, i) => ((byte)c.LedId, c.Key, colours[i])));
    }

    [Fact]
    public async Task The_owner_route_selects_wired_or_2_4_ghz_and_a_mismatched_owner_is_blocked()
    {
        var bridge = new RogWiredLightingBridge();
        using var rf = new WiredKeyboardAdapter(bridge, WirelessRoute, new() { Enabled = true, Maps = [Map(Wireless)] });
        var d = (await rf.DiscoverAsync(default)).Single();
        d.Zones.Single().PerKey!.Transport.Should().Be(Wireless);

        var wrong = bridge.Attach(); wrong.AcceptKeyFrames(Wired);
        await rf.OpenAsync(d.Id, default);
        ((Action)(() => rf.WriteFrame(d.Id, [Distinct(81)]))).Should().Throw<DeviceBlockedException>();
        wrong.HasWork.Should().BeFalse();
        bridge.Detach(wrong);

        var owner = bridge.Attach(); owner.AcceptKeyFrames(Wireless);
        (await Serve(rf, d.Id, owner)).Should().Be(Wireless);

        // Both routes verified: one shared zone; whichever route the owner holds carries the frame.
        var both = new RogWiredLightingBridge();
        using var adapter = new WiredKeyboardAdapter(both, BothRoutes, new() { Enabled = true, Maps = [Map(Wired), Map(Wireless)] });
        var id = (await adapter.DiscoverAsync(default)).Single().Id;
        foreach (var route in new[] { Wired, Wireless })
        {
            var o = both.Attach(); o.AcceptKeyFrames(route);
            await adapter.OpenAsync(id, default);
            (await Serve(adapter, id, o)).Should().Be(route);
            both.Detach(o);
        }

        using var oneMap = new WiredKeyboardAdapter(new(), BothRoutes, new() { Enabled = true, Maps = [Map(Wired)] });
        var usable = (await oneMap.DiscoverAsync(default)).Single().Zones.Single();
        usable.PerKey!.State.Should().Be(PerKeyState.Ready, "USB can ship independently of the unscanned 2.4 GHz route");
        usable.PerKey.Transport.Should().Be(Wired);
        usable.Mapped.Should().BeTrue();
    }

    [Fact]
    public async Task Active_route_uses_its_own_led_ids_and_record_order_with_the_same_physical_key_colours()
    {
        var bridge = new RogWiredLightingBridge();
        var rfMap = Map(Wireless);
        rfMap = rfMap with { Keys = rfMap.Keys.Reverse().Select(k => k with { LedId = 255 - k.LedId }).ToArray() };
        using var adapter = new WiredKeyboardAdapter(bridge, BothRoutes, new() { Enabled = true, Maps = [Map(Wired), rfMap] });
        var id = (await adapter.DiscoverAsync(default)).Single().Id;
        var owner = bridge.Attach(); owner.AcceptKeyFrames(Wireless);
        await adapter.OpenAsync(id, default);
        var colours = Distinct(81);
        var write = Task.Run(() => adapter.WriteFrame(id, [colours]));
        SpinWait.SpinUntil(() => owner.HasWork, TimeSpan.FromSeconds(5)).Should().BeTrue();
        owner.TryTakeKeys(out var work).Should().BeTrue();
        work!.Transport.Should().Be(Wireless);
        work.Keys.Should().Equal(rfMap.Keys.Select((key, index) => new RogKeyColor((byte)key.LedId, colours[80 - index])));
        work.Completion.TrySetResult();
        (await write).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_raw_led_marker_uses_the_same_owner_and_release_restores_the_captured_normal_frame(bool mapped)
    {
        var bridge = new RogWiredLightingBridge();
        using var adapter = new WiredKeyboardAdapter(bridge, WiredRoute, mapped ? new() { Enabled = true, Maps = [Map(Wired)] } : null);
        var descriptor = (await adapter.DiscoverAsync(default)).Single();
        var owner = bridge.Attach(); owner.AcceptKeyFrames(Wired);
        await adapter.OpenAsync(descriptor.Id, default);
        await ((Func<Task>)(() => adapter.OpenForBringupAsync(descriptor.Id, default))).Should().ThrowAsync<DeviceBlockedException>();
        var colours = mapped ? Distinct(81) : [new Rgb(3, 7, 11)];
        var normal = Task.Run(() => adapter.WriteFrame(descriptor.Id, [colours]));
        SpinWait.SpinUntil(() => owner.HasWork, TimeSpan.FromSeconds(5)).Should().BeTrue();
        if (mapped) { owner.TryTakeKeys(out var frame).Should().BeTrue(); frame!.Completion.TrySetResult(); }
        else { owner.TryTake(out var colour).Should().BeTrue(); colour!.Completion.TrySetResult(); }
        (await normal).Should().BeTrue();

        await adapter.OpenForBringupAsync(descriptor.Id, default);
        var status = await adapter.RunProbeAsync(descriptor.Id, "per-key-status", default);
        status.Fields["physicalMap"].Should().Be("unverified");
        owner.HasWork.Should().BeFalse("opening and status do not send any wire request");
        await ((Func<Task>)(() => adapter.BeginMarkerAsync(descriptor.Id, new(128, 1, Rgb.Black, 1), default))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => adapter.BeginMarkerAsync(descriptor.Id, new(0, 2, Rgb.Black, 1), default))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => adapter.BeginMarkerAsync(descriptor.Id, new(0, 1, Rgb.Black, 0), default))).Should().ThrowAsync<ArgumentException>();
        owner.HasWork.Should().BeFalse();

        var marker = adapter.BeginMarkerAsync(descriptor.Id, new(17, 1, new(9, 8, 7), WiredKeyboardAdapter.PerKeyLedMarkerChannel), default);
        owner.TryTakeKeys(out var raw).Should().BeTrue();
        raw!.Transport.Should().Be(Wired);
        raw.Keys.Should().Equal(new RogKeyColor(17, new(9, 8, 7)));
        raw.Completion.TrySetResult(); (await marker).Ok.Should().BeTrue();

        var release = adapter.ReleaseAsync(descriptor.Id, default);
        if (mapped)
        {
            owner.TryTakeKeys(out var restored).Should().BeTrue();
            restored!.Keys.Select(k => k.Color).Should().Equal(colours);
            restored.Completion.TrySetResult();
        }
        else
        {
            owner.TryTake(out var restored).Should().BeTrue();
            restored!.Colour.Should().Be(colours[0]);
            restored.Completion.TrySetResult();
        }
        (await release).Ok.Should().BeTrue();
        (await adapter.ReleaseAsync(descriptor.Id, default)).Ok.Should().BeTrue();
        owner.HasWork.Should().BeFalse("release is idempotent");
    }

    [Fact]
    public async Task Bound_configuration_reaches_the_adapter_through_its_factory()
    {
        var keys = string.Join(",", RogAzothCandidateMap.For(Wired).Map.Keys
            .Select(k => $$"""{ "LedId": {{k.LedId}}, "Key": {{System.Text.Json.JsonSerializer.Serialize(k.Key)}}, "Row": {{k.Row}}, "Column": {{k.Column}} }"""));
        var json = $$"""
            { "UnifiedRgb": { "KeyboardDisplay": { "ControlVerified": true, "LightingVerified": true, "DeviceKey": "{{new string('A', 64)}}" },
              "AsusRog": { "KeyboardPerKey": { "Enabled": true, "Maps": [ { "Model": "m", "Transport": "{{Wired}}", "Complete": true,
                "Verified": true, "Evidence": "synthetic", "KeyOrderVerified": true, "Keys": [ {{keys}} ] } ] } } } }
            """;
        var options = new UnifiedRgbOptions();
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(UnifiedRgbOptions.Section).Bind(options);
        options.AsusRog.KeyboardPerKey.Maps.Single().ToMap().Keys.Should().Equal(RogAzothCandidateMap.For(Wired).Map.Keys);
        new UnifiedRgbOptions().AsusRog.KeyboardPerKey.Configured.Should().BeFalse("per-key is off unless configured");

        using var services = new ServiceCollection().AddSingleton(options).AddSingleton<RogWiredLightingBridge>().BuildServiceProvider();
        using var adapter = (WiredKeyboardAdapter)new WiredKeyboardAdapterFactory().Create(services);
        (await adapter.DiscoverAsync(default)).Single().Zones.Single().PerKey!.State.Should().Be(PerKeyState.Ready);
    }

    [Fact]
    public void The_review_lays_out_each_key_by_row_and_column_with_its_own_colour()
    {
        var cells = RogAzothCandidateMap.For(Wired).Map.Keys;
        var pixels = cells.Select((c, i) => new PerKeyPixelDto((byte)c.LedId, c.Key, c.Row, c.Column, $"#{i * 3:X2}{250 - i:X2}10")).ToArray();
        var review = new PerKeyFrameReview(new(7, pixels));

        review.Keys.Select(k => (k.LedId, k.Key, k.Color)).Should().Equal(pixels.Select(p => (p.LedId, p.Key, p.Color)));
        review.Summary.Should().Be("帧 #7 · 81 键 · 81 种颜色");
        review.Width.Should().BeLessThanOrEqualTo(PerKeyFrameReview.DefaultWidth + 1);
        foreach (var row in review.Keys.GroupBy(k => k.Row))
        {
            var ordered = row.OrderBy(k => k.Column).ToArray();
            ordered.Zip(ordered.Skip(1)).Should().OnlyContain(p => p.First.Left + p.First.Size <= p.Second.Left, "neighbouring keys never overlap");
        }
        review.Keys.Where(k => k.Row == 1).Should().OnlyContain(k => k.Top > review.Keys.First(f => f.Row == 0).Top);

        var item = new ControllerItem(new("asusr:wired:x", "ASUS ROG", "kb", "AsusRog", "Keyboard", "", [], 10, ControllerStates.Active, null, true,
            new PerKeyFrameDto(7, pixels)));
        item.PerKeyReview!.Keys.Should().HaveCount(81);
        (item with { Dto = item.Dto with { PerKeyFrame = null } }).PerKeyReview.Should().BeNull();
        new PerKeyFrameReview(new(1, [])).Keys.Should().BeEmpty();
    }

    private static async Task<string> Serve(WiredKeyboardAdapter adapter, ControllerId id, RogWiredLightingBridge.Owner owner)
    {
        var write = Task.Run(() => adapter.WriteFrame(id, [Distinct(81)]));
        SpinWait.SpinUntil(() => owner.HasWork, TimeSpan.FromSeconds(5)).Should().BeTrue();
        owner.TryTakeKeys(out var work).Should().BeTrue();
        work!.Completion.TrySetResult();
        (await write).Should().BeTrue();
        return work.Transport;
    }
}
