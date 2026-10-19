using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// NATIVE-RF-CLOCK: the RF sender caches its master counter from the USB 17 replies it already reads, and the adapter exposes
/// the open session's clock. No extra query, packet or handle; a successful query is not a playback ACK.
/// </summary>
public class LianLiClockTests
{
    [Fact]
    public void A_validated_reply_publishes_one_coherent_sample_stamped_at_read_completion()
    {
        var (sender, wire, stamps) = Sender();
        sender.Clock.Latest.Should().BeNull("nothing read yet");
        wire.EnqueueRead(Reply(0xF000_0001));
        sender.ReadMaster().ClockTicks.Should().Be(0xF000_0001);

        var s = sender.Clock.Latest!;
        s.Ticks.Should().Be(0xF000_0001u, "the raw unsigned counter, never a signed conversion");
        s.TicksPerSecond.Should().Be(RfSender.ClockTicksPerSecond).And.Be(1600);
        s.Domain.Kind.Should().Be(RfSender.ClockKind);
        s.QueryStartedTimestamp.Should().Be(stamps[0], "stamped before the query was sent");
        s.Timestamp.Should().Be(stamps[1], "stamped when the reply was read");
        s.QueryLatencySeconds.Should().BeGreaterThan(0);
        wire.SentFrames.Should().ContainSingle().Which.Should().Equal(UsbFrames.GetMac(8));
    }

    [Fact]
    public void Reading_the_clock_sends_nothing_and_returns_the_same_immutable_sample()
    {
        var (sender, wire, _) = Sender();
        wire.EnqueueRead(Reply(1234));
        sender.ReadMaster();
        var sent = wire.SentFrames.Count;
        var first = sender.Clock.Latest;
        for (var i = 0; i < 1000; i++)
        {
            sender.Clock.Latest.Should().BeSameAs(first);
        }

        wire.SentFrames.Should().HaveCount(sent, "a clock read is a cached reference read, no USB operation");
    }

    [Fact]
    public void A_missing_malformed_or_zero_reply_never_refreshes_the_sample_which_keeps_aging()
    {
        var (sender, wire, stamps) = Sender();
        wire.EnqueueRead(Reply(5000));
        sender.ReadMaster();
        var good = sender.Clock.Latest!;

        var wrongCmd = Reply(9000);
        wrongCmd[0] = 16;
        var zeroMac = Reply(9000);
        zeroMac.AsSpan(1, 6).Clear();
        byte[][] bad = [wrongCmd, zeroMac, Reply(0), Reply(9000)[..10]];
        foreach (var reply in bad)
        {
            wire.EnqueueRead(reply);
            var act = () => sender.ReadMaster();
            act.Should().Throw<InvalidDataException>();
            sender.Clock.Latest.Should().BeSameAs(good, "a rejected reply publishes nothing");
        }

        var none = () => sender.ReadMaster(); // nothing queued: a zero-length read
        none.Should().Throw<InvalidDataException>();
        sender.Clock.Latest.Should().BeSameAs(good);
        sender.Master!.Value.ClockTicks.Should().Be(5000u, "the master info is kept as well");
        good.AgeSeconds(stamps[^1]).Should().BeGreaterThan(0, "the kept sample ages with host time");
    }

    [Fact]
    public void A_fresh_sender_is_a_fresh_domain_and_a_disposed_one_has_no_sample()
    {
        var (a, wireA, _) = Sender();
        var (b, wireB, _) = Sender();
        wireA.EnqueueRead(Reply(100));
        wireB.EnqueueRead(Reply(100));
        a.ReadMaster();
        b.ReadMaster();
        a.Clock.Latest!.Domain.Should().NotBe(b.Clock.Latest!.Domain, "two opened senders are never one lock, even at equal counters");
        var domain = a.Clock.Latest!.Domain;
        wireA.EnqueueRead(Reply(200));
        a.ReadMaster();
        a.Clock.Latest!.Domain.Should().Be(domain, "one sender keeps its domain across queries");
        a.Dispose();
        a.Clock.Latest.Should().BeNull("a closed sender's last sample is not presented as current");
        b.Dispose();
    }

    [Fact]
    public async Task The_adapter_exposes_the_open_sessions_clock_and_a_reopen_gets_a_fresh_domain()
    {
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        var opens = 0;
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        options.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = "SLv3", Leds = 160, Channel = 8, Rx = 1 });
        options.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-02", Family = "CL", Leds = 96, Channel = 8, Rx = 2 });
        options.UseDeviceClock.Should().BeFalse("the source default is off");
        await using var a = new LianLiAdapter(options, usb, _ => { opens++; return wire; }, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => Rf.Now);
        a.ClockOf(new ControllerId("lianli:rf:AA-BB-CC-DD-EE-01")).Should().BeNull("not discovered");
        var found = await a.DiscoverAsync(CancellationToken.None);
        var (d1, d2) = (found[0], found[1]);
        var h1 = a.ClockOf(d1.Id)!;
        var h2 = a.ClockOf(d2.Id)!;
        h1.Latest.Should().BeNull("discovered, not open");

        wire.EnqueueRead(Reply(0x0002_0000));
        await a.OpenAsync(d1.Id, CancellationToken.None);
        await a.OpenAsync(d2.Id, CancellationToken.None);
        opens.Should().Be(1, "both chains share one sender; the clock opened no handle of its own");
        a.ClockOf(d1.Id).Should().BeSameAs(h1, "a stable handle per controller");
        h1.Latest.Should().NotBeNull();
        h2.Latest!.Domain.Should().Be(h1.Latest!.Domain, "one sender, one domain");
        var first = h1.Latest!.Domain;

        await a.CloseAsync(d1.Id);
        h1.Latest.Should().BeNull("a closed session reports no sample");
        h2.Latest.Should().NotBeNull("the other session still has the sender");
        await a.CloseAsync(d2.Id);
        h2.Latest.Should().BeNull();

        wire.EnqueueRead(Reply(0x0003_0000));
        await a.OpenAsync(d1.Id, CancellationToken.None);
        opens.Should().Be(2);
        h1.Latest!.Domain.Should().NotBe(first, "a reopened sender never continues an old domain");
        h2.Latest.Should().BeNull("d2 is still closed");
        await a.CloseAsync(d1.Id);
    }

    private static byte[] Reply(uint clock)
    {
        var r = Rf.MasterReply();
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(7), clock);
        return r;
    }

    private static (RfSender Sender, RecordingTransport Wire, List<long> Stamps) Sender()
    {
        var wire = new RecordingTransport();
        var stamps = new List<long>();
        var next = 1_000_000L;
        long Stamp()
        {
            next += 12_345;
            stamps.Add(next);
            return next;
        }

        var sender = new RfSender(new FakeUsb().Present[0], wire, new DeviceState(), 8, () => Rf.Now, _ => { }, Stamp);
        return (sender, wire, stamps);
    }
}
