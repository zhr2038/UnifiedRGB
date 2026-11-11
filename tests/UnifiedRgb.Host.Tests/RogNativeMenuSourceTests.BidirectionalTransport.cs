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
        public bool WiredAvailable { get; set; }
        public bool AbsenceVisible { get; set; } = true;
        public bool RadioPeerUnavailable { get; set; }
        public bool RadioCapsUnavailable { get; set; }
        public bool RadioCapsMalformed { get; set; }
        public bool RadioRefuseArmOnce { get; set; }
        public bool RadioRefuseDisarmOnce { get; set; }
        private string? _radioTransientPhase;
        private Port? _radioTransientPort;
        private int _radioTransientImageTarget;
        public void ArmRadioTransientPhase(string phase)
        {
            _radioTransientPort = Ports.Last(p => p.Transport == 2 && !p.Disposed);
            _radioTransientPhase = phase; _radioTransientImageTarget = Radio.Images.Count + 1;
        }
        public string? RadioPartialStartup { get; set; }
        public byte? RadioActiveProfileToken { get; set; }
        public byte? RadioStartupBrightness { get; set; }
        public bool RadioRefuseBrightnessReadbackAfterSetter { get; set; }
        public byte? RefuseCapabilityTransport { get; set; }
        public byte? RefuseReadCommand { get; set; }
        public Action? AfterCapabilityRead { get; set; }
        public int? TimeoutColourPacket { get; set; }
        public int? RefuseCapabilityAfterImages { get; set; }
        public bool? RadioRestoreBrightnessRefusalBeforeWrite { get; set; }
        public bool RadioRefuseNextCap { get; set; }
        public bool RadioRefuseCommitCap { get; set; }
        public bool WiredBadBasicInfo { get; set; }
        public bool UseCodexPage { get; set; } = codexPage;
        public int EmptyPeerReplies { get; private set; }
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
            var port = new Port(this, transport, transport == 1 ? Usb : Radio, UseCodexPage); Ports.Add(port);
            MaximumLivePorts = Math.Max(MaximumLivePorts, Ports.Count(p => !p.Disposed)); return port;
        }
        public void Dispose() { foreach (var port in Ports) port.Dispose(); Usb.Dispose(); Radio.Dispose(); }
        public sealed class Port(DualTransportFactory routes, byte transport, CodexWireChannel firmware, bool codexPage) : IHidReportChannel
        {
            public byte Transport => transport;
            public bool Disposed { get; private set; }
            public int MaximumReaders { get; private set; }
            public List<byte[]> Requests { get; } = [];
            public int? FirstEmptyAtWriteCount { get; private set; }
            public int? RefusedCapAtWriteCount { get; private set; }
            public int? PartialStartupAtWriteCount { get; private set; }
            public int? TransientFailureAtWriteCount { get; private set; }
            public int? FreshPresentationProbeAtWriteCount { get; private set; }
            public List<string> InjectedTransientFailures { get; } = [];
            private int _readers;
            private int _colourPackets;
            private bool _presetAcknowledged;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                Requests.Add(report.ToArray());
                if (report.Span[1] == 0xC0 && report.Span[2] == 0x81
                    && ++_colourPackets == routes.TimeoutColourPacket)
                {
                    routes.TimeoutColourPacket = null;
                    TransientFailureAtWriteCount = Requests.Count;
                    InjectedTransientFailures.Add("colour-write-timeout");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
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
                    if (transport == 2 && routes.RadioRestoreBrightnessRefusalBeforeWrite == true
                        && _presetAcknowledged && Requests[^1][1] == 0x61)
                    {
                        routes.RadioRestoreBrightnessRefusalBeforeWrite = null;
                        RewriteNegative(buffer, "brightness-before-write");
                        return 64;
                    }
                    var length = await firmware.ReadAsync(buffer, ct);
                    if (buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D
                        && routes.RefuseCapabilityAfterImages is { } target && firmware.Images.Count >= target)
                    {
                        routes.RefuseCapabilityAfterImages = null;
                        RewriteNegative(buffer, "post-frame-capability");
                    }
                    else if (buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D && routes.RefuseCapabilityTransport == transport)
                    {
                        routes.RefuseCapabilityTransport = null;
                        RewriteNegative(buffer, "standalone-capability");
                    }
                    else if (routes.RefuseReadCommand == buffer.Span[1])
                    {
                        routes.RefuseReadCommand = null;
                        RewriteNegative(buffer, "readonly-reconciliation");
                    }
                    else if (buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D)
                    {
                        routes.AfterCapabilityRead?.Invoke();
                    }
                    if (transport == 2 && buffer.Span[1] == 0x61) _presetAcknowledged = true;
                    if (transport == 2 && buffer.Span[1] == 0x68 && routes.RadioRestoreBrightnessRefusalBeforeWrite == false)
                    {
                        routes.RadioRestoreBrightnessRefusalBeforeWrite = null;
                        RewriteNegative(buffer, "brightness-after-write");
                    }
                    if (transport == 2 && buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D
                        && (routes.RadioCapsUnavailable || routes.RadioCapsMalformed))
                    {
                        FreshPresentationProbeAtWriteCount = Requests.Count;
                        if (routes.RadioCapsUnavailable)
                        { buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA; }
                        else buffer.Span[33] ^= 1; // malformed208px geometry is not a temporary transport refusal
                    }
                    if (transport == 2 && buffer.Span[1] == 0x71 && buffer.Span[2] == 0x7D
                        && (routes.RadioRefuseArmOnce && Requests[^1][10] == 1 || routes.RadioRefuseDisarmOnce && Requests[^1][10] == 0))
                    {
                        routes.RadioRefuseArmOnce = false; routes.RadioRefuseDisarmOnce = false;
                        RewriteNegative(buffer, "persistent-arm-or-disarm");
                    }
                    if (transport == 2 && ReferenceEquals(this, routes._radioTransientPort))
                    {
                        var matches = routes._radioTransientPhase switch
                        {
                            "image-begin" => buffer.Span[1] == 0x67 && buffer.Span[2] == 0,
                            "pixel95-abort-cap" => buffer.Span[1] == 0x67 && buffer.Span[2] == 1 && buffer.Span[3] == 95,
                            "abort-cap" => buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D,
                            "post-frame-capability" => buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D && firmware.Images.Count >= routes._radioTransientImageTarget,
                            "first-native-lease" => buffer.Span[1] == 0x67 && buffer.Span[2] == 2 && firmware.Images.Count >= routes._radioTransientImageTarget,
                            "native-lease" => buffer.Span[1] == 0x67 && buffer.Span[2] == 2,
                            _ => false,
                        };
                        if (matches)
                        {
                            var phase = routes._radioTransientPhase!;
                            routes._radioTransientPhase = phase == "pixel95-abort-cap" ? "abort-cap" : null;
                            routes.RadioPeerUnavailable = true; RewriteNegative(buffer, phase);
                        }
                    }
                    if (transport == 2 && buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D
                        && (routes.RadioRefuseNextCap || routes.RadioRefuseCommitCap && firmware.Images.Count > 0))
                    {
                        routes.RadioRefuseNextCap = false; routes.RadioRefuseCommitCap = false;
                        routes.RadioPeerUnavailable = true; RefusedCapAtWriteCount = Requests.Count;
                        buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA;
                    }
                    if (!codexPage && buffer.Span[1] == 0x12 && buffer.Span[2] == 0x7D) buffer.Span[11] = 0;
                    if (buffer.Span[1] == 0x12 && buffer.Span[2] == 0)
                    {
                        if (transport == 2 && routes.RadioPeerUnavailable)
                        {
                            buffer.Span.Slice(12, 4).Clear(); routes.EmptyPeerReplies++;
                            FirstEmptyAtWriteCount ??= Requests.Count;
                        }
                        else if (transport == 1 && routes.WiredBadBasicInfo)
                        { buffer.Span[9] = 0; buffer.Span[11] = 0; }
                        if (transport == 2 && routes.RadioActiveProfileToken is { } profile) buffer.Span[11] = profile;
                    }
                    if (transport == 2 && !routes.RadioPeerUnavailable && routes.RadioPartialStartup is { } stage
                        && (stage.StartsWith("selector", StringComparison.Ordinal) && buffer.Span[1] == 0x21 && buffer.Span[2] == 0
                            || stage.StartsWith("content", StringComparison.Ordinal) && buffer.Span[1] == 0x23 && buffer.Span[2] == 2))
                    {
                        PartialStartupAtWriteCount = Requests.Count;
                        if (stage.EndsWith("negative", StringComparison.Ordinal))
                        { buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA; }
                        else buffer.Span[5] = 0;
                    }
                    if (transport == 2 && buffer.Span[1] == 0x23 && buffer.Span[2] == 3)
                    {
                        if (routes.RadioRefuseBrightnessReadbackAfterSetter && Requests.Any(p => p[1] == 0x68))
                        { buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA; }
                        else if (routes.RadioStartupBrightness is { } brightness) buffer.Span[5] = brightness;
                    }
                    return length;
                }
                finally { _readers--; }
            }
            public void Dispose() => Disposed = true; // preserve the simulated physical keyboard state for its next owner
            private void RewriteNegative(Memory<byte> buffer, string phase)
            {
                buffer.Span.Clear(); buffer.Span[0] = transport == 1 ? (byte)0 : (byte)2; buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA;
                TransientFailureAtWriteCount = Requests.Count; InjectedTransientFailures.Add(phase);
            }
        }
    }
}
