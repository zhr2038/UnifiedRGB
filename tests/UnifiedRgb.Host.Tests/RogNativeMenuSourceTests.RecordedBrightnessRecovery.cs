using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recorded_RF_restore_replaces_owner_only_if_refused_brightness_was_never_sent(bool beforeWrite)
    {
        var time = new SourceClock();
        using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "rf-recorded-brightness-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data);
        ulong cookie = Cookie;
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create();
        ((SceneCycleRgbProxy)rgb).Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels,
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count > 0, time);
            var journalBefore = File.ReadAllBytes(journal);
            var imagesBefore = routes.Radio.Images.Count;
            var initial = routes.Ports[^1];
            routes.RadioRefuseNextCap = true;
            await SwitchUntil(() => initial.Disposed && routes.EmptyPeerReplies > 0
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting", time);

            routes.RadioRestoreBrightnessRefusalBeforeWrite = beforeWrite;
            routes.RadioPeerUnavailable = false;
            var marker = beforeWrite ? "brightness-before-write" : "brightness-after-write";
            await SwitchUntil(() => routes.Ports.Any(p => p.Disposed && p.InjectedTransientFailures.Contains(marker)), time);
            var failed = routes.Ports.Single(p => p.InjectedTransientFailures.Contains(marker));
            failed.Requests.Count(p => p[1] == 0x61).Should().Be(1, "the preset completed before the brightness exchange");
            failed.Requests.Count(p => p[1] == 0x68).Should().Be(beforeWrite ? 0 : 1);
            failed.Requests.Should().HaveCount(failed.TransientFailureAtWriteCount!.Value,
                "a poisoned owner cannot send any further command");
            File.ReadAllBytes(journal).Should().Equal(journalBefore);
            await SwitchUntil(() => owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State is "Waiting" or "Error", time);
            (await owner.GetKeyboardDisplayStatusAsync()).State.Should().Be(beforeWrite ? "Waiting" : "Error");

            if (!beforeWrite)
            {
                routes.Ports[^1].Should().BeSameAs(failed);
                return;
            }

            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count > imagesBefore, time);
            var recovered = routes.Ports[^1];
            recovered.Should().NotBeSameAs(failed);
            var capability = recovered.Requests.FindIndex(p => p[1] == 0x12 && p[2] == 0x7D);
            var preset = recovered.Requests.FindIndex(p => p[1] == 0x61);
            capability.Should().BeGreaterThan(0);
            preset.Should().BeGreaterThan(capability);
            routes.Radio.Images[^1].Should().Equal(Pixels());
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            routes.MaximumLivePorts.Should().Be(1);
            routes.OpenWhilePriorInputLive.Should().BeFalse();
        }
        finally
        {
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
