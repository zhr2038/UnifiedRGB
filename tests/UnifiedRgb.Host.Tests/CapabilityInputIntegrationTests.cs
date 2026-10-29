using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class CapabilityInputIntegrationTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const ulong Cookie = 0x2233445566778899;
    private static readonly string RfFamily = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;

    private static readonly HidCollectionInfo ReceiverTarget = Info("mi_03&col04", 3, 0xFFC0, 21, 0, 0);
    private static readonly HidCollectionInfo ReceiverFfc1 = Info("mi_03&col05", 3, 0xFFC1, 21, 0, 0);
    private static readonly HidCollectionInfo ReceiverFfc2 = Info("mi_03&col03", 3, 0xFFC2, 21, 0, 0);
    private static readonly HidCollectionInfo[] ReceiverControls =
    [
        Info("mi_02&col01", 2, 0xFF02, 64, 64, 0),
        Info("mi_02&col02", 2, 0xFF00, 64, 64, 0),
        Info("mi_02&col03", 2, 0xFF01, 64, 64, 0),
    ];

    [Theory]
    [InlineData(0x24, 0x24314D51u, "GYM1:25314D51", "", "", "")]
    [InlineData(0x25, 0x25444342u, "stale build note", "zz", "unknown-contract", "not-a-header")]
    [InlineData(0x26, 0x26000000u, "", "", "", "0800070024000100")]
    public async Task Real_owner_and_actor_use_live_capability_not_diagnostic_configuration(
        byte oem, uint liveTag, string configuredBuild, string image, string contract, string configuredHeader)
    {
        var time = new LogicalClock();
        await using var rig = new Rig(time, Policy(configuredBuild, image, contract, configuredHeader), oem, liveTag);
        rig.Source.RequestEnabled(true);

        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();

        var paired = rig.Connection.NativeControlBinding!.PairingTimestamp;
        var evidence = rig.Connection.PairingEvidence!;
        evidence.Keyboard.Should().Be(new RogPairedDevice(0x1C25, 2, 4));
        evidence.PairingTimestamp.Should().Be(paired);
        evidence.Observations.Should().Be(2, "the validated CAP and ARM exchanges advance evidence immediately");
        evidence.Invalidated.Should().BeNull();
        var writes = rig.Channel.Writes;
        rig.Connection.PairingEvidence.Should().Be(evidence);
        rig.Connection.PairingEvidence.Should().Be(evidence);
        rig.Channel.Writes.Should().Be(writes, "repeated readonly getter reads perform no I/O or evidence advance");
        rig.Channel.Arms.Should().Be(1);
        rig.Inputs.Opened.Should().Equal(ReceiverTarget);
        rig.Inputs.Opened.Should().NotContain(ReceiverFfc1).And.NotContain(ReceiverFfc2);
        AssertRfImage(rig.Channel.ImageChunks, Pixels());
    }

    [Fact]
    public async Task Real_same_owner_observations_keep_actor_and_queued_center_press_usable_beyond_180_seconds()
    {
        var time = new LogicalClock();
        await using var rig = new Rig(time, Policy("arbitrary", "", "", ""));
        rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var initial = rig.Connection.PairingEvidence!;
        var provenance = initial.PairingTimestamp;

        time.Advance(TimeSpan.FromSeconds(181));
        rig.Inputs.Send(Notice(1, 0, 3));
        rig.Inputs.Send(Notice(2, 2, 1));
        await WaitUntil(() => rig.Actor.ReceivedNotifications >= 2);

        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();

        var evidence = rig.Connection.PairingEvidence!;
        evidence.PairingTimestamp.Should().Be(provenance);
        evidence.LastObservedTimestamp.Should().BeGreaterThan(provenance);
        evidence.Observations.Should().Be(3, "the existing per-tick CAP is the fresh observation");
        evidence.Invalidated.Should().BeNull();
        time.GetElapsedTime(provenance).Should().BeGreaterThan(TimeSpan.FromSeconds(180));
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle()
            .Which.Intent.Action.Should().Be(DesiredAction.Toggle);
        rig.Events.OfType<NativeMenuContextEnded>().Should().BeEmpty();
        rig.Channel.Arms.Should().Be(1);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("geometry")]
    [InlineData("profile")]
    public async Task Real_owner_invalidates_unsupported_live_capability_and_actor_does_not_ARM(string fault)
    {
        var time = new LogicalClock();
        await using var rig = new Rig(time, Policy("", "", "", ""));
        rig.Channel.CapabilityFault = fault;
        rig.Source.RequestEnabled(true);

        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();

        rig.Connection.PairingEvidence!.Invalidated.Should().Be("native capability grammar differs");
        rig.Channel.Arms.Should().Be(0);
        rig.Inputs.Enumerations.Should().Be(0);
        rig.Inputs.Opened.Should().BeEmpty();
        rig.Events.Should().BeEmpty();
        var writes = rig.Channel.Writes;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Channel.Writes.Should().Be(writes, "an unsupported generation is not probed in a loop");
    }

    [Fact]
    public async Task Default_off_performs_no_real_owner_exchange_evidence_advance_or_input_discovery()
    {
        var time = new LogicalClock();
        await using var rig = new Rig(time, Policy("GYM1:25314D51", "", "", ""));

        for (var i = 0; i < 3; i++)
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();

        rig.Channel.Writes.Should().Be(0);
        rig.Channel.Arms.Should().Be(0);
        rig.Inputs.Enumerations.Should().Be(0);
        rig.Inputs.Opened.Should().BeEmpty();
        rig.Connection.PairingEvidence!.Observations.Should().Be(0);
        rig.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Actor_presses_drive_existing_center_coordinator_in_FIFO_with_fresh_scene_decisions()
    {
        var time = new LogicalClock();
        var host = new MenuHostFake(Running());
        await using var actions = new NativeMenuCaseActionCoordinator(() => host.Connection, time);
        await using var rig = new Rig(time, Policy("", "", "", ""), sink: e =>
        {
            switch (e)
            {
                case NativeMenuContextBegun begun:
                    actions.Begin(begun.SessionId, begun.ContextGeneration, begun.ProfileGeneration, begun.SequenceFloor);
                    break;
                case NativeMenuContextEnded ended:
                    actions.Invalidate(ended.SessionId, ended.ContextGeneration, ended.Disconnected);
                    break;
                case NativeMenuPress press:
                    actions.Submit(press.Intent);
                    break;
            }
        });
        rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();

        byte[] masks = [0, 2, 2, 2, 0, 2, 0, 1, 0, 4];
        for (ushort n = 0; n < masks.Length; n++)
            rig.Inputs.Send(Notice((ushort)(n + 1), masks[n], n == 0 ? (byte)3 : (byte)1));
        await WaitUntil(() => rig.Actor.ReceivedNotifications >= masks.Length);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        await WaitUntil(() => host.Applied.Length == 2);

        var presses = rig.Events.OfType<NativeMenuPress>().Select(e => e.Intent).ToArray();
        presses.Select(p => p.Sequence).Should().Equal(2, 6);
        presses.Select(p => p.NeutralArmSequence).Should().Equal(1, 5);
        host.Applied.Select(CaseStatusSummary.IsBlackout).Should().Equal(true, false);
        host.Calls.Count(c => c.StartsWith("SetScene#", StringComparison.Ordinal)).Should().Be(2);
        host.MaxConcurrentCalls.Should().Be(1);
        CaseStatusSummary.IsBlackout(host.Scene).Should().BeFalse();
    }

    private static KeyboardNativeMenuOptions Policy(string build, string image, string contract, string header) => new()
    {
        Enabled = true,
        SourceControlNotifications = true,
        NativeBuild = build,
        CandidateSha256 = image,
        ContractSha256 = contract,
        DeviceKey = Key,
        ConnectionKind = "wireless",
        FirmwareHeader = header,
    };

    private static HidCollectionInfo Info(string mi, int iface, ushort usagePage, int input, int output, int feature) =>
        new($@"\\?\hid#vid_0b05&pid_1ace&{mi}#synthetic#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            0x0B05, 0x1ACE, iface, usagePage, 1, input, output, feature);

    private static SceneDto Running() => new([new([new("lianli:1", 0)], "StaticColor", ["#FF0000"])]);

    private static byte[] Pixels() => Enumerable.Range(0, RogWiredOledProtocol.PixelBytes)
        .Select(i => (byte)(1 + (i * 73 + i / 251) % 255)).ToArray();

    private static byte[] Notice(ushort sequence, byte mask, byte flags)
    {
        var p = new byte[21];
        p[0] = 4;
        p[1] = 0x72;
        p[2] = mask;
        p[3] = 0xD5;
        p[4] = RogNativeMenuCodec.NativeMode;
        p[6] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), Cookie);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(19), sequence);
        return p;
    }

    private static void AssertRfImage(IReadOnlyList<byte[]> chunks, byte[] pixels)
    {
        chunks.Should().HaveCount(113);
        var rebuilt = chunks.SelectMany((p, i) => p.AsSpan(5, i == 112 ? 48 : 59).ToArray()).ToArray();
        rebuilt.Should().Equal(pixels);
        chunks[^1].AsSpan(53).ToArray().Should().OnlyContain(b => b == 0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly IDisposable _registration;

        public Rig(LogicalClock time, KeyboardNativeMenuOptions options, byte oem = 0x25, uint liveTag = 0x25314D49,
            Action<NativeMenuSourceEvent>? sink = null)
        {
            Inputs = new(time);
            Channel = new(oem, liveTag);
            var paired = time.GetTimestamp();
            Connection = new(new RogReceiver(Key, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), Channel, RfFamily, () => true)
            {
                NativeControlBinding = new(ReceiverControls, new(0x1C25, 2, 4), paired),
                EvidenceTime = time,
            };
            Source = new(options, Inputs, Inputs, time, () => Cookie);
            _registration = Source.Connect(e =>
            {
                Events.Enqueue(e);
                sink?.Invoke(e);
            });
            Actor = Source.Attach(Connection);
        }

        public InputWorld Inputs { get; }
        public RfChannel Channel { get; }
        public RogWirelessOledConnection Connection { get; }
        public RogNativeMenuSource Source { get; }
        public RogNativeMenuSource.Actor Actor { get; }
        public ConcurrentQueue<NativeMenuSourceEvent> Events { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Actor.DisposeAsync();
            _registration.Dispose();
            Connection.Dispose();
            Inputs.Dispose();
        }
    }

    private sealed class RfChannel(byte oem, uint liveTag) : IHidReportChannel
    {
        private readonly ConcurrentQueue<byte[]> _replies = new();
        private readonly SemaphoreSlim _ready = new(0);
        private uint _epoch = 1;
        private ulong _cookie;

        public string? CapabilityFault { get; set; }
        public int Writes { get; private set; }
        public int Arms { get; private set; }
        public List<byte[]> ImageChunks { get; } = [];

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Writes++;
            var wireless = report.ToArray();
            if (wireless.Length != 64 || wireless[0] != 2) throw new InvalidDataException("RID2/64 report required.");
            var request = new byte[65];
            wireless.AsSpan(1, 63).CopyTo(request.AsSpan(1));
            var reply = new byte[65];
            request.AsSpan(0, 5).CopyTo(reply);
            switch (request[1], request[2])
            {
                case (0x12, 0x7D):
                    reply = Capability(0x12);
                    break;
                case (0x71, 0x7D):
                    BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(19)).Should().Be(_epoch);
                    request[24].Should().Be(2);
                    if (request[10] == 1)
                    {
                        Arms++;
                        _cookie = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(11));
                    }
                    else _cookie = 0;
                    _epoch++;
                    reply = Capability(0x71);
                    break;
                case (0x67, 1):
                    ImageChunks.Add(request);
                    break;
                case (0x67, 0) or (0x67, 2):
                    break;
                default:
                    throw new InvalidOperationException("Unexpected synthetic owner command.");
            }
            var answer = new byte[64];
            answer[0] = 2;
            reply.AsSpan(1, 63).CopyTo(answer.AsSpan(1));
            _replies.Enqueue(answer);
            _ready.Release();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await _ready.WaitAsync(ct);
            if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException("No scripted reply.");
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() => _ready.Dispose();

        private byte[] Capability(byte command)
        {
            var p = new byte[65];
            p[1] = command;
            p[2] = 0x7D;
            "GYM1"u8.CopyTo(p.AsSpan(5));
            p[9] = 2;
            p[10] = _cookie == 0 ? (byte)0 : (byte)1;
            p[11] = RogNativeMenuCodec.NativeMode;
            p[12] = 0;
            p[13] = 2;
            p[14] = _cookie == 0 ? (byte)48 : (byte)49;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), _epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), _cookie);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), liveTag);
            p[32] = oem;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208);
            p[35] = 64;
            p[36] = 59;
            p[37] = 3;
            switch (CapabilityFault)
            {
                case "protocol": p[9] = 1; break;
                case "geometry": p[36] = 60; break;
                case "profile": p[12] = 6; break;
            }
            return p;
        }
    }

    private sealed class InputWorld : IHidInputReportDevices, IHidContainerResolver, IDisposable
    {
        private static readonly MethodInfo FromPlatform = typeof(HidPhysicalContainer)
            .GetMethod("FromPlatformContainerId", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly TimeProvider _time;
        private readonly Guid _container = Guid.NewGuid();
        private Handle? _handle;

        public InputWorld(TimeProvider time) => _time = time;

        public int Enumerations { get; private set; }
        public List<HidCollectionInfo> Opened { get; } = [];

        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid)
        {
            Enumerations++;
            vid.Should().Be(0x0B05);
            pid.Should().Be(0x1ACE);
            return [ReceiverFfc2, ReceiverTarget, ReceiverFfc1, .. ReceiverControls];
        }

        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection)
        {
            collection.Should().Be(ReceiverTarget);
            Opened.Add(collection);
            return _handle = new();
        }

        public HidContainerResolution Resolve(HidCollectionInfo collection) => HidContainerResolution.Resolved(
            (HidPhysicalContainer)FromPlatform.Invoke(null, [_container, _time.GetTimestamp()])!);

        public void Send(byte[] report) => (_handle ?? throw new InvalidOperationException("Input is not open."))
            .Reports.Writer.TryWrite(report).Should().BeTrue();

        public void Dispose() => _handle?.Dispose();

        private sealed class Handle : IHidInputReportHandle
        {
            public Channel<byte[]> Reports { get; } = Channel.CreateUnbounded<byte[]>();
            public HidInputDescriptor Describe() => new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, 0, 0, [4]);

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var report = await Reports.Reader.ReadAsync(ct);
                report.CopyTo(buffer);
                return report.Length;
            }

            public void Dispose() => Reports.Writer.TryComplete();
        }
    }

    private sealed class LogicalClock : TimeProvider
    {
        private long _timestampOffset;
        private long _utcOffset;

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + Interlocked.Read(ref _timestampOffset);
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _utcOffset));

        public void Advance(TimeSpan by)
        {
            Interlocked.Add(ref _timestampOffset, (long)(by.TotalSeconds * TimestampFrequency));
            Interlocked.Add(ref _utcOffset, by.Ticks);
        }
    }
}
