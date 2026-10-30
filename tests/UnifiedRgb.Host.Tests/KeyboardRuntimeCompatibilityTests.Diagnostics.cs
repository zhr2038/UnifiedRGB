using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Owner correction: declared build, image, contract and header values are diagnostics only; they never admit, veto, open or
/// label the native route. Admission is the owner's default-off opt-in plus the captured descriptor route (device key and
/// transport); the connection checks live identity against the known native family and live CAP grammar decides actual
/// support. Journals bind device, transport format, family and profile.
/// </summary>
public sealed partial class KeyboardRuntimeCompatibilityTests
{
    private const string Wireless26 = "0800070026000100";
    private static readonly string WiredFamily = RogRuntimeCompatibility.NativeFamilyAnchor("wired")!;
    private static readonly string WirelessFamily = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;

    [Theory]
    [InlineData("blank-build")] [InlineData("build51")] [InlineData("build47")] [InlineData("malformed-build")] [InlineData("blank-image")]
    [InlineData("native5-image")] [InlineData("d79-image")] [InlineData("dcf-image")] [InlineData("malformed-image")]
    [InlineData("blank-contract")] [InlineData("native5-contract")] [InlineData("unknown-contract")] [InlineData("blank-header")]
    [InlineData("stale-header")] [InlineData("arbitrary-header")] [InlineData("other-family-header")] [InlineData("cross-kind-header")]
    [InlineData("all-blank")]
    public void Native_declared_fields_are_diagnostics_that_never_veto_admission(string diagnostic)
    {
        var policy = diagnostic switch
        {
            "blank-build" => NativePolicy() with { NativeBuild = "" },
            "build51" => NativePolicy() with { NativeBuild = "GYM1:25314D51" },
            "build47" => NativePolicy() with { NativeBuild = "GYM1:25314D47" },
            "malformed-build" => NativePolicy() with { NativeBuild = "stale build note" },
            "blank-image" => NativePolicy() with { CandidateSha256 = "" },
            "native5-image" => NativePolicy() with { CandidateSha256 = "bfa1af30deecb6d64394e73963d7231c5c86811bad476cd7027c87c2d695292c" },
            "d79-image" => NativePolicy() with { CandidateSha256 = "d79e8be2a6430e2ee62624898089376a58ce815614a8b5fd9f8213277abba4d0" },
            "dcf-image" => NativePolicy() with { CandidateSha256 = "dcfcc1af245a8624dab977cbf18ad9aaba3efbe69393f53b6ebcd158ade4e679" },
            "malformed-image" => NativePolicy() with { CandidateSha256 = "zz" },
            "blank-contract" => NativePolicy() with { ContractSha256 = "" },
            "native5-contract" => NativePolicy() with { ContractSha256 = "c631094eae90fa265a978167017b50af55c29f7a8e2323e89ecbaf51ebe7b52c" },
            "unknown-contract" => NativePolicy() with { ContractSha256 = new string('a', 64) },
            "blank-header" => NativePolicy() with { FirmwareHeader = "" },
            "stale-header" => NativePolicy() with { FirmwareHeader = Wired24 },
            "arbitrary-header" => NativePolicy() with { FirmwareHeader = "not a header" },
            "other-family-header" => NativePolicy() with { FirmwareHeader = "001200000025000200" },
            "cross-kind-header" => NativePolicy() with { FirmwareHeader = Wireless25 },
            _ => NativePolicy() with { NativeBuild = "", CandidateSha256 = "", ContractSha256 = "", FirmwareHeader = "" },
        };
        policy.Admitted.Should().BeTrue(); policy.HasQualifiedRoute.Should().BeTrue();
        policy.RouteAnchor.Should().Be(WiredFamily, "the route never takes its anchor from a declared header");
        new RogNativeMenuSource(policy).OwnsDisplayOutput.Should().BeTrue();
        new RogNative49PresentationCapability(policy).Evaluate(new(policy.DeviceKey, policy.RouteAnchor, policy.ConnectionKind))
            .Should().Be(KeyboardNativePresentationSupport.Available);
    }

    [Theory]
    [InlineData("wired", Wired24)] [InlineData("wired", Wired25)] [InlineData("wired", Wired26)] [InlineData("wired", "")]
    [InlineData("wired", "stale note")] [InlineData("wired", Wireless25)] [InlineData("wireless", Wireless24)]
    [InlineData("wireless", Wireless26)] [InlineData("wireless", "")] [InlineData("wireless", Wired25)]
    public void Native_route_is_the_captured_descriptor_route_whatever_the_configured_header_says(string kind, string configured)
    {
        var policy = NativePolicy() with { ConnectionKind = kind, FirmwareHeader = configured };
        var family = kind == "wired" ? WiredFamily : WirelessFamily;
        policy.HasQualifiedRoute.Should().BeTrue(); policy.RouteAnchor.Should().Be(family);
        foreach (var anchor in kind == "wired" ? new[] { WiredFamily, Wired24, Wired25, Wired26 } : new[] { WirelessFamily, Wireless24, Wireless25, "0900070026000100" })
            policy.MatchesScope(policy.DeviceKey, anchor, kind).Should().BeTrue("patch bytes of a family anchor are never a version gate");
        policy.MatchesScope(policy.DeviceKey, family, kind == "wired" ? "wireless" : "wired").Should().BeFalse();
        policy.MatchesScope(new string('B', 64), family, kind).Should().BeFalse();
    }

    [Theory]
    [InlineData("wired", "001200000025000200", true)] [InlineData("wired", "00120000002G000100", false)]
    [InlineData("wired", "001300000025000100", false)] [InlineData("wired", "001200000000000000", true)]
    [InlineData("wired", Wireless25, false)] [InlineData("wired", "", false)]
    [InlineData("wireless", "0800080025000100", true)] [InlineData("wireless", "0800070025000200", true)]
    [InlineData("wireless", Wired25, false)] [InlineData("wireless", "08000700250001", false)] [InlineData("wireless", "", false)]
    public void Native_scope_binds_transport_label_grammar_without_interpreting_oem_bytes(string kind, string header, bool matches) =>
        (NativePolicy() with { ConnectionKind = kind, FirmwareHeader = "" }).MatchesScope(NativePolicy().DeviceKey, header, kind).Should().Be(matches);

    [Theory]
    [InlineData("kind-bluetooth")] [InlineData("kind-blank")] [InlineData("kind-case")] [InlineData("key-short")] [InlineData("key-nonhex")]
    public void Native_route_without_a_captured_descriptor_route_is_never_qualified(string fault)
    {
        var policy = fault switch
        {
            "kind-bluetooth" => NativePolicy() with { ConnectionKind = "bluetooth" },
            "kind-blank" => NativePolicy() with { ConnectionKind = "" },
            "kind-case" => NativePolicy() with { ConnectionKind = "Wired" },
            "key-short" => NativePolicy() with { DeviceKey = new string('C', 63) },
            _ => NativePolicy() with { DeviceKey = new string('G', 64) },
        };
        policy.HasQualifiedRoute.Should().BeFalse(); policy.MatchesScope(policy.DeviceKey, WiredFamily, policy.ConnectionKind).Should().BeFalse();
    }

    [Theory]
    [InlineData(Wired25, "")] [InlineData(Wired24, Wired25)] [InlineData(Wired26, "stale note")] [InlineData("001200000000000100", Wired24)]
    public async Task Native_route_journal_of_any_former_family_label_restores_on_the_family_anchor(string recorded, string configured)
    {
        // Older native journals carry the former fixed ...25 route label (or another same-family header); still readable.
        WriteJournal(new { Schema = 2, DeviceKey = Key, FirmwareHeader = recorded, Brightness = (byte)25, ProfileToken = (byte)6 });
        var wired = new WiredFactory(_ => new Io { Patch = 0x24, Brightness = 100 });
        using var service = Make(new KeyboardDisplayOptions(), wired,
            native: new RogNativeMenuSource(NativePolicy() with { DeviceKey = Key, FirmwareHeader = configured }));
        await service.ReleaseForPeripheralConfigurationAsync(default);
        wired.Anchors.Should().Equal(WiredFamily); wired.Devices.Single().Brightness.Should().Be(25);
        File.Exists(JournalPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("cross-transport")] [InlineData("malformed-label")] [InlineData("device")]
    public async Task Native_route_journal_never_crosses_transport_family_or_device(string fault)
    {
        var bytes = WriteJournal(new
        {
            Schema = 2, DeviceKey = fault == "device" ? new string('B', 64) : Key,
            FirmwareHeader = fault switch { "cross-transport" => Wireless25, "malformed-label" => "001300000025000200", _ => Wired25 },
            Brightness = (byte)25, ProfileToken = (byte)6,
        });
        var wired = new WiredFactory(_ => new Io { Patch = 0x24 });
        using var service = Make(new KeyboardDisplayOptions(), wired,
            native: new RogNativeMenuSource(NativePolicy() with { DeviceKey = Key, FirmwareHeader = Wired24 }));
        await ((Func<Task>)(() => service.ReleaseForPeripheralConfigurationAsync(default))).Should().ThrowAsync<IOException>();
        wired.Anchors.Should().BeEmpty(); File.ReadAllBytes(JournalPath).Should().Equal(bytes);
    }

    [Theory]
    [InlineData(Wired24, 0x24)] [InlineData(Wired25, 0x26)] [InlineData("", 0x25)] [InlineData("stale note", 0x51)]
    public async Task Requested_native_route_opens_on_the_family_anchor_and_unsupported_live_grammar_blocks_only_native(string configured, byte patch)
    {
        var source = new RogNativeMenuSource(NativePolicy() with { DeviceKey = Key, FirmwareHeader = configured });
        source.RequestEnabled(true);
        var factory = new WiredFactory(_ => new Io { Patch = patch, AnswerMalformedCapability = true });
        using var service = Make(new KeyboardDisplayOptions(), factory, native: source);
        await service.StartAsync(default);
        try
        {
            await UntilAsync(() => File.Exists(JournalPath)); await Task.Delay(500);
            factory.Anchors.Should().Equal(WiredFamily);
            using (var record = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(JournalPath)))
                record.RootElement.GetProperty("FirmwareHeader").GetString().Should().Be(WiredFamily, "the route fingerprint, never a declared or fake observed header");
            var io = factory.Devices.Single();
            io.NativeArms.Should().Be(0);
            io.NativeQueries.Should().BeLessThanOrEqualTo(1, "an unsupported live grammar is native-unavailable, never probed in a loop");
            (await service.GetKeyboardDisplayStatusAsync()).State.Should().NotBe("Error");
        }
        finally { await service.StopAsync(default); }
        factory.Devices.Single().Brightness.Should().Be(25);
        File.Exists(JournalPath).Should().BeFalse("the same owner restored the original state");
    }

    [Fact]
    public async Task Requested_wireless_native_route_opens_on_the_family_anchor_whatever_the_configured_header()
    {
        var source = new RogNativeMenuSource(NativePolicy() with { DeviceKey = Key, ConnectionKind = "wireless", FirmwareHeader = "" });
        source.RequestEnabled(true);
        var wireless = new WirelessFactory(_ => new WirelessChannel(new Io { Patch = 0x26, AnswerMalformedCapability = true }, receiverPatch: 9));
        using var native = Make(new KeyboardDisplayOptions(), new WiredFactory(_ => throw new InvalidOperationException("No wired route")), wireless,
            native: source);
        await native.StartAsync(default);
        try
        {
            await UntilAsync(() => File.Exists(JournalPath)); await Task.Delay(500);
            wireless.Anchors.Should().Equal(WirelessFamily);
            wireless.Channels.Single().Inner.NativeArms.Should().Be(0);
            (await native.GetKeyboardDisplayStatusAsync()).State.Should().NotBe("Error");
        }
        finally { await native.StopAsync(default); }
        wireless.Channels.Single().Inner.Brightness.Should().Be(25); File.Exists(JournalPath).Should().BeFalse();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, bound.Token);
    }
}
