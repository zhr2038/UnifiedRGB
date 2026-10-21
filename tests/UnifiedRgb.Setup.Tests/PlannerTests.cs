using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Host;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup.Tests;

public sealed class PlannerTests
{
    /// <summary>An existing installation: unknown fields, a copied foreign device list, calibrated mounting, all gates closed.</summary>
    internal const string Existing = """
        {
          "UnifiedRgb": {
            "Fps": 60,
            "UseVirtualAdapter": true,
            "EnableLianLi": false,
            "EnableMsiGpu": false,
            "EnableAsusAura": false,
            "EnableEneDram": false,
            "FutureSwitch": { "Keep": [1, 2, 3] },
            "MsiGpu": { "RegisterMapVerified": false, "ControlVerified": false },
            "AsusAura": { "ControlVerified": false, "HidTimeoutMs": 250, "Headers": [ { "LedCount": 30 } ] },
            "EneDram": { "PawnIoModuleVerified": false, "ControlVerified": false, "SleepMode": 2, "CandidateAddresses": [ 113 ] },
            "Screens": { "Enabled": false, "TurzxLandscapeRotation": 270 },
            "LianLi": {
              "WireFormatVerified": false,
              "MasterChannel": 8,
              "UseDeviceClock": true,
              "Devices": [ { "Mac": "99:99:99:00:00:01", "RecType": 28, "FanNum": 3, "Leds": 78, "Channel": 8, "Rx": 2,
                             "Mounting": { "RotationDegrees": 180, "Reverse": true } } ]
            }
          },
          "Serilog": { "MinimumLevel": { "Default": "Information" } }
        }
        """;

    private static SetupConfigSnapshot Snapshot(string json) => Snapshot(Encoding.UTF8.GetBytes(json));

    private static SetupConfigSnapshot Snapshot(byte[] bytes)
    {
        string? error = null;
        try
        {
            SetupConfigDocument.Parse(bytes);
        }
        catch (SetupConfigException ex)
        {
            error = ex.Message;
        }

        return new("C:\\x\\appsettings.json", true, SetupConfigDocument.Sha256(bytes), bytes, error);
    }

    private static readonly SetupConfigSnapshot Absent = new("C:\\x\\appsettings.json", false, SetupConfigSnapshot.AbsentSha256, null, null);

    private static SetupReport FullReport()
    {
        var system = FakeSetupSystem.WithLianLi(Reply.TypicalCase());
        system.InstanceIds.AddRange(
        [
            @"PCI\VEN_10DE&DEV_2B85&SUBSYS_53011462&REV_A1\4&1", @"PCI\VEN_1022&DEV_790B&SUBSYS_88771043&REV_71\3&2",
            @"USB\VID_1CBE&PID_0006\A1", @"USB\VID_1CBE&PID_A021\B1", @"USB\VID_1CBE&PID_0088\C1",
        ]);
        return system.Inspect();
    }

    private static SetupSelection Hardware(params SetupFamilySelection[] families) => new(SetupMode.Hardware, families);

    private static JsonObject App(SetupPlan plan) => (JsonObject)JsonNode.Parse(plan.ProposedJson)!["UnifiedRgb"]!;

    [Fact]
    public void Demo_mode_needs_no_hardware_and_keeps_everything_else_verbatim()
    {
        var report = new FakeSetupSystem().Inspect();

        var plan = SetupPlanner.BuildPlan(report, new SetupSelection(SetupMode.Demo, []), Snapshot(Existing));

        plan.CanSave.Should().BeTrue();
        var app = App(plan);
        app["UseVirtualAdapter"]!.GetValue<bool>().Should().BeTrue();
        app["FutureSwitch"]!.ToJsonString().Should().Be("""{"Keep":[1,2,3]}""");
        app["LianLi"]!["Devices"]![0]!["Mounting"]!["RotationDegrees"]!.GetValue<int>().Should().Be(180);
        app["AsusAura"]!["Headers"]![0]!["LedCount"]!.GetValue<int>().Should().Be(30);
        JsonNode.Parse(plan.ProposedJson)!["Serilog"]!["MinimumLevel"]!["Default"]!.GetValue<string>().Should().Be("Information");
        plan.Changes.Select(c => c.Path).Should().Equal(["UnifiedRgb:EnableOpenRgb"], "everything else already is a demo configuration");
    }

    [Fact]
    public void Lian_Li_output_replaces_the_foreign_device_list_with_this_computers_devices_only()
    {
        var report = FullReport();
        var macs = report.LianLi.Proposals.Select(p => p.Mac).ToArray();
        var selection = Hardware(new SetupFamilySelection(SetupFamily.LianLi, true, true)) with { AcceptedLianLiMacs = macs };

        var plan = SetupPlanner.BuildPlan(report, selection, Snapshot(Existing));

        plan.CanSave.Should().BeTrue(string.Join("; ", plan.Findings.Select(f => f.Message)));
        plan.ProposedJson.Should().NotContain("99:99:99", "a copied device list is not this computer's");
        plan.ProposedJson.Should().NotContain("Mounting", "setup never proposes mounting or calibration");
        plan.GateChanges.Should().ContainSingle(g => g.Path == SetupPlanner.LianLiWireGatePath && g.After);
        plan.Outcomes.Single(o => o.Family == SetupFamily.LianLi).Playback.Should().Be(SetupPlaybackState.OutputUnverified);

        // the planned file binds into the Host's real options and the adapter drives exactly the proposals
        var options = Bind(plan);
        options.UseVirtualAdapter.Should().BeFalse();
        options.EnableLianLi.Should().BeTrue();
        options.LianLi.YuzDllPath.Should().BeEmpty();
        options.LianLi.UseDeviceClock.Should().BeTrue("unrelated existing values are preserved");
        var devices = LianLiDiscovery.Discover(options.LianLi, new OneSender(), NullLogger.Instance);
        devices.Select(d => d.Mac.ToString()).Should().BeEquivalentTo(macs.Select(m => m.Replace(':', '-').ToUpperInvariant()));
        options.EneDram.ControlVerified.Should().BeFalse();
        options.MsiGpu.ControlVerified.Should().BeFalse();
    }

    [Fact]
    public void Rechecking_the_same_device_preserves_its_mounting_and_future_fields()
    {
        var report = FullReport();
        var mac = report.LianLi.Proposals[0].Mac;
        var existing = Existing.Replace("99:99:99:00:00:01", mac, StringComparison.Ordinal).Replace("\"Mac\"", "\"mac\"", StringComparison.Ordinal);
        var selection = Hardware(new SetupFamilySelection(SetupFamily.LianLi, true, true)) with { AcceptedLianLiMacs = [mac] };
        var plan = SetupPlanner.BuildPlan(report, selection, Snapshot(existing));
        plan.CanSave.Should().BeTrue();
        SetupConfigDocument.Parse(Encoding.UTF8.GetBytes(plan.ProposedJson)); // no case-duplicate device keys
        App(plan)["LianLi"]!["Devices"]![0]!["Mounting"]!["RotationDegrees"]!.GetValue<int>().Should().Be(180);
        Bind(plan).LianLi.Codec.Should().Be(LianLiCompressionCodec.Managed);
    }

    [Fact]
    public void Discovery_only_closes_the_Lian_Li_wire_gate_and_keeps_the_device_list()
    {
        var existing = Existing.Replace("\"WireFormatVerified\": false", "\"WireFormatVerified\": true", StringComparison.Ordinal);

        var plan = SetupPlanner.BuildPlan(FullReport(), Hardware(new SetupFamilySelection(SetupFamily.LianLi, true, false)), Snapshot(existing));

        plan.CanSave.Should().BeTrue();
        App(plan)["LianLi"]!["WireFormatVerified"]!.GetValue<bool>().Should().BeFalse();
        App(plan)["LianLi"]!["Devices"]![0]!["Mac"]!.GetValue<string>().Should().Be("99:99:99:00:00:01");
        plan.Findings.Should().Contain(f => f.Code == "ForeignDevice");
        plan.Outcomes.Single(o => o.Family == SetupFamily.LianLi).Playback.Should().Be(SetupPlaybackState.DiscoveryOnly);
    }

    [Theory]
    [InlineData(null, SetupInspector.LianLiInterfaceGuid)]
    [InlineData("", SetupInspector.LianLiInterfaceGuid)]
    [InlineData("not-a-guid", SetupInspector.LianLiInterfaceGuid)]
    [InlineData("00000000-0000-0000-0000-000000000000", SetupInspector.LianLiInterfaceGuid)]
    [InlineData("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    public void Enabling_Lian_Li_supplies_the_dongle_interface_class_only_when_the_existing_one_is_unusable(string? existing, string expected)
    {
        var json = existing is null
            ? """{ "UnifiedRgb": { "LianLi": { } } }"""
            : $$"""{ "UnifiedRgb": { "LianLi": { "ReceiverInterfaceGuid": "{{existing}}" } } }""";

        var plan = SetupPlanner.BuildPlan(FullReport(), Hardware(new SetupFamilySelection(SetupFamily.LianLi, true, false)), Snapshot(json));

        plan.CanSave.Should().BeTrue();
        Bind(plan).LianLi.ReceiverInterfaceGuid.Should().Be(expected, "the Host's own default (empty) would disable every sender");
    }

    [Theory]
    [InlineData(SetupFamily.MsiGpu, "MsiGpu")]
    [InlineData(SetupFamily.AsusAura, "AsusAura")]
    [InlineData(SetupFamily.EneDram, "EneDram")]
    public void User_consent_never_sets_ControlVerified_and_the_writer_stays_blocked(SetupFamily family, string section)
    {
        var report = FullReport() is var r && family == SetupFamily.AsusAura ? WithAura(r) : r;

        var plan = SetupPlanner.BuildPlan(report, Hardware(new SetupFamilySelection(family, true, true)), Snapshot(Existing));

        plan.CanSave.Should().BeTrue(string.Join("; ", plan.Findings.Select(f => f.Message)));
        App(plan)[$"Enable{section}"]!.GetValue<bool>().Should().BeTrue();
        App(plan)[section]!["ControlVerified"]!.GetValue<bool>().Should().BeFalse();
        plan.GateChanges.Should().NotContain(g => g.Path.EndsWith("Verified", StringComparison.Ordinal));
        plan.Outcomes.Single(o => o.Family == family).Playback.Should().Be(SetupPlaybackState.BlockedByVerificationGate);
    }

    [Fact]
    public void ENE_uses_the_packaged_relative_module_and_keeps_its_pins_and_gates()
    {
        var plan = SetupPlanner.BuildPlan(FullReport(), Hardware(new SetupFamilySelection(SetupFamily.EneDram, true, false)), Snapshot(Existing));

        var ene = Bind(plan).EneDram;
        ene.ModulePath.Should().Be(SetupEnvironment.DefaultPawnIoModuleRelativePath);
        ene.PawnIoLibPath.Should().Be(FakeSetupSystem.PawnIoLibPath);
        ene.ModuleSha256.Should().Be(Devices.EneDram.EneDramDefaults.ModuleSha256);
        ene.PawnIoModuleVerified.Should().BeFalse();
        ene.CandidateAddresses.Should().Equal(113);
    }

    [Fact]
    public void Previously_verified_class_must_be_explicitly_authorized_or_disabled()
    {
        var existing = Existing.Replace("\"MsiGpu\": { \"RegisterMapVerified\": false, \"ControlVerified\": false }",
            "\"MsiGpu\": { \"RegisterMapVerified\": true, \"ControlVerified\": true }", StringComparison.Ordinal);

        var discoveryOnly = SetupPlanner.BuildPlan(FullReport(), Hardware(new SetupFamilySelection(SetupFamily.MsiGpu, true, false)), Snapshot(existing));
        discoveryOnly.CanSave.Should().BeFalse("enabling would write although only discovery was chosen");
        discoveryOnly.Findings.Should().Contain(f => f.Code == "OutputAlreadyGranted");

        var authorized = SetupPlanner.BuildPlan(FullReport(), Hardware(new SetupFamilySelection(SetupFamily.MsiGpu, true, true)), Snapshot(existing));
        authorized.CanSave.Should().BeTrue();
        authorized.Outcomes.Single(o => o.Family == SetupFamily.MsiGpu).Playback.Should().Be(SetupPlaybackState.PreviouslyVerified);
        App(authorized)["MsiGpu"]!["ControlVerified"]!.GetValue<bool>().Should().BeTrue("existing evidence is preserved, not cleared");
    }

    [Fact]
    public void Screen_output_needs_the_master_gate_and_only_the_authorized_families()
    {
        var plan = SetupPlanner.BuildPlan(FullReport(), Hardware(
            new SetupFamilySelection(SetupFamily.ScreenH2, true, true),
            new SetupFamilySelection(SetupFamily.ScreenTlLcd, true, false)), Snapshot(Existing));

        var screens = Bind(plan).Screens;
        screens.Enabled.Should().BeTrue();
        screens.H2.Output.Should().BeTrue();
        screens.TlLcd.Output.Should().BeFalse();
        screens.Turzx.Output.Should().BeFalse();
        screens.H2.ControlVerified.Should().BeFalse();
        screens.TurzxLandscapeRotation.Should().Be(270, "unrelated screen settings are preserved");
        plan.Outcomes.Single(o => o.Family == SetupFamily.ScreenH2).Playback.Should().Be(SetupPlaybackState.OutputUnverified);
    }

    public static TheoryData<string, SetupSelection> InvalidSelections => new()
    {
        { "SelectionInvalid", new(SetupMode.Hardware, [new(SetupFamily.MsiGpu, false, true)]) },
        { "SelectionInvalid", new(SetupMode.Demo, [new(SetupFamily.LianLi, true, false)]) },
        { "SelectionInvalid", new(SetupMode.Hardware, [new(SetupFamily.MsiGpu, true, false), new(SetupFamily.MsiGpu, false, false)]) },
        { "NotAvailable", new(SetupMode.Hardware, [new(SetupFamily.AsusAura, true, false)]) },
        { "NotAvailable", new(SetupMode.Hardware, [new(SetupFamily.ScreenTurzx, true, true)]) },
        { "UnknownDevice", new(SetupMode.Hardware, [new(SetupFamily.LianLi, true, false)], ["99:99:99:00:00:01"]) },
        { "SelectionInvalid", new(SetupMode.Hardware, [], ["11:11:11:00:00:01"]) },
    };

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public void Invalid_selections_produce_an_unsavable_plan_without_throwing(string code, SetupSelection selection)
    {
        var report = FullReport();
        var noTurzx = report with
        {
            Families = report.Families.Select(f => f.Family == SetupFamily.ScreenTurzx ? f with { CanEnable = false, CanAuthorizeOutput = false } : f).ToArray(),
        };

        var plan = SetupPlanner.BuildPlan(noTurzx, selection, Snapshot(Existing));

        plan.CanSave.Should().BeFalse();
        plan.Findings.Should().Contain(f => f.Severity == SetupSeverity.Error && f.Code == code);
    }

    [Theory]
    [InlineData("""{ "UnifiedRgb": { "EnableLianLi": "yes" } }""")]
    [InlineData("""{ "UnifiedRgb": { "LianLi": [] } }""")]
    [InlineData("""{ "UnifiedRgb": [] }""")]
    [InlineData("""{ "UnifiedRgb": { "Screens": { "Enabled": null } } }""")]
    public void Unexpected_types_at_touched_keys_refuse_instead_of_coercing(string json)
    {
        var plan = SetupPlanner.BuildPlan(FullReport(), Hardware(
            new SetupFamilySelection(SetupFamily.LianLi, true, false), new SetupFamilySelection(SetupFamily.ScreenH2, true, true)), Snapshot(json));

        plan.CanSave.Should().BeFalse();
        plan.Findings.Should().Contain(f => f.Code == "UnexpectedJson");
    }

    [Fact]
    public void Fresh_installation_without_a_file_gets_a_minimal_portable_configuration()
    {
        var report = FullReport();
        var plan = SetupPlanner.BuildPlan(report, Hardware(new SetupFamilySelection(SetupFamily.LianLi, true, true))
            with { AcceptedLianLiMacs = [report.LianLi.Proposals[0].Mac] }, Absent);

        plan.CanSave.Should().BeTrue();
        plan.BaseExists.Should().BeFalse();
        plan.ProposedJson.Should().NotContainAny(@"C:\\", "PawnIoLibPath", "ProgramData");
        Bind(plan).LianLi.Devices.Should().ContainSingle();
    }

    [Fact]
    public void Case_variant_keys_are_edited_in_place_not_duplicated()
    {
        var plan = SetupPlanner.BuildPlan(new FakeSetupSystem().Inspect(), new SetupSelection(SetupMode.Demo, []),
            Snapshot("""{ "unifiedrgb": { "usevirtualadapter": false, "x": 1 } }"""));

        plan.CanSave.Should().BeTrue();
        var root = JsonNode.Parse(plan.ProposedJson)!.AsObject();
        root.Select(kv => kv.Key).Should().Equal("unifiedrgb");
        root["unifiedrgb"]!["usevirtualadapter"]!.GetValue<bool>().Should().BeTrue();
        root["unifiedrgb"]!.AsObject().Count(kv => kv.Key.Equals("UseVirtualAdapter", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    private static SetupReport WithAura(SetupReport r) => r with
    {
        Families = r.Families.Select(f => f.Family == SetupFamily.AsusAura ? f with { Support = SetupSupport.Supported, CanEnable = true, CanAuthorizeOutput = true } : f).ToArray(),
    };

    private static UnifiedRgbOptions Bind(SetupPlan plan)
    {
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(plan.ProposedJson))).Build();
        var options = new UnifiedRgbOptions();
        config.GetSection(UnifiedRgbOptions.Section).Bind(options);
        return options;
    }

    private sealed class OneSender : IUsbEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass) => [FakeSetupSystem.Sender()];
    }
}
