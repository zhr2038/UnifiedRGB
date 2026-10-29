using System.Collections.Concurrent;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Frozen RF evidence surface (v3): the pairing provenance is immutable; LastObserved/Observations advance only after a validated
/// keyboard reply to this same owner's exchange, on the owner's TimeProvider; invalidation is terminal. Synthetic RID2/64 fixtures
/// and a manual clock only; spans beyond the 180-second pairing window are simulated, never slept.
/// </summary>
public sealed partial class RuntimeCapabilityCompatibilityTests
{
    private static readonly RogPairedDevice Azoth = new(0x1C25, 2, 4);
    private static readonly string RfFamily = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;

    [Fact]
    public void Rf_evidence_starts_at_the_pairing_provenance_and_time_alone_never_advances_it()
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        var io = new RfKeyboard(); using var connection = Paired(io, time);
        connection.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, paired, paired, 0, null));
        time.Advance(TimeSpan.FromSeconds(181));
        var evidence = connection.PairingEvidence!;
        evidence.Should().Be(new RogPairingEvidence(Azoth, paired, paired, 0, null), "no timer, query or timestamp reset exists");
        time.GetElapsedTime(evidence.LastObservedTimestamp).Should().BeGreaterThan(TimeSpan.FromMinutes(3));
        io.Writes.Should().Be(0, "reading evidence performs no I/O");
    }

    [Theory]
    [InlineData("capability")] [InlineData("identity")] [InlineData("arm")]
    public async Task Rf_evidence_advances_only_after_a_validated_reply_to_this_owners_exchange(string command)
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        using var connection = Paired(new RfKeyboard(), time);
        var initial = connection.PairingEvidence!;
        time.Advance(TimeSpan.FromSeconds(170)); var first = time.GetTimestamp();
        await Exchange(connection, command);
        connection.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, paired, first, 1, null));
        initial.Observations.Should().Be(0, "a returned snapshot is immutable");

        time.Advance(TimeSpan.FromSeconds(30));
        var evidence = connection.PairingEvidence!;
        time.GetElapsedTime(evidence.PairingTimestamp).Should().BeGreaterThan(TimeSpan.FromMinutes(3), "the original provenance never moves");
        time.GetElapsedTime(evidence.LastObservedTimestamp).Should().Be(TimeSpan.FromSeconds(30));

        time.Advance(TimeSpan.FromSeconds(170)); var second = time.GetTimestamp();
        await Exchange(connection, command);
        connection.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, paired, second, 2, null));
        time.GetElapsedTime(paired).Should().Be(TimeSpan.FromSeconds(370));
    }

    [Fact]
    public async Task Rf_evidence_never_advances_on_lighting_acknowledgement_echo_or_renewal_traffic()
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        var io = new RfKeyboard(); using var connection = Paired(io, time);
        time.Advance(TimeSpan.FromSeconds(100));
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x21, 0), false, default);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 2), false, default);
        await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default);
        await connection.ExchangeAsync(RogWiredOledProtocol.Preset(3), true, default);
        await connection.ExchangeAsync(RogWiredOledProtocol.Renew(), true, default);
        var brightness = new byte[65]; brightness[1] = 0x68; brightness[5] = 40;
        await connection.ExchangeAsync(brightness, false, default);
        var renewals = 0;
        await connection.LeaseAsync(() => renewals > 0, () => renewals++, default);
        time.Advance(TimeSpan.FromSeconds(100));
        renewals.Should().Be(1); io.Writes.Should().Be(7);
        connection.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, paired, paired, 0, null),
            "OLED/lighting replies, ACKs, echoes and renewals are not RF keyboard evidence");
    }

    [Theory]
    [InlineData("capability-wired-transport")] [InlineData("capability-protocol")] [InlineData("capability-geometry")]
    [InlineData("capability-flag")] [InlineData("arm-lifecycle")] [InlineData("identity-asleep")] [InlineData("identity-keyboard-family")]
    [InlineData("identity-receiver-family")] [InlineData("identity-generation")]
    public async Task Rf_evidence_is_terminally_invalidated_by_a_rejected_keyboard_reply(string fault)
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        var io = new RfKeyboard(); using var connection = Paired(io, time);
        time.Advance(TimeSpan.FromSeconds(100)); var observed = time.GetTimestamp();
        connection.RequireFirmwareIdentity(await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default), RfFamily);
        connection.PairingEvidence!.Observations.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(10));
        switch (fault)
        {
            case "capability-wired-transport": io.Cap = Capability(transport: 1); break;
            case "capability-protocol": io.Cap = Capability(transport: 2); io.Cap[9] = 3; break;
            case "capability-geometry": io.Cap = Capability(transport: 2); io.Cap[36] = 60; break;
            case "capability-flag": io.Cap = Capability(transport: 2, flags: 16); break;
            case "arm-lifecycle": io.Arm = Capability(transport: 2, status: 1, flags: 48, command: 0x71); break;
            case "identity-asleep": io.Identity.AsSpan(12, 4).Clear(); break;
            case "identity-keyboard-family": io.Identity[14] = 2; break;
            case "identity-receiver-family": io.Identity[7] = 8; break;
            default: io.Identity[12] = 0x27; break;
        }
        var command = fault.StartsWith("identity", StringComparison.Ordinal) ? "identity" : fault.StartsWith("arm", StringComparison.Ordinal) ? "arm" : "capability";
        await ((Func<Task>)(() => Exchange(connection, command))).Should().ThrowAsync<InvalidDataException>("the caller rejects it too");

        var invalidated = connection.PairingEvidence!;
        invalidated.Invalidated.Should().NotBeNullOrEmpty();
        invalidated.Should().Be(new RogPairingEvidence(Azoth, paired, observed, 1, invalidated.Invalidated), "the other fields freeze");

        io.Cap = Capability(transport: 2); io.Arm = null; io.Identity = WirelessIdentity(9, 0x26);
        time.Advance(TimeSpan.FromSeconds(100));
        await connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, default);
        connection.PairingEvidence.Should().Be(invalidated, "invalidation is terminal; a later valid reply never revives it");
    }

    [Theory]
    [InlineData("write")] [InlineData("eof")] [InlineData("timeout")] [InlineData("refused")] [InlineData("envelope")]
    public async Task Rf_evidence_is_terminally_invalidated_when_the_owner_connection_faults(string fault)
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        var io = new RfKeyboard(); using var connection = Paired(io, time);
        time.Advance(TimeSpan.FromSeconds(100)); var observed = time.GetTimestamp();
        await Exchange(connection, "capability");
        io.Fault = fault;
        await ((Func<Task>)(() => Exchange(connection, "capability"))).Should().ThrowAsync<Exception>();
        var evidence = connection.PairingEvidence!;
        evidence.Invalidated.Should().StartWith("connection poisoned: ");
        evidence.Should().Be(new RogPairingEvidence(Azoth, paired, observed, 1, evidence.Invalidated));

        io.Fault = null; time.Advance(TimeSpan.FromSeconds(100));
        await ((Func<Task>)(() => Exchange(connection, "capability"))).Should().ThrowAsync<IOException>("no further wire I/O on a poisoned owner");
        connection.PairingEvidence.Should().Be(evidence);
    }

    [Fact]
    public async Task Rf_evidence_ignores_an_exchange_the_caller_cancelled_after_the_reply()
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        using var cancel = new CancellationTokenSource();
        var io = new RfKeyboard { OnWrite = cancel.Cancel }; using var connection = Paired(io, time);
        time.Advance(TimeSpan.FromSeconds(100));
        await ((Func<Task>)(() => connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, cancel.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        connection.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, paired, paired, 0, null), "an undelivered reply is no evidence");
    }

    [Fact]
    public async Task Rf_evidence_is_invalidated_on_dispose_and_a_new_owner_starts_from_its_own_pairing()
    {
        var time = new ManualTimeProvider(); time.Advance(TimeSpan.FromMinutes(1)); var paired = time.GetTimestamp();
        var connection = Paired(new RfKeyboard(), time);
        time.Advance(TimeSpan.FromSeconds(100)); var observed = time.GetTimestamp();
        await Exchange(connection, "capability");
        connection.Dispose(); connection.Dispose();
        connection.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, paired, observed, 1, "disposed"));

        time.Advance(TimeSpan.FromSeconds(200)); var repaired = time.GetTimestamp();
        using var fresh = Paired(new RfKeyboard(), time);
        fresh.PairingEvidence.Should().Be(new RogPairingEvidence(Azoth, repaired, repaired, 0, null), "evidence never crosses owners");
        connection.PairingEvidence!.Invalidated.Should().Be("disposed");
    }

    [Theory]
    [InlineData("wired")] [InlineData("wireless-unbound")] [InlineData("wireless-without-keyboard")]
    public async Task Rf_evidence_is_absent_without_a_paired_rf_keyboard_binding(string route)
    {
        var time = new ManualTimeProvider();
        if (route == "wired")
        {
            using var wired = new RogWiredOledConnection(new NoIo(), () => true, () => Key, () => Wired24);
            ((IRogOledConnection)wired).PairingEvidence.Should().BeNull("wired owners carry no RF evidence");
            return;
        }
        var io = new RfKeyboard();
        using var connection = route == "wireless-unbound" ? Wireless(io, RfFamily)
            : new RogWirelessOledConnection(new RogReceiver(Key, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, RfFamily, () => true)
            { NativeControlBinding = new([], null, time.GetTimestamp()), EvidenceTime = time };
        ((IRogOledConnection)connection).PairingEvidence.Should().BeNull();
        await Exchange(connection, "capability");
        connection.PairingEvidence.Should().BeNull("no keyboard provenance exists to observe");
        connection.Dispose(); connection.PairingEvidence.Should().BeNull();
    }

    [Fact]
    public void Rf_factory_keeps_its_parameterless_constructor_and_requires_a_clock()
    {
        _ = new RogWirelessOledFactory(); _ = new RogWirelessOledFactory(new ManualTimeProvider());
        ((Action)(() => _ = new RogWirelessOledFactory(null!))).Should().Throw<ArgumentNullException>();
    }

    private static RogWirelessOledConnection Paired(RfKeyboard io, ManualTimeProvider time) =>
        new(new RogReceiver(Key, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, RfFamily, () => true)
        { NativeControlBinding = new([], Azoth, time.GetTimestamp()), EvidenceTime = time };

    // The same request/validation pairs the existing owners use: identity (session preflight), CAP (native tick) and ARM.
    private static async Task Exchange(RogWirelessOledConnection connection, string command)
    {
        switch (command)
        {
            case "identity":
                connection.RequireFirmwareIdentity(await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default), RfFamily);
                break;
            case "arm":
                var expected = RogNativeMenuCodec.ParseCapability(Capability(transport: 2), 0x12, 2);
                RogNativeMenuCodec.ParseCapability(await connection.ExchangeAsync(RogNativeMenuCodec.ArmRequest(expected, 0x1122334455667788), false, default), 0x71, 2);
                break;
            default:
                RogNativeMenuCodec.ParseCapability(await connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, default), 0x12, 2);
                break;
        }
    }

    /// <summary>Scripted RID2/64 keyboard behind the receiver; replies only to what was written, in order.</summary>
    private sealed class RfKeyboard : IHidReportChannel
    {
        private readonly ConcurrentQueue<byte[]> _replies = new();
        private readonly SemaphoreSlim _ready = new(0);
        public byte[] Identity { get; set; } = WirelessIdentity(9, 0x26);
        public byte[] Cap { get; set; } = Capability(transport: 2);
        public byte[]? Arm { get; set; }
        public string? Fault { get; set; }
        public Action? OnWrite { get; init; }
        public int Writes { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            Writes++; OnWrite?.Invoke();
            var p = report.ToArray();
            if (p.Length != 64 || p[0] != 2) throw new InvalidOperationException("Not an RID2/64 report.");
            if (Fault == "write") throw new IOException("Synthetic write fault.");
            if (Fault is "timeout" or "eof") return ValueTask.CompletedTask;
            var request = new byte[65]; p.AsSpan(1).CopyTo(request.AsSpan(1));
            var reply = new byte[65]; request.AsSpan(0, 5).CopyTo(reply);
            switch (p[1], p[2])
            {
                case (0x12, 0): reply = (byte[])Identity.Clone(); break;
                case (0x12, 0x7D): reply = (byte[])Cap.Clone(); break;
                case (0x71, 0x7D): reply = Arm ?? Capability(transport: 2, status: 1, flags: 49, command: 0x71); break;
                case (0x21, 0): reply[5] = 3; break;
                case (0x23, 2): reply[5] = 1; break;
                case (0x23, 3): reply[5] = 25; break;
                case (0x61, 0) or (0x67, 2): break;
                case (0x68, 0): reply = request; break;
                default: throw new InvalidOperationException("Unexpected synthetic packet.");
            }
            var wireless = new byte[64]; reply.AsSpan(1, 63).CopyTo(wireless.AsSpan(1)); wireless[0] = Fault == "envelope" ? (byte)3 : (byte)2;
            if (Fault == "refused") { wireless[1] = 0xFF; wireless[2] = 0xAA; }
            _replies.Enqueue(wireless); _ready.Release();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (Fault == "eof") return 0;
            await _ready.WaitAsync(ct).ConfigureAwait(false);
            if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException("No unsolicited synthetic reply.");
            reply.CopyTo(buffer); return reply.Length;
        }

        public void Dispose() => _ready.Dispose();
    }
}
