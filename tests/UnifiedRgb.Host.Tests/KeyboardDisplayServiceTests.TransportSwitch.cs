using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    private string SwitchJournal => Path.Combine(_directory, "peripherals", "keyboard-display-session.json");

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
}
