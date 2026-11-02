using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>The actual RF connection over one inert channel and a monotonic fake clock. No HID devices are opened.
/// Replays four early C082/14, two late C082/14 and a missing final C082/1, as in the captured19:54 fault.
/// Replies are queue pressure observations only; none uniquely acknowledges an RGB chunk or proves delivery.</summary>
public sealed class RogWirelessColourTailTests
{
    private static RogKeyColor[] Keys => Enumerable.Range(0, 85)
        .Select(i => new RogKeyColor((byte)i, new((byte)i, (byte)(255 - i), (byte)(i * 3)))).ToArray();

    [Fact]
    public async Task Four_early_two_late_colour_replies_do_not_allow_control_inside_the_RF_burst_tail()
    {
        using var io = new TailChannel(); using var connection = Open(io);
        var before = connection.PairingEvidence;
        await connection.WriteLightingFrameAsync(Keys, default);
        io.Reads.Should().Be(0, "the seven RGB packets stay contiguous, without per-packet read/waits");
        io.Writes.Select(Convert.ToHexString).Should().Equal(RogProtocol.KeyboardFrame(2, Keys).Select(Convert.ToHexString));
        io.Writes.Select(p => p[3]).Should().Equal(14, 14, 14, 14, 14, 14, 1);
        var expectedSpace = Keys.Where(k => new byte[] { 37, 45, 53, 61, 69 }.Contains(k.LedId)).ToArray();
        expectedSpace.Select(k => k.Color).Distinct().Should().HaveCount(5);
        connection.PairingEvidence.Should().Be(before, "write completion and C082 are not pairing or optical evidence");

        var reply = await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        reply[1].Should().Be(0x12); reply[5].Should().Be(0x08);
        io.ControlAt.Should().BeGreaterThanOrEqualTo(16, "2ms quiet ended while the observed RF burst tail was still in flight");
        io.LateRepliesRead.Should().Be(2); io.ControlWrites.Should().Be(1, "no query/setter retry is added");
        io.MaximumReaders.Should().Be(1); io.PendingReads.Should().Be(0);
        connection.PairingEvidence!.Observations.Should().Be(1, "only the strict fresh basic identity advances evidence");
    }

    [Fact]
    public async Task All_colour_replies_missing_uses_a_finite_settle_bound_without_new_freeze_or_poison()
    {
        using var io = new TailChannel { EarlyReplies = 0, LateReplies = 0 }; using var connection = Open(io);
        await connection.WriteLightingFrameAsync(Keys, default);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        io.ControlAt.Should().BeInRange(16, 32); io.ControlWrites.Should().Be(1);
        io.PendingReads.Should().Be(0); connection.PairingEvidence!.Invalidated.Should().BeNull();
        io.Clock.Advance(40);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        io.ControlWrites.Should().Be(2, "the owner remains usable after missing optional colour replies");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(128)]
    public async Task Repeated_same_count_replies_cannot_bypass_the_tail_floor_as_unique_ACKs(int replies)
    {
        using var io = new TailChannel { EarlyReplies = replies, LateReplies = 0 }; using var connection = Open(io);
        await connection.WriteLightingFrameAsync(Keys, default);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
        io.ControlAt.Should().BeInRange(16, 32);
        io.ControlWrites.Should().Be(1); io.PendingReads.Should().Be(0);
    }

    private static RogWirelessOledConnection Open(TailChannel io) => new(
        new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => true)
    {
        EvidenceTime = io.Clock, KeyReportDelayMs = 0,
        NativeControlBinding = new([], new(0x1C25, 2, 4), 0),
    };

    private sealed class Clock : TimeProvider
    {
        private long _now;
        private long? _runningAt;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _now)
            + (_runningAt is { } at ? global::System.Diagnostics.Stopwatch.GetElapsedTime(at).Ticks : 0);
        public double Milliseconds => TimeSpan.FromTicks(GetTimestamp()).TotalMilliseconds;
        public void Advance(double milliseconds) => Interlocked.Add(ref _now, TimeSpan.FromMilliseconds(milliseconds).Ticks);
        public void Run() => _runningAt ??= global::System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private sealed class TailChannel : IHidReportChannel
    {
        private readonly Channel<byte[]> _strict = Channel.CreateUnbounded<byte[]>();
        private int _earlyRead;
        public Clock Clock { get; } = new();
        public int EarlyReplies { get; init; } = 4;
        public int LateReplies { get; init; } = 2;
        public int LateRepliesRead { get; private set; }
        public int Reads { get; private set; }
        public int PendingReads { get; private set; }
        public int MaximumReaders { get; private set; }
        public int ControlWrites { get; private set; }
        public double ControlAt { get; private set; }
        public List<byte[]> Writes { get; } = [];

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); PendingReads.Should().Be(0, "the previous actual read must join before TX");
            var p = report.ToArray(); Writes.Add(p);
            if (p[1] == 0xC0 && p[2] == 0x81) return ValueTask.CompletedTask;
            ControlWrites++; ControlAt = Clock.Milliseconds;
            // An early control is swallowed by the still-occupied RF path in this failure fixture. Later RGB responses
            // remain readable, but no fake response is invented for the swallowed query.
            if (ControlAt >= 16)
            {
                var reply = new byte[64]; reply[0] = 2; reply[1] = 0x12;
                reply[5] = 8; reply[7] = 7; reply[9] = 6; reply[11] = 6; reply[12] = 0x25; reply[14] = 1;
                _strict.Writer.TryWrite(reply).Should().BeTrue();
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++; PendingReads++; MaximumReaders = Math.Max(MaximumReaders, PendingReads);
            try
            {
                byte[] reply;
                if (_earlyRead < EarlyReplies)
                {
                    _earlyRead++; reply = Colour();
                    // At the existing128-report allowance the production drain deliberately stops reading. Use real elapsed
                    // time for this one budget-edge case so timer-only settlement does not depend on a fake read callback.
                    if (EarlyReplies == 128 && _earlyRead == EarlyReplies) Clock.Run();
                }
                else if (LateRepliesRead < LateReplies && (Clock.Milliseconds >= 11 || ControlWrites > 0))
                {
                    if (Clock.Milliseconds < 11) Clock.Advance(11 - Clock.Milliseconds);
                    Clock.Advance(1); LateRepliesRead++; reply = Colour();
                }
                else if (_strict.Reader.TryRead(out var strict)) reply = strict;
                else
                {
                    try { reply = await _strict.Reader.ReadAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        Clock.Advance(2); // Each completed existing high-resolution quiet window advances the fake monotonic clock.
                        throw;
                    }
                }
                reply.CopyTo(buffer); return reply.Length;
            }
            finally { PendingReads--; }
        }

        private static byte[] Colour()
        {
            var reply = new byte[64]; reply[0] = 2; reply[1] = 0xC0; reply[2] = 0x82; reply[3] = 14;
            reply.AsSpan(5, 23).Fill(0xA5); return reply;
        }
        public void Dispose() => _strict.Writer.TryComplete();
    }
}
