using System.Buffers.Binary;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Transports.WinUsb;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Devices.Safety;

namespace UnifiedRgb.Devices.Tests;

internal static class RfTrackerTestClock
{
    public static bool ObserveAt(this RfDeliveryTracker tracker, RfReceiverReport? report, DateTimeOffset at, bool retry) =>
        tracker.Observe(report, at, retry, at - new DateTimeOffset(Rf.Now));
}

public class RfDeliveryTests
{
    private static readonly RfMac Mac = RfMac.Parse("AA-BB-CC-DD-EE-01");
    private static readonly RfAddress Address = new(8, 3);
    private static readonly DateTimeOffset Epoch = new(Rf.Now);

    private static byte[] Report(uint index, uint counter, RfMac? mac = null, RfMac? master = null, byte rx = 3)
    {
        var data = new byte[64]; data[0] = 16; data[1] = 1;
        var record = data.AsSpan(4, 42);
        (mac ?? Mac).Write(record); (master ?? Rf.Master).Write(record[6..]);
        record[12] = 8; record[13] = rx;
        BinaryPrimitives.WriteUInt32BigEndian(record[14..], counter);
        BinaryPrimitives.WriteUInt32BigEndian(record[20..], index);
        record[18] = 10; record[41] = 28;
        return data;
    }

    private static RfReceiverReport R(uint index, uint counter) => RfReceiverReport.ParseComplete(Report(index, counter)).Single();
    private static RfDeliveryTracker Tracker() => new(100, Mac, Rf.Master, Address, TimeSpan.Zero);

    [Fact]
    public void Complete_list_carries_receiver_counter_and_rejects_partial_or_duplicate_records()
    {
        var bytes = Report(123, 999);
        var report = RfReceiverReport.ParseComplete(bytes).Single();
        (report.Counter, report.Device.EffectIndex, report.Device.Mac).Should().Be((999u, 123u, Mac));
        Action partial = () => RfReceiverReport.ParseComplete(bytes.AsSpan(0, 45));
        partial.Should().Throw<InvalidDataException>();
        bytes[45] = 0;
        Action tag = () => RfReceiverReport.ParseComplete(bytes);
        tag.Should().Throw<InvalidDataException>();
        bytes = Report(123, 999);
        var duplicate = new byte[128]; bytes.CopyTo(duplicate, 0); bytes.AsSpan(4, 42).CopyTo(duplicate.AsSpan(46)); duplicate[1] = 2;
        Action duplicateAction = () => RfReceiverReport.ParseComplete(duplicate);
        duplicateAction.Should().Throw<InvalidDataException>();
        duplicate[1] = 11;
        Action unsupportedPage = () => RfReceiverReport.ParseComplete(duplicate);
        unsupportedPage.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 50)] // actual 10050032 PWM-counter header
    [InlineData(128, 16)] // actual 10058010 firmware-version header
    public void Version_or_pwm_header_fields_are_not_a_receiver_list_error(byte high, byte low)
    {
        var bytes = Report(123, 999); bytes[2] = high; bytes[3] = low;
        var record = RfReceiverReport.ParseComplete(bytes).Single();
        (record.Counter, record.Device.EffectIndex).Should().Be((999u, 123u));
    }

    [Fact]
    public void A_matching_token_requires_two_progressing_reports_and_never_means_optical_success()
    {
        var t = Tracker();
        t.ObserveAt(R(100, 1000), Epoch.AddSeconds(1), true).Should().BeFalse();
        t.Status.State.Should().Be("Unknown");
        t.ObserveAt(R(100, 2600), Epoch.AddSeconds(2), true).Should().BeFalse();
        t.Status.State.Should().Be("ReportedMatch");
        t.Status.Detail.Should().Contain("not verified");
        t.Status.Retries.Should().Be(0);
    }

    [Theory]
    [InlineData(1000u)] // cached counter
    [InlineData(10u)] // reset/backwards, rather than a small wrap
    [InlineData(999999u)] // implausible progress
    public void Stale_reset_or_implausible_counters_do_not_trigger_retries(uint next)
    {
        var t = Tracker();
        t.ObserveAt(R(5, 1000), Epoch.AddSeconds(1), true);
        t.ObserveAt(R(5, next), Epoch.AddSeconds(2), true).Should().BeFalse();
        t.Status.State.Should().Be("Unknown");
    }

    [Fact]
    public void Counter_wrap_is_allowed_only_when_the_unsigned_progress_is_plausible()
    {
        var t = Tracker();
        t.ObserveAt(R(100, uint.MaxValue - 799), Epoch.AddSeconds(1), true);
        t.ObserveAt(R(100, 800), Epoch.AddSeconds(2), true).Should().BeFalse();
        t.Status.State.Should().Be("ReportedMatch");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Missing_wrong_device_master_or_address_is_unknown(int kind)
    {
        var t = Tracker();
        var wrong = kind switch
        {
            0 => null,
            1 => RfReceiverReport.ParseComplete(Report(1, 2600, mac: Rf.Master)).Single(),
            2 => RfReceiverReport.ParseComplete(Report(1, 2600, master: Mac)).Single(),
            _ => RfReceiverReport.ParseComplete(Report(1, 2600, rx: 4)).Single(),
        };
        t.ObserveAt(R(1, 1000), Epoch.AddSeconds(1), true);
        t.ObserveAt(wrong, Epoch.AddSeconds(2), true).Should().BeFalse();
        t.Status.State.Should().Be("Unknown");
    }

    [Fact]
    public void Mismatches_need_repeated_fresh_evidence_and_retries_are_bounded_to_two()
    {
        var t = Tracker();
        t.ObserveAt(R(5, 1000), Epoch.AddSeconds(1), true).Should().BeFalse();
        t.ObserveAt(R(5, 2600), Epoch.AddSeconds(2), true).Should().BeFalse();
        t.ObserveAt(R(5, 4200), Epoch.AddSeconds(3), true).Should().BeTrue();
        t.Retried(TimeSpan.FromSeconds(3));
        t.ObserveAt(R(5, 5800), Epoch.AddSeconds(4), true).Should().BeFalse();
        t.ObserveAt(R(5, 7400), Epoch.AddSeconds(5), true).Should().BeTrue();
        t.Retried(TimeSpan.FromSeconds(5)); t.Failed("USB failure", false);
        for (var i = 6; i < 12; i++) { t.ObserveAt(R(5, (uint)(1000 + (i - 1) * 1600)), Epoch.AddSeconds(i), true).Should().BeFalse(); }
        (t.Status.Retries, t.Status.State).Should().Be((2, "RetryExhausted"));
        t.ObserveAt(R(100, 18600), Epoch.AddSeconds(12), true).Should().BeFalse();
        t.Status.State.Should().Be("ReportedMatch", "a late report may converge, without granting another retry budget");
    }

    [Fact]
    public void Observation_only_and_expired_recovery_window_do_not_send()
    {
        var t = Tracker();
        for (var i = 1; i < 20; i++) { t.ObserveAt(R(5, (uint)(i * 1600)), Epoch.AddSeconds(i), false).Should().BeFalse(); }
        t.ObserveAt(R(5, 32000), Epoch.AddSeconds(20), true).Should().BeFalse();
        t.Status.State.Should().Be("RetryExhausted");
        t.Status.Retries.Should().Be(0);
    }

    [Fact]
    public void A_new_tracker_with_a_colliding_token_still_needs_its_own_fresh_counter_baseline()
    {
        var old = Tracker(); old.ObserveAt(R(100, 1000), Epoch.AddSeconds(1), true); old.ObserveAt(R(100, 2600), Epoch.AddSeconds(2), true);
        var nextSession = new RfDeliveryTracker(100, Mac, Rf.Master, Address, TimeSpan.FromSeconds(3));
        nextSession.ObserveAt(R(100, 4200), Epoch.AddSeconds(4), true).Should().BeFalse();
        nextSession.Status.State.Should().Be("Unknown");
    }

    [Fact]
    public void A_missing_list_or_long_gap_invalidates_previous_freshness_evidence()
    {
        var t = Tracker();
        t.ObserveAt(R(5, 1000), Epoch.AddSeconds(1), true);
        t.ObserveAt(R(5, 2600), Epoch.AddSeconds(2), true);
        t.Unknown("receiver unplugged");
        t.ObserveAt(R(5, 4200), Epoch.AddSeconds(3), true).Should().BeFalse();
        t.Status.State.Should().Be("Unknown");
        t.ObserveAt(R(5, 20200), Epoch.AddSeconds(13), true).Should().BeFalse();
        t.Status.State.Should().Be("Unknown");
    }

    [Fact]
    public void Clock_adjustment_does_not_change_counter_freshness_backoff_or_deadline()
    {
        var t = Tracker();
        t.Observe(R(5, 1000), Epoch, true, TimeSpan.FromSeconds(1));
        t.Observe(R(5, 2600), Epoch.AddHours(-2), true, TimeSpan.FromSeconds(2)).Should().BeFalse();
        t.Observe(R(5, 4200), Epoch.AddHours(2), true, TimeSpan.FromSeconds(3)).Should().BeTrue();
        t.CanCommitRetry(TimeSpan.FromSeconds(15)).Should().BeFalse("expiry is rechecked at actual commit, after slow I/O");
        t.Retried(TimeSpan.FromSeconds(3));
        t.CanCommitRetry(TimeSpan.FromSeconds(4)).Should().BeFalse();
        t.Snapshot(TimeSpan.FromSeconds(10)).State.Should().Be("Unknown");
    }

    [Fact]
    public async Task Failed_probe_is_unknown_and_never_causes_an_automatic_upload()
    {
        var wire = new RecordingTransport(); wire.EnqueueRead(Rf.MasterReply());
        var now = Rf.Now;
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid, ReceiverFeedbackEnabled = true, ReceiverRetriesEnabled = true };
        options.Devices.Add(new LianLiDeviceConfig { Mac = Mac.ToString(), Family = "H2", Leds = 24, Channel = 8, Rx = 3 });
        await using var adapter = new LianLiAdapter(options, new FakeUsb(), _ => wire, encoder: new IdentityEncoder(), sleep: _ => { }, now: () => now,
            receiverProbe: () => throw new IOException("receiver unplugged"), startKeepAliveTimer: false, feedbackElapsed: () => now - Rf.Now);
        var id = (await adapter.DiscoverAsync(CancellationToken.None)).Single().Id;
        await adapter.OpenAsync(id, CancellationToken.None);
        adapter.SetRecoveryAllowed(id, true);
        await adapter.UploadLoopAsync(id, LianLiAdapter.ChainZone, new LoopFrames(1, 24, 20, 100, new byte[72]), CancellationToken.None);
        var count = wire.SentFrames.Count;
        for (var i = 1; i < 10; i++) { now = Rf.Now.AddSeconds(i); adapter.ObserveReceiverFeedback(); }
        wire.SentFrames.Should().HaveCount(count);
        adapter.LastUpload(id)!.ReceiverDelivery!.State.Should().Be("Unknown");
        adapter.LastUpload(id)!.ReceiverDelivery!.Detail.Should().Contain("unplugged");
        options.ReceiverFeedbackEnabled = false;
        now = now.AddSeconds(2); adapter.ObserveReceiverFeedback();
        wire.SentFrames.Should().HaveCount(count);
    }

    [Fact]
    public async Task Non_io_failure_after_a_retry_header_is_charged_and_blocks_further_automatic_attempts()
    {
        var wire = new RecordingTransport(); wire.EnqueueRead(Rf.MasterReply());
        var now = Rf.Now; var counter = 0u; var fail = false;
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid, ReceiverFeedbackEnabled = true, ReceiverRetriesEnabled = true };
        options.Devices.Add(new LianLiDeviceConfig { Mac = Mac.ToString(), Family = "H2", Leds = 24, Channel = 8, Rx = 3 });
        await using var adapter = new LianLiAdapter(options, new FakeUsb(), _ => wire, encoder: new IdentityEncoder(),
            sleep: _ => { if (fail) { throw new FrameRejectedException(FrameRejectReason.DeviceOffline, 64); } }, now: () => now,
            receiverProbe: () => Report(5, counter), startKeepAliveTimer: false, feedbackElapsed: () => now - Rf.Now);
        var id = (await adapter.DiscoverAsync(CancellationToken.None)).Single().Id;
        await adapter.OpenAsync(id, CancellationToken.None);
        adapter.SetRecoveryAllowed(id, true);
        await adapter.UploadLoopAsync(id, LianLiAdapter.ChainZone, new LoopFrames(1, 24, 20, 100, new byte[72]), CancellationToken.None);
        var originalCount = wire.SentFrames.Count;
        fail = true;
        for (var i = 1; i <= 3; i++) { now = Rf.Now.AddSeconds(i); counter = (uint)(i * 1600); adapter.ObserveReceiverFeedback(); }
        wire.SentFrames.Should().HaveCount(originalCount + 4, "one retry header was committed before the non-IO failure");
        adapter.LastUpload(id)!.ReceiverDelivery!.Retries.Should().Be(1, "the attempt is charged at commit even when it does not return normally");
        for (var i = 4; i < 12; i++) { now = Rf.Now.AddSeconds(i); counter = (uint)(i * 1600); adapter.ObserveReceiverFeedback(); }
        wire.SentFrames.Should().HaveCount(originalCount + 4);
        adapter.LastUpload(id)!.ReceiverDelivery!.State.Should().Be("Unknown");
        adapter.LastUpload(id)!.ReceiverDelivery!.Detail.Should().Contain("DeviceOffline");
    }

    [Theory]
    [InlineData("0416", "8040")] [InlineData("1cbe", "0006")] [InlineData("1a86", "e305")] [InlineData("1234", "8041")]
    public void Receiver_query_cannot_open_a_sender_screen_or_unmeasured_interface(string vid, string pid)
    {
        var device = UsbDeviceInfo.FromPath($@"\\?\usb#vid_{vid}&pid_{pid}#6&a&0&1#{{{FakeUsb.Guid}}}")!;
        RfReceiverQuery.Supports(device).Should().BeFalse();
        Action query = () => RfReceiverQuery.ReadList(device);
        query.Should().Throw<ArgumentException>("must reject before any native call");
    }

    [Fact]
    public async Task Adapter_retries_exact_packets_without_changing_timing_or_generation_and_stops_after_match()
    {
        var usb = new FakeUsb(); var wire = new RecordingTransport(); wire.EnqueueRead(Rf.MasterReply());
        var now = Rf.Now; var index = 5u; var counter = 0u;
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid, ReceiverFeedbackEnabled = true, ReceiverRetriesEnabled = true };
        options.Devices.Add(new LianLiDeviceConfig { Mac = Mac.ToString(), Family = "H2", Leds = 24, Channel = 8, Rx = 3 });
        await using var adapter = new LianLiAdapter(options, usb, _ => wire, encoder: new IdentityEncoder(), sleep: _ => { }, now: () => now,
            receiverProbe: () => Report(index, counter), startKeepAliveTimer: false, feedbackElapsed: () => now - Rf.Now);
        var id = (await adapter.DiscoverAsync(CancellationToken.None)).Single().Id;
        await adapter.OpenAsync(id, CancellationToken.None);
        await adapter.UploadLoopAsync(id, LianLiAdapter.ChainZone, new LoopFrames(1, 24, 20, 100, new byte[72]), CancellationToken.None);
        var original = adapter.LastUpload(id)!;
        adapter.SetRecoveryAllowed(id, true);
        var upload = Rf.Group(wire.SentFrames).Where(g => g.Packet is not null).Select(g => g.Packet!).ToArray();
        for (var i = 1; i <= 3; i++) { now = Rf.Now.AddSeconds(i); counter = (uint)(i * 1600); adapter.ObserveReceiverFeedback(); }
        var afterRetry = Rf.Group(wire.SentFrames).Where(g => g.Packet is not null).Select(g => g.Packet!).ToArray();
        afterRetry.Length.Should().Be(upload.Length * 2);
        for (var i = 0; i < upload.Length; i++) { afterRetry[i + upload.Length].Should().Equal(upload[i]); }
        adapter.LastUpload(id)!.StartedTimestamp.Should().Be(original.StartedTimestamp, "a same-generation recovery must not re-anchor native companions");
        adapter.LastUpload(id)!.ReceiverDelivery!.Retries.Should().Be(1);
        adapter.SetRecoveryAllowed(id, false);
        for (var i = 4; i <= 5; i++) { now = Rf.Now.AddSeconds(i); counter += 1600; adapter.ObserveReceiverFeedback(); }
        Rf.Group(wire.SentFrames).Count(g => g.Packet is not null).Should().Be(afterRetry.Length, "a paused/contended writer forbids autonomous recovery too");
        index = 100; now = Rf.Now.AddSeconds(6); counter += 1600; adapter.ObserveReceiverFeedback();
        adapter.LastUpload(id)!.ReceiverDelivery!.State.Should().Be("ReportedMatch");
        Rf.Group(wire.SentFrames).Count(g => g.Packet is not null).Should().Be(afterRetry.Length);
        await adapter.CloseAsync(id);
        adapter.LastUpload(id)!.ReceiverDelivery!.State.Should().Be("Unknown");
        adapter.LastUpload(id)!.ReceiverDelivery!.ObservedAt.Should().BeNull();
        wire.EnqueueRead(Rf.MasterReply());
        await adapter.OpenAsync(id, CancellationToken.None);
        adapter.LastUpload(id)!.ReceiverDelivery!.ReportedEffectIndex.Should().BeNull();
        await adapter.UploadLoopAsync(id, LianLiAdapter.ChainZone, new LoopFrames(1, 24, 20, 100, new byte[72]), CancellationToken.None);
        now = Rf.Now.AddSeconds(7); counter += 1600; adapter.ObserveReceiverFeedback();
        adapter.LastUpload(id)!.ReceiverDelivery!.State.Should().Be("Unknown", "a reopened session cannot inherit the previous counter baseline even with an identical numeric token");
        now = Rf.Now.AddSeconds(8); counter += 1600; adapter.ObserveReceiverFeedback();
        adapter.LastUpload(id)!.ReceiverDelivery!.State.Should().Be("ReportedMatch");
    }

    [Fact]
    public async Task Scene_change_during_observation_invalidates_old_recovery_before_first_header()
    {
        var wire = new RecordingTransport(); wire.EnqueueRead(Rf.MasterReply());
        var now = Rf.Now; var calls = 0; var counter = 0u;
        using var observed = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid, ReceiverFeedbackEnabled = true, ReceiverRetriesEnabled = true };
        options.Devices.Add(new LianLiDeviceConfig { Mac = Mac.ToString(), Family = "H2", Leds = 24, Channel = 8, Rx = 3 });
        await using var adapter = new LianLiAdapter(options, new FakeUsb(), _ => wire, encoder: new IdentityEncoder(), sleep: _ => { }, now: () => now,
            receiverProbe: () => { if (++calls == 3) { observed.Set(); release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(); } return Report(5, counter); }, startKeepAliveTimer: false, feedbackElapsed: () => now - Rf.Now);
        var id = (await adapter.DiscoverAsync(CancellationToken.None)).Single().Id;
        await adapter.OpenAsync(id, CancellationToken.None);
        adapter.SetRecoveryAllowed(id, true);
        LoopFrames Loop(uint value) => new(1, 24, 20, value, new byte[72]);
        await adapter.UploadLoopAsync(id, LianLiAdapter.ChainZone, Loop(100), CancellationToken.None);
        for (var i = 1; i <= 2; i++) { now = Rf.Now.AddSeconds(i); counter = (uint)(i * 1600); adapter.ObserveReceiverFeedback(); }
        now = Rf.Now.AddSeconds(3); counter = 4800;
        var observation = Task.Run(adapter.ObserveReceiverFeedback);
        observed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        var newer = adapter.UploadLoopAsync(id, LianLiAdapter.ChainZone, Loop(200), CancellationToken.None); // invalidates before awaiting the held gate
        release.Set(); await observation; await newer;
        var headers = Rf.Group(wire.SentFrames).Where(g => g.Packet is { } p && p[18] == 0).Select(g => BinaryPrimitives.ReadUInt32BigEndian(g.Packet!.AsSpan(14))).ToArray();
        headers.Should().Equal(100u, 100u, 100u, 100u, 200u, 200u, 200u, 200u);
        adapter.LastUpload(id)!.EffectIndex.Should().Be(200);
        adapter.LastUpload(id)!.ReceiverDelivery!.Retries.Should().Be(0);
    }

    [Fact]
    public async Task Multiple_senders_expose_unknown_pairing_without_queries_retries_or_delivery_claims()
    {
        var usb = new FakeUsb();
        usb.Present.Add(UsbDeviceInfo.FromPath(@"\\?\usb#vid_0416&pid_8040#6&zz&0&7#{" + FakeUsb.Guid + "}")!);
        var wires = new Dictionary<string, RecordingTransport>(StringComparer.OrdinalIgnoreCase);
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid, ReceiverFeedbackEnabled = true, ReceiverRetriesEnabled = true };
        options.Devices.Add(new() { Mac = Mac.ToString(), Family = "H2", Leds = 24, Channel = 8, Rx = 3, Sender = 0 });
        options.Devices.Add(new() { Mac = "AA-BB-CC-DD-EE-02", Family = "H2", Leds = 24, Channel = 8, Rx = 4, Sender = 1 });
        var queries = 0;
        var now = Rf.Now;
        await using var adapter = new LianLiAdapter(options, usb, info =>
        {
            var wire = new RecordingTransport(); wire.EnqueueRead(Rf.MasterReply()); wires[info.Path] = wire; return wire;
        }, encoder: new IdentityEncoder(), sleep: _ => { }, now: () => now,
            receiverProbe: () => { queries++; return Report(100, 1600); }, startKeepAliveTimer: false, feedbackElapsed: () => now - Rf.Now);
        var found = await adapter.DiscoverAsync(default);
        found.Should().HaveCount(2);
        foreach (var device in found)
        {
            await adapter.OpenAsync(device.Id, default);
            adapter.SetRecoveryAllowed(device.Id, true);
            await adapter.UploadLoopAsync(device.Id, LianLiAdapter.ChainZone, new LoopFrames(1, 24, 20, 100, new byte[72]), default);
        }
        var before = wires.Values.Sum(w => w.SentFrames.Count);
        now = now.AddSeconds(2);
        adapter.ObserveReceiverFeedback();
        queries.Should().Be(0);
        wires.Values.Sum(w => w.SentFrames.Count).Should().Be(before);
        foreach (var device in found)
        {
            var delivery = adapter.LastUpload(device.Id)!.ReceiverDelivery!;
            delivery.State.Should().Be("Unknown");
            delivery.Detail.Should().Contain("multiple RF senders");
            delivery.Retries.Should().Be(0);
        }
    }
}
