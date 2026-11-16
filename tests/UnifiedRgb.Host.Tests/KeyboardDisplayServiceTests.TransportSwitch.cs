using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    private string SwitchJournal => Path.Combine(_directory, "peripherals", "keyboard-display-session.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_only_keyboard_startup_timeout_reopens_the_same_transport_without_replaying_output(bool rf)
    {
        var wired = new Factory { Available = !rf };
        wired.Io.StallFirstFirmware = !rf;
        var wireless = new WirelessFactory { Prepare = (open, io) => io.StallFirstFirmware = open == 1 };
        var originalUsb = wired.Io;
        using var service = Make(rf ? WirelessPinned() : Pinned(), wired, wirelessFactory: wireless);
        await service.StartAsync(default);
        await WaitAsync(service, "Running");
        var original = rf ? wireless.Channels[0].Inner : originalUsb;
        original.Mutations.Should().Be(0);
        original.NativeWrites.Should().Be(0);
        if (rf) wireless.Opens.Should().Be(2);
        else { wired.Opens.Should().Be(2); wireless.Opens.Should().Be(0); }
        await service.StopAsync(default);
    }

    [Fact]
    public void RF_checks_exact_USB_immediately_on_relevant_HID_notification_and_backs_off_periodic_checks()
    {
        var time = new ManualTimeProvider();
        var watcher = new TestHidWatcher();
        var probes = 0;
        var present = false;
        using var routes = new KeyboardTransportSwitch(time, () => { probes++; return present; }, watcher);
        routes.Observe().Should().BeFalse();
        time.Advance(TimeSpan.FromMilliseconds(200));
        watcher.Signal("hid#vid_0000&pid_0000");
        routes.Observe().Should().BeFalse();
        probes.Should().Be(1);
        present = true;
        watcher.Signal("hid#VID_0B05&PID_1C24");
        routes.Observe().Should().BeTrue();
        routes.Observe().Should().BeTrue();
        probes.Should().Be(2);
    }

    private sealed class TestHidWatcher : IHotplugSource
    {
        public event Action<DeviceInterfaceChange, string>? Changed;
        public void Signal(string link) => Changed?.Invoke(DeviceInterfaceChange.Arrived, link);
        public void Dispose() { }
    }

    private static byte[] WriteSwitchJournal(string path, string transport = "wired", byte brightness = 37, byte profile = 6)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = Key,
            FirmwareHeader = transport == "wired" ? "001200000024000100" : "0800070024000100", Brightness = brightness, ProfileToken = profile });
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    [Fact]
    public void Missing_USB_route_preserves_exact_recovery_before_selecting_RF_without_mutation()
    {
        var before = WriteSwitchJournal(SwitchJournal);
        var wired = new Factory { Available = false }; var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        using var selected = service.SelectConnection();
        selected.Should().NotBeNull(); selected!.ConnectionKind.Should().Be("wireless");
        File.Exists(SwitchJournal).Should().BeFalse();
        File.ReadAllBytes(SwitchJournal + ".deferred-wired").Should().Equal(before);
        wireless.Channels.Should().OnlyContain(io => io.Inner.Mutations == 0);
        wired.Io.Mutations.Should().Be(0);
    }

    [Fact]
    public void Returning_USB_reactivates_only_its_exact_record_before_any_wire_mutation()
    {
        var deferred = SwitchJournal + ".deferred-wired";
        var before = WriteSwitchJournal(deferred);
        var wired = new Factory(); var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        using var selected = service.SelectConnection();
        selected!.ConnectionKind.Should().Be("wired");
        File.ReadAllBytes(SwitchJournal).Should().Equal(before); File.Exists(deferred).Should().BeFalse();
        wired.Io.Mutations.Should().Be(0); wireless.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("schema")] [InlineData("profile")] [InlineData("identity")] [InlineData("brightness")]
    [InlineData("transport")] [InlineData("duplicate")]
    public void Invalid_or_duplicate_recovery_never_opens_an_alternate_route(string fault)
    {
        var path = SwitchJournal + ".deferred-wired";
        WriteSwitchJournal(path);
        if (fault == "duplicate") WriteSwitchJournal(SwitchJournal);
        else
        {
            var json = File.ReadAllText(path);
            json = fault switch
            {
                "schema" => json.Replace("\"Schema\":2", "\"Schema\":7", StringComparison.Ordinal),
                "profile" => json.Replace("\"ProfileToken\":6", "\"ProfileToken\":0", StringComparison.Ordinal),
                "identity" => json.Replace(Key, new string('B', 64), StringComparison.Ordinal),
                "brightness" => json.Replace("\"Brightness\":37", "\"Brightness\":101", StringComparison.Ordinal),
                _ => json.Replace("001200000024000100", "0800070024000100", StringComparison.Ordinal),
            };
            File.WriteAllText(path, json);
        }
        var before = File.ReadAllBytes(path);
        var wired = new Factory { Available = false }; var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        ((Action)(() => service.SelectConnection())).Should().Throw<Exception>().Where(ex => ex is IOException || ex is InvalidDataException);
        wired.Opens.Should().Be(0); wireless.Opens.Should().Be(0);
        File.ReadAllBytes(path).Should().Equal(before);
    }

    [Fact]
    public void Non_file_deferred_path_blocks_acquisition_instead_of_hiding_recovery_obligations()
    {
        Directory.CreateDirectory(SwitchJournal + ".deferred-wired");
        var wired = new Factory(); var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        ((Action)(() => service.SelectConnection())).Should().Throw<IOException>();
        KeyboardRecoveryJournals.Any(SwitchJournal).Should().BeTrue();
        wired.Opens.Should().Be(0); wireless.Opens.Should().Be(0);
    }

    [Fact]
    public void Failed_recorded_open_cannot_be_reinterpreted_as_absence_or_deferred()
    {
        var before = WriteSwitchJournal(SwitchJournal, "wireless");
        var wired = new Factory { OpenFailure = new IOException("USB open failed") }; var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        ((Action)(() => service.SelectConnection())).Should().Throw<Exception>().Where(ex => ex is IOException || ex is InvalidDataException);
        wireless.Opens.Should().Be(0); File.ReadAllBytes(SwitchJournal).Should().Equal(before);
        File.Exists(SwitchJournal + ".deferred-wireless").Should().BeFalse();
    }

    [Fact]
    public async Task RF_can_run_with_missing_USB_obligation_then_USB_restores_its_own_original()
    {
        var before = WriteSwitchJournal(SwitchJournal);
        var wired = new Factory { Available = false }; var wireless = new WirelessFactory();
        using (var rf = Make(BothPinned(), wired, wirelessFactory: wireless))
        {
            await rf.StartAsync(default); await WaitAsync(rf, "Running");
            (await rf.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            File.ReadAllBytes(SwitchJournal + ".deferred-wired").Should().Equal(before);
            await rf.StopAsync(default);
        }
        File.Exists(SwitchJournal).Should().BeFalse();
        wired.Available = true; wired.Io.Brightness = 100;
        using (var usb = Make(BothPinned(), wired, wirelessFactory: wireless))
        {
            await usb.StartAsync(default); await WaitAsync(usb, "Running");
            await usb.StopAsync(default);
        }
        wired.Io.Brightness.Should().Be(37);
        KeyboardRecoveryJournals.Any(SwitchJournal).Should().BeFalse();
    }

    [Fact]
    public async Task Running_USB_removal_joins_old_owner_and_automatically_connects_RF_with_its_own_snapshot()
    {
        var wired = new Factory(); var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        await service.StartAsync(default); await WaitAsync(service, "Running");
        var before = File.ReadAllBytes(SwitchJournal);
        wired.Available = false; wired.Io.Removed = true;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!wireless.Channels.Any(io => io.Inner.Data == 113)) await Task.Delay(20, bound.Token);
        (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        File.ReadAllBytes(SwitchJournal + ".deferred-wired").Should().Equal(before);
        using var json = JsonDocument.Parse(File.ReadAllBytes(SwitchJournal));
        json.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be("0800070024000100");
        await service.StopAsync(default);
    }

    [Fact]
    public async Task Maintenance_retains_absent_deferred_route_and_never_claims_release()
    {
        var deferred = SwitchJournal + ".deferred-wired"; var before = WriteSwitchJournal(deferred);
        using var service = Make(BothPinned(), new Factory { Available = false }, wirelessFactory: new WirelessFactory());
        await ((Func<Task>)(() => service.ReleaseForPeripheralConfigurationAsync(default))).Should().ThrowAsync<IOException>();
        File.ReadAllBytes(deferred).Should().Equal(before);
        File.Exists(SwitchJournal).Should().BeFalse();
    }

    [Fact]
    public async Task Bidirectional_USB_to_RF_and_RF_to_USB_switches_and_clean_stop_keep_per_key_writer_error_free()
    {
        static RogPerKeyMapOptions ReadyMap(string transport) => new()
        {
            Model = "synthetic",
            Transport = transport,
            Complete = true,
            Verified = true,
            Evidence = "synthetic test",
            KeyOrderVerified = true,
            Keys = RogAzothCandidateMap.For(transport).Map.Keys
                .Select(k => new RogPerKeyCellOptions { LedId = k.LedId, Key = k.Key, Row = k.Row, Column = k.Column }).ToArray(),
        };

        var perKey = new RogPerKeyOptions
        {
            Enabled = true,
            RfReportDelayMs = 0,
            Maps = [ReadyMap(RogPerKeyLighting.WiredTransport), ReadyMap(RogPerKeyLighting.WirelessTransport)],
        };
        var kbOptions = BothPinned() with { LightingVerified = true, WirelessLightingVerified = true };
        var hostOptions = new UnifiedRgbOptions
        {
            DataDirectory = _directory,
            KeyboardDisplay = kbOptions,
            AsusRog = new() { KeyboardPerKey = perKey },
        };
        var wired = new Factory();
        var wireless = new WirelessFactory();
        var bridge = new RogWiredLightingBridge();
        await using var services = new ServiceCollection().AddSingleton(bridge).AddSingleton(hostOptions).BuildServiceProvider();
        using var bus = new FrameBus();
        var guard = new ClearGuard(takeoverEnabled: true);
        await using var adapters = new AdapterHost([new WiredKeyboardAdapterFactory()], services, guard, bus,
            () => new HybridSleeper(), NullLogger<AdapterHost>.Instance, hostOptions);
        var clock = new ManualClock();
        using var engine = new LightingEngine(hostOptions, adapters, bus, EffectRegistry.Default, clock,
            new ProfileStore(hostOptions), new MountingStore(hostOptions), guard, new RgbNotifier(), NullLogger<LightingEngine>.Instance);
        using var service = Make(kbOptions, wired, bridge, wireless, wiredRoutePresent: _ => wired.Available, perKey: perKey);
        var controllerId = new ControllerId("asusr:wired:" + Key);

        await engine.StartAsync(default);
        using var pumpStop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            byte r = 1;
            while (!pumpStop.IsCancellationRequested)
            {
                if (adapters.RescanRequested) await engine.RescanAsync(pumpStop.Token);
                if (bus[controllerId] is { } mailbox)
                {
                    Array.Fill(mailbox.Back.Zones[0], new Rgb(r++, 20, 40));
                    bus.Publish(controllerId, mailbox.Back);
                }
                try { await Task.Delay(25, pumpStop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });

        try
        {
            await service.StartAsync(default);
            await UntilAsync(() => bridge.Available && bridge.KeyFrameTransport == RogPerKeyLighting.WiredTransport,
                "initial USB lighting owner");
            await UntilAsync(() => engine.BuildStatus(false).Devices.Single() is { State: UnifiedRgb.Contracts.Dto.ControllerStates.Active, Written: >= 2 },
                "USB per-key writes");
            wired.Io.NativeWrites.Should().BeGreaterThanOrEqualTo(2);
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull();

            // USB -> RF switch: even if USB disappears mid-write, AdapterHost + bridge transition cleanly to RF with LastError == null
            wired.Available = false;
            wired.Io.Removed = true;
            await UntilAsync(() => bridge.Available && bridge.KeyFrameTransport == RogPerKeyLighting.WirelessTransport,
                "USB->RF switch");
            await UntilAsync(() => !adapters.RescanRequested && engine.BuildStatus(false).Devices.Single() is
                { State: UnifiedRgb.Contracts.Dto.ControllerStates.Active, Written: >= 1, LastError: null }
                && wireless.Channels.Any(c => c.ColourPackets > 0), "RF per-key writes after switch");
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();

            // RF -> USB switch: KeyboardTransportSwitch retires RF and reopens USB with LastError == null
            var opensBeforeReturn = wired.Opens;
            var ioBeforeReturn = wired.Io;
            wired.Available = true;
            await UntilAsync(() => bridge.Available && bridge.KeyFrameTransport == RogPerKeyLighting.WiredTransport
                && wired.Opens > opensBeforeReturn && !ReferenceEquals(wired.Io, ioBeforeReturn),
                "RF->USB switch");
            await UntilAsync(() => !adapters.RescanRequested && engine.BuildStatus(false).Devices.Single() is
                { State: UnifiedRgb.Contracts.Dto.ControllerStates.Active, Written: >= 1, LastError: null }
                && wired.Io.NativeWrites >= 1, "USB per-key writes after switch");
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();

            // Clean stop suppresses not-ready errors and leaves LastError == null both before and after rescan
            await service.StopAsync(default);
            await Task.Delay(80);
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull();
            await engine.RescanAsync(default);
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull();
        }
        finally
        {
            pumpStop.Cancel();
            await pump;
            await service.StopAsync(default);
            await engine.StopAsync(default);
        }
    }

    [Fact]
    public async Task AdapterHost_rescan_clears_stale_not_ready_lastError_after_fault_on_switch_both_ways_and_clean_stop()
    {
        static RogPerKeyMapOptions ReadyMap(string transport) => new()
        {
            Model = "synthetic",
            Transport = transport,
            Complete = true,
            Verified = true,
            Evidence = "synthetic test",
            KeyOrderVerified = true,
            Keys = RogAzothCandidateMap.For(transport).Map.Keys
                .Select(k => new RogPerKeyCellOptions { LedId = k.LedId, Key = k.Key, Row = k.Row, Column = k.Column }).ToArray(),
        };

        var perKey = new RogPerKeyOptions
        {
            Enabled = true,
            RfReportDelayMs = 0,
            Maps = [ReadyMap(RogPerKeyLighting.WiredTransport), ReadyMap(RogPerKeyLighting.WirelessTransport)],
        };
        var options = new UnifiedRgbOptions
        {
            DataDirectory = _directory,
            KeyboardDisplay = BothPinned() with { LightingVerified = true, WirelessLightingVerified = true },
            AsusRog = new() { KeyboardPerKey = perKey },
        };
        var bridge = new RogWiredLightingBridge();
        await using var services = new ServiceCollection().AddSingleton(bridge).AddSingleton(options).BuildServiceProvider();
        using var bus = new FrameBus();
        var guard = new ClearGuard(takeoverEnabled: true);
        await using var adapters = new AdapterHost([new WiredKeyboardAdapterFactory()], services, guard, bus,
            () => new HybridSleeper(), NullLogger<AdapterHost>.Instance, options);
        var clock = new ManualClock();
        using var engine = new LightingEngine(options, adapters, bus, EffectRegistry.Default, clock,
            new ProfileStore(options), new MountingStore(options), guard, new RgbNotifier(), NullLogger<LightingEngine>.Instance);
        await engine.StartAsync(default);
        try
        {
            var entry = adapters.Controllers.Should().ContainSingle().Subject;
            entry.Descriptor.Name.Should().Be("ROG 夜魔 X（逐键）");

            // 1. USB active
            var usbOwner = bridge.Attach(RogPerKeyLighting.WiredTransport);
            await adapters.RescanAsync(default);
            entry.Writer.Should().NotBeNull();
            var usbServe = Task.Run(async () =>
            {
                while (!usbOwner.HasWork) await Task.Delay(5);
                usbOwner.TryTakeKeys(out var work).Should().BeTrue();
                work!.Transport.Should().Be(RogPerKeyLighting.WiredTransport);
                work.Completion.TrySetResult();
            });
            PublishOne();
            await usbServe.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => engine.BuildStatus(false).Devices.Single().Written >= 1, "USB write counted");
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull();

            // 2. Real fault latches "Keyboard USB owner is not ready." on Writer
            bridge.Detach(usbOwner, RogLightingDetachReason.Fault);
            PublishOne();
            await UntilAsync(() => engine.BuildStatus(false).Devices.Single().LastError == "Keyboard USB owner is not ready.",
                "fault latched onto DeviceWriter");

            // 3. USB -> RF switch: rescan detaches the faulted writer and reopens cleanly on RF with LastError == null
            await adapters.RescanAsync(default);
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull("detach clears stale writer error");
            var rfOwner = bridge.Attach(RogPerKeyLighting.WirelessTransport);
            await adapters.RescanAsync(default);
            var rfServe = Task.Run(async () =>
            {
                while (!rfOwner.HasWork) await Task.Delay(5);
                rfOwner.TryTakeKeys(out var work).Should().BeTrue();
                work!.Transport.Should().Be(RogPerKeyLighting.WirelessTransport);
                work.Completion.TrySetResult();
            });
            PublishOne();
            await rfServe.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => engine.BuildStatus(false).Devices.Single().Written >= 1, "RF write counted");
            engine.BuildStatus(false).Devices.Single().Should().Match<UnifiedRgb.Contracts.Dto.DeviceStatusDto>(
                d => d.State == UnifiedRgb.Contracts.Dto.ControllerStates.Active && d.Dropped == 0 && d.LastError == null);

            // 4. Real fault on RF -> RF -> USB switch clears stale error again
            bridge.Detach(rfOwner, RogLightingDetachReason.Fault);
            PublishOne();
            await UntilAsync(() => engine.BuildStatus(false).Devices.Single().LastError == "Keyboard USB owner is not ready.",
                "RF fault latched onto DeviceWriter");
            var usbAgain = bridge.Attach(RogPerKeyLighting.WiredTransport);
            await adapters.RescanAsync(default);
            var usbAgainServe = Task.Run(async () =>
            {
                while (!usbAgain.HasWork) await Task.Delay(5);
                usbAgain.TryTakeKeys(out var work).Should().BeTrue();
                work!.Transport.Should().Be(RogPerKeyLighting.WiredTransport);
                work.Completion.TrySetResult();
            });
            PublishOne();
            await usbAgainServe.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => engine.BuildStatus(false).Devices.Single().Written >= 1, "USB reopen write counted");
            engine.BuildStatus(false).Devices.Single().Should().Match<UnifiedRgb.Contracts.Dto.DeviceStatusDto>(
                d => d.State == UnifiedRgb.Contracts.Dto.ControllerStates.Active && d.Dropped == 0 && d.LastError == null);

            // 5. Clean stop leaves LastError null
            bridge.Detach(usbAgain, RogLightingDetachReason.CleanStop);
            PublishOne();
            await Task.Delay(60);
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull();
            await adapters.RescanAsync(default);
            engine.BuildStatus(false).Devices.Single().LastError.Should().BeNull();
        }
        finally
        {
            await engine.StopAsync(default);
        }

        void PublishOne()
        {
            var box = bus[new("asusr:wired:" + Key)]!;
            Array.Fill(box.Back.Zones[0], new Rgb(12, 34, 56));
            bus.Publish(box.Controller.Id, box.Back);
        }
    }
}
