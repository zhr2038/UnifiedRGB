using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;
using System.Reflection;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task ObservationRecovery_armed_native_input_retires_without_poisoned_wire_IO_before_the_fresh_owner()
        => await RunNativeRecoveryProofAsync(false);

    [Fact]
    public async Task ObservationRecovery_armed_native_readonly_CAP_timeout_retires_without_latching_a_mutation_fault()
        => await RunNativeRecoveryProofAsync(true);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_wireless_unexpected_standalone_reply_joins_native_input_before_fresh_recovery(bool capFault)
        => await RunNativeRecoveryProofAsync(capFault, true);

    [Fact]
    public async Task ObservationRecovery_only_native_Step_CAP_gets_the_typed_readonly_role()
    {
        await using var rig = new Rig(2); rig.Source.RequestEnabled(true); await rig.Actor.StepAsync(Pixels, default);
        rig.Io.ExchangeFailure = p => p[1] == 0x12 && p[2] == 0x7D
            ? new RogOledUnexpectedReplyException("Injected complete unmatched reply", new InvalidDataException()) : null;
        var error = await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<RogReadOnlyObservationUnexpectedReplyException>();
        error.Which.Operation.Should().Be(RogReadOnlyOperation.NativeStepCapability);
        error.Which.DeviceKey.Should().Be(rig.Io.DeviceKey); error.Which.ProfileToken.Should().BeNull();
        rig.Actor.RecoveryBlocked.Should().BeFalse();
        var writes = rig.Io.Writes.Count;
        await rig.Actor.RetireAfterRecoverableFaultAsync(default);
        rig.Io.Writes.Should().HaveCount(writes); rig.Input.Pending.Should().Be(0); rig.Input.LiveHandles.Should().Be(0);
    }

    [Theory] [InlineData("arm")] [InlineData("begin")] [InlineData("chunk")] [InlineData("stop-capability")]
    public async Task ObservationRecovery_unexpected_native_mutation_or_stop_verification_is_terminal(string role)
    {
        await using var rig = new Rig(2); rig.Source.RequestEnabled(true);
        if (role == "stop-capability") await rig.Actor.StepAsync(Pixels, default);
        rig.Io.ExchangeFailure = p => (role switch
        {
            "arm" => p[1] == 0x71,
            "begin" => p[1] == 0x67 && p[2] == 0,
            "chunk" => p[1] == 0x67 && p[2] == 1,
            _ => p[1] == 0x12 && p[2] == 0x7D,
        }) ? new RogOledUnexpectedReplyException("Injected complete unmatched reply", new InvalidDataException()) : null;
        Func<Task> action = role == "stop-capability" ? () => rig.Actor.StopAsync(default) : () => rig.Actor.StepAsync(Pixels, default);
        await action.Should().ThrowAsync<RogOledUnexpectedReplyException>();
        rig.Actor.RecoveryBlocked.Should().BeTrue();
        var writes = rig.Io.Writes.Count;
        await rig.Actor.StopAsync(default); await rig.Actor.StopAsync(default);
        rig.Io.Writes.Should().HaveCount(writes); rig.Input.Pending.Should().Be(0);
    }

    [Theory] [InlineData("arm-cookie")] [InlineData("arm-epoch")]
    public async Task ObservationRecovery_matched_CAP_semantic_mutation_failure_is_not_reclassified(string fault)
    {
        await using var rig = new Rig(2); rig.Source.RequestEnabled(true); rig.Io.SemanticFault = fault;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        rig.Actor.RecoveryBlocked.Should().BeTrue(); rig.Io.Arms.Should().Be(1);
        var writes = rig.Io.Writes.Count; await rig.Actor.StopAsync(default);
        rig.Io.Writes.Should().HaveCount(writes); rig.Input.Pending.Should().Be(0);
    }

    private static async Task RunNativeRecoveryProofAsync(bool capFault, bool unexpected = false)
    {
        var transport = unexpected ? (byte)2 : (byte)1;
        var time = new SourceClock(); using var input = new Input(time, transport);
        using var control = new Connection(time, transport) { Input = input };
        var factory = new NativeObservationFactory(control, input) { CapFault = capFault, UnexpectedReply = unexpected };
        ulong cookie = Cookie;
        var source = new RogNativeMenuSource(Policy(transport), input, input, time, () => cookie++); source.RequestEnabled(true);
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var data = Path.Combine(Path.GetTempPath(), "native-observation-recovery-" + Guid.NewGuid().ToString("N"));
        using var service = new KeyboardDisplayService(new UnifiedRgbOptions { DataDirectory = data,
            KeyboardDisplay = unexpected
                ? new() { WirelessControlVerified = true, WirelessReceiverKey = control.DeviceKey, WirelessFirmwareHeader = control.FirmwareHeader, DeviceShowsNativeEntryLogo = true }
                : new() { ControlVerified = true, DeviceKey = control.DeviceKey, DeviceShowsNativeEntryLogo = true } },
            DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>(),
            DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>(), factory, new MutationClearGuard(),
            configurationGate: gate, nativeMenu: source, nativePresentation: new DeclaredPresentation(KeyboardNativePresentationSupport.Available), wirelessFactory: factory);
        await service.StartAsync(default);
        try
        {
            await Until(() => factory.Channels.Count == 2 && control.Arms == 2 && source.IsPresent);
            factory.OpenWhileInputLive.Should().BeFalse(); factory.Channels[0].AfterFaultWrites.Should().Be(0);
            factory.Channels[0].Disposed.Should().BeTrue(); input.Opens.Should().Be(2); input.LiveHandles.Should().Be(1);
            control.ImageBegins.Should().Be(0); control.Disarms.Should().Be(0, "the failed native owner is retired without any wire command");
        }
        finally { await service.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private sealed class NativeObservationFactory(Connection control, Input input) : IRogWiredOledFactory, IRogWirelessOledFactory
    {
        public List<NativeObservationChannel> Channels { get; } = [];
        public bool OpenWhileInputLive { get; private set; }
        public bool CapFault { get; init; }
        public bool UnexpectedReply { get; init; }
        public RogWiredOledConnection? TryOpen(string key)
            => new(CreateChannel(), () => true, () => key, () => control.FirmwareHeader) { NativeControlBinding = control.NativeControlBinding };
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
            => new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), new NativeRfObservationChannel(CreateChannel()),
                firmwareHeader, () => true) { NativeControlBinding = control.NativeControlBinding };
        private NativeObservationChannel CreateChannel()
        {
            OpenWhileInputLive |= input.LiveHandles != 0;
            var channel = new NativeObservationChannel(new LegacyPageChannel(control), Channels.Count == 0, CapFault,
                () => control.Arms > 0) { UnexpectedReply = UnexpectedReply }; Channels.Add(channel);
            return channel;
        }
    }
    private sealed class NativeObservationChannel(IHidReportChannel inner, bool drop, bool capFault, Func<bool> armed) : IHidReportChannel
    {
        private (byte, byte) _last;
        private bool _stall, _fault;
        private byte[]? _unexpected;
        public bool UnexpectedReply { get; init; }
        public bool Disposed { get; private set; }
        public int AfterFaultWrites { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            if (_fault) AfterFaultWrites++;
            var p = report.Span; var idle = p[1] == 0x23 && p[2] == 3 && _last == ((byte)0x12, (byte)0);
            _last = (p[1], p[2]);
            if (drop && (capFault ? p[1] == 0x12 && p[2] == 0x7D && armed() : idle))
            {
                if (UnexpectedReply) { _unexpected = new byte[65]; _unexpected[1] = 0x25; _unexpected[3] = 1; }
                else _stall = true;
                return ValueTask.CompletedTask;
            }
            return inner.WriteAsync(report, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_unexpected is { } reply) { _unexpected = null; _fault = true; reply.CopyTo(buffer); return 65; }
            if (_stall) try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { _fault = true; throw; }
            return await inner.ReadAsync(buffer, ct);
        }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    private sealed class NativeRfObservationChannel(IHidReportChannel inner) : IHidReportChannel
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        { var p = new byte[65]; report.Span[1..].CopyTo(p.AsSpan(1)); return inner.WriteAsync(p, ct); }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var p = new byte[65]; var length = await inner.ReadAsync(p, ct); if (length == 0) return 0;
            if (p[1] == 0x12 && p[2] == 0) { p[5] = 8; p[7] = 7; p[12] = 0x25; p[14] = 1; }
            p.AsMemory(0, 64).CopyTo(buffer); buffer.Span[0] = 2; return 64;
        }
        public void Dispose() => inner.Dispose();
    }
}
