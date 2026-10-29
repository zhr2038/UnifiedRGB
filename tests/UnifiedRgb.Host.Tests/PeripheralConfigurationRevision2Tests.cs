using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class PeripheralConfigurationRevision2Tests
{
    private static string NewData()
    {
        var path = Path.Combine(Path.GetTempPath(), "urgb-peripheral-r2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "peripherals"));
        return path;
    }
    private static string Marker(string data) => Path.Combine(data, "peripherals", "peripheral-configuration.json");
    private static (PeripheralConfigurationService Service, PeripheralConfigurationGate Gate, TraceResources Resources) Make(string data, TimeSpan? timeout = null)
    {
        var options = new UnifiedRgbOptions { DataDirectory = data };
        var gate = new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load());
        var resources = new TraceResources(gate, Marker(data));
        return (new PeripheralConfigurationService(options, gate, resources, timeout), gate, resources);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("[]")]
    public async Task Revision2_corrupt_schema_kind_cannot_throw_host_construction(string value)
    {
        var data = NewData();
        File.WriteAllText(Marker(data), "{\"Schema\":" + value + ",\"Requested\":true}");
        var made = Make(data);
        made.Gate.RogAcquisitionAllowed.Should().BeFalse();
        (await made.Service.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Error");
        File.ReadAllText(Marker(data)).Should().Contain(value.Trim('"'));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revision2_failed_resume_keeps_marker_gate_and_blocks_later_acquisition(bool timeout)
    {
        var data = NewData();
        var (service, gate, resources) = Make(data, TimeSpan.FromMilliseconds(20));
        (await service.EnterPeripheralConfigurationAsync()).State.Should().Be("Ready");
        resources.Resume = async ct => { if (timeout) await Task.Delay(Timeout.InfiniteTimeSpan, ct); else throw new IOException("fake sharing32 busy device"); };
        var failed = await service.ExitPeripheralConfigurationAsync();
        failed.State.Should().Be("Error");
        failed.LastError.Should().NotBeNull();
        failed.ConfigurationMode.Should().BeTrue();
        File.Exists(Marker(data)).Should().BeTrue();
        gate.RogAcquisitionAllowed.Should().BeFalse();
        resources.TryLaterIoAcquisition().Should().BeFalse();
    }

    [Fact]
    public async Task Revision2_pending_journal_cannot_exit_bypass()
    {
        var data = NewData();
        File.WriteAllText(Marker(data), "{\"Schema\":1,\"Requested\":true}");
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        File.WriteAllText(journal, "{\"Schema\":2,\"DeviceKey\":\"WIRED-FAKE\",\"ProfileToken\":6,\"Brightness\":0}");
        var (service, gate, resources) = Make(data);
        (await service.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Error");
        var after = await service.ExitPeripheralConfigurationAsync();
        resources.Trace.Should().NotContain(x => x.StartsWith("resume:", StringComparison.Ordinal));
        File.Exists(Marker(data)).Should().BeTrue();
        gate.RogAcquisitionAllowed.Should().BeFalse();
        after.State.Should().Be("Error");
        after.LastError.Should().Contain("recovery journal");
    }

    [Fact]
    public async Task Revision2_failed_adapter_is_retained_until_its_same_live_resource_releases()
    {
        var data = NewData();
        var (_, gate, _) = Make(data);
        var adapter = new HeldAdapter();
        var factory = new OneFactory(adapter);
        using var bus = new FrameBus();
        var host = new AdapterHost([factory], null!, new ClearGuard(), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance, new UnifiedRgbOptions { DataDirectory = data }, gate);
        await host.StartAsync(default);
        var resources = new TraceResources(gate, Marker(data)) { Release = host.ReleaseForPeripheralConfigurationAsync };
        var service = new PeripheralConfigurationService(new UnifiedRgbOptions { DataDirectory = data }, gate, resources);
        try
        {
            var first = await service.EnterPeripheralConfigurationAsync();
            var second = await service.EnterPeripheralConfigurationAsync();
            first.State.Should().Be("Error");
            second.State.Should().Be("Error");
            adapter.ReleaseCalls.Should().Be(2);
            adapter.LiveChannels.Should().Be(1);
            adapter.TryReport().Should().BeTrue();
            factory.Creates.Should().Be(1);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Revision2_ignored_dispose_token_is_bounded_and_late_completion_is_not_late_ready()
    {
        var data = NewData();
        var (_, gate, _) = Make(data);
        var adapter = new StalledAdapter();
        var factory = new OneFactory(adapter);
        using var bus = new FrameBus();
        var host = new AdapterHost([factory], null!, new ClearGuard(), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance, new UnifiedRgbOptions { DataDirectory = data }, gate);
        await host.StartAsync(default);
        CancellationToken operationToken = default;
        var resources = new TraceResources(gate, Marker(data)) { Release = ct => { operationToken = ct; return host.ReleaseForPeripheralConfigurationAsync(ct); } };
        var service = new PeripheralConfigurationService(new UnifiedRgbOptions { DataDirectory = data }, gate, resources, TimeSpan.FromMilliseconds(20));
        var entering = service.EnterPeripheralConfigurationAsync();
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var completedWithinBound = true;
        try { await entering.WaitAsync(TimeSpan.FromMilliseconds(150)); }
        catch (TimeoutException) { completedWithinBound = false; }
        (await service.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Error");
        var laterExplicitObservation = service.EnterPeripheralConfigurationAsync();
        adapter.Finish.TrySetResult();
        (await entering).State.Should().Be("Error");
        (await laterExplicitObservation.WaitAsync(TimeSpan.FromSeconds(2))).State.Should().Be("Ready");
        operationToken.IsCancellationRequested.Should().BeTrue();
        completedWithinBound.Should().BeTrue();
        gate.RogAcquisitionAllowed.Should().BeFalse();
        await host.StopAsync();
    }

    [Fact]
    public async Task Revision2_actual_adapter_present_open_failure_cannot_be_committed_as_active()
    {
        var data = NewData();
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null));
        var adapter = new FailingOpenAdapter();
        var factory = new OneFactory(adapter);
        using var bus = new FrameBus();
        var host = new AdapterHost([factory], null!, new ClearGuard(), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance,
            new UnifiedRgbOptions { DataDirectory = data }, gate);
        await host.StartAsync(default);
        gate.BeginFreshResume();
        try
        {
            var resume = () => host.ResumeAfterPeripheralConfigurationAsync(default);
            await resume.Should().ThrowAsync<AggregateException>().WithMessage("*current-settings open*");
            adapter.OpenCalls.Should().Be(1);
            gate.RogAcquisitionAllowed.Should().BeFalse();
        }
        finally
        {
            gate.EndFreshResume();
            await host.StopAsync();
        }
    }

    private sealed class FailingOpenAdapter : IDeviceAdapter, INativeBringup
    {
        private readonly ControllerDescriptor _descriptor = new(new ControllerId("rog:present"), "fake", "present ROG", DeviceType.Mouse,
            DeviceClass.AsusRog, string.Empty, [AggregateZone.Descriptor("mouse")], Capabilities.DirectOnly(10));
        public int OpenCalls { get; private set; }
        public string Vendor => "revision2-fake";
        public DeviceClass Class => DeviceClass.AsusRog;
        public IReadOnlyList<string> Probes => [];
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }
        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_descriptor]);
        public Task OpenAsync(ControllerId id, CancellationToken ct) { OpenCalls++; throw new IOException("present device sharing32 busy"); }
        public bool IsControlVerified(ControllerId id) => true;
        public string UnverifiedReason(ControllerId id) => string.Empty;
        public Task OpenForBringupAsync(ControllerId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct) => Task.FromResult(new NativeProbeResult("release", true, "fake", []));
        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) { }
        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId id) => null;
        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }

    private sealed class TraceResources(PeripheralConfigurationGate gate, string marker) : IPeripheralConfigurationResources
    {
        public List<string> Trace { get; } = [];
        public Func<CancellationToken, Task>? Release { get; set; }
        public Func<CancellationToken, Task>? Resume { get; set; }
        public Task ReleaseAsync(CancellationToken ct) { Trace.Add("release"); return Release?.Invoke(ct) ?? Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken ct) { Trace.Add($"resume:allowed={gate.RogAcquisitionAllowed},marker={File.Exists(marker)}"); return Resume?.Invoke(ct) ?? Task.CompletedTask; }
        public bool TryLaterIoAcquisition() { Trace.Add("later-acquire:" + gate.RogAcquisitionAllowed); return gate.RogAcquisitionAllowed; }
    }
    private sealed class OneFactory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => DeviceClass.AsusRog;
        public int Creates { get; private set; }
        public IDeviceAdapter Create(IServiceProvider sp) { Creates++; return adapter; }
    }
    private abstract class EmptyAdapter : IDeviceAdapter
    {
        public string Vendor => "revision2-fake";
        public DeviceClass Class => DeviceClass.AsusRog;
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }
        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);
        public Task OpenAsync(ControllerId id, CancellationToken ct) => throw new InvalidOperationException("No real open.");
        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new InvalidOperationException("No real write.");
        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new InvalidOperationException("No real firmware.");
        public Telemetry? ReadTelemetry(ControllerId id) => null;
        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }
    private sealed class HeldAdapter : EmptyAdapter, IRogPeripheralConfigurationLifetime
    {
        public int ReleaseCalls { get; private set; }
        public int LiveChannels { get; private set; } = 1;
        public Task ReleaseForConfigurationAsync(CancellationToken ct) { ReleaseCalls++; throw new IOException("original restoration failed; live channel retained"); }
        public bool TryReport() => LiveChannels > 0;
    }
    private sealed class StalledAdapter : EmptyAdapter, IAsyncDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync() { Started.TrySetResult(); await Finish.Task; }
    }
    private sealed class ClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass cls) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass cls) => cls == DeviceClass.AsusRog;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass cls, bool enabled, CancellationToken ct) => Task.FromResult(new TakeoverResult(cls, enabled, [], [], null));
        public Task<TakeoverResult> TakeoverAsync(DeviceClass cls, CancellationToken ct) => Task.FromResult(new TakeoverResult(cls, true, [], [], null));
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
