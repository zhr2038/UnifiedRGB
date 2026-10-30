using System.Text.Json;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fake-only route policy for the installed RF-primary native config plus the exact verified USB and RF OLED identities. It
/// exercises the actual SelectConnection, journal route choice and the shared source/capability route admission. Inert
/// channels/inputs count and refuse every I/O; nothing here says how a physical keyboard or receiver behaves.
/// </summary>
public sealed class KeyboardNativeRouteSelectionTests
{
    private static readonly string UsbKey = new('A', 64), RfKey = new('B', 64), UnknownKey = new('E', 64);
    private static readonly string WiredAnchor = RogRuntimeCompatibility.NativeFamilyAnchor("wired")!;
    private static readonly string RfAnchor = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;

    // Installed shape: the native policy names the RF receiver; build/image/contract/header notes are diagnostic (stale here).
    private static KeyboardNativeMenuOptions RfPrimary => new()
    {
        Enabled = true, SourceControlNotifications = true, ConnectionKind = "wireless", DeviceKey = RfKey,
        FirmwareHeader = "0800070024000100", NativeBuild = "GYM1:stale", CandidateSha256 = "stale", ContractSha256 = string.Empty,
    };

    // Display preference off and lighting unverified: the ordinary display path opens nothing, so any open is a native route.
    private static KeyboardDisplayOptions Display => new()
    {
        ControlVerified = true, DeviceKey = UsbKey, FirmwareHeader = "001200000024000100",
        WirelessControlVerified = true, WirelessReceiverKey = RfKey, WirelessFirmwareHeader = "0800070024000100",
    };

    [Theory]
    [InlineData("", "")] [InlineData("001200000024000100", "0800070024000100")] [InlineData("stale note", "0800070099000100")]
    public async Task Rf_primary_policy_turns_over_USB_RF_USB_on_exact_known_identities_with_one_live_owner(string wiredNote, string rfNote)
    {
        using var rig = new Rig(RfPrimary with { FirmwareHeader = rfNote }, Display with { FirmwareHeader = wiredNote, WirelessFirmwareHeader = rfNote });
        (await rig.Turn()).Should().Be((UsbKey, "wired"));
        rig.Rf.Opens.Should().BeEmpty("the RF receiver is never opened while the exact USB route is present");
        rig.Usb.Present = false;
        (await rig.Turn()).Should().Be((RfKey, "wireless"));
        rig.Usb.Present = true;
        (await rig.Turn()).Should().Be((UsbKey, "wired"));
        // Header notes never reach an open: every route opens with its neutral transport label.
        rig.Usb.Opens.Should().Equal((UsbKey, WiredAnchor), (UsbKey, WiredAnchor), (UsbKey, WiredAnchor));
        rig.Rf.Opens.Should().Equal((RfKey, RfAnchor));
        rig.Ledger.MaxLive.Should().Be(1); rig.Ledger.Live.Should().Be(0); rig.Ledger.Io.Should().Be(0); rig.Input.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void An_open_exception_on_the_selected_route_never_falls_through_to_another_route(bool usbFails)
    {
        using var rig = new Rig(RfPrimary, Display);
        var failure = new InvalidOperationException("offline open failure");
        if (usbFails) rig.Usb.Failure = failure; else { rig.Usb.Present = false; rig.Rf.Failure = failure; }
        rig.Service.Invoking(s => s.SelectConnection()).Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
        rig.Usb.Opens.Should().HaveCount(1, "neither the native USB route nor the ordinary display path is retried");
        rig.Rf.Opens.Should().HaveCount(usbFails ? 0 : 1);
        rig.Ledger.MaxLive.Should().Be(0);
    }

    [Theory]
    [InlineData("usb", "001200000000000100", "wired", true)]
    [InlineData("usb", "001200000027000100", "wired", true)] // other OEM bytes in the live label are diagnostic
    [InlineData("rf", "0000070000000100", "wireless", true)]
    [InlineData("rf", "0800070027000100", "wireless", true)]
    [InlineData("unknown", "001200000000000100", "wired", false)]
    [InlineData("unknown", "0000070000000100", "wireless", false)]
    [InlineData("rf", "001200000000000100", "wired", false)] // known RF receiver key on the USB transport
    [InlineData("usb", "0000070000000100", "wireless", false)] // known USB key on the RF transport
    [InlineData("usb", "0000070000000100", "wired", false)] // USB route with an RF-family label
    public async Task Source_and_presentation_admit_the_same_known_routes_and_reject_others_before_CAP_or_input_IO(
        string identity, string header, string kind, bool admitted)
    {
        var input = new NoInput();
        var source = new RogNativeMenuSource(RfPrimary, input, input, displayOptions: Display); source.RequestEnabled(true);
        var capability = new RogNative49PresentationCapability(RfPrimary, Display);
        var key = identity switch { "usb" => UsbKey, "rf" => RfKey, _ => UnknownKey };
        var connection = new Recording(key, header, kind);
        source.AcceptsScope(key, header, kind).Should().Be(admitted);
        capability.Evaluate(new(key, header, kind)).Should().Be(admitted
            ? KeyboardNativePresentationSupport.Available : KeyboardNativePresentationSupport.Unavailable);
        await using (var actor = source.Attach(connection))
        {
            var step = () => actor.StepAsync(() => new byte[RogWiredOledProtocol.PixelBytes], CancellationToken.None);
            // Positive control: the same setup reaches the (offline, failing) CAP exchange only for an admitted route.
            if (admitted) await step.Should().ThrowAsync<IOException>().WithMessage("Offline CAP exchange reached");
            else (await step()).Should().BeFalse();
        }
        connection.Requests.Should().HaveCount(admitted ? 1 : 0);
        if (admitted) connection.Requests[0].Should().Equal(RogNativeMenuCodec.CapabilityRequest());
        connection.Leases.Should().Be(0); input.Calls.Should().Be(0); source.IsPresent.Should().BeFalse();
    }

    [Theory]
    [InlineData("default")] [InlineData("off")] [InlineData("notifications-off")] [InlineData("bad-key")] [InlineData("bad-kind")]
    public void Invalid_or_default_off_native_policy_is_not_widened_by_verified_display_identities(string fault)
    {
        var policy = fault switch
        {
            "default" => new KeyboardNativeMenuOptions(),
            "off" => RfPrimary with { Enabled = false },
            "notifications-off" => RfPrimary with { SourceControlNotifications = false },
            "bad-key" => RfPrimary with { DeviceKey = "1ACE" },
            _ => RfPrimary with { ConnectionKind = "bluetooth" },
        };
        policy.QualifiedRoutes(Display).Should().BeEmpty();
        using var rig = new Rig(policy, Display);
        rig.Source.QualifiedRoutes.Should().BeEmpty();
        foreach (var (key, header, kind) in new[] { (UsbKey, WiredAnchor, "wired"), (RfKey, RfAnchor, "wireless") })
        {
            rig.Source.AcceptsScope(key, header, kind).Should().BeFalse();
            rig.Capability.Evaluate(new(key, header, kind)).Should().Be(KeyboardNativePresentationSupport.Unavailable);
        }
        rig.Service.SelectConnection().Should().BeNull();
        rig.Usb.Opens.Should().BeEmpty(); rig.Rf.Opens.Should().BeEmpty(); rig.Ledger.MaxLive.Should().Be(0);
    }

    [Theory]
    [InlineData("wired")] [InlineData("wireless")] [InlineData("unknown")]
    public async Task Recovery_journal_reopens_only_its_exact_recorded_known_route(string recorded)
    {
        using var rig = new Rig(RfPrimary, Display with { FirmwareHeader = string.Empty, WirelessFirmwareHeader = string.Empty });
        var failure = new InvalidOperationException("offline recovery open reached");
        rig.Usb.Failure = failure; rig.Rf.Failure = failure;
        // Older journals keep their recorded OEM label bytes; only device and transport label select the route.
        var (key, header) = recorded switch
        {
            "wired" => (UsbKey, "001200000024000100"), "wireless" => (RfKey, "0800070024000100"), _ => (UnknownKey, "001200000024000100"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Journal)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = key, FirmwareHeader = header, Brightness = (byte)50, ProfileToken = (byte)6 });
        File.WriteAllBytes(rig.Journal, bytes);
        var release = () => rig.Service.ReleaseForPeripheralConfigurationAsync(default);
        if (recorded == "unknown") await release.Should().ThrowAsync<IOException>();
        else (await release.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        (string, string)[] usb = recorded == "wired" ? [(UsbKey, WiredAnchor)] : [];
        (string, string)[] rf = recorded == "wireless" ? [(RfKey, RfAnchor)] : [];
        rig.Usb.Opens.Should().Equal(usb); rig.Rf.Opens.Should().Equal(rf);
        File.ReadAllBytes(rig.Journal).Should().Equal(bytes);
    }

    private sealed class Rig : IDisposable
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "UrgbNativeRoute-" + Guid.NewGuid().ToString("N"));
        public Ledger Ledger { get; } = new();
        public NoInput Input { get; } = new();
        public Usb Usb { get; }
        public Rf Rf { get; }
        public RogNativeMenuSource Source { get; }
        public RogNative49PresentationCapability Capability { get; }
        public KeyboardDisplayService Service { get; }
        public string Journal => Path.Combine(_data, "peripherals", "keyboard-display-session.json");

        public Rig(KeyboardNativeMenuOptions policy, KeyboardDisplayOptions display)
        {
            Usb = new(Ledger); Rf = new(Ledger);
            Source = new(policy, Input, Input, displayOptions: display); Source.RequestEnabled(true);
            Capability = new(policy, display);
            Service = new(new UnifiedRgbOptions { DataDirectory = _data, KeyboardDisplay = display }, null!, null!, Usb, new FakeGuard(),
                wirelessFactory: Rf, nativeMenu: Source);
        }

        /// <summary>One owner turn: select, check the shared admission, borrow the native actor once, then dispose both.</summary>
        public async Task<(string Key, string Kind)> Turn()
        {
            using var connection = Service.SelectConnection() ?? throw new InvalidOperationException("No route selected.");
            Ledger.Live.Should().Be(1);
            Source.AcceptsScope(connection.DeviceKey, connection.FirmwareHeader, connection.ConnectionKind).Should().BeTrue();
            Capability.Evaluate(new(connection.DeviceKey, connection.FirmwareHeader, connection.ConnectionKind))
                .Should().Be(KeyboardNativePresentationSupport.Available);
            var actor = Source.Attach(connection);
            Source.Invoking(s => s.Attach(connection)).Should().Throw<InvalidOperationException>();
            await actor.DisposeAsync();
            return (connection.DeviceKey, connection.ConnectionKind);
        }

        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(_data)) Directory.Delete(_data, true);
        }
    }

    private sealed class Ledger { public int Live, MaxLive, Io; }

    private sealed class Usb(Ledger ledger) : IRogWiredOledFactory
    {
        public bool Present { get; set; } = true;
        public Exception? Failure { get; set; }
        public List<(string, string)> Opens { get; } = [];
        public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("Native routes pass a transport label.");
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
        {
            Opens.Add((deviceKey, firmwareHeader));
            if (Failure is not null) throw Failure;
            return Present ? new(new Inert(ledger), () => true, () => deviceKey, () => firmwareHeader) : null;
        }
    }

    private sealed class Rf(Ledger ledger) : IRogWirelessOledFactory
    {
        public Exception? Failure { get; set; }
        public List<(string, string)> Opens { get; } = [];
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            Opens.Add((receiverKey, firmwareHeader));
            if (Failure is not null) throw Failure;
            return new(new RogReceiver(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), new Inert(ledger), firmwareHeader, () => true);
        }
    }

    private sealed class Inert : IHidReportChannel
    {
        private readonly Ledger _ledger;
        private bool _disposed;
        public Inert(Ledger ledger) { _ledger = ledger; ledger.MaxLive = Math.Max(ledger.MaxLive, ++ledger.Live); }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) { _ledger.Io++; throw new InvalidOperationException("No device I/O in route fixtures."); }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { _ledger.Io++; throw new InvalidOperationException("No device I/O in route fixtures."); }
        public void Dispose() { if (!_disposed) { _disposed = true; _ledger.Live--; } }
    }

    private sealed class Recording(string key, string header, string kind) : IRogOledConnection
    {
        public List<byte[]> Requests { get; } = [];
        public int Leases { get; private set; }
        public string DeviceKey => key;
        public string FirmwareHeader => header;
        public string ConnectionKind => kind;
        public int PixelPayloadCapacity => kind == "wired" ? 60 : 59;
        public bool SameDevice() => true;
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) => throw new InvalidOperationException("No identity I/O in route fixtures.");
        public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct) { Requests.Add((byte[])request.Clone()); throw new IOException("Offline CAP exchange reached"); }
        public Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct) { Leases++; throw new IOException("Offline lease reached"); }
        public void Dispose() { }
    }

    private sealed class NoInput : IHidInputReportDevices, IHidContainerResolver
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) { Calls++; throw new InvalidOperationException("No HID enumeration in route fixtures."); }
        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection) { Calls++; throw new InvalidOperationException("No HID input open in route fixtures."); }
        public HidContainerResolution Resolve(HidCollectionInfo collection) { Calls++; throw new InvalidOperationException("No container query in route fixtures."); }
    }
}
