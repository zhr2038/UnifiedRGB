using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task Actual_RF_standalone_CAP_NACK_then_empty_fresh_snapshot_waits_and_wakes_without_replaying_old_owner()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-cap-nack-" + Guid.NewGuid().ToString("N"));
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
            await SwitchUntil(() => bridge.Available && routes.Radio.Images.Count >= 1 && source.DedicatedPageActive, time);
            await WriteRouteKeys(bridge, 2);
            var before = File.ReadAllBytes(journal); var original = routes.Ports[^1]; var opens = routes.Ports.Count;
            routes.RadioRefuseNextCap = true; time.Advance(TimeSpan.FromMilliseconds(101));
            await SwitchUntil(() => original.Disposed && routes.Ports.Count > opens && routes.Ports[^1].Disposed
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting", time);
            original.RefusedCapAtWriteCount.Should().NotBeNull();
            original.Requests.Should().HaveCount(original.RefusedCapAtWriteCount!.Value);
            Convert.ToHexString(original.Requests[^1].AsSpan(0, 5)).Should().Be("02127D0000");
            routes.Ports.Skip(opens).Should().OnlyContain(p => p.Requests.Count == 1
                && p.Requests[0][1] == 0x12 && p.Requests[0][2] == 0);
            File.ReadAllBytes(journal).Should().Equal(before); inputs.LiveHandles.Should().Be(0);
            ((int)budget.GetValue(owner)!).Should().Be(1, "the standalone CAP refusal consumes exactly one existing bounded replacement attempt");

            var images = routes.Radio.Images.Count; routes.RadioPeerUnavailable = false;
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count > images, time);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            await WriteRouteKeys(bridge, 2); await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);
            ((int)budget.GetValue(owner)!).Should().Be(1);
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse();
            routes.Ports.Should().OnlyContain(p => p.MaximumReaders <= 1); state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioPeerUnavailable = false; await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task Actual_RF_post_pixel_CAP_transport_NACK_retires_before_fresh_snapshot_and_keeps_READY_barrier()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true) { RadioRefuseCommitCap = true };
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-commit-cap-nack-" + Guid.NewGuid().ToString("N"));
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
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => routes.Ports.Count > 1 && routes.Ports[0].Disposed && routes.Ports[^1].Disposed
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting", time);
            routes.Radio.Images.Should().ContainSingle(); routes.Radio.Renewals.Should().Be(0);
            var original = routes.Ports[0]; original.RefusedCapAtWriteCount.Should().NotBeNull();
            original.Requests.Should().HaveCount(original.RefusedCapAtWriteCount!.Value);
            routes.Ports.Skip(1).Should().OnlyContain(p => p.Requests.Count == 1 && p.Requests[0][1] == 0x12 && p.Requests[0][2] == 0);
            routes.RadioPeerUnavailable = false;
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count >= 2, time);
            routes.Radio.Renewals.Should().BeGreaterThan(0);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioPeerUnavailable = false; await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task Actual_RF_cold_start_receiver_without_peer_waits_past_three_attempts_then_automatically_owns_Codex_and_RGB()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true) { RadioPeerUnavailable = true };
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-cold-empty-peer-" + Guid.NewGuid().ToString("N"));
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
        var peripherals = Path.Combine(data, "peripherals");
        var budget = typeof(KeyboardDisplayService).GetField("_observationReplacements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => routes.Ports.Count >= 4 && routes.Ports.All(p => p.Disposed)
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting", time);
            routes.Ports.Should().OnlyContain(p => p.Transport == 2 && p.Requests.Count == 1
                && p.Requests[0][1] == 0x12 && p.Requests[0][2] == 0);
            inputs.LiveHandles.Should().Be(0); bridge.Available.Should().BeFalse();
            File.Exists(Path.Combine(peripherals, "keyboard-display-session.json")).Should().BeFalse();
            File.Exists(Path.Combine(peripherals, "keyboard-native-display.json")).Should().BeFalse();
            File.Exists(Path.Combine(peripherals, "keyboard-display.json")).Should().BeFalse();
            ((int)budget.GetValue(owner)!).Should().Be(0);

            // Host remains the original instance; only receiver peer identity becomes present.
            routes.RadioPeerUnavailable = false;
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && source.DedicatedPageActive
                && routes.Radio.Images.Count > 0, time);
            routes.Radio.Images[^1].Should().Equal(Pixels());
            await WriteRouteKeys(bridge, 2); await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            File.Exists(Path.Combine(peripherals, "keyboard-display-session.json")).Should().BeTrue();
            File.Exists(Path.Combine(peripherals, "keyboard-display.json")).Should().BeFalse();
            ((int)budget.GetValue(owner)!).Should().Be(0);
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse();
            state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioPeerUnavailable = false; await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task Actual_RF_empty_peer_retires_established_owner_waits_snapshot_only_and_wakes_twice_without_budget()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: false);
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-sleep-wake-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); ulong cookie = Cookie; var browses = new List<int>();
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay, browses.Add);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var scene = JsonSerializer.Serialize(state.Scene);
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
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available, time);
            await WriteRouteKeys(bridge, 2);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var before = File.ReadAllBytes(journal);
                var failedPort = routes.Ports[^1];
                var portsAtSleep = routes.Ports.Count; var emptyBefore = routes.EmptyPeerReplies;
                routes.RadioPeerUnavailable = true;
                if (cycle == 1)
                {
                    // Established Mode10 is serviced through its shared per-key preflight, rather than an idle GET.
                    await ((Func<Task>)(() => WriteRouteKeys(bridge, 2))).Should().ThrowAsync<IOException>();
                }
                await SwitchUntil(() => failedPort.Disposed && inputs.LiveHandles == 0 && routes.EmptyPeerReplies > emptyBefore, time);
                await SwitchUntil(() => routes.Ports.Count > portsAtSleep && routes.Ports[^1].Disposed, time);
                routes.EmptyPeerReplies.Should().BeGreaterThan(emptyBefore + 1);
                failedPort.Requests.Should().HaveCount(failedPort.FirstEmptyAtWriteCount!.Value,
                    "the established owner sends no restore, renewal, frame or probe after its known empty peer reply");
                routes.Ports.Skip(portsAtSleep).Should().OnlyContain(p => p.Requests.Count == 1
                    && p.Requests[0][1] == 0x12 && p.Requests[0][2] == 0,
                    "every asleep replacement is a new read-only basic-identity snapshot owner with zero SET/ARM/frame traffic");
                File.ReadAllBytes(journal).Should().Equal(before);
                ((int)budget.GetValue(owner)!).Should().Be(0, "known empty RF peers are not independent protocol fault attempts");
                (await owner.GetKeyboardDisplayStatusAsync()).State.Should().Be("Waiting");

                routes.UseCodexPage = true; routes.RadioPeerUnavailable = false;
                var images = routes.Radio.Images.Count;
                await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && routes.Radio.Images.Count > images, time);
                (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
                await WriteRouteKeys(bridge, 2);
                await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);
                ((int)budget.GetValue(owner)!).Should().Be(0);
            }
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse();
            routes.Ports.Should().OnlyContain(p => p.Transport == 2 && p.MaximumReaders <= 1);
            routes.WritesToUnavailableRoute.Should().Be(0); state.Applied.Should().BeEmpty();
            JsonSerializer.Serialize(state.Scene).Should().Be(scene);
        }
        finally
        {
            routes.RadioPeerUnavailable = false;
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task Actual_USB_bad_basic_info_remains_terminal_and_never_uses_RF_empty_peer_wake_policy()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-usb-bad-basic-" + Guid.NewGuid().ToString("N"));
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
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && routes.Radio.Images.Count >= 1, time);
            routes.ChangeAfterNextSafeReply(1);
            await SwitchUntil(() => routes.ActiveTransport == 1 && bridge.Available && routes.Usb.Images.Count >= 1, time);
            routes.WiredBadBasicInfo = true;
            await ((Func<Task>)(() => WriteRouteKeys(bridge, 1))).Should().ThrowAsync<Exception>();
            await SwitchUntil(() => owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error"
                && routes.Ports.All(p => p.Disposed), time);
            var opens = routes.Ports.Count;
            routes.WiredBadBasicInfo = false; time.Advance(TimeSpan.FromSeconds(2)); await Task.Delay(1100);
            routes.Ports.Should().HaveCount(opens);
            (await owner.GetKeyboardDisplayStatusAsync()).State.Should().Be("Error");
            state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.WiredBadBasicInfo = false;
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
