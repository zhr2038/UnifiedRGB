using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Synthetic in-memory keyboard fixtures only (no Host process, HID, settings or physical claim). A configured anchor runs
/// same-family devices without repinning; device key, transport, profile, journal and native scope binding stay strict.
/// </summary>
public sealed partial class KeyboardRuntimeCompatibilityTests : IDisposable
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Wired24 = "001200000024000100", Wired25 = "001200000025000100", Wired26 = "001200000026000100";
    private const string Wireless24 = "0800070024000100", Wireless25 = "0800070025000100";
    private const string ReferenceImage = "0d7cd76d3056671bf6580196ca58041ab2417b824c980de2c9a3034d63341958";
    private const string OtherImage = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Contract = "b6ff1d41add209a95fb3dfebbcee7993aafbb1199b9ffb7a8551788028d4a585";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UnifiedRgb-RuntimeCompat-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_directory, "peripherals", "keyboard-display-session.json");

    [Theory]
    [InlineData("GYM1:25314D49", ReferenceImage)] [InlineData("GYM1:25314D50", ReferenceImage)]
    [InlineData("GYM1:24314D49", "")] [InlineData("GYM1:26314D50", OtherImage)]
    public void Native_policy_admits_without_any_build_or_image_repin(string build, string image)
    {
        var policy = NativePolicy() with { NativeBuild = build, CandidateSha256 = image };
        policy.Admitted.Should().BeTrue(); policy.HasQualifiedRoute.Should().BeTrue();
        new RogNative49PresentationCapability(policy).Evaluate(new(policy.DeviceKey, policy.FirmwareHeader, policy.ConnectionKind))
            .Should().Be(KeyboardNativePresentationSupport.Available);
    }

    [Theory]
    [InlineData("default")] [InlineData("off")] [InlineData("notifications")] [InlineData("off-with-all-diagnostics")]
    public void Native_policy_stays_default_off_and_needs_both_owner_opt_ins(string fault)
    {
        // The former build/image/contract rejection rows encoded diagnostics as admission; see the Diagnostics partial.
        var policy = fault switch
        {
            "default" => new KeyboardNativeMenuOptions(),
            "off" => NativePolicy() with { Enabled = false },
            "notifications" => NativePolicy() with { SourceControlNotifications = false },
            _ => new KeyboardNativeMenuOptions
            {
                NativeBuild = "GYM1:25314D49", CandidateSha256 = ReferenceImage, ContractSha256 = Contract,
                DeviceKey = new string('C', 64), FirmwareHeader = Wired25, ConnectionKind = "wired",
            },
        };
        policy.Admitted.Should().BeFalse(); policy.HasQualifiedRoute.Should().BeFalse();
        new RogNativeMenuSource(policy).OwnsDisplayOutput.Should().BeFalse();
    }

    [Theory]
    [InlineData("device")] [InlineData("kind")] [InlineData("cross-kind")] [InlineData("scope-family")] [InlineData("scope-transport-format")]
    public void Native_route_scope_stays_an_exact_device_transport_and_identity_family(string fault)
    {
        // Expectation change: the former header-equality rows (scope 24 vs policy 25, policy 26) encoded a declared header as scope.
        var policy = NativePolicy();
        string key = policy.DeviceKey, header = policy.RouteAnchor, kind = policy.ConnectionKind;
        policy.MatchesScope(key, header, kind).Should().BeTrue();
        switch (fault)
        {
            case "device": key = new string('B', 64); break;
            case "kind": kind = "bluetooth"; break;
            case "cross-kind": kind = "wireless"; break;
            case "scope-family": header = "001200000024000200"; break;
            default: header = Wireless24; break;
        }
        policy.MatchesScope(key, header, kind).Should().BeFalse();
    }

    [Fact]
    public void Configured_identities_of_the_same_family_are_all_verified_pins()
    {
        foreach (var header in new[] { Wired24, Wired25, Wired26 })
            Pinned(header).HasVerifiedWiredIdentity.Should().BeTrue();
        foreach (var header in new[] { Wireless24, Wireless25 })
            WirelessPinned(header).HasVerifiedWirelessIdentity.Should().BeTrue();
        new KeyboardDisplayOptions().HasVerifiedWiredIdentity.Should().BeFalse();
        new KeyboardDisplayOptions().Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(Wired24, 0x25)] [InlineData(Wired24, 0x26)] [InlineData(Wired25, 0x24)] [InlineData(Wired26, 0x25)]
    public async Task Wired_display_runs_on_a_same_family_device_without_repinning_and_restores_it(string anchor, byte patch)
    {
        var factory = new WiredFactory(_ => new Io { Patch = patch });
        using var service = Make(Pinned(anchor), factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            (await service.GetKeyboardDisplayStatusAsync()).FramesAcknowledged.Should().BeGreaterThan(0);
            factory.Anchors.Should().Equal(anchor);
            using var record = JsonDocument.Parse(File.ReadAllBytes(JournalPath));
            record.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be(anchor, "the journal records the configured anchor, not an observed header");
            record.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                new[] { "Schema", "DeviceKey", "FirmwareHeader", "Brightness", "ProfileToken", "Lighting", "StaticBank" }, "new journals keep the existing format");
        }
        finally { await service.StopAsync(default); }
        factory.Devices.Single().Brightness.Should().Be(25); File.Exists(JournalPath).Should().BeFalse();
    }

    [Fact]
    public async Task Wireless_display_runs_on_same_family_receiver_and_keyboard_without_repinning()
    {
        var wireless = new WirelessFactory(_ => new WirelessChannel(new Io { Patch = 0x26 }, receiverPatch: 9));
        using var service = Make(WirelessPinned(Wireless24), new WiredFactory(_ => throw new InvalidOperationException("No wired route")), wireless);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            using var record = JsonDocument.Parse(File.ReadAllBytes(JournalPath));
            record.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be(Wireless24);
        }
        finally { await service.StopAsync(default); }
        wireless.Channels.Single().Inner.Brightness.Should().Be(25); wireless.Anchors.Should().Equal(Wireless24);
    }

    [Fact]
    public async Task Old_schema2_journal_from_a_compatible_anchor_restores_the_same_device_transport_and_profile()
    {
        var old = WriteJournal(new { Schema = 2, DeviceKey = Key, FirmwareHeader = Wired24, Brightness = (byte)25, ProfileToken = (byte)6 });
        old.Should().NotBeEmpty();
        var factory = new WiredFactory(_ => new Io { Patch = 0x26, Brightness = 100 });
        using var service = Make(Pinned(Wired25), factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            using var record = JsonDocument.Parse(File.ReadAllBytes(JournalPath));
            record.RootElement.GetProperty("Brightness").GetByte().Should().Be(25, "the recorded original, not the temporary current value");
            record.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be(Wired25);
        }
        finally { await service.StopAsync(default); }
        factory.Devices.Single().Brightness.Should().Be(25);
    }

    [Fact]
    public async Task Old_schema1_journal_from_a_compatible_anchor_is_only_adopted_when_already_matching_and_is_archived()
    {
        var old = WriteJournal(new { Schema = 1, DeviceKey = Key, FirmwareHeader = Wired24, Brightness = (byte)25 });
        var factory = new WiredFactory(_ => new Io { Patch = 0x25, Brightness = 25 });
        using var service = Make(Pinned(Wired26), factory);
        await service.StartAsync(default);
        try { await WaitAsync(service, "Running"); }
        finally { await service.StopAsync(default); }
        var archives = Directory.GetFiles(Path.GetDirectoryName(JournalPath)!, "keyboard-display-session.json.schema1-*.archive");
        archives.Should().ContainSingle(); File.ReadAllBytes(archives[0]).Should().Equal(old);
        factory.Devices.Single().Brightness.Should().Be(25);
    }

    [Theory]
    [InlineData("device")] [InlineData("transport")] [InlineData("profile")] [InlineData("family")] [InlineData("malformed")]
    [InlineData("missing")] [InlineData("schema1-mismatch")]
    public async Task Journal_for_another_device_transport_profile_or_family_is_retained_without_any_write(string fault)
    {
        object record = fault switch
        {
            "device" => new { Schema = 2, DeviceKey = new string('B', 64), FirmwareHeader = Wired24, Brightness = (byte)25, ProfileToken = (byte)6 },
            "transport" => new { Schema = 2, DeviceKey = Key, FirmwareHeader = Wireless24, Brightness = (byte)25, ProfileToken = (byte)6 },
            "profile" => new { Schema = 2, DeviceKey = Key, FirmwareHeader = Wired24, Brightness = (byte)25, ProfileToken = (byte)5 },
            "family" => new { Schema = 2, DeviceKey = Key, FirmwareHeader = "001200000024000200", Brightness = (byte)25, ProfileToken = (byte)6 },
            "malformed" => new { Schema = 2, DeviceKey = Key, FirmwareHeader = "00120000002G000100", Brightness = (byte)25, ProfileToken = (byte)6 },
            "missing" => new { Schema = 2, DeviceKey = Key, Brightness = (byte)25, ProfileToken = (byte)6 },
            _ => new { Schema = 1, DeviceKey = Key, FirmwareHeader = Wired25, Brightness = (byte)40 },
        };
        var bytes = WriteJournal(record);
        var factory = new WiredFactory(_ => new Io { Patch = 0x25, Brightness = 100 });
        using var service = Make(Pinned(Wired24), factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error"); await Task.Delay(700);
            factory.Devices.Should().ContainSingle("a refused recovery is sticky and never reopens in a loop");
            factory.Devices[0].Mutations.Should().Be(0);
        }
        finally { await service.StopAsync(default); }
        File.ReadAllBytes(JournalPath).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("wired")] [InlineData("wireless")]
    public async Task Peripheral_release_recovers_a_compatible_journal_only_through_its_recorded_transport(string transport)
    {
        WriteJournal(new { Schema = 2, DeviceKey = Key, FirmwareHeader = transport == "wired" ? Wired26 : Wireless25, Brightness = (byte)25, ProfileToken = (byte)6 });
        var wired = new WiredFactory(_ => new Io { Patch = 0x25, Brightness = 100 });
        var wireless = new WirelessFactory(_ => new WirelessChannel(new Io { Patch = 0x24, Brightness = 100 }, receiverPatch: 8));
        using var service = Make(Pinned(Wired24) with { WirelessControlVerified = true, WirelessReceiverKey = Key, WirelessFirmwareHeader = Wireless24 }, wired, wireless);
        await service.ReleaseForPeripheralConfigurationAsync(default);
        File.Exists(JournalPath).Should().BeFalse();
        if (transport == "wired")
        {
            wired.Anchors.Should().Equal(Wired24); wireless.Anchors.Should().BeEmpty(); wired.Devices.Single().Brightness.Should().Be(25);
        }
        else
        {
            wireless.Anchors.Should().Equal(Wireless24); wired.Anchors.Should().BeEmpty(); wireless.Channels.Single().Inner.Brightness.Should().Be(25);
        }
    }

    [Theory]
    [InlineData("cross-transport")] [InlineData("family")] [InlineData("device")]
    public async Task Peripheral_release_never_replays_a_journal_across_transport_family_or_device(string fault)
    {
        var bytes = WriteJournal(new
        {
            Schema = 2, DeviceKey = fault == "device" ? new string('B', 64) : Key,
            FirmwareHeader = fault switch { "cross-transport" => Wireless24, "family" => "001200000024000200", _ => Wired24 },
            Brightness = (byte)25, ProfileToken = (byte)6,
        });
        var wired = new WiredFactory(_ => new Io { Patch = 0x24 });
        using var service = Make(Pinned(Wired24), wired);
        await ((Func<Task>)(() => service.ReleaseForPeripheralConfigurationAsync(default))).Should().ThrowAsync<IOException>();
        wired.Anchors.Should().BeEmpty(); File.ReadAllBytes(JournalPath).Should().Equal(bytes);
    }

    [Fact]
    public async Task Genuinely_incompatible_firmware_family_is_a_sticky_error_without_mutation_or_reopen()
    {
        var factory = new WiredFactory(_ => new Io { Patch = 0x25, Major = 2 });
        using var service = Make(Pinned(Wired24), factory);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Error"); await Task.Delay(700);
            factory.Devices.Should().ContainSingle(); factory.Devices[0].Mutations.Should().Be(0);
            (await service.GetKeyboardDisplayStatusAsync()).LastError.Should().Contain("firmware differs");
        }
        finally { await service.StopAsync(default); }
        File.Exists(JournalPath).Should().BeFalse();
    }

    [Fact]
    public async Task A_generation_change_during_the_session_refuses_restoration_writes_and_retains_the_journal()
    {
        var factory = new WiredFactory(_ => new Io { Patch = 0x25 });
        using var service = Make(Pinned(Wired24), factory);
        await service.StartAsync(default);
        Io device;
        try
        {
            await WaitAsync(service, "Running");
            device = factory.Devices.Single(); device.BrightnessWrites.Should().Equal(100);
            device.Patch = 0x26;
        }
        finally { await service.StopAsync(default); }
        device.BrightnessWrites.Should().Equal(new byte[] { 100 }, "no original-state write or replay crosses an observed generation change");
        File.Exists(JournalPath).Should().BeTrue("unknown original state stays journalled");
        factory.Devices.Should().ContainSingle();
    }

    [Theory]
    [InlineData("native5-build")] [InlineData("unknown-contract")] [InlineData("ineligible-image")]
    public async Task Diagnostic_native_declaration_never_blocks_and_unsupported_live_grammar_blocks_only_that_feature(string fault)
    {
        // Expectation change: a declared build/contract/image no longer decides support; the live CAP grammar does.
        var policy = fault switch
        {
            "native5-build" => NativePolicy() with { NativeBuild = "GYM1:25314D47", DeviceKey = Key },
            "unknown-contract" => NativePolicy() with { ContractSha256 = new string('a', 64), DeviceKey = Key },
            _ => NativePolicy() with { CandidateSha256 = "bfa1af30deecb6d64394e73963d7231c5c86811bad476cd7027c87c2d695292c", DeviceKey = Key },
        };
        var source = new RogNativeMenuSource(policy); source.RequestEnabled(true);
        var factory = new WiredFactory(_ => new Io { Patch = 0x26, AnswerMalformedCapability = true });
        using var service = Make(Pinned(Wired24), factory, native: source);
        await service.StartAsync(default);
        try
        {
            await UntilAsync(() => File.Exists(JournalPath)); await Task.Delay(500);
            factory.Anchors.Should().Equal(WiredFamily);
            factory.Devices.Single().NativeQueries.Should().BeLessThanOrEqualTo(1, "an unsupported live grammar is native-unavailable, never probed in a loop");
            factory.Devices.Single().NativeArms.Should().Be(0);
            (await service.GetKeyboardDisplayStatusAsync()).State.Should().NotBe("Error");
            File.Exists(JournalPath).Should().BeTrue();
        }
        finally { await service.StopAsync(default); }
        factory.Devices.Single().Brightness.Should().Be(25); File.Exists(JournalPath).Should().BeFalse();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static KeyboardNativeMenuOptions NativePolicy() => new()
    {
        Enabled = true, SourceControlNotifications = true, NativeBuild = "GYM1:25314D49", CandidateSha256 = ReferenceImage,
        ContractSha256 = Contract, DeviceKey = new string('C', 64), FirmwareHeader = Wired25, ConnectionKind = "wired",
    };

    private static KeyboardDisplayOptions Pinned(string header) => new() { Enabled = true, ControlVerified = true, DeviceKey = Key, FirmwareHeader = header };
    private static KeyboardDisplayOptions WirelessPinned(string header) => new()
    { Enabled = true, WirelessControlVerified = true, WirelessReceiverKey = Key, WirelessFirmwareHeader = header };

    private byte[] WriteJournal(object record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        File.WriteAllBytes(JournalPath, bytes);
        return bytes;
    }

    private KeyboardDisplayService Make(KeyboardDisplayOptions options, IRogWiredOledFactory wired, IRogWirelessOledFactory? wireless = null,
        RogNativeMenuSource? native = null)
    {
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        return new(new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = options }, rgb, screens, wired, new ClearGuard(),
            wirelessFactory: wireless, nativeMenu: native);
    }

    private static async Task WaitAsync(KeyboardDisplayService service, string state)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await service.GetKeyboardDisplayStatusAsync()).State != state) await Task.Delay(20, bound.Token);
    }

    private sealed class ClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException("No takeover side effects");
    }

    private sealed class WiredFactory(Func<int, Io> device) : IRogWiredOledFactory
    {
        public List<Io> Devices { get; } = [];
        public List<string> Anchors { get; } = [];
        public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("The configured-anchor overload is required.");
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
        {
            Anchors.Add(firmwareHeader);
            var io = device(Devices.Count + 1); Devices.Add(io);
            return new(io, () => true, () => deviceKey, () => firmwareHeader);
        }
    }

    private sealed class WirelessFactory(Func<int, WirelessChannel> channel) : IRogWirelessOledFactory
    {
        public List<WirelessChannel> Channels { get; } = [];
        public List<string> Anchors { get; } = [];
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            Anchors.Add(firmwareHeader);
            var io = channel(Channels.Count + 1); Channels.Add(io);
            return new(new RogReceiver(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, firmwareHeader, () => true);
        }
    }

    /// <summary>RID2/64-byte receiver framing around the wired model; receiver 7.00.x and keyboard x.01 family bytes.</summary>
    private sealed class WirelessChannel(Io inner, byte receiverPatch) : IHidReportChannel
    {
        public Io Inner => inner;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var p = report.ToArray();
            if (p.Length != 64 || p[0] != 2) throw new InvalidDataException("Wrong wireless request envelope.");
            var wired = new byte[65]; p.AsSpan(1, 63).CopyTo(wired.AsSpan(1));
            return inner.WriteAsync(wired, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var wired = new byte[65];
            if (await inner.ReadAsync(wired, ct) != 65) throw new InvalidDataException("Wrong synthetic reply.");
            if (wired[1] == 0x12 && wired[2] == 0)
            {
                var keyboard = wired[5]; var major = wired[7];
                wired[5] = receiverPatch; wired[6] = 0; wired[7] = 7; wired[8] = 0;
                wired[12] = keyboard; wired[13] = 0; wired[14] = major; wired[15] = 0;
            }
            var wireless = new byte[64]; wired.AsSpan(1, 63).CopyTo(wireless.AsSpan(1)); wireless[0] = 2;
            wireless.CopyTo(buffer); return wireless.Length;
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class Io : IHidReportChannel
    {
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(8);
        public byte Patch { get; set; } = 0x24;
        public byte Major { get; init; } = 1;
        public byte Brightness { get; set; } = 25;
        public int Mutations { get; private set; }
        public int NativeQueries { get; private set; }
        public int NativeArms { get; private set; }
        /// <summary>Answer a GYM1 capability query with a header-only reply: well framed, but not the supported grammar.</summary>
        public bool AnswerMalformedCapability { get; init; }
        public List<byte> BrightnessWrites { get; } = [];
        public ValueTask WriteAsync(ReadOnlyMemory<byte> request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var p = request.ToArray(); var b = new byte[65]; p.AsSpan(0, 5).CopyTo(b);
            switch (p[1], p[2])
            {
                case (0x12, 0): b[5] = Patch; b[7] = Major; b[9] = 6; b[11] = 6; break;
                case (0x21, 0): b[5] = 3; break;
                case (0x23, 2): b[5] = 1; break;
                case (0x23, 3): b[5] = Brightness; break;
                case (0x61, 0): Mutations++; break;
                case (0x68, 0): Mutations++; Brightness = p[5]; BrightnessWrites.Add(p[5]); b = p; break;
                case (0x67, 0) or (0x67, 1): Mutations++; break;
                case (0x67, 2): Mutations++; return ValueTask.CompletedTask;
                case (0x12, 0x7D) when AnswerMalformedCapability: NativeQueries++; break;
                case (0x71, 0x7D): NativeArms++; throw new InvalidOperationException("No native ARM expected.");
                case (0x12, 0x7D): NativeQueries++; throw new InvalidOperationException("No native feature query expected.");
                default: throw new InvalidOperationException("Unexpected synthetic packet.");
            }
            _queue.Writer.TryWrite(b).Should().BeTrue(); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { var p = await _queue.Reader.ReadAsync(ct); p.CopyTo(buffer); return p.Length; }
        public void Dispose() => _queue.Writer.TryComplete();
    }
}
