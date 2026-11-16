using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// Stage 2 self-healing: a transport that disposed itself after a timeout (ObjectDisposedException on the following frames)
/// and hardware state failures (InvalidOperationException, e.g. NVAPI status) must keep counting towards the Host's 3-strike
/// close/reopen recovery instead of resetting it; contract errors, standing blocks and lifecycle shutdown do not count.
/// </summary>
public sealed class DeviceWriterIoFailureTests
{
    private static readonly ControllerDescriptor Strip = TestSupport.Controller("test:io-failures", ZoneLayout.Linear(2));

    public static TheoryData<string> DeviceFaults => new() { "timeout-then-disposed", "nvapi-like", "io", "win32" };

    [Theory]
    [MemberData(nameof(DeviceFaults))]
    public void Device_faults_accumulate_to_the_recovery_threshold(string kind)
    {
        using var bus = new FrameBus();
        var box = bus.Register(Strip);
        var adapter = new ThrowingAdapter();
        var errors = new List<Exception>();
        using var writer = new DeviceWriter(box, adapter, onError: (_, ex) => errors.Add(ex));

        for (var i = 0; i < 3; i++)
        {
            adapter.Next = kind switch
            {
                // HidTransport: the first frame times out and the transport disposes itself; later frames see it disposed
                "timeout-then-disposed" => i == 0 ? new TimeoutException("hid write timed out") : new ObjectDisposedException("HidTransport"),
                "nvapi-like" => new InvalidOperationException("NVAPI I2CWriteEx failed with status -5"),
                "io" => new IOException("device not connected"),
                _ => new System.ComponentModel.Win32Exception(31),
            };
            bus.Publish(Strip.Id, box.Back);
            writer.DrainOnce().Should().BeTrue();
            writer.ConsecutiveIoFailures.Should().Be(i + 1, $"strike {i + 1} of {kind} is a device fault");
        }

        errors.Should().HaveCount(3);
        writer.GetStats().Errors.Should().Be(3);
    }

    [Fact]
    public void A_successful_frame_resets_the_streak()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Strip);
        var adapter = new ThrowingAdapter { Next = new ObjectDisposedException("HidTransport") };
        using var writer = new DeviceWriter(box, adapter);
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(1);

        adapter.Next = null;
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(0);
    }

    [Fact]
    public void Contract_errors_and_standing_blocks_reset_the_streak()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Strip);
        var adapter = new ThrowingAdapter { Next = new IOException("x") };
        using var writer = new DeviceWriter(box, adapter);
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(1);

        adapter.Next = new ArgumentException("bad zone");
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(0);

        adapter.Next = new IOException("x");
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        adapter.Next = new DeviceBlockedException("vendor writer present");
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(0, "a standing block is not healed by reopening");
    }

    [Fact]
    public async Task ObjectDisposed_after_lifecycle_stop_is_not_a_device_fault()
    {
        using var bus = new FrameBus();
        var box = bus.Register(Strip);
        var adapter = new ThrowingAdapter { Next = new IOException("x") };
        var writer = new DeviceWriter(box, adapter);
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(1);

        writer.Start();
        await writer.StopAsync();
        adapter.Next = new ObjectDisposedException("adapter closed by shutdown");
        bus.Publish(Strip.Id, box.Back);
        writer.DrainOnce();
        writer.ConsecutiveIoFailures.Should().Be(0, "the writer's own lifecycle stopped it; the adapter is being closed");
        writer.Dispose();
    }

    private sealed class ThrowingAdapter : IDeviceAdapter
    {
        public Exception? Next { get; set; }

        public string Vendor => "Test";

        public DeviceClass Class => DeviceClass.Virtual;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);

        public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
            if (Next is { } ex) throw ex;
        }

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }
}
