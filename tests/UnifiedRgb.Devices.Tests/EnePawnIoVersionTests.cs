using System.Diagnostics;
using System.Globalization;
using UnifiedRgb.Devices.EneDram;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// PAWNIO-VERSION-CORRECTION-20261018 §4, offline: the real <see cref="PawnIoSmbusProvider"/> and <see cref="EneDramAdapter"/>
/// over a scripted library (only the DLL binding is replaced; the SHA pins, the file-version evidence and the exported API
/// gate run as in production) and temp library/module files. Nothing loads PawnIOLib.dll, takes the global SMBus mutex or
/// sends an SMBus transfer.
/// </summary>
public sealed class EnePawnIoVersionTests : IDisposable
{
    private static readonly ControllerId P0 = EneDramAdapter.PortId(0);
    private static readonly string[] PinKeys = ["libPath", "libSha256", "libApiExpected", "libFileRelease", "modulePath", "moduleSha256"];
    private readonly string _plainLib = Path.Combine(Path.GetTempPath(), $"urgb-ene-lib-{Guid.NewGuid():N}.bin");
    private readonly string _peLib = Path.Combine(Path.GetTempPath(), $"urgb-ene-lib-{Guid.NewGuid():N}.dll");
    private readonly string _module = Path.Combine(Path.GetTempPath(), $"urgb-ene-module-{Guid.NewGuid():N}.bin");
    private readonly ScriptedLibrary _library = new();
    private int _binds;
    private int _mutexes;

    public EnePawnIoVersionTests()
    {
        File.WriteAllBytes(_plainLib, [0x4C, 0x49, 0x42]);
        File.Copy(typeof(EnePawnIoVersionTests).Assembly.Location, _peLib);
        File.WriteAllBytes(_module, [0xDE, 0xAD, 0xBE, 0xEF]);
    }

    [Fact]
    public void Defaults_gate_the_exported_api_and_only_label_the_file_release()
    {
        var o = new EneDramOptions();
        (o.PawnIoApiVersion, o.PawnIoLibVersion).Should().Be(("2.0.0", "2.2.0"));
        (EneDramDefaults.PawnIoApiVersion, EneDramDefaults.PawnIoLibVersion).Should().Be(("2.0.0", "2.2.0"));
        (o.PawnIoLibSha256, o.ModuleSha256).Should().Be((EneDramDefaults.PawnIoLibSha256, EneDramDefaults.ModuleSha256));
        (o.PawnIoModuleVerified, o.ControlVerified, o.RemapOnOpen, o.HoldBlackOnRelease).Should().Be((false, false, false, false));

        using var provider = new PawnIoSmbusProvider(o, Bind, Mutex);
        provider.Pins.Keys.Should().Equal(PinKeys);
        (provider.Pins["libApiExpected"], provider.Pins["libFileRelease"]).Should().Be(("2.0.0", "2.2.0"));
        provider.Version.Should().BeNull();
        (_binds, _mutexes).Should().Be((0, 0), "nothing loads before a probe asks for a port");
    }

    [Theory]
    [InlineData(false, "2.2.0")]
    [InlineData(true, "2.2.0")]
    [InlineData(false, "not-a-version")] // the label is never consulted
    public async Task Pawnio_id_reports_expected_actual_raw_and_file_evidence_side_by_side(bool peFile, string label)
    {
        var (options, file) = Options(peFile, label);
        await using var a = Adapter(options, out var provider);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);

        r.Ok.Should().BeTrue();
        r.Fields.Should().Contain("libApiExpected", "2.0.0")
            .And.Contain("libApiActual", "2.0.0")
            .And.Contain("libApiRaw", "0x00020000")
            .And.Contain("libFileRelease", label)
            .And.Contain("libFileVersion", file)
            .And.Contain("driverVersion", "not-observed")
            .And.Contain("pci", "1022:790B")
            .And.NotContainKey("libVersion");
        r.Rows.Should().HaveCount(2);
        r.Rows[0].Should().Be(new NativeProbeRow("pawnio_version", string.Empty, 0, "0x00020000", 0, $"api 2.0.0 (raw 0x00020000) == expected 2.0.0; file {file} evidence only"));
        r.Rows[1].Note.Should().Be("module and library pins verified before loading");
        provider.Version.Should().Be(new PawnIoVersionEvidence("2.0.0", 0, 0x00020000, file));
        (_binds, _mutexes, _library.Opens).Should().Be((1, 1, 1));
        _library.Functions.Should().Equal(PawnIoSmbus.IoctlIdentity, PawnIoSmbus.IoctlPortSelect, PawnIoSmbus.IoctlSleepMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_conflated_2_2_0_is_blocked_before_pawnio_open_with_both_numbers_in_the_result(bool peFile)
    {
        _library.Raw = 0x00020200;
        var (options, file) = Options(peFile, EneDramDefaults.PawnIoLibVersion);
        await using var a = Adapter(options, out var provider);
        await a.OpenForBringupAsync(P0, default);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);
            r.Ok.Should().BeFalse();
            r.Fields.Should().Contain("comm", "blocked")
                .And.Contain("blocked", $"PawnIOLib exported API version 2.2.0 is not the pinned 2.0.0 (file version {file} is evidence only)")
                .And.Contain("libApiExpected", "2.0.0")
                .And.Contain("libApiActual", "2.2.0")
                .And.Contain("libApiRaw", "0x00020200")
                .And.Contain("libFileRelease", "2.2.0")
                .And.Contain("libFileVersion", file)
                .And.Contain("driverVersion", "not-observed")
                .And.NotContainKey("libVersion");
            r.Rows.Select(x => (x.Step, x.Status)).Should().Equal(("pawnio_version", 0), ("pawnio open", EneDramAdapter.StatusRefused));
            r.Rows[0].Note.Should().Be($"api 2.2.0 (raw 0x00020200) != expected 2.0.0; file {file} evidence only");
            _binds.Should().Be(attempt, "a refused library is released, so the next probe checks again");
        }

        provider.Version.Should().BeNull("nothing was admitted");
        (_library.Opens, _mutexes, _library.Disposals).Should().Be((0, 0, 2));
        _library.Functions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_pawnio_version_is_blocked_before_pawnio_open()
    {
        _library.Hr = unchecked((int)0x80004005);
        var (options, _) = Options(false, EneDramDefaults.PawnIoLibVersion);
        await using var a = Adapter(options, out _);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbeEneScan, default);

        r.Ok.Should().BeFalse();
        r.Fields["blocked"].Should().Be("pawnio_version failed: HRESULT 0x80004005 (file version unavailable is evidence only)");
        (r.Fields["libApiExpected"], r.Fields["libApiActual"], r.Fields["libApiRaw"], r.Fields["driverVersion"]).Should().Be(("2.0.0", "unavailable", "unavailable", "not-observed"));
        r.Rows[0].Should().Be(new NativeProbeRow("pawnio_version", string.Empty, unchecked((int)0x80004005), "unavailable", 0, "pawnio_version failed: HRESULT 0x80004005; file unavailable evidence only"));
        (_library.Opens, _mutexes).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_block_after_the_api_gate_still_shows_the_admitted_version()
    {
        _library.Device = 0x1234;
        var (options, _) = Options(false, EneDramDefaults.PawnIoLibVersion);
        await using var a = Adapter(options, out var provider);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);

        r.Ok.Should().BeFalse();
        r.Fields["blocked"].Should().Be("SMBus controller 1022:1234 is not 1022:790B");
        (r.Fields["libApiActual"], r.Fields["libApiRaw"], r.Fields["libFileVersion"]).Should().Be(("2.0.0", "0x00020000", "unavailable"));
        r.Rows.Select(x => x.Step).Should().Equal("pawnio_version", "pawnio open");
        r.Rows[0].Note.Should().Be("api 2.0.0 (raw 0x00020000) == expected 2.0.0; file unavailable evidence only");
        provider.Version!.Admitted.Should().BeTrue();
        _library.Closes.Should().Be(1, "the handle of a refused identity is closed");
        _library.Functions.Should().Equal(PawnIoSmbus.IoctlIdentity);
    }

    [Fact]
    public async Task A_changed_library_file_is_refused_before_binding()
    {
        var (options, _) = Options(false, EneDramDefaults.PawnIoLibVersion);
        File.WriteAllBytes(_plainLib, [0x00]);
        await using var a = Adapter(options, out _);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);

        r.Ok.Should().BeFalse();
        r.Fields["blocked"].Should().StartWith("PawnIOLib.dll SHA-256 ").And.Contain("is not the pinned");
        r.Fields.Should().NotContainKey("libApiActual");
        r.Rows.Should().ContainSingle();
        _binds.Should().Be(0);
    }

    // ---------------------------------------------------------------- NATIVE-DYNAMIC-SYNC-FIX §A: sleep mode

    [Fact]
    public void The_source_default_sleep_mode_is_two()
    {
        new EneDramOptions().SleepMode.Should().Be(2);
        EneDramDefaults.SleepMode.Should().Be(PawnIoSmbus.DefaultSleepMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Pawnio_id_reports_the_configured_and_the_applied_sleep_mode_of_the_real_port(int mode)
    {
        var (options, _) = Options(false, EneDramDefaults.PawnIoLibVersion);
        await using var a = Adapter(options with { SleepMode = mode }, out _);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);

        r.Ok.Should().BeTrue();
        var text = mode.ToString(CultureInfo.InvariantCulture);
        (r.Fields["sleepModeConfigured"], r.Fields["sleepModeApplied"]).Should().Be((text, text), "the real port reports what the ioctl accepted");
        _library.SleepInputs.Should().Equal((ulong)mode);
        _library.Functions.Should().Equal(PawnIoSmbus.IoctlIdentity, PawnIoSmbus.IoctlPortSelect, PawnIoSmbus.IoctlSleepMode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(255)]
    public async Task An_unsupported_sleep_mode_blocks_before_the_module_the_library_or_the_device_is_opened(int mode)
    {
        var (options, _) = Options(false, EneDramDefaults.PawnIoLibVersion);
        await using var a = Adapter(options with { SleepMode = mode }, out var provider);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);

        r.Ok.Should().BeFalse();
        var text = mode.ToString(CultureInfo.InvariantCulture);
        r.Fields["blocked"].Should().Be($"EneDram:SleepMode {text} is not a PawnIO sleep mode (0 AlwaysBusy, 1 ShortBusy, 2 AlwaysSleep)");
        (r.Fields["sleepModeConfigured"], r.Fields["sleepModeApplied"]).Should().Be((text, EneDramAdapter.SleepModeNotApplied));
        r.Rows[^1].Status.Should().Be(EneDramAdapter.StatusRefused);
        (_binds, _mutexes, _library.Opens).Should().Be((0, 0, 0), "no fallback: nothing native is touched");
        _library.Functions.Should().BeEmpty();
        _library.SleepInputs.Should().BeEmpty();
        provider.Version.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_sleep_mode_ioctl_blocks_closes_the_handle_and_reports_nothing_applied()
    {
        _library.SleepHr = unchecked((int)0xC000000D);
        var (options, _) = Options(false, EneDramDefaults.PawnIoLibVersion);
        await using var a = Adapter(options with { SleepMode = 0 }, out _);
        await a.OpenForBringupAsync(P0, default);
        var r = await a.RunProbeAsync(P0, EneDramAdapter.ProbePawnIoId, default);

        r.Ok.Should().BeFalse();
        r.Fields["blocked"].Should().Be("ioctl_set_sleep_mode(0) failed: HRESULT 0xC000000D");
        (r.Fields["sleepModeConfigured"], r.Fields["sleepModeApplied"]).Should().Be(("0", EneDramAdapter.SleepModeNotApplied));
        _library.SleepInputs.Should().Equal(0UL);
        _library.Closes.Should().Be(1, "the handle is closed");
        _library.Functions.Should().Equal(PawnIoSmbus.IoctlIdentity, PawnIoSmbus.IoctlPortSelect, PawnIoSmbus.IoctlSleepMode);
    }

    public void Dispose()
    {
        File.Delete(_plainLib);
        File.Delete(_peLib);
        File.Delete(_module);
    }

    private (EneDramOptions Options, string FileVersion) Options(bool peFile, string label)
    {
        var lib = peFile ? _peLib : _plainLib;
        var file = peFile ? FileVersionInfo.GetVersionInfo(lib).FileVersion! : PawnIoVersionEvidence.Unavailable;
        file.Should().NotBeNullOrWhiteSpace();
        var options = new EneDramOptions
        {
            PawnIoLibPath = lib,
            PawnIoLibSha256 = PawnIoPin.Sha256Of(lib),
            PawnIoLibVersion = label,
            ModulePath = _module,
            ModuleSha256 = PawnIoPin.Sha256Of(_module),
        };
        return (options, file);
    }

    private EneDramAdapter Adapter(EneDramOptions options, out PawnIoSmbusProvider provider)
    {
        provider = new PawnIoSmbusProvider(options, Bind, Mutex);
        return new EneDramAdapter(provider, options);
    }

    private ScriptedLibrary Bind(string path)
    {
        _binds++;
        return _library;
    }

    private FreeMutex Mutex()
    {
        _mutexes++;
        return new FreeMutex();
    }

    private sealed class FreeMutex : ISmbusMutex
    {
        public bool Wait(int milliseconds) => true;

        public void Release()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>pawnio_version as scripted; open/load/identity (1022:<see cref="Device"/>)/port select/sleep mode; no transfer.</summary>
    private sealed class ScriptedLibrary : IPawnIoLibrary, IDisposable
    {
        public uint Raw { get; set; } = 0x00020000;

        public int Hr { get; set; }

        public ushort Device { get; set; } = 0x790B;

        public int Opens { get; private set; }

        public int Closes { get; private set; }

        public int Disposals { get; private set; }

        public List<string> Functions { get; } = [];

        /// <summary>Every <c>ioctl_set_sleep_mode</c> input cell, in order.</summary>
        public List<ulong> SleepInputs { get; } = [];

        /// <summary>HRESULT of <c>ioctl_set_sleep_mode</c> (0 = accepted).</summary>
        public int SleepHr { get; set; }

        public int Version(out uint version)
        {
            version = Raw;
            return Hr;
        }

        public int Open(out IntPtr handle)
        {
            Opens++;
            handle = 0x5150;
            return 0;
        }

        public int Load(IntPtr handle, ReadOnlySpan<byte> blob) => 0;

        public int Execute(IntPtr handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output, out nuint returned)
        {
            Functions.Add(function);
            switch (function)
            {
                case PawnIoSmbus.IoctlIdentity:
                    output[0] = 0x3458494950; // "PIIX4"
                    output[1] = 0xB00;
                    output[2] = 0x1022UL | ((ulong)Device << 16) | (0x1043UL << 32) | (0x8877UL << 48);
                    returned = 3;
                    return 0;
                case PawnIoSmbus.IoctlPortSelect:
                    returned = 1;
                    return 0;
                case PawnIoSmbus.IoctlSleepMode:
                    SleepInputs.Add(input[0]);
                    returned = 0;
                    return SleepHr;
                default:
                    throw new InvalidOperationException($"{function}: no SMBus transfer in these tests");
            }
        }

        public int Close(IntPtr handle)
        {
            Closes++;
            return 0;
        }

        public void Dispose() => Disposals++;
    }
}
