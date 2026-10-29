using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Devices.Virtual;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

// RF cold start of the shared keyboard LIGHTING owner with the status screen off and no UI request. The actual KeyboardDisplayService,
// RogWiredLightingBridge, WiredKeyboardAdapterFactory, AdapterHost, LightingEngine, RgbService, GuardMonitor and (handoff case)
// PeripheralConfigurationService/RogPeripheralConfigurationResources are wired as in HostComposition (default virtual adapter as the case
// stand-in), over the existing OLED/HID fakes and a manual render clock. Devices stay opaque: fake counters and the fake's last native
// lighting state are compared, never decoded.
public sealed partial class KeyboardDisplayServiceTests
{
    private const string RfKeyboard = "asusr:wired:" + Key;
    private readonly Xunit.Abstractions.ITestOutputHelper? _output;

    public KeyboardDisplayServiceTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Rf_cold_start_with_the_screen_off_attaches_the_shared_lighting_owner_and_renders_the_restored_scene_without_any_ui_request()
    {
        var wireless = new WirelessFactory();
        await using var rig = new RfRig(_directory, wireless, guardRescanSeconds: 5);
        rig.SaveStartupScene("#FF0000");
        await rig.StartEngineAsync();
        // Host order: the engine scans before the keyboard service runs, so the lighting entry waits for the shared owner.
        var waiting = rig.Adapters.Controllers.Should().ContainSingle(c => c.Descriptor.Id.Value == RfKeyboard).Subject;
        waiting.IsActive.Should().BeFalse();
        waiting.Verdict.Writer.Should().Be("Keyboard USB owner is connecting.");
        rig.Engine.Loop.Controllers.Should().NotBeEmpty().And.NotContain(c => c.Id.Value == RfKeyboard);

        await rig.StartServicesAsync();
        await UntilAsync(() => rig.KeyboardRenders, "the RF keyboard to enter the render inventory");
        rig.Bridge.Available.Should().BeTrue();
        rig.Hotplugs.Should().Contain(new HotplugEvent(new(RfKeyboard), HotplugKind.Arrived));
        var channel = wireless.Channels.Should().ContainSingle().Subject;
        var restored = await rig.ShowAsync(channel); // the first tick carries the active.json colour, with no scene call
        await rig.PaintAsync("#0000FF");
        (await rig.ShowAsync(channel)).SameContent(restored).Should().BeFalse();
        await rig.PaintAsync("#FF0000");
        (await rig.ShowAsync(channel)).SameContent(restored).Should().BeTrue();

        var status = await rig.Keyboard.GetKeyboardDisplayStatusAsync();
        _output?.WriteLine($"cold start: {status}");
        status.Enabled.Should().BeFalse(); status.State.Should().Be("Stopped"); status.LastError.Should().BeNull();
        status.FramesAcknowledged.Should().Be(0);
        wireless.Opens.Should().Be(1);
        channel.Inner.Data.Should().Be(0); channel.Inner.BeginPackets.Should().Be(0);
        channel.Inner.Brightness.Should().Be(25); channel.Inner.BrightnessWrites.Should().OnlyContain(b => b == 25);
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display.json")).Should().BeFalse("no UI toggle was made or persisted");
        rig.Screens.ScreenMutations.Should().Be(0);

        await rig.StopAsync();
        rig.Bridge.Available.Should().BeFalse();
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("reassert-disabled")]
    [InlineData("peripheral-handoff")]
    public async Task Rf_startup_recovery_failure_stays_sticky_without_resend_until_an_explicit_lifecycle_recovery_reattaches_lighting(string recovery)
    {
        var (journal, journalBytes) = WriteRfJournal();
        WirelessFactory? wireless = null;
        wireless = new WirelessFactory
        {
            // An interrupted session left status brightness on the device and the first restoration write fails with an unknown
            // outcome. Each later channel sees the brightness the fake device kept.
            Prepare = (open, io) =>
            {
                io.Brightness = open == 1 ? (byte)100 : wireless!.Channels[^1].Inner.Brightness;
                io.FailRestoreBrightness = open == 1;
            },
        };
        await using var rig = new RfRig(_directory, wireless, guardRescanSeconds: 1);
        rig.SaveStartupScene("#FF0000");
        await rig.StartEngineAsync();
        await rig.StartServicesAsync();

        await WaitAsync(rig.Keyboard, "Error");
        var failed = await rig.Keyboard.GetKeyboardDisplayStatusAsync();
        _output?.WriteLine($"sticky: {failed}; lighting entry: {rig.Entry?.Verdict}");
        failed.Enabled.Should().BeFalse(); failed.LastError.Should().NotBeNullOrEmpty();
        var first = wireless.Channels.Should().ContainSingle().Subject.Inner;
        var (mutations, natives) = (first.Mutations, first.NativeWrites);
        first.Brightness.Should().Be(100, "a failed restoration is neither reported nor replayed as success");
        var passes = rig.Guard.VirtualChecks;
        await UntilAsync(() => rig.Guard.VirtualChecks >= passes + 2, "two GuardMonitor rechecks during the sticky fault");
        await rig.Engine.RescanAsync(default);
        wireless.Opens.Should().Be(1);
        first.Mutations.Should().Be(mutations); first.NativeWrites.Should().Be(natives); first.BrightnessWrites.Should().BeEmpty();
        File.ReadAllBytes(journal).Should().Equal(journalBytes);
        (await rig.Keyboard.GetKeyboardDisplayStatusAsync()).Should().Be(failed);
        rig.Bridge.Available.Should().BeFalse();
        rig.Entry!.IsActive.Should().BeFalse();
        rig.Engine.Loop.Controllers.Should().NotBeEmpty().And.NotContain(c => c.Id.Value == RfKeyboard);

        if (recovery == "reassert-disabled") await rig.Keyboard.SetKeyboardDisplayEnabledAsync(false);
        else
        {
            var entered = await rig.Configuration.EnterPeripheralConfigurationAsync();
            entered.State.Should().Be("Ready", entered.LastError);
            File.Exists(journal).Should().BeFalse("Enter restores the journalled native brightness on the recorded transport");
            var exited = await rig.Configuration.ExitPeripheralConfigurationAsync();
            exited.State.Should().Be("Active", exited.LastError);
        }
        await UntilAsync(() => rig.KeyboardRenders, "the recovered RF keyboard to re-enter the render inventory");
        var recovered = await rig.Keyboard.GetKeyboardDisplayStatusAsync();
        _output?.WriteLine($"{recovery}: {recovered}; opens {wireless.Opens}");
        recovered.Enabled.Should().BeFalse(); recovered.State.Should().Be("Stopped"); recovered.LastError.Should().BeNull();
        rig.Bridge.Available.Should().BeTrue();
        wireless.Opens.Should().Be(recovery == "reassert-disabled" ? 2 : 3);
        var fresh = wireless.Channels[^1];
        var restored = await rig.ShowAsync(fresh);
        await rig.PaintAsync("#0000FF");
        (await rig.ShowAsync(fresh)).SameContent(restored).Should().BeFalse();
        await rig.PaintAsync("#FF0000");
        (await rig.ShowAsync(fresh)).SameContent(restored).Should().BeTrue();
        first.Mutations.Should().Be(mutations); first.NativeWrites.Should().Be(natives);

        wireless.Channels.Should().OnlyContain(c => c.Inner.Data == 0 && c.Inner.BeginPackets == 0);
        wireless.Channels.SelectMany(c => c.Inner.BrightnessWrites).Should().NotBeEmpty().And.OnlyContain(b => b == 25);
        fresh.Inner.Brightness.Should().Be(25);
        using (var preference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_directory, "peripherals", "keyboard-native-display.json"))))
            preference.RootElement.GetProperty("Brightness").GetByte().Should().Be(25);
        var settings = Path.Combine(_directory, "peripherals", "keyboard-display.json");
        if (recovery == "reassert-disabled")
        {
            using var saved = JsonDocument.Parse(File.ReadAllBytes(settings));
            saved.RootElement.GetProperty("Enabled").GetBoolean().Should().BeFalse();
        }
        else File.Exists(settings).Should().BeFalse();
        rig.Screens.ScreenMutations.Should().Be(0);

        await rig.StopAsync();
        rig.Bridge.Available.Should().BeFalse();
        File.Exists(journal).Should().BeFalse();
    }

    // A receiver whose firmware differs from the configured pin (e.g. after an owner-side update) is a deliberate version gate,
    // not a lifecycle fault: neither explicit lifecycle action adopts the new header, replays the journal or attaches lighting.
    [Theory]
    [InlineData("reassert-disabled")]
    [InlineData("peripheral-handoff")]
    public async Task Rf_cold_start_with_changed_firmware_is_a_deliberate_version_rejection_that_no_lifecycle_recovery_bypasses(string recovery)
    {
        var (journal, journalBytes) = WriteRfJournal();
        var wireless = new WirelessFactory { BadFirmwareEveryOpen = true };
        await using var rig = new RfRig(_directory, wireless, guardRescanSeconds: 1);
        rig.SaveStartupScene("#FF0000");
        await rig.StartEngineAsync();
        await rig.StartServicesAsync();

        await WaitAsync(rig.Keyboard, "Error");
        var rejected = await rig.Keyboard.GetKeyboardDisplayStatusAsync();
        _output?.WriteLine($"version gate: {rejected}");
        rejected.Enabled.Should().BeFalse(); rejected.LastError.Should().NotBeNullOrEmpty();
        if (recovery == "reassert-disabled")
        {
            await rig.Keyboard.SetKeyboardDisplayEnabledAsync(false);
            await UntilAsync(() => wireless.Opens == 2, "the explicit re-attempt");
            await WaitAsync(rig.Keyboard, "Error");
            _output?.WriteLine($"{recovery}: {await rig.Keyboard.GetKeyboardDisplayStatusAsync()}");
        }
        else
        {
            var entered = await rig.Configuration.EnterPeripheralConfigurationAsync();
            _output?.WriteLine($"enter: {entered}");
            entered.State.Should().Be("Error"); entered.ConfigurationMode.Should().BeTrue();
            var exited = await rig.Configuration.ExitPeripheralConfigurationAsync();
            _output?.WriteLine($"exit: {exited}");
            exited.State.Should().Be("Error", "a retained journal refuses normal resume");
        }
        var passes = rig.Guard.VirtualChecks;
        await UntilAsync(() => rig.Guard.VirtualChecks >= passes + 2, "two GuardMonitor rechecks after the explicit attempt");
        wireless.Opens.Should().Be(2);
        wireless.Channels.Should().OnlyContain(c => c.Inner.Mutations == 0 && c.Inner.NativeWrites == 0);
        File.ReadAllBytes(journal).Should().Equal(journalBytes);
        Directory.GetFiles(Path.GetDirectoryName(journal)!, "keyboard-*").Select(f => Path.GetFileName(f)).Should().BeEquivalentTo(
            recovery == "reassert-disabled" ? new[] { "keyboard-display-session.json", "keyboard-display.json" } : new[] { "keyboard-display-session.json" });
        rig.Bridge.Available.Should().BeFalse();
        (rig.Entry is { IsActive: true }).Should().BeFalse();
        rig.Engine.Loop.Controllers.Should().NotBeEmpty().And.NotContain(c => c.Id.Value == RfKeyboard);
        rig.Screens.ScreenMutations.Should().Be(0);

        await rig.StopAsync();
        File.ReadAllBytes(journal).Should().Equal(journalBytes);
    }

    private (string Path, byte[] Bytes) WriteRfJournal()
    {
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = Key, FirmwareHeader = "0800070024000100",
            Brightness = (byte)25, ProfileToken = (byte)6 });
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            try { await Task.Delay(20, bound.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("Timed out waiting for " + what + "."); }
        }
    }

    private sealed class RfRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private bool _engine, _hosted;

        public RfRig(string directory, WirelessFactory wireless, int guardRescanSeconds)
        {
            Options = new UnifiedRgbOptions { UseVirtualAdapter = true, DataDirectory = directory, GuardRescanSeconds = guardRescanSeconds,
                KeyboardDisplay = WirelessPinned() with { Enabled = false, WirelessLightingVerified = true } };
            _services = new ServiceCollection().AddSingleton(Bridge).AddSingleton(Options).BuildServiceProvider();
            Adapters = new AdapterHost([new VirtualAdapterFactory(), new WiredKeyboardAdapterFactory()], _services, Guard, Bus, () => new HybridSleeper(),
                NullLogger<AdapterHost>.Instance, Options, Gate);
            Adapters.AdapterCreated += adapter => adapter.Hotplug += Hotplugs.Enqueue;
            var profiles = new ProfileStore(Options);
            var notifier = new RgbNotifier();
            Engine = new LightingEngine(Options, Adapters, Bus, EffectRegistry.Default, Clock, profiles, new MountingStore(Options), Guard,
                notifier, NullLogger<LightingEngine>.Instance);
            Rgb = new RgbService(Engine, profiles, Guard, notifier);
            var screens = DispatchProxy.Create<IScreenService, ReadProxy>();
            Screens = (ReadProxy)screens;
            Keyboard = new KeyboardDisplayService(Options, Rgb, screens, new Factory(), Guard, Bridge, wireless, Gate);
            Monitor = new GuardMonitor(Options, Engine, NullLogger<GuardMonitor>.Instance);
            Configuration = new PeripheralConfigurationService(Options, Gate, new RogPeripheralConfigurationResources(Engine, Keyboard));
        }

        public UnifiedRgbOptions Options { get; }
        public RogWiredLightingBridge Bridge { get; } = new();
        public CountingGuard Guard { get; } = new();
        public ManualClock Clock { get; } = new();
        public FrameBus Bus { get; } = new();
        public PeripheralConfigurationGate Gate { get; } = new(new PeripheralConfigurationMarker.LoadResult(false, null));
        public ConcurrentQueue<HotplugEvent> Hotplugs { get; } = new();
        public AdapterHost Adapters { get; }
        public LightingEngine Engine { get; }
        public RgbService Rgb { get; }
        public ReadProxy Screens { get; }
        public KeyboardDisplayService Keyboard { get; }
        public GuardMonitor Monitor { get; }
        public PeripheralConfigurationService Configuration { get; }
        public ControllerEntry? Entry => Adapters.Controllers.SingleOrDefault(c => c.Descriptor.Id.Value == RfKeyboard);
        public bool KeyboardRenders => Entry is { IsActive: true } && Engine.Loop.Controllers.Any(c => c.Id.Value == RfKeyboard);

        public void SaveStartupScene(string hex) => new ProfileStore(Options).SaveActive(new(LightingEngine.CustomProfileName, Scene(hex)));

        public async Task StartEngineAsync() { _engine = true; await Engine.StartAsync(default); }

        // HostComposition hosted-service order after the engine: GuardMonitor, then (status/package/screen services) KeyboardDisplayService.
        public async Task StartServicesAsync() { _hosted = true; await Monitor.StartAsync(default); await Keyboard.StartAsync(default); }

        public Task PaintAsync(string hex) => Rgb.SetSceneAsync(Scene(hex));

        // One tick of the render clock; returns the fake device's lighting state once the keyboard writer completed that frame.
        public async Task<RogKeyboardNativeState> ShowAsync(WirelessIo channel)
        {
            var writer = Entry!.Writer!;
            var before = writer.GetStats();
            var writes = channel.Inner.NativeWrites;
            Clock.Advance();
            await UntilAsync(() => writer.GetStats() is var s && (s.Written > before.Written || s.Errors > before.Errors), "the keyboard writer to take the frame");
            var after = writer.GetStats();
            after.Errors.Should().Be(before.Errors, after.LastError);
            channel.Inner.NativeWrites.Should().BeGreaterThan(writes);
            return channel.Inner.Native;
        }

        public async Task StopAsync()
        {
            if (_hosted) { _hosted = false; await Keyboard.StopAsync(default); await Monitor.StopAsync(default); }
            if (_engine) { _engine = false; await Engine.StopAsync(default); }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Keyboard.Dispose(); Monitor.Dispose(); Engine.Dispose(); Bus.Dispose(); Gate.Dispose();
            await _services.DisposeAsync();
        }

        private static SceneDto Scene(string hex) => new([new([new(RfKeyboard, 0)], "StaticColor", [hex])]);
    }

    private sealed class CountingGuard : IWriterGuard
    {
        private int _virtualChecks;
        // One per GuardMonitor recheck pass: the always-present virtual (case stand-in) adapter is checked even while ROG is released.
        public int VirtualChecks => Volatile.Read(ref _virtualChecks);
        public GuardVerdict Check(DeviceClass deviceClass)
        {
            if (deviceClass == DeviceClass.Virtual) Interlocked.Increment(ref _virtualChecks);
            return GuardVerdict.Clear;
        }
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => deviceClass == DeviceClass.AsusRog;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ManualClock : IClock
    {
        private bool _running;
        public double Now { get; private set; }
        public long Frame { get; private set; }
        public event Action<double, double>? Tick;
        public void Start(int fps) => _running = true;
        public void Stop() => _running = false;
        public void Advance()
        {
            if (!_running) throw new InvalidOperationException("The engine has not started the render clock.");
            Frame++; Now = Frame / 60.0; Tick?.Invoke(Now, 1 / 60.0);
        }
    }
}
