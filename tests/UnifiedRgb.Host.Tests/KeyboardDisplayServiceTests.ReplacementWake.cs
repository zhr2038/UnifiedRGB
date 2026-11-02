using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public async Task ObservationRecovery_replacement_not_ready_waits_with_same_record_and_budget_until_a_fresh_wake()
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var factory = new ReplacementWakeFactory();
        using var service = new KeyboardDisplayService(new UnifiedRgbOptions { DataDirectory = _directory,
            KeyboardDisplay = WirelessPinned() with { Enabled = false, WirelessLightingVerified = true } },
            DispatchProxy.Create<IRgbService, ReadProxy>(), DispatchProxy.Create<IScreenService, ReadProxy>(), new Factory(), new ClearGuard(),
            wirelessFactory: factory, configurationGate: gate);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Waiting"); factory.Channels.Should().HaveCount(2);
            var record = File.ReadAllBytes(ObservationJournal);
            factory.Channels[0].Inner.WritesAfterFault.Should().Be(0);
            factory.Channels[1].Writes.Should().Be(1, "the blank owner only answered the identity query");
            factory.Channels[1].Inner.Inner.Mutations.Should().Be(0);
            factory.Channels[1].Inner.Inner.NativeWrites.Should().Be(0);
            factory.Channels[1].Disposed.Should().BeTrue(); factory.OpenWhilePreviousLive.Should().BeFalse();
            typeof(KeyboardDisplayService).GetField("_observationReplacements", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service).Should().Be(1);
            await Task.Delay(250); File.ReadAllBytes(ObservationJournal).Should().Equal(record);
            await ObservationUntil(() => factory.Channels.Count == 3 && factory.Channels[2].Inner.Observations > 0);
            factory.OpenWhilePreviousLive.Should().BeFalse(); factory.Channels[2].Inner.Inner.Brightness.Should().Be(25);
            typeof(KeyboardDisplayService).GetField("_observationReplacements", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service).Should().Be(1, "NotReady neither spends nor resets the replacement budget");
            (await service.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
            File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display.json")).Should().BeFalse();
        }
        finally { await service.StopAsync(default); }
    }

    private sealed class ReplacementWakeFactory : IRogWirelessOledFactory
    {
        public List<ReplacementWakeChannel> Channels { get; } = [];
        public bool OpenWhilePreviousLive { get; private set; }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            OpenWhilePreviousLive |= Channels.Count > 0 && !Channels[^1].Disposed;
            var n = Channels.Count;
            var inner = n == 0 ? new Io() : Channels[^1].Inner.Inner.Reopen();
            var channel = new ReplacementWakeChannel(new ObservationChannel(inner, n == 0, false, false, _ => { }), n == 1);
            Channels.Add(channel);
            return new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), channel, firmwareHeader, () => true);
        }
    }
    private sealed class ReplacementWakeChannel(ObservationChannel inner, bool blank) : IHidReportChannel
    {
        public ObservationChannel Inner => inner;
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            Writes++; var wired = new byte[65]; report.Span[1..].CopyTo(wired.AsSpan(1));
            return inner.WriteAsync(wired, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var wired = new byte[65]; var length = await inner.ReadAsync(wired, ct);
            if (length == 65 && wired[1] == 0x12 && wired[2] == 0)
            {
                wired[5] = 8; wired[7] = 7;
                if (!blank) { wired[12] = 0x24; wired[14] = 1; }
            }
            buffer.Span.Clear(); wired.AsSpan(1, 63).CopyTo(buffer.Span[1..]); buffer.Span[0] = 2; return 64;
        }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }
}
