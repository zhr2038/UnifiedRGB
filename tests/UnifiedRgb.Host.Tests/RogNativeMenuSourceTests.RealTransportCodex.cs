using System.Buffers.Binary;
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
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Actual_transport_native_identity_loss_without_positive_absence_is_terminal(byte transport)
    {
        await using var rig = new RealTransportCodexRig(transport);
        await rig.Actor.StepAsync(Pixels, default);
        var writes = rig.Wire.Requests.Count;
        rig.AbsenceProof = false; rig.Present = false;
        rig.Time.Advance(TimeSpan.FromMilliseconds(101));
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        rig.Actor.RecoveryBlocked.Should().BeTrue();
        await rig.Actor.StopAsync(default);
        rig.Wire.Requests.Should().HaveCount(writes);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Actual_transport_native_physical_disappearance_is_typed_and_performs_no_scope_replay(byte transport)
    {
        await using var rig = new RealTransportCodexRig(transport);
        await rig.Actor.StepAsync(Pixels, default);
        var writes = rig.Wire.Requests.Count;
        rig.Present = false;
        rig.Time.Advance(TimeSpan.FromMilliseconds(101));
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<RogOledConnectionLostException>();
        rig.Actor.PhysicalScopeLost.Should().BeTrue();
        await rig.Actor.StopAsync(default);
        rig.Wire.Requests.Should().HaveCount(writes);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Actual_transport_Codex_complete_frames_browse_and_launch_keep_one_owner(byte transport)
    {
        await using var rig = new RealTransportCodexRig(transport);
        var activator = new CodexLaunchTests.FakeActivator(); Func<Task>? queued = null;
        using var launcher = new CodexLaunchCoordinator(activator, rig.Time, schedule: run => { queued = run; return Task.CompletedTask; });
        rig.Source.SetLaunchCoordinator(launcher);
        rig.Wire.DuplicateReadyEcho = transport == 2;
        var pixels = Pixels();
        (await rig.Actor.StepAsync(() => pixels, default)).Should().BeTrue();
        rig.Wire.Images.Should().ContainSingle().Which.Should().Equal(pixels);
        rig.Wire.Requests.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(transport == 1 ? 111 : 113);
        rig.Wire.LastCopyLength.Should().Be(transport == 1 ? 56 : 48);
        rig.Wire.Arms.Should().Be(1); rig.Wire.Renewals.Should().Be(1);
        rig.Wire.Operations.TakeLast(2).Should().Equal("CAP_READY", "RENEW");
        rig.Wire.Requests.Should().OnlyContain(p => p.Length == (transport == 1 ? 65 : 64) && p[0] == (transport == 1 ? 0 : 2));
        await rig.SendAsync(1, 0, 3); await rig.SendAsync(2, 1); await rig.SendAsync(3, 0); await rig.SendAsync(4, 4);
        await rig.Actor.StepAsync(() => pixels, default);
        rig.Browses.Should().Equal(-1, 1); activator.Calls.Should().Be(0);
        await rig.SendAsync(5, 0); await rig.SendAsync(6, 2); await rig.SendAsync(7, 0);
        await rig.Actor.StepAsync(() => pixels, default); queued.Should().NotBeNull(); await queued!();
        activator.Calls.Should().Be(1); rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        pixels = pixels.Select(b => (byte)(b ^ 0x5A)).ToArray();
        await rig.Actor.StepAsync(() => pixels, default);
        rig.Wire.Images.Should().HaveCount(2); rig.Wire.Images[^1].Should().Equal(pixels);
        rig.Wire.Arms.Should().Be(1); rig.Input.Opens.Should().Be(1);
        rig.Wire.MaximumReaders.Should().Be(1); rig.Wire.PendingReads.Should().Be(0);
        rig.Input.MaximumPending.Should().Be(1);
        if (rig.Connection is RogWirelessOledConnection wireless)
        {
            wireless.PairingEvidence.Should().NotBeNull().And.Match<RogPairingEvidence>(p => p.Invalidated == null && p.Observations >= 3);
            rig.Wire.DuplicateEchoesRead.Should().Be(2, "the real RF optional-lease reader drains only exact CAP replies completed on this owner");
        }
    }

    [Theory]
    [InlineData((byte)1, "ready")] [InlineData((byte)2, "ready")]
    [InlineData((byte)1, "cookie")] [InlineData((byte)2, "cookie")]
    [InlineData((byte)1, "epoch")] [InlineData((byte)2, "epoch")]
    [InlineData((byte)1, "transport")] [InlineData((byte)2, "transport")]
    public async Task Actual_transport_Codex_requires_fresh_same_scope_READY_before_renewal(byte transport, string fault)
    {
        await using var rig = new RealTransportCodexRig(transport); rig.Wire.CommitFault = fault;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>()
            .WithMessage("*terminalAfterPixelWrites*renewalNotSent*");
        rig.Wire.Images.Should().ContainSingle(); rig.Wire.Renewals.Should().Be(0);
        rig.Actor.RecoveryBlocked.Should().BeTrue();
        var writes = rig.Wire.Requests.Count;
        await rig.Actor.StopAsync(default); rig.Wire.Requests.Should().HaveCount(writes);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Actual_transport_Codex_replayed_input_never_launches_or_changes_scene(byte transport)
    {
        await using var rig = new RealTransportCodexRig(transport); var activator = new CodexLaunchTests.FakeActivator();
        using var launcher = new CodexLaunchCoordinator(activator, rig.Time); rig.Source.SetLaunchCoordinator(launcher);
        await rig.Actor.StepAsync(Pixels, default);
        await rig.SendAsync(1, 0, 3); await rig.SendAsync(1, 2);
        await rig.Actor.StepAsync(Pixels, default);
        activator.Calls.Should().Be(0); rig.Browses.Should().BeEmpty();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_RF_Codex_negative_pixel_uses_native_role_and_never_renews_incomplete_frame()
    {
        await using var rig = new RealTransportCodexRig(2); rig.Wire.PixelNegative = true;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<RogNativeImageContinuationException>();
        rig.Wire.Renewals.Should().Be(0); rig.Wire.Images.Should().BeEmpty(); rig.Actor.RecoveryBlocked.Should().BeFalse();
        var rejected = rig.Wire.Requests.Single(p => p[1] == 0x67 && p[2] == 1 && p[3] == 25);
        rejected.Should().HaveCount(64); rig.Wire.MaximumReaders.Should().Be(1); rig.Wire.PendingReads.Should().Be(0);
        rig.Wire.Requests.Should().NotContain(p => p[1] == 0x61 || p[1] == 0x68 || p[1] == 0x74);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Actual_transport_Codex_runs_through_existing_Host_display_owner_and_shared_RGB(byte transport)
    {
        var time = new SourceClock(); using var input = new Input(time, transport); using var wire = new CodexWireChannel(transport);
        var policy = Policy(transport); var factory = new CodexActualTransportFactory(policy, wire, time);
        var data = Path.Combine(Path.GetTempPath(), "codex-actual-transport-" + Guid.NewGuid().ToString("N"));
        var display = transport == 1 ? new KeyboardDisplayOptions { ControlVerified = true, LightingVerified = true,
            DeviceKey = policy.DeviceKey, FirmwareHeader = policy.FirmwareHeader, DeviceShowsNativeEntryLogo = true }
            : new KeyboardDisplayOptions { WirelessControlVerified = true, WirelessLightingVerified = true,
                WirelessReceiverKey = policy.DeviceKey, WirelessFirmwareHeader = policy.FirmwareHeader, DeviceShowsNativeEntryLogo = true };
        var options = new UnifiedRgbOptions { DataDirectory = data, KeyboardDisplay = display };
        var source = new RogNativeMenuSource(policy, input, input, time, () => Cookie); source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var rgbState = (SceneCycleRgbProxy)rgb;
        rgbState.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, factory, new MutationClearGuard(), bridge,
            wirelessFactory: factory, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels);
        try
        {
            await owner.StartAsync(default); await Until(() => wire.Arms == 1 && bridge.Available);
            time.Advance(TimeSpan.FromMilliseconds(1201)); await Until(() => wire.Renewals >= 1);
            wire.Images.Should().NotBeEmpty(); wire.Images[0].Should().Equal(Pixels());
            var colors = Enumerable.Range(0, 85).Select(i => new RogKeyColor((byte)i, new((byte)i, 7, 9))).ToArray();
            await bridge.WriteKeysAsync(colors, transport == 1 ? RogPerKeyLighting.WiredTransport : RogPerKeyLighting.WirelessTransport,
                default).WaitAsync(Patience);
            wire.Requests.Where(p => p[1] == 0xC0).Should().HaveCount(7);
            factory.Opens.Should().Be(1); wire.MaximumReaders.Should().Be(1); rgbState.Applied.Should().BeEmpty();
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        }
        finally { await owner.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private sealed class RealTransportCodexRig : IAsyncDisposable
    {
        public SourceClock Time { get; } = new();
        public Input Input { get; }
        public CodexWireChannel Wire { get; }
        public IRogOledConnection Connection { get; }
        public RogNativeMenuSource Source { get; }
        public RogNativeMenuSource.Actor Actor { get; }
        public List<int> Browses { get; } = [];
        public List<NativeMenuSourceEvent> Events { get; } = [];
        private readonly byte _transport;
        private readonly IDisposable _registration;
        public bool Present { get; set; } = true;
        public bool AbsenceProof { get; set; } = true;
        public RealTransportCodexRig(byte transport)
        {
            _transport = transport; Input = new(Time, transport); Wire = new(transport);
            var factory = new CodexActualTransportFactory(Policy(transport), Wire, Time, () => Present, () => !Present && AbsenceProof);
            Connection = transport == 1 ? factory.TryOpen(Policy(transport).DeviceKey)! : factory.TryOpen(Policy(transport).DeviceKey, Policy(transport).FirmwareHeader)!;
            Source = new(Policy(transport), Input, Input, Time, () => Cookie, codexTaskBrowse: Browses.Add);
            _registration = Source.Connect(Events.Add); Source.RequestCodexEnabled(true);
            Actor = Source.Attach(Connection); Input.Admitted = () => Actor.ReceivedNotifications;
        }
        public async Task SendAsync(ushort sequence, byte mask, byte flags = 1)
        {
            var notice = Notice(sequence, mask, flags, _transport); notice[4] = 10;
            BinaryPrimitives.WriteUInt32LittleEndian(notice.AsSpan(7), Wire.Epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(notice.AsSpan(11), Wire.CookieValue);
            var before = Actor.ReceivedNotifications; Input.Send(notice); await Until(() => Actor.ReceivedNotifications > before);
        }
        public async ValueTask DisposeAsync() { await Actor.DisposeAsync(); _registration.Dispose(); Input.Dispose(); Connection.Dispose(); }
    }

    private sealed class CodexActualTransportFactory(KeyboardNativeMenuOptions policy, CodexWireChannel wire, TimeProvider time,
        Func<bool>? present = null, Func<bool>? absence = null)
        : IRogWiredOledFactory, IRogWirelessOledFactory
    {
        public int Opens { get; private set; }
        private HidCollectionInfo Control => new(policy.ConnectionKind == "wired" ? "fake#mi_01#control" : "fake#mi_02#control", 0x0B05,
            policy.ConnectionKind == "wired" ? (ushort)0x1C24 : (ushort)0x1ACE, policy.ConnectionKind == "wired" ? 1 : 2,
            0xFF00, 1, policy.ConnectionKind == "wired" ? 65 : 64, policy.ConnectionKind == "wired" ? 65 : 64, 0);
        public RogWiredOledConnection? TryOpen(string deviceKey)
        {
            if (policy.ConnectionKind != "wired") return null;
            deviceKey.Should().Be(policy.DeviceKey); Opens++;
            return new(wire, () => present?.Invoke() ?? true, () => deviceKey, () => policy.FirmwareHeader)
            { NativeControlBinding = new([Control], null, time.GetTimestamp()), RouteAbsentProbe = () => absence?.Invoke() ?? present?.Invoke() == false };
        }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            if (policy.ConnectionKind != "wireless") return null;
            receiverKey.Should().Be(policy.DeviceKey); Opens++;
            return new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), wire, firmwareHeader, () => present?.Invoke() ?? true)
            { NativeControlBinding = new([Control], new(0x1C25, 2, 4), time.GetTimestamp()), EvidenceTime = time, KeyReportDelayMs = 0,
                RouteAbsentProbe = () => absence?.Invoke() ?? present?.Invoke() == false };
        }
    }

    // The sole fake control seam is the raw HID channel. The Host always receives real wired/RF connection classes.
    // This is an independent packet model, not a fake IRogOledConnection or a call-through to that fake.
    private sealed class CodexWireChannel(byte transport) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _pixels = [];
        private readonly RogKeyboardNativeState _bank = RogProtocol.KeyboardColourFrame(new(1, 2, 3), 25, 80);
        private ushort _remaining;
        private byte _software;
        private bool _ready;
        public uint Epoch { get; private set; } = 1;
        public ulong CookieValue { get; private set; }
        public int Arms { get; private set; }
        public int Renewals { get; private set; }
        public int LastCopyLength { get; private set; }
        public int MaximumReaders { get; private set; }
        public int PendingReads { get; private set; }
        public string? CommitFault { get; set; }
        public bool PixelNegative { get; set; }
        public bool DuplicateReadyEcho { get; set; }
        public int DuplicateEchoesRead { get; private set; }
        private readonly HashSet<byte[]> _duplicateEchoes = [];
        private int _duplicatedImages;
        public List<byte[]> Requests { get; } = [];
        public List<byte[]> Images { get; } = [];
        public List<string> Operations { get; } = [];
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); report.Length.Should().Be(transport == 1 ? 65 : 64); report.Span[0].Should().Be(transport == 1 ? (byte)0 : (byte)2);
            var p = new byte[65]; report.Span[1..].CopyTo(p.AsSpan(1)); Requests.Add(report.ToArray());
            var reply = new byte[65]; p.AsSpan(0, 5).CopyTo(reply);
            switch (p[1], p[2])
            {
                case (0x12, 0x7D):
                    Operations.Add(_ready ? "CAP_READY" : "CAP"); reply = Cap(0x12);
                    if (_ready) switch (CommitFault)
                    {
                        case "ready": reply[14] &= 0xF7; break;
                        case "cookie": reply[19] ^= 1; break;
                        case "epoch": reply[15] ^= 1; break;
                        case "transport": reply[13] = transport == 1 ? (byte)2 : (byte)1; break;
                    }
                    break;
                case (0x71, 0x7D):
                    p[24].Should().Be(transport); p[25].Should().Be(10);
                    BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(19)).Should().Be(Epoch);
                    Epoch++; CookieValue = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(11)); _ready = false; _remaining = 0;
                    if (p[10] == 1) Arms++; Operations.Add(p[10] == 1 ? "ARM" : "DISARM"); reply = Cap(0x71); break;
                case (0x67, 0):
                    CookieValue.Should().NotBe(0); _remaining = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(7));
                    _remaining.Should().Be(transport == 1 ? (ushort)111 : (ushort)113);
                    _pixels.Clear(); _ready = false; Operations.Add("BEGIN"); break;
                case (0x67, 1):
                    CookieValue.Should().NotBe(0); _remaining.Should().BeGreaterThan(0);
                    BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3)).Should().Be((ushort)(_remaining - 1));
                    if (PixelNegative && p[3] == 25)
                    { reply = new byte[65]; reply[1] = 0xFF; reply[2] = 0xAA; Epoch++; CookieValue = 0; _remaining = 0; break; }
                    LastCopyLength = Math.Min(transport == 1 ? 60 : 59, 6656 - _pixels.Count);
                    _pixels.AddRange(p.AsSpan(5, LastCopyLength).ToArray()); _remaining--;
                    if (_remaining == 0) { _ready = _pixels.Count == 6656; Images.Add(_pixels.ToArray()); }
                    Operations.Add("PIXEL"); break;
                case (0x67, 2): _ready.Should().BeTrue(); Operations.Add("RENEW"); Renewals++; break;
                case (0x12, 0):
                    if (transport == 2) { reply[5] = 8; reply[7] = 7; reply[12] = 0x25; reply[14] = 1; }
                    else { reply[5] = 0x25; reply[7] = 1; }
                    reply[9] = 6; reply[11] = 6; break;
                case (0x21, 0): reply[5] = 3; break;
                case (0x23, 2): reply[5] = 1; break;
                case (0x23, 3): reply[5] = 100; break;
                case (0x25, 0x0E): Convert.FromHexString(_bank.Payloads[RogProtocol.KeyboardPayloadLengths(0).Count - 1 - p[4]]).CopyTo(reply, 5); break;
                case (0x27, 0): reply[5] = _software; break;
                case (0x74, 0): _software = p[5]; reply[5] = _software; break;
                case (0xC0, 0x81): reply[2] = 0x82; break;
                case (0x51, 0x2C): case (0x68, 0): reply = p; break;
                case (0x61, 0): break;
                default: throw new InvalidOperationException("Unexpected raw test packet.");
            }
            var wire = transport == 1 ? reply : new byte[64];
            if (transport == 2) { wire[0] = 2; reply.AsSpan(1, 63).CopyTo(wire.AsSpan(1)); }
            _replies.Writer.TryWrite(wire).Should().BeTrue();
            if (DuplicateReadyEcho && p[1] == 0x12 && p[2] == 0x7D && _ready && Images.Count > _duplicatedImages)
            {
                _duplicatedImages = Images.Count;
                var duplicate = wire.ToArray(); _duplicateEchoes.Add(duplicate); _replies.Writer.TryWrite(duplicate).Should().BeTrue();
            }
            return ValueTask.CompletedTask;
        }
        private byte[] Cap(byte command)
        {
            var p = new byte[65]; p[1] = command; p[2] = 0x7D; "GYM1"u8.CopyTo(p.AsSpan(5));
            p[9] = 2; p[10] = CookieValue == 0 ? (byte)0 : (byte)1; p[11] = 10; p[13] = transport;
            p[14] = (byte)(0x70 | (CookieValue == 0 ? 0 : 1) | (_remaining == 0 ? 0 : 4) | (_ready ? 8 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), Epoch); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), CookieValue);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), 0x25314D50); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208);
            p[35] = 64; p[36] = transport == 1 ? (byte)60 : (byte)59; p[37] = 3; return p;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            MaximumReaders = Math.Max(MaximumReaders, ++PendingReads);
            try
            {
                var p = await _replies.Reader.ReadAsync(ct);
                if (_duplicateEchoes.Remove(p)) DuplicateEchoesRead++;
                p.CopyTo(buffer); return p.Length;
            }
            finally { PendingReads--; }
        }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
