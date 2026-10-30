using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Fake-only RF per-key pacing trial: an optional pause before every C0 81 report, requested by the actor on the wireless route
/// only. Recorded pauses do not wait; nothing here shows that pacing fixes a physical receiver timeout.
/// </summary>
public sealed class RogPerKeyPacingTests
{
    private const string RfHeader = "0800070024000100";
    private static readonly TimeSpan Eight = TimeSpan.FromMilliseconds(8);

    // 85 independent LEDs (as in the current local map): distinct ids and colours, 6 full reports and one single-record report.
    private static RogKeyColor[] Keys(int count) =>
        Enumerable.Range(0, count).Select(i => new RogKeyColor((byte)i, new((byte)i, (byte)(255 - i), (byte)(i * 3)))).ToArray();

    private static IRogOledConnection Open(KeyFake io, bool rf) => rf
        ? new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, RfHeader, () => true)
        : new RogWiredOledConnection(io, () => true);

    private static IEnumerable<string> Hex(IEnumerable<byte[]> reports) => reports.Select(Convert.ToHexString);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_paced_frame_waits_before_every_report_including_the_first_and_sends_identical_bytes(bool rf)
    {
        var keys = Keys(85);
        using var plainInner = new RogWiredOledTests.OledFake(); using var plain = new KeyFake(plainInner, rf); using var plainConnection = Open(plain, rf);
        var unexpected = 0;
        await RogPerKeyLighting.WriteAsync(plainConnection.ExchangeAsync, keys, default, TimeSpan.Zero, (_, _) => { unexpected++; return Task.CompletedTask; });
        unexpected.Should().Be(0, "the default zero delay never waits");

        using var pacedInner = new RogWiredOledTests.OledFake(); using var paced = new KeyFake(pacedInner, rf); using var pacedConnection = Open(paced, rf);
        var pauses = new List<(TimeSpan Delay, int SentBefore)>();
        await RogPerKeyLighting.WriteAsync(pacedConnection.ExchangeAsync, keys, default, Eight,
            (d, _) => { pauses.Add((d, paced.KeyReports.Count)); return Task.CompletedTask; });

        pauses.Should().Equal(Enumerable.Range(0, 7).Select(i => (Eight, i)));
        Hex(paced.KeyReports).Should().Equal(Hex(plain.KeyReports));
        paced.KeyReports.Select(r => r[3]).Should().Equal(14, 14, 14, 14, 14, 14, 1);
        paced.Decoded.Should().Equal(keys);

        var negative = () => RogPerKeyLighting.WriteAsync(pacedConnection.ExchangeAsync, keys, default, TimeSpan.FromMilliseconds(-1));
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();
        paced.KeyReports.Should().HaveCount(7, "an invalid delay sends nothing");
    }

    [Fact]
    public async Task Cancellation_during_a_pause_sends_nothing_further()
    {
        using var inner = new RogWiredOledTests.OledFake(); using var io = new KeyFake(inner, true); using var connection = Open(io, true);
        using (var cts = new CancellationTokenSource(50))
        {
            // The default (real) delay before the FIRST report observes the token.
            var first = () => RogPerKeyLighting.WriteAsync(connection.ExchangeAsync, Keys(85), cts.Token, TimeSpan.FromMinutes(1));
            await first.Should().ThrowAsync<OperationCanceledException>();
        }
        io.KeyReports.Should().BeEmpty();

        using var mid = new CancellationTokenSource(); var calls = 0;
        var third = () => RogPerKeyLighting.WriteAsync(connection.ExchangeAsync, Keys(85), mid.Token, Eight, (_, token) =>
        {
            if (++calls < 3) return Task.CompletedTask;
            var wait = Task.Delay(Timeout.InfiniteTimeSpan, token); mid.Cancel(); return wait;
        });
        await third.Should().ThrowAsync<OperationCanceledException>();
        io.KeyReports.Should().HaveCount(2, "the report after a cancelled pause is never written");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_the_RF_actor_requests_8ms_before_its_first_and_every_key_report(bool rf)
    {
        using var inner = new RogWiredOledTests.OledFake(); using var io = new KeyFake(inner, rf); using var connection = Open(io, rf);
        var pauses = new List<(TimeSpan Delay, int SentBefore, (byte, byte) Previous)>();
        var session = new RogWiredOledSession(connection)
        {
            KeyReportDelay = (d, _) => { pauses.Add((d, io.KeyReports.Count, io.Operations[^1])); return Task.CompletedTask; },
        };
        await session.SnapshotAsync(rf ? RfHeader : "001200000024000100", default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        var keys = Keys(85);

        await bridge.WriteKeysAsync(keys, rf ? RogPerKeyLighting.WirelessTransport : RogPerKeyLighting.WiredTransport, default);

        if (rf)
        {
            pauses.Select(p => (p.Delay, p.SentBefore)).Should().Equal(Enumerable.Range(0, 7).Select(i => (Eight, i)));
            pauses[0].Previous.Should().Be(((byte)0x27, (byte)0), "the first pause follows the preceding software-control readback");
            pauses.Skip(1).Select(p => p.Previous).Should().Equal(Enumerable.Repeat(((byte)0xC0, (byte)0x81), 6));
        }
        else pauses.Should().BeEmpty("the wired route stays unpaced");
        Hex(io.KeyReports).Should().Equal(Hex(rf ? RogProtocol.KeyboardFrame(2, keys) : RogPerKeyLighting.BoundaryFrame(keys)));
        io.Decoded.Should().Equal(keys);
        keys.Select(k => k.Color).Distinct().Should().HaveCount(85);

        stop.Cancel(); await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>(); bridge.Detach(owner);
    }

    [Fact]
    public async Task Cancelling_the_RF_actor_during_its_first_pause_sends_no_key_report()
    {
        using var inner = new RogWiredOledTests.OledFake(); using var io = new KeyFake(inner, true); using var connection = Open(io, true);
        using var stop = new CancellationTokenSource();
        var session = new RogWiredOledSession(connection)
        {
            KeyReportDelay = (_, token) => { var wait = Task.Delay(Timeout.InfiniteTimeSpan, token); stop.Cancel(); return wait; },
        };
        await session.SnapshotAsync(RfHeader, default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);

        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(85), RogPerKeyLighting.WirelessTransport, default))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
        io.KeyReports.Should().BeEmpty();
        session.LightingTouched.Should().BeTrue("the owner's original-lighting restore stays required");
        bridge.Detach(owner);
    }

    /// <summary>
    /// Answers C0 81 with the stock C0/82 shape (refusals are covered by the existing per-key tests) and delegates every other
    /// command to the existing OLED fake. With
    /// <c>rf</c> it carries RID2/64 reports and reports the receiver and keyboard firmware as the existing wireless fake does.
    /// </summary>
    private sealed class KeyFake(RogWiredOledTests.OledFake inner, bool rf) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> KeyReports { get; } = [];
        public List<(byte Command, byte Key)> Operations { get; } = [];
        public IEnumerable<RogKeyColor> Decoded => KeyReports.SelectMany(w => Enumerable.Range(0, w[3])
            .Select(k => new RogKeyColor(w[5 + k * 4], new(w[6 + k * 4], w[7 + k * 4], w[8 + k * 4]))));

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var p = report.ToArray();
            if (p.Length != (rf ? 64 : 65) || p[0] != (rf ? 2 : 0)) throw new InvalidDataException("Wrong request envelope.");
            var wired = new byte[65]; p.AsSpan(1).CopyTo(wired.AsSpan(1));
            Operations.Add((wired[1], wired[2]));
            if (wired[1] == 0xC0 && wired[2] == 0x81)
            {
                KeyReports.Add(p);
                var echo = new byte[65]; wired.AsSpan(0, 5).CopyTo(echo);
                echo[2] = 0x82; echo.AsSpan(5, 23).Fill(0x5A);
                _replies.Writer.TryWrite(echo);
                return;
            }
            await inner.WriteAsync(wired, ct);
            if (wired[1] == 0x67 && wired[2] == 2) return;
            var buffer = new byte[65];
            var length = await inner.ReadAsync(buffer, ct);
            _replies.Writer.TryWrite(buffer[..length]);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
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

        public void Dispose() => _replies.Writer.TryComplete();
    }
}
