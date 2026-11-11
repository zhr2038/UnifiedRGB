using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(1, true)] [InlineData(2, true)]
    public async Task Native_reconciliation_reconnects_without_setting_writes_and_preserves_original_baseline(byte transport, bool delayedRead)
    {
        await using var rig = new NativeReconciliationRig();
        await rig.StartAsync(transport);
        await WriteRouteKeys(rig.Bridge, transport);
        var record = File.ReadAllBytes(rig.Journal);
        using (var json = JsonDocument.Parse(record))
            json.RootElement.GetProperty("Lighting").GetProperty("SoftwareControl").GetByte().Should().Be(0);
        var initial = rig.Routes.Ports[^1];
        var images = rig.Firmware(transport).Images.Count;
        rig.Routes.RefuseCapabilityTransport = transport;
        await SwitchUntil(() => initial.Disposed, rig.Time);
        if (delayedRead) rig.Routes.RefuseReadCommand = 0x27;
        await SwitchUntil(() => rig.Routes.Ports.Count > 1 && rig.Routes.Ports[^1] != initial
            && rig.Bridge.Available && rig.Source.DedicatedPageActive && rig.Firmware(transport).Images.Count > images, rig.Time);
        var replacement = rig.Routes.Ports[^1];
        var replacements = rig.Routes.Ports.SkipWhile(p => p != initial).Skip(1).ToArray();
        replacements.Should().OnlyContain(p => !p.Requests.Any(IsPersistentSetting),
            "native reattachment reads its existing configuration and starts a new ephemeral ARM");
        if (delayedRead)
        {
            var waiting = replacements.Single(p => p.InjectedTransientFailures.Contains("readonly-reconciliation"));
            waiting.Disposed.Should().BeTrue();
            waiting.Requests.Should().OnlyContain(p => IsRecoveryRead(p));
        }
        File.ReadAllBytes(rig.Journal).Should().Equal(record);
        await WriteRouteKeys(rig.Bridge, transport);
        replacement.Requests.Should().NotContain(p => IsPersistentSetting(p));
        await BrowseOnCurrentRoute(rig.Routes, rig.Inputs, rig.Browses, transport, rig.Time);
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        rig.Routes.MaximumLivePorts.Should().Be(1);
        rig.Routes.OpenWhilePriorInputLive.Should().BeFalse();
        await rig.Owner.StopAsync(default);
        rig.Firmware(transport).Requests.Should().Contain(p => p[1] == 0x74 && p[5] == 0,
            "stopping the new owner still restores the original software-control flag");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Native_reconciliation_checks_journal_again_after_the_last_read_before_adopting(byte transport)
    {
        await using var rig = new NativeReconciliationRig();
        await rig.StartAsync(transport);
        await WriteRouteKeys(rig.Bridge, transport);
        var initial = rig.Routes.Ports[^1];
        rig.Routes.RefuseCapabilityTransport = transport;
        await SwitchUntil(() => initial.Disposed, rig.Time);
        byte[]? changed = null;
        var reads = 0;
        rig.Routes.AfterCapabilityRead = () =>
        {
            if (++reads != 2) return;
            var record = JsonNode.Parse(File.ReadAllBytes(rig.Journal))!;
            record["Brightness"] = 73;
            changed = JsonSerializer.SerializeToUtf8Bytes(record);
            File.WriteAllBytes(rig.Journal, changed);
        };
        await SwitchUntil(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error", rig.Time);
        changed.Should().NotBeNull();
        File.ReadAllBytes(rig.Journal).Should().Equal(changed!);
        rig.Routes.Ports[^1].Requests.Should().OnlyContain(p => IsRecoveryRead(p));
        rig.Source.DedicatedPageActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Native_reconciliation_new_intent_during_first_capability_read_never_falls_through_to_old_restore(byte transport)
    {
        await using var rig = new NativeReconciliationRig();
        await rig.StartAsync(transport);
        var initial = rig.Routes.Ports[^1];
        rig.Routes.RefuseCapabilityTransport = transport;
        await SwitchUntil(() => initial.Disposed, rig.Time);
        DualTransportFactory.Port? interrupted = null;
        rig.Routes.AfterCapabilityRead = () =>
        {
            rig.Routes.AfterCapabilityRead = null;
            interrupted = rig.Routes.Ports[^1];
            rig.Source.RequestCodexEnabled(false);
        };
        await SwitchUntil(() => interrupted?.Disposed == true, rig.Time);
        interrupted!.Requests.Should().OnlyContain(p => IsRecoveryRead(p));
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task Native_reconciliation_preserves_stock_profile_token_mapping_and_saved_brightness_goal(byte profile)
    {
        await using var rig = new NativeReconciliationRig(profile);
        await rig.StartAsync(2);
        await WriteRouteKeys(rig.Bridge, 2);
        var record = File.ReadAllBytes(rig.Journal);
        var preferencePath = Path.Combine(rig.DirectoryPath, "peripherals", "keyboard-native-display.json");
        var preference = JsonNode.Parse(File.ReadAllBytes(preferencePath))!;
        preference["Brightness"] = 50;
        File.WriteAllBytes(preferencePath, JsonSerializer.SerializeToUtf8Bytes(preference));
        rig.Routes.Radio.Brightness = 50;
        var initial = rig.Routes.Ports[^1];
        var images = rig.Routes.Radio.Images.Count;
        rig.Routes.RefuseCapabilityTransport = 2;
        await SwitchUntil(() => initial.Disposed && rig.Source.DedicatedPageActive
            && rig.Routes.Radio.Images.Count > images, rig.Time);
        File.ReadAllBytes(rig.Journal).Should().Equal(record);
        rig.Routes.Ports[^1].Requests.Should().NotContain(p => IsPersistentSetting(p));
        rig.Routes.Radio.Brightness.Should().Be(50);
        await rig.Owner.StopAsync(default);
        rig.Routes.Radio.Brightness.Should().Be(50, "final cleanup uses the separately saved native goal");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Native_reconciliation_initial_lighting_snapshot_refusal_waits_before_any_mutation(byte transport)
    {
        await using var rig = new NativeReconciliationRig();
        rig.Routes.RefuseReadCommand = 0x27;
        await rig.StartAsync(transport);
        var failed = rig.Routes.Ports.Single(p => p.InjectedTransientFailures.Contains("readonly-reconciliation"));
        failed.Requests.Should().OnlyContain(p => IsRecoveryRead(p));
        failed.Disposed.Should().BeTrue();
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
    }

    private static bool IsPersistentSetting(byte[] packet) => packet[1] is 0x61 or 0x68 or 0x74 or 0x51;
    private static bool IsRecoveryRead(byte[] packet) =>
        (packet[1], packet[2]) is (0x12, 0) or (0x12, 0x7D) or (0x21, 0) or (0x23, 2) or (0x23, 3) or (0x25, 0x0E) or (0x27, 0);

    private sealed class NativeReconciliationRig : IAsyncDisposable
    {
        public SourceClock Time { get; } = new();
        public DualTransportInputs Inputs { get; }
        public DualTransportFactory Routes { get; }
        public RogNativeMenuSource Source { get; }
        public RogWiredLightingBridge Bridge { get; } = new();
        public List<int> Browses { get; } = [];
        public KeyboardDisplayService Owner { get; }
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "native-reconciliation-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(DirectoryPath, "peripherals", "keyboard-display-session.json");
        private readonly PeripheralConfigurationGate _gate = new(new(false, null));
        private byte[] _pixels = Pixels();
        public byte[] CurrentPixels => Volatile.Read(ref _pixels);
        public void ChangePixels() => Volatile.Write(ref _pixels, _pixels.Select(b => (byte)(b ^ 0x5A)).ToArray());

        public NativeReconciliationRig(byte profile = 6)
        {
            Inputs = new(Time);
            Routes = new(Time, Inputs, codexPage: true);
            Routes.Usb.ProfileToken = profile;
            Routes.Radio.ProfileToken = profile;
            var options = DualOptions(DirectoryPath);
            options.AsusRog = new()
            {
                KeyboardPerKey = new()
                {
                    Enabled = true,
                    Maps = new[] { RogPerKeyLighting.WiredTransport, RogPerKeyLighting.WirelessTransport }.Select(transport =>
                        new RogPerKeyMapOptions
                        {
                            Model = "Synthetic recovery map", Transport = transport, Complete = true, Verified = true,
                            Evidence = "raw-HID regression", KeyOrderVerified = true,
                            Keys = RogAzothCandidateMap.For(transport).Map.Keys.Select(k =>
                                new RogPerKeyCellOptions { LedId = k.LedId, Key = k.Key, Row = k.Row, Column = k.Column }).ToArray(),
                        }).ToArray(),
                },
            };
            ulong cookie = Cookie;
            Source = new(Policy(1), Inputs, Inputs, Time, () => cookie++, options.KeyboardDisplay, Browses.Add);
            Source.RequestCodexEnabled(true);
            var rgb = SceneCycleRgbProxy.Create();
            ((SceneCycleRgbProxy)rgb).Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
            Owner = new(options, rgb, DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>(),
                Routes, new MutationClearGuard(), Bridge, wirelessFactory: Routes, configurationGate: _gate, nativeMenu: Source,
                time: Time, idleFrame: () => CurrentPixels, wiredRoutePresent: _ => Routes.WiredAvailable,
                nativePresentation: new DeclaredPresentation(KeyboardNativePresentationSupport.Available));
        }

        public CodexWireChannel Firmware(byte transport) => transport == 1 ? Routes.Usb : Routes.Radio;

        public async Task StartAsync(byte transport)
        {
            Routes.WiredAvailable = transport == 1;
            await Owner.StartAsync(default);
            await SwitchUntil(() => Routes.ActiveTransport == transport && Bridge.Available
                && Source.DedicatedPageActive && Firmware(transport).Images.Count > 0, Time);
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.StopAsync(default);
            Owner.Dispose();
            Routes.Dispose();
            Inputs.Dispose();
            _gate.Dispose();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }
}
