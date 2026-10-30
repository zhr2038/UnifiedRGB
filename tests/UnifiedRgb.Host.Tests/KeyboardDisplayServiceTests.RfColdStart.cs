using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fake-only RF cold start: an owner's first read-only identity answered by the receiver without keyboard firmware. Each open
/// is a fresh fake owner with its own pairing observation; nothing here describes physical receiver or keyboard timing.
/// </summary>
public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public async Task Rf_cold_not_ready_is_typed_only_for_a_well_framed_RF_identity_without_keyboard_firmware()
    {
        var cold = new ColdRfFactory { BlankOwners = 1 };
        using (var rf = cold.TryOpen(Key, "0000070000000100")!)
        {
            await ((Func<Task>)(() => new RogWiredOledSession(rf).SnapshotAsync(rf.FirmwareHeader, default))).Should().ThrowAsync<RogKeyboardNotReadyException>();
            rf.ObservedFirmwareHeader.Should().BeNull("the blank identity is never accepted");
            rf.PairingEvidence!.Invalidated.Should().Contain("keyboard identity unavailable"); rf.PairingEvidence.Observations.Should().Be(0);
            cold.Channels[0].Writes.Should().Be(1); cold.Channels[0].Io.Inner.Mutations.Should().Be(0);
        }
        // The same empty bytes [12..16) are ordinary in a wired BasicInfo reply.
        using var wired = new Factory().TryOpen(Key)!;
        (await new RogWiredOledSession(wired).SnapshotAsync(wired.FirmwareHeader, default)).Should().Be(25);
    }

    [Fact]
    public async Task Rf_cold_not_ready_keeps_resume_pending_and_succeeds_on_a_fresh_owner()
    {
        var cold = new ColdRfFactory();
        using var service = MakeCold(cold);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running"); cold.Opens.Should().Be(1);
            cold.BlankOwners = 2;
            await service.ResumeAfterPeripheralConfigurationAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            cold.Opens.Should().Be(4);
            foreach (var i in new[] { 1, 2 })
            {
                cold.Channels[i].Writes.Should().Be(1, "only the read-only identity query reached a blank owner");
                cold.Channels[i].Io.Inner.Mutations.Should().Be(0); cold.Channels[i].Disposed.Should().BeTrue();
                var evidence = cold.Owners[i].PairingEvidence!;
                evidence.Invalidated.Should().Contain("keyboard identity unavailable");
                evidence.Observations.Should().Be(0); evidence.LastObservedTimestamp.Should().Be(evidence.PairingTimestamp);
            }
            cold.Owners[3].PairingEvidence!.Invalidated.Should().BeNull(); cold.Owners[3].PairingEvidence!.Observations.Should().BePositive();
            await WaitAsync(service, "Running"); (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Rf_cold_not_ready_stops_after_three_fresh_owners_and_fails_the_pending_resume()
    {
        var cold = new ColdRfFactory();
        using var service = MakeCold(cold);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            cold.BlankOwners = int.MaxValue;
            await ((Func<Task>)(() => service.ResumeAfterPeripheralConfigurationAsync(default).WaitAsync(TimeSpan.FromSeconds(10))))
                .Should().ThrowAsync<RogKeyboardNotReadyException>();
            cold.Opens.Should().Be(5, "one attempt plus three fresh owners");
            var status = await service.GetKeyboardDisplayStatusAsync();
            status.State.Should().Be("Error"); status.LastError.Should().Contain(nameof(RogKeyboardNotReadyException));
            await Task.Delay(1300);
            cold.Opens.Should().Be(5, "no further loop after the attempts are exhausted");
            cold.Channels.Skip(1).Should().OnlyContain(c => c.Writes == 1 && c.Io.Inner.Mutations == 0 && c.Disposed);
            cold.BlankOwners = 0;
            await service.SetKeyboardDisplayEnabledAsync(true); await WaitAsync(service, "Running");
            cold.Opens.Should().Be(6);
        }
        finally { await service.StopAsync(default); }
    }

    [Theory]
    [InlineData("malformed")] [InlineData("ffaa")] [InlineData("write")]
    public async Task Rf_cold_start_does_not_retry_malformed_refused_or_unknown_write_faults(string fault)
    {
        var cold = new ColdRfFactory { Fault = fault };
        using var service = MakeCold(cold);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error");
            await Task.Delay(1300);
            cold.Opens.Should().Be(1);
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().NotContain(nameof(RogKeyboardNotReadyException));
        }
        finally { await service.StopAsync(default); }
    }

    private KeyboardDisplayService MakeCold(ColdRfFactory cold)
    {
        var rgb = DispatchProxy.Create<IRgbService, ReadProxy>(); var screens = DispatchProxy.Create<IScreenService, ReadProxy>();
        _rgbProxy = (ReadProxy)rgb; _screensProxy = (ReadProxy)screens;
        return new(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = WirelessPinned() }, rgb, screens, new Factory(),
            new ClearGuard(), wirelessFactory: cold);
    }

    private sealed class ColdRfFactory : IRogWirelessOledFactory
    {
        public int BlankOwners { get; set; }
        public string? Fault { get; init; }
        public int Opens => Owners.Count;
        public List<RogWirelessOledConnection> Owners { get; } = [];
        public List<ColdChannel> Channels { get; } = [];
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            var blank = BlankOwners > 0;
            if (blank && BlankOwners != int.MaxValue) BlankOwners--;
            var io = new WirelessIo(); var channel = new ColdChannel(io, blank, Fault); Channels.Add(channel);
            var keyboard = new RogPairedDevice(RogKeyboardInputContract.KeyboardProductId, RogKeyboardInputContract.ControlReportId, RogKeyboardInputContract.NotifyReportId);
            var owner = new RogWirelessOledConnection(new RogReceiver(receiverKey, RogKeyboardInputContract.ReceiverProductId, new Dictionary<byte, HidCollectionInfo>()),
                channel, firmwareHeader, () => io.Present) { NativeControlBinding = new([], keyboard, TimeProvider.System.GetTimestamp()) };
            Owners.Add(owner); return owner;
        }
    }

    private sealed class ColdChannel(WirelessIo io, bool blank, string? fault) : IHidReportChannel
    {
        public WirelessIo Io => io;
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            Writes++;
            if (fault == "write" && report.Span[1] is 0x51 or 0x61 or 0x67 or 0x68) throw new IOException("Offline mutation outcome unknown.");
            return io.WriteAsync(report, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var length = await io.ReadAsync(buffer, ct);
            if (length == 64 && buffer.Span[1] == 0x12 && buffer.Span[2] == 0)
            {
                if (blank) buffer.Slice(12, 4).Span.Clear(); // receiver envelope kept, keyboard firmware bytes empty
                if (fault == "malformed") buffer.Span[0] = 3;
                if (fault == "ffaa") { buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA; }
            }
            return length;
        }
        public void Dispose() { Disposed = true; io.Dispose(); }
    }
}
