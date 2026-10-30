using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Fake-only checks of the opt-in per-key pacing on the shared keyboard actor (responsive lease, bounded post-frame
/// observation, RF report delay). Fake I/O timing never shows a physical frame rate, optical output or a keepalive deadline:
/// these tests establish order, counts, joined workers and byte-exact reports only.
/// </summary>
public sealed class RogKeyboardSmoothnessTests
{
    private const string UsbHeader = "001200000024000100";
    private const string RfHeader = "0800070024000100";
    private static readonly RogPerKeyOptions Responsive = new() { MaximumFps = 30 };
    private static readonly byte[] Pixels = new byte[RogWiredOledProtocol.PixelBytes];

    // 85 independent LEDs (distinct ids and colours); green/blue encode the frame number, so frames are unique below 65536.
    private static RogKeyColor[] Frame(int n) => Enumerable.Range(0, 85)
        .Select(i => new RogKeyColor((byte)i, new((byte)i, (byte)((n >> 8) * 5 + 255 - i), (byte)(n * 7 + i * 3)))).ToArray();

    private static (byte, byte) Cmd(int command, int key) => ((byte)command, (byte)key);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_and_aggregate_outputs_keep_the_legacy_observation_after_every_frame(bool aggregateWithOption)
    {
        using var rig = new Rig(); using var stop = new CancellationTokenSource();
        var run = await rig.StartAsync(stop.Token, pacing: aggregateWithOption ? Responsive : null);
        for (var n = 0; n < 4; n++)
        {
            if (aggregateWithOption) await rig.Bridge.WriteAsync(new Rgb((byte)n, 2, 3), default);
            else await rig.Bridge.WriteKeysAsync(Frame(n), rig.Transport, default);
        }
        bool End(Fake.Op op) => aggregateWithOption ? op.Command == 0x51 && op.Key == 0x2C : op.Colour > 0 && op.Colour % 7 == 0;
        await WaitForAsync(() => rig.Ops is var ops && ops.Count(End) == 4 && ops.Length >= Array.FindLastIndex(ops, End) + 3);
        await StopAsync(stop, run);

        var all = rig.Ops;
        var ends = Enumerable.Range(0, all.Length).Where(i => End(all[i])).ToArray();
        ends.Should().HaveCount(4);
        foreach (var i in ends)
            all.Skip(i + 1).Take(2).Select(o => (o.Command, o.Key)).Should().Equal(new[] { Cmd(0x12, 0), Cmd(0x23, 3) },
                "without opt-in per-key output every frame is followed by the unchanged Basic + brightness observation");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_option_wakes_display_mode_frames_promptly_where_the_default_waits_out_its_lease(bool responsive)
    {
        using var rig = new Rig(); using var stop = new CancellationTokenSource();
        var run = await rig.StartAsync(stop.Token, display: true, pacing: responsive ? Responsive : null);
        await rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);
        // Both modes renew after the first frame (the first per-key renewal is due); then the actor waits between frames.
        await WaitForAsync(() => rig.Ops is var ops && Array.FindLastIndex(ops, o => o.Command == 0x67 && o.Key == 2) > Array.FindIndex(ops, o => o.Colour == 7));
        var queued = Stopwatch.GetTimestamp();
        await rig.Bridge.WriteKeysAsync(Frame(1), rig.Transport, default);
        var waited = Stopwatch.GetElapsedTime(queued, rig.Ops.First(o => o.Colour == 8).At);
        await StopAsync(stop, run);

        if (responsive) waited.Should().BeLessThan(TimeSpan.FromMilliseconds(40), "lighting work wakes the renewed lease at once");
        else waited.Should().BeGreaterThan(TimeSpan.FromMilliseconds(40), "the legacy lease re-checks for work only after its 80 ms delay");
        Decode(rig.Ops).Should().Equal(new[] { Frame(0), Frame(1) }, (a, b) => a.SequenceEqual(b));
    }

    [Fact]
    public async Task Responsive_display_mode_completes_many_byte_exact_unique_frames_while_due_renewals_continue()
    {
        using var rig = new Rig(); using var stop = new CancellationTokenSource();
        var run = await rig.StartAsync(stop.Token, display: true, pacing: Responsive);
        var sent = await ProduceAsync(rig, TimeSpan.FromMilliseconds(800));
        await StopAsync(stop, run);

        var ops = rig.Ops;
        ops.Where(o => o.Colour > 0).Select(o => Convert.ToHexString(o.Report))
            .Should().Equal(sent.SelectMany(f => RogPerKeyLighting.BoundaryFrame(f)).Select(Convert.ToHexString), "every report is byte-exact and in order");
        sent.Select(f => Convert.ToHexString(f.SelectMany(k => new[] { k.LedId, k.Color.R, k.Color.G, k.Color.B }).ToArray()))
            .Should().OnlyHaveUniqueItems();
        sent.Count.Should().BeGreaterThan(30, "a lease-gated actor completes at most about ten frames in 800 ms; fake I/O is not physical FPS");

        var first = ops.First(o => o.Colour == 1).At; var last = ops.Last(o => o.Colour > 0).At;
        var renewals = ops.Where(o => o.Command == 0x67 && o.Key == 2 && o.At >= first && o.At <= last).Select(o => o.At).ToArray();
        renewals.Length.Should().BeInRange(5, 14, "due renewals continue under continuous work, but not one per frame");
        renewals.Prepend(first).Append(last).Zip(renewals.Append(last))
            .Max(p => Stopwatch.GetElapsedTime(p.First, p.Second)).Should().BeLessThan(TimeSpan.FromMilliseconds(300));
        rig.Io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task A_due_renewal_precedes_already_queued_work_and_none_is_added_between_frames_when_not_due()
    {
        using var rig = new Rig(fakeClock: true); using var stop = new CancellationTokenSource();
        var holdA = new Hold(); var holdB = new Hold();
        rig.Io.BeforeWrite = (op, ct) => op.Colour == 7 ? holdA.PassAsync(ct) : op.Colour == 14 ? holdB.PassAsync(ct) : Task.CompletedTask;
        var run = await rig.StartAsync(stop.Token, nativeArtwork: true, pacing: Responsive);

        var a = rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);
        await holdA.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var b = rig.Bridge.WriteKeysAsync(Frame(1), rig.Transport, default); // queued before the actor reaches its lease
        rig.Advance(TimeSpan.FromSeconds(1));                                // a renewal is due
        holdA.Release(); await a;
        await holdB.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var c = rig.Bridge.WriteKeysAsync(Frame(2), rig.Transport, default); // the fake clock stays: no renewal is due
        holdB.Release(); await b; await c;
        await StopAsync(stop, run);

        var ops = rig.Ops;
        var endA = Array.FindIndex(ops, o => o.Colour == 7); var endB = Array.FindIndex(ops, o => o.Colour == 14);
        ops.Skip(endA + 1).Take(3).Select(o => (o.Command, o.Key)).Should().Equal(new[] { Cmd(0x67, 2), Cmd(0x12, 0), Cmd(0xC0, 0x81) },
            "exactly one due renewal is written although the next frame was already queued");
        ops.Skip(endB + 1).Take(2).Select(o => (o.Command, o.Key)).Should().Equal(new[] { Cmd(0x12, 0), Cmd(0xC0, 0x81) },
            "a renewal that is not due is never added between frames");
        Decode(ops).Should().Equal(new[] { Frame(0), Frame(1), Frame(2) }, (x, y) => x.SequenceEqual(y));
        rig.Io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task Work_arriving_during_an_admitted_renewal_waits_for_it_and_then_wakes_promptly()
    {
        using var rig = new Rig(fakeClock: true); using var stop = new CancellationTokenSource();
        var hold = new Hold(armed: false);
        rig.Io.BeforeWrite = (op, ct) => op.Command == 0x67 && op.Key == 2 ? hold.PassAsync(ct) : Task.CompletedTask;
        var run = await rig.StartAsync(stop.Token, nativeArtwork: true, pacing: Responsive);
        await rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);

        hold.Arm(); rig.Advance(TimeSpan.FromSeconds(1));
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var b = rig.Bridge.WriteKeysAsync(Frame(1), rig.Transport, default);
        await Task.Delay(100);
        b.IsCompleted.Should().BeFalse("queued work cannot pass an admitted renewal that has not completed");
        rig.Ops.Count(o => o.Colour > 0).Should().Be(7);
        var released = Stopwatch.GetTimestamp();
        hold.Release(); await b;
        var waited = Stopwatch.GetElapsedTime(released, rig.Ops.First(o => o.Colour == 8).At);
        await StopAsync(stop, run);

        waited.Should().BeLessThan(TimeSpan.FromMilliseconds(40), "the wake is armed by the completed renewal, not by its 80 ms delay");
        var ops = rig.Ops; var endA = Array.FindIndex(ops, o => o.Colour == 7);
        ops.Skip(endA + 1).TakeWhile(o => o.Colour == 0).Count(o => o.Command == 0x67).Should().Be(1);
        rig.Io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_during_an_admitted_renewal_retires_it_and_joins_the_reader()
    {
        using var rig = new Rig(fakeClock: true); using var stop = new CancellationTokenSource();
        var hold = new Hold(armed: false);
        rig.Io.BeforeWrite = (op, ct) => op.Command == 0x67 && op.Key == 2 ? hold.PassAsync(ct) : Task.CompletedTask;
        var run = await rig.StartAsync(stop.Token, nativeArtwork: true, pacing: Responsive);
        await rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);
        hold.Arm(); rig.Advance(TimeSpan.FromSeconds(1));
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = rig.Bridge.WriteKeysAsync(Frame(1), rig.Transport, default);

        stop.Cancel();
        await Task.Delay(30);
        run.IsCompleted.Should().BeFalse("an admitted renewal retires on its own bounded deadline");
        hold.Release();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();

        rig.Ops[^1].Should().Match<Fake.Op>(o => o.Command == 0x67 && o.Key == 2, "the admitted renewal completed and nothing followed it");
        rig.Ops.Count(o => o.Colour > 0).Should().Be(7);
        rig.Io.PendingReads.Should().Be(0);
        rig.Bridge.Detach(rig.Owner);
        await ((Func<Task>)(() => pending)).Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Cancelling_before_a_renewal_is_due_writes_nothing_more_and_joins()
    {
        using var rig = new Rig(fakeClock: true); using var stop = new CancellationTokenSource();
        var run = await rig.StartAsync(stop.Token, nativeArtwork: true, pacing: Responsive);
        await rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);
        await Task.Delay(150); // not due on the fake clock: the actor waits without wire I/O
        var written = rig.Ops.Length;

        stop.Cancel();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
        rig.Ops.Should().HaveCount(written);
        rig.Io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_due_renewal_wins_over_the_queued_work_wake()
    {
        using var rig = new Rig(fakeClock: true); using var stop = new CancellationTokenSource();
        var holdA = new Hold(); var fail = false;
        rig.Io.BeforeWrite = (op, ct) => op.Colour == 7 ? holdA.PassAsync(ct)
            : op.Command == 0x67 && op.Key == 2 && Volatile.Read(ref fail) ? Task.FromException(new IOException("fake renewal write failed")) : Task.CompletedTask;
        var run = await rig.StartAsync(stop.Token, nativeArtwork: true, pacing: Responsive);
        var a = rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);
        await holdA.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var b = rig.Bridge.WriteKeysAsync(Frame(1), rig.Transport, default);
        Volatile.Write(ref fail, true); rig.Advance(TimeSpan.FromSeconds(1));
        holdA.Release(); await a;

        var failure = await ((Func<Task>)(() => run)).Should().ThrowAsync<IOException>();
        failure.Which.Should().NotBeAssignableTo<OperationCanceledException>();
        rig.Ops.Count(o => o.Colour > 0).Should().Be(7, "the queued frame is not served after an unknown renewal outcome");
        rig.Io.PendingReads.Should().Be(0);
        rig.Bridge.Detach(rig.Owner);
        await ((Func<Task>)(() => b)).Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Every_frame_keeps_fresh_scope_and_settings_checks_while_the_post_frame_observation_is_bounded()
    {
        using var rig = new Rig(); using var stop = new CancellationTokenSource();
        var run = await rig.StartAsync(stop.Token, pacing: Responsive);
        var clock = Stopwatch.StartNew();
        var sent = await ProduceAsync(rig, TimeSpan.FromMilliseconds(600));
        var elapsed = clock.Elapsed;
        await StopAsync(stop, run);

        var ops = rig.Ops;
        var first = Array.FindIndex(ops, o => o.Colour == 1);
        ops.Skip(first - 4).Take(4).Select(o => (o.Command, o.Key)).Should().Equal(new[] { Cmd(0x12, 0), Cmd(0x12, 0), Cmd(0x74, 0), Cmd(0x27, 0) },
            "fresh pre-frame scope, then the unchanged SET74 scope, echo and readback");
        Enumerable.Range(0, ops.Length).Where(i => ops[i].Colour > 1 && ops[i].Colour % 7 == 1)
            .Should().OnlyContain(i => ops[i - 1].Command == 0x12 && ops[i - 1].Key == 0, "every later frame keeps its own fresh Basic scope query");
        var observed = Enumerable.Range(first, ops.Length - first).Where(i => ops[i].Command == 0x23 && ops[i].Key == 3).ToArray();
        observed.Should().OnlyContain(i => ops[i - 1].Command == 0x12 && ops[i - 1].Key == 0);
        observed.Length.Should().BeLessThanOrEqualTo((int)(elapsed.TotalMilliseconds / 100) + 2, "the observation is bounded to 100 ms");
        sent.Count.Should().BeGreaterThan(observed.Length * 3);
        Decode(ops).Should().HaveCount(sent.Count);
    }

    [Theory]
    [InlineData(null, 8)]
    [InlineData(8, 8)]
    [InlineData(4, 4)]
    [InlineData(0, 0)]
    public async Task Rf_report_delay_tunes_only_the_pause_of_the_real_rf_owner_with_identical_bytes(int? configured, int expected)
    {
        using var rig = new Rig(rf: true); using var stop = new CancellationTokenSource();
        var run = await rig.StartAsync(stop.Token, pacing: configured is null ? null : new RogPerKeyOptions { RfReportDelayMs = configured });
        var keys = Frame(3);
        await rig.Bridge.WriteKeysAsync(keys, rig.Transport, default);
        // The following Basic + brightness exchange drains the seven buffered C0/82 replies (no mandatory RGB ACK).
        await WaitForAsync(() => rig.Ops.Skip(Array.FindIndex(rig.Ops, o => o.Colour == 7) + 1).Any(o => o.Command == 0x23 && o.Key == 3));
        await StopAsync(stop, run);

        ((RogWirelessOledConnection)rig.Connection).KeyReportDelayMs.Should().Be(expected);
        rig.Pauses.Should().Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(expected), expected == 0 ? 0 : 7));
        rig.Ops.Where(o => o.Colour > 0).Select(o => Convert.ToHexString(o.Report)).Should().Equal(RogProtocol.KeyboardFrame(2, keys).Select(Convert.ToHexString));
        Decode(rig.Ops).Should().ContainSingle().Which.Should().Equal(keys);
        keys.Select(k => k.Color).Distinct().Should().HaveCount(85);
        rig.Io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task Out_of_range_pacing_is_refused_before_any_actor_io()
    {
        using var rig = new Rig(rf: true);
        await rig.Session.SnapshotAsync(RfHeader, default);
        await rig.Session.SnapshotLightingAsync(default);
        var written = rig.Ops.Length;
        foreach (var bad in new RogPerKeyOptions[] { new() { RfReportDelayMs = 9 }, new() { RfReportDelayMs = -1 }, new() { MaximumFps = 0 }, new() { MaximumFps = 31 } })
            await ((Func<Task>)(() => rig.Session.RunAsync(() => Pixels, () => { }, default, rig.Owner, false, null, 100, null, null, null, null, null, null, bad)))
                .Should().ThrowAsync<InvalidOperationException>();
        rig.Ops.Should().HaveCount(written);
        var rf = (RogWirelessOledConnection)rig.Connection;
        ((Action)(() => rf.KeyReportDelayMs = 9)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => rf.KeyReportDelayMs = -1)).Should().Throw<ArgumentOutOfRangeException>();
        rf.KeyReportDelayMs.Should().Be(RogPerKeyOptions.LegacyRfReportDelayMs);
    }

    [Fact]
    public void RunAsync_abi_keeps_the_exact_original_13_parameter_entry_point_and_a_required_only_paced_overload()
    {
        var original = new[] { typeof(Func<byte[]>), typeof(Action), typeof(CancellationToken), typeof(RogWiredLightingBridge.Owner), typeof(bool),
            typeof(Func<bool>), typeof(byte), typeof(Action<bool>), typeof(Func<CancellationToken, Task<byte>>),
            typeof(Func<byte, CancellationToken, Task>), typeof(Func<CancellationToken, Task<bool>>), typeof(Func<bool>), typeof(Func<CancellationToken, Task>) };
        typeof(RogWiredOledSession).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(RogWiredOledSession.RunAsync)).Select(m => m.GetParameters().Length).Should().BeEquivalentTo(new[] { 13, 14 });

        var legacy = typeof(RogWiredOledSession).GetMethod(nameof(RogWiredOledSession.RunAsync), original);
        legacy.Should().NotBeNull("compiled Host callers bind this exact public 13-parameter signature");
        legacy!.ReturnType.Should().Be<Task>();
        var parameters = legacy.GetParameters();
        parameters.Select(p => p.Name).Should().Equal("latest", "acknowledged", "ct", "lighting", "display", "displayEnabled", "brightness",
            "displayChanged", "restoreNativeGoal", "nativeBrightnessChanged", "nativeTick", "nativeWake", "nativeStopped");
        parameters.Take(3).Should().OnlyContain(p => !p.IsOptional);
        parameters.Skip(3).Should().OnlyContain(p => p.IsOptional);
        parameters.Skip(3).Select(p => p.DefaultValue).Should().Equal(null, true, null, (byte)100, null, null, null, null, null, null);

        var paced = typeof(RogWiredOledSession).GetMethod(nameof(RogWiredOledSession.RunAsync), original.Append(typeof(RogPerKeyOptions)).ToArray());
        paced.Should().NotBeNull();
        paced!.ReturnType.Should().Be<Task>();
        paced.GetParameters().Should().OnlyContain(p => !p.IsOptional, "no optional-overload ambiguity with the original entry point");
        paced.GetParameters()[^1].Name.Should().Be("perKeyPacing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_preserves_legacy_pacing_through_the_original_entry_point_for_a_compiled_caller(bool rf)
    {
        using var rig = new Rig(rf); using var stop = new CancellationTokenSource();
        await rig.Session.SnapshotAsync(rf ? RfHeader : UsbHeader, default);
        await rig.Session.SnapshotLightingAsync(default);
        var entry = Original(rig.Session);
        entry.Method.GetParameters().Should().HaveCount(13);
        var start = rig.Io.Ops.Length; var stopped = 0;
        var run = entry(() => Pixels, () => { }, stop.Token, rig.Owner, false, null, 100, null, null, null, null, null,
            _ => { Interlocked.Increment(ref stopped); return Task.CompletedTask; });
        for (var n = 0; n < 3; n++) await rig.Bridge.WriteKeysAsync(Frame(n), rig.Transport, default);
        await WaitForAsync(() => rig.Io.Ops[start..] is var ops && Array.FindIndex(ops, o => o.Colour == 21) is var last && last >= 0 && ops.Length >= last + 3);
        await StopAsync(stop, run);

        var all = rig.Io.Ops[start..];
        foreach (var end in new[] { 7, 14, 21 })
            all.SkipWhile(o => o.Colour != end).Skip(1).Take(2).Select(o => (o.Command, o.Key)).Should().Equal(new[] { Cmd(0x12, 0), Cmd(0x23, 3) },
                "the original entry point keeps the observation after every frame");
        rig.Pauses.Should().Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(8), rf ? 21 : 0), "RF keeps its 8 ms default; USB stays unpaced");
        if (rf) ((RogWirelessOledConnection)rig.Connection).KeyReportDelayMs.Should().Be(RogPerKeyOptions.LegacyRfReportDelayMs);
        Decode(all).Should().Equal(new[] { Frame(0), Frame(1), Frame(2) }, (x, y) => x.SequenceEqual(y));
        stopped.Should().Be(1, "nativeStopped is forwarded");
        rig.Io.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_preserves_display_callbacks_and_the_legacy_lease_through_the_original_entry_point()
    {
        using var rig = new Rig(); using var stop = new CancellationTokenSource();
        await rig.Session.SnapshotAsync(UsbHeader, default);
        await rig.Session.SnapshotLightingAsync(default);
        var start = rig.Io.Ops.Length; var acknowledged = 0; var changed = 0; var stopped = 0;
        var run = Original(rig.Session)(() => Pixels, () => Interlocked.Increment(ref acknowledged), stop.Token, rig.Owner, true, () => true, 100,
            _ => Interlocked.Increment(ref changed), null, null, null, () => false, _ => { Interlocked.Increment(ref stopped); return Task.CompletedTask; });
        await WaitForAsync(() => Volatile.Read(ref acknowledged) == 1);
        await rig.Bridge.WriteKeysAsync(Frame(0), rig.Transport, default);
        await WaitForAsync(() => rig.Io.Ops[start..] is var ops && Array.FindIndex(ops, o => o.Colour == 7) is var end && end >= 0 && ops.Length > end + 1);
        await StopAsync(stop, run);

        var all = rig.Io.Ops[start..];
        all.SkipWhile(o => o.Colour != 7).Skip(1).First().Should().Match<Fake.Op>(o => o.Command == 0x67 && o.Key == 2,
            "the legacy lease renews at entry after a frame");
        all.Count(o => o.Command == 0x67 && o.Key == 1).Should().Be(111, "one OLED upload with the forwarded display flag");
        acknowledged.Should().Be(1);
        changed.Should().Be(0, "displayEnabled and nativeWake are forwarded unswapped");
        stopped.Should().Be(1);
        rig.Io.PendingReads.Should().Be(0);
    }

    // Binds exactly as a caller compiled against the original 13-parameter signature does.
    private static Func<Func<byte[]>, Action, CancellationToken, RogWiredLightingBridge.Owner?, bool, Func<bool>?, byte, Action<bool>?,
        Func<CancellationToken, Task<byte>>?, Func<byte, CancellationToken, Task>?, Func<CancellationToken, Task<bool>>?, Func<bool>?,
        Func<CancellationToken, Task>?, Task> Original(RogWiredOledSession session) => session.RunAsync;

    private static List<RogKeyColor[]> Decode(IEnumerable<Fake.Op> ops)
    {
        var reports = ops.Where(o => o.Colour > 0).Select(o => o.Report).ToArray();
        var frames = new List<RogKeyColor[]>();
        for (var i = 0; i + 7 <= reports.Length; i += 7)
            frames.Add(reports.Skip(i).Take(7).SelectMany(r => Enumerable.Range(0, r[3])
                .Select(k => new RogKeyColor(r[5 + k * 4], new(r[6 + k * 4], r[7 + k * 4], r[8 + k * 4])))).ToArray());
        return frames;
    }

    // Behaves like a DeviceWriter that is always behind: the next unique frame is offered as soon as the previous completed.
    private static async Task<List<RogKeyColor[]>> ProduceAsync(Rig rig, TimeSpan window)
    {
        var sent = new List<RogKeyColor[]>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < window)
        {
            var frame = Frame(sent.Count);
            await rig.Bridge.WriteKeysAsync(frame, rig.Transport, default);
            sent.Add(frame);
        }
        return sent;
    }

    private static async Task StopAsync(CancellationTokenSource stop, Task run)
    {
        stop.Cancel();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Fake condition was not reached.");
            await Task.Delay(2);
        }
    }

    /// <summary>Holds the first matching write after arming until released; it then completes the write normally.</summary>
    private sealed class Hold(bool armed = true)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed = armed;
        public Task Entered => _entered.Task;
        public void Arm() => _armed = true;
        public void Release() => _released.TrySetResult();
        public Task PassAsync(CancellationToken ct)
        {
            if (!_armed) return Task.CompletedTask;
            _armed = false;
            _entered.TrySetResult();
            return _released.Task.WaitAsync(ct);
        }
    }

    private sealed class Rig : IDisposable
    {
        private long _now = 1_000_000_000;
        private int _start;
        public Rig(bool rf = false, bool fakeClock = false)
        {
            Io = new Fake(rf);
            Transport = rf ? RogPerKeyLighting.WirelessTransport : RogPerKeyLighting.WiredTransport;
            Connection = rf
                ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), Io, RfHeader, () => true)
                : new RogWiredOledConnection(Io, () => true);
            Session = new RogWiredOledSession(Connection)
            {
                KeyReportDelay = (delay, ct) => { lock (Pauses) Pauses.Add(delay); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                Timestamp = fakeClock ? () => Interlocked.Read(ref _now) : () => Stopwatch.GetTimestamp(),
            };
            Owner = Bridge.Attach();
        }

        public Fake Io { get; }
        public IRogOledConnection Connection { get; }
        public RogWiredOledSession Session { get; }
        public RogWiredLightingBridge Bridge { get; } = new();
        public RogWiredLightingBridge.Owner Owner { get; }
        public string Transport { get; }
        public List<TimeSpan> Pauses { get; } = [];
        public Fake.Op[] Ops => Io.Ops[_start..];

        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, (long)(by.TotalSeconds * Stopwatch.Frequency));

        public async Task<Task> StartAsync(CancellationToken ct, bool display = false, bool nativeArtwork = false, RogPerKeyOptions? pacing = null)
        {
            await Session.SnapshotAsync(Transport == RogPerKeyLighting.WirelessTransport ? RfHeader : UsbHeader, default);
            await Session.SnapshotLightingAsync(default);
            _start = Io.Ops.Length;
            // nativeArtwork: a fake native tick that declares native artwork active (lease mode) without native I/O.
            Func<CancellationToken, Task<bool>>? tick = nativeArtwork ? _ => Task.FromResult(true) : null;
            // Without pacing the original 13-parameter entry point is used; intentional pacing supplies all 14 values.
            return pacing is null
                ? Session.RunAsync(() => Pixels, () => { }, ct, Owner, display, nativeTick: tick)
                : Session.RunAsync(() => Pixels, () => { }, ct, Owner, display, null, 100, null, null, null, tick, null, null, pacing);
        }

        public void Dispose() { Bridge.Detach(Owner); Connection.Dispose(); Io.Dispose(); }
    }

    /// <summary>
    /// Records every report with a completion timestamp. C0 81 gets the stock C0/82 shape (read strictly by USB, drained later
    /// by RF), 67/02 has no mandatory reply, everything else is answered by the existing OLED fake; RF uses RID2/64.
    /// </summary>
    private sealed class Fake(bool rf) : IHidReportChannel
    {
        private readonly RogWiredOledTests.OledFake _inner = new();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly List<Op> _ops = [];
        private int _pendingReads, _colourWrites;

        internal sealed record Op(byte Command, byte Key, byte[] Report, int Colour)
        {
            public long At { get; init; }
        }

        public Func<Op, CancellationToken, Task>? BeforeWrite { get; set; }
        public int PendingReads => Volatile.Read(ref _pendingReads);
        public Op[] Ops { get { lock (_ops) return [.. _ops]; } }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var p = report.ToArray();
            if (p.Length != (rf ? 64 : 65) || p[0] != (rf ? 2 : 0)) throw new InvalidDataException("Wrong request envelope.");
            var wired = new byte[65]; p.AsSpan(1).CopyTo(wired.AsSpan(1));
            var colour = wired[1] == 0xC0 && wired[2] == 0x81;
            var op = new Op(wired[1], wired[2], p, colour ? Interlocked.Increment(ref _colourWrites) : 0);
            if (BeforeWrite is { } hook) await hook(op, ct);
            lock (_ops) _ops.Add(op with { At = Stopwatch.GetTimestamp() });
            if (op.Command == 0x67 && op.Key == 2) return;
            if (colour)
            {
                var reply = new byte[65]; wired.AsSpan(0, 5).CopyTo(reply);
                reply[2] = 0x82; reply.AsSpan(5, 23).Fill(0x5A);
                _replies.Writer.TryWrite(reply).Should().BeTrue();
                return;
            }
            await _inner.WriteAsync(wired, ct);
            var buffer = new byte[65];
            var length = await _inner.ReadAsync(buffer, ct);
            _replies.Writer.TryWrite(buffer[..length]).Should().BeTrue();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Interlocked.Increment(ref _pendingReads);
            try
            {
                var wired = await _replies.Reader.ReadAsync(ct);
                if (!rf) { wired.CopyTo(buffer); return wired.Length; }
                if (wired[1] == 0x12 && wired[2] == 0)
                {
                    wired[5] = 8; wired[6] = 0; wired[7] = 7; wired[8] = 0;
                    wired[12] = 0x24; wired[13] = 0; wired[14] = 1; wired[15] = 0;
                }
                var wireless = new byte[64]; wired.AsSpan(1, 63).CopyTo(wireless.AsSpan(1)); wireless[0] = 2;
                wireless.CopyTo(buffer);
                return wireless.Length;
            }
            finally { Interlocked.Decrement(ref _pendingReads); }
        }

        public void Dispose() { _replies.Writer.TryComplete(); _inner.Dispose(); }
    }
}
