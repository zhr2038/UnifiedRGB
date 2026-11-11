using System.Reflection;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    [Theory]
    [InlineData("image")] [InlineData("lease")]
    public async Task Ephemeral_presentation_retirement_starts_the_fresh_owner_with_BasicInfo_and_waits_without_CAP_or_settings(string family)
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        using var first = new PresentationFailureConnection(family, ObservationJournal, 6);
        var replacement = new PresentationSleepingFactory(first);
        using var service = PresentationService(replacement, gate);
        using var stop = new CancellationTokenSource();
        var run = RunPresentationOwner(service, first, stop.Token);
        try
        {
            await WaitAsync(service, "Waiting");
            first.Disposed.Should().BeTrue(); first.CallsAfterFailure.Should().Be(0);
            replacement.OpenBeforeOldDisposed.Should().BeFalse();
            replacement.Channels.Should().ContainSingle();
            var channel = replacement.Channels[0]; channel.Disposed.Should().BeTrue();
            channel.Requests.Should().ContainSingle();
            Convert.ToHexString(channel.Requests[0].AsSpan(0, 5)).Should().Be("0212000000",
                "a sleeping peer must first pass a fresh read-only BasicInfo snapshot; no pre-snapshot native CAP is sent");
            File.ReadAllBytes(ObservationJournal).Should().Equal(first.JournalAtFailure!);
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        }
        finally
        {
            stop.Cancel();
            await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task Ephemeral_native_lease_profile_mismatch_cannot_reopen_or_restore_a_different_record()
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        using var first = new PresentationFailureConnection("lease", ObservationJournal, 5);
        var replacement = new PresentationSleepingFactory(first);
        using var service = PresentationService(replacement, gate);
        var error = await Record.ExceptionAsync(() => RunPresentationOwner(service, first, default));
        error.Should().BeOfType<AggregateException>().Which.Message.Should().Contain("no matching recorded recovery scope");
        replacement.Channels.Should().BeEmpty(); first.Disposed.Should().BeTrue(); first.CallsAfterFailure.Should().Be(0);
        File.ReadAllBytes(ObservationJournal).Should().Equal(first.JournalAtFailure!);
    }

    private KeyboardDisplayService PresentationService(PresentationSleepingFactory replacement, PeripheralConfigurationGate gate) =>
        new(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = WirelessPinned() with
            { Enabled = false, WirelessLightingVerified = true } },
            DispatchProxy.Create<IRgbService, ReadProxy>(), DispatchProxy.Create<IScreenService, ReadProxy>(), new Factory(),
            new ClearGuard(), wirelessFactory: replacement, configurationGate: gate, wiredRoutePresent: _ => false);

    private static Task RunPresentationOwner(KeyboardDisplayService service, IRogOledConnection first, CancellationToken ct) =>
        (Task)typeof(KeyboardDisplayService).GetMethod("RunOwnerWithObservationRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [first, ct])!;

    // Host lifecycle seam only: the actual source/session producer is separately tested through the real RF envelope.
    private sealed class PresentationFailureConnection(string family, string journal, byte reportedProfile) : IRogOledConnection
    {
        private readonly RogWirelessOledConnection _inner = new(new(Key, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()),
            new WirelessIo(), "0800070024000100", () => true);
        private int _brightnessReads;
        private bool _failed;
        public bool Disposed { get; private set; }
        public int CallsAfterFailure { get; private set; }
        public byte[]? JournalAtFailure { get; private set; }
        public string DeviceKey => _inner.DeviceKey;
        public string FirmwareHeader => _inner.FirmwareHeader;
        public string ConnectionKind => "wireless";
        public int PixelPayloadCapacity => 59;
        public bool SameDevice() => true;
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) => _inner.RequireFirmwareIdentity(identity, expectedHeader);
        public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
        {
            if (_failed) { CallsAfterFailure++; throw new IOException("No old-owner I/O after presentation retirement."); }
            if (request[1] == 0x23 && request[2] == 3 && ++_brightnessReads == 3)
            {
                JournalAtFailure = File.ReadAllBytes(journal); _failed = true;
                if (family == "image") throw new RogNativeArtworkUploadRefusedException(Key, "wireless", null,
                    new IOException("Synthetic image abort status failure"), "image-abort-status");
                throw (RogNativePresentationUnavailableException)Activator.CreateInstance(typeof(RogNativePresentationUnavailableException),
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [Key, reportedProfile, "native-lease", new IOException("Synthetic native lease failure")], null)!;
            }
            return _inner.ExchangeAsync(request, zeroAck, ct);
        }
        public Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct) => _inner.LeaseAsync(changed, renewed, ct);
        public void Dispose() { Disposed = true; _inner.Dispose(); }
    }

    private sealed class PresentationSleepingFactory(PresentationFailureConnection first) : IRogWirelessOledFactory
    {
        public bool OpenBeforeOldDisposed { get; private set; }
        public List<PresentationSleepingChannel> Channels { get; } = [];
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            OpenBeforeOldDisposed |= !first.Disposed;
            var channel = new PresentationSleepingChannel(); Channels.Add(channel);
            return new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), channel, firmwareHeader, () => true);
        }
    }
    private sealed class PresentationSleepingChannel : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public bool Disposed { get; private set; }
        public List<byte[]> Requests { get; } = [];
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var request = report.ToArray(); Requests.Add(request);
            var reply = new byte[64]; reply[0] = 2;
            if (request[1] == 0x12 && request[2] == 0)
            {
                reply[1] = 0x12; reply[5] = 8; reply[7] = 7; reply[9] = 6; reply[11] = 6;
            }
            else { reply[1] = 0xFF; reply[2] = 0xAA; }
            _replies.Writer.TryWrite(reply).Should().BeTrue(); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length;
        }
        public void Dispose() { Disposed = true; _replies.Writer.TryComplete(); }
    }
}
