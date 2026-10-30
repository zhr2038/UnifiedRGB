using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UnifiedRgb-OledTest-" + Guid.NewGuid().ToString("N"));
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private ReadProxy? _rgbProxy;
    private ReadProxy? _screensProxy;

    [Fact]
    public async Task Default_off_does_not_enumerate_or_open_hardware()
    {
        var defaults = new KeyboardDisplayOptions();
        defaults.WirelessControlVerified.Should().BeFalse(); defaults.WirelessLightingVerified.Should().BeFalse();
        defaults.WirelessReceiverKey.Should().BeEmpty(); defaults.WirelessFirmwareHeader.Should().BeEmpty();
        var factory = new Factory(); var wireless = new WirelessFactory(); using var service = Make(defaults, factory, wirelessFactory: wireless);
        await service.StartAsync(default); await Task.Delay(60); await service.StopAsync(default);
        factory.Opens.Should().Be(0); wireless.Opens.Should().Be(0); (await service.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_local_pins_cannot_be_enabled_or_create_configuration()
    {
        using var service = Make(new(), new());
        await ((Func<Task>)(() => service.SetKeyboardDisplayEnabledAsync(true))).Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Permanent_display_survives_without_App_and_disable_restores_original_with_no_case_writes()
    {
        var factory = new Factory(); using var service = Make(Pinned(), factory);
        await service.StartAsync(default); await WaitAsync(service, "Running");
        var running = await service.GetKeyboardDisplayStatusAsync(); running.FramesAcknowledged.Should().Be(1);
        factory.Io.Data.Should().Be(111); factory.Io.Brightness.Should().Be(100);
        await service.SetKeyboardDisplayEnabledAsync(false); await WaitAsync(service, "Stopped");
        factory.Io.Brightness.Should().Be(25);
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeFalse();
        await service.StopAsync(default);
        // The persisted local toggle wins over the initial deployment default after a program restart.
        using var restarted = Make(Pinned(), new());
        (await restarted.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Unclean_restart_uses_journal_original_not_temporary_current_brightness()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        File.WriteAllText(Path.Combine(_directory, "peripherals", "keyboard-display-session.json"),
            JsonSerializer.Serialize(new { Schema = 2, DeviceKey = Key, FirmwareHeader = "001200000024000100", Brightness = (byte)25, ProfileToken = (byte)6 }));
        var factory = new Factory(); factory.Io.Brightness = 100;
        using var service = Make(Pinned(), factory);
        await service.StartAsync(default); await WaitAsync(service, "Running"); await service.StopAsync(default);
        factory.Io.Brightness.Should().Be(25);
    }

    [Fact]
    public async Task Protocol_failure_is_sticky_and_never_reopens_in_a_retry_loop()
    {
        var factory = new Factory(); factory.Io.BadFirmware = true;
        using var service = Make(Pinned(), factory);
        await service.StartAsync(default); await WaitAsync(service, "Error"); await Task.Delay(700);
        factory.Opens.Should().Be(1); factory.Io.Mutations.Should().Be(0);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task Changed_identity_in_recovery_journal_prevents_mutation()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        File.WriteAllText(Path.Combine(_directory, "peripherals", "keyboard-display-session.json"),
            JsonSerializer.Serialize(new { Schema = 2, DeviceKey = new string('B', 64), FirmwareHeader = "001200000024000100", Brightness = 0, ProfileToken = (byte)6 }));
        var factory = new Factory(); using var service = Make(Pinned(), factory);
        await service.StartAsync(default); await WaitAsync(service, "Error"); factory.Io.Mutations.Should().Be(0);
        await service.StopAsync(default);
    }

    [Fact]
    public void Rendered_logo_and_Chinese_artwork_are_real_independent_packed_pixels()
    {
        var bytes = KeyboardStatusArtwork.Render(new(CaseStatusState.Blackout, "熄灯模式", "屏幕 5/5 上报休眠"));
        bytes.Should().HaveCount(6656); bytes.Should().Contain(b => b != 0);
        bytes.Should().Contain(b => (b & 15) != (b >> 4)); // Not the rejected104pair duplicated-pixel encoding.
        var repeat = KeyboardStatusArtwork.Render(new(CaseStatusState.Blackout, "熄灯模式", "屏幕 5/5 上报休眠"));
        repeat.Should().Equal(bytes);
    }

    [Fact]
    public async Task New_recovery_record_binds_the_verified_keyboard_profile()
    {
        var factory = new Factory();
        using var service = Make(Pinned(), factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")));
            record.RootElement.GetProperty("Schema").GetInt32().Should().Be(2);
            record.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
        }
        finally { await service.StopAsync(default); }
    }

    [Theory]
    [InlineData(1, 6, "lacks verified profile scope")]
    [InlineData(2, 0, "lacks verified profile scope")]
    [InlineData(2, 7, "lacks verified profile scope")]
    [InlineData(2, 5, "different keyboard profile")]
    public async Task Unverified_or_different_profile_journal_is_retained_without_recovery_writes(int schema, byte savedProfile, string reason)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = schema, DeviceKey = Key,
            FirmwareHeader = "001200000024000100", Brightness = (byte)25, ProfileToken = savedProfile });
        File.WriteAllBytes(path, bytes);
        var factory = new Factory();
        factory.Io.Brightness = 100;
        using var service = Make(Pinned(), factory);
        await service.StartAsync(default);
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await service.GetKeyboardDisplayStatusAsync()).State is not ("Running" or "Error"))
                await Task.Delay(20, bound.Token);
            var status = await service.GetKeyboardDisplayStatusAsync();
            status.State.Should().Be("Error");
            status.LastError.Should().Contain(reason);
            factory.Io.Mutations.Should().Be(0);
            factory.Io.NativeWrites.Should().Be(0);
            File.ReadAllBytes(path).Should().Equal(bytes);
        }
        finally { await service.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_matching_legacy_state_is_archived_and_adopted_without_replaying_it(bool lighting)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        var native = lighting ? RogProtocol.KeyboardColourFrame(new(10, 20, 30), 25, 80) : null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, DeviceKey = Key,
            FirmwareHeader = "001200000024000100", Brightness = (byte)25, Lighting = native, StaticBank = native });
        File.WriteAllBytes(path, bytes);
        var factory = new Factory(); factory.Io.Brightness = 25;
        using var service = Make(Pinned() with { LightingVerified = lighting }, factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            factory.Io.BrightnessWrites.Should().Equal(100); // No legacy25 restoration was sent.
            factory.Io.NativeWrites.Should().Be(0);
            var archives = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.archive");
            archives.Should().HaveCount(1);
            File.ReadAllBytes(archives[0]).Should().Equal(bytes);
            using var record = JsonDocument.Parse(File.ReadAllBytes(path));
            record.RootElement.GetProperty("Schema").GetInt32().Should().Be(2);
            record.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
        }
        finally { await service.StopAsync(default); }
        factory.Io.Brightness.Should().Be(25);
    }

    [Fact]
    public async Task Matching_legacy_brightness_does_not_allow_different_lighting_banks_to_be_replayed()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        var other = RogProtocol.KeyboardColourFrame(new(200, 100, 50), 25, 80);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, DeviceKey = Key,
            FirmwareHeader = "001200000024000100", Brightness = (byte)25, Lighting = other, StaticBank = other });
        File.WriteAllBytes(path, bytes);
        var factory = new Factory(); factory.Io.Brightness = 25;
        using var service = Make(Pinned() with { LightingVerified = true }, factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error");
            factory.Io.Mutations.Should().Be(0);
            factory.Io.NativeWrites.Should().Be(0);
            File.ReadAllBytes(path).Should().Equal(bytes);
            Directory.GetFiles(Path.GetDirectoryName(path)!, "*.archive").Should().BeEmpty();
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Screen_switch_preserves_the_single_USB_owner_and_continuing_scene_colours()
    {
        var bridge = new RogWiredLightingBridge(); var factory = new Factory();
        using var service = Make(Pinned() with { LightingVerified = true }, factory, bridge);
        await service.StartAsync(default); await WaitAsync(service, "Running");
        await bridge.WriteAsync(new(255, 0, 0), default);
        await service.SetKeyboardDisplayEnabledAsync(false); await WaitAsync(service, "Stopped");
        bridge.Available.Should().BeTrue(); factory.Opens.Should().Be(1);
        var writes = factory.Io.NativeWrites; await Task.Delay(100); factory.Io.NativeWrites.Should().Be(writes);
        await bridge.WriteAsync(new(0, 255, 0), default); factory.Io.NativeWrites.Should().Be(writes + 1);
        factory.Io.Brightness.Should().Be(25);
        await service.SetKeyboardDisplayEnabledAsync(true); await WaitAsync(service, "Running"); factory.Opens.Should().Be(1);
        await service.StopAsync(default); bridge.Available.Should().BeFalse(); factory.Io.Brightness.Should().Be(25);
    }

    [Fact]
    public async Task Wireless_only_pins_never_probe_wired_and_journal_uses_wireless_identity()
    {
        var wired = new Factory(); var wireless = new WirelessFactory();
        using var service = Make(WirelessPinned(), wired, wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (wireless.Channels[0].Inner.Data < 2 * 113) await Task.Delay(10, deadline.Token);
            }
            wired.Opens.Should().Be(0); wireless.Opens.Should().Be(1);
            wireless.Channels[0].DeclaredChunks.Should().Be(113);
            wireless.Channels[0].Remaining.Should().Be(0);
            wireless.Channels[0].CopyLengths[^1].Should().Be(48);
            wireless.Channels[0].Pixels.Should().Equal(KeyboardStatusArtwork.Render(new(CaseStatusState.Blackout, "熄灯模式", "屏幕 1/1 上报休眠")));
            (await service.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("无线");
            using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")));
            record.RootElement.GetProperty("DeviceKey").GetString().Should().Be(Key);
            record.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be("0800070024000100");
        }
        finally { await service.StopAsync(default); }
        wireless.Channels.Single().Inner.Brightness.Should().Be(25);
    }

    [Fact]
    public async Task Missing_wired_connection_falls_back_to_the_pinned_receiver()
    {
        var wired = new Factory { Available = false }; var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless);
        await service.StartAsync(default); await WaitAsync(service, "Running");
        wired.Opens.Should().Be(1); wireless.Opens.Should().Be(1);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task Confirmed_wireless_removal_reconnects_without_replaying_recovery_to_a_different_identity()
    {
        var wired = new Factory(); var wireless = new WirelessFactory { DisconnectFirstAfterData = true };
        using var service = Make(WirelessPinned(), wired, wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (wireless.Opens != 2 || wireless.Channels.Count != 2 || wireless.Channels[1].Inner.Data != 113
                || (await service.GetKeyboardDisplayStatusAsync()).State != "Running") await Task.Delay(20, bound.Token);
            wireless.Channels[0].Present.Should().BeFalse();
            File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeTrue();
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        }
        finally { await service.StopAsync(default); }
        wireless.Channels[1].Inner.Brightness.Should().Be(25);
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_during_reconnect_backoff_keeps_the_worker_alive_and_allows_explicit_reenable()
    {
        var wireless = new WirelessFactory { DisconnectFirstAfterData = true };
        using var service = Make(WirelessPinned(), new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitForReconnectBackoffAsync(service);
            await service.SetKeyboardDisplayEnabledAsync(false);
            await Task.Delay(80);
            service.ExecuteTask.Should().NotBeNull();
            service.ExecuteTask!.IsCompleted.Should().BeFalse("cancelling a session backoff must not terminate the BackgroundService");
            await WaitAsync(service, "Stopped");
            await service.SetKeyboardDisplayEnabledAsync(true);
            await WaitAsync(service, "Running");
            wireless.Opens.Should().Be(2);
            wireless.Channels[1].Inner.Data.Should().Be(113);
        }
        finally { await service.StopAsync(default); }
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Stop_during_reconnect_backoff_completes_cleanly_and_retains_the_unresolved_journal()
    {
        var wireless = new WirelessFactory { DisconnectFirstAfterData = true };
        using var service = Make(WirelessPinned(), new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitForReconnectBackoffAsync(service);
            await service.StopAsync(default);
            service.ExecuteTask.Should().NotBeNull();
            service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("normal shutdown during backoff must retire the worker without a cancelled task escaping");
            wireless.Opens.Should().Be(1);
            wireless.Channels.Single().Inner.Data.Should().Be(1);
            File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeTrue();
        }
        finally { await service.StopAsync(default); }
    }

    private static async Task WaitForReconnectBackoffAsync(KeyboardDisplayService service)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = await service.GetKeyboardDisplayStatusAsync();
            if (status.State == "Waiting" && status.Detail.Contains("控制接口已断开", StringComparison.Ordinal)) return;
            await Task.Delay(5, bound.Token);
        }
    }

    [Fact]
    public async Task Command_timeout_poison_retains_original_scope_until_explicit_fresh_reenable()
    {
        var wireless = new WirelessFactory { TimeoutFirstBegin = true, Prepare = (open, io) => { if (open > 1) io.Brightness = 100; } };
        using var service = Make(WirelessPinned(), new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error");
            var first = wireless.Channels.Single();
            first.Inner.BeginPackets.Should().Be(1);
            first.Inner.Brightness.Should().Be(100); // The unknown connection cannot send its finally restoration.
            var mutations = first.Inner.Mutations;
            var journal = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
            var retained = File.ReadAllBytes(journal);
            using (var record = JsonDocument.Parse(retained))
            { record.RootElement.GetProperty("Brightness").GetByte().Should().Be(25); record.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6); }
            await Task.Delay(2300);
            wireless.Opens.Should().Be(1); first.Inner.BeginPackets.Should().Be(1);
            first.Inner.Mutations.Should().Be(mutations); File.ReadAllBytes(journal).Should().Equal(retained);
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain(nameof(RogOledTimeoutException));
            await service.SetKeyboardDisplayEnabledAsync(true); await WaitAsync(service, "Running");
            wireless.Opens.Should().Be(2); wireless.Channels[1].Inner.Data.Should().Be(113);
            wireless.Channels[1].Inner.BrightnessWrites.Take(2).Should().Equal((byte)25, (byte)100);
            first.Inner.Mutations.Should().Be(mutations);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Stream_eof_without_confirmed_removal_does_not_automatically_reopen_a_mutating_session()
    {
        var wireless = new WirelessFactory { EofFirstBegin = true };
        using var service = Make(WirelessPinned(), new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error");
            var first = wireless.Channels.Single(); first.Present.Should().BeTrue();
            first.Inner.BeginPackets.Should().Be(1); first.Inner.Brightness.Should().Be(100);
            var mutations = first.Inner.Mutations;
            var journal = Path.Combine(_directory, "peripherals", "keyboard-display-session.json"); var retained = File.ReadAllBytes(journal);
            await Task.Delay(1300); wireless.Opens.Should().Be(1);
            first.Inner.Mutations.Should().Be(mutations); File.ReadAllBytes(journal).Should().Equal(retained);
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain(nameof(RogOledConnectionLostException)).And.Contain("poisoned");
        }
        finally { await service.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnected_session_does_not_restore_or_restart_output_in_a_changed_firmware_or_profile(bool firmware)
    {
        var wireless = new WirelessFactory { DisconnectFirstAfterData = true,
            ReconnectProfile = firmware ? (byte)6 : (byte)5, BadReconnectFirmware = firmware };
        using var service = Make(WirelessPinned(), new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error");
            wireless.Opens.Should().Be(2); wireless.Channels[1].Inner.Mutations.Should().Be(0);
            wireless.Channels[1].Inner.NativeWrites.Should().Be(0);
            var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
            using var journal = JsonDocument.Parse(File.ReadAllBytes(path));
            journal.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
            journal.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be("0800070024000100");
            await Task.Delay(700); wireless.Opens.Should().Be(2);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Stale_wired_journal_is_retained_without_cross_route_recovery_or_output()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = new string('B', 64),
            FirmwareHeader = "001200000024000100", Brightness = (byte)25, ProfileToken = (byte)6 });
        File.WriteAllBytes(path, bytes);
        var wireless = new WirelessFactory();
        using var service = Make(WirelessPinned(), new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error"); wireless.Channels.Single().Inner.Mutations.Should().Be(0);
            File.ReadAllBytes(path).Should().Equal(bytes);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Wired_lighting_approval_does_not_attach_a_wireless_lighting_owner()
    {
        var bridge = new RogWiredLightingBridge(); var wireless = new WirelessFactory();
        using var service = Make(BothPinned() with { LightingVerified = true }, new() { Available = false }, bridge, wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running"); bridge.Available.Should().BeFalse();
            wireless.Channels.Single().Inner.NativeWrites.Should().Be(0);
            await service.SetKeyboardDisplayEnabledAsync(false); await WaitAsync(service, "Stopped");
            wireless.Channels.Single().Inner.Brightness.Should().Be(25);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Wireless_lighting_requires_its_own_explicit_approval_and_continues_when_the_display_is_off()
    {
        var bridge = new RogWiredLightingBridge(); var wireless = new WirelessFactory();
        using var service = Make(WirelessPinned() with { WirelessLightingVerified = true }, new(), bridge, wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running"); bridge.Available.Should().BeTrue();
            await bridge.WriteAsync(new(255, 0, 0), default);
            await service.SetKeyboardDisplayEnabledAsync(false); await WaitAsync(service, "Stopped");
            bridge.Available.Should().BeTrue();
            await bridge.WriteAsync(new(0, 255, 0), default);
            wireless.Opens.Should().Be(1); wireless.Channels.Single().Inner.NativeWrites.Should().Be(2);
        }
        finally { await service.StopAsync(default); }
        bridge.Available.Should().BeFalse();
    }

    [Fact]
    public async Task Wireless_lighting_journal_without_current_transport_approval_is_retained_before_mutation()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        var native = RogProtocol.KeyboardColourFrame(new(10, 20, 30), 25, 80);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = Key,
            FirmwareHeader = "0800070024000100", Brightness = (byte)25, ProfileToken = (byte)6, Lighting = native, StaticBank = native });
        File.WriteAllBytes(path, bytes);
        var wireless = new WirelessFactory();
        using var service = Make(WirelessPinned() with { LightingVerified = true }, new(), wirelessFactory: wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error"); var io = wireless.Channels.Single().Inner;
            io.Mutations.Should().Be(0); io.NativeWrites.Should().Be(0); File.ReadAllBytes(path).Should().Equal(bytes);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task PeripheralConfiguration_unplugged_wired_zero_journal_is_recovered_explicitly_before_fresh_rf_hundred_snapshot()
    {
        var journal = WriteWiredJournal(6);
        var originalBytes = File.ReadAllBytes(journal);
        var wired = new Factory { Available = false };
        wired.Io.Brightness = 100;
        var wireless = new WirelessFactory();
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null));
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless, configurationGate: gate);
        var release = () => service.ReleaseForPeripheralConfigurationAsync(default);
        await release.Should().ThrowAsync<IOException>();
        File.ReadAllBytes(journal).Should().Equal(originalBytes);
        wireless.Opens.Should().Be(0);
        (await service.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeTrue();

        wired.Available = true;
        await release();
        wired.Io.Brightness.Should().Be(25);
        File.Exists(journal).Should().BeFalse();
        wireless.Opens.Should().Be(0, "wired recovery never substitutes or reuses an RF snapshot");

        wired.Available = false;
        gate.AllowRogAcquisition();
        await service.StartAsync(default);
        await service.ResumeAfterPeripheralConfigurationAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            var rf = wireless.Channels.Single().Inner;
            rf.Brightness.Should().Be(100);
            rf.ProfileToken.Should().Be(6);
            rf.BrightnessWrites.Should().NotContain((byte)0);
        }
        finally { await service.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        _screensProxy!.ScreenMutations.Should().Be(0);
    }

    [Fact]
    public async Task PeripheralConfiguration_wired_restore_failure_is_retained_and_only_explicit_reattempt_deletes_journal()
    {
        var journal = WriteWiredJournal(6);
        var originalBytes = File.ReadAllBytes(journal);
        var wired = new Factory();
        wired.Io.Brightness = 100;
        wired.Io.FailRestoreBrightness = true;
        var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless,
            configurationGate: new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null)));
        var release = () => service.ReleaseForPeripheralConfigurationAsync(default);
        await release.Should().ThrowAsync<IOException>();
        File.ReadAllBytes(journal).Should().Equal(originalBytes);
        wired.Opens.Should().Be(1);
        wireless.Opens.Should().Be(0);

        wired.Io.FailRestoreBrightness = false;
        await release();
        wired.Opens.Should().Be(2);
        wired.Io.Brightness.Should().Be(25);
        File.Exists(journal).Should().BeFalse();
    }

    [Fact]
    public async Task PeripheralConfiguration_scope_mismatch_rejects_wired_recovery_without_rf_substitution_or_journal_deletion()
    {
        var journal = WriteWiredJournal(5);
        var originalBytes = File.ReadAllBytes(journal);
        var wired = new Factory();
        wired.Io.Brightness = 100;
        var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless,
            configurationGate: new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null)));
        var release = () => service.ReleaseForPeripheralConfigurationAsync(default);
        await release.Should().ThrowAsync<IOException>();
        File.ReadAllBytes(journal).Should().Equal(originalBytes);
        wired.Opens.Should().Be(1);
        wired.Io.BrightnessWrites.Should().BeEmpty();
        wireless.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Revision2_original_zero_uses_attributed_fallback_preserves_source_evidence_and_stops_67_but_not_rgb()
    {
        var factory = new Factory();
        factory.Io.Brightness = 0;
        var bridge = new RogWiredLightingBridge();
        using var service = Make(Pinned() with { LightingVerified = true }, factory, bridge);
        var journal = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            factory.Io.Brightness.Should().Be(100);
            using (var source = JsonDocument.Parse(File.ReadAllBytes(journal)))
                source.RootElement.GetProperty("Brightness").GetByte().Should().Be(0);
            await service.SetKeyboardDisplayEnabledAsync(false);
            await WaitAsync(service, "Stopped");
            factory.Io.Brightness.Should().Be(100);
            factory.Io.PresetSelections.Should().Contain(3);
            using (var preference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_directory, "peripherals", "keyboard-native-display.json"))))
            {
                preference.RootElement.GetProperty("Brightness").GetByte().Should().Be(100);
                preference.RootElement.GetProperty("Origin").GetString().Should().Be("explicit-fallback-100");
                preference.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
            }
            File.Exists(journal).Should().BeTrue("the shared RGB owner is still active");
            var data = factory.Io.Data;
            var renewals = factory.Io.RenewPackets;
            await Task.Delay(350);
            factory.Io.Data.Should().Be(data);
            factory.Io.RenewPackets.Should().Be(renewals);
            var writes = factory.Io.NativeWrites;
            await bridge.WriteAsync(new(0, 255, 0), default);
            factory.Io.NativeWrites.Should().Be(writes + 1);
            factory.Io.Brightness = 20;
            var preferencePath = Path.Combine(_directory, "peripherals", "keyboard-native-display.json");
            using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (true)
                {
                    using var preference = JsonDocument.Parse(File.ReadAllBytes(preferencePath));
                    if (preference.RootElement.GetProperty("Brightness").GetByte() == 20) break;
                    await Task.Delay(20, bound.Token);
                }
            }
            await service.ReleaseForPeripheralConfigurationAsync(default);
            File.Exists(journal).Should().BeFalse();
            using (var preference = JsonDocument.Parse(File.ReadAllBytes(preferencePath)))
            {
                preference.RootElement.GetProperty("Brightness").GetByte().Should().Be(20);
                preference.RootElement.GetProperty("Origin").GetString().Should().Be("fresh-native-while-disabled");
            }
        }
        finally { await service.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        _screensProxy!.ScreenMutations.Should().Be(0);
    }

    [Fact]
    public async Task Revision2_fresh_native_twenty_is_preserved_across_reenable_disable_restart_and_handoff()
    {
        var factory = new Factory();
        factory.Io.Brightness = 20;
        var bridge = new RogWiredLightingBridge();
        var options = Pinned() with { Enabled = false, LightingVerified = true };
        using var service = Make(options, factory, bridge);
        var preferencePath = Path.Combine(_directory, "peripherals", "keyboard-native-display.json");
        await service.StartAsync(default);
        try
        {
            using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!File.Exists(preferencePath)) await Task.Delay(20, bound.Token);
            await WaitAsync(service, "Stopped");
            factory.Io.Brightness.Should().Be(20);
            factory.Io.Data.Should().Be(0);
            factory.Io.RenewPackets.Should().Be(0);
            using (var preference = JsonDocument.Parse(File.ReadAllBytes(preferencePath)))
            {
                preference.RootElement.GetProperty("Brightness").GetByte().Should().Be(20);
                preference.RootElement.GetProperty("Origin").GetString().Should().Be("fresh-native-while-disabled");
            }
            await service.SetKeyboardDisplayEnabledAsync(true);
            await WaitAsync(service, "Running");
            factory.Io.Brightness.Should().Be(100);
            await service.SetKeyboardDisplayEnabledAsync(false);
            await WaitAsync(service, "Stopped");
            factory.Io.Brightness.Should().Be(20);
            var writes = factory.Io.NativeWrites;
            await bridge.WriteAsync(new(0, 255, 0), default);
            factory.Io.NativeWrites.Should().Be(writes + 1);
            await service.ReleaseForPeripheralConfigurationAsync(default);
            using (var preference = JsonDocument.Parse(File.ReadAllBytes(preferencePath)))
                preference.RootElement.GetProperty("Brightness").GetByte().Should().Be(20);
        }
        finally { await service.StopAsync(default); }

        using var restarted = Make(options, factory, bridge);
        await restarted.StartAsync(default);
        try
        {
            await WaitAsync(restarted, "Stopped");
            factory.Io.Brightness.Should().Be(20);
            factory.Io.Data.Should().Be(0);
            await restarted.ReleaseForPeripheralConfigurationAsync(default);
        }
        finally { await restarted.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        _screensProxy!.ScreenMutations.Should().Be(0);
    }

    [Fact]
    public async Task Revision2_malformed_native_goal_fails_before_restore_and_retains_recovery()
    {
        var preferencePath = Path.Combine(_directory, "peripherals", "keyboard-native-display.json");
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        File.WriteAllText(preferencePath, "not-json");
        var factory = new Factory();
        factory.Io.Brightness = 0;
        using var service = Make(Pinned(), factory);
        var journal = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            var presets = factory.Io.PresetSelections.Count;
            await service.SetKeyboardDisplayEnabledAsync(false);
            await WaitAsync(service, "Error");
            factory.Io.PresetSelections.Should().HaveCount(presets);
            factory.Io.Brightness.Should().Be(100);
            File.Exists(journal).Should().BeTrue();
            File.ReadAllText(preferencePath).Should().Be("not-json");
            var handoff = () => service.ReleaseForPeripheralConfigurationAsync(default);
            await handoff.Should().ThrowAsync<Exception>();
            File.Exists(journal).Should().BeTrue();
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Revision2_actual_keyboard_busy_resume_stays_requested_and_explicit_retry_uses_fresh_session()
    {
        var factory = new Factory();
        factory.Io.Brightness = 25;
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(false, null));
        using var keyboard = Make(Pinned(), factory, configurationGate: gate);
        var options = new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = Pinned() };
        var coordinator = new PeripheralConfigurationService(options, gate, new KeyboardOnlyResources(keyboard), TimeSpan.FromSeconds(2));
        await keyboard.StartAsync(default);
        try
        {
            await WaitAsync(keyboard, "Running");
            (await coordinator.EnterPeripheralConfigurationAsync()).State.Should().Be("Ready");
            factory.OpenFailure = new IOException("fake sharing32 competing owner");
            var failed = await coordinator.ExitPeripheralConfigurationAsync();
            failed.State.Should().Be("Error");
            failed.ConfigurationMode.Should().BeTrue();
            failed.LastError.Should().Contain("sharing32");
            gate.RogAcquisitionAllowed.Should().BeFalse();
            File.Exists(Path.Combine(_directory, "peripherals", "peripheral-configuration.json")).Should().BeTrue();

            factory.OpenFailure = null;
            var resumed = await coordinator.ExitPeripheralConfigurationAsync();
            resumed.State.Should().Be("Active");
            gate.RogAcquisitionAllowed.Should().BeTrue();
            factory.Opens.Should().Be(3);
            factory.Io.Brightness.Should().Be(100);
        }
        finally { await keyboard.StopAsync(default); }
        factory.Io.Brightness.Should().Be(25);
    }

    [Fact]
    public async Task Revision2_actual_keyboard_blocked_factory_does_not_deadlock_transition_gate()
    {
        var factory = new Factory();
        factory.Io.Brightness = 25;
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(false, null));
        using var keyboard = Make(Pinned(), factory, configurationGate: gate);
        var coordinator = new PeripheralConfigurationService(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = Pinned() },
            gate, new KeyboardOnlyResources(keyboard), TimeSpan.FromSeconds(1));
        await keyboard.StartAsync(default);
        try
        {
            await WaitAsync(keyboard, "Running");
            (await coordinator.EnterPeripheralConfigurationAsync()).State.Should().Be("Ready");
            factory.OpenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.OpenRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exiting = coordinator.ExitPeripheralConfigurationAsync();
            await factory.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            exiting.IsCompleted.Should().BeFalse();
            factory.OpenRelease.SetResult();
            (await exiting.WaitAsync(TimeSpan.FromSeconds(2))).State.Should().Be("Active");
        }
        finally { await keyboard.StopAsync(default); }
    }

    [Fact]
    public async Task Revision2_actual_keyboard_definite_absence_is_not_a_present_failure()
    {
        var factory = new Factory { Available = false };
        var gate = new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null));
        using var keyboard = Make(Pinned(), factory, configurationGate: gate);
        var coordinator = new PeripheralConfigurationService(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = Pinned() },
            gate, new KeyboardOnlyResources(keyboard), TimeSpan.FromSeconds(1));
        await keyboard.StartAsync(default);
        try { (await coordinator.ExitPeripheralConfigurationAsync()).State.Should().Be("Active"); }
        finally { await keyboard.StopAsync(default); }
        factory.Opens.Should().Be(1);
    }

    [Fact]
    public async Task Revision2_matching_rf_schema2_recovery_restores_recorded_transport_without_wired_substitution()
    {
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 2, DeviceKey = Key, FirmwareHeader = "0800070024000100", Brightness = (byte)25, ProfileToken = (byte)6,
        }));
        var wired = new Factory();
        var wireless = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: wireless,
            configurationGate: new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true, null)));
        await service.ReleaseForPeripheralConfigurationAsync(default);
        wired.Opens.Should().Be(0);
        wireless.Opens.Should().Be(1);
        wireless.Channels.Single().Inner.Brightness.Should().Be(25);
        File.Exists(path).Should().BeFalse();
        _rgbProxy!.CaseMutations.Should().Be(0);
        _screensProxy!.ScreenMutations.Should().Be(0);
    }

    private sealed class KeyboardOnlyResources(KeyboardDisplayService keyboard) : IPeripheralConfigurationResources
    {
        public Task ReleaseAsync(CancellationToken ct) => keyboard.ReleaseForPeripheralConfigurationAsync(ct);
        public Task ResumeAsync(CancellationToken ct) => keyboard.ResumeAfterPeripheralConfigurationAsync(ct);
    }

    private string WriteWiredJournal(byte profile)
    {
        var path = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 2, DeviceKey = Key, FirmwareHeader = "001200000024000100", Brightness = (byte)25, ProfileToken = profile,
        }));
        return path;
    }

    private KeyboardDisplayService Make(KeyboardDisplayOptions options, Factory factory, RogWiredLightingBridge? bridge = null,
        WirelessFactory? wirelessFactory = null, PeripheralConfigurationGate? configurationGate = null)
    {
        var rgb = DispatchProxy.Create<IRgbService, ReadProxy>();
        var screens = DispatchProxy.Create<IScreenService, ReadProxy>();
        _rgbProxy = (ReadProxy)rgb;
        _screensProxy = (ReadProxy)screens;
        return new(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = options }, rgb, screens, factory, new ClearGuard(), bridge, wirelessFactory, configurationGate);
    }

    private static KeyboardDisplayOptions Pinned() => new() { Enabled = true, ControlVerified = true, DeviceKey = Key, FirmwareHeader = "001200000024000100" };
    private static KeyboardDisplayOptions WirelessPinned() => new() { Enabled = true, WirelessControlVerified = true,
        WirelessReceiverKey = Key, WirelessFirmwareHeader = "0800070024000100" };
    private static KeyboardDisplayOptions BothPinned() => Pinned() with { WirelessControlVerified = true,
        WirelessReceiverKey = Key, WirelessFirmwareHeader = "0800070024000100" };
    private static async Task WaitAsync(KeyboardDisplayService service, string state)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await service.GetKeyboardDisplayStatusAsync()).State != state) await Task.Delay(20, bound.Token);
    }

    public class ReadProxy : DispatchProxy
    {
        public int CaseMutations { get; private set; }
        public int ScreenMutations { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "GetSceneAsync" => Task.FromResult(new SceneDto([new([new("case:one", 0)], "StaticColor", ["#000000"], Brightness: 0)])),
                "ListControllersAsync" => Task.FromResult<IReadOnlyList<ControllerDto>>([new("case:one", "fake", "case", "Virtual", "Linear", "", [new(0, "zone", "Linear", 1, 0, 0)], 60, "Active", null, true)]),
                "GetScreenSceneAsync" => Task.FromResult(new ScreenSceneDto([new("screen:h2:test")])),
                "ListScreensAsync" => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([new("screen:h2:test", "H2", "H2", 400, 400, "Sleeping", null, true, 0, 0, null, 0)]),
                _ => CountAndReject(targetMethod),
            };

            object? CountAndReject(MethodInfo? method)
            {
                if (method?.DeclaringType == typeof(IRgbService)) CaseMutations++;
                if (method?.DeclaringType == typeof(IScreenService)) ScreenMutations++;
                throw new InvalidOperationException("No case or screen writes allowed: " + method?.Name);
            }
        }
    }

    private sealed class ClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
    }

    private sealed class Factory : IRogWiredOledFactory
    {
        public Io Io { get; private set; } = new(); public int Opens { get; private set; } public bool Available { get; set; } = true;
        public Exception? OpenFailure { get; set; }
        public TaskCompletionSource? OpenStarted { get; set; }
        public TaskCompletionSource? OpenRelease { get; set; }
        public RogWiredOledConnection? TryOpen(string key)
        {
            Opens++;
            OpenStarted?.TrySetResult();
            OpenRelease?.Task.GetAwaiter().GetResult();
            if (OpenFailure is not null) throw OpenFailure;
            if (!Available) return null;
            if (Opens > 1) Io = Io.Reopen();
            return new(Io, () => true, () => key, () => "001200000024000100");
        }
    }

    private sealed class WirelessFactory : IRogWirelessOledFactory
    {
        public int Opens { get; private set; } public bool DisconnectFirstAfterData { get; init; }
        public bool TimeoutFirstBegin { get; init; }
        public bool EofFirstBegin { get; init; }
        public byte ReconnectProfile { get; init; } = 6;
        public bool BadReconnectFirmware { get; init; }
        public bool BadFirmwareEveryOpen { get; init; }
        public Action<int, Io>? Prepare { get; init; }
        public List<WirelessIo> Channels { get; } = [];
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            Opens++;
            var io = new WirelessIo { DisconnectAfterData = DisconnectFirstAfterData && Opens == 1,
                BadFirmware = (BadReconnectFirmware && Opens > 1) || BadFirmwareEveryOpen };
            io.Inner.ProfileToken = Opens > 1 ? ReconnectProfile : (byte)6;
            io.Inner.StallFirstBegin = TimeoutFirstBegin && Opens == 1;
            io.Inner.EofFirstBegin = EofFirstBegin && Opens == 1; Prepare?.Invoke(Opens, io.Inner); Channels.Add(io);
            return new(new RogReceiver(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, firmwareHeader, () => io.Present);
        }
    }

    private sealed class WirelessIo : IHidReportChannel
    {
        public Io Inner { get; } = new(); public bool Present { get; set; } = true; public bool DisconnectAfterData { get; init; }
        public bool BadFirmware { get; init; }
        public List<byte> Pixels { get; } = [];
        public List<int> CopyLengths { get; } = [];
        public int DeclaredChunks { get; private set; }
        public int Remaining { get; private set; }
        private int _total;
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            if (!Present) throw new IOException("Receiver removed.");
            var p = report.ToArray();
            if (p.Length != 64 || p[0] != 2) throw new InvalidDataException("Wrong wireless request envelope.");
            var wired = new byte[65]; p.AsSpan(1, 63).CopyTo(wired.AsSpan(1));
            if (p[1] == 0x67 && p[2] == 0)
            {
                Remaining = DeclaredChunks = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(7));
                _total = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(9)) * BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(11)) / 2;
                Pixels.Clear(); CopyLengths.Clear();
            }
            else if (p[1] == 0x67 && p[2] == 1)
            {
                BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3)).Should().Be((ushort)(Remaining - 1)); Remaining--;
                var count = Math.Min(59, _total - Pixels.Count); Pixels.AddRange(p.AsSpan(5, count).ToArray()); CopyLengths.Add(count);
            }
            await Inner.WriteAsync(wired, ct);
            if (DisconnectAfterData && Inner.Data > 0) { Present = false; throw new IOException("Receiver removed after data."); }
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!Present) throw new IOException("Receiver removed.");
            var wired = new byte[65]; var length = await Inner.ReadAsync(wired, ct);
            if (length == 0) return 0;
            if (length != 65) throw new InvalidDataException("Wrong fake wired reply.");
            if (wired[1] == 0x12 && wired[2] == 0)
            {
                wired[5] = 8; wired[6] = 0; wired[7] = 7; wired[8] = 0;
                wired[2] = BadFirmware ? (byte)1 : (byte)0; wired[12] = 0x24; wired[13] = 0; wired[14] = 1; wired[15] = 0;
            }
            var wireless = new byte[64]; wired.AsSpan(1, 63).CopyTo(wireless.AsSpan(1)); wireless[0] = 2;
            wireless.CopyTo(buffer); return wireless.Length;
        }
        public void Dispose() => Inner.Dispose();
    }

    private sealed class Io : IHidReportChannel
    {
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(8);
        public byte Brightness { get; set; } = 25;
        public byte SoftwareControl { get; set; }
        public bool BadFirmware { get; set; }
        public byte ProfileToken { get; set; } = 6;
        public bool StallFirstBegin { get; set; }
        public bool EofFirstBegin { get; set; }
        public bool FailRestoreBrightness { get; set; }
        private bool _eofNextRead;
        public int BeginPackets { get; private set; }
        public int Data { get; private set; }
        public int RenewPackets { get; private set; }
        public int Mutations { get; private set; }
        public int NativeWrites { get; private set; }
        public List<byte> PresetSelections { get; } = [];
        public List<byte> BrightnessWrites { get; } = [];
        private RogKeyboardNativeState _native = RogProtocol.KeyboardColourFrame(new(10, 20, 30), 25, 80);
        public RogKeyboardNativeState Native => _native;
        public Io Reopen() => new()
        {
            Brightness = Brightness,
            BadFirmware = BadFirmware,
            ProfileToken = ProfileToken,
            StallFirstBegin = StallFirstBegin,
            EofFirstBegin = EofFirstBegin,
            FailRestoreBrightness = FailRestoreBrightness,
            _native = _native,
            SoftwareControl = SoftwareControl,
        };
        public ValueTask WriteAsync(ReadOnlyMemory<byte> request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var p = request.ToArray(); var b = new byte[65]; p.AsSpan(0, 5).CopyTo(b);
            switch (p[1], p[2])
            {
                case (0x12, 0): b[2] = BadFirmware ? (byte)1 : (byte)0; b[5] = 0x24; b[7] = 1; b[9] = 6; b[11] = ProfileToken; break;
                case (0x25, 0x0E): Convert.FromHexString(_native.Payloads[0]).CopyTo(b, 5); break;
                case (0x51, 0x2C): _native = new(0, new[] { Convert.ToHexString(p.AsSpan(5, 14)) }); NativeWrites++; b = p; break;
                case (0x27, 0): b[5] = SoftwareControl; break;
                case (0x74, 0): SoftwareControl = p[5]; b[5] = SoftwareControl; Mutations++; break;
                case (0x21, 0): b[5] = 3; break;
                case (0x23, 2): b[5] = 1; break;
                case (0x23, 3): b[5] = Brightness; break;
                case (0x61, 0): Mutations++; PresetSelections.Add(p[5]); break;
                case (0x68, 0):
                    if (FailRestoreBrightness && p[5] == 25) throw new IOException("Wired original brightness restoration failed.");
                    Mutations++; Brightness = p[5]; BrightnessWrites.Add(p[5]); b = p; break;
                case (0x67, 0):
                    Mutations++; BeginPackets++;
                    if (StallFirstBegin) { StallFirstBegin = false; return ValueTask.CompletedTask; }
                    if (EofFirstBegin) { EofFirstBegin = false; _eofNextRead = true; return ValueTask.CompletedTask; }
                    break;
                case (0x67, 1): Mutations++; Data++; break;
                case (0x67, 2): Mutations++; RenewPackets++; return ValueTask.CompletedTask;
                default: throw new InvalidOperationException("Unexpected packet");
            }
            _queue.Writer.TryWrite(b).Should().BeTrue(); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct) { if (_eofNextRead) { _eofNextRead = false; return 0; } var p = await _queue.Reader.ReadAsync(ct); p.CopyTo(b); return p.Length; }
        public void Dispose() => _queue.Writer.TryComplete();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
