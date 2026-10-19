using System.Buffers.Binary;
using System.Diagnostics;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// RF 22 LCD-group reset (OPUS-LCD-RESTART-UI-BACKEND): literal packet against the 21:09 hardware-accepted prefix, rfList index
/// without the master record, live pins, sequence wrap, the fresh-acknowledgement rule and its three-send bound, the unchanged
/// deny lists, and transaction isolation from uploads, keep-alive and the feedback observation.
/// </summary>
public class LianLiLcdResetTests
{
    private static readonly RfMac Bottom = RfMac.Parse("35-F7-EF-72-AB-3C");
    private static readonly RfMac Top = RfMac.Parse("1B-7B-F1-72-AB-3C");
    private static readonly RfMac Waterblock = RfMac.Parse("EF-7E-63-62-32-E1");
    private static readonly RfMac Side = RfMac.Parse("06-A8-F0-72-AB-3C");
    private static readonly RfAddress BottomAt = new(8, 5);

    private sealed record Rec(RfMac Mac, RfMac MasterMac, byte Channel, byte Rx, byte DevType, byte Fans, byte FanType, byte CmdSeq, uint Counter, byte Pwm = 137);

    private static Rec Lcd(byte seq = 0, uint counter = 1000, byte pwm = 137, RfMac? master = null, byte rx = 5) =>
        new(Bottom, master ?? Rf.Master, 8, rx, 0, 3, 35, seq, counter, pwm);

    private static Rec MasterRecord(RfMac? mac = null) => new(mac ?? Rf.Master, mac ?? Rf.Master, 8, 0, 255, 0, 0, 0, 1);

    /// <summary>A complete receiver list reply: [16][count][hdr][hdr] then 42-byte records, as RefreshList reads it.</summary>
    private static byte[] List(params Rec[] records)
    {
        var data = new byte[4 + (records.Length * 42)];
        data[0] = 16;
        data[1] = (byte)records.Length;
        data[2] = 0x80; // firmware-version header: not an error code
        data[3] = 0x10;
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            var o = data.AsSpan(4 + (i * 42), 42);
            r.Mac.Write(o);
            r.MasterMac.Write(o[6..]);
            (o[12], o[13]) = (r.Channel, r.Rx);
            BinaryPrimitives.WriteUInt32BigEndian(o[14..], r.Counter);
            o[18] = r.DevType;
            o[19] = r.Fans;
            for (var f = 0; f < r.Fans; f++)
            {
                o[24 + f] = r.FanType;
            }

            for (var p = 0; p < 4; p++)
            {
                o[36 + p] = r.Pwm;
            }

            o[40] = r.CmdSeq;
            o[41] = 28;
        }

        return data;
    }

    /// <summary>The live order of the 21:09 read: top, H2, bottom, side, plus the master; here the master sits SECOND.</summary>
    private static byte[] Live(Rec bottom, Rec? master = null) => List(
        new Rec(Top, Rf.Master, 8, 2, 0, 3, 28, 0, bottom.Counter),
        master ?? MasterRecord(),
        new Rec(Waterblock, Rf.Master, 8, 3, 10, 1, 0, 0, bottom.Counter),
        bottom,
        new Rec(Side, Rf.Master, 8, 4, 0, 3, 28, 0, bottom.Counter));

    [Fact]
    public void Packet_matches_the_hardware_accepted_bytes_with_a_zero_tail_and_no_pwm()
    {
        var master = RfMac.Parse("DC-BA-7E-72-AB-3C");
        var p = RfLcdGroupReset.Packet(Bottom, master, BottomAt, slaveIndex: 2, sequence: 1);
        p.Should().HaveCount(240);
        Convert.ToHexString(p.AsSpan(0, 18)).Should().Be("121635F7EF72AB3CDCBA7E72AB3C05080201");
        p.AsSpan(18).ToArray().Should().OnlyContain(b => b == 0, "SyncControlInfo array8 leaves [18..240) zero: no PWM/value bytes");
        var frames = RfRelay.Frames(BottomAt, p);
        frames.Should().HaveCount(4).And.OnlyContain(f => f.Length == 64);
        (frames[0][0], frames[0][2], frames[0][3], frames[0][4], frames[0][5]).Should().Be(((byte)16, (byte)8, (byte)5, (byte)18, (byte)22));
        frames.SelectMany(f => f.Skip(4)).Should().Equal(p);
        OpcodeFirewall.Check(frames[0], OpcodeLayout.LianLi).Should().Be(0, "RF 22 is not on the deny list");
    }

    [Fact]
    public void Deny_lists_are_unchanged_and_rf22_is_not_an_rfcmd_member()
    {
        OpcodeFirewall.DeniedRfOpcodes.Should().Equal(23, 24, 33);
        OpcodeFirewall.DeniedUsbOpcodes.Should().Equal(32, 33, 34, 35);
        Enum.GetValues<RfCmd>().Select(v => (byte)v).Should().NotContain(RfLcdGroupReset.Opcode, "only the LCD-reset builder may emit 22");
        foreach (var denied in new byte[] { 23, 24, 33 })
        {
            var p = RfLcdGroupReset.Packet(Bottom, Rf.Master, BottomAt, 2, 1);
            p[1] = denied;
            FluentActions.Invoking(() => OpcodeFirewall.Check(RfRelay.Frames(BottomAt, p)[0], OpcodeLayout.LianLi)).Should().Throw<ForbiddenOpcodeException>();
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(253, 254)]
    [InlineData(254, 1)] // 255 is never sent: >= 255 wraps to 1
    [InlineData(255, 1)]
    public void Sequence_increments_and_wraps_to_one(byte current, byte next)
    {
        RfLcdGroupReset.NextSequence(current).Should().Be(next);
        FluentActions.Invoking(() => RfLcdGroupReset.Packet(Bottom, Rf.Master, BottomAt, 0, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Slave_index_excludes_the_master_record_and_ignores_configured_order()
    {
        var list = RfReceiverReport.ParseComplete(Live(Lcd()));
        list.Should().HaveCount(5);
        var target = RfLcdGroupReset.Resolve(list, Bottom, Rf.Master, BottomAt, 3);
        target.SlaveIndex.Should().Be(2, "rfList is top, H2, bottom, side; counting the master record would give 3");
        target.Report.Device.Mac.Should().Be(Bottom);
    }

    public static TheoryData<string, byte[]> WrongLists => new()
    {
        { "no master record", List(Lcd()) },
        { "master record of another dongle", Live(Lcd(), MasterRecord(RfMac.Parse("01-02-03-04-05-06"))) },
        { "two master records", List(MasterRecord(), Lcd(), new Rec(RfMac.Parse("01-02-03-04-05-06"), Rf.Master, 8, 0, 255, 0, 0, 0, 1)) },
        { "target bound to another master", List(MasterRecord(), Lcd(master: RfMac.Parse("01-02-03-04-05-06"))) },
        { "target missing", List(MasterRecord(), new Rec(Side, Rf.Master, 8, 4, 0, 3, 28, 0, 1)) },
        { "different rx", Live(Lcd(rx: 6)) },
        { "non-LCD TL fans", Live(Lcd() with { FanType = 28 }) },
        { "other LCD type not verified", Live(Lcd() with { FanType = 36 }) },
        { "two fans", Live(Lcd() with { Fans = 2 }) },
        { "waterblock dev type", Live(Lcd() with { DevType = 10 }) },
    };

    [Theory]
    [MemberData(nameof(WrongLists))]
    public void Live_target_master_address_and_type_are_pinned(string why, byte[] reply)
    {
        FluentActions.Invoking(() => RfLcdGroupReset.Resolve(RfReceiverReport.ParseComplete(reply), Bottom, Rf.Master, BottomAt, 3))
            .Should().Throw<InvalidDataException>(why);
    }

    [Fact]
    public async Task Stale_report_is_not_acceptance_and_the_fresh_matching_sequence_stops_the_sends()
    {
        await using var rig = await LcdRig.OpenAsync(Live(Lcd(seq: 0, counter: 1000)), Live(Lcd(seq: 0, counter: 1000)), Live(Lcd(seq: 1, counter: 1000)), Live(Lcd(seq: 1, counter: 2600)));
        var r = await rig.ResetAsync();
        (r.Accepted, r.Attempts, r.Sequence, r.PreviousSequence, r.SlaveIndex).Should().Be((true, 3, (byte)1, (byte)0, (byte)2),
            "an unchanged counter is stale even when it already shows the sequence");
        r.PwmBefore.Should().Be(r.PwmAfter);
        var resets = rig.Rf22();
        resets.Should().HaveCount(3).And.OnlyContain(p => p.SequenceEqual(resets[0]), "identical sequence on every attempt");
        rig.Callbacks.Should().Be(1);
        rig.Gaps().Should().OnlyContain(g => g >= TimeSpan.FromMilliseconds(190));
    }

    [Fact]
    public async Task Acknowledged_on_the_first_fresh_report_sends_once()
    {
        await using var rig = await LcdRig.OpenAsync(Live(Lcd(seq: 254)), Live(Lcd(seq: 1, counter: 1400)));
        var r = await rig.ResetAsync();
        (r.Accepted, r.Attempts, r.Sequence).Should().Be((true, 1, (byte)1));
        rig.Rf22().Single()[17].Should().Be(1);
    }

    [Fact]
    public async Task An_older_counter_with_the_expected_sequence_is_not_a_fresh_acknowledgement()
    {
        await using var rig = await LcdRig.OpenAsync(Live(Lcd(seq: 0, counter: 1000)), Live(Lcd(seq: 1, counter: 999)), Live(Lcd(seq: 1, counter: 1400)));
        var r = await rig.ResetAsync();
        r.Accepted.Should().BeTrue(); r.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Never_acknowledged_is_bounded_to_three_identical_sends()
    {
        var replies = Enumerable.Range(0, 5).Select(i => Live(Lcd(seq: 9, counter: (uint)(1000 + (i * 400))))).ToArray();
        await using var rig = await LcdRig.OpenAsync(replies);
        var r = await rig.ResetAsync();
        (r.Accepted, r.Attempts).Should().Be((false, 3));
        r.Detail.Should().Contain("still reports command sequence 9");
        rig.Rf22().Should().HaveCount(3);
    }

    [Fact]
    public async Task A_foreign_sequence_or_changed_pwm_stops_without_further_sends()
    {
        await using (var rig = await LcdRig.OpenAsync(Live(Lcd(seq: 4)), Live(Lcd(seq: 7, counter: 1400))))
        {
            var r = await rig.ResetAsync();
            (r.Accepted, r.Attempts).Should().Be((false, 1));
            r.Detail.Should().Contain("another sender");
        }

        await using (var rig = await LcdRig.OpenAsync(Live(Lcd(seq: 4)), Live(Lcd(seq: 5, counter: 1400, pwm: 100))))
        {
            var r = await rig.ResetAsync();
            (r.Accepted, r.Attempts).Should().Be((false, 1), "a matching sequence with different PWM is not accepted");
            r.Detail.Should().Contain("PWM changed");
            rig.Rf22().Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Prechecks_fail_before_any_rf_send_or_callback()
    {
        await using (var rig = await LcdRig.OpenAsync(Live(Lcd() with { FanType = 28 })))
        {
            await FluentActions.Awaiting(rig.ResetAsync).Should().ThrowAsync<InvalidDataException>();
            (rig.Rf22().Count, rig.Callbacks).Should().Be((0, 0));
        }

        await using (var rig = await LcdRig.OpenAsync(Live(Lcd())))
        {
            FluentActions.Invoking(() => rig.Adapter.ValidateLcdGroupReset(rig.Id, 2)).Should().Throw<ArgumentOutOfRangeException>();
            rig.Options.WireFormatVerified = false;
            FluentActions.Invoking(() => rig.Adapter.ValidateLcdGroupReset(rig.Id, 3)).Should().Throw<InvalidOperationException>().WithMessage("*WireFormatVerified*");
            rig.Options.WireFormatVerified = true;
            await rig.Adapter.CloseAsync(rig.Id);
            FluentActions.Invoking(() => rig.Adapter.ValidateLcdGroupReset(rig.Id, 3)).Should().Throw<InvalidOperationException>().WithMessage("*not open*");
            (rig.Rf22().Count, rig.Callbacks, rig.ProbeCalls).Should().Be((0, 0, 0));
        }

        await using (var h2 = await LcdRig.OpenAsync(new LianLiDeviceConfig { Mac = Bottom.ToString(), Family = "H2", Leds = 24, Channel = 8, Rx = 5 }, Live(Lcd())))
        {
            FluentActions.Invoking(() => h2.Adapter.ValidateLcdGroupReset(h2.Id, 3)).Should().Throw<ArgumentException>().WithMessage("*not a 3-fan TL group*");
        }
    }

    [Fact]
    public async Task Cancellation_before_the_first_send_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        await using var rig = await LcdRig.OpenAsync(Live(Lcd()));
        rig.OnProbe = cts.Cancel;
        await FluentActions.Awaiting(() => rig.ResetAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        (rig.Rf22().Count, rig.Callbacks).Should().Be((0, 0));
    }

    [Fact]
    public async Task Upload_keep_alive_and_feedback_observation_never_interleave_with_the_reset()
    {
        await using var rig = await LcdRig.OpenAsync(Live(Lcd()), Live(Lcd(seq: 1, counter: 1400)));
        await rig.Adapter.UploadLoopAsync(rig.Id, LianLiAdapter.ChainZone, new LoopFrames(1, 78, 20, 100, new byte[234]), CancellationToken.None);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.OnProbe = () =>
        {
            if (rig.ProbeCalls == 1)
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var reset = rig.ResetAsync();
        inside.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var frames = rig.Wire.SentFrames.Count;
        var upload = rig.Adapter.UploadLoopAsync(rig.Id, LianLiAdapter.ChainZone, new LoopFrames(1, 78, 20, 200, new byte[234]), CancellationToken.None);
        rig.Now = Rf.Now.AddSeconds(5);
        await Task.Run(rig.Adapter.KeepAlive); // keep-alive (USB 17 + ClockSync) and the feedback observation both back off
        upload.IsCompleted.Should().BeFalse("the upload waits for the sender gate");
        rig.Wire.SentFrames.Should().HaveCount(frames);
        rig.ProbeCalls.Should().Be(1, "the feedback observation must not query (or consume) receiver replies");
        release.Set();
        (await reset).Accepted.Should().BeTrue();
        await upload;
        var groups = Rf.Group(rig.Wire.SentFrames.Skip(frames).ToArray()).Where(g => g.Packet is not null).Select(g => g.Packet![1]).ToArray();
        groups.First().Should().Be(RfLcdGroupReset.Opcode, "the reset's sends precede the queued upload");
        groups.SkipWhile(op => op == RfLcdGroupReset.Opcode).Should().Contain((byte)RfCmd.RgbSync).And.NotContain(RfLcdGroupReset.Opcode);
        rig.Rf22().Should().ContainSingle();
    }

    private sealed class LcdRig : IAsyncDisposable
    {
        private readonly Queue<byte[]> _replies;
        private int _callbacks;
        private int _probes;

        private LcdRig(LianLiOptions options, Queue<byte[]> replies)
        {
            Options = options;
            _replies = replies;
            Wire.EnqueueRead(Rf.MasterReply()); // open
            for (var i = 0; i < 4; i++)
            {
                Wire.EnqueueRead(Rf.MasterReply()); // the reset's fresh USB 17 (and later keep-alives)
            }

            Adapter = new LianLiAdapter(options, new FakeUsb(), _ => Wire, encoder: new IdentityEncoder(), sleep: _ => { }, now: () => Now,
                receiverProbe: Probe, startKeepAliveTimer: false, feedbackElapsed: () => Now - Rf.Now);
        }

        public LianLiOptions Options { get; }

        public RecordingTransport Wire { get; } = new();

        public LianLiAdapter Adapter { get; }

        public ControllerId Id { get; private set; }

        public DateTime Now { get; set; } = Rf.Now;

        public Action? OnProbe { get; set; }

        public int Callbacks => Volatile.Read(ref _callbacks);

        public int ProbeCalls => Volatile.Read(ref _probes);

        public static Task<LcdRig> OpenAsync(params byte[][] replies) =>
            OpenAsync(new LianLiDeviceConfig { Mac = Bottom.ToString(), RecType = 28, FanNum = 3, Leds = 78, Channel = 8, Rx = 5 }, replies);

        public static async Task<LcdRig> OpenAsync(LianLiDeviceConfig device, params byte[][] replies)
        {
            var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid, ReceiverFeedbackEnabled = true };
            options.Devices.Add(device);
            var rig = new LcdRig(options, new Queue<byte[]>(replies));
            rig.Id = (await rig.Adapter.DiscoverAsync(CancellationToken.None)).Single().Id;
            await rig.Adapter.OpenAsync(rig.Id, CancellationToken.None);
            return rig;
        }

        public Task<LcdGroupResetResult> ResetAsync() => ResetAsync(CancellationToken.None);

        public Task<LcdGroupResetResult> ResetAsync(CancellationToken ct) =>
            Adapter.ResetLcdGroupAsync(Id, 3, () =>
            {
                Rf22().Should().BeEmpty("the callback runs before the first RF 22 frame");
                Interlocked.Increment(ref _callbacks);
            }, ct);

        public List<byte[]> Rf22() =>
            [.. Rf.Group(Wire.SentFrames).Where(g => g.Packet is { } p && p[1] == RfLcdGroupReset.Opcode).Select(g => g.Packet!)];

        /// <summary>Time between the first chunks of consecutive RF 22 sends.</summary>
        public IEnumerable<TimeSpan> Gaps()
        {
            var starts = Wire.Frames.Where(f => f.Direction == RecordDirection.Out && f.Data[0] == 16 && f.Data[1] == 0 && f.Data[5] == RfLcdGroupReset.Opcode)
                .Select(f => f.TimestampTicks).ToArray();
            return starts.Zip(starts.Skip(1), (a, b) => TimeSpan.FromSeconds((b - a) / (double)Stopwatch.Frequency));
        }

        public ValueTask DisposeAsync() => Adapter.DisposeAsync();

        private byte[] Probe()
        {
            Interlocked.Increment(ref _probes);
            OnProbe?.Invoke();
            lock (_replies)
            {
                return _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            }
        }
    }
}
