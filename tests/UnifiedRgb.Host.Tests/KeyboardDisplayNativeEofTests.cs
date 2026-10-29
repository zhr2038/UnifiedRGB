using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_CAP_EOF_only_verified_physical_removal_allows_ExecuteAsync_to_open_a_new_owner(bool physicallyRemoved)
    {
        const string header = "001200000025000100";
        var journal = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        byte[]? originalJournal = null;
        var factory = new NativeCapEofFactory(physicallyRemoved, () => originalJournal = File.ReadAllBytes(journal));
        var noInput = new NoNativeEofInput();
        var source = new RogNativeMenuSource(new()
        {
            Enabled = true, SourceControlNotifications = true, NativeBuild = "GYM1:25314D49",
            DeviceKey = Key, FirmwareHeader = header, ConnectionKind = "wired",
            CandidateSha256 = "0d7cd76d3056671bf6580196ca58041ab2417b824c980de2c9a3034d63341958",
            ContractSha256 = "b6ff1d41add209a95fb3dfebbcee7993aafbb1199b9ffb7a8551788028d4a585",
        }, noInput, noInput);
        source.RequestEnabled(true);
        source.Requested.Should().BeTrue("the actual native source must attempt CAP, not take its default-off path");
        var rgb = DispatchProxy.Create<IRgbService, ReadProxy>();
        var screens = DispatchProxy.Create<IScreenService, ReadProxy>();
        var options = new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = Pinned() with { FirmwareHeader = header } };
        using var service = new KeyboardDisplayService(options, rgb, screens, factory, new ClearGuard(), nativeMenu: source);
        await service.StartAsync(default);
        try
        {
            await factory.FirstCapEof.Task.WaitAsync(TimeSpan.FromSeconds(5));
            originalJournal.Should().NotBeNull("the owner recorded its exact original state before native CAP");
            factory.Channels[0].NativeCaps.Should().Be(1);
            factory.Channels[0].RawWritesAfterEof.Should().Be(0);
            if (physicallyRemoved)
            {
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (factory.Opens < 2 || factory.Channels[1].IdentityReads == 0) await Task.Delay(20, bound.Token);
                await WaitAsync(service, "Stopped");
                factory.Opens.Should().Be(2, "verified physical removal retains the existing one-second reconnect path");
                factory.Headers.Should().Equal(header, header);
                factory.Channels[0].Present.Should().BeFalse();
                factory.Channels[1].IdentityReads.Should().BeGreaterThan(0, "the replacement owner must freshly verify identity/profile pins");
                factory.Channels[1].Inner.Data.Should().Be(0, "installed native policy forbids legacy image67 on the replacement owner");
                factory.Channels[1].Inner.BeginPackets.Should().Be(0);
                var status = await service.GetKeyboardDisplayStatusAsync();
                status.Enabled.Should().BeTrue(); status.FramesAcknowledged.Should().Be(0); status.LastError.Should().BeNull();
                status.Detail.Should().Contain("原生光域菜单").And.Contain("偏好已保留");
                using var record = JsonDocument.Parse(File.ReadAllBytes(journal));
                record.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be(header);
                record.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
            }
            else
            {
                // Cross the reconnect delay in the real ExecuteAsync, with no toggle or explicit new request.
                await Task.Delay(1400);
                var status = await service.GetKeyboardDisplayStatusAsync();
                status.State.Should().Be("Error", "native wire-fault cleanup suppression is not proof that this present device was removed");
                status.LastError.Should().Contain("without confirmed physical removal").And.Contain("no automatic retry");
                factory.Opens.Should().Be(1);
                factory.Channels[0].Present.Should().BeTrue();
                File.ReadAllBytes(journal).Should().Equal(originalJournal!, "unknown outcome retains the exact original recovery journal");
            }
            factory.Channels[0].RawWritesAfterEof.Should().Be(0, "RecoveryBlocked still forbids cleanup I/O on the failed connection");
            noInput.Calls.Should().Be(0, "CAP failed before any vendor input collection can be bound or opened");
            ((ReadProxy)rgb).CaseMutations.Should().Be(0); ((ReadProxy)screens).ScreenMutations.Should().Be(0);
        }
        finally { await service.StopAsync(default); }
        if (!physicallyRemoved) File.ReadAllBytes(journal).Should().Equal(originalJournal!);
    }

    private sealed class NativeCapEofFactory(bool removeFirst, Action journalAtEof) : IRogWiredOledFactory
    {
        public int Opens { get; private set; }
        public List<NativeCapEofChannel> Channels { get; } = [];
        public List<string> Headers { get; } = [];
        public TaskCompletionSource FirstCapEof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("The exact actual25 firmware overload is required.");
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
        {
            deviceKey.Should().Be(Key); firmwareHeader.Should().Be("001200000025000100");
            Opens++; Headers.Add(firmwareHeader);
            var channel = new NativeCapEofChannel(Opens == 1, removeFirst, () => { journalAtEof(); FirstCapEof.TrySetResult(); });
            Channels.Add(channel);
            return new(channel, () => channel.Present, () => deviceKey, () => firmwareHeader);
        }
    }

    private sealed class NativeCapEofChannel(bool eofOnCap, bool removeOnEof, Action beforeEof) : IHidReportChannel
    {
        public Io Inner { get; } = new();
        public bool Present { get; private set; } = true;
        public int NativeCaps { get; private set; }
        public int IdentityReads { get; private set; }
        public int RawWritesAfterEof { get; private set; }
        private bool _eofPending, _eofObserved, _unsupportedCapPending;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
        {
            if (_eofObserved) RawWritesAfterEof++;
            if (packet.Span[1] == 0x12 && packet.Span[2] == 0x7D)
            {
                NativeCaps++;
                if (eofOnCap) { beforeEof(); _eofPending = true; }
                else _unsupportedCapPending = true; // A fresh unsupported CAP does not cancel installed native output ownership.
                return ValueTask.CompletedTask;
            }
            return Inner.WriteAsync(packet, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_eofPending)
            {
                _eofPending = false; _eofObserved = true; if (removeOnEof) Present = false;
                return 0; // The real atomic RogWiredOledConnection classifies and poisons this actual stream EOF.
            }
            if (_unsupportedCapPending)
            {
                _unsupportedCapPending = false; buffer.Span.Clear(); buffer.Span[1] = 0x12; buffer.Span[2] = 0x7D; return 65;
            }
            var count = await Inner.ReadAsync(buffer, ct);
            if (count == 65 && buffer.Span[1] == 0x12 && buffer.Span[2] == 0)
            { buffer.Span[5] = 0x25; IdentityReads++; }
            return count;
        }
        public void Dispose() => Inner.Dispose();
    }

    private sealed class NoNativeEofInput : IHidInputReportDevices, IHidContainerResolver
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) { Calls++; throw new InvalidOperationException("No input enumeration after CAP EOF."); }
        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection) { Calls++; throw new InvalidOperationException("No input open after CAP EOF."); }
        public HidContainerResolution Resolve(HidCollectionInfo collection) { Calls++; throw new InvalidOperationException("No container query after CAP EOF."); }
    }
}
