using System.Threading.Channels;
using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;
using Xunit.Abstractions;

namespace UnifiedRgb.Host.Tests;

public sealed class RogWirelessPreWriteDrainTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PreWriteDrain_queued_identical_header_old_body_is_consumed_before_new_GET12_is_written()
    {
        using var channel = new DrainChannel(); using var connection = Open(channel);
        var request = RogWiredOledProtocol.Query(0x12, 0);
        var first = await connection.ExchangeAsync(request, false, default);
        first[20].Should().Be(0x11);
        channel.Enqueue(first, stale: true);
        var current = await connection.ExchangeAsync(request, false, default);
        current[20].Should().Be(0x22, "the already completed old reply cannot answer a command that has not yet been written");
        channel.StaleReadAtWrite.Should().Be(1); channel.Writes.Should().Be(2);
        channel.MaximumReaders.Should().Be(1); channel.PendingReads.Should().Be(0);
    }

    [Fact]
    public async Task PreWriteDrain_cancel_joins_the_actual_pending_read_sends_nothing_and_leaves_data_chunks_unpaced()
    {
        using var channel = new DrainChannel(); using var connection = Open(channel);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        channel.EmptyReadStarts.Should().Be(0, "the very first wire command preserves existing startup reply handling");
        channel.HoldCancelledRead = true;
        using var stop = new CancellationTokenSource();
        var exchange = connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, stop.Token);
        await channel.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await channel.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        exchange.IsCompleted.Should().BeFalse("the actual cancelled read has not joined yet");
        channel.Writes.Should().Be(1, "cancellation did not admit another TX");
        channel.ReleaseRead.TrySetResult();
        await ((Func<Task>)(() => exchange)).Should().ThrowAsync<OperationCanceledException>();
        channel.PendingReads.Should().Be(0); channel.HoldCancelledRead = false;
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        channel.WriteWhileReading.Should().BeFalse();

        using var chunkChannel = new DrainChannel(); using var chunkOwner = Open(chunkChannel);
        var chunk = new byte[65]; chunk[1] = 0x67; chunk[2] = 1; chunk[3] = 25;
        await chunkOwner.ExchangeAsync(chunk, true, default);
        chunkChannel.EmptyReadStarts.Should().Be(0, "67/1 begins its strict write without adding any drain/quiet wait");
        chunkChannel.Writes.Should().Be(1);
    }

    [Fact]
    public async Task PreWriteDrain_cap_is_nonfatal_and_truthfully_traced_after_the_read_has_joined()
    {
        using var channel = new DrainChannel(); using var connection = Open(channel);
        await connection.WriteLightingFrameAsync(Enumerable.Range(0, 85).Select(i => new RogKeyColor((byte)i, new(1, 2, 3))).ToArray(), default);
        channel.ContinuousColour = true;
        var reply = await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        reply[20].Should().Be(0x11); channel.Writes.Should().Be(8);
        channel.WriteWhileReading.Should().BeFalse(); channel.PendingReads.Should().Be(0);
        channel.DropControl = true;
        var failure = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default)))
            .Should().ThrowAsync<RogOledTimeoutException>();
        failure.Which.Message.Should().Contain("PreDrain/Cap:0212000000/64").And.Contain("c082Observed=1")
            .And.Contain("PreDrain/RxDone:02C0820E00/64");
    }

    [Theory]
    [InlineData("unknown")] [InlineData("refused")] [InlineData("malformed")]
    public async Task PreWriteDrain_unknown_refused_or_malformed_pre_TX_reports_still_fault_without_writing(string kind)
    {
        using var channel = new DrainChannel(); using var connection = Open(channel);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        var reply = new byte[65]; reply[1] = kind == "refused" ? (byte)0xFF : (byte)0x12;
        reply[2] = kind == "refused" ? (byte)0xAA : (byte)0;
        if (kind == "malformed") reply[3] = 1;
        channel.Enqueue(reply);
        var act = () => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        if (kind == "refused") await act.Should().ThrowAsync<RogOledEnvelopeRefusedException>();
        else await act.Should().ThrowAsync<InvalidDataException>();
        channel.Writes.Should().Be(1);
        await act.Should().ThrowAsync<IOException>(); channel.Writes.Should().Be(1);
    }

    [Fact]
    public async Task PreWriteDrain_real_timer_microbenchmark_uses_only_a_normal_fake_channel_and_reports_cost()
    {
        using var channel = new DrainChannel(); using var connection = Open(channel);
        var query = RogWiredOledProtocol.Query(0x23, 3);
        for (var i = 0; i < 4; i++) await connection.ExchangeAsync(query, false, default);
        var samples = new double[64];
        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp(); await connection.ExchangeAsync(query, false, default);
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        output.WriteLine("PREWRITE_TIMER_BENCH=" + JsonSerializer.Serialize(new { samples = samples.Length,
            medianMs = (samples[31] + samples[32]) / 2, p95Ms = samples[60], minMs = samples[0], maxMs = samples[^1],
            highResolutionWindows = typeof(RogWirelessOledConnection).GetField("_drainHighResolutionWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection),
            coarseWindows = typeof(RogWirelessOledConnection).GetField("_drainCoarseWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection),
            scope = "real2ms highres waitabletimer optional-read cancellation+join and synchronous fake query; noHID/no system timer change" }));
        channel.Writes.Should().Be(68); channel.EmptyReadStarts.Should().Be(67);
        channel.WriteWhileReading.Should().BeFalse(); channel.MaximumReaders.Should().Be(1);
    }

    [Fact]
    public async Task PreWriteDrain_read_window_disposal_races_cancellation_callbacks_and_repeated_windows_safely()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = HidReadWindow.Start(TimeSpan.FromMilliseconds(2), default);
        using var registration = first.Token.Register(() => { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); finished.TrySetResult(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Task.Run(first.Dispose).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.Set(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); first.Token.IsCancellationRequested.Should().BeTrue();
        var highResolution = 0;
        for (var i = 0; i < 32; i++)
        {
            using var caller = new CancellationTokenSource();
            using var window = HidReadWindow.Start(TimeSpan.FromMilliseconds(1), caller.Token);
            if (window.HighResolutionUsed) highResolution++;
            var read = Task.Delay(Timeout.InfiniteTimeSpan, window.Token);
            if (i % 2 == 0) caller.Cancel();
            await ((Func<Task>)(() => read)).Should().ThrowAsync<OperationCanceledException>();
            window.Dispose(); // idempotent second disposal at using-exit, after the awaited read has actually joined
        }
        output.WriteLine("HID_READ_WINDOW_RACE=" + JsonSerializer.Serialize(new { windows = 32, highResolution, firstHighResolution = first.HighResolutionUsed }));
    }

    private static RogWirelessOledConnection Open(DrainChannel channel) => new(
        new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), channel, "0800070025000100", () => true)
    { KeyReportDelayMs = 0 };

    private sealed class DrainChannel : IHidReportChannel
    {
        private readonly Channel<(byte[] Reply, bool Stale)> _replies = Channel.CreateUnbounded<(byte[], bool)>();
        public int Writes { get; private set; }
        public int PendingReads { get; private set; }
        public int MaximumReaders { get; private set; }
        public int StaleReadAtWrite { get; private set; }
        private int _controlWrites;
        public bool HoldCancelledRead { get; set; }
        public bool ContinuousColour { get; set; }
        public bool DropControl { get; set; }
        public bool WriteWhileReading { get; private set; }
        public int EmptyReadStarts { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enqueue(byte[] normalized, bool stale = false)
        { var wire = new byte[64]; wire[0] = 2; normalized.AsSpan(1, 63).CopyTo(wire.AsSpan(1)); _replies.Writer.TryWrite((wire, stale)); }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Writes++;
            WriteWhileReading |= PendingReads > 0;
            var reply = new byte[65]; report.Span[1..].CopyTo(reply.AsSpan(1));
            if (reply[1] == 0xC0) return ValueTask.CompletedTask;
            ContinuousColour = false; _controlWrites++;
            if (DropControl) return ValueTask.CompletedTask;
            if (reply[1] == 0x12) { reply[9] = 6; reply[11] = 6; reply[12] = 0x25; reply[20] = _controlWrites == 1 ? (byte)0x11 : (byte)0x22; }
            Enqueue(reply); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            MaximumReaders = Math.Max(MaximumReaders, ++PendingReads);
            try
            {
                if (ContinuousColour)
                {
                    // Complete one real read after the recent-RGB32ms cap, modelling an I/O completion racing cancellation. The owner must
                    // join it, validate the report, record Cap and proceed once; it must not fabricate a quiet outcome.
                    await Task.Delay(40, CancellationToken.None); buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0xC0;
                    buffer.Span[2] = 0x82; buffer.Span[3] = 14; return 64;
                }
                if (_replies.Reader.Count == 0) { EmptyReadStarts++; ReadStarted.TrySetResult(); }
                (byte[] Reply, bool Stale) item;
                try { item = await _replies.Reader.ReadAsync(ct); }
                catch (OperationCanceledException) when (HoldCancelledRead)
                { CancellationObserved.TrySetResult(); await ReleaseRead.Task; throw; }
                if (item.Stale) StaleReadAtWrite = Writes; item.Reply.CopyTo(buffer); return item.Reply.Length;
            }
            finally { PendingReads--; }
        }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
