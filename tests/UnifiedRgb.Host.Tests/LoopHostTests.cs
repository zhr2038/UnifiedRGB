using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

/// <summary>ARCHITECTURE-D2 §3/§9 host wiring: codec Blocked(reason), loop writers, explicit independent/vendor selection.</summary>
public class LoopHostTests
{
    private static WriterGuard ClearGuard() => new(new ProcessScanner(new EmptySnapshot()));

    [Fact]
    public async Task A_device_blocked_at_open_shows_Blocked_with_its_reason_and_gets_no_writer()
    {
        var adapter = new LoopAdapter { OpenError = new DeviceBlockedException("yuz.dll missing (LianLi:YuzDllPath not set and L-Connect 3 not found)") };
        using var bus = new FrameBus();
        var host = new AdapterHost([new Factory(adapter)], null!, ClearGuard(), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await host.StartAsync(CancellationToken.None);

        var c = host.Controllers.Should().ContainSingle().Subject;
        c.IsActive.Should().BeFalse();
        c.Verdict.State.Should().Be(GuardState.Blocked);
        c.Verdict.Writer.Should().StartWith("yuz.dll missing");
        DtoMapper.ToDto(c).State.Should().Be(ControllerStates.Blocked);
        adapter.Uploads.Should().Be(0);
        await host.StopAsync();
    }

    [Fact]
    public async Task An_uploaded_loop_controller_gets_a_loop_writer_that_reports_uploads()
    {
        var adapter = new LoopAdapter();
        using var bus = new FrameBus();
        var host = new AdapterHost([new Factory(adapter)], null!, ClearGuard(), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        var reported = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.LoopUploaded += (_, p, _) => reported.TrySetResult(p.Loop.EffectIndex);
        await host.StartAsync(CancellationToken.None);

        var c = host.Controllers.Should().ContainSingle().Subject;
        c.IsActive.Should().BeTrue();
        c.Writer!.UploadsLoops.Should().BeTrue();
        bus.PublishLoop(c.Descriptor.Id, new ZoneId(0), new LoopFrames(2, 4, 20, 77, new byte[24]));
        (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(77u);
        adapter.Uploads.Should().Be(1);
        adapter.Writes.Should().Be(0, "WriteFrame is never called for an UploadedLoop controller");
        await host.StopAsync();
    }

    [Fact]
    public async Task Consecutive_IO_failures_close_the_old_writer_before_reopening_the_controller()
    {
        var adapter = new LoopAdapter { FailUploads = true };
        using var bus = new FrameBus();
        await using var host = new AdapterHost([new Factory(adapter)], null!, ClearGuard(), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        var entry = host.Controllers.Should().ContainSingle().Subject;
        var firstWriter = entry.Writer!;
        for (uint i = 1; i <= 3; i++)
        {
            bus.PublishLoop(entry.Descriptor.Id, new ZoneId(0), new LoopFrames(2, 4, 20, i, new byte[24]));
            while (firstWriter.ConsecutiveIoFailures < i) await Task.Delay(10, timeout.Token);
        }
        while (!host.RescanRequested) await Task.Delay(10, timeout.Token);
        adapter.FailUploads = false;
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.CloseHook = () => { closing.TrySetResult(); return closeAllowed.Task; };
        var recovery = host.RescanAsync(timeout.Token);
        try
        {
            await closing.Task.WaitAsync(timeout.Token);
            Action reserveBringup = () => host.ReserveBringup(entry, "test");
            reserveBringup.Should().Throw<InvalidOperationException>().WithMessage("*recovery*");
            adapter.Opens.Should().Be(1);
        }
        finally { closeAllowed.TrySetResult(); }
        (await recovery).Should().BeTrue();
        entry.Writer.Should().NotBeSameAs(firstWriter);
        adapter.Opens.Should().Be(2);
        adapter.Closes.Should().Be(1);
        host.TakeArrivals().Should().ContainSingle().Which.Should().Be(entry.Descriptor.Id);
    }

    [Fact]
    public async Task StopAsync_isolates_uncertain_shared_adapter_cleanly_disposes_healthy_independent_adapter_and_rethrows()
    {
        var sys = new FakeSystem();
        var guard = new WriterGuard(new ProcessScanner(sys));
        using var bus = new FrameBus();
        var sleepersDisposed = 0;

        await using var sharedAdapter = new SharedTwoWriterAdapter("c:hung", "c:sibling");
        await using var independentAdapter = new IndependentStopAdapter("c:healthy");
        var host = new AdapterHost(
            [new StubFactory(sharedAdapter), new StubFactory(independentAdapter)],
            null!,
            guard,
            bus,
            () => new CountingSleeper(() => Interlocked.Increment(ref sleepersDisposed)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdapterHost>.Instance)
        {
            WriterStopTimeout = TimeSpan.FromMilliseconds(120),
        };

        await host.StartAsync(CancellationToken.None);
        host.Controllers.Should().HaveCount(3).And.OnlyContain(c => c.IsActive);

        // Trigger the blocked in-flight write on c:hung, plus active writes on sibling & independent.
        var hungBox = bus[new ControllerId("c:hung")]!;
        var siblingBox = bus[new ControllerId("c:sibling")]!;
        var healthyBox = bus[new ControllerId("c:healthy")]!;
        hungBox.Back.Zones[0][0] = new Rgb(10, 20, 30);
        siblingBox.Back.Zones[0][0] = new Rgb(10, 20, 30);
        healthyBox.Back.Zones[0][0] = new Rgb(10, 20, 30);
        bus.Publish(new ControllerId("c:hung"), hungBox.Back);
        bus.Publish(new ControllerId("c:sibling"), siblingBox.Back);
        bus.Publish(new ControllerId("c:healthy"), healthyBox.Back);

        sharedAdapter.HungEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        var waitUntil = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < waitUntil && (sharedAdapter.SiblingWrites == 0 || independentAdapter.Writes == 0))
            await Task.Delay(10);
        sharedAdapter.SiblingWrites.Should().BePositive();
        independentAdapter.Writes.Should().BePositive();

        try
        {
            var stop = () => host.StopAsync();
            await stop.Should().ThrowAsync<TimeoutException>();

            // Healthy independent adapter was cleanly closed + disposed.
            independentAdapter.ClosedIds.Should().Equal("c:healthy");
            independentAdapter.Disposed.Should().Be(1);

            // Uncertain hung controller AND its healthy sibling sharing the same IDeviceAdapter were NOT closed or disposed.
            sharedAdapter.ClosedIds.Should().BeEmpty();
            sharedAdapter.Disposed.Should().Be(0);

            // Sleepers are NOT disposed when shutdown had uncertain writers (writer thread may still be touching its sleeper).
            sleepersDisposed.Should().Be(0);
        }
        finally
        {
            sharedAdapter.ReleaseHung.Set();
        }
    }

    private sealed class StubFactory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => adapter.Class;
        public IDeviceAdapter Create(IServiceProvider sp) => adapter;
    }

    private sealed class CountingSleeper(Action onDispose) : IPreciseSleeper, IDisposable
    {
        public void SleepUntil(long deadlineTicks) => Thread.Sleep(1);
        public void Dispose() => onDispose();
    }

    private sealed class SharedTwoWriterAdapter(string hungId, string siblingId) : IDeviceAdapter, IAsyncDisposable
    {
        private int _siblingWrites;
        private int _disposed;
        public ManualResetEventSlim HungEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseHung { get; } = new(false);
        public List<string> ClosedIds { get; } = [];
        public int SiblingWrites => Volatile.Read(ref _siblingWrites);
        public int Disposed => Volatile.Read(ref _disposed);
        public string Vendor => "Test";
        public DeviceClass Class => DeviceClass.LianLi;
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>(
            [
                new(new ControllerId(hungId), "Hung", "H", DeviceType.Cooler, Class, hungId, [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)], Capabilities.DirectOnly(60)),
                new(new ControllerId(siblingId), "Sibling", "S", DeviceType.Cooler, Class, siblingId, [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)], Capabilities.DirectOnly(60)),
            ]);

        public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
            if (id.Value == hungId)
            {
                HungEntered.Set();
                ReleaseHung.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                Interlocked.Increment(ref _siblingWrites);
            }
        }

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id)
        {
            lock (ClosedIds) ClosedIds.Add(id.Value);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            HungEntered.Dispose();
            ReleaseHung.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IndependentStopAdapter(string id) : IDeviceAdapter, IAsyncDisposable
    {
        private int _writes;
        private int _disposed;
        public List<string> ClosedIds { get; } = [];
        public int Writes => Volatile.Read(ref _writes);
        public int Disposed => Volatile.Read(ref _disposed);
        public string Vendor => "Test";
        public DeviceClass Class => DeviceClass.MsiGpu;
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>(
                [new(new ControllerId(id), "Healthy", "I", DeviceType.Gpu, Class, id, [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)], Capabilities.DirectOnly(60))]);

        public Task OpenAsync(ControllerId idValue, CancellationToken ct) => Task.CompletedTask;
        public void WriteFrame(ControllerId idValue, ZoneId zone, ReadOnlySpan<Rgb> colors) => Interlocked.Increment(ref _writes);
        public Task SetFirmwareModeAsync(ControllerId idValue, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId idValue) => null;

        public Task CloseAsync(ControllerId idValue)
        {
            lock (ClosedIds) ClosedIds.Add(idValue.Value);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Managed_is_the_default_even_with_a_stale_vendor_path_and_vendor_is_explicit()
    {
        var options = new LianLiOptions { YuzDllPath = @"C:\missing\yuz.dll" };
        RgbAnimationEncoders.Create(options).Should().BeOfType<ManagedTinyuzEncoder>();
        options.Codec = LianLiCompressionCodec.VendorYuz;
        var missing = () => RgbAnimationEncoders.Create(options);
        missing.Should().Throw<DeviceBlockedException>().Which.Reason.Should().Contain(options.YuzDllPath);
        options.Codec = (LianLiCompressionCodec)99;
        var invalid = () => RgbAnimationEncoders.Create(options);
        invalid.Should().Throw<DeviceBlockedException>().WithMessage("Unknown*codec*");
    }

    private sealed class EmptySnapshot : ISystemSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes() => [];

        public IReadOnlyList<ServiceInfo> Services() => [];
    }

    private sealed class Factory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => adapter.Class;

        public IDeviceAdapter Create(IServiceProvider sp) => adapter;
    }

    /// <summary>A Virtual-class (guard always clear) UploadedLoop adapter.</summary>
    private sealed class LoopAdapter : ILoopUploadAdapter
    {
        private readonly ControllerDescriptor _d = new(new ControllerId("loop:1"), "L", "Chain", DeviceType.Cooler, DeviceClass.Virtual, "1",
            [new ZoneDescriptor(new ZoneId(0), "chain", ZoneLayout.Linear(4))], Capabilities.UploadedLoopOnly(200, 20, 12288));

        private LoopUploadStats? _last;

        public Exception? OpenError { get; init; }
        public bool FailUploads { get; set; }
        public Func<Task>? CloseHook { get; set; }
        public int Opens { get; private set; }
        public int Closes { get; private set; }

        public int Uploads { get; private set; }

        public int Writes { get; private set; }

        public string Vendor => "L";

        public DeviceClass Class => DeviceClass.Virtual;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_d]);

        public Task OpenAsync(ControllerId id, CancellationToken ct)
        {
            Opens.Should().Be(Closes, "an earlier writer still owns the connection");
            Opens++;
            return OpenError is { } e ? Task.FromException(e) : Task.CompletedTask;
        }

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => Writes++;

        public Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct)
        {
            Uploads++;
            if (FailUploads) throw new IOException("injected controller I/O failure");
            _last = new LoopUploadStats(DateTimeOffset.UtcNow, 1, loop.EffectIndex, loop.Frames, loop.IntervalMs, loop.Rgb.Length, loop.Rgb.Length, 1, TimeSpan.Zero, 0, "test");
            return Task.CompletedTask;
        }

        public LoopUploadStats? LastUpload(ControllerId id) => _last;

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public async Task CloseAsync(ControllerId id)
        {
            if (CloseHook is { } close) await close();
            Closes++;
        }
    }
}
