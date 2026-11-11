using System.Globalization;
using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>Only the real RF connection over fake HID. History is passive host timing, never radio/delivery evidence.</summary>
public sealed class RogWirelessFlightHistoryTests
{
    [Fact]
    public async Task FlightHistory_timeout_retains_the_seven_report_burst_control_transition_and_late_colour_replies()
    {
        var time = new FlightClock(); using var channel = new FlightChannel(time) { DropControl = true, ColourReplies = true };
        using var connection = Open(channel, time);
        var keys = Enumerable.Range(0, 85).Select(i => new RogKeyColor((byte)i, new(0xD6, 0xE7, 0xF8))).ToArray();
        await connection.WriteLightingFrameAsync(keys, default);
        var error = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default)))
            .Should().ThrowAsync<RogOledTimeoutException>();
        var detail = error.Which.Message;
        detail.Should().Contain("colourReportsAdmitted=7; colourReportsCompleted=7; c082Observed=7; lastColourDoneMs=14.000")
            .And.Contain("rfElapsedMs=2045.000")
            .And.Contain("0.000:RGB/TxAdmit:02C0810E00/64")
            .And.Contain("14.000:RGB/TxDone:02C0810100/64|14.000:PreDrain/Start:0223030000/64")
            .And.Contain("18.000:PreDrain/RxDone:02C0820E00/64")
            .And.Contain("42.000:PreDrain/RxDone:02C0820100/64")
            .And.Contain("1042.000:Exchange/TxAdmit:0223030000/64|1045.000:Exchange/TxDone:0223030000/64")
            .And.NotContain("D6E7F8", "RGB payloads are never retained or emitted");
        Entries(detail).Should().HaveCount(25);
        channel.Reads.Should().Be(9); channel.MaximumReaders.Should().Be(1); channel.PendingReads.Should().Be(0);
        channel.Writes.Should().Be(8, "instrumentation sends no extra reports");
    }

    [Fact]
    public async Task FlightHistory_is_bounded_to_128_records_and_includes_lease_without_changing_ownership()
    {
        var time = new FlightClock(); using var channel = new FlightChannel(time);
        using var connection = Open(channel, time);
        for (var i = 0; i < 60; i++) await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default);
        var renewed = false;
        await connection.LeaseAsync(() => renewed, () => renewed = true, default);
        channel.DropControl = true;
        var error = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default)))
            .Should().ThrowAsync<RogOledTimeoutException>();
        var entries = Entries(error.Which.Message);
        entries.Should().HaveCount(128);
        entries.Should().Contain(e => e.Contains("Lease/TxAdmit:0267020000/64", StringComparison.Ordinal));
        entries.Should().Contain(e => e.Contains("Lease/TxDone:0267020000/64", StringComparison.Ordinal));
        entries.Should().Contain(e => e.Contains("Lease/RxDone:0267020000/64", StringComparison.Ordinal));
        entries[0].Should().NotStartWith("0.000:", "the oldest records were evicted");
        entries.Select(e => double.Parse(e.AsSpan(0, e.IndexOf(':')), CultureInfo.InvariantCulture)).Should().BeInAscendingOrder();
        error.Which.Message.Should().Contain("flightRecords=128/305")
            .And.Contain("colourReportsAdmitted=0; colourReportsCompleted=0; c082Observed=0; lastColourDoneMs=none");
        channel.Writes.Should().Be(62); channel.MaximumReaders.Should().Be(1); channel.PendingReads.Should().Be(0);
    }

    private static string[] Entries(string message)
    {
        var start = message.IndexOf("rfFlight=[", StringComparison.Ordinal) + "rfFlight=[".Length;
        var end = message.IndexOf(']', start);
        return message[start..end].Split('|');
    }
    private static RogWirelessOledConnection Open(FlightChannel channel, TimeProvider time) => new(
        new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), channel, "0800070025000100", () => true)
    { KeyReportDelayMs = 0, EvidenceTime = time };

    private sealed class FlightClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(int milliseconds) => Interlocked.Add(ref _ticks, milliseconds);
    }
    private sealed class FlightChannel(FlightClock time) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public bool DropControl { get; set; }
        public bool ColourReplies { get; init; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public int PendingReads { get; private set; }
        public int MaximumReaders { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Writes++;
            var p = report.ToArray(); var colour = p[1] == 0xC0 && p[2] == 0x81;
            time.Advance(colour || !DropControl ? 2 : 3);
            if (colour)
            {
                if (ColourReplies) { var reply = new byte[64]; reply[0] = 2; reply[1] = 0xC0; reply[2] = 0x82; reply[3] = p[3]; _replies.Writer.TryWrite(reply); }
            }
            else if (!DropControl)
            {
                var reply = new byte[64]; p.AsSpan(0, 5).CopyTo(reply); if (p[1] == 0x23) reply[5] = 37;
                _replies.Writer.TryWrite(reply);
            }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++; MaximumReaders = Math.Max(MaximumReaders, ++PendingReads);
            try
            {
                if (DropControl && _replies.Reader.Count == 0) time.Advance(1000);
                var reply = await _replies.Reader.ReadAsync(ct); time.Advance(4); reply.CopyTo(buffer); return reply.Length;
            }
            finally { PendingReads--; }
        }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
