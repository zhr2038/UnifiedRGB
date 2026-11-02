using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogPhysicalRoutePresenceTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Same_path_changed_container_or_unavailable_metadata_is_not_physical_removal(bool metadataUnavailable)
    {
        var devices = new DeviceSnapshot(); var resolver = new Resolver();
        var factory = new RogWiredOledFactory(devices, resolver, _ => new IoFailure());
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(Control));
        connection.Should().NotBeNull();
        if (metadataUnavailable) resolver.Unavailable = true;
        else resolver.ContainerId = Guid.NewGuid();
        connection!.SameDevice().Should().BeFalse();
        connection.PhysicalRouteAbsent.Should().BeFalse();
        var failed = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<IOException>();
        failed.Which.Should().NotBeOfType<RogOledConnectionLostException>();
    }
    private const string Path = "synthetic-control-mi_01";
    private static readonly HidCollectionInfo Control = new(Path, 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0);

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Io_failure_with_false_identity_requires_positive_absence_before_loss_classification(bool wireless, bool absent)
    {
        using var io = new IoFailure();
        using IRogOledConnection connection = wireless
            ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => false) { RouteAbsentProbe = () => absent }
            : new RogWiredOledConnection(io, () => false) { RouteAbsentProbe = () => absent };
        connection.SameDevice().Should().BeFalse(); connection.PhysicalRouteAbsent.Should().Be(absent);
        var error = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<IOException>();
        if (absent) error.Which.Should().BeOfType<RogOledConnectionLostException>();
        else error.Which.Should().BeOfType<IOException>();
        io.Writes.Should().Be(1);
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Writes.Should().Be(1);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Missing_or_failed_probe_never_proves_absence(bool wireless)
    {
        using var io = new IoFailure();
        using IRogOledConnection unknown = wireless
            ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => false)
            : new RogWiredOledConnection(io, () => false);
        unknown.PhysicalRouteAbsent.Should().BeFalse();
        using IRogOledConnection failed = wireless
            ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => false) { RouteAbsentProbe = () => throw new IOException("metadata unavailable") }
            : new RogWiredOledConnection(io, () => false) { RouteAbsentProbe = () => throw new IOException("metadata unavailable") };
        failed.PhysicalRouteAbsent.Should().BeFalse();
    }

    [Fact]
    public void Complete_snapshot_distinguishes_absent_from_present_but_changed_shape_or_failed_metadata()
    {
        RogHidRoutePresence.Probe(() => [], Path).Should().BeTrue();
        RogHidRoutePresence.Probe(() => [Control with { Path = Path.ToUpperInvariant(), UsagePage = 0, OutputReportLength = 0, Error = "caps unavailable" }], Path).Should().BeFalse();
        RogHidRoutePresence.Probe(() => throw new IOException("enumeration incomplete"), Path).Should().BeFalse();
        RogHidRoutePresence.Probe(() => [Control], "").Should().BeFalse();
    }

    [Fact]
    public void Wired_factory_positive_absence_uses_captured_path_even_if_metadata_shape_changes()
    {
        var devices = new DeviceSnapshot();
        var factory = new RogWiredOledFactory(devices, new Resolver(), _ => new IoFailure());
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(Control));
        connection.Should().NotBeNull(); connection!.PhysicalRouteAbsent.Should().BeFalse();
        devices.Collections = [Control with { Interface = 9, OutputReportLength = 0, Error = "metadata unavailable" }];
        connection.SameDevice().Should().BeFalse(); connection.PhysicalRouteAbsent.Should().BeFalse();
        devices.Fail = true; connection.PhysicalRouteAbsent.Should().BeFalse();
        devices.Fail = false; devices.Collections = []; connection.PhysicalRouteAbsent.Should().BeTrue();
    }

    [Fact]
    public void Complete_platform_enumeration_refuses_unknown_injected_enumerator()
    {
        var platform = new WinHidReportDevices(new UnknownUsbEnumerator());
        ((Action)(() => platform.EnumerateComplete(0x0B05, 0x1C24))).Should().Throw<InvalidOperationException>();
        RogHidRoutePresence.Probe(platform, 0x0B05, 0x1C24, Path).Should().BeFalse();
    }

    private sealed class UnknownUsbEnumerator : IUsbEnumerator
    { public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass) => []; }
    private sealed class DeviceSnapshot : IHidReportDevices
    {
        public bool Fail { get; set; }
        public IReadOnlyList<HidCollectionInfo> Collections { get; set; } = [Control];
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => Fail ? throw new IOException("metadata unavailable") : Collections;
        public IHidReportChannel Open(HidCollectionInfo collection) => throw new InvalidOperationException("No shared opener");
    }
    private sealed class Resolver : IHidContainerResolver
    {
        public Guid ContainerId { get; set; } = Guid.NewGuid();
        public bool Unavailable { get; set; }
        public HidContainerResolution Resolve(HidCollectionInfo collection) => Unavailable
            ? HidContainerResolution.Unavailable("metadata unavailable")
            : HidContainerResolution.Resolved(HidPhysicalContainer.FromPlatformContainerId(ContainerId, TimeProvider.System.GetTimestamp()));
    }
    private sealed class IoFailure : IHidReportChannel
    {
        public int Writes { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) { Writes++; throw new IOException("synthetic write failure"); }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public void Dispose() { }
    }
}
