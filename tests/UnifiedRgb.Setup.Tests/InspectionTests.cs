using System.Security.AccessControl;
using UnifiedRgb.Devices.AsusAura;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Setup.Tests;

public sealed class InspectionTests
{
    [Fact]
    public void A_computer_without_hardware_or_optional_resources_still_gets_a_complete_report()
    {
        var system = new FakeSetupSystem();

        var report = system.Inspect();

        report.Families.Should().HaveCount(Enum.GetValues<SetupFamily>().Length);
        report.Families.Should().OnlyContain(f => f.Support == SetupSupport.NotPresent && !f.CanEnable && !f.CanAuthorizeOutput);
        report.LianLi.State.Should().Be(LianLiPairingState.NoReceiver);
        report.Resources.Where(r => r.Name != SetupInspector.CodecResource).Should().OnlyContain(r => r.State == SetupResourceState.Missing);
        report.Resource(SetupInspector.CodecResource)!.State.Should().Be(SetupResourceState.Verified);
        report.Findings.Should().Contain(f => f.Code == "NoSupportedHardware");
        system.Queries.Should().Be(0);
    }

    [Fact]
    public void Unknown_or_unvalidated_hardware_is_reported_unsupported_never_enabled()
    {
        var system = new FakeSetupSystem();
        system.AddPinnedResources();
        system.InstanceIds.AddRange(
        [
            @"PCI\VEN_10DE&DEV_2B85&SUBSYS_88881043&REV_A1\4&1",   // same GPU chip, other board vendor
            @"PCI\VEN_10DE&DEV_2684&SUBSYS_53011462&REV_A1\4&2",   // other NVIDIA chip
            @"USB\VID_1CBE&PID_0099\SER1",                         // unknown Lian Li/TURZX product id
        ]);
        system.Aura.Add(new HidCollectionInfo("p", AsusAuraDefaults.Vid, AsusAuraDefaults.Pid, 2, 0xFF00, 0x0001, 65, 65, 0));

        var report = system.Inspect();

        report.Family(SetupFamily.MsiGpu).Support.Should().Be(SetupSupport.Unsupported);
        report.Family(SetupFamily.AsusAura).Support.Should().Be(SetupSupport.Unsupported);
        report.Families.Where(f => f.Family >= SetupFamily.ScreenTlLcd).Should().OnlyContain(f => f.Support == SetupSupport.NotPresent);
        report.Families.Should().OnlyContain(f => !f.CanEnable && !f.CanAuthorizeOutput);
    }

    [Fact]
    public void Supported_identities_are_recognised_from_read_only_ids()
    {
        var system = FakeSetupSystem.WithLianLi(Reply.TypicalCase());
        system.InstanceIds.AddRange(
        [
            @"PCI\VEN_10DE&DEV_2B85&SUBSYS_53011462&REV_A1\4&1",
            @"PCI\VEN_1022&DEV_790B&SUBSYS_88771043&REV_71\3&2",
            @"USB\VID_1CBE&PID_0006\A1", @"USB\VID_1CBE&PID_0006&MI_00\7&1", @"USB\VID_1CBE&PID_0006\A2",
            @"USB\VID_1CBE&PID_A021\B1",
        ]);
        system.Aura.Add(new HidCollectionInfo(@"\\?\hid#vid_0b05&pid_19af&mi_02#x", AsusAuraDefaults.Vid, AsusAuraDefaults.Pid, 2,
            AsusAuraDefaults.UsagePage, AsusAuraDefaults.Usage, AsusAuraDefaults.Rule.ReportLength, AsusAuraDefaults.Rule.ReportLength, 0));

        var report = system.Inspect();

        report.Family(SetupFamily.LianLi).Support.Should().Be(SetupSupport.Supported);
        report.Family(SetupFamily.MsiGpu).Support.Should().Be(SetupSupport.Supported);
        report.Family(SetupFamily.AsusAura).Support.Should().Be(SetupSupport.Supported);
        report.Family(SetupFamily.EneDram).Support.Should().Be(SetupSupport.Unknown, "DRAM presence needs SMBus probing, which setup never does");
        report.Family(SetupFamily.EneDram).CanEnable.Should().BeTrue();
        report.Family(SetupFamily.ScreenTlLcd).Identities.Single().Should().EndWith("x2");
        report.Family(SetupFamily.ScreenH2).Support.Should().Be(SetupSupport.Supported);
        report.Family(SetupFamily.ScreenTurzx).Support.Should().Be(SetupSupport.NotPresent);
        system.Queries.Should().Be(1);
    }

    public static TheoryData<string, SetupResourceState> CodecProblems => new()
    {
        { "missing", SetupResourceState.Missing },
        { "hash", SetupResourceState.HashMismatch },
        { "untrusted", SetupResourceState.UntrustedLocation },
    };

    [Theory]
    [MemberData(nameof(CodecProblems))]
    public void Managed_Lian_Li_output_does_not_inspect_or_load_a_legacy_manufacturer_DLL(string problem, SetupResourceState state)
    {
        var system = FakeSetupSystem.WithLianLi(Reply.TypicalCase());
        switch (problem)
        {
            case "missing":
                system.Files.Remove(FakeSetupSystem.CodecPath);
                break;
            case "hash":
                system.Files[FakeSetupSystem.CodecPath] = new(true, 1000, new string('0', 64), null);
                break;
            default:
                system.Untrusted.Add(FakeSetupSystem.CodecPath);
                break;
        }

        var report = system.Inspect();

        _ = state;
        report.Resource(SetupInspector.CodecResource)!.State.Should().Be(SetupResourceState.Verified);
        system.InspectedFiles.Should().NotContain(FakeSetupSystem.CodecPath);
        report.Family(SetupFamily.LianLi).Support.Should().Be(SetupSupport.Supported);
        report.Family(SetupFamily.LianLi).CanAuthorizeOutput.Should().BeTrue();
        report.LianLi.Proposals.Should().HaveCount(4, "pairing discovery itself does not need the codec");
    }

    [Fact]
    public void A_refused_PawnIO_api_blocks_ENE_and_hash_mismatch_never_loads_the_library()
    {
        var system = new FakeSetupSystem();
        system.AddPinnedResources();
        system.InstanceIds.Add(@"PCI\VEN_1022&DEV_790B&SUBSYS_00000000&REV_71\3&2");
        system.PawnIoProbe = null;

        var refused = system.Inspect();

        refused.Resource(SetupInspector.PawnIoLibraryResource)!.State.Should().Be(SetupResourceState.ApiMismatch);
        refused.Family(SetupFamily.EneDram).Support.Should().Be(SetupSupport.PrerequisiteMissing);

        var probed = 0;
        system.PawnIoProbe = _ => { probed++; return "ok"; };
        system.Files[FakeSetupSystem.PawnIoLibPath] = new(true, 1000, new string('A', 64), "9.9.9");
        system.Inspect().Resource(SetupInspector.PawnIoLibraryResource)!.State.Should().Be(SetupResourceState.HashMismatch);
        probed.Should().Be(0);
    }

    [Theory]
    [InlineData(@"..\..\..\Users\Public\yuz.dll")]
    [InlineData(@"Resources\..\..\yuz.dll")]
    [InlineData(@"C:\Users\Public\yuz.dll")]
    [InlineData(@"C:yuz.dll")]
    [InlineData(@"\\server\share\yuz.dll")]
    [InlineData(@"\Windows\yuz.dll")]
    [InlineData(@"Resources\yuz.dll:evil")]
    [InlineData(@"Resources\*.dll")]
    [InlineData(@"Resources\yuz.dll.")]
    [InlineData("")]
    public void Escaping_or_non_plain_resource_paths_are_never_inspected(string relative)
    {
        var system = new FakeSetupSystem();

        var report = new SetupInspector(system, new SetupEnvironment(FakeSetupSystem.InstallDir, PawnIoModuleRelativePath: relative))
            .Inspect(CancellationToken.None);

        report.Resource(SetupInspector.PawnIoModuleResource)!.State.Should().Be(SetupResourceState.InvalidPath);
        system.InspectedFiles.Should().NotContain(p => p.EndsWith("yuz.dll", StringComparison.OrdinalIgnoreCase) || p.Contains("evil"));
    }

    [Theory]
    [InlineData(@"relative\dir")]
    [InlineData(@"\\server\share\app")]
    public void Install_directory_must_be_a_local_absolute_path(string dir)
    {
        SetupPaths.TryResolveInstallRelative(dir, SetupEnvironment.DefaultCodecRelativePath, out _, out _).Should().BeFalse();
    }

    private const string User = "S-1-5-21-1-2-3-1001";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";

    [Fact]
    public void Administrator_only_file_with_user_read_access_is_trusted()
    {
        NativePathTrust.Evaluate("f", NativePathTrust.Administrators,
        [
            new(NativePathTrust.Administrators, true, FileSystemRights.FullControl, false),
            new(NativePathTrust.LocalSystem, true, FileSystemRights.FullControl, false),
            new(Users, true, FileSystemRights.ReadAndExecute, false),
            new(Users, true, FileSystemRights.Modify, InheritOnly: true),
        ], isDirectory: false, isRoot: false).Should().BeNull();
    }

    [Theory]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.Delete)]
    public void A_file_users_can_change_is_untrusted_even_when_its_hash_matches(FileSystemRights rights)
    {
        NativePathTrust.Evaluate("f", NativePathTrust.Administrators, [new(AuthenticatedUsers, true, rights, false)], false, false)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_user_owned_file_or_a_directory_users_can_empty_is_untrusted()
    {
        NativePathTrust.Evaluate("f", User, [], false, false).Should().NotBeNull();
        NativePathTrust.Evaluate("d", NativePathTrust.TrustedInstaller,
            [new(Users, true, FileSystemRights.DeleteSubdirectoriesAndFiles, false)], isDirectory: true, isRoot: false).Should().NotBeNull();
    }

    [Fact]
    public void Deny_entries_and_a_drive_roots_own_delete_right_do_not_matter()
    {
        NativePathTrust.Evaluate("C:\\", NativePathTrust.TrustedInstaller,
        [
            new(AuthenticatedUsers, true, FileSystemRights.Delete | FileSystemRights.CreateDirectories, false),
            new(Users, false, FileSystemRights.FullControl, false),
        ], isDirectory: true, isRoot: true).Should().BeNull();
    }
}
