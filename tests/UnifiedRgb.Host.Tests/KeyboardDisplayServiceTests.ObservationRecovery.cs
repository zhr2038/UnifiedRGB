using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_replaces_only_the_disposed_owner_and_preserves_recorded_settings_and_intent(bool unexpected)
    {
        var factory = new ObservationFactory { Drop = n => n == 1, UnexpectedReply = unexpected };
        using var gate = new PeripheralConfigurationGate(new(false, null));
        using var service = ObservationService(factory, gate);
        await service.StartAsync(default);
        try
        {
            await ObservationUntil(() => factory.Channels.Count == 2 && factory.Channels[1].Observations > 0);
            factory.OpenWhilePreviousLive.Should().BeFalse();
            factory.Channels[0].Disposed.Should().BeTrue(); factory.Channels[0].WritesAfterFault.Should().Be(0);
            factory.Channels[1].Inner.Brightness.Should().Be(25);
            factory.Channels[1].Inner.Native.SameContent(factory.Channels[0].OriginalNative).Should().BeTrue();
            using var journal = JsonDocument.Parse(File.ReadAllBytes(ObservationJournal));
            journal.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
            journal.RootElement.GetProperty("Brightness").GetByte().Should().Be(25);
            (await service.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
            File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display.json")).Should().BeFalse("automatic replacement never changes the user's preference");
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task ObservationRecovery_received_mismatched_GET12_retires_then_restores_on_a_fresh_wireless_owner()
    {
        var factory = new ObservationFactory { UnexpectedReply = true };
        using var gate = new PeripheralConfigurationGate(new(false, null)); using var service = ObservationService(factory, gate);
        await service.StartAsync(default);
        try
        {
            await ObservationUntil(() => factory.Channels.Count == 1 && factory.Channels[0].Observations > 0);
            factory.Channels[0].DropNextScope = true;
            await ObservationUntil(() => factory.Channels.Count == 2 && factory.Channels[1].Observations > 0);
            factory.Channels[0].Disposed.Should().BeTrue(); factory.Channels[0].WritesAfterFault.Should().Be(0);
            factory.OpenWhilePreviousLive.Should().BeFalse();
            factory.Channels[1].Inner.BrightnessWrites.Should().Contain(25);
            factory.Channels[1].Inner.NativeWrites.Should().BeGreaterThan(0);
            factory.Channels[1].Inner.Native.SameContent(factory.Channels[0].OriginalNative).Should().BeTrue();
        }
        finally { await service.StopAsync(default); }
    }

    [Theory] [InlineData("schema")] [InlineData("device")] [InlineData("profile")] [InlineData("transport")]
    public async Task ObservationRecovery_unexpected_reply_requires_the_exact_existing_recorded_scope_before_reopen(string mismatch)
    {
        var factory = new ObservationFactory { UnexpectedReply = true };
        using var gate = new PeripheralConfigurationGate(new(false, null)); using var service = ObservationService(factory, gate);
        await service.StartAsync(default);
        try
        {
            await ObservationUntil(() => factory.Channels.Count == 1 && factory.Channels[0].Observations > 0);
            var record = JsonNode.Parse(File.ReadAllBytes(ObservationJournal))!;
            switch (mismatch)
            {
                case "schema": record["Schema"] = 1; break;
                case "device": record["DeviceKey"] = new string('B', 64); break;
                case "profile": record["ProfileToken"] = 5; break;
                case "transport": record["FirmwareHeader"] = "001200000024000100"; break;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record); File.WriteAllBytes(ObservationJournal, bytes);
            factory.Channels[0].DropNextScope = true;
            await WaitAsync(service, "Error"); await Task.Delay(600);
            factory.Channels.Should().ContainSingle(); factory.Channels[0].WritesAfterFault.Should().Be(0);
            File.ReadAllBytes(ObservationJournal).Should().Equal(bytes);
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain("no matching recorded recovery scope");
        }
        finally { await service.StopAsync(default); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_three_replacements_bound_an_episode_and_only_a_healthy_failed_session_resets_it(bool unexpected)
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var repeated = new ObservationFactory { Drop = _ => true, UnexpectedReply = unexpected };
        using (var service = ObservationService(repeated, gate))
        {
            await service.StartAsync(default);
            try { await WaitAsync(service, "Error"); repeated.Channels.Should().HaveCount(4); await Task.Delay(600); repeated.Channels.Should().HaveCount(4); }
            finally { await service.StopAsync(default); }
        }
        var clock = new ManualTimeProvider();
        var resets = new ObservationFactory { Drop = n => n <= 4, UnexpectedReply = unexpected, OnObservation = (n, _) => { if (n == 4) clock.Advance(TimeSpan.FromSeconds(61)); } };
        using (var service = ObservationService(resets, gate, clock))
        {
            await service.StartAsync(default);
            try
            {
                await ObservationUntil(() => resets.Channels.Count == 5 && resets.Channels[4].Observations > 0);
                typeof(KeyboardDisplayService).GetField("_observationReplacements", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(service).Should().Be(1, "only session4 actually ran for61s; startup success never resets the first3 attempts");
            }
            finally { await service.StopAsync(default); }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_gate_fresh_resume_and_cancelled_or_newer_intent_prevent_a_reopen(bool unexpected)
    {
        foreach (var condition in new[] { "gate", "fresh-resume", "cancel", "intent" })
        {
            using var gate = new PeripheralConfigurationGate(new(false, null));
            using var stop = new CancellationTokenSource();
            var native = new RogNativeMenuSource(new());
            var factory = new ObservationFactory { Drop = n => n == 1, UnexpectedReply = unexpected, OnObservation = (_, _) =>
            {
                if (condition == "gate") gate.BlockRogAcquisition();
                else if (condition == "fresh-resume") gate.BeginFreshResume();
                else if (condition == "cancel") stop.Cancel();
            } };
            using (var service = ObservationService(factory, gate, native: native))
            {
                await service.StartAsync(stop.Token);
                try
                {
                    if (condition == "intent")
                    {
                        await WaitAsync(service, "Recovering"); native.RequestEnabled(true);
                        await WaitAsync(service, "Error");
                        (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain("replacementDenied=newer-native-intent");
                    }
                    else await ObservationUntil(() => factory.Channels.Count > 0 && factory.Channels[0].Faulted);
                    await Task.Delay(650); factory.Channels.Should().ContainSingle();
                }
                finally { await service.StopAsync(default); }
            }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_preexisting_native_join_or_wire_fault_and_physical_change_are_terminal(bool unexpected)
    {
        foreach (var field in new[] { "_joinFailed", "_wireFault", "physical" })
        {
            using var gate = new PeripheralConfigurationGate(new(false, null));
            var native = new RogNativeMenuSource(new());
            var factory = new ObservationFactory { Drop = _ => true, UnexpectedReply = unexpected, OnObservation = (_, channel) =>
            {
                if (field == "physical") { channel.Present = false; return; }
                var actor = typeof(RogNativeMenuSource).GetField("_actor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
                typeof(RogNativeMenuSource.Actor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(actor, field == "_joinFailed" ? true : new IOException("Injected existing native wire fault"));
            } };
            using var service = ObservationService(factory, gate, native: native);
            await service.StartAsync(default);
            try { await WaitAsync(service, "Error"); await Task.Delay(600); factory.Channels.Should().ContainSingle(); }
            finally { await service.StopAsync(default); }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_setter_readback_and_refusal_never_receive_the_readonly_classification(bool unexpected)
    {
        foreach (var refusal in new[] { false, true })
        {
            using var gate = new PeripheralConfigurationGate(new(false, null));
            var factory = new ObservationFactory { DropSetter = !refusal, Refuse = refusal, UnexpectedReply = unexpected };
            using var service = ObservationService(factory, gate);
            await service.StartAsync(default);
            try
            {
                await WaitAsync(service, "Error"); factory.Channels.Should().ContainSingle();
                File.Exists(ObservationJournal).Should().BeTrue("unverified mutations never lose the recovery record");
                if (!refusal) (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain(unexpected ? "RogOledUnexpectedReplyException" : "brightness-setter-readback");
                factory.Channels[0].WritesAfterFault.Should().Be(0);
            }
            finally { await service.StopAsync(default); }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_changed_or_missing_journal_and_changed_profile_do_not_write_recovery_setters(bool unexpected)
    {
        foreach (var change in new[] { "missing", "changed", "profile", "open-notready" })
        {
            using var gate = new PeripheralConfigurationGate(new(false, null));
            var factory = new ObservationFactory { Drop = n => n == 1, UnexpectedReply = unexpected, ChangedProfileOnReplacement = change == "profile",
                ReplacementOpenFailure = change == "open-notready" ? new RogKeyboardNotReadyException("Injected replacement unavailable") : null };
            using var service = ObservationService(factory, gate);
            await service.StartAsync(default);
            try
            {
                await WaitAsync(service, "Recovering");
                if (change == "missing") File.Delete(ObservationJournal);
                if (change == "changed") File.AppendAllText(ObservationJournal, " "); // even validJSON byte drift is not adopted
                await WaitAsync(service, "Error");
                if (change == "profile") { factory.Channels.Should().HaveCount(2); factory.Channels[1].Inner.Mutations.Should().Be(0); factory.Channels[1].Inner.NativeWrites.Should().Be(0); }
                else factory.Channels.Should().ContainSingle();
            }
            finally { await service.StopAsync(default); }
        }
    }

    [Fact]
    public async Task ObservationRecovery_normal_cold_wait_is_readonly_until_wake_and_fresh_resume_stays_bounded()
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var cold = new ColdRfFactory { BlankOwners = 4 };
        using (var service = new KeyboardDisplayService(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = WirelessPinned() },
            DispatchProxy.Create<IRgbService, ReadProxy>(), DispatchProxy.Create<IScreenService, ReadProxy>(), new Factory(), new ClearGuard(),
            wirelessFactory: cold, configurationGate: gate))
        {
            await service.StartAsync(default);
            try
            {
                await WaitAsync(service, "Waiting"); cold.Opens.Should().Be(4);
                cold.Channels.Should().OnlyContain(c => c.Writes == 1 && c.Io.Inner.Mutations == 0 && c.Disposed);
                await WaitAsync(service, "Running"); cold.Opens.Should().Be(5);
            }
            finally { await service.StopAsync(default); }
        }
        gate.BlockRogAcquisition(); gate.BeginFreshResume();
        cold = new ColdRfFactory { BlankOwners = int.MaxValue };
        using (var service = new KeyboardDisplayService(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = WirelessPinned() },
            DispatchProxy.Create<IRgbService, ReadProxy>(), DispatchProxy.Create<IScreenService, ReadProxy>(), new Factory(), new ClearGuard(),
            wirelessFactory: cold, configurationGate: gate))
        {
            await service.StartAsync(default);
            try { await WaitAsync(service, "Error"); cold.Opens.Should().Be(4); cold.Channels.Should().OnlyContain(c => c.Io.Inner.Mutations == 0); }
            finally { await service.StopAsync(default); gate.EndFreshResume(); }
        }
    }

    [Fact]
    public async Task ObservationRecovery_RF_failure_reports_only_the_last_eight_reply_headers()
    {
        using var channel = new HeaderChannel();
        using var connection = new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE,
            new Dictionary<byte, HidCollectionInfo>()), channel, "0800070025000100", () => true);
        var failure = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, default)))
            .Should().ThrowAsync<InvalidDataException>();
        failure.Which.Message.Should().Contain("seenReplyHeaders=[" + string.Join(",", Enumerable.Repeat("0267020000/64", 8)) + "]");
    }

    [Fact]
    public async Task ObservationRecovery_scope_roles_are_exact_and_restoration_scope_failure_stays_terminal()
    {
        foreach (var role in new[] { "idle", "pre-key", "restore" })
        {
            using var channel = new ObservationChannel(new Io(), false, false, false, _ => { });
            using var connection = new RogWiredOledConnection(channel, () => true, () => Key, () => "001200000024000100");
            var session = new RogWiredOledSession(connection);
            await session.SnapshotAsync(connection.FirmwareHeader, default); await session.SnapshotLightingAsync(default);
            channel.DropNextScope = true;
            if (role == "restore")
            {
                var error = await ((Func<Task>)(() => session.RestoreAsync(25, default))).Should().ThrowAsync<RogOledTimeoutException>();
                error.Which.Should().NotBeOfType<RogReadOnlyObservationTimeoutException>();
            }
            else
            {
                var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); owner.AcceptKeyFrames(RogPerKeyLighting.WiredTransport);
                Task? write = role == "pre-key" ? bridge.WriteKeysAsync([new(0, new(1, 2, 3))], RogPerKeyLighting.WiredTransport, default) : null;
                var error = await ((Func<Task>)(() => session.RunAsync(() => new byte[6656], () => { }, default, owner, false)))
                    .Should().ThrowAsync<RogReadOnlyObservationTimeoutException>();
                error.Which.Operation.Should().Be(role == "pre-key" ? RogReadOnlyOperation.PreKeyFrameScope : RogReadOnlyOperation.IdleScopeObservation);
                if (write is not null) await ((Func<Task>)(() => write)).Should().ThrowAsync<RogReadOnlyObservationTimeoutException>();
                bridge.Detach(owner);
            }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_late_native_enable_and_page_toggle_before_fault_recover_the_latest_intent(bool unexpected)
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var native = new RogNativeMenuSource(new());
        var factory = new ObservationFactory { UnexpectedReply = unexpected };
        using var service = ObservationService(factory, gate, native: native, artworkEnabled: false);
        await service.StartAsync(default);
        try
        {
            await ObservationUntil(() => factory.Channels.Count == 1 && factory.Channels[0].Observations > 0);
            native.RequestEnabled(true); // persisted preference/startup or legitimate later user intent, before the fault
            await service.SetKeyboardDisplayEnabledAsync(true);
            await service.SetKeyboardDisplayEnabledAsync(false);
            factory.Channels[0].DropObservation = true;
            await ObservationUntil(() => factory.Channels.Count == 2 && factory.Channels[1].Observations > 0);
            factory.OpenWhilePreviousLive.Should().BeFalse();
            (await service.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
            factory.Channels[1].Inner.Data.Should().Be(0, "the emergency controls-only option remains effective");
        }
        finally { await service.StopAsync(default); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservationRecovery_intent_after_fault_denies_with_exact_counter_and_revision_diagnostics(bool unexpected)
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var native = new RogNativeMenuSource(new()); var factory = new ObservationFactory { Drop = n => n == 1, UnexpectedReply = unexpected };
        using var service = ObservationService(factory, gate, native: native, artworkEnabled: false);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Recovering"); native.RequestEnabled(true);
            await WaitAsync(service, "Error"); factory.Channels.Should().ContainSingle();
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain("replacementDenied=newer-native-intent")
                .And.Contain("attempts=1/3").And.Contain("faultNative=0; currentNative=1")
                .And.Contain("normalGate=True; freshResume=False");
        }
        finally { await service.StopAsync(default); }
    }

    private string ObservationJournal => Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
    private KeyboardDisplayService ObservationService(ObservationFactory factory, PeripheralConfigurationGate gate,
        TimeProvider? time = null, RogNativeMenuSource? native = null, bool artworkEnabled = true) => new(
        new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = (factory.UnexpectedReply ? WirelessPinned() : Pinned()) with
            { Enabled = false, LightingVerified = !factory.UnexpectedReply, WirelessLightingVerified = factory.UnexpectedReply, HostArtworkEnabled = artworkEnabled } },
        DispatchProxy.Create<IRgbService, ReadProxy>(), DispatchProxy.Create<IScreenService, ReadProxy>(), factory, new ClearGuard(),
        configurationGate: gate, time: time, nativeMenu: native, wirelessFactory: factory);
    private static async Task ObservationUntil(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, stop.Token);
    }

    private sealed class ObservationFactory : IRogWiredOledFactory, IRogWirelessOledFactory
    {
        public List<ObservationChannel> Channels { get; } = [];
        public Func<int, bool>? Drop { get; init; }
        public Action<int, ObservationChannel>? OnObservation { get; init; }
        public bool DropSetter { get; init; }
        public bool UnexpectedReply { get; init; }
        public bool Refuse { get; init; }
        public bool ChangedProfileOnReplacement { get; init; }
        public Exception? ReplacementOpenFailure { get; init; }
        public bool OpenWhilePreviousLive { get; private set; }
        public RogWiredOledConnection? TryOpen(string key)
        {
            var channel = CreateChannel();
            return new(channel, () => channel.Present, () => key, () => "001200000024000100");
        }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            var channel = CreateChannel();
            return new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()),
                new WirelessObservationChannel(channel), firmwareHeader, () => channel.Present);
        }
        private ObservationChannel CreateChannel()
        {
            OpenWhilePreviousLive |= Channels.Count > 0 && !Channels[^1].Disposed;
            var number = Channels.Count + 1;
            if (number > 1 && ReplacementOpenFailure is { } failure) throw failure;
            var inner = Channels.Count == 0 ? new Io() : Channels[^1].Inner.Reopen();
            if (ChangedProfileOnReplacement && number > 1) inner.ProfileToken = 5;
            var channel = new ObservationChannel(inner, Drop?.Invoke(number) == true, DropSetter, Refuse,
                c => OnObservation?.Invoke(number, c)) { UnexpectedReply = UnexpectedReply };
            Channels.Add(channel);
            return channel;
        }
    }

    private sealed class ObservationChannel(Io inner, bool drop, bool dropSetter, bool refuse, Action<ObservationChannel> observed) : IHidReportChannel
    {
        public Io Inner => inner;
        public RogKeyboardNativeState OriginalNative { get; } = inner.Native;
        public bool Disposed { get; private set; }
        public bool Present { get; set; } = true;
        public bool Faulted { get; private set; }
        public int WritesAfterFault { get; private set; }
        public int Observations { get; private set; }
        public bool DropNextScope { get; set; }
        public bool UnexpectedReply { get; init; }
        public bool DropObservation { get; set; } = drop;
        private (byte Command, byte Key) _last;
        private bool _stall;
        private byte[]? _ownReply;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            if (Faulted) WritesAfterFault++;
            var p = report.ToArray(); var query = p[1] == 0x23 && p[2] == 3;
            if (DropNextScope && p[1] == 0x12 && p[2] == 0)
            {
                DropNextScope = false;
                if (UnexpectedReply) { _ownReply = new byte[65]; _ownReply[1] = 0x25; _ownReply[3] = 1; }
                else _stall = true;
                return ValueTask.CompletedTask;
            }
            var observer = query && _last == ((byte)0x12, (byte)0);
            var setterReadback = query && _last.Command == 0x68;
            _last = (p[1], p[2]);
            if (observer) { Observations++; observed(this); }
            if (DropObservation && observer || dropSetter && setterReadback)
            {
                if (UnexpectedReply) { _ownReply = new byte[65]; _ownReply[1] = 0x25; _ownReply[3] = 1; }
                else _stall = true;
                return ValueTask.CompletedTask;
            }
            // This fixture option tests mandatory verification after SET68, not an independent idle read.
            if (refuse && setterReadback) { _ownReply = new byte[65]; _ownReply[1] = 0xFF; _ownReply[2] = 0xAA; return ValueTask.CompletedTask; }
            if (p[1] is 0x27 or 0x74) { _ownReply = new byte[65]; p.AsSpan(0, 6).CopyTo(_ownReply); return ValueTask.CompletedTask; }
            return inner.WriteAsync(report, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_stall)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { Faulted = true; throw; }
            }
            if (_ownReply is { } reply) { _ownReply = null; Faulted |= reply[1] is 0x25 or 0xFF; reply.CopyTo(buffer); return reply.Length; }
            return await inner.ReadAsync(buffer, ct);
        }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    private sealed class WirelessObservationChannel(ObservationChannel inner) : IHidReportChannel
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        { var p = new byte[65]; report.Span[1..].CopyTo(p.AsSpan(1)); return inner.WriteAsync(p, ct); }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var p = new byte[65]; var length = await inner.ReadAsync(p, ct); if (length == 0) return 0;
            if (p[1] == 0x12 && p[2] == 0) { p[5] = 8; p[7] = 7; p[12] = 0x24; p[14] = 1; }
            p.AsMemory(0, 64).CopyTo(buffer); buffer.Span[0] = 2; return 64;
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class HeaderChannel : IHidReportChannel
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0x67; buffer.Span[2] = 2; return ValueTask.FromResult(64); }
        public void Dispose() { }
    }
}
