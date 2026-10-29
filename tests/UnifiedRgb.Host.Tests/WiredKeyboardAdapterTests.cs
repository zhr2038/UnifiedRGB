using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class WiredKeyboardAdapterTests
{
    private static KeyboardDisplayOptions Options => new() { ControlVerified = true, LightingVerified = true, DeviceKey = new string('A', 64), FirmwareHeader = "001200000024000100" };

    [Fact]
    public void Unverified_lighting_cannot_construct_an_adapter() =>
        ((Action)(() => { using var adapter = new WiredKeyboardAdapter(new(), Options with { LightingVerified = false }); })).Should().Throw<InvalidOperationException>();

    [Fact]
    public void Wireless_control_and_wired_lighting_permissions_cannot_combine_into_a_verified_route()
    {
        var options = Options with { ControlVerified = false, WirelessControlVerified = true,
            WirelessReceiverKey = new string('B', 64), WirelessFirmwareHeader = "0800070024000100" };
        ((Action)(() => { using var adapter = new WiredKeyboardAdapter(new(), options); })).Should().Throw<InvalidOperationException>();
        using var verified = new WiredKeyboardAdapter(new(), options with { WirelessLightingVerified = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_wireless_lighting_uses_its_verified_identity_instead_of_a_stale_wired_key(bool badHeader)
    {
        var options = Options with { ControlVerified = false, LightingVerified = false, WirelessControlVerified = true,
            WirelessLightingVerified = true, WirelessReceiverKey = new string('B', 64), WirelessFirmwareHeader = badHeader ? "" : "0800070024000100" };
        if (badHeader) ((Action)(() => { using var adapter = new WiredKeyboardAdapter(new(), options); })).Should().Throw<InvalidOperationException>();
        else
        {
            using var adapter = new WiredKeyboardAdapter(new(), options);
            (await adapter.DiscoverAsync(default)).Single().Id.Value.Should().Be("asusr:wired:" + new string('B', 64));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composition_registers_only_a_complete_transport_permission_and_blocks_a_duplicate_legacy_writer(bool verified)
    {
        var display = Options with { ControlVerified = false, LightingVerified = true, WirelessControlVerified = true,
            WirelessReceiverKey = new string('B', 64), WirelessFirmwareHeader = "0800070024000100", WirelessLightingVerified = verified };
        var services = new ServiceCollection().AddUnifiedRgb(new() { KeyboardDisplay = display }, enableIpc: false);
        services.Count(d => d.ServiceType == typeof(IDeviceAdapterFactory) && d.ImplementationType == typeof(WiredKeyboardAdapterFactory)).Should().Be(verified ? 1 : 0);
        if (verified)
        {
            var options = new UnifiedRgbOptions { KeyboardDisplay = display, AsusRog = new() { Keyboards = [new()] } };
            ((Action)(() => new ServiceCollection().AddUnifiedRgb(options, enableIpc: false))).Should().Throw<InvalidOperationException>()
                .WithMessage("*duplicate keyboard route*");
        }
    }

    [Fact]
    public async Task One_aggregate_zone_uses_existing_native_takeover_gate_and_actual_actor_completion()
    {
        var bridge = new RogWiredLightingBridge(); using var adapter = new WiredKeyboardAdapter(bridge, Options);
        adapter.Should().BeAssignableTo<INativeBringup>();
        var descriptor = (await adapter.DiscoverAsync(default)).Single();
        descriptor.TotalLeds.Should().Be(1); descriptor.Capabilities.MaxFps.Should().Be(10);
        await ((Func<Task>)(() => adapter.OpenAsync(descriptor.Id, default))).Should().ThrowAsync<DeviceBlockedException>();
        var events = new List<HotplugKind>(); adapter.Hotplug += e => events.Add(e.Kind);
        var owner = bridge.Attach(); await adapter.OpenAsync(descriptor.Id, default);
        var pending = Task.Run(() => adapter.WriteFrame(descriptor.Id, new[] { new[] { new Rgb(10, 20, 30) } }));
        using var bound = new CancellationTokenSource(2000);
        while (!owner.HasWork) await Task.Delay(5, bound.Token);
        pending.IsCompleted.Should().BeFalse();
        owner.TryTake(out var request).Should().BeTrue(); request!.Colour.Should().Be(new Rgb(10, 20, 30));
        request.Completion.TrySetResult(); (await pending).Should().BeTrue();
        bridge.Detach(owner); events.Should().Equal(HotplugKind.Arrived, HotplugKind.Removed);
    }
}
