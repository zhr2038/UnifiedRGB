using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public void Known_USB_route_defers_RF_without_opening_its_still_present_receiver_or_settings()
    {
        var oldRf = WriteSwitchJournal(SwitchJournal, "wireless", 37);
        var oldUsb = WriteSwitchJournal(SwitchJournal + ".deferred-wired", "wired", 51);
        var wired = new Factory(); var rf = new WirelessFactory { BadFirmwareEveryOpen = true };
        using var service = Make(BothPinned(), wired, wirelessFactory: rf);
        using var selected = service.SelectConnection();
        selected!.ConnectionKind.Should().Be("wired");
        rf.Opens.Should().Be(0, "receiver presence is not a prerequisite for the verified USB route");
        rf.Channels.Should().BeEmpty("neither RF BasicInfo nor a refused RF GET23/03 restoration may be sent");
        File.ReadAllBytes(SwitchJournal + ".deferred-wireless").Should().Equal(oldRf);
        File.ReadAllBytes(SwitchJournal).Should().Equal(oldUsb);
        File.Exists(SwitchJournal + ".deferred-wired").Should().BeFalse();
        wired.Io.Mutations.Should().Be(0, "selection transfers records; live startup/profile verification still comes first");
    }

    [Fact]
    public async Task Returning_USB_runs_from_its_own_original_while_RF_recovery_remains_exact_and_unqueried()
    {
        var oldRf = WriteSwitchJournal(SwitchJournal, "wireless", 37);
        WriteSwitchJournal(SwitchJournal + ".deferred-wired", "wired", 51);
        var wired = new Factory(); wired.Io.Brightness = 100;
        var rf = new WirelessFactory { BadFirmwareEveryOpen = true };
        using var service = Make(BothPinned(), wired, wirelessFactory: rf);
        await service.StartAsync(default); await WaitAsync(service, "Running");
        (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        await service.StopAsync(default);
        wired.Io.Brightness.Should().Be(51);
        rf.Opens.Should().Be(0);
        File.ReadAllBytes(SwitchJournal + ".deferred-wireless").Should().Equal(oldRf);
        File.Exists(SwitchJournal).Should().BeFalse();
    }

    [Fact]
    public void Absent_USB_does_not_defer_RF_or_invent_a_route_switch()
    {
        var oldRf = WriteSwitchJournal(SwitchJournal, "wireless", 37);
        var wired = new Factory { Available = false }; var rf = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: rf);
        using var selected = service.SelectConnection();
        selected!.ConnectionKind.Should().Be("wireless");
        File.ReadAllBytes(SwitchJournal).Should().Equal(oldRf);
        File.Exists(SwitchJournal + ".deferred-wireless").Should().BeFalse();
    }

    [Fact]
    public void Legacy_unscoped_RF_journal_still_blocks_USB_without_deletion_or_RF_fallback()
    {
        WriteSwitchJournal(SwitchJournal, "wireless", 37);
        var json = File.ReadAllText(SwitchJournal).Replace("\"Schema\":2", "\"Schema\":1", StringComparison.Ordinal);
        File.WriteAllText(SwitchJournal, json); var before = File.ReadAllBytes(SwitchJournal);
        var wired = new Factory(); var rf = new WirelessFactory();
        using var service = Make(BothPinned(), wired, wirelessFactory: rf);
        ((Action)(() => service.SelectConnection())).Should().Throw<IOException>();
        File.ReadAllBytes(SwitchJournal).Should().Equal(before);
        File.Exists(SwitchJournal + ".deferred-wireless").Should().BeFalse();
        wired.Io.Mutations.Should().Be(0); rf.Opens.Should().Be(0);
    }
}
