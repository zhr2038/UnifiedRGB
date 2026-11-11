using System.Threading.Channels;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>Production USB/RF framing with fake HID only; no device or physical acceptance claims.</summary>
public sealed class RogNativeRecoveryAdoptionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Readonly_adoption_preserves_current_takeover_and_original_cleanup_before_any_new_key_frame(bool wireless, bool aggregate)
    {
        using var io = new RecoveryChannel(wireless);
        io.Inner.Brightness = 70; io.Inner.SoftwareControl = 1;
        using var connection = Connect(io, wireless);
        var session = new RogWiredOledSession(connection);
        await SnapshotAsync(session, connection);
        var payloads = session.OriginalLighting!.Payloads.ToArray();
        var expectedPayload = payloads[0];
        var original = session.OriginalLighting with { SoftwareControl = 0, Payloads = payloads };
        var bank = session.OriginalStaticBank! with { SoftwareControl = 0 };
        var writes = io.Requests.Count;

        session.TryAdoptNativeRecoveryBaseline(30, 70, original, bank, true).Should().BeTrue();

        io.Requests.Should().HaveCount(writes, "adoption never opens another transaction");
        io.Inner.SoftwareControl.Should().Be(1);
        session.OriginalBrightness.Should().Be(30); session.CurrentNativeBrightness.Should().Be(70);
        session.OriginalLighting!.SoftwareControl.Should().Be(0);
        session.OriginalStaticBank!.SoftwareControl.Should().Be(0);
        session.Touched.Should().BeTrue(); session.LightingTouched.Should().BeTrue();
        session.MutationAdmitted.Should().BeFalse();
        session.OriginalLighting.Should().NotBeSameAs(original);
        session.OriginalStaticBank.Should().NotBeSameAs(bank);
        payloads[0] = new string('0', payloads[0].Length);
        session.OriginalLighting.Payloads[0].Should().Be(expectedPayload, "the caller cannot alter a carried cleanup baseline");

        if (aggregate)
        {
            var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach();
            using var stop = new CancellationTokenSource();
            var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
            try
            {
                await bridge.WriteAsync(new(1, 2, 3), default);
                io.Inner.SoftwareControl.Should().Be(0, "aggregate output restores the original software flag even before a new key frame");
            }
            finally
            {
                stop.Cancel();
                await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
                bridge.Detach(owner);
            }
        }
        else
        {
            await session.RestoreLightingAsync(session.OriginalLighting!, session.OriginalStaticBank!, default);
            await session.RestoreAsync(session.OriginalBrightness, default);
            io.Inner.SoftwareControl.Should().Be(0, "release carries the prior owner's obligation without any new frame");
            io.Inner.Brightness.Should().Be(30);
        }
        var mutations = io.Requests.Skip(writes).Select(p => p[1]).ToList();
        mutations.IndexOf(0x74).Should().BeLessThan(mutations.IndexOf(0x51));
        io.Requests.Should().NotContain(p => p[1] == 0xC0, "this verification never relies on a new key frame setting cleanup flags");
    }

    [Theory]
    [InlineData(false, "brightness")] [InlineData(true, "brightness")]
    [InlineData(false, "active-bank")] [InlineData(true, "active-bank")]
    [InlineData(false, "static-bank")] [InlineData(true, "static-bank")]
    [InlineData(false, "unknown-flag")] [InlineData(true, "unknown-flag")]
    [InlineData(false, "no-consumer")] [InlineData(true, "no-consumer")]
    [InlineData(false, "reverse-flag")] [InlineData(true, "reverse-flag")]
    public async Task Legitimate_state_mismatch_does_not_adopt_or_write(bool wireless, string mismatch)
    {
        using var io = new RecoveryChannel(wireless);
        io.Inner.Brightness = 70; io.Inner.SoftwareControl = mismatch == "reverse-flag" ? (byte)0 : (byte)1;
        using var connection = Connect(io, wireless); var session = new RogWiredOledSession(connection);
        await SnapshotAsync(session, connection);
        var current = session.OriginalLighting;
        var currentBank = session.OriginalStaticBank;
        var recorded = current! with { SoftwareControl = mismatch == "reverse-flag" ? (byte)1 : (byte)0 };
        var bank = currentBank!;
        if (mismatch == "active-bank") recorded = recorded with { Payloads = [new string('0', recorded.Payloads[0].Length)] };
        if (mismatch == "static-bank") bank = bank with { Payloads = [new string('0', bank.Payloads[0].Length)] };
        if (mismatch == "unknown-flag") recorded = recorded with { SoftwareControl = null };
        var writes = io.Requests.Count;

        session.TryAdoptNativeRecoveryBaseline(30, mismatch == "brightness" ? (byte)69 : (byte)70,
            recorded, bank, mismatch != "no-consumer").Should().BeFalse();

        io.Requests.Should().HaveCount(writes);
        session.OriginalBrightness.Should().Be(70); session.CurrentNativeBrightness.Should().Be(70);
        session.OriginalLighting.Should().BeSameAs(current); session.OriginalStaticBank.Should().BeSameAs(currentBank);
        session.Touched.Should().BeFalse(); session.LightingTouched.Should().BeFalse(); session.MutationAdmitted.Should().BeFalse();
    }

    [Fact]
    public async Task Adoption_requires_complete_readonly_snapshots_and_cannot_replace_an_adopted_or_mutated_baseline()
    {
        using var io = new RecoveryChannel(false); io.Inner.Brightness = 70;
        using var connection = Connect(io, false); var session = new RogWiredOledSession(connection);
        ((Action)(() => session.TryAdoptNativeRecoveryBaseline(30, 70, null, null, false)))
            .Should().Throw<InvalidOperationException>();
        await session.SnapshotAsync(connection.FirmwareHeader, default);
        var recorded = io.Inner.Native with { SoftwareControl = 0 };
        session.TryAdoptNativeRecoveryBaseline(30, 70, recorded, recorded, false).Should().BeFalse();
        ((Action)(() => session.TryAdoptNativeRecoveryBaseline(101, 70, null, null, false)))
            .Should().Throw<ArgumentOutOfRangeException>();
        session.TryAdoptNativeRecoveryBaseline(30, 70, null, null, false).Should().BeTrue();
        ((Action)(() => session.TryAdoptNativeRecoveryBaseline(40, 70, null, null, false)))
            .Should().Throw<InvalidOperationException>();

        using var mutatedIo = new RecoveryChannel(false); mutatedIo.Inner.Brightness = 70;
        using var mutatedConnection = Connect(mutatedIo, false); var mutated = new RogWiredOledSession(mutatedConnection);
        await SnapshotAsync(mutated, mutatedConnection);
        await mutated.StartOutputAsync(70, default);
        var writes = mutatedIo.Requests.Count;
        ((Action)(() => mutated.TryAdoptNativeRecoveryBaseline(30, 70, mutated.OriginalLighting, mutated.OriginalStaticBank, false)))
            .Should().Throw<InvalidOperationException>();
        mutatedIo.Requests.Should().HaveCount(writes);
    }

    [Fact]
    public async Task A_partial_lighting_snapshot_does_not_qualify_for_baseline_adoption()
    {
        using var io = new RecoveryChannel(false); io.Inner.Brightness = 70;
        using var connection = Connect(io, false); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(connection.FirmwareHeader, default);
        io.Refuse = p => p[1] == 0x12 && io.Requests.Count(request => request[1] == 0x12) == 3;
        await ((Func<Task>)(() => session.SnapshotLightingAsync(default))).Should().ThrowAsync<RogOledEnvelopeRefusedException>();
        session.OriginalLighting.Should().NotBeNull("the final scope check failed after the bank data arrived");
        var writes = io.Requests.Count;
        session.TryAdoptNativeRecoveryBaseline(30, 70, session.OriginalLighting, session.OriginalStaticBank, false).Should().BeFalse();
        session.Touched.Should().BeFalse(); session.LightingTouched.Should().BeFalse(); session.MutationAdmitted.Should().BeFalse();
        io.Requests.Should().HaveCount(writes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Usb_refusal_is_typed_only_for_the_exact_native_envelope(int malformed)
    {
        using var io = new RecoveryChannel(false) { Refuse = _ => true, Malformed = malformed };
        using var connection = Connect(io, false);
        var request = RogWiredOledProtocol.Query(0x12, 0);
        var action = () => connection.ExchangeAsync(request, false, default);
        if (malformed == 0)
        {
            var refusal = (await action.Should().ThrowAsync<RogOledEnvelopeRefusedException>()).Which;
            refusal.UnsentCommand.Should().BeNull("USB has no proven pre-drain unsent-command evidence");
        }
        else await action.Should().ThrowAsync<InvalidDataException>();
        await action.Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Usb_exact_standalone_scope_or_brightness_refusal_is_observation_only(bool brightness)
    {
        using var io = new RecoveryChannel(false); io.Inner.Brightness = 70;
        using var connection = Connect(io, false); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(connection.FirmwareHeader, default);
        io.Refuse = p => brightness ? p[1] == 0x23 && p[2] == 3 : p[1] == 0x12;

        var action = () => session.RunAsync(() => new byte[6656], () => { }, default, display: false);
        var failure = (await action.Should().ThrowAsync<RogReadOnlyObservationEnvelopeRefusedException>()).Which;
        failure.ConnectionKind.Should().Be("wired"); failure.ProfileToken.Should().Be(6);
        failure.Operation.Should().Be(brightness ? RogReadOnlyOperation.IdleBrightnessObservation : RogReadOnlyOperation.IdleScopeObservation);
        session.MutationAdmitted.Should().BeFalse(); io.Inner.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task Usb_mandatory_brightness_setter_readback_refusal_remains_a_mutation_failure()
    {
        using var io = new RecoveryChannel(false); io.Inner.Brightness = 70;
        using var connection = Connect(io, false); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(connection.FirmwareHeader, default);
        io.Refuse = p => p[1] == 0x23 && p[2] == 3;
        var action = () => session.RestoreAsync(80, default);
        var failure = (await action.Should().ThrowAsync<RogOledEnvelopeRefusedException>()).Which;
        failure.Should().NotBeOfType<RogReadOnlyObservationEnvelopeRefusedException>();
        session.MutationAdmitted.Should().BeTrue(); io.Inner.Brightness.Should().Be(80);
        io.Requests.Count(p => p[1] == 0x68).Should().Be(1);
        failure.UnsentCommand.Should().BeNull();
    }

    [Fact]
    public async Task Usb_lease_exact_refusal_is_preserved_but_never_tagged_as_a_standalone_observation()
    {
        using var io = new RecoveryChannel(false) { Refuse = _ => true };
        using var connection = Connect(io, false);
        var failure = (await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => { }, default)))
            .Should().ThrowAsync<RogOledEnvelopeRefusedException>()).Which;
        failure.UnsentCommand.Should().BeNull();
        io.Requests.Should().HaveCount(1);
        var legacy = new RogReadOnlyObservationEnvelopeRefusedException(RogReadOnlyOperation.IdleScopeObservation, "key", 6, failure);
        legacy.ConnectionKind.Should().Be("wireless", "the existing public constructor retains its RF meaning");
    }

    private static async Task SnapshotAsync(RogWiredOledSession session, IRogOledConnection connection)
    {
        await session.SnapshotAsync(connection.FirmwareHeader, default);
        await session.SnapshotLightingAsync(default);
    }

    private static IRogOledConnection Connect(RecoveryChannel channel, bool wireless) => wireless
        ? new RogWirelessOledConnection(new RogReceiver(new string('A', 64), 0x1ACE,
            new Dictionary<byte, HidCollectionInfo>()), channel, "0800070024000100", () => true)
        : new RogWiredOledConnection(channel, () => true);

    private sealed class RecoveryChannel(bool wireless) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public RogWiredOledTests.OledFake Inner { get; } = new();
        public List<byte[]> Requests { get; } = [];
        public Func<byte[], bool>? Refuse { get; set; }
        public int Malformed { get; init; }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var raw = report.ToArray(); Requests.Add(raw);
            var wired = new byte[65]; raw.AsSpan(1).CopyTo(wired.AsSpan(1));
            byte[] reply;
            if (Refuse?.Invoke(wired) == true)
            {
                reply = new byte[65]; reply[1] = 0xFF; reply[2] = 0xAA;
                if (Malformed == 1) reply[64] = 1;
                if (Malformed == 2) reply[0] = 2;
                if (Malformed == 3) reply = reply[..64];
            }
            else
            {
                await Inner.WriteAsync(wired, ct);
                if (wired[1] == 0x67 && wired[2] == 2) return;
                reply = new byte[65];
                (await Inner.ReadAsync(reply, ct)).Should().Be(65);
                if (wireless && wired[1] == 0x12 && wired[2] == 0)
                { reply[5] = 8; reply[7] = 7; reply[12] = 0x24; reply[14] = 1; }
            }
            if (wireless) { reply = reply[..64]; reply[0] = 2; }
            _replies.Writer.TryWrite(reply).Should().BeTrue();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length;
        }

        public void Dispose() { _replies.Writer.TryComplete(); Inner.Dispose(); }
    }
}
