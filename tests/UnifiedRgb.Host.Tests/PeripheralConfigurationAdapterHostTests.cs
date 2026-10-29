using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class PeripheralConfigurationAdapterHostTests
{
    [Fact]
    public async Task Blocked_bootstrap_scans_rechecks_and_hotplug_never_construct_rog_but_other_vendor_is_untouched()
    {
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null));
        var rog = new Factory(DeviceClass.AsusRog);
        var other = new Factory(DeviceClass.AsusAura);
        using var bus = new FrameBus();
        var host = new AdapterHost([rog, other], null!, new ClearGuard(), bus, () => new HybridSleeper(),
            NullLogger<AdapterHost>.Instance, new UnifiedRgbOptions(), gate);
        await host.StartAsync(default);
        rog.Creates.Should().Be(0);
        other.Creates.Should().Be(1);
        other.Adapters.Single().Discovers.Should().Be(1);
        await host.RescanAsync(default);
        await host.RecheckAsync(default);
        rog.Creates.Should().Be(0);
        other.Adapters.Single().Discovers.Should().Be(2);

        await host.ResumeAfterPeripheralConfigurationAsync(default);
        await host.ResumeAfterPeripheralConfigurationAsync(default);
        rog.Creates.Should().Be(1);
        rog.Adapters.Single().Discovers.Should().Be(1);
        var current = rog.Adapters.Single();
        await host.ReleaseForPeripheralConfigurationAsync(default);
        current.Releases.Should().Be(1);
        current.Disposes.Should().Be(1);
        other.Adapters.Single().Disposes.Should().Be(0);
        await host.RescanAsync(default);
        await host.RecheckAsync(default);
        current.Discovers.Should().Be(1);
        current.RaiseHotplug();
        host.RescanRequested.Should().BeFalse();
        rog.Creates.Should().Be(1);
        other.Adapters.Single().Disposes.Should().Be(0);
        await host.StopAsync();
        other.Adapters.Single().Disposes.Should().Be(1);
    }

    private sealed class Factory(DeviceClass cls) : IDeviceAdapterFactory
    {
        public DeviceClass Class => cls;
        public int Creates { get; private set; }
        public List<FakeAdapter> Adapters { get; } = [];
        public IDeviceAdapter Create(IServiceProvider sp)
        {
            Creates++;
            var adapter = new FakeAdapter(cls);
            Adapters.Add(adapter);
            return adapter;
        }
    }

    private sealed class FakeAdapter(DeviceClass cls) : IDeviceAdapter, IRogPeripheralConfigurationLifetime, IAsyncDisposable
    {
        public string Vendor => cls.ToString();
        public DeviceClass Class => cls;
        public int Discovers { get; private set; }
        public int Releases { get; private set; }
        public int Disposes { get; private set; }
        public event Action<HotplugEvent>? Hotplug;
        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Discovers++;
            return Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);
        }
        public Task OpenAsync(ControllerId id, CancellationToken ct) => throw new NotSupportedException();
        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new NotSupportedException();
        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId id) => null;
        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
        public Task ReleaseForConfigurationAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Releases++;
            Disposes++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
        public void RaiseHotplug() => Hotplug?.Invoke(new HotplugEvent(new ControllerId("rog"), HotplugKind.Arrived));
    }

    private sealed class ClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) =>
            Task.FromResult(new TakeoverResult(deviceClass, enabled, [], [], null));
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) =>
            Task.FromResult(new TakeoverResult(deviceClass, true, [], [], null));
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
