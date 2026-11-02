using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_RF_USB_RF_automatic_handoff_joins_one_shared_owner_preserves_exact_route_records(bool codexPage)
    {
        var time = new SourceClock(); using var inputs = new DualTransportInputs(time);
        using var routes = new DualTransportFactory(time, inputs, codexPage);
        var data = Path.Combine(Path.GetTempPath(), "actual-bidirectional-" + Guid.NewGuid().ToString("N"));
        var options = DualOptions(data); var browses = new List<int>();
        ulong cookie = Cookie;
        var source = new RogNativeMenuSource(Policy(1), inputs, inputs, time, () => cookie++, options.KeyboardDisplay, browses.Add);
        source.RequestCodexEnabled(true);
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        var sceneBefore = JsonSerializer.Serialize(state.Scene);
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null)); var bridge = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(options, rgb, screens, routes, new MutationClearGuard(), bridge,
            wirelessFactory: routes, configurationGate: gate, nativeMenu: source, time: time, idleFrame: Pixels,
            wiredRoutePresent: _ => routes.WiredAvailable);
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json");
        try
        {
            await owner.StartAsync(default);
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && (!codexPage || routes.Radio.Images.Count >= 1), time);
            var rfRecord = File.ReadAllBytes(journal);
            await WriteRouteKeys(bridge, 2);
            if (codexPage) await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);

            routes.ChangeAfterNextSafeReply(1);
            await SwitchUntil(() => routes.ActiveTransport == 1 && bridge.Available && (!codexPage || routes.Usb.Images.Count >= 1), time);
            routes.WritesToUnavailableRoute.Should().Be(0, "the passive USB arrival must retire the RF actor before another old-route query, renewal or restore");
            var usbRecord = File.ReadAllBytes(journal);
            JsonDocument.Parse(usbRecord).RootElement.GetProperty("DeviceKey").GetString().Should().Be(Policy(1).DeviceKey);
            Directory.EnumerateFiles(Path.GetDirectoryName(journal)!, "keyboard-display-session*")
                .Where(p => p != journal).Should().Contain(p => File.ReadAllBytes(p).SequenceEqual(rfRecord), "the exact RF restoration obligation is retained on the RF route");
            await WriteRouteKeys(bridge, 1);
            if (codexPage) await BrowseOnCurrentRoute(routes, inputs, browses, 1, time);

            routes.ChangeAfterNextSafeReply(2);
            await SwitchUntil(() => routes.ActiveTransport == 2 && bridge.Available && (!codexPage || routes.Radio.Images.Count >= 2), time);
            routes.WritesToUnavailableRoute.Should().Be(0);
            JsonDocument.Parse(File.ReadAllBytes(journal)).RootElement.GetProperty("DeviceKey").GetString().Should().Be(Policy(2).DeviceKey);
            Directory.EnumerateFiles(Path.GetDirectoryName(journal)!, "keyboard-display-session*")
                .Where(p => p != journal).Should().Contain(p => File.ReadAllBytes(p).SequenceEqual(usbRecord), "the absent USB obligation must not be replayed through RF");
            await WriteRouteKeys(bridge, 2);
            if (codexPage) await BrowseOnCurrentRoute(routes, inputs, browses, 2, time);

            routes.MaximumLivePorts.Should().Be(1); routes.OpenWhilePriorInputLive.Should().BeFalse();
            routes.Ports.Should().Contain(p => p.Transport == 1).And.Contain(p => p.Transport == 2);
            routes.Ports.Should().OnlyContain(p => p.MaximumReaders <= 1);
            routes.Radio.Requests.Should().OnlyContain(p => p.Length == 64 && p[0] == 2);
            routes.Usb.Requests.Should().OnlyContain(p => p.Length == 65 && p[0] == 0);
            state.Applied.Should().BeEmpty(); JsonSerializer.Serialize(state.Scene).Should().Be(sceneBefore);
            (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
        }
        finally
        {
            await owner.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    private static UnifiedRgbOptions DualOptions(string data) => new()
    {
        DataDirectory = data,
        KeyboardDisplay = new()
        {
            ControlVerified = true, LightingVerified = true, DeviceKey = Policy(1).DeviceKey, FirmwareHeader = Policy(1).FirmwareHeader,
            WirelessControlVerified = true, WirelessLightingVerified = true, WirelessReceiverKey = Policy(2).DeviceKey,
            WirelessFirmwareHeader = Policy(2).FirmwareHeader, DeviceShowsNativeEntryLogo = true,
        },
    };

    private static Task WriteRouteKeys(RogWiredLightingBridge bridge, byte transport) => bridge.WriteKeysAsync(
        Enumerable.Range(0, 85).Select(i => new RogKeyColor((byte)i, new((byte)(i + transport), 7, 9))).ToArray(),
        transport == 1 ? RogPerKeyLighting.WiredTransport : RogPerKeyLighting.WirelessTransport, default).WaitAsync(Patience);

    private static async Task SwitchUntil(Func<bool> condition, SourceClock time)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Actual transport owner did not reach the expected handoff boundary.");
            time.Advance(TimeSpan.FromMilliseconds(100)); await Task.Delay(20);
        }
    }

    private static async Task BrowseOnCurrentRoute(DualTransportFactory routes, DualTransportInputs inputs, List<int> browses,
        byte transport, SourceClock time)
    {
        var firmware = transport == 1 ? routes.Usb : routes.Radio;
        var before = browses.Count;
        for (ushort sequence = 1; sequence <= 2; sequence++)
        {
            var report = Notice(sequence, sequence == 1 ? (byte)0 : (byte)4, sequence == 1 ? (byte)3 : (byte)1, transport);
            report[4] = 10; BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(7), firmware.Epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(11), firmware.CookieValue);
            inputs.Send(transport, report);
        }
        await SwitchUntil(() => browses.Count == before + 1, time); browses[^1].Should().Be(1);
    }

    private sealed class DualTransportInputs(TimeProvider time) : IHidInputReportDevices, IHidContainerResolver, IDisposable
    {
        private readonly Input _usb = new(time, 1), _rf = new(time, 2);
        public int LiveHandles => _usb.LiveHandles + _rf.LiveHandles;
        private Input Select(ushort pid) => pid == 0x1C24 ? _usb : _rf;
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => Select(pid).Enumerate(vid, pid);
        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection) => Select(collection.Pid).OpenReadOnly(collection);
        public HidContainerResolution Resolve(HidCollectionInfo collection) => Select(collection.Pid).Resolve(collection);
        public void Send(byte transport, byte[] notice) => (transport == 1 ? _usb : _rf).Send(notice);
        public void Dispose() { _usb.Dispose(); _rf.Dispose(); }
    }

    private sealed class DualTransportFactory(TimeProvider time, DualTransportInputs inputs, bool codexPage)
        : IRogWiredOledFactory, IRogWirelessOledFactory, IDisposable
    {
        public CodexWireChannel Usb { get; } = new(1);
        public CodexWireChannel Radio { get; } = new(2);
        public List<Port> Ports { get; } = [];
        public bool WiredAvailable { get; private set; }
        public bool AbsenceVisible { get; set; } = true;
        public bool FailUsbKeyWriteAndSwitchToRadio { get; set; }
        public int TransitionWriteFailures { get; private set; }
        public byte ActiveTransport => Ports.LastOrDefault(p => !p.Disposed)?.Transport ?? 0;
        public int MaximumLivePorts { get; private set; }
        public bool OpenWhilePriorInputLive { get; private set; }
        public int WritesToUnavailableRoute { get; private set; }
        private byte _changeTo;
        public void ChangeAfterNextSafeReply(byte transport) => _changeTo = transport;
        private void ApplyRouteChange()
        {
            // Put the edge beyond the passive metadata sampler's100ms observation cache.
            if (time is SourceClock clock) clock.Advance(TimeSpan.FromMilliseconds(101));
            WiredAvailable = _changeTo == 1; _changeTo = 0;
        }
        public RogWiredOledConnection? TryOpen(string deviceKey)
        {
            if (!WiredAvailable) return null;
            deviceKey.Should().Be(Policy(1).DeviceKey);
            var port = OpenPort(1);
            return new(port, () => WiredAvailable, () => deviceKey, () => Policy(1).FirmwareHeader)
            { NativeControlBinding = Binding(1), RouteAbsentProbe = () => !WiredAvailable && AbsenceVisible };
        }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            receiverKey.Should().Be(Policy(2).DeviceKey);
            // RF receiver physically remains attached while the keyboard uses USB. The passive USB signal, rather than
            // receiver disappearance or an empty/negative RF query, must drive RF->USB handoff.
            var port = OpenPort(2);
            return new(new(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), port, firmwareHeader, () => true)
            { NativeControlBinding = Binding(2), EvidenceTime = time, KeyReportDelayMs = 0, RouteAbsentProbe = () => false };
        }
        private RogNativeControlBinding Binding(byte transport) => new([new(transport == 1 ? "fake#mi_01#control" : "fake#mi_02#control",
            0x0B05, transport == 1 ? (ushort)0x1C24 : (ushort)0x1ACE, transport == 1 ? 1 : 2, 0xFF00, 1,
            transport == 1 ? 65 : 64, transport == 1 ? 65 : 64, 0)], transport == 1 ? null : new(0x1C25, 2, 4), time.GetTimestamp());
        private Port OpenPort(byte transport)
        {
            OpenWhilePriorInputLive |= inputs.LiveHandles != 0;
            var port = new Port(this, transport, transport == 1 ? Usb : Radio, codexPage); Ports.Add(port);
            MaximumLivePorts = Math.Max(MaximumLivePorts, Ports.Count(p => !p.Disposed)); return port;
        }
        public void Dispose() { foreach (var port in Ports) port.Dispose(); Usb.Dispose(); Radio.Dispose(); }
        public sealed class Port(DualTransportFactory routes, byte transport, CodexWireChannel firmware, bool codexPage) : IHidReportChannel
        {
            public byte Transport => transport;
            public bool Disposed { get; private set; }
            public int MaximumReaders { get; private set; }
            private int _readers;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (transport == 1 && routes.FailUsbKeyWriteAndSwitchToRadio && report.Span[1] == 0xC0 && report.Span[2] == 0x81)
                {
                    // The write was already admitted on the USB route. Simulate Windows losing it while SetupAPI's
                    // strict absence observation is still unknown. No packet is delivered to the simulated firmware.
                    routes.FailUsbKeyWriteAndSwitchToRadio = false; routes.WiredAvailable = false;
                    routes.TransitionWriteFailures++;
                    throw new IOException("The specified nonexistent device disappeared during an admitted USB color write.");
                }
                if (transport == 1 ? !routes.WiredAvailable : routes.WiredAvailable)
                { routes.WritesToUnavailableRoute++; throw new IOException("Test forbids an old-route write after a passive transport edge."); }
                await firmware.WriteAsync(report, ct);
                if (routes._changeTo != 0 && (codexPage ? report.Span[1] == 0x67 && report.Span[2] == 2
                    : report.Span[1] == 0x23 && report.Span[2] == 3))
                {
                    // This covers a fresh positive observation, not impossible zero-latency route detection.
                    routes.ApplyRouteChange();
                }
            }
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                MaximumReaders = Math.Max(MaximumReaders, ++_readers);
                try
                {
                    var length = await firmware.ReadAsync(buffer, ct);
                    if (!codexPage && buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D) buffer.Span[11] = 0;
                    return length;
                }
                finally { _readers--; }
            }
            public void Dispose() => Disposed = true; // preserve the simulated physical keyboard state for its next owner
        }
    }
}
