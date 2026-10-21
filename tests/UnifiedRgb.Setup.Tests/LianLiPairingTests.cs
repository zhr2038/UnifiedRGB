using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup.Tests;

public sealed class LianLiPairingTests
{
    private static readonly UsbDeviceInfo[] OneReceiver = [FakeSetupSystem.Receiver()];
    private static readonly UsbDeviceInfo[] OneSender = [FakeSetupSystem.Sender()];

    private static LianLiPairingResult Analyse(byte[] reply) => LianLiPairingPlanner.Analyse(OneReceiver, OneSender, reply);

    [Fact]
    public void Typical_case_proposes_every_device_and_the_adapter_accepts_all_of_them()
    {
        var result = Analyse(Reply.TypicalCase());

        result.State.Should().Be(LianLiPairingState.Ready);
        result.Channel.Should().Be(8);
        result.MasterMac.Should().Be("0a:0b:0c:0d:0e:0f");
        result.Proposals.Select(p => (p.Mac, p.RecType, p.FanNum, p.Leds, p.Rx)).Should().BeEquivalentTo(new[]
        {
            ("11:11:11:00:00:01", 28, 3, 78, 2),
            ("11:11:11:00:00:02", 28, 3, 78, 3),
            ("11:11:11:00:00:04", 51, 3, 78, 5),
            ("22:22:22:00:00:03", 10, 0, 24, 6),
        });

        // the adapter's own discovery (same validation as at Host start) must drive every proposal
        var options = new LianLiOptions { ReceiverInterfaceGuid = SetupInspector.LianLiInterfaceGuid };
        foreach (var p in result.Proposals)
        {
            options.Devices.Add(LianLiPairingPlanner.ToConfig(p));
        }

        var devices = LianLiDiscovery.Discover(options, new Enumerator(OneSender), NullLogger.Instance);
        devices.Select(d => d.Assembler.Family).Should().BeEquivalentTo(["TL", "TL", "TL", "H2"]);
        result.Proposals.Should().OnlyContain(p => p.Sender == 0);
    }

    [Theory]
    [InlineData(1, 26)]
    [InlineData(2, 52)]
    [InlineData(4, 104)]
    public void Tl_group_size_variants_get_the_vendor_led_count(int fans, int leds)
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1, fans: fans)));

        result.State.Should().Be(LianLiPairingState.Ready);
        result.Proposals.Single().Leds.Should().Be(leds);
    }

    [Fact]
    public void Duplicate_mac_rejects_the_whole_report()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1), Reply.Record(0x0000AA000001, 2)));

        result.State.Should().Be(LianLiPairingState.ReportInvalid);
        result.Proposals.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_rf_address_rejects_both_devices_but_keeps_the_others()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 4), Reply.Record(0x0000AA000002, 4), Reply.Record(0x0000AA000003, 5)));

        result.State.Should().Be(LianLiPairingState.Ready);
        result.Proposals.Select(p => p.Mac).Should().Equal("00:00:aa:00:00:03");
        result.Rejections.Where(r => r.Code == "DuplicateAddress").Should().HaveCount(2);
    }

    [Fact]
    public void Devices_of_a_neighbouring_sender_make_the_master_ambiguous()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1), Reply.Record(0x0000BB000001, 2, master: 0x0F0E0D0C0B0A)));

        result.State.Should().Be(LianLiPairingState.AmbiguousMaster);
        result.Proposals.Should().BeEmpty();
    }

    [Fact]
    public void A_known_master_resolves_neighbours_by_rejecting_them()
    {
        var result = LianLiPairingPlanner.Analyse(OneReceiver, OneSender,
            Reply.Of(Reply.Record(0x0000AA000001, 1), Reply.Record(0x0000BB000001, 2, master: 0x0F0E0D0C0B0A)),
            knownMaster: new Devices.LianLi.Protocol.RfMac(Reply.Master));

        result.Proposals.Select(p => p.Mac).Should().Equal("00:00:aa:00:00:01");
        result.Rejections.Single().Code.Should().Be("OtherMaster");
    }

    [Fact]
    public void Unpaired_devices_are_reported_not_proposed()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1), Reply.Record(0x0000AA000002, 2, master: 0)));

        result.State.Should().Be(LianLiPairingState.Ready);
        result.Proposals.Should().ContainSingle();
        result.Rejections.Single().Code.Should().Be("Unpaired");
    }

    [Theory]
    [InlineData(new byte[] { 28, 28, 52, 0 }, "MixedFanTypes")] // TL and TL-LCD in one group
    [InlineData(new byte[] { 0, 0, 0, 0 }, "MixedFanTypes")] // unknown fan type
    [InlineData(new byte[] { 20, 20, 20, 0 }, "UnvalidatedFamily")] // SL v3: recognised, never validated here
    [InlineData(new byte[] { 37, 37, 37, 0 }, "UnvalidatedFamily")] // INF
    public void Unsupported_or_mixed_groups_are_refused(byte[] fanTypes, string code)
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1, fanTypes: fanTypes)));

        result.Proposals.Should().BeEmpty();
        result.Rejections.Single().Code.Should().Be(code);
    }

    [Fact]
    public void H2_with_attached_fans_is_outside_what_the_adapter_drives()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1, devType: 10, fans: 2)));

        result.Proposals.Should().BeEmpty();
        result.Rejections.Single().Code.Should().Be("NotDrivable");
    }

    [Fact]
    public void Other_device_types_are_unsupported()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1, devType: 65, fans: 0)));

        result.Rejections.Single().Code.Should().Be("UnsupportedDeviceType");
    }

    [Fact]
    public void Different_channels_of_one_sender_are_refused_as_ambiguous()
    {
        var result = Analyse(Reply.Of(Reply.Record(0x0000AA000001, 1), Reply.Record(0x0000AA000002, 2, channel: 9)));

        result.State.Should().Be(LianLiPairingState.AmbiguousChannel);
        result.Proposals.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    public void Out_of_range_rx_slot_is_refused(byte rx)
    {
        Analyse(Reply.Of(Reply.Record(0x0000AA000001, rx))).Rejections.Single().Code.Should().Be("InvalidAddress");
    }

    [Fact]
    public void Truncated_report_is_refused()
    {
        var reply = Reply.TypicalCase();
        Analyse(reply[..^10]).State.Should().Be(LianLiPairingState.ReportInvalid);
    }

    public static TheoryData<string, LianLiPairingState> Inventories => new()
    {
        { "none", LianLiPairingState.NoReceiver },
        { "two-receivers", LianLiPairingState.MultipleReceivers },
        { "wch-receiver", LianLiPairingState.UnverifiedReceiver },
        { "no-sender", LianLiPairingState.NoSender },
        { "two-senders", LianLiPairingState.MultipleSenders },
    };

    [Theory]
    [MemberData(nameof(Inventories))]
    public void Inventory_problems_never_send_the_receiver_query(string inventory, LianLiPairingState expected)
    {
        var system = new FakeSetupSystem { Query = _ => Reply.TypicalCase() };
        system.AddPinnedResources();
        system.Interfaces.AddRange(inventory switch
        {
            "none" => [],
            "two-receivers" => [FakeSetupSystem.Sender(), FakeSetupSystem.Receiver("a"), FakeSetupSystem.Receiver("b")],
            "wch-receiver" => [FakeSetupSystem.Sender(), FakeSetupSystem.Usb(0x1A86, 0xE305, "c")],
            "no-sender" => [FakeSetupSystem.Receiver()],
            _ => [FakeSetupSystem.Sender("a"), FakeSetupSystem.Sender("b"), FakeSetupSystem.Receiver()],
        });

        var report = system.Inspect();

        report.LianLi.State.Should().Be(expected);
        system.Queries.Should().Be(0);
        report.Family(SetupFamily.LianLi).CanAuthorizeOutput.Should().BeFalse();
    }

    [Fact]
    public void A_failing_query_is_reported_and_inspection_continues()
    {
        var system = FakeSetupSystem.WithLianLi([]);
        system.Query = _ => throw new IOException("pipe timeout");

        var report = system.Inspect();

        system.Queries.Should().Be(1);
        report.LianLi.State.Should().Be(LianLiPairingState.QueryFailed);
        report.LianLi.Detail.Should().Contain("pipe timeout");
        report.Families.Should().HaveCount(7);
    }

    private sealed class Enumerator(IReadOnlyList<UsbDeviceInfo> devices) : IUsbEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass) => devices;
    }
}
