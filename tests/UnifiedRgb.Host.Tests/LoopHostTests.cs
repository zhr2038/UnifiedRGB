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

        public Task OpenAsync(ControllerId id, CancellationToken ct) => OpenError is { } e ? Task.FromException(e) : Task.CompletedTask;

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => Writes++;

        public Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct)
        {
            Uploads++;
            _last = new LoopUploadStats(DateTimeOffset.UtcNow, 1, loop.EffectIndex, loop.Frames, loop.IntervalMs, loop.Rgb.Length, loop.Rgb.Length, 1, TimeSpan.Zero, 0, "test");
            return Task.CompletedTask;
        }

        public LoopUploadStats? LastUpload(ControllerId id) => _last;

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }
}
