using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class PowerResumeTests
{
    [Fact]
    public async Task OnPowerResumeAsync_clears_dedup_reopens_streamed_direct_adapters_resyncs_loops_and_preserves_blackout()
    {
        var streamed = new TrackingStreamedAdapter("gpu:1", DeviceClass.MsiGpu);
        var looped = new TrackingLoopAdapter("lianli:loop1");
        var guard = new AlwaysClearGuard();

        await using var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IWriterGuard>(guard);
            s.AddSingleton<IDeviceAdapterFactory>(new Factory(streamed));
            s.AddSingleton<IDeviceAdapterFactory>(new Factory(looped));
        });

        var rgb = host.Get<IRgbService>();
        var guardMonitor = host.Get<GuardMonitor>();

        var targetColor = new Rgb(0x22, 0xAA, 0x44);
        await rgb.SetSceneAsync(new SceneDto(
        [
            new AssignmentDto([new ZoneRefDto("gpu:1", 0), new ZoneRefDto("lianli:loop1", 0)], "StaticColor", ["#22AA44"]),
        ]));

        await WaitUntilAsync(() => streamed.HasWrittenColor(targetColor) && looped.Uploads >= 1);
        var opensBeforeResume = streamed.Opens;
        var uploadsBeforeResume = looped.Uploads;

        // Clear driver-side dedup memory would be ignored if we hadn't cleared LastFrame + closed/reopened streamed adapters.
        streamed.ResetColorSeen();
        (await guardMonitor.OnPowerResumeAsync(CancellationToken.None)).Should().BeTrue();

        // Streamed non-ROG controller was detached (CloseAsync) + reopened (OpenAsync), clearing static dedup cache and rewriting #22AA44;
        // UploadedLoop controller got ResyncLoops() re-upload.
        await WaitUntilAsync(() => streamed.Opens > opensBeforeResume && streamed.HasWrittenColor(targetColor) && looped.Uploads > uploadsBeforeResume);
        streamed.Closes.Should().BeGreaterThanOrEqualTo(1);

        // Now blackout (#000000): resuming from sleep MUST preserve blackout (never restoring earlier colored scene).
        await rgb.SetSceneAsync(new SceneDto(
        [
            new AssignmentDto([new ZoneRefDto("gpu:1", 0), new ZoneRefDto("lianli:loop1", 0)], "StaticColor", ["#000000"]),
        ]));
        await WaitUntilAsync(() => streamed.HasWrittenColor(Rgb.Black));
        var uploadsBeforeBlackoutResume = looped.Uploads;
        streamed.ResetColorSeen();

        await guardMonitor.OnPowerResumeAsync(CancellationToken.None);

        await WaitUntilAsync(() => streamed.HasWrittenColor(Rgb.Black) && looped.Uploads > uploadsBeforeBlackoutResume);
        streamed.LastColor.Should().Be(Rgb.Black);
    }

    [Fact]
    public async Task Hotplug_Removed_detaches_writer_and_clears_stale_LastError_when_device_returns()
    {
        var adapter = new HotplugFaultAdapter("gpu:hp");
        var guard = new AlwaysClearGuard();
        using var bus = new FrameBus();
        var host = new AdapterHost([new Factory(adapter)], null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var entry = host.Controllers.Single();
            entry.IsActive.Should().BeTrue();

            // Inject 1 write error so DeviceWriter._lastError becomes non-null.
            adapter.FailWrites = true;
            var box = bus[entry.Descriptor.Id]!;
            box.Back.Zones[0][0] = new Rgb(10, 20, 30);
            bus.Publish(entry.Descriptor.Id, box.Back);
            await WaitUntilAsync(() => entry.Writer?.GetStats().LastError is not null);

            // Simulate physical device removal + Hotplug(Removed).
            adapter.FailWrites = false;
            adapter.Present = false;
            adapter.RaiseRemoved();
            (await host.RescanAsync(CancellationToken.None)).Should().BeTrue();

            entry.IsActive.Should().BeFalse();
            entry.Writer.Should().BeNull();
            adapter.Closes.Should().Be(1);

            // Re-plug device: fresh writer starts with LastError == null.
            adapter.Present = true;
            adapter.RaiseArrived();
            (await host.RescanAsync(CancellationToken.None)).Should().BeTrue();

            entry.IsActive.Should().BeTrue();
            entry.Writer.Should().NotBeNull();
            entry.Writer!.GetStats().LastError.Should().BeNull();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Sleeping_wireless_mouse_does_not_fail_resume_or_case_restore_and_syncs_upon_wake()
    {
        var hid = new FakeSleepableMouseHid { MouseAsleep = true };
        var receivers = RogReceiver.Discover(hid);
        receivers.Should().ContainSingle();
        var rogFactory = new FuncFactory(() => new RogAdapter(hid, new RogOptions
        {
            HidTimeoutMs = 40,
            Mice = [new RogMouseBinding { ReceiverKey = receivers[0].Key, ReportId = 3, ControlVerified = true }],
            Keyboards = [],
        }));
        var caseFan = new TrackingStreamedAdapter("case:fan1", DeviceClass.MsiGpu);
        var guard = new AlwaysClearGuard();

        await using var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IWriterGuard>(guard);
            s.AddSingleton<IDeviceAdapterFactory>(rogFactory);
            s.AddSingleton<IDeviceAdapterFactory>(new Factory(caseFan));
        });

        var adapters = host.Get<AdapterHost>();
        var gate = host.Get<PeripheralConfigurationGate>();
        var rgb = host.Get<IRgbService>();
        using var coordinator = new CasePowerCoordinator(rgb);

        var mouseEntry = adapters.Controllers.Single(c => c.Descriptor.DeviceType == DeviceType.Mouse);
        mouseEntry.IsActive.Should().BeFalse("sleeping wireless mouse timed out startup");
        var mouseId = mouseEntry.Descriptor.Id.Value;

        // 1. ResumeAfterPeripheralConfigurationAsync must NOT throw when sleeping wireless mouse is offline/retrying.
        gate.BlockRogAcquisition();
        await adapters.ReleaseForPeripheralConfigurationAsync(CancellationToken.None);
        gate.AllowRogAcquisition();
        await FluentActions.Awaiting(() => adapters.ResumeAfterPeripheralConfigurationAsync(CancellationToken.None))
            .Should().NotThrowAsync();

        // 2. Setting a mixed scene with case fan + sleeping mouse works and keeps case fan active,
        //    and Blackout -> Restore leaves case fan lit without failing on the sleeping mouse.
        await rgb.SetSceneAsync(new SceneDto(
        [
            new AssignmentDto([new ZoneRefDto("case:fan1", 0), new ZoneRefDto(mouseId, 0)], "StaticColor", ["#CC3311"]),
        ]));
        await WaitUntilAsync(() => caseFan.HasWrittenColor(new Rgb(0xCC, 0x33, 0x11)));

        (await coordinator.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeTrue();
        await WaitUntilAsync(() => caseFan.HasWrittenColor(Rgb.Black));

        caseFan.ResetColorSeen();
        (await coordinator.ExecuteAsync(CasePowerIntent.Restore)).Changed.Should().BeTrue();
        await WaitUntilAsync(() => caseFan.HasWrittenColor(new Rgb(0xCC, 0x33, 0x11)));

        // 3. Mouse wakes up -> RescanAsync opens the fresh mouse entry and renders the restored active scene color (#CC3311) to it!
        hid.MouseAsleep = false;
        var updatedControllers = await rgb.RescanAsync(CancellationToken.None);
        var currentMouse = adapters.Controllers.Single(c => c.Descriptor.DeviceType == DeviceType.Mouse);
        currentMouse.IsActive.Should().BeTrue();
        updatedControllers.Should().ContainSingle(c => c.Id == mouseId && c.State == ControllerStates.Active);

        await WaitUntilAsync(() => hid.SawMouseColor(0xCC, 0x33, 0x11));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }

        condition().Should().BeTrue($"condition was not satisfied within {timeoutMs} ms");
    }

    private sealed class Factory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => adapter.Class;
        public IDeviceAdapter Create(IServiceProvider sp) => adapter;
    }

    private sealed class FuncFactory(Func<IDeviceAdapter> create) : IDeviceAdapterFactory
    {
        public DeviceClass Class => DeviceClass.AsusRog;
        public IDeviceAdapter Create(IServiceProvider sp) => create();
    }

    private sealed class AlwaysClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass cls) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass cls) => true;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass cls, bool enabled, CancellationToken ct) =>
            Task.FromResult(new TakeoverResult(cls, enabled, [], [], null));
        public Task<TakeoverResult> TakeoverAsync(DeviceClass cls, CancellationToken ct) =>
            Task.FromResult(new TakeoverResult(cls, true, [], [], null));
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TrackingStreamedAdapter(string id, DeviceClass cls) : IDeviceAdapter
    {
        private readonly ControllerDescriptor _desc = new(
            new ControllerId(id), "Test", id, DeviceType.Gpu, cls, id,
            [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)],
            Capabilities.DirectOnly(60));

        private readonly object _sync = new();
        private Rgb? _last;
        private readonly HashSet<Rgb> _seen = [];
        private int _opens;
        private int _closes;

        public int Opens => Volatile.Read(ref _opens);
        public int Closes => Volatile.Read(ref _closes);
        public Rgb? LastColor { get { lock (_sync) return _last; } }
        public string Vendor => "Test";
        public DeviceClass Class => cls;
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }

        public void ResetColorSeen() { lock (_sync) _seen.Clear(); }
        public bool HasWrittenColor(Rgb c) { lock (_sync) return _seen.Contains(c); }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_desc]);

        public Task OpenAsync(ControllerId cid, CancellationToken ct)
        {
            Interlocked.Increment(ref _opens);
            return Task.CompletedTask;
        }

        public void WriteFrame(ControllerId cid, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
            if (colors.IsEmpty) return;
            lock (_sync)
            {
                _last = colors[0];
                _seen.Add(colors[0]);
            }
        }

        public Task SetFirmwareModeAsync(ControllerId cid, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId cid) => null;

        public Task CloseAsync(ControllerId cid)
        {
            Interlocked.Increment(ref _closes);
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingLoopAdapter(string id) : ILoopUploadAdapter
    {
        private readonly ControllerDescriptor _desc = new(
            new ControllerId(id), "Test", id, DeviceType.Cooler, DeviceClass.LianLi, id,
            [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Ring(16))],
            Capabilities.UploadedLoopOnly(400, 40.0, 4096));

        private int _uploads;
        public int Uploads => Volatile.Read(ref _uploads);
        public string Vendor => "Test";
        public DeviceClass Class => DeviceClass.LianLi;
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }
        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_desc]);
        public Task OpenAsync(ControllerId cid, CancellationToken ct) => Task.CompletedTask;
        public void WriteFrame(ControllerId cid, ZoneId zone, ReadOnlySpan<Rgb> colors) { }
        public Task UploadLoopAsync(ControllerId cid, ZoneId zone, LoopFrames frames, CancellationToken ct)
        {
            Interlocked.Increment(ref _uploads);
            return Task.CompletedTask;
        }
        public LoopUploadStats? LastUpload(ControllerId cid) => null;
        public Task SetFirmwareModeAsync(ControllerId cid, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId cid) => null;
        public Task CloseAsync(ControllerId cid) => Task.CompletedTask;
    }

    private sealed class HotplugFaultAdapter(string id) : IDeviceAdapter
    {
        private readonly ControllerDescriptor _desc = new(
            new ControllerId(id), "Test", id, DeviceType.Gpu, DeviceClass.MsiGpu, id,
            [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)],
            Capabilities.DirectOnly(60));

        private int _closes;
        public bool Present { get; set; } = true;
        public bool FailWrites { get; set; }
        public int Closes => Volatile.Read(ref _closes);
        public string Vendor => "Test";
        public DeviceClass Class => DeviceClass.MsiGpu;
        public event Action<HotplugEvent>? Hotplug;
        public void RaiseRemoved() => Hotplug?.Invoke(new HotplugEvent(_desc.Id, HotplugKind.Removed));
        public void RaiseArrived() => Hotplug?.Invoke(new HotplugEvent(_desc.Id, HotplugKind.Arrived));

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>(Present ? [_desc] : []);
        public Task OpenAsync(ControllerId cid, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId cid, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
            if (FailWrites) throw new IOException("transient device error");
        }

        public Task SetFirmwareModeAsync(ControllerId cid, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId cid) => null;
        public Task CloseAsync(ControllerId cid)
        {
            Interlocked.Increment(ref _closes);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSleepableMouseHid : IHidReportDevices
    {
        private readonly ConcurrentQueue<(byte R, byte G, byte B)> _mouseColors = new();
        public volatile bool MouseAsleep;

        public bool SawMouseColor(byte r, byte g, byte b) =>
            _mouseColors.Any(c => c.R == r && c.G == g && c.B == b);

        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid)
        {
            if (vid != RogProtocol.VendorId || pid != 0x1ACE) return [];
            return [Col(1, 0xFF02), Col(2, 0xFF00), Col(3, 0xFF01)];
        }

        private static HidCollectionInfo Col(byte rid, ushort up) => new(
            $@"\\?\HID#VID_0B05&PID_1ACE&MI_02&COL0{rid}#mouseA&0001#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            0x0B05, 0x1ACE, 2, up, 1, 64, 64, 0);

        public IHidReportChannel Open(HidCollectionInfo collection)
        {
            byte rid = collection.UsagePage switch { 0xFF02 => 1, 0xFF00 => 2, _ => 3 };
            return new Channel(this, rid);
        }

        private sealed class Channel(FakeSleepableMouseHid owner, byte rid) : IHidReportChannel
        {
            private readonly Channel<byte[]> _replies = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                var span = report.Span;
                if (rid == 1 && span.Length > 1 && span[1] == 0xA0)
                {
                    var reply = new byte[64];
                    new byte[] { 1, 0xA0, 0, 1, 0, 0x7C, 0x1D, 3, 5 }.CopyTo(reply, 0);
                    _replies.Writer.TryWrite(reply);
                }
                else if (rid == 3 && !owner.MouseAsleep)
                {
                    if (span.Length >= 10 && span[1] == 0x51)
                    {
                        owner._mouseColors.Enqueue((span[7], span[8], span[9]));
                        var echo = new byte[RogProtocol.ReportLength];
                        span.CopyTo(echo);
                        _replies.Writer.TryWrite(echo);
                    }
                    else
                    {
                        var reply = RogProtocol.MouseStaticReport(3, new(81, Rgb.Black));
                        reply[1] = 0x12;
                        reply[2] = 3;
                        _replies.Writer.TryWrite(reply);
                    }
                }

                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var packet = await _replies.Reader.ReadAsync(ct).ConfigureAwait(false);
                packet.CopyTo(buffer.Span);
                return packet.Length;
            }

            public void Dispose() => _replies.Writer.TryComplete();
        }
    }
}
