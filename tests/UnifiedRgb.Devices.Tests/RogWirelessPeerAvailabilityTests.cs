using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogWirelessPeerAvailabilityTests
{
    [Fact]
    public async Task Established_RF_standalone_read_without_keyboard_is_typed_unavailable_and_sends_no_settings()
    {
        using var channel = new PeerChannel(); using var connection = Open(channel);
        var session = new RogWiredOledSession(connection); await session.SnapshotAsync(connection.FirmwareHeader, default);
        channel.PeerPresent = false; var count = channel.Writes.Count;
        var result = await ((Func<Task>)(() => session.RunAsync(() => new byte[6656], () => { }, default, display: false)))
            .Should().ThrowAsync<RogReadOnlyObservationPeerUnavailableException>();
        result.Which.Operation.Should().Be(RogReadOnlyOperation.IdleScopeObservation);
        result.Which.ProfileToken.Should().Be(6);
        channel.Writes.Skip(count).Should().ContainSingle().Which.Should().Equal(0x12, 0);
    }

    [Fact]
    public async Task Mandatory_restore_scope_without_keyboard_stays_terminal_and_never_becomes_wake_recovery()
    {
        using var channel = new PeerChannel(); using var connection = Open(channel);
        var session = new RogWiredOledSession(connection); await session.SnapshotAsync(connection.FirmwareHeader, default);
        channel.PeerPresent = false; var count = channel.Writes.Count;
        await ((Func<Task>)(() => session.RestoreAsync(50, default))).Should().ThrowAsync<InvalidDataException>();
        channel.Writes.Skip(count).Should().ContainSingle().Which.Should().Equal(0x12, 0);
    }

    [Fact]
    public async Task Standalone_RF_brightness_negative_is_classified_but_setter_readback_negative_is_terminal()
    {
        using var idleChannel = new PeerChannel(); using var idleConnection = Open(idleChannel);
        var idle = new RogWiredOledSession(idleConnection); await idle.SnapshotAsync(idleConnection.FirmwareHeader, default);
        idleChannel.RefuseBrightness = true;
        var read = await ((Func<Task>)(() => idle.RunAsync(() => new byte[6656], () => { }, default, display: false)))
            .Should().ThrowAsync<RogReadOnlyObservationEnvelopeRefusedException>();
        read.Which.Operation.Should().Be(RogReadOnlyOperation.IdleBrightnessObservation);
        idleChannel.Writes.Should().NotContain(p => p[0] == 0x61 || p[0] == 0x68);

        using var setterChannel = new PeerChannel(); using var setterConnection = Open(setterChannel);
        var setter = new RogWiredOledSession(setterConnection); await setter.SnapshotAsync(setterConnection.FirmwareHeader, default);
        setterChannel.RefuseBrightness = true;
        await ((Func<Task>)(() => setter.RestoreAsync(50, default))).Should().ThrowAsync<RogOledEnvelopeRefusedException>();
        setterChannel.Writes.Should().Contain(p => p[0] == 0x68);
    }

    [Fact]
    public void Empty_paired_table_is_waitable_but_an_unrelated_paired_device_is_not_adopted()
    {
        ((Action)(() => RogWirelessOledFactory.RequirePairedKeyboard([]))).Should().Throw<RogKeyboardNotReadyException>();
        ((Action)(() => RogWirelessOledFactory.RequirePairedKeyboard([new(0x1D7C, 3, 5)]))).Should().Throw<InvalidDataException>();
        var target = new RogPairedDevice(0x1C25, 2, 4);
        RogWirelessOledFactory.RequirePairedKeyboard([target]).Should().BeSameAs(target);
    }

    private static RogWirelessOledConnection Open(PeerChannel channel) => new(
        new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), channel, "0800070025000100", () => true);

    private sealed class PeerChannel : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public bool PeerPresent { get; set; } = true;
        public bool RefuseBrightness { get; set; }
        public List<byte[]> Writes { get; } = [];
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            report.Length.Should().Be(64); report.Span[0].Should().Be(2);
            var reply = new byte[64]; report.Span[..5].CopyTo(reply);
            Writes.Add([report.Span[1], report.Span[2]]);
            switch (report.Span[1], report.Span[2])
            {
                case (0x12, 0):
                    reply[5] = 8; reply[7] = 7; reply[9] = 6; reply[11] = 6;
                    if (PeerPresent) { reply[12] = 0x25; reply[14] = 1; }
                    break;
                case (0x21, 0): reply[5] = 3; break;
                case (0x23, 2): reply[5] = 1; break;
                case (0x23, 3):
                    if (RefuseBrightness) { Array.Clear(reply); reply[0] = 2; reply[1] = 0xFF; reply[2] = 0xAA; }
                    else reply[5] = 50;
                    break;
                case (0x61, 0): break;
                case (0x68, 0): report.Span.CopyTo(reply); break;
                default: throw new InvalidOperationException("Only read-only identity/selector/brightness were expected.");
            }
            _replies.Writer.TryWrite(reply).Should().BeTrue(); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
