using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogAtomicTransactionTests
{
    private static IRogOledConnection Open(Fake io, bool rf) => rf
        ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => true)
        : new RogWiredOledConnection(io, () => true);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_after_completed_write_consumes_its_reply_before_a_different_command(bool rf)
    {
        using var io = new Fake(rf); using var connection = Open(io, rf); using var stop = new CancellationTokenSource();
        io.AfterWrite = stop.Cancel;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        io.Queued.Should().Be(0); io.Reads.Should().Be(1); io.PendingReads.Should().Be(0);
        io.AfterWrite = null;
        (await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default))[1].Should().Be(0x12);
        io.Writes.Should().Be(2); io.Queued.Should().Be(0);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Same_header_query_after_cancellation_cannot_accept_the_previous_value(bool rf)
    {
        using var io = new Fake(rf) { Brightness = 10 }; using var connection = Open(io, rf); using var stop = new CancellationTokenSource();
        io.AfterWrite = stop.Cancel;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        io.AfterWrite = null; io.Brightness = 20;
        (await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))[5].Should().Be(20);
        io.Queued.Should().Be(0); io.Reads.Should().Be(2);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Pre_canceled_operations_have_zero_wire_io_and_do_not_poison_a_healthy_connection(bool rf)
    {
        using var io = new Fake(rf); using var connection = Open(io, rf); using var stop = new CancellationTokenSource(); stop.Cancel();
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => { }, stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        io.Writes.Should().Be(0); io.Reads.Should().Be(0);
        (await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))[5].Should().Be(100);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Partial_out_failure_is_terminal_and_only_an_explicit_fresh_connection_can_continue(bool rf)
    {
        using var io = new Fake(rf) { ShortNextOut = true }; using var connection = Open(io, rf);
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))).Should().ThrowAsync<IOException>();
        await NoFurtherIo(connection, io); io.PendingWrites.Should().Be(0);
        using var freshIo = new Fake(rf) { Brightness = 20 }; using var fresh = Open(freshIo, rf);
        (await fresh.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))[5].Should().Be(20);
        freshIo.Writes.Should().Be(1);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Missing_reply_after_caller_stop_still_reaches_command_deadline_and_poison(bool rf)
    {
        using var io = new Fake(rf) { DropReply = true }; using var connection = Open(io, rf); using var stop = new CancellationTokenSource();
        io.AfterWrite = stop.Cancel;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, stop.Token))).Should().ThrowAsync<RogOledTimeoutException>()
            .WithMessage("*stage=read*reply=none*connection poisoned*");
        io.PendingReads.Should().Be(0); await NoFurtherIo(connection, io);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unexpected_native_echo_is_not_drained_and_poison_retains_the_first_bounded_diagnostic(bool rf)
    {
        using var io = new Fake(rf); using var connection = Open(io, rf);
        var stale = new byte[65]; stale[1] = 0x51; stale[2] = 0x2C; stale[5] = 30; io.Enqueue(stale);
        var failure = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default))).Should().ThrowAsync<InvalidDataException>();
        failure.Which.Message.Should().Contain("stage=optional-renewal").And.Contain("request=" + (rf ? "02" : "00") + "12000000")
            .And.Contain("reply=" + (rf ? "02" : "00") + "512C0000");
        io.Queued.Should().Be(1); await NoFurtherIo(connection, io); io.Queued.Should().Be(1);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Bad_ack_wins_over_a_racing_normal_stop_and_freezes_the_connection(bool rf)
    {
        using var io = new Fake(rf) { NonzeroAck = true }; using var connection = Open(io, rf); using var stop = new CancellationTokenSource(); io.AfterWrite = stop.Cancel;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Preset(3), true, stop.Token))).Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*stage=ack*nonzeroAckBody=True*");
        await NoFurtherIo(connection, io);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Existing_full_echo_setters_are_verified_before_retirement_even_when_the_caller_stops(bool rf, bool native)
    {
        using var io = new Fake(rf) { BadEcho = true }; using var connection = Open(io, rf); using var stop = new CancellationTokenSource(); io.AfterWrite = stop.Cancel;
        var request = RogWiredOledProtocol.Brightness(20);
        if (native) { request = new byte[65]; RogProtocol.KeyboardNativeReports(2, RogProtocol.KeyboardColourFrame(new(1, 2, 3), 25, 80))[0].CopyTo(request, 0); request[0] = 0; }
        await ((Func<Task>)(() => connection.ExchangeAsync(request, false, stop.Token))).Should().ThrowAsync<InvalidDataException>().WithMessage("*stage=echo*");
        await NoFurtherIo(connection, io);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Attempted_lease_write_timeout_is_not_success_and_all_workers_are_joined(bool rf)
    {
        using var io = new Fake(rf) { BlockRenewWrite = true }; using var connection = Open(io, rf); var renewed = 0;
        await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => renewed++, default))).Should().ThrowAsync<RogOledTimeoutException>()
            .WithMessage("*stage=lease-write*");
        io.WriteCancellationObserved.Should().BeTrue(); renewed.Should().Be(0);
        io.PendingReads.Should().Be(0); io.PendingWrites.Should().Be(0); await NoFurtherIo(connection, io);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Normal_lease_stop_finishes_its_admitted_write_then_joins_before_returning_cancellation(bool rf)
    {
        using var io = new Fake(rf) { GateRenewWrite = true, EchoRenew = true }; using var connection = Open(io, rf); using var stop = new CancellationTokenSource(); var renewed = 0;
        var lease = connection.LeaseAsync(() => false, () => renewed++, stop.Token);
        await io.RenewAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        lease.IsCompleted.Should().BeFalse(); io.WriteCancellationObserved.Should().BeFalse();
        io.FinishRenew.SetResult(); await ((Func<Task>)(() => lease)).Should().ThrowAsync<OperationCanceledException>();
        renewed.Should().Be(1); io.PendingReads.Should().Be(0); io.PendingWrites.Should().Be(0);
        io.Brightness = 20;
        (await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))[5].Should().Be(20);
        io.Writes.Should().Be(2); io.Queued.Should().Be(0);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Lease_reader_protocol_failure_prevents_the_next_write_and_keeps_the_route_terminal(bool rf)
    {
        using var io = new Fake(rf); using var connection = Open(io, rf);
        var stale = new byte[65]; stale[1] = 0x51; stale[2] = 0x2C; io.Enqueue(stale);
        await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => { }, default))).Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*stage=lease-read*");
        io.Writes.Should().Be(0); io.PendingReads.Should().Be(0); await NoFurtherIo(connection, io);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Recognized_optional_renewals_keep_the_existing_eight_report_budget(bool rf)
    {
        using var io = new Fake(rf); using var connection = Open(io, rf);
        io.Enqueue(RogWiredOledProtocol.Renew());
        (await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))[5].Should().Be(100);
        for (var i = 0; i < 9; i++) io.Enqueue(RogWiredOledProtocol.Renew());
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))).Should().ThrowAsync<InvalidDataException>();
        var queued = io.Queued; queued.Should().Be(2); await NoFurtherIo(connection, io); io.Queued.Should().Be(queued);
    }

    private static async Task NoFurtherIo(IRogOledConnection connection, Fake io)
    {
        var writes = io.Writes; var reads = io.Reads;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Preset(3), true, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        await ((Func<Task>)(() => connection.LeaseAsync(() => true, () => { }, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Writes.Should().Be(writes); io.Reads.Should().Be(reads);
    }

    // Astra Harness.Fake source reproduced here, with explicit fault/join gates added. All traffic is fake vendor control.
    private sealed class Fake(bool rf) : IHidReportChannel
    {
        private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>();
        public int Queued => _queue.Reader.Count;
        public byte Brightness = 100;
        public bool NonzeroAck, BadEcho, BlockRenewWrite, GateRenewWrite, EchoRenew, ShortNextOut, DropReply, WriteCancellationObserved;
        public Action? AfterWrite;
        public int Writes, Reads, PendingWrites, PendingReads;
        public TaskCompletionSource RenewAdmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishRenew { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enqueue(byte[] normalized)
        { var wire = new byte[rf ? 64 : 65]; normalized.AsSpan(0, wire.Length).CopyTo(wire); wire[0] = rf ? (byte)2 : (byte)0; _queue.Writer.TryWrite(wire).Should().BeTrue(); }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Writes++; PendingWrites++; var p = report.ToArray();
            try
            {
                if (ShortNextOut) { ShortNextOut = false; throw new IOException("Synthetic partial OUT; completion unknown"); }
                if (p[1] == 0x67 && p[2] == 2)
                {
                    RenewAdmitted.TrySetResult();
                    if (BlockRenewWrite) await Task.Delay(Timeout.Infinite, ct);
                    if (GateRenewWrite) await FinishRenew.Task.WaitAsync(ct);
                    if (EchoRenew) Enqueue(RogWiredOledProtocol.Renew()); return;
                }
                if (!DropReply)
                {
                    var b = new byte[65]; p.AsSpan(0, 5).CopyTo(b); b[0] = 0;
                    if (p[1] == 0x23 && p[2] == 3) b[5] = Brightness;
                    if (p[1] == 0x12) { b[5] = rf ? (byte)8 : (byte)0x24; b[7] = rf ? (byte)7 : (byte)1; b[9] = 6; b[11] = 6; if (rf) { b[12] = 0x25; b[14] = 1; } }
                    if (p[1] is 0x68 or 0x51) { p.CopyTo(b, 0); b[0] = 0; if (BadEcho) b[5] ^= 1; }
                    if (p[1] == 0x61 && NonzeroAck) b[9] = 0x7F;
                    Enqueue(b);
                }
                AfterWrite?.Invoke();
            }
            catch (OperationCanceledException) { WriteCancellationObserved = true; throw; }
            finally { PendingWrites--; }
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Reads++; PendingReads++;
            try { var b = await _queue.Reader.ReadAsync(ct); b.CopyTo(buffer); return b.Length; }
            finally { PendingReads--; }
        }
        public void Dispose() => _queue.Writer.TryComplete();
    }
}
