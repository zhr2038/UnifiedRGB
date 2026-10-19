using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

/// <summary>Deferred active discovery uses fake devices, guard snapshots and in-memory takeover state only.</summary>
public sealed class DeferredDiscoveryRecoveryTests
{
    [Fact]
    public async Task Removing_LEDKeeper_discovers_both_RAMs_once_without_a_manual_rescan()
    {
        var system = new FakeSystem().Process("LEDKeeper2");
        using var guard = new WriterGuard(new ProcessScanner(system), system, new TakeoverStore((string?)null));
        await guard.SetTakeoverAsync(DeviceClass.EneDram, true, default);
        using var bus = new FrameBus();
        var adapter = new DiscoveringRam("ram");
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        host.Controllers.Should().BeEmpty();
        adapter.Calls.Should().BeEmpty("blocked active discovery must not touch the adapter");
        (await host.RecheckAsync(default)).Should().BeFalse();
        adapter.Calls.Should().BeEmpty();

        system.Kill("LEDKeeper2"); // a fake process snapshot, never a real process operation
        (await host.RecheckAsync(default)).Should().BeTrue();
        host.Controllers.Should().HaveCount(2).And.OnlyContain(c => c.IsActive);
        adapter.Calls.Should().Equal("discover", "open ram:0", "open ram:1");
        (await host.RecheckAsync(default)).Should().BeFalse();
        (await host.RecheckAsync(default)).Should().BeFalse();

        system.Process("LEDKeeper2");
        await host.RecheckAsync(default);
        host.Controllers.Should().OnlyContain(c => c.Writer!.Paused);
        system.Kill("LEDKeeper2");
        await host.RecheckAsync(default);
        host.Controllers.Should().OnlyContain(c => !c.Writer!.Paused);
        adapter.Discoveries.Should().Be(1, "successful discovery must not repeat on later unblock checks");
    }

    [Fact]
    public async Task Deferred_adapters_retry_independently_until_discovery_succeeds()
    {
        var guard = BlockedGuard();
        using var bus = new FrameBus();
        var retry = new DiscoveringRam("retry") { FailDiscoveries = 1 };
        var ready = new DiscoveringRam("ready");
        await using var host = Host(bus, guard, retry, ready);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;

        (await host.RecheckAsync(default)).Should().BeTrue();
        host.Controllers.Should().HaveCount(2).And.OnlyContain(c => c.Descriptor.Id.Value.StartsWith("ready:", StringComparison.Ordinal));
        (await host.RecheckAsync(default)).Should().BeTrue();
        host.Controllers.Should().HaveCount(4).And.OnlyContain(c => c.IsActive);
        (await host.RecheckAsync(default)).Should().BeFalse();
        retry.Discoveries.Should().Be(2);
        ready.Discoveries.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_discovery_does_not_consume_the_deferred_retry(bool returnDescriptors)
    {
        var guard = BlockedGuard();
        using var bus = new FrameBus();
        using var cancellation = new CancellationTokenSource();
        var adapter = new DiscoveringRam("ram")
        {
            DuringDiscovery = async ct =>
            {
                await cancellation.CancelAsync();
                if (!returnDescriptors) ct.ThrowIfCancellationRequested();
            },
        };
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;

        await host.Invoking(h => h.RecheckAsync(cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
        host.Controllers.Should().BeEmpty();
        adapter.Calls.Should().Equal("discover");
        adapter.DuringDiscovery = null;
        (await host.RecheckAsync(default)).Should().BeTrue();
        adapter.Discoveries.Should().Be(2);
        host.Controllers.Should().HaveCount(2).And.OnlyContain(c => c.IsActive);
    }

    [Fact]
    public async Task A_new_block_during_discovery_prevents_open_and_keeps_successful_discovery()
    {
        var guard = BlockedGuard();
        using var bus = new FrameBus();
        var adapter = new DiscoveringRam("ram")
        {
            DuringDiscovery = _ =>
            {
                guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Blocked("LEDKeeper2");
                return Task.CompletedTask;
            },
        };
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        await host.RecheckAsync(default);
        host.Controllers.Should().HaveCount(2).And.OnlyContain(c => !c.IsActive);
        adapter.Calls.Should().Equal("discover");

        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        await host.RecheckAsync(default);
        host.Controllers.Should().OnlyContain(c => c.IsActive);
        adapter.Discoveries.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Deferred_discovery_does_not_bypass_takeover_or_control_verification(bool takeover, bool verified)
    {
        var guard = BlockedGuard();
        await guard.SetTakeoverAsync(DeviceClass.EneDram, takeover, default);
        using var bus = new FrameBus();
        var adapter = new DiscoveringRam("ram") { Verified = verified };
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        await host.RecheckAsync(default);
        host.Controllers.Should().HaveCount(2).And.OnlyContain(c => !c.IsActive);
        adapter.Calls.Should().Equal("discover");

        guard.Enable(DeviceClass.EneDram);
        adapter.Verified = true;
        await host.RecheckAsync(default);
        host.Controllers.Should().OnlyContain(c => c.IsActive);
        adapter.Discoveries.Should().Be(1);
    }

    [Fact]
    public async Task Successful_empty_discovery_is_not_retried_by_guard_checks()
    {
        var guard = BlockedGuard();
        using var bus = new FrameBus();
        var adapter = new DiscoveringRam("ram") { Empty = true };
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        await host.RecheckAsync(default);
        await host.RecheckAsync(default);
        adapter.Discoveries.Should().Be(1);
        host.Controllers.Should().BeEmpty();

        await host.RescanAsync(default); // an explicitly requested scan still works
        adapter.Discoveries.Should().Be(2);
    }

    [Fact]
    public async Task A_bringup_lease_stays_paused_across_later_guard_changes()
    {
        var guard = BlockedGuard();
        using var bus = new FrameBus();
        var adapter = new DiscoveringRam("ram");
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        await host.RecheckAsync(default);
        var leased = host.Controllers[0];
        host.PauseForBringup(leased, "test-owner");

        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Blocked("LEDKeeper2");
        await host.RecheckAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        await host.RecheckAsync(default);
        leased.Writer!.Paused.Should().BeTrue();
        leased.BringupLease.Should().Be("test-owner");
        host.Controllers[1].Writer!.Paused.Should().BeFalse();
        adapter.Calls.Should().Equal("discover", "open ram:0", "open ram:1");
    }

    [Fact]
    public async Task Shutdown_during_discovery_adds_no_controller_and_never_opens()
    {
        var guard = BlockedGuard();
        using var bus = new FrameBus();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DiscoveringRam("ram")
        {
            DuringDiscovery = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); },
        };
        await using var host = Host(bus, guard, adapter);
        await host.StartAsync(default);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Clear;
        var check = host.RecheckAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.BeginStop();
        }
        finally
        {
            release.TrySetResult();
        }

        (await check.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        host.Controllers.Should().BeEmpty();
        (await host.RecheckAsync(default)).Should().BeFalse();
        (await host.RescanAsync(default)).Should().BeFalse();
        adapter.Calls.Should().Equal("discover");
    }

    private static FakeGuard BlockedGuard()
    {
        var guard = new FakeGuard().Enable(DeviceClass.EneDram);
        guard.Verdicts[DeviceClass.EneDram] = GuardVerdict.Blocked("LEDKeeper2");
        return guard;
    }

    private static AdapterHost Host(FrameBus bus, IWriterGuard guard, params DiscoveringRam[] adapters) =>
        new(adapters.Select(a => new InstanceFactory(a)), null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);

    private sealed class DiscoveringRam(string prefix) : IDeviceAdapter, IActiveDiscovery, INativeBringup
    {
        public List<string> Calls { get; } = [];
        public int Discoveries { get; private set; }
        public int FailDiscoveries { get; set; }
        public bool Verified { get; set; } = true;
        public bool Empty { get; set; }
        public Func<CancellationToken, Task>? DuringDiscovery { get; set; }
        public string Vendor => "Test";
        public DeviceClass Class => DeviceClass.EneDram;
        public IReadOnlyList<string> Probes => [];
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }

        public async Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("discover");
            Discoveries++;
            if (FailDiscoveries-- > 0) throw new IOException("temporary discovery failure");
            if (DuringDiscovery is { } action) await action(ct);
            return Empty ? [] : Enumerable.Range(0, 2).Select(i => new ControllerDescriptor(new($"{prefix}:{i}"), "Test", "RAM",
                DeviceType.Dram, Class, "", [new(new(0), "RAM", ZoneLayout.Linear(8))], Capabilities.DirectOnly(20))).ToArray();
        }

        public Task OpenAsync(ControllerId id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Calls.Add("open " + id.Value); return Task.CompletedTask; }
        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new NotSupportedException();
        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId id) => null;
        public bool IsControlVerified(ControllerId id) => Verified;
        public string UnverifiedReason(ControllerId id) => "test control unverified";
        public Task OpenForBringupAsync(ControllerId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct) => throw new NotSupportedException();
    }
}
