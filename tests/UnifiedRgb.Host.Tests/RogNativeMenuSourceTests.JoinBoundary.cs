using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task Confirmed_USB_absence_with_unjoined_native_input_never_calls_a_new_factory_even_after_explicit_recovery()
    {
        var time = new SourceClock(); using var input = new Input(time, 1) { IgnoreCancellation = true };
        using var wire = new CodexWireChannel(1); var factory = new JoinBoundaryFactory(time, wire);
        var data = Path.Combine(Path.GetTempPath(), "urgb-owner-join-boundary-" + Guid.NewGuid().ToString("N"));
        var cleanupRoot = Path.GetFullPath(data);
        if (!cleanupRoot.StartsWith(Path.Combine(Path.GetTempPath(), "urgb-owner-join-boundary-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected private test cleanup path.");
        var options = DualOptions(data);
        var source = new RogNativeMenuSource(Policy(1), input, input, time, () => Cookie, options.KeyboardDisplay);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null));
        using var owner = new KeyboardDisplayService(options, rgb, screens, factory, new MutationClearGuard(),
            wirelessFactory: factory, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels,
            wiredRoutePresent: _ => factory.Available);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        try
        {
            await owner.StartAsync(default);
            await Until(() => input.Pending == 1);
            var originalJournal = File.ReadAllBytes(journal);
            factory.Available = false; time.Advance(TimeSpan.FromMilliseconds(101));
            await Until(() => owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
            await Task.Delay(1200); // the old Lost/Waiting branch used to call a fresh factory after one second
            factory.WirelessCalls.Should().Be(0, "positive physical absence cannot bypass an unjoined old input reader");
            factory.WiredCalls.Should().Be(1, "no new acquisition, even a metadata/pairing factory, may start before detachment");
            source.OwnerDetached.Should().BeFalse(); input.Pending.Should().Be(1);
            File.ReadAllBytes(journal).Should().Equal(originalJournal);
            var stopped = await owner.GetKeyboardDisplayStatusAsync();
            stopped.LastError.Should().Contain("AggregateException").And.Contain("retirement");

            // The recovery button cannot bypass a retained owner either; it may only reset the user's request.
            await owner.SetKeyboardDisplayEnabledAsync(stopped.Enabled);
            await Until(() => owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
            factory.WiredCalls.Should().Be(1); factory.WirelessCalls.Should().Be(0);
            File.ReadAllBytes(journal).Should().Equal(originalJournal);
            state.Applied.Should().BeEmpty();
        }
        finally
        {
            input.IgnoreCancellation = false;
            if (input.Pending != 0) { input.Send(new byte[21]); await Until(() => input.Pending == 0); }
            await owner.StopAsync(default);
            if (Directory.Exists(cleanupRoot)) Directory.Delete(cleanupRoot, true);
        }
    }

    private sealed class JoinBoundaryFactory(TimeProvider time, CodexWireChannel wire) : IRogWiredOledFactory, IRogWirelessOledFactory
    {
        public bool Available { get; set; } = true;
        public int WiredCalls { get; private set; }
        public int WirelessCalls { get; private set; }
        public RogWiredOledConnection? TryOpen(string deviceKey)
        {
            WiredCalls++;
            if (!Available) return null;
            var control = new HidCollectionInfo("fake#mi_01#control", 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0);
            return new(wire, () => Available, () => deviceKey, () => Policy(1).FirmwareHeader)
            { NativeControlBinding = new([control], null, time.GetTimestamp()), RouteAbsentProbe = () => !Available };
        }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            WirelessCalls++;
            throw new InvalidOperationException("A new receiver factory must not run while the previous input reader has not joined.");
        }
    }
}
