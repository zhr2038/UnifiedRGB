using System.Diagnostics;
using System.Globalization;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.Safety;
using Xunit.Abstractions;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// NATIVE-DYNAMIC-SYNC-FIX §B: a native Streamed writer paced by its adapter's FrameBudget alone. A virtual microsecond clock
/// drives the real <see cref="FrameBudget"/>, a 60 fps "engine" publishing numbered frames into the real mailbox (while the
/// writer sleeps or sends, as the master clock does), a sleeper that wakes early or late by a scripted jitter, and a send cost
/// that advances time. <see cref="DeviceWriter.PacedDrainOnce"/> is the step the writer thread runs after each publish; the
/// threaded tests run the writer thread itself.
/// </summary>
public sealed class NativePacingTests(ITestOutputHelper output)
{
    private const long Us = 1_000_000;
    private const long EngineUs = 16_667;

    [Fact]
    public void The_budget_alone_paces_jittery_wakes_and_costly_sends_without_a_skipped_slot()
    {
        using var sim = new Sim();
        var c = Native("native:gpu");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(20, () => sim.Now, Us), () => sim.Now, sim.Spend) { CostUs = (_, _) => 8_000 };
        long[] jitter = [0, -300, 1_200, -1, 450, 2_000, -40, 0, 900, -250]; // µs; negative = early wake
        var sleeper = new JitterSleeper(sim, n => jitter[n % jitter.Length]);
        using var writer = new DeviceWriter(box, adapter, sleeper);
        writer.PacedByAdapter.Should().BeTrue();

        Run(sim, [writer], 10 * Us);

        var starts = adapter.StartsOf(c.Id);
        var gaps = Gaps(starts);
        adapter.Budget.Stats().Skips.Should().Be(0, "the writer never offers a frame before the deadline the budget enforces");
        adapter.Refused.Should().Be(0);
        gaps.Should().OnlyContain(g => g >= 50_000, "never above the strict 20 fps ceiling");
        gaps.Should().OnlyContain(g => g <= 50_000 + 2_000 + 2, "at most the latest wake late; a lost slot would be a 100 ms gap");
        var fps = Fps(starts);
        fps.Should().BeGreaterThan(19.0, "the ceiling is reached (the old double grid achieved 12 of 20)");
        AssertLatestWins(sim, adapter, c.Id);
        writer.GetStats().Errors.Should().Be(0);
        box.Superseded.Should().BeGreaterThan(0, "60 fps engine frames at a 20 fps device: the rest is superseded, never queued");
        Report("mixed jitter, 8 ms send, 20 fps ceiling", starts, adapter.Budget.Stats(), sleeper.Calls);
    }

    [Fact]
    public void Wakes_just_before_the_deadline_wait_again_instead_of_losing_the_slot()
    {
        using var sim = new Sim();
        var c = Native("native:aura");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(30, () => sim.Now, Us), () => sim.Now, sim.Spend) { CostUs = (_, _) => 3_000 };
        var sleeper = new JitterSleeper(sim, n => -1 - (n % 50)); // every wake 1..50 µs early
        using var writer = new DeviceWriter(box, adapter, sleeper);

        Run(sim, [writer], 5 * Us);

        var starts = adapter.StartsOf(c.Id);
        var gaps = Gaps(starts);
        adapter.Budget.Stats().Skips.Should().Be(0);
        gaps.Should().OnlyContain(g => g >= 33_334 && g <= 33_334 + 2, "33.33… ms, admitted at the first whole microsecond after it");
        Fps(starts).Should().BeGreaterThan(29.9);
        sleeper.Calls.Should().BeGreaterThan(starts.Length, "an early wake re-reads the delay and sleeps the remainder, never spins");
        AssertLatestWins(sim, adapter, c.Id);
        Report("systematically early wakes, 3 ms send, 30 fps ceiling", starts, adapter.Budget.Stats(), sleeper.Calls);
    }

    [Fact]
    public void Measured_expensive_frames_slow_the_writer_to_the_effective_interval_without_skips()
    {
        using var sim = new Sim();
        var c = Native("native:gpu");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(20, () => sim.Now, Us), () => sim.Now, sim.Spend) { CostUs = (_, _) => 80_000 };
        long[] jitter = [0, 700, -100, 1_500];
        var sleeper = new JitterSleeper(sim, n => jitter[n % jitter.Length]);
        using var writer = new DeviceWriter(box, adapter, sleeper);

        Run(sim, [writer], 30 * Us);

        var starts = adapter.StartsOf(c.Id);
        var gaps = Gaps(starts);
        var stats = adapter.Budget.Stats();
        (stats.FpsSource, stats.EffectiveFps, stats.Skips).Should().Be((FrameBudget.Measured, 8, 0L), "⌊1000 / (80 × 1.5)⌋");
        gaps.Take(FrameBudget.Window - 1).Should().OnlyContain(g => g == 80_000, "before the window fills: back to back, each send longer than 50 ms");
        gaps.Skip(FrameBudget.Window - 1).Should().OnlyContain(g => g >= 125_000 && g <= 125_000 + 1_500 + 2, "then the measured 125 ms interval");
        Fps(starts[FrameBudget.Window..]).Should().BeInRange(7.9, 8.0);
        AssertLatestWins(sim, adapter, c.Id);
        Report("80 ms sends, measured slowdown", starts[FrameBudget.Window..], stats, sleeper.Calls);
    }

    [Fact]
    public void Frames_published_during_the_wait_supersede_the_older_one_which_is_never_sent()
    {
        using var sim = new Sim();
        var c = Native("native:gpu");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(20, () => sim.Now, Us), () => sim.Now, sim.Spend);
        var sleeper = new JitterSleeper(sim, _ => 0);
        using var writer = new DeviceWriter(box, adapter, sleeper);
        sim.AdvanceTo(0); // frame 1
        writer.PacedDrainOnce().Should().BeTrue();
        sleeper.Calls.Should().Be(0, "the first frame is due at once");

        writer.PacedDrainOnce().Should().BeTrue("frames 2 and 3 were published at 16.7 and 33.3 ms during the 50 ms wait");
        adapter.Starts.Select(s => (s.Seq, s.Start)).Should().Equal((1L, 0L), (3L, 50_000L));
        box.Superseded.Should().Be(1, "frame 2 was replaced in the mailbox, not queued or replayed");
        writer.PacedDrainOnce().Should().BeTrue();
        adapter.Starts[^1].Seq.Should().Be(6, "the newest at 100 ms");
        adapter.Starts.Select(s => s.Seq).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Two_ene_sticks_pace_by_their_own_members_of_one_shared_measured_budget()
    {
        using var sim = new Sim();
        var s70 = Native("enedram:p0:0x70");
        var s71 = Native("enedram:p0:0x71");
        var b70 = sim.Add(s70);
        var b71 = sim.Add(s71);
        var adapter = new PacedAdapter(new FrameBudget(10, () => sim.Now, Us), () => sim.Now, sim.Spend) { CostUs = (_, _) => 50_000 };
        adapter.Members[s70.Id] = "0x70";
        adapter.Members[s71.Id] = "0x71";
        long[] jitter = [0, 400, -30, 1_000, 0, -1];
        var sleeper = new JitterSleeper(sim, n => jitter[n % jitter.Length]);
        using var w70 = new DeviceWriter(b70, adapter, sleeper);
        using var w71 = new DeviceWriter(b71, adapter, sleeper);

        Run(sim, [w70, w71], 60 * Us);

        var stats = adapter.Budget.Stats();
        (stats.FpsSource, stats.EffectiveFps, stats.Skips).Should().Be((FrameBudget.Measured, 6, 0L),
            "a sample is the sum of both sticks' latest frames (100 ms): ⌊1000 / 150⌋");
        foreach (var id in new[] { s70.Id, s71.Id })
        {
            var starts = adapter.StartsOf(id);
            var gaps = Gaps(starts);
            gaps.Should().OnlyContain(g => g >= 100_000, $"{id}: never above the 10 fps ceiling");
            gaps.Skip(FrameBudget.Window).Should().OnlyContain(g => g >= 166_667 && g <= 166_667 + 1_000 + 2, $"{id}: the shared measured interval");
            AssertLatestWins(sim, adapter, id);
            Report($"{id} of a shared port, 50 ms sends", starts[(FrameBudget.Window / 2)..], stats, sleeper.Calls);
        }
    }

    [Fact]
    public void A_paused_writer_awaits_nothing_and_sends_nothing()
    {
        using var sim = new Sim();
        var c = Native("native:gpu");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(20, () => sim.Now, Us), () => sim.Now, sim.Spend);
        var sleeper = new JitterSleeper(sim, _ => 0);
        using var writer = new DeviceWriter(box, adapter, sleeper) { Paused = true };
        sim.AdvanceTo(0);
        writer.PacedDrainOnce().Should().BeTrue("the frame is drained");
        (adapter.Queries, sleeper.Calls, adapter.Starts.Count).Should().Be((0, 0, 0));
    }

    [Fact]
    public void A_pause_during_the_wait_ends_it_and_the_frame_is_not_sent()
    {
        using var sim = new Sim();
        var c = Native("native:gpu");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(20, () => sim.Now, Us), () => sim.Now, sim.Spend);
        var sleeper = new JitterSleeper(sim, _ => -10_000); // wakes 10 ms early: the loop would sleep again
        using var writer = new DeviceWriter(box, adapter, sleeper);
        sim.AdvanceTo(0);
        writer.PacedDrainOnce();
        sleeper.OnSleep = _ => writer.Paused = true; // a lease / contention arrives mid-wait
        writer.PacedDrainOnce().Should().BeTrue();
        sleeper.Calls.Should().Be(1, "the paused writer stops waiting at once");
        adapter.Starts.Should().ContainSingle("nothing is sent while paused");
    }

    [Fact]
    public void A_failing_hint_is_counted_and_the_adapter_still_decides()
    {
        using var sim = new Sim();
        var c = Native("native:gpu");
        var box = sim.Add(c);
        var adapter = new PacedAdapter(new FrameBudget(20, () => sim.Now, Us), () => sim.Now, sim.Spend) { QueryError = new IOException("hint failed") };
        var errors = new List<string>();
        using var writer = new DeviceWriter(box, adapter, new JitterSleeper(sim, _ => 0), (_, ex) => errors.Add(ex.Message));
        sim.AdvanceTo(0);
        writer.PacedDrainOnce().Should().BeTrue();
        adapter.Starts.Should().ContainSingle("admitted by the budget itself");
        writer.GetStats().Errors.Should().Be(1);
        writer.GetStats().LastError.Should().Be("hint failed");
        errors.Should().Equal("hint failed");
    }

    [Fact]
    public void Stop_ends_a_long_paced_wait_within_one_slice_and_nothing_is_sent()
    {
        using var bus = new FrameBus();
        var c = Native("native:gpu");
        var box = bus.Register(c);
        var adapter = new PacedAdapter(new FrameBudget(20), Stopwatch.GetTimestamp, _ => { }) { FixedDelay = TimeSpan.FromSeconds(30) };
        using var writer = new DeviceWriter(box, adapter);
        writer.Start();
        bus.Publish(c.Id, box.Back);
        SpinWait.SpinUntil(() => Volatile.Read(ref adapter.QueryCount) > 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
        var sw = Stopwatch.StartNew();
        writer.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the wait is sliced at 100 ms and re-checks Stop");
        adapter.Starts.Should().BeEmpty();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Stop during a 30 s paced wait returned after {sw.Elapsed.TotalMilliseconds:0.0} ms"));
    }

    [Fact]
    public void Quiesce_during_a_paced_wait_returns_at_once_and_no_frame_is_sent_afterwards()
    {
        using var bus = new FrameBus();
        var c = Native("native:gpu");
        var box = bus.Register(c);
        var adapter = new PacedAdapter(new FrameBudget(20), Stopwatch.GetTimestamp, _ => { }) { FixedDelay = TimeSpan.FromSeconds(30) };
        using var writer = new DeviceWriter(box, adapter);
        writer.Start();
        bus.Publish(c.Id, box.Back);
        SpinWait.SpinUntil(() => Volatile.Read(ref adapter.QueryCount) > 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
        var sw = Stopwatch.StartNew();
        writer.Quiesce();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the wait holds no I/O lock");
        SpinWait.SpinUntil(() => box.Consumed > 0, TimeSpan.FromSeconds(5)).Should().BeTrue("the paused writer drains the frame");
        bus.Publish(c.Id, box.Back);
        Thread.Sleep(300);
        adapter.Starts.Should().BeEmpty("a quiesced writer sends nothing");
        writer.Stop();
    }

    [Fact]
    public void The_running_writer_thread_has_no_budget_skips_in_real_time()
    {
        using var bus = new FrameBus();
        var c = Native("native:aura");
        var box = bus.Register(c);
        var adapter = new PacedAdapter(new FrameBudget(20), Stopwatch.GetTimestamp, _ => { });
        using var writer = new DeviceWriter(box, adapter);
        writer.Start();
        var until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 3 / 2);
        long seq = 0;
        while (Stopwatch.GetTimestamp() < until)
        {
            var back = box.Back;
            back.Zones[0][0] = Encode(++seq);
            bus.Publish(c.Id, back);
            Thread.Sleep(16);
        }

        writer.Stop();
        var starts = adapter.Starts.Select(s => s.Start).ToArray();
        adapter.Budget.Stats().Skips.Should().Be(0, "a frame is offered only once the same budget reports it due");
        starts.Should().HaveCountGreaterThan(10);
        adapter.Refused.Should().Be(0);
        // starts are stamped just after admission, so two stamps may sit a few ticks closer than the admitted starts
        Gaps(starts).Should().OnlyContain(g => g >= (Stopwatch.Frequency / 20) - (Stopwatch.Frequency / 1000), "never above the ceiling");
        adapter.Starts.Select(s => s.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("no frame is replayed");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"real time: {starts.Length} frames, {(starts.Length - 1) * (double)Stopwatch.Frequency / (starts[^1] - starts[0]):0.00} fps at a 20 fps ceiling, 0 skips"));
    }

    [Fact]
    public void Adapters_without_a_pacing_source_and_uploaded_loops_keep_their_paths()
    {
        using var bus = new FrameBus();
        var plain = Native("native:plain");
        var recording = new RecordingAdapter();
        using var legacy = new DeviceWriter(bus.Register(plain), recording);
        legacy.PacedByAdapter.Should().BeFalse("the MaxFps grid stays for every other adapter");
        legacy.MaxFps.Should().Be(60);
        var act = () => legacy.PacedDrainOnce();
        act.Should().Throw<InvalidOperationException>();
        var box = bus[plain.Id]!;
        bus.Publish(plain.Id, box.Back);
        legacy.DrainOnce().Should().BeTrue();
        recording.Writes.Should().ContainSingle();

        var chain = new ControllerDescriptor(new ControllerId("lianli:test"), "Test", "chain", DeviceType.Cooler, DeviceClass.Virtual, "chain",
            [new ZoneDescriptor(new ZoneId(0), "chain", ZoneLayout.Linear(4))], Capabilities.UploadedLoopOnly(200, 20, 12288));
        var loops = new PacingLoopAdapter();
        using var uploaded = new DeviceWriter(bus.Register(chain), loops);
        uploaded.UploadsLoops.Should().BeTrue();
        uploaded.PacedByAdapter.Should().BeFalse("an UploadedLoop controller never takes the paced frame path");
        bus.PublishLoop(chain.Id, new ZoneId(0), new LoopFrames(1, 4, 20, 7, new byte[12]));
        uploaded.DrainOnce().Should().BeTrue();
        loops.Uploads.Should().Be(1);
        loops.Queries.Should().Be(0, "the hint is never read for uploaded loops");
    }

    // ---------------------------------------------------------------- harness

    private static ControllerDescriptor Native(string id) => TestSupport.Controller(id, ZoneLayout.Point);

    private static Rgb Encode(long seq) => new((byte)(seq & 0xFF), (byte)((seq >> 8) & 0xFF), (byte)((seq >> 16) & 0xFF));

    private static long Decode(Rgb c) => c.R | ((long)c.G << 8) | ((long)c.B << 16);

    /// <summary>Steps the writers in turn (each step is what its thread does after a publish) until virtual time passes <paramref name="untilUs"/>.</summary>
    private static void Run(Sim sim, DeviceWriter[] writers, long untilUs)
    {
        sim.AdvanceTo(0);
        while (sim.Now < untilUs)
        {
            var any = false;
            foreach (var w in writers)
            {
                any |= w.PacedDrainOnce();
            }

            if (!any)
            {
                sim.AdvanceTo(sim.NextPublish);
            }
        }
    }

    private static long[] Gaps(long[] starts) => starts.Zip(starts.Skip(1), (a, b) => b - a).ToArray();

    private static double Fps(long[] starts) => (starts.Length - 1) * (double)Us / (starts[^1] - starts[0]);

    /// <summary>Every frame sent is the newest the engine had published when its send began; none is replayed.</summary>
    private static void AssertLatestWins(Sim sim, PacedAdapter adapter, ControllerId id)
    {
        var sent = adapter.Starts.Where(s => s.Id == id).ToArray();
        sent.Should().OnlyContain(s => s.Seq == sim.NewestAt(id, s.Start), "latest wins after the wait");
        sent.Select(s => s.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    private void Report(string label, long[] starts, FrameBudgetStats stats, int sleeps)
    {
        var gaps = Gaps(starts);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}: {starts.Length} frames, {Fps(starts):0.000} fps, gap min {gaps.Min() / 1000.0:0.000} / max {gaps.Max() / 1000.0:0.000} ms, " +
            $"effective {stats.EffectiveFps} fps ({stats.FpsSource}), skips {stats.Skips}, sleeps {sleeps}"));
        output.WriteLine("first starts (ms): " + string.Join(", ", starts.Take(12).Select(s => (s / 1000.0).ToString("0.000", CultureInfo.InvariantCulture))));
    }

    /// <summary>Virtual time in µs plus the 60 fps engine that publishes one numbered frame per controller per tick.</summary>
    private sealed class Sim : IDisposable
    {
        private readonly List<ControllerDescriptor> _targets = [];
        private long _seq;

        public FrameBus Bus { get; } = new();

        public long Now { get; private set; }

        public long NextPublish { get; private set; }

        public List<(ControllerId Id, long Seq, long At)> Published { get; } = [];

        public Mailbox Add(ControllerDescriptor c)
        {
            _targets.Add(c);
            return Bus.Register(c);
        }

        public void Spend(long us) => AdvanceTo(Now + us);

        public void AdvanceTo(long t)
        {
            while (NextPublish <= t)
            {
                Now = Math.Max(Now, NextPublish);
                foreach (var c in _targets)
                {
                    var back = Bus[c.Id]!.Back;
                    back.Zones[0][0] = Encode(++_seq);
                    Bus.Publish(c.Id, back);
                    Published.Add((c.Id, _seq, NextPublish));
                }

                NextPublish += EngineUs;
            }

            Now = Math.Max(Now, t);
        }

        public long NewestAt(ControllerId id, long at) => Published.Where(p => p.Id == id && p.At <= at).Max(p => p.Seq);

        public void Dispose() => Bus.Dispose();
    }

    /// <summary>Advances virtual time by the requested remainder plus a scripted jitter (never less than 1 µs: no spin).</summary>
    private sealed class JitterSleeper(Sim sim, Func<int, long> jitterUs) : IPreciseSleeper
    {
        public int Calls { get; private set; }

        public Action<int>? OnSleep { get; set; }

        public void SleepUntil(long deadlineTicks)
        {
            var remaining = deadlineTicks - Stopwatch.GetTimestamp();
            var requested = Math.Max(1L, (long)Math.Ceiling(remaining * (double)Us / Stopwatch.Frequency));
            var n = Calls++;
            OnSleep?.Invoke(n);
            sim.AdvanceTo(sim.Now + Math.Max(1L, requested + jitterUs(n)));
        }
    }

    /// <summary>A whole-frame native fake: the real FrameBudget admits each frame (per member), the send costs virtual time.</summary>
    private sealed class PacedAdapter(FrameBudget budget, Func<long> clock, Action<long> spend) : IWholeFrameAdapter, IFramePacingSource
    {
        public int QueryCount;

        public FrameBudget Budget => budget;

        public Dictionary<ControllerId, string> Members { get; } = [];

        public Func<ControllerId, int, long> CostUs { get; set; } = (_, _) => 0;

        public List<(ControllerId Id, long Seq, long Start)> Starts { get; } = [];

        public int Refused { get; private set; }

        public int Queries => Volatile.Read(ref QueryCount);

        public Exception? QueryError { get; set; }

        /// <summary>When set, reported instead of the budget's delay (threaded stop/quiesce tests).</summary>
        public TimeSpan? FixedDelay { get; set; }

        public string Vendor => "Test";

        public DeviceClass Class => DeviceClass.Virtual;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public long[] StartsOf(ControllerId id) => Starts.Where(s => s.Id == id).Select(s => s.Start).ToArray();

        public bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones)
        {
            using var scope = budget.TryBeginFrame(Member(id));
            if (scope is null)
            {
                Refused++;
                return false;
            }

            budget.CountOperation();
            lock (Starts)
            {
                Starts.Add((id, Decode(zones[0][0]), clock()));
            }

            spend(CostUs(id, Starts.Count));
            scope.Complete();
            return true;
        }

        public TimeSpan DelayUntilNextFrame(ControllerId id)
        {
            Interlocked.Increment(ref QueryCount);
            if (QueryError is { } e)
            {
                throw e;
            }

            return FixedDelay ?? budget.DelayUntilNextFrame(Member(id));
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);

        public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new NotSupportedException("whole frames only");

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => Task.CompletedTask;

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;

        private string Member(ControllerId id) => Members.TryGetValue(id, out var m) ? m : string.Empty;
    }

    /// <summary>An uploaded-loop adapter that would also answer the pacing hint: it must never be asked.</summary>
    private sealed class PacingLoopAdapter : ILoopUploadAdapter, IFramePacingSource
    {
        public int Uploads { get; private set; }

        public int Queries { get; private set; }

        public string Vendor => "Test";

        public DeviceClass Class => DeviceClass.Virtual;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public TimeSpan DelayUntilNextFrame(ControllerId id)
        {
            Queries++;
            return TimeSpan.FromSeconds(30);
        }

        public Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct)
        {
            Uploads++;
            return Task.CompletedTask;
        }

        public LoopUploadStats? LastUpload(ControllerId id) => null;

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);

        public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new NotSupportedException("uploaded loops only");

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => Task.CompletedTask;

        public Telemetry? ReadTelemetry(ControllerId id) => null;

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }
}
