using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

public class PipeAclTests
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier AuthUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Service = new(WellKnownSidType.ServiceSid, null);
    private static readonly SecurityIdentifier Batch = new(WellKnownSidType.BatchSid, null);
    private static readonly SecurityIdentifier Anonymous = new(WellKnownSidType.AnonymousSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier SomeUser = new("S-1-5-21-1-2-3-1001");

    private static readonly PipeSecurity Acl = PipeAcl.Create(new SecurityIdentifier("S-1-5-21-9-9-9-500"));

    [Fact]
    public void Network_logon_is_denied_even_for_an_interactive_admin_member()
    {
        PipeAcl.EffectiveRights(Acl, [SomeUser, Everyone, AuthUsers, Users, PipeAcl.Network, PipeAcl.Interactive]).Should().Be(0);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("batch")]
    [InlineData("anonymous")]
    public void Non_interactive_logons_get_no_access(string kind)
    {
        var logon = kind switch { "service" => Service, "batch" => Batch, _ => Anonymous };
        PipeAcl.EffectiveRights(Acl, [SomeUser, Everyone, AuthUsers, Users, logon]).Should().Be(0);
    }

    [Fact]
    public void Interactive_user_gets_client_rights_only()
    {
        var rights = PipeAcl.EffectiveRights(Acl, [SomeUser, Everyone, AuthUsers, Users, PipeAcl.Interactive]);
        rights.Should().Be(PipeAcl.ClientRights);
        rights.HasFlag(PipeAccessRights.ChangePermissions).Should().BeFalse();
        rights.HasFlag(PipeAccessRights.TakeOwnership).Should().BeFalse();
    }

    [Fact]
    public void Administrators_and_system_have_full_control_and_dacl_is_protected()
    {
        PipeAcl.EffectiveRights(Acl, [PipeAcl.Administrators]).Should().Be(PipeAccessRights.FullControl);
        PipeAcl.EffectiveRights(Acl, [PipeAcl.LocalSystem]).Should().Be(PipeAccessRights.FullControl);
        Acl.AreAccessRulesProtected.Should().BeTrue();
    }
}

public class WriterGuardTests
{
    [Fact]
    public void Virtual_is_always_clear_and_vendor_writer_blocks_its_class_only()
    {
        var guard = new WriterGuard(new ProcessScanner(new FakeSnapshot(processes: ["L-Connect-Service"])));
        guard.Check(DeviceClass.Virtual).State.Should().Be(GuardState.Clear);
        guard.Check(DeviceClass.LianLi).Should().Be(GuardVerdict.Blocked("L-Connect-Service"));
        guard.Check(DeviceClass.MsiGpu).State.Should().Be(GuardState.Clear);
        guard.Check((DeviceClass)42).State.Should().Be(GuardState.Blocked);
    }

    [Fact]
    public void Scan_failure_fails_closed()
    {
        var guard = new WriterGuard(new ProcessScanner(new ThrowingSnapshot()));
        guard.Check(DeviceClass.OpenRgb).State.Should().Be(GuardState.Blocked);
    }

    [Fact]
    public async Task Blocked_controllers_are_never_opened_or_written()
    {
        var adapter = new CountingAdapter(DeviceClass.MsiGpu);
        var guard = new WriterGuard(new ProcessScanner(new FakeSnapshot(processes: ["Mystic_Light_Service"])));
        using var bus = new FrameBus();
        var host = new AdapterHost([new Factory(adapter)], null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await host.StartAsync(CancellationToken.None);

        host.Controllers.Should().ContainSingle();
        host.Controllers[0].IsActive.Should().BeFalse();
        host.Controllers[0].Verdict.Writer.Should().Be("Mystic_Light_Service");
        DtoMapper.ToDto(host.Controllers[0]).State.Should().Be(ControllerStates.Blocked);
        adapter.Opens.Should().Be(0);
        adapter.Writes.Should().Be(0);
        bus.Mailboxes.Should().BeEmpty();
        await host.StopAsync();
    }

    [Fact]
    public async Task Active_discovery_adapter_is_not_even_discovered_while_blocked()
    {
        var adapter = new ActiveAdapter();
        var guard = new WriterGuard(new ProcessScanner(new FakeSnapshot(processes: ["OpenRGB"])));
        using var bus = new FrameBus();
        var host = new AdapterHost([new Factory(adapter)], null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        adapter.Discoveries.Should().Be(0, "discovery would launch OpenRGB and probe SMBus");
        adapter.Opens.Should().Be(0);
        host.Controllers.Should().BeEmpty();
        await host.StopAsync();
        adapter.Disposed.Should().BeTrue("AdapterHost releases adapters (e.g. stops the supervised OpenRGB)");
    }

    private sealed class FakeSnapshot(string[]? processes = null, string[]? services = null) : ISystemSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes() => (processes ?? []).Select((p, i) => new ProcessInfo(1000 + i, p)).ToArray();

        public IReadOnlyList<ServiceInfo> Services() => (services ?? []).Select(s => new ServiceInfo(s, true, "Automatic")).ToArray();
    }

    private sealed class ThrowingSnapshot : ISystemSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes() => throw new InvalidOperationException("access denied");

        public IReadOnlyList<ServiceInfo> Services() => throw new InvalidOperationException("access denied");
    }

    private sealed class Factory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => adapter.Class;

        public IDeviceAdapter Create(IServiceProvider sp) => adapter;
    }

    private sealed class CountingAdapter(DeviceClass cls) : IDeviceAdapter
    {
        private readonly ControllerDescriptor _d = new(new ControllerId("x:1"), "X", "X", DeviceType.Gpu, cls, "1",
            [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)], Capabilities.DirectOnly(20));

        public int Opens { get; private set; }

        public int Writes { get; private set; }

        public string Vendor => "X";

        public DeviceClass Class => cls;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_d]);

        public Task OpenAsync(ControllerId id, CancellationToken ct)
        {
            Opens++;
            return Task.CompletedTask;
        }

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => Writes++;

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }

    private sealed class ActiveAdapter : IDeviceAdapter, IActiveDiscovery, IAsyncDisposable
    {
        public int Discoveries { get; private set; }

        public int Opens { get; private set; }

        public bool Disposed { get; private set; }

        public string Vendor => "OpenRGB";

        public DeviceClass Class => DeviceClass.OpenRgb;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
        {
            Discoveries++;
            return Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);
        }

        public Task OpenAsync(ControllerId id, CancellationToken ct)
        {
            Opens++;
            return Task.CompletedTask;
        }

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
        }

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

public class DtoMapperTests
{
    [Fact]
    public void Colors_round_trip_and_bad_input_is_rejected()
    {
        DtoMapper.ParseColor("#0A0b0C").Should().Be(new Rgb(10, 11, 12));
        DtoMapper.FormatColor(new Rgb(10, 11, 12)).Should().Be("#0A0B0C");
        var bad = () => DtoMapper.ParseColor("#GG0000");
        bad.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-0.1f, 1f)]
    [InlineData(0.5f, 1.5f)]
    [InlineData(float.NaN, 1f)]
    public void Out_of_range_speed_or_brightness_is_rejected(float speed, float brightness)
    {
        var dto = new AssignmentDto([new ZoneRefDto("virtual:gpu", 0)], "Rainbow", [], speed, brightness);
        var act = () => DtoMapper.FromDto(dto);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_targets_rejected()
    {
        var act = () => DtoMapper.FromDto(new AssignmentDto([], "Rainbow", []));
        act.Should().Throw<ArgumentException>();
    }
}
