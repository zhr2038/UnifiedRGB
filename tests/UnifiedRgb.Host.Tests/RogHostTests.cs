using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

public sealed class RogHostTests
{
    [Fact]
    public void Existing_configuration_without_rog_keys_does_not_register_or_construct_a_receiver()
    {
        var options = new UnifiedRgbOptions { UseVirtualAdapter = false };
        options.EnableAsusRog.Should().BeFalse();
        var services = new ServiceCollection().AddUnifiedRgb(options, enableIpc: false);
        services.Where(d => d.ServiceType == typeof(IDeviceAdapterFactory)).Should().BeEmpty();
        options.AsusRog.Keyboards.Should().BeEmpty();
        options.ZoneFollowers.Should().BeEmpty();
    }

    [Fact]
    public async Task Opt_in_adds_one_existing_host_factory_and_no_background_host_or_network_client()
    {
        var options = new UnifiedRgbOptions { UseVirtualAdapter = false, EnableAsusRog = true };
        var services = new ServiceCollection().AddUnifiedRgb(options, enableIpc: false);
        services.Where(d => d.ServiceType == typeof(IDeviceAdapterFactory)).Select(d => d.ImplementationType)
            .Should().Equal(typeof(RogAdapterFactory));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDeviceAdapterFactory>();
        var adapter = factory.Create(provider);
        adapter.Class.Should().Be(DeviceClass.AsusRog);
        await ((IAsyncDisposable)adapter).DisposeAsync();
    }

    [Fact]
    public void Local_binding_configuration_is_explicit_and_never_inferred_from_receiver_pid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UnifiedRgb:EnableAsusRog"] = "true",
            ["UnifiedRgb:AsusRog:Keyboards:0:ReceiverKey"] = new string('A', 64),
            ["UnifiedRgb:AsusRog:Keyboards:0:ReportId"] = "2",
            ["UnifiedRgb:AsusRog:Keyboards:0:LedIds"] = "AQIH", // ConfigurationBinder byte[] is base64.
        }).Build();
        var options = new UnifiedRgbOptions();
        configuration.GetSection(UnifiedRgbOptions.Section).Bind(options);
        var pin = options.AsusRog.Keyboards.Single();
        pin.LedIds.Should().Equal(1, 2, 7);
        pin.ControlVerified.Should().BeFalse();
        pin.KeyOrderVerified.Should().BeFalse();
    }

    [Fact]
    public void Receiver_writer_blockers_do_not_grant_service_or_input_control()
    {
        var writers = KnownWriters.All.Where(w => w.Affects.Contains(DeviceClass.AsusRog)).ToArray();
        writers.Select(w => w.Name).Should().Contain(["OpenRGB", "GHelper"]);
        writers.Should().OnlyContain(w => w.Kind == WriterKind.Process);
        writers.Should().NotContain(w => w.Name.Contains("HID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Local_following_is_separate_from_packages_and_does_not_enable_any_adapter()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UnifiedRgb:ZoneFollowers:0:SourceControllerId"] = "source",
            ["UnifiedRgb:ZoneFollowers:0:SourceZoneId"] = "2",
            ["UnifiedRgb:ZoneFollowers:0:SourceLed"] = "3",
            ["UnifiedRgb:ZoneFollowers:0:TargetControllerId"] = "target",
            ["UnifiedRgb:ZoneFollowers:0:TargetZoneId"] = "0",
        }).Build();
        var options = new UnifiedRgbOptions(); configuration.GetSection(UnifiedRgbOptions.Section).Bind(options);
        var binding = options.ZoneFollowers.Single().ToBinding();
        binding.Source.Controller.Value.Should().Be("source"); binding.Source.Zone.Value.Should().Be(2);
        binding.SourceLed.Should().Be(3); binding.Target.Controller.Value.Should().Be("target");
        options.EnableAsusRog.Should().BeFalse(); options.AsusRog.Mice.Should().BeEmpty(); options.AsusRog.Keyboards.Should().BeEmpty();
    }
}
