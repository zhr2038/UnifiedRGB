using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
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
    public Task ArtworkRefusal_mid_chunk_keeps_same_owner_and_selects_the_current_position(bool cleared) =>
        SameOwnerImageAsync(cleared, false);

    [Fact]
    public Task LocalImageAbort_old_native_ACK_and_negative_at_11ms_are_drained_before_fresh_CAP() => SameOwnerImageAsync(true, true);

    private static async Task SameOwnerImageAsync(bool cleared, bool delayed)
    {
        await using var rig = new ArtworkRefusalRig("chunk");
        rig.Factory.DelayNativeReplies = delayed;
        rig.Factory.OnRefusal = () =>
        {
            rig.JournalAtFault = File.ReadAllBytes(rig.Journal); rig.Timeline.At(2500);
            rig.Input.Send(Notice(1, 0, 3, 2)); rig.Input.Send(Notice(2, 2, 1, 2));
            if (cleared)
            {
                rig.Control.Epoch += 2;
                typeof(Connection).GetField("_cookie", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Control, 0UL);
            }
        };
        await rig.StartAsync();
        await Until(() => rig.Control.ImageCommits == 1 && rig.Control.Arms == 2 && rig.Source.IsPresent
            || rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).State.Should().NotBe("Error", "old native traffic belongs to the aborted image, before a fresh CAP");
        rig.Factory.Channels.Should().ContainSingle(); rig.Factory.OpenWhileInputLive.Should().BeFalse();
        rig.Factory.Channels[0].Disposed.Should().BeFalse(); rig.Input.Opens.Should().Be(2); rig.Input.LiveHandles.Should().Be(1);
        if (delayed) rig.Factory.Channels[0].LateRepliesAtMs.Should().BeGreaterThanOrEqualTo(10);
        rig.Control.ArmWhileInputReading.Should().BeFalse();
        rig.Control.Disarms.Should().Be(cleared ? 0 : 1, "only the stillmatching armed scope needs a shared-RX DISARM");
        rig.Control.Rebuilt.Should().Equal(KeyboardDisplayServiceTests.Fill(0x33), "current2500ms position, never the failed1500ms frame");
        var after = rig.Factory.Channels[0].Requests.SkipWhile(p => !(p[1] == 0x67 && p[2] == 1 && p[3] == 25)).Skip(1).ToArray();
        after.Should().NotContain(p => new byte[] { 0x61, 0x68, 0x74, 0x51 }.Contains(p[1]));
        File.ReadAllBytes(rig.Journal).Should().Equal(rig.JournalAtFault!); File.ReadAllBytes(rig.Preference).Should().Equal(rig.PreferenceBefore);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        await rig.Bridge.WriteAsync(new(7, 8, 9), default).WaitAsync(Patience);
        rig.Control.Colours.Should().Contain(new UnifiedRgb.Devices.Rgb(7, 8, 9));
        foreach (var p in new[] { Notice(1, 0, 3, 2), Notice(2, 2, 1, 2) })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), rig.Control.Epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), Cookie + 1); rig.Input.Send(p);
        }
        await Until(() => rig.Events.OfType<NativeMenuPress>().Count() == 1);
    }

    [Theory]
    [InlineData("arm")] [InlineData("brightness")] [InlineData("software")]
    [InlineData("join")] [InlineData("profile")] [InlineData("malformed")]
    public async Task ArtworkRefusal_unknown_nonimage_faults_failed_join_and_changed_profile_remain_terminal(string operation)
    {
        await using var rig = new ArtworkRefusalRig(operation);
        if (operation == "join") rig.Factory.OnRefusal = () =>
        {
            var actor = typeof(RogNativeMenuSource).GetField("_actor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig.Source)!;
            typeof(RogNativeMenuSource.Actor).GetField("_joinFailed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(actor, true);
        };
        if (operation == "profile") rig.Factory.OnRefusal = () => rig.Control.Profile = 1;
        await rig.StartAsync();
        await Until(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
        rig.Factory.Channels.Should().ContainSingle();
        if (operation is "arm" or "brightness" or "software" or "malformed") rig.Factory.Channels[0].AfterFaultWrites.Should().Be(0);
        File.Exists(rig.Journal).Should().BeTrue(); File.ReadAllBytes(rig.Preference).Should().Equal(rig.PreferenceBefore);
    }

    [Theory]
    [InlineData("cap")]
    [InlineData("replacement-cap")]
    public async Task ArtworkRefusal_read_only_CAP_retires_the_owner_and_waits_if_the_replacement_is_unavailable(string operation)
    {
        await using var rig = new ArtworkRefusalRig(operation);
        await rig.StartAsync();
        await Until(() => rig.Factory.Channels.Count >= 2 && rig.Factory.Channels[0].Disposed);
        if (operation == "cap")
        {
            await Until(() => rig.Source.IsPresent);
            (await rig.Owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            rig.Factory.Channels[0].AfterFaultWrites.Should().Be(0);
        }
        else
        {
            await Until(() => rig.Factory.Channels[1].Disposed
                && rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Waiting");
            rig.Factory.Channels[1].Requests.Should().NotContain(p => IsPersistentSetting(p) || p[1] == 0x71);
            rig.Source.IsPresent.Should().BeFalse();
        }

        rig.Factory.OpenWhilePreviousLive.Should().BeFalse();
        rig.Factory.OpenWhileInputLive.Should().BeFalse();
        File.Exists(rig.Journal).Should().BeTrue();
        File.ReadAllBytes(rig.Preference).Should().Equal(rig.PreferenceBefore);
    }

    private sealed class ArtworkRefusalRig : IAsyncDisposable
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "native-artwork-refusal-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable _registration;
        private readonly PeripheralConfigurationGate _gate = new(new(false, null));
        public Input Input { get; }
        public Connection Control { get; }
        public RogNativeMenuSource Source { get; }
        public ArtworkRefusalFactory Factory { get; }
        public KeyboardDisplayService Owner { get; }
        public RogWiredLightingBridge Bridge { get; } = new();
        public FakeSharedTimeline Timeline { get; } = new();
        public ConcurrentQueue<NativeMenuSourceEvent> Events { get; } = new();
        public string Journal => Path.Combine(_data, "peripherals", "keyboard-display-session.json");
        public string Preference => Path.Combine(_data, "peripherals", KeyboardDisplayAnimation.FileName);
        public byte[] PreferenceBefore { get; } = KeyboardDisplayServiceTests.AnimationFile(enabled: true);
        public byte[]? JournalAtFault { get; set; }
        public ArtworkRefusalRig(string operation)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Preference)!); File.WriteAllBytes(Preference, PreferenceBefore);
            var time = new SourceClock(); Input = new(time, 2); Control = new(time, 2) { Input = Input };
            Factory = new(Control, Input, time, operation);
            ulong cookie = Cookie; Source = new(Policy(2), Input, Input, time, () => cookie++);
            _registration = Source.Connect(Events.Enqueue); Source.RequestEnabled(true);
            var media = new FakeKeyboardOledMedia { Scene = new([new("screen:h2:test", KeyboardDisplayServiceTests.AssetA,
                TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]) };
            Timeline.At(1500);
            var host = new MenuHostFake(new([new([new("lianli:1", 0)], "Rainbow", ["#FF0000"])]));
            Owner = new(new UnifiedRgbOptions { DataDirectory = _data, KeyboardDisplay = new() {
                WirelessControlVerified = true, WirelessLightingVerified = true, WirelessReceiverKey = Control.DeviceKey,
                WirelessFirmwareHeader = Control.FirmwareHeader, AnimationAdmitted = true, DeviceShowsNativeEntryLogo = true } },
                host.Connection.Rgb, KeyboardAnimationScreens.Create(media), new NoOled(), new MutationClearGuard(), Bridge,
                wirelessFactory: Factory, configurationGate: _gate, nativeMenu: Source, keyboardMedia: media, sharedTimeline: Timeline,
                nativePresentation: new DeclaredPresentation(KeyboardNativePresentationSupport.Available));
            if (operation is "brightness" or "software")
            {
                var bank = RogProtocol.KeyboardColourFrame(new(1, 2, 3), 25, 80) with { SoftwareControl = 1 };
                File.WriteAllBytes(Journal, JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = Control.DeviceKey,
                    FirmwareHeader = Control.FirmwareHeader, Brightness = (byte)100, ProfileToken = (byte)6,
                    Lighting = operation == "software" ? bank : null, StaticBank = operation == "software" ? bank : null }));
            }
        }
        public async Task StartAsync()
        {
            await Owner.GetKeyboardDisplayAnimationAsync();
            await Until(() => Owner.GetKeyboardDisplayAnimationAsync().GetAwaiter().GetResult().CachedFrames == 3);
            await Owner.StartAsync(default);
        }
        public async ValueTask DisposeAsync()
        {
            await Owner.StopAsync(default); Owner.Dispose(); _registration.Dispose(); Input.Dispose(); Control.Dispose(); _gate.Dispose();
            if (Directory.Exists(_data)) Directory.Delete(_data, true);
        }
    }

    private sealed class ArtworkRefusalFactory(Connection control, Input input, TimeProvider time, string operation) : IRogWirelessOledFactory
    {
        public List<ArtworkRefusalChannel> Channels { get; } = [];
        public bool OpenWhilePreviousLive { get; private set; }
        public bool OpenWhileInputLive { get; private set; }
        public Action? OnRefusal { get; set; }
        public Func<CancellationToken, Task>? BeforeRefusal { get; set; }
        public bool SoftwareOn { get; init; }
        public bool DelayNativeReplies { get; set; }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            OpenWhilePreviousLive |= Channels.Count > 0 && !Channels[^1].Disposed; OpenWhileInputLive |= input.LiveHandles != 0;
            var channel = new ArtworkRefusalChannel(control, Channels.Count == 0 ? operation : operation == "replacement-cap" ? "cap" : "none", () => OnRefusal?.Invoke(),
                operation == "profile" && Channels.Count > 0, Channels.Count > 0, SoftwareOn, DelayNativeReplies, BeforeRefusal); Channels.Add(channel);
            return new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), channel, firmwareHeader, () => true)
            { NativeControlBinding = control.NativeControlBinding, EvidenceTime = time };
        }
    }

    private sealed class ArtworkRefusalChannel(Connection control, string operation, Action refused, bool changedProfile, bool replacement, bool softwareOn, bool delayed,
        Func<CancellationToken, Task>? beforeRefusal = null) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private bool _fault;
        private readonly CancellationTokenSource _stop = new();
        private Task? _late, _deferred;
        private long _nackWriteAt;
        public double LateRepliesAtMs { get; private set; }
        public bool Disposed { get; private set; }
        public int AfterFaultWrites { get; private set; }
        public int PartialBytes { get; private set; }
        public int PendingReads { get; private set; }
        public int MaximumReaders { get; private set; }
        public List<byte[]> Requests { get; } = [];
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            if (_fault) AfterFaultWrites++;
            var p = new byte[65]; report.Span[1..].CopyTo(p.AsSpan(1)); Requests.Add(report.ToArray());
            var fail = operation switch { "cap" => p[1] == 0x12 && p[2] == 0x7D, "arm" => p[1] == 0x71,
                "brightness" => p[1] == 0x68, "software" => p[1] == 0x74,
                "chunk" or "join" or "profile" or "malformed" or "replacement-cap" => !_fault && p[1] == 0x67 && p[2] == 1 && p[3] == 25, _ => false };
            if (operation == "replacement-cap" && _fault && p[1] == 0x12 && p[2] == 0x7D) fail = true;
            byte[] reply;
            if (fail)
            {
                if (beforeRefusal is not null) await beforeRefusal(ct);
                _fault = true; PartialBytes = control.Rebuilt.Length; refused(); reply = new byte[65]; reply[1] = 0xFF; reply[2] = 0xAA;
                if (operation == "malformed") reply[5] = 1;
            }
            else
            {
                if (p[1] == 0xC0 && p[2] == 0x81)
                { reply = new byte[65]; reply[1] = 0xC0; reply[2] = 0x82; reply[3] = p[3]; }
                else reply = await control.ExchangeAsync(p, false, ct);
                if (softwareOn && p[1] == 0x27) reply[5] = 1;
                if (replacement && Requests.Count == 1 && p[1] == 0x12 && p[2] == 0x7D) reply[14] = 0x35; // actual ARMED+RX, not the old ARM flags
                if (p[1] == 0x12 && p[2] == 0) { reply[5] = 8; reply[7] = 7; reply[12] = 0x25; reply[14] = 1; if (changedProfile || operation == "profile" && _fault) reply[11] = 5; }
            }
            var wire = new byte[64]; wire[0] = 2; reply.AsSpan(1, 63).CopyTo(wire.AsSpan(1));
            if (delayed && _fault && !fail && p[1] == 0x12 && p[2] == 0x7D && _late is { IsCompleted: false })
                _deferred = QueueFreshCapAfterOldRepliesAsync(wire);
            else _replies.Writer.TryWrite(wire).Should().BeTrue();
            if (fail && p[1] == 0x67 && operation != "malformed")
            {
                // Late ACK for an accepted chunk, late ACK for the written-but-rejected index, and another negative. These
                // must all be drained in the image role before Source sends its fresh CAP; none acknowledges a new frame.
                if (delayed) { _nackWriteAt = System.Diagnostics.Stopwatch.GetTimestamp(); _late = QueueOldRepliesAsync(); }
                else QueueOldReplies();
            }
        }
        private async Task QueueOldRepliesAsync()
        {
            using var timer = HidReadWindow.Start(TimeSpan.FromMilliseconds(11), _stop.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, timer.Token); }
            catch (OperationCanceledException) when (timer.Token.IsCancellationRequested) { }
            if (_stop.IsCancellationRequested) return;
            LateRepliesAtMs = System.Diagnostics.Stopwatch.GetElapsedTime(_nackWriteAt).TotalMilliseconds;
            QueueOldReplies();
        }
        private void QueueOldReplies()
        {
            foreach (var index in new byte[] { 26, 25 })
            { var late = new byte[64]; late[0] = 2; late[1] = 0x67; late[2] = 1; late[3] = index; _replies.Writer.TryWrite(late); }
            var negative = new byte[64]; negative[0] = 2; negative[1] = 0xFF; negative[2] = 0xAA; _replies.Writer.TryWrite(negative);
        }
        private async Task QueueFreshCapAfterOldRepliesAsync(byte[] wire)
        { await _late!; if (!_stop.IsCancellationRequested) _replies.Writer.TryWrite(wire); }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            MaximumReaders = Math.Max(MaximumReaders, ++PendingReads);
            try { var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
            finally { PendingReads--; }
        }
        public void Dispose()
        {
            Disposed = true; _stop.Cancel(); _late?.GetAwaiter().GetResult(); _deferred?.GetAwaiter().GetResult();
            _replies.Writer.TryComplete(); _stop.Dispose();
        }
    }
}
