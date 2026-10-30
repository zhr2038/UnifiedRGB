using System.Reflection;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData("arm-cookie")]
    [InlineData("arm-epoch")]
    [InlineData("arm-grammar")]
    public async Task Semantic_ARM_failure_reaches_actual_owner_as_unknown_and_retains_journal_without_cleanup(string fault)
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNativeMutation-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        var time = new SourceClock(); using var input = new Input(time, 1);
        using var control = new Connection(time, 1) { Input = input, SemanticFault = fault };
        byte[]? before = null;
        var factory = new SemanticArmFactory(control, () => before = File.ReadAllBytes(journal));
        var source = new RogNativeMenuSource(Policy(), input, input, time, () => Cookie);
        source.RequestEnabled(true);
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var owner = new KeyboardDisplayService(new()
        {
            DataDirectory = data,
            KeyboardDisplay = new() { Enabled = true, ControlVerified = true, DeviceKey = control.DeviceKey, FirmwareHeader = control.FirmwareHeader },
        }, rgb, screens, factory, new MutationClearGuard(), nativeMenu: source);
        await owner.StartAsync(default);
        try
        {
            await factory.MutationWritten.Task.WaitAsync(Patience);
            await Until(() => owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
            control.Arms.Should().Be(1, "the matched-envelope semantic failure follows an actual mutating ARM write");
            input.Opens.Should().Be(1); input.LiveHandles.Should().Be(0);
            before.Should().NotBeNull();
            File.Exists(journal).Should().BeTrue("an unvalidated mutating reply cannot authorize cleanup or deleting the original recovery record");
            File.ReadAllBytes(journal).Should().Equal(before!);
            factory.Channel!.WritesAfterArm.Should().Be(0, "no later CAP, DISARM, legacy restoration or retry follows the unvalidated ARM");
            await Task.Delay(1400); factory.Opens.Should().Be(1);
            ((KeyboardDisplayServiceTests.ReadProxy)rgb).CaseMutations.Should().Be(0);
            ((KeyboardDisplayServiceTests.ReadProxy)screens).ScreenMutations.Should().Be(0);
        }
        finally
        {
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Theory]
    [InlineData("disarm-epoch")]
    [InlineData("disarm-status")]
    [InlineData("disarm-grammar")]
    public async Task Semantic_DISARM_failure_latches_unknown_before_Stop_or_Dispose_can_do_further_control_IO(string fault)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        await rig.Actor.StepAsync(Pixels, default); rig.Io.SemanticFault = fault;
        await ((Func<Task>)(() => rig.Actor.StopAsync(default))).Should().ThrowAsync<InvalidDataException>();
        rig.Io.Disarms.Should().Be(1);
        rig.Actor.RecoveryBlocked.Should().BeTrue("semantic validation is part of the same mutating DISARM operation");
        rig.Source.IsPresent.Should().BeFalse(); rig.Input.Pending.Should().Be(0);
        var written = rig.Io.Writes.Count;
        await rig.Actor.StopAsync(default); await rig.Actor.DisposeAsync();
        rig.Io.Writes.Should().HaveCount(written);
    }

    [Fact]
    public async Task Unsupported_read_only_CAP_remains_unavailable_without_poisoning_native_recovery()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); rig.Io.BadCapabilityGrammar = true;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Actor.RecoveryBlocked.Should().BeFalse(); rig.Source.IsPresent.Should().BeFalse();
        rig.Io.Writes.Should().Equal("12/7D"); rig.Input.Opens.Should().Be(0);
    }

    private sealed class SemanticArmFactory(Connection control, Action beforeArm) : IRogWiredOledFactory
    {
        public int Opens { get; private set; }
        public SemanticArmChannel? Channel { get; private set; }
        public TaskCompletionSource MutationWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("Exact firmware overload required.");
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
        {
            deviceKey.Should().Be(control.DeviceKey); firmwareHeader.Should().Be(RogRuntimeCompatibility.NativeFamilyAnchor(control.ConnectionKind)); Opens++;
            Channel = new(control, () => { beforeArm(); MutationWritten.TrySetResult(); });
            return new(Channel, () => true, () => deviceKey, () => firmwareHeader) { NativeControlBinding = control.NativeControlBinding };
        }
    }

    private sealed class SemanticArmChannel(Connection control, Action beforeArm) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private bool _armWritten;
        public int WritesAfterArm { get; private set; }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
        {
            if (_armWritten) WritesAfterArm++;
            var request = packet.ToArray();
            if (request[1] == 0x71 && request[2] == 0x7D && request[10] == 1) { beforeArm(); _armWritten = true; }
            var reply = await control.ExchangeAsync(request, false, ct);
            if (request[1] == 0x12 && request[2] == 0) { reply[5] = 0x25; reply[7] = 1; }
            _replies.Writer.TryWrite(reply).Should().BeTrue();
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
        public void Dispose() => _replies.Writer.TryComplete();
    }

    private sealed class MutationClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException();
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException();
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException();
    }
}
