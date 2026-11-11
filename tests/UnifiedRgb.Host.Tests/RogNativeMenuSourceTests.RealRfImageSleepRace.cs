using System.Reflection;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_RF_cached_positive_basic_requires_fresh_native_handler_before_any_recovery_setting(bool malformed)
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-cached-basic-native-probe-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); ulong cookie = Cookie; var pixels = Pixels();
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: () => Volatile.Read(ref pixels),
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        var budget = typeof(KeyboardDisplayService).GetField("_observationReplacements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count >= 1, time);
            var before = File.ReadAllBytes(journal); var original = routes.Ports[^1];
            routes.ArmRadioTransientPhase("image-begin");
            Volatile.Write(ref pixels, pixels.Select(b => (byte)(b ^ 0x4C)).ToArray()); time.Advance(TimeSpan.FromMilliseconds(101));
            await SwitchUntil(() => original.Disposed && inputs.LiveHandles == 0, time);
            // Receiver cache becomes positive before its native handler. A new owner must finish all4SnapshotGETs
            // and validate its exact recorded profile before probingCAP, yet admit no restoration setter whileCAP fails.
            routes.RadioPeerUnavailable = false; routes.RadioCapsMalformed = malformed; routes.RadioCapsUnavailable = !malformed;
            var expected = malformed ? "Error" : "Waiting";
            await SwitchUntil(() => routes.Ports.Count >= 2 && routes.Ports[^1].Disposed
                && routes.Ports[^1].FreshPresentationProbeAtWriteCount != null
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == expected, time);
            var unready = routes.Ports[^1];
            unready.Requests.Select(p => Convert.ToHexString(p.AsSpan(0, 5))).Should().Equal(
                "0212000000", "0221000000", "0223020000", "0223030000", "02127D0000");
            unready.Requests.Should().HaveCount(unready.FreshPresentationProbeAtWriteCount!.Value);
            File.ReadAllBytes(journal).Should().Equal(before); inputs.LiveHandles.Should().Be(0);
            ((int)budget.GetValue(owner)!).Should().Be(1);
            var images = routes.Radio.Images.Count; var opens = routes.Ports.Count;
            routes.RadioCapsUnavailable = false; routes.RadioCapsMalformed = false;
            if (malformed)
            {
                time.Advance(TimeSpan.FromSeconds(2)); await Task.Delay(1100);
                routes.Ports.Should().HaveCount(opens); (await owner.GetKeyboardDisplayStatusAsync()).State.Should().Be("Error");
            }
            else
            {
                await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count > images, time);
                routes.Radio.Images[^1].Should().Equal(Volatile.Read(ref pixels));
                await WriteRouteKeys(bridge, 2);
                (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
                ((int)budget.GetValue(owner)!).Should().Be(1);
            }
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse(); state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioCapsUnavailable = false; routes.RadioCapsMalformed = false; routes.RadioPeerUnavailable = false;
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Theory]
    [InlineData("image-begin", "image-begin")] [InlineData("pixel95-abort-cap", "image-abort-status")]
    [InlineData("post-frame-capability", "post-frame-capability")] [InlineData("first-native-lease", "first-native-lease")]
    public async Task Actual_RF_source_ephemeral_roles_raise_typed_retirement_without_latching_persistent_fault(string phase, string expected)
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        using var connection = routes.TryOpen(Policy(2).DeviceKey, Policy(2).FirmwareHeader)!;
        var source = new RogNativeMenuSource(Policy(2), inputs, inputs, time, () => Cookie); source.RequestCodexEnabled(true);
        await using var actor = source.Attach(connection); var pixels = Pixels();
        await actor.StepAsync(() => pixels, default);
        routes.ArmRadioTransientPhase(phase); pixels = pixels.Select(b => (byte)(b ^ 0x3C)).ToArray();
        var failure = await ((Func<Task>)(() => actor.StepAsync(() => pixels, default)))
            .Should().ThrowAsync<RogNativeArtworkUploadRefusedException>();
        failure.Which.Phase.Should().Be(expected); actor.RecoveryBlocked.Should().BeFalse();
        var writes = routes.Radio.Requests.Count;
        await actor.RetireAfterRecoverableFaultAsync(default); inputs.LiveHandles.Should().Be(0);
        routes.Radio.Requests.Should().HaveCount(writes);
    }

    [Fact]
    public async Task Actual_RF_session_ongoing_native_lease_refusal_has_scoped_presentation_exception()
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        using var connection = routes.TryOpen(Policy(2).DeviceKey, Policy(2).FirmwareHeader)!;
        var session = new RogWiredOledSession(connection); await session.SnapshotAsync(connection.FirmwareHeader, default);
        var source = new RogNativeMenuSource(Policy(2), inputs, inputs, time, () => Cookie); source.RequestCodexEnabled(true);
        await using var actor = source.Attach(connection); var pixels = Pixels();
        await actor.StepAsync(() => pixels, default); routes.ArmRadioTransientPhase("native-lease");
        var failure = await ((Func<Task>)(() => session.RunAsync(() => pixels, () => { }, default, display: false,
            nativeTick: ct => actor.StepAsync(() => pixels, ct), nativeWake: () => actor.HasChanges)))
            .Should().ThrowAsync<RogNativePresentationUnavailableException>();
        failure.Which.Phase.Should().Be("native-lease"); failure.Which.DeviceKey.Should().Be(connection.DeviceKey);
        failure.Which.ProfileToken.Should().Be(session.VerifiedProfileToken); actor.RecoveryBlocked.Should().BeFalse();
        var writes = routes.Radio.Requests.Count; await actor.RetireAfterRecoverableFaultAsync(default);
        routes.Radio.Requests.Should().HaveCount(writes); inputs.LiveHandles.Should().Be(0);
    }

    [Theory]
    [InlineData("image-begin")] [InlineData("pixel95-abort-cap")]
    [InlineData("post-frame-capability")] [InlineData("first-native-lease")] [InlineData("native-lease")]
    public async Task Actual_RF_ephemeral_image_or_native_lease_refusal_retires_before_fresh_snapshot_then_rebuilds_complete_frame(string phase)
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true);
        var data = Path.Combine(Path.GetTempPath(), "actual-rf-image-sleep-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); ulong cookie = Cookie; var browses = new List<int>(); var pixels = Pixels();
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay, browses.Add);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: () => Volatile.Read(ref pixels),
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count >= 1, time);
            await WriteRouteKeys(bridge, 2);
            var before = File.ReadAllBytes(journal); var original = routes.Ports[^1]; var opens = routes.Ports.Count;
            var operationStart = original.Requests.Count; routes.ArmRadioTransientPhase(phase);
            if (phase != "native-lease") Volatile.Write(ref pixels, pixels.Select(b => (byte)(b ^ 0x35)).ToArray());
            time.Advance(TimeSpan.FromMilliseconds(101));
            await SwitchUntil(() => original.Disposed && routes.Ports.Count > opens && routes.Ports[^1].Disposed
                && owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting", time);
            original.TransientFailureAtWriteCount.Should().NotBeNull();
            original.Requests.Should().HaveCount(original.TransientFailureAtWriteCount!.Value,
                "no old chunk, ARM, setter or recovery query is retried after the final refused ephemeral operation");
            var failedOperation = original.Requests.Skip(operationStart).ToArray();
            if (phase == "pixel95-abort-cap")
            {
                original.InjectedTransientFailures.Should().Equal("pixel95-abort-cap", "abort-cap");
                failedOperation.Where(p => p[1] == 0x67 && p[2] == 1).Select(p => p[3]).Should()
                    .Equal(Enumerable.Range(95, 18).Reverse().Select(i => (byte)i));
                Convert.ToHexString(original.Requests[^1].AsSpan(0, 5)).Should().Be("02127D0000");
                failedOperation.Should().NotContain(p => p[1] == 0x67 && p[2] == 2);
            }
            else original.InjectedTransientFailures.Should().ContainSingle().Which.Should().Be(phase);
            if (phase == "image-begin") failedOperation.Should().NotContain(p => p[1] == 0x67 && (p[2] == 1 || p[2] == 2));
            if (phase == "post-frame-capability") failedOperation.Should().NotContain(p => p[1] == 0x67 && p[2] == 2);
            routes.Ports.Skip(opens).Should().OnlyContain(p => p.Requests.Count == 1
                && p.Requests[0][1] == 0x12 && p.Requests[0][2] == 0,
                "a sleeping replacement starts with its own BasicInfo, never a CAP/ARM/SET before a positive snapshot");
            File.ReadAllBytes(journal).Should().Equal(before); inputs.LiveHandles.Should().Be(0);
            source.DedicatedPageActive.Should().BeFalse();

            var imagesBeforeWake = routes.Radio.Images.Count; routes.RadioPeerUnavailable = false;
            await SwitchUntil(() => bridge.Available && source.DedicatedPageActive && routes.Radio.Images.Count > imagesBeforeWake, time);
            var recovered = routes.Ports[^1];
            recovered.Requests.Where(p => p[1] == 0x67 && p[2] == 1).Should().HaveCount(113);
            recovered.Requests.First(p => p[1] == 0x67 && p[2] == 1)[3].Should().Be(112);
            routes.Radio.Images[^1].Should().Equal(Volatile.Read(ref pixels));
            recovered.Requests.Should().Contain(p => p[1] == 0x67 && p[2] == 2);
            await WriteRouteKeys(bridge, 2); await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse();
            routes.Ports.Should().OnlyContain(p => p.MaximumReaders <= 1); state.Applied.Should().BeEmpty();
        }
        finally
        {
            routes.RadioPeerUnavailable = false; await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_RF_ARM_or_DISARM_refusal_remains_terminal_outside_ephemeral_artwork(bool disarm)
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage: true) { RadioRefuseArmOnce = !disarm };
        using var connection = routes.TryOpen(Policy(2).DeviceKey, Policy(2).FirmwareHeader)!;
        var source = new RogNativeMenuSource(Policy(2), inputs, inputs, time, () => Cookie); source.RequestCodexEnabled(true);
        await using var actor = source.Attach(connection);
        if (disarm)
        {
            await actor.StepAsync(Pixels, default); routes.RadioRefuseDisarmOnce = true;
            await ((Func<Task>)(() => actor.StopAsync(default))).Should().ThrowAsync<IOException>();
        }
        else await ((Func<Task>)(() => actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>();
        actor.RecoveryBlocked.Should().BeTrue(); var writes = routes.Radio.Requests.Count;
        await actor.StopAsync(default); routes.Radio.Requests.Should().HaveCount(writes);
    }

    [Fact]
    public async Task Actual_RF_legacy_display_lease_refusal_does_not_use_native_presentation_recovery_exception()
    {
        using var channel = new LegacyRfLeaseRefusalChannel();
        using var connection = new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE,
            new Dictionary<byte, HidCollectionInfo>()), channel, Policy(2).FirmwareHeader, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(connection.FirmwareHeader, default); await session.StartOutputAsync(100, default);
        var failure = await ((Func<Task>)(() => session.RunAsync(Pixels, () => { }, default)))
            .Should().ThrowAsync<RogOledEnvelopeRefusedException>();
        failure.Which.Should().NotBeOfType<RogNativePresentationUnavailableException>();
        channel.Requests.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(113);
        channel.PendingReaders.Should().Be(0); channel.MaximumReaders.Should().Be(1);
    }

    private sealed class LegacyRfLeaseRefusalChannel : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Requests { get; } = [];
        public int PendingReaders { get; private set; }
        public int MaximumReaders { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); report.Length.Should().Be(64); report.Span[0].Should().Be(2);
            var p = report.ToArray(); Requests.Add(p); var response = new byte[64]; p.AsSpan(0, 5).CopyTo(response);
            switch (p[1], p[2])
            {
                case (0x12, 0): response[5] = 8; response[7] = 7; response[9] = 6; response[11] = 6; response[12] = 0x25; response[14] = 1; break;
                case (0x21, 0): response[5] = 3; break;
                case (0x23, 2): response[5] = 1; break;
                case (0x23, 3): response[5] = 100; break;
                case (0x68, 0): response = p; break;
                case (0x61, 0): case (0x67, 0): case (0x67, 1): break;
                case (0x67, 2): Array.Clear(response); response[0] = 2; response[1] = 0xFF; response[2] = 0xAA; break;
                default: throw new InvalidOperationException("Unexpected legacy RF test packet.");
            }
            _replies.Writer.TryWrite(response).Should().BeTrue(); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            MaximumReaders = Math.Max(MaximumReaders, ++PendingReaders);
            try { var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
            finally { PendingReaders--; }
        }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
