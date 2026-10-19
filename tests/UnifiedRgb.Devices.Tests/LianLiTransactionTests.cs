using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// SPATIAL-PATH review R4: the keep-alive (USB 17 query + RF 20 ClockSync) never runs inside a transaction holding the
/// sender's <see cref="RfSender.UploadGate"/>; a skipped tick sends nothing, keeps the due time and does not re-stamp the clock.
/// Transaction isolation/recovery hardening only: no receiver ACK and no proof that the physical stale-update fault is solved.
/// </summary>
public class LianLiTransactionTests
{
    private static byte[] Reply(uint clock)
    {
        var r = Rf.MasterReply();
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(7), clock);
        return r;
    }

    private static int Queries(RecordingTransport wire) => wire.SentFrames.Count(f => f.AsSpan().SequenceEqual(UsbFrames.GetMac(8)));

    private static int ClockSyncs(RecordingTransport wire) => Rf.Group(wire.SentFrames).Count(g => g.Packet?[1] == (byte)RfCmd.ClockSync);

    /// <summary>A sender on a manual monotonic clock (seconds).</summary>
    private static (RfSender Sender, RecordingTransport Wire, Func<double, double> Advance) Sender()
    {
        var wire = new RecordingTransport();
        var t = 0.0;
        var stamp = 1_000_000L;
        var sender = new RfSender(new FakeUsb().Present[0], wire, new DeviceState(), 8, () => Rf.Now, _ => { },
            () => Interlocked.Add(ref stamp, 12_345), () => TimeSpan.FromSeconds(Volatile.Read(ref t)));
        return (sender, wire, s => t += s);
    }

    [Fact]
    public void A_tick_while_the_gate_is_held_sends_nothing_and_the_due_heartbeat_follows_the_release()
    {
        var (sender, wire, advance) = Sender();
        wire.EnqueueRead(Reply(1000));
        sender.Tick();
        (Queries(wire), ClockSyncs(wire)).Should().Be((1, 1), "the first heartbeat is due immediately");
        var first = sender.Clock.Latest!;
        first.Ticks.Should().Be(1000u);

        advance(5); // long overdue
        sender.UploadGate.Wait(0).Should().BeTrue("a transaction (upload / SaveConfig) holds the gate");
        wire.EnqueueRead(Reply(9000));
        for (var i = 0; i < 10; i++)
        {
            sender.Tick();
        }

        wire.SentFrames.Should().HaveCount(1 + 4, "a skipped tick inserts no USB 17 and no RF 20");
        sender.Clock.Latest.Should().BeSameAs(first, "a skip never re-stamps the stale sample; it keeps aging");
        sender.UploadGate.Release();

        sender.Tick(); // due time was not advanced by the skips: the first tick after release is the heartbeat
        (Queries(wire), ClockSyncs(wire)).Should().Be((2, 2));
        sender.Clock.Latest.Should().NotBeSameAs(first);
        sender.Clock.Latest!.Ticks.Should().Be(9000u, "a fresh sample from the existing query");
        sender.UploadGate.CurrentCount.Should().Be(1, "released in finally");

        sender.Tick();
        (Queries(wire), ClockSyncs(wire)).Should().Be((2, 2), "not due again within the period");
    }

    [Fact]
    public void A_failed_query_inside_a_due_tick_still_releases_the_gate()
    {
        var (sender, wire, advance) = Sender();
        wire.EnqueueRead(Reply(1000));
        sender.Tick();
        advance(1);
        sender.Tick(); // nothing queued: the query fails and the last master is kept
        (Queries(wire), ClockSyncs(wire)).Should().Be((2, 2));
        sender.UploadGate.CurrentCount.Should().Be(1);
    }

    [Fact]
    public void Concurrent_ticks_send_exactly_one_heartbeat_per_due_period()
    {
        var (sender, wire, advance) = Sender();
        const int Rounds = 20;
        const int Threads = 8;
        using var barrier = new Barrier(Threads, _ => advance(1)); // post-phase: the next period becomes due
        var workers = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
        {
            for (var r = 0; r < Rounds; r++)
            {
                sender.Tick();
                barrier.SignalAndWait();
            }
        })).ToArray();
        for (var r = 0; r < Rounds; r++)
        {
            wire.EnqueueRead(Reply((uint)(r + 1)));
        }

        foreach (var w in workers)
        {
            w.Start();
        }

        foreach (var w in workers)
        {
            w.Join();
        }

        (Queries(wire), ClockSyncs(wire)).Should().Be((Rounds, Rounds), "the due recheck after acquisition keeps one heartbeat per period");
        sender.UploadGate.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task An_in_flight_upload_stays_contiguous_while_concurrent_and_reentrant_keep_alives_are_skipped()
    {
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        LianLiAdapter? adapter = null;
        var reentrant = 0;
        var blockOnce = 0;
        void Sleep(TimeSpan t)
        {
            if (t == RfPackets.UploadHeaderRepeatDelay && Interlocked.Exchange(ref blockOnce, 1) == 0)
            {
                // inside Upload, between header sends, on the upload thread that holds the gate: a reentrant keep-alive
                adapter!.KeepAlive();
                Interlocked.Increment(ref reentrant);
                blocked.Set();
                release.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            }
        }

        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        options.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = "SLv3", Leds = 160, Channel = 8, Rx = 1 });
        await using var a = new LianLiAdapter(options, usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), Sleep, () => Rf.Now);
        adapter = a;
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        var clock = a.ClockOf(d.Id)!;
        await WaitUntil(() => ClockSyncs(wire) >= 1); // the timer's immediate first heartbeat
        var before = clock.Latest;

        var upload = Task.Run(() => a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone,
            new LoopFrames(2, 160, 20, 7, TinyuzEncoder.SampleAnimation(2 * 160 * 3)), CancellationToken.None));
        blocked.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the upload is parked between its header sends");
        reentrant.Should().Be(1, "the reentrant keep-alive returned instead of deadlocking on the gate");

        // concurrent keep-alives (the test thread and the real 250 ms timer) across more than one due period
        var queries = Queries(wire);
        var syncs = ClockSyncs(wire);
        wire.EnqueueRead(Reply(0xF000_0000));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        while (DateTime.UtcNow < deadline)
        {
            var call = Task.Run(a.KeepAlive);
            (await Task.WhenAny(call, Task.Delay(5000))).Should().BeSameAs(call, "a skipped keep-alive returns at once");
            await Task.Delay(50);
        }

        (Queries(wire), ClockSyncs(wire)).Should().Be((queries, syncs), "nothing is inserted while the upload holds the gate");
        clock.Latest.Should().BeSameAs(before, "the stale sample is not re-stamped by a skipped tick");

        release.Set();
        await upload;
        await WaitUntil(() => clock.Latest?.Ticks == 0xF000_0000u); // the next due tick: existing query + ClockSync
        await WaitUntil(() => ClockSyncs(wire) > syncs);

        // the whole upload (4 header sends + data) is one contiguous run of RF 32 packets
        var groups = Rf.Group(wire.SentFrames);
        var first = groups.FindIndex(g => g.Packet?[1] == (byte)RfCmd.RgbSync);
        var last = groups.FindLastIndex(g => g.Packet?[1] == (byte)RfCmd.RgbSync);
        first.Should().BeGreaterThanOrEqualTo(0);
        groups.Skip(first).Take(last - first + 1).Should().OnlyContain(g => g.Packet != null && g.Packet[1] == (byte)RfCmd.RgbSync,
            "no USB 17 query or RF 20 ClockSync between the first header and the last data packet");
        groups.Skip(last + 1).Should().Contain(g => g.Packet == null).And.Contain(g => g.Packet != null && g.Packet[1] == (byte)RfCmd.ClockSync,
            "the heartbeat resumes after the upload");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the condition should hold within 10 s");
            await Task.Delay(20);
        }
    }
}
