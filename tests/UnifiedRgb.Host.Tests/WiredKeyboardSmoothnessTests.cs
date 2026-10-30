using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fake-only opt-in offer ceiling and software completion counters of the shared keyboard lighting adapter. The synthetic
/// maps are configuration, not a physical scan; the counters are host software only and never a physical refresh rate.
/// </summary>
public sealed class WiredKeyboardSmoothnessTests
{
    private const string Wired = RogPerKeyLighting.WiredTransport;
    private static KeyboardDisplayOptions WiredRoute => new() { ControlVerified = true, LightingVerified = true, DeviceKey = new string('A', 64), FirmwareHeader = "001200000024000100" };

    private static RogPerKeyMapOptions Map(bool verified = true) => new()
    {
        Model = "synthetic configuration",
        Transport = Wired,
        Complete = verified,
        Verified = verified,
        Evidence = verified ? "synthetic, not a physical scan" : null,
        KeyOrderVerified = verified,
        Keys = RogAzothCandidateMap.For(Wired).Map.Keys
            .Select(k => new RogPerKeyCellOptions { LedId = k.LedId, Key = k.Key, Row = k.Row, Column = k.Column }).ToArray(),
    };

    private static Rgb[] Colours(int n) => Enumerable.Range(0, 81).Select(i => new Rgb((byte)(i * 3), (byte)(250 - i), (byte)(n * 40 + i))).ToArray();

    [Fact]
    public async Task The_offer_ceiling_is_opt_in_and_only_for_a_ready_per_key_map()
    {
        (await OfferCeiling(null)).Should().Be(10);
        (await OfferCeiling(new() { MaximumFps = 30 })).Should().Be(10, "without a per-key map the zone stays aggregate");
        (await OfferCeiling(new() { Enabled = true, Maps = [Map()] })).Should().Be(10, "absent keeps the legacy ceiling");
        (await OfferCeiling(new() { Enabled = true, Maps = [Map(verified: false)], MaximumFps = 30 })).Should().Be(10, "a not-Ready map stays aggregate");
        (await OfferCeiling(new() { Enabled = true, Maps = [Map()], MaximumFps = 30 })).Should().Be(30);
        (await OfferCeiling(new() { Enabled = true, Maps = [Map()], MaximumFps = 1, RfReportDelayMs = 0 })).Should().Be(1);
        foreach (var bad in new RogPerKeyOptions[] { new() { MaximumFps = 0 }, new() { MaximumFps = 31 }, new() { RfReportDelayMs = -1 }, new() { RfReportDelayMs = 9 } })
            ((Action)(() => new WiredKeyboardAdapter(new(), WiredRoute, bad).Dispose())).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task The_status_probe_counts_completed_and_unique_frames_without_suppressing_repeats()
    {
        var bridge = new RogWiredLightingBridge();
        using var adapter = new WiredKeyboardAdapter(bridge, WiredRoute, new() { Enabled = true, Maps = [Map()], MaximumFps = 30, RfReportDelayMs = 4 });
        var id = (await adapter.DiscoverAsync(default)).Single().Id;
        var owner = bridge.Attach(); owner.AcceptKeyFrames(Wired);
        await adapter.OpenAsync(id, default);
        var served = new List<IReadOnlyList<RogKeyColor>>();
        foreach (var colours in new[] { Colours(0), Colours(0), Colours(1) }) served.Add(await Serve(adapter, id, owner, colours));
        served.Should().HaveCount(3, "a repeated frame is still written in order");
        served[1].Should().Equal(served[0]);
        served[2].Should().NotEqual(served[1]);

        var fields = (await adapter.RunProbeAsync(id, "per-key-status", default)).Fields;
        fields["offerCeilingFps"].Should().Be("30");
        fields["configuredRfReportDelayMs"].Should().Be("4");
        fields["completedFrames"].Should().Be("3");
        fields["uniqueFrames"].Should().Be("2");
        double.Parse(fields["lastCompletionMs"], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0);
        double.Parse(fields["meanCompletionMs"], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0);
        fields["counters"].Should().Contain("not optical");
        fields["physicalMap"].Should().Be("unverified");

        // A raw marker and its restoration use the owner directly; they are not normal frames.
        await adapter.OpenForBringupAsync(id, default);
        var marker = adapter.BeginMarkerAsync(id, new(17, 1, new(9, 8, 7), WiredKeyboardAdapter.PerKeyLedMarkerChannel), default);
        owner.TryTakeKeys(out var raw).Should().BeTrue();
        raw!.Completion.TrySetResult(); (await marker).Ok.Should().BeTrue();
        var release = adapter.ReleaseAsync(id, default);
        owner.TryTakeKeys(out var restored).Should().BeTrue();
        restored!.Completion.TrySetResult(); (await release).Ok.Should().BeTrue();
        fields = (await adapter.RunProbeAsync(id, "per-key-status", default)).Fields;
        fields["completedFrames"].Should().Be("3");
        fields["uniqueFrames"].Should().Be("2");
    }

    [Fact]
    public async Task Bound_local_configuration_carries_both_pacing_options_to_the_adapter()
    {
        var keys = string.Join(",", RogAzothCandidateMap.For(Wired).Map.Keys
            .Select(k => $$"""{ "LedId": {{k.LedId}}, "Key": {{System.Text.Json.JsonSerializer.Serialize(k.Key)}}, "Row": {{k.Row}}, "Column": {{k.Column}} }"""));
        var json = $$"""
            { "UnifiedRgb": { "KeyboardDisplay": { "ControlVerified": true, "LightingVerified": true, "DeviceKey": "{{new string('A', 64)}}" },
              "AsusRog": { "KeyboardPerKey": { "Enabled": true, "MaximumFps": 20, "RfReportDelayMs": 0, "Maps": [ { "Model": "m",
                "Transport": "{{Wired}}", "Complete": true, "Verified": true, "Evidence": "synthetic", "KeyOrderVerified": true, "Keys": [ {{keys}} ] } ] } } } }
            """;
        var options = new UnifiedRgbOptions();
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(UnifiedRgbOptions.Section).Bind(options);
        options.AsusRog.KeyboardPerKey.MaximumFps.Should().Be(20);
        options.AsusRog.KeyboardPerKey.RfReportDelayMs.Should().Be(0);
        new UnifiedRgbOptions().AsusRog.KeyboardPerKey.MaximumFps.Should().BeNull("pacing is opt-in");
        new UnifiedRgbOptions().AsusRog.KeyboardPerKey.RfReportDelayMs.Should().BeNull("the 8 ms default stays unless configured");

        using var services = new ServiceCollection().AddSingleton(options).AddSingleton<RogWiredLightingBridge>().BuildServiceProvider();
        using var adapter = (WiredKeyboardAdapter)new WiredKeyboardAdapterFactory().Create(services);
        var descriptor = (await adapter.DiscoverAsync(default)).Single();
        descriptor.Capabilities.MaxFps.Should().Be(20);
        (await adapter.RunProbeAsync(descriptor.Id, "per-key-status", default)).Fields["configuredRfReportDelayMs"].Should().Be("0");
    }

    private static async Task<int> OfferCeiling(RogPerKeyOptions? perKey)
    {
        using var adapter = new WiredKeyboardAdapter(new(), WiredRoute, perKey);
        var descriptor = (await adapter.DiscoverAsync(default)).Single();
        var probe = await adapter.RunProbeAsync(descriptor.Id, "per-key-status", default);
        probe.Fields["offerCeilingFps"].Should().Be(descriptor.Capabilities.MaxFps.ToString(CultureInfo.InvariantCulture));
        return descriptor.Capabilities.MaxFps;
    }

    private static async Task<IReadOnlyList<RogKeyColor>> Serve(WiredKeyboardAdapter adapter, ControllerId id, RogWiredLightingBridge.Owner owner, Rgb[] colours)
    {
        var write = Task.Run(() => adapter.WriteFrame(id, [colours]));
        SpinWait.SpinUntil(() => owner.HasWork, TimeSpan.FromSeconds(5)).Should().BeTrue();
        owner.TryTakeKeys(out var work).Should().BeTrue();
        work!.Completion.TrySetResult();
        (await write).Should().BeTrue();
        return work.Keys;
    }
}
