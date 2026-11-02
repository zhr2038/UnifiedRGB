using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task Actual_transport_deferred_absence_evidence_automatically_reacquires_RF_only_after_positive_USB_absence()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-deferred-absence-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); ulong cookie = Cookie;
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var beforeScene = JsonSerializer.Serialize(state.Scene);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels,
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && routes.Radio.Images.Count >= 1, time);
            routes.ChangeAfterNextSafeReply(1);
            await SwitchUntil(() => routes.ActiveTransport == 1 && bridge.Available && routes.Usb.Images.Count >= 1, time);
            var usbRecord = File.ReadAllBytes(journal);
            var rfOpens = routes.Ports.Count(p => p.Transport == 2);
            routes.AbsenceVisible = false; routes.FailUsbKeyWriteAndSwitchToRadio = true;
            await ((Func<Task>)(() => WriteRouteKeys(bridge, 1))).Should().ThrowAsync<IOException>();
            await SwitchUntil(() => owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error"
                && routes.Ports.All(p => p.Disposed) && inputs.LiveHandles == 0, time);
            routes.TransitionWriteFailures.Should().Be(1);
            routes.WiredAvailable.Should().BeFalse(); routes.AbsenceVisible.Should().BeFalse();
            var writesAtFailure = routes.Usb.Requests.Count;
            time.Advance(TimeSpan.FromSeconds(2)); await Task.Delay(1100);
            (await owner.GetKeyboardDisplayStatusAsync()).State.Should().Be("Error");
            routes.Ports.Count(p => p.Transport == 2).Should().Be(rfOpens, "unknown metadata must not authorize a new RF owner");
            routes.Usb.Requests.Should().HaveCount(writesAtFailure, "no old USB query, setter, frame or recovery replay may probe an uncertain route");
            File.ReadAllBytes(journal).Should().Equal(usbRecord);

            // Only the read-only metadata evidence changes. No toggle, recovery RPC, fresh write to USB or Host restart.
            routes.AbsenceVisible = true;
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && routes.Radio.Images.Count >= 2, time);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            routes.Usb.Requests.Should().HaveCount(writesAtFailure);
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse();
            Directory.EnumerateFiles(Path.GetDirectoryName(journal)!, "keyboard-display-session*")
                .Where(p => p != journal).Should().Contain(p => File.ReadAllBytes(p).SequenceEqual(usbRecord));
            await WriteRouteKeys(bridge, 2);
            state.Applied.Should().BeEmpty(); JsonSerializer.Serialize(state.Scene).Should().Be(beforeScene);
        }
        finally
        {
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
