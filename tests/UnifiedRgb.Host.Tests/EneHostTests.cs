using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusAura;
using UnifiedRgb.Devices.EneDram;
using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// NATIVE-2/3 Host wiring: the source appsettings keep every native gate closed, factories are registered only when their
/// class is enabled (hold-black copied from Bringup), and a real <see cref="EneDramAdapter"/> over a scripted bus runs a
/// leased port session through the Host while no writer ever opens.
/// </summary>
public sealed class EneHostTests
{
    /// <summary>Only <c>pawnio_version</c>; any other call fails the test.</summary>
    private sealed class VersionOnly(uint raw) : IPawnIoLibrary
    {
        public int Version(out uint version)
        {
            version = raw;
            return 0;
        }

        public int Open(out IntPtr handle) => throw new NotSupportedException();

        public int Load(IntPtr handle, ReadOnlySpan<byte> blob) => throw new NotSupportedException();

        public int Execute(IntPtr handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output, out nuint returned) => throw new NotSupportedException();

        public int Close(IntPtr handle) => throw new NotSupportedException();
    }

    [Fact]
    public void Source_appsettings_bind_with_every_native_gate_closed()
    {
        var path = Path.Combine(RepoRoot(), "src", "UnifiedRgb.Host", "appsettings.json");
        var o = new UnifiedRgbOptions();
        new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(UnifiedRgbOptions.Section).Bind(o);

        o.UseVirtualAdapter.Should().BeTrue();
        (o.EnableLianLi, o.EnableMsiGpu, o.EnableOpenRgb, o.EnableAsusAura, o.EnableEneDram).Should().Be((false, false, false, false, false));
        o.LianLi.WireFormatVerified.Should().BeFalse();
        (o.MsiGpu.RegisterMapVerified, o.MsiGpu.ControlVerified).Should().Be((false, false));
        o.AsusAura.ControlVerified.Should().BeFalse();
        o.AsusAura.Headers.Should().BeEmpty();
        o.EneDram.Should().BeEquivalentTo(new EneDramOptions(), "the source file states exactly the closed defaults");
        o.EneDram.ModulePath.Should().BeEmpty("unset until Codex points it at the pinned tools/pawnio module");
        (o.EneDram.PawnIoApiVersion, o.EneDram.PawnIoLibVersion).Should().Be(("2.0.0", "2.2.0"));
        File.ReadAllText(path).Should().Contain("\"PawnIoApiVersion\": \"2.0.0\"", "the API pin is stated in the file, not only defaulted");
        o.Bringup.HoldBlackOnRelease.Should().BeFalse();
    }

    [Fact]
    public void Settings_with_only_the_old_version_key_bind_and_still_gate_on_api_2_0_0()
    {
        // an OUTPUT file written before the correction: PawnIoLibVersion only, now the file release label
        const string json = """
            { "UnifiedRgb": { "EneDram": {
                "PawnIoLibPath": "C:\\Program Files\\PawnIO\\PawnIOLib.dll",
                "PawnIoLibSha256": "D71F62627D66983BB9F5B1C269F27BCD8C1B8A46E794377A6330F84C198F4443",
                "PawnIoLibVersion": "2.2.0",
                "ModuleSha256": "91F9B4B1C39E3D399CE48477A89D8F6BD3E58A2241064A4DAEC3A1513DFF56E5" } } }
            """;
        var o = new UnifiedRgbOptions();
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
        {
            new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(UnifiedRgbOptions.Section).Bind(o);
        }

        (o.EneDram.PawnIoApiVersion, o.EneDram.PawnIoLibVersion).Should().Be(("2.0.0", "2.2.0"));
        o.EneDram.Should().BeEquivalentTo(new EneDramOptions(), "nothing else moved either");
        using var provider = new PawnIoSmbusProvider(o.EneDram);
        provider.Pins.Should().Contain("libApiExpected", "2.0.0").And.Contain("libFileRelease", "2.2.0").And.NotContainKey("libVersion");
        provider.Version.Should().BeNull("constructing the provider loads nothing");

        PawnIoPin.VerifyVersion(new VersionOnly(0x00020000), o.EneDram.PawnIoApiVersion).Admitted.Should().BeTrue();
        var conflated = () => PawnIoPin.VerifyVersion(new VersionOnly(0x00020200), o.EneDram.PawnIoApiVersion);
        conflated.Should().Throw<DeviceBlockedException>().WithMessage("PawnIOLib exported API version 2.2.0 is not the pinned 2.0.0*");
    }

    [Fact]
    public void Ene_and_aura_options_bind_from_configuration()
    {
        var o = new UnifiedRgbOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UnifiedRgb:EneDram:ModulePath"] = @"tools\pawnio\SmbusPIIX4.bin",
            ["UnifiedRgb:EneDram:PawnIoModuleVerified"] = "true",
            ["UnifiedRgb:EneDram:CandidateAddresses:0"] = "112",
            ["UnifiedRgb:EneDram:CandidateAddresses:1"] = "119",
            ["UnifiedRgb:EneDram:FrameBudgetMs"] = "200",
            ["UnifiedRgb:AsusAura:Headers:0:LedCount"] = "0",
            ["UnifiedRgb:AsusAura:Headers:1:LedCount"] = "30",
        }).Build().GetSection(UnifiedRgbOptions.Section).Bind(o);

        o.EneDram.ModulePath.Should().Be(@"tools\pawnio\SmbusPIIX4.bin");
        o.EneDram.PawnIoModuleVerified.Should().BeTrue();
        o.EneDram.ControlVerified.Should().BeFalse();
        o.EneDram.CandidateAddresses.Should().Equal(0x70, 0x77);
        o.EneDram.FrameBudgetMs.Should().Be(200);
        o.AsusAura.Headers.Select(h => h.LedCount).Should().Equal(0, 30);
    }

    [Fact]
    public void Native_factories_are_registered_only_when_enabled_and_inherit_hold_black()
    {
        static List<Type?> Factories(UnifiedRgbOptions o)
        {
            var services = new ServiceCollection();
            services.AddUnifiedRgb(o, enableIpc: false);
            return [.. services.Where(d => d.ServiceType == typeof(IDeviceAdapterFactory)).Select(d => d.ImplementationType)];
        }

        Factories(new UnifiedRgbOptions { UseVirtualAdapter = false }).Should().BeEmpty();

        var on = new UnifiedRgbOptions { UseVirtualAdapter = false, EnableMsiGpu = true, EnableAsusAura = true, EnableEneDram = true };
        on.Bringup.HoldBlackOnRelease = true;
        var services = new ServiceCollection();
        services.AddUnifiedRgb(on, enableIpc: false);
        services.Where(d => d.ServiceType == typeof(IDeviceAdapterFactory)).Select(d => d.ImplementationType)
            .Should().Equal(typeof(MsiGpuAdapterFactory), typeof(AsusAuraAdapterFactory), typeof(EneDramAdapterFactory));
        on.EneDram.HoldBlackOnRelease.Should().BeTrue();
        on.AsusAura.HoldBlackOnRelease.Should().BeTrue();
        on.MsiGpu.HoldBlackOnRelease.Should().BeTrue();
        services.Single(d => d.ServiceType == typeof(EneDramOptions)).ImplementationInstance.Should().BeSameAs(on.EneDram);
        on.EneDram.PawnIoModuleVerified.Should().BeFalse("only the hold-black flag is copied");
        on.EneDram.ControlVerified.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Port_session_scans_marks_and_releases_while_no_writer_opens(bool holdBlack)
    {
        var bus = new ScriptedEneBus();
        var ene = new EneDramAdapter(new ScriptedEneProvider(bus), new EneDramOptions { HoldBlackOnRelease = holdBlack });
        var guard = new FakeGuard().Enable(DeviceClass.EneDram);
        var frames = new FrameBus();
        var adapters = new AdapterHost([new InstanceFactory(ene)], null!, guard, frames, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await adapters.StartAsync(default);
        var bringup = new BringupManager(adapters, new RenderLoop(EffectRegistry.Default, frames), guard,
            new BringupOptions { HoldBlackOnRelease = holdBlack }, new ProbeLog(null), new ManualTimeProvider());
        try
        {
            adapters.Controllers.Select(c => c.Descriptor.Id.Value).Should().Equal("enedram:p0", "enedram:p1");
            adapters.Controllers.Should().OnlyContain(c => c.Writer == null && !c.IsActive);
            bus.Sent.Should().BeEmpty("discovery of an unverified class touches no bus");

            var session = await bringup.BeginAsync("pipe:A", "enedram:p0", 60, default);
            session.Probes.Should().Equal("pawnio-id", "ene-scan", "ene-remap");
            session.HoldBlackOnRelease.Should().Be(holdBlack);
            var scan = await bringup.RunProbeAsync("pipe:A", "enedram:p0", "ene-scan", default);
            scan.Ok.Should().BeTrue();
            scan.Fields["controllers"].Should().Be("0x70");
            bus.Sent.Should().NotContain(c => c.Kind == SmbusCompoundKind.RegWrite || c.Kind == SmbusCompoundKind.RegBlock);

            if (holdBlack)
            {
                var white = () => bringup.BeginMarkerAsync("pipe:A", new MarkerDto("enedram:p0", 0, 8, "#FFFFFF", 0x70), default);
                await white.Should().ThrowAsync<ArgumentException>();
            }

            (await bringup.BeginMarkerAsync("pipe:A", new MarkerDto("enedram:p0", 0, 8, "#000000", 0x70), default)).Ok.Should().BeTrue();
            (await bringup.EndMarkerAsync("pipe:A", "enedram:p0", default)).Ok.Should().BeTrue();
            bus.Sent.Clear();
            var end = await bringup.EndAsync("pipe:A", "enedram:p0");
            end.Release!.Fields["release"].Should().Be(holdBlack ? "deferred-hold-black" : "done");
            var released = holdBlack ? [] : new[] { SmbusCompound.RegWrite(0x70, 0x8020, 0), SmbusCompound.RegWrite(0x70, 0x80A0, 1) };
            bus.Sent.Should().Equal(released);

            bus.Sent.Clear();
            await adapters.RescanAsync(default);
            var stick = adapters.Controllers.Single(c => c.Descriptor.Id.Value == "enedram:p0:0x70");
            stick.Writer.Should().BeNull("the scanned controller is listed, but ControlVerified is false");
            stick.Verdict.Writer.Should().StartWith("EneDram PawnIO module not verified");
            bus.Sent.Should().BeEmpty();
        }
        finally
        {
            await bringup.DisposeAsync();
            await adapters.StopAsync();
            frames.Dispose();
        }

        bus.Sent.Should().BeEmpty("the session already released; Host stop sends nothing more");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_port_marker_keeps_another_connection_off_the_aliased_stick_until_its_session_ends(bool holdBlack)
    {
        var bus = new ScriptedEneBus();
        var ene = new EneDramAdapter(new ScriptedEneProvider(bus), new EneDramOptions { HoldBlackOnRelease = holdBlack });
        var guard = new FakeGuard().Enable(DeviceClass.EneDram);
        var frames = new FrameBus();
        var adapters = new AdapterHost([new InstanceFactory(ene)], null!, guard, frames, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await adapters.StartAsync(default);
        var bringup = new BringupManager(adapters, new RenderLoop(EffectRegistry.Default, frames), guard,
            new BringupOptions { HoldBlackOnRelease = holdBlack }, new ProbeLog(null), new ManualTimeProvider());
        try
        {
            await bringup.BeginAsync("pipe:A", "enedram:p0", 60, default);
            (await bringup.RunProbeAsync("pipe:A", "enedram:p0", "ene-scan", default)).Ok.Should().BeTrue();
            await adapters.RescanAsync(default);
            adapters.Controllers.Select(c => c.Descriptor.Id.Value).Should().Contain("enedram:p0:0x70");
            (await bringup.BeginMarkerAsync("pipe:A", new MarkerDto("enedram:p0", 0, 8, "#000000", 0x70), default)).Ok.Should().BeTrue();
            bus.Sent.Clear();

            var steal = () => bringup.BeginAsync("pipe:B", "enedram:p0:0x70", 60, default);
            (await steal.Should().ThrowAsync<DeviceBlockedException>()).Which.Reason.Should().Contain("marker drives 0x70");
            bus.Sent.Should().BeEmpty("refused before any packet, and the cleanup of the refused open sends nothing");
            bringup.Sessions.Select(x => (x.Id.Value, x.Owner)).Should().Equal(("enedram:p0", "pipe:A"));
            (await bringup.EndMarkerAsync("pipe:A", "enedram:p0", default)).Ok.Should().BeTrue("the port session still owns the stick");

            bus.Sent.Clear();
            (await bringup.EndAsync("pipe:A", "enedram:p0")).Release!.Fields["release"].Should().Be(holdBlack ? "deferred-hold-black" : "done");
            bus.Sent.Clear();
            await bringup.BeginAsync("pipe:B", "enedram:p0:0x70", 60, default);
            bus.Sent.Should().BeEmpty("reacquired once the port session ended; opening sends nothing");
            (await bringup.EndAsync("pipe:B", "enedram:p0:0x70")).Release!.Fields["release"].Should().Be(holdBlack ? "deferred-hold-black" : "not-applicable");
            bus.Sent.Should().BeEmpty();
        }
        finally
        {
            await bringup.DisposeAsync();
            await adapters.StopAsync();
            frames.Dispose();
        }

        bus.Sent.Should().BeEmpty("Host stop sends nothing more (hold-black defers; daytime already sent direct off)");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root (src/UnifiedRgb.sln) not found");
    }

    /// <summary>One AUMA0-E6K5-0104 module with 8 LEDs at 0x70 on port 0; every other address NACKs. Records what is sent.</summary>
    private sealed class ScriptedEneBus : IPawnIoPort
    {
        private const string Name = "AUMA0-E6K5-0104";

        public int Port => 0;

        public PawnIoIdentity Identity { get; } = new("PIIX4", 0xB00, 0x1022, 0x790B, 0x1043, 0x8877);

        public long MutexTimeouts => 0;

        public List<SmbusCompound> Sent { get; } = [];

        public SmbusResult Execute(SmbusCompound c)
        {
            Sent.Add(c);
            if (c.Address != 0x70)
            {
                return new SmbusResult(false, unchecked((int)0x8007045D), 0, "NACK", Step: "read");
            }

            var value = c.Kind switch
            {
                SmbusCompoundKind.ReadSig when c.Command >= 0xA0 => (byte)(c.Command - 0xA0),
                SmbusCompoundKind.RegRead when c.Register is >= 0x1000 and < 0x1010 => c.Register - 0x1000 < Name.Length ? (byte)Name[c.Register - 0x1000] : (byte)0,
                SmbusCompoundKind.RegRead when c.Register == 0x1C02 => (byte)8,
                _ => (byte)0,
            };
            return SmbusResult.Success(value, 0);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedEneProvider(ScriptedEneBus bus) : IEneSmbusProvider
    {
        public IReadOnlyDictionary<string, string> Pins { get; } = new Dictionary<string, string>();

        public PawnIoVersionEvidence? Version => null;

        public IPawnIoPort Open(int port) => port == 0 ? bus : throw new DeviceBlockedException("port 1 not scripted");

        public void Dispose()
        {
        }
    }
}
