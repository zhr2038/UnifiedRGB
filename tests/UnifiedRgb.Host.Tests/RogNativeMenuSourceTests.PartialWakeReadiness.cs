using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData("selector-zero")] [InlineData("content-zero")]
    [InlineData("selector-negative")] [InlineData("content-negative")]
    public async Task Actual_RF_fresh_owner_partial_wake_waits_without_mutating_journal_then_automatically_restores(string stage)
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-startup-readiness-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); ulong cookie = Cookie; var browses = new List<int>();
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay, browses.Add);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels,
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        var budget = typeof(KeyboardDisplayService).GetField("_observationReplacements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count >= 1, time);
            var before = File.ReadAllBytes(journal); var original = routes.Ports[^1];
            routes.RadioRefuseNextCap = true; time.Advance(TimeSpan.FromMilliseconds(101));
            await SwitchUntil(() => original.Disposed && inputs.LiveHandles == 0, time);
            routes.RadioPartialStartup = stage; routes.RadioPeerUnavailable = false;
            await SwitchUntil(() => routes.Ports[^1].PartialStartupAtWriteCount != null && routes.Ports.All(p => p.Disposed)
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting", time);
            var partial = routes.Ports[^1];
            partial.Requests.Should().HaveCount(partial.PartialStartupAtWriteCount!.Value);
            partial.Requests.Should().OnlyContain(p => p[1] == 0x12 || p[1] == 0x21 || p[1] == 0x23);
            partial.Requests.Should().HaveCount(stage.StartsWith("selector", StringComparison.Ordinal) ? 2 : 3);
            File.ReadAllBytes(journal).Should().Equal(before); inputs.LiveHandles.Should().Be(0);
            ((int)budget.GetValue(owner)!).Should().Be(1);
            var images = routes.Radio.Images.Count; routes.RadioPartialStartup = null;
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count > images, time);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            await WriteRouteKeys(bridge, 2); await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);
            ((int)budget.GetValue(owner)!).Should().Be(1);
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse(); state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioPartialStartup = null; routes.RadioPeerUnavailable = false;
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task Actual_RF_fresh_positive_wrong_profile_cannot_restore_another_profile_journal_or_wait_as_startup()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-wrong-profile-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); ulong cookie = Cookie;
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels,
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count >= 1, time);
            var before = File.ReadAllBytes(journal); var old = routes.Ports[^1];
            routes.RadioRefuseNextCap = true; time.Advance(TimeSpan.FromMilliseconds(101));
            await SwitchUntil(() => old.Disposed && inputs.LiveHandles == 0, time);
            routes.RadioActiveProfileToken = 5; routes.RadioPeerUnavailable = false;
            await SwitchUntil(() => routes.Ports.Count >= 2 && routes.Ports.All(p => p.Disposed)
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error", time);
            var last = routes.Ports[^1]; last.Requests.Should().OnlyContain(p => p[1] == 0x12 || p[1] == 0x21 || p[1] == 0x23);
            File.ReadAllBytes(journal).Should().Equal(before);
            var opens = routes.Ports.Count; routes.RadioActiveProfileToken = null;
            time.Advance(TimeSpan.FromSeconds(2)); await Task.Delay(1100);
            routes.Ports.Should().HaveCount(opens); (await owner.GetKeyboardDisplayStatusAsync()).State.Should().Be("Error");
            state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioPeerUnavailable = false; routes.RadioActiveProfileToken = null;
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task Actual_RF_setter_readback_NACK_is_terminal_after_mutation_marker()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        using var connection = routes.TryOpen(Policy(2).DeviceKey, Policy(2).FirmwareHeader)!;
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(connection.FirmwareHeader, default); session.MutationAdmitted.Should().BeFalse();
        routes.RadioRefuseBrightnessReadbackAfterSetter = true;
        var error = await ((Func<Task>)(() => session.StartOutputAsync(100, default))).Should().ThrowAsync<IOException>();
        error.Which.Should().NotBeOfType<RogKeyboardStartupNotReadyException>();
        session.MutationAdmitted.Should().BeTrue(); session.Touched.Should().BeTrue();
        var writes = routes.Radio.Requests.Count;
        var repeated = await ((Func<Task>)(() => session.SnapshotAsync(connection.FirmwareHeader, default))).Should().ThrowAsync<IOException>();
        repeated.Which.Should().NotBeOfType<RogKeyboardStartupNotReadyException>();
        routes.Radio.Requests.Should().HaveCount(writes);
    }

    [Fact]
    public async Task Actual_RF_empty_peer_after_admitted_mutation_is_not_startup_not_ready()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        using var connection = routes.TryOpen(Policy(2).DeviceKey, Policy(2).FirmwareHeader)!;
        var session = new RogWiredOledSession(connection);
        await session.ExchangeAsync(RogWiredOledProtocol.Preset(3), true, default); session.MutationAdmitted.Should().BeTrue();
        routes.RadioPeerUnavailable = true;
        await ((Func<Task>)(() => session.SnapshotAsync(connection.FirmwareHeader, default))).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Actual_RF_unknown_initial_brightness_stays_terminal_before_mutation()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true) { RadioStartupBrightness = 255 };
        using var connection = routes.TryOpen(Policy(2).DeviceKey, Policy(2).FirmwareHeader)!;
        var session = new RogWiredOledSession(connection);
        await ((Func<Task>)(() => session.SnapshotAsync(connection.FirmwareHeader, default))).Should().ThrowAsync<InvalidDataException>();
        session.MutationAdmitted.Should().BeFalse(); routes.Radio.Requests.Should().OnlyContain(p => p[1] == 0x12 || p[1] == 0x21 || p[1] == 0x23);
    }

    [Theory]
    [InlineData((byte)0x21, (byte)0)] [InlineData((byte)0x23, (byte)2)]
    public async Task Actual_USB_unsupported_snapshot_values_do_not_use_RF_startup_readiness(byte command, byte subcommand)
    {
        using var model = new CodexWireChannel(1); using var channel = new WiredUnsupportedSnapshotChannel(model, command, subcommand);
        using var connection = new RogWiredOledConnection(channel, () => true, () => Policy(1).DeviceKey, () => Policy(1).FirmwareHeader);
        var session = new RogWiredOledSession(connection);
        await ((Func<Task>)(() => session.SnapshotAsync(connection.FirmwareHeader, default))).Should().ThrowAsync<InvalidDataException>();
        session.MutationAdmitted.Should().BeFalse(); model.Requests.Should().OnlyContain(p => p[1] == 0x12 || p[1] == 0x21 || p[1] == 0x23);
    }

    private sealed class WiredUnsupportedSnapshotChannel(CodexWireChannel inner, byte command, byte subcommand) : IHidReportChannel
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) => inner.WriteAsync(report, ct);
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var length = await inner.ReadAsync(buffer, ct);
            if (buffer.Span[1] == command && buffer.Span[2] == subcommand) buffer.Span[5] = 0;
            return length;
        }
        public void Dispose() => inner.Dispose();
    }
}
