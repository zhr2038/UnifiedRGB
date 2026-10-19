using System.Diagnostics;
using System.Runtime.InteropServices;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Safety.Tests;

/// <summary>
/// NATIVE design §5.2: the PawnIO SMBus port over a scripted library and mutex. Never loads PawnIOLib.dll or a module;
/// every native call is a fake that records its function, input cells and requested output size.
/// </summary>
public class PawnIoSmbusTests
{
    private static readonly byte[] Module = [0xDE, 0xAD];

    private sealed record Call(string Function, ulong[] Input, int OutCells);

    private sealed class FakeLibrary : IPawnIoLibrary, IDisposable
    {
        public List<Call> Calls { get; } = [];

        public int Opens { get; private set; }

        public bool Disposed { get; private set; }

        public int Closes { get; private set; }

        public int Loads { get; private set; }

        public ulong[] IdentityOut { get; set; } = [0x3458494950, 0xB00, Ids(0x1022, 0x790B)];

        public nuint IdentityCells { get; set; } = 3;

        public nuint PortSelCells { get; set; } = 1;

        public int OpenHr { get; set; }

        public int LoadHr { get; set; }

        /// <summary>HRESULT of <c>ioctl_set_sleep_mode</c> (0 = accepted).</summary>
        public int SleepHr { get; set; }

        /// <summary>Per xfer call (0-based): HRESULT, returned cells (null = requested), value.</summary>
        public Func<int, ulong[], (int Hr, nuint? Cells, ulong Value)> Xfer { get; set; } = (_, _) => (0, null, 0x42);

        /// <summary>What the pinned DLL's three version exports report: 0x00020000 ("2.0.0").</summary>
        public uint LibVersion { get; set; } = 0x00020000;

        public int VersionHr { get; set; }

        public static ulong Ids(ushort vendor, ushort device) => vendor | ((ulong)device << 16) | (0x1043UL << 32) | (0x8877UL << 48);

        public int Version(out uint version)
        {
            version = LibVersion;
            return VersionHr;
        }

        public int Open(out IntPtr handle)
        {
            Opens++;
            handle = OpenHr == 0 ? 0x1234 : IntPtr.Zero;
            return OpenHr;
        }

        public void Dispose() => Disposed = true;

        public int Load(IntPtr handle, ReadOnlySpan<byte> blob)
        {
            Loads++;
            return LoadHr;
        }

        public int Execute(IntPtr handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output, out nuint returned)
        {
            Calls.Add(new Call(function, input.ToArray(), output.Length));
            switch (function)
            {
                case PawnIoSmbus.IoctlIdentity:
                    IdentityOut.AsSpan(0, Math.Min(IdentityOut.Length, output.Length)).CopyTo(output);
                    returned = IdentityCells;
                    return 0;
                case PawnIoSmbus.IoctlPortSelect:
                    returned = PortSelCells;
                    return 0;
                case PawnIoSmbus.IoctlSleepMode:
                    returned = 0;
                    return SleepHr;
                default:
                {
                    var n = Calls.Count(c => c.Function == PawnIoSmbus.IoctlXfer) - 1;
                    var (hr, cells, value) = Xfer(n, input.ToArray());
                    if (output.Length > 0)
                    {
                        output[0] = value;
                    }

                    returned = cells ?? (nuint)output.Length;
                    return hr;
                }
            }
        }

        public int Close(IntPtr handle)
        {
            Closes++;
            return 0;
        }

        public IEnumerable<Call> Xfers => Calls.Where(c => c.Function == PawnIoSmbus.IoctlXfer);
    }

    private sealed class FakeMutex : ISmbusMutex
    {
        public bool Acquire { get; set; } = true;

        public int Waits { get; private set; }

        public int Releases { get; private set; }

        public bool Wait(int milliseconds)
        {
            Waits++;
            return Acquire;
        }

        public void Release() => Releases++;

        public void Dispose()
        {
        }
    }

    private static PawnIoSmbus Open(FakeLibrary lib, FakeMutex mutex, int port = 0) => PawnIoSmbus.Open(lib, Module, port, mutex, 50);

    [Fact]
    public void Open_loads_identifies_selects_the_port_once_and_sets_sleep_mode()
    {
        var lib = new FakeLibrary();
        var mutex = new FakeMutex();
        using var port = Open(lib, mutex, 1);
        lib.Loads.Should().Be(1);
        lib.Calls.Select(c => c.Function).Should().Equal(PawnIoSmbus.IoctlIdentity, PawnIoSmbus.IoctlPortSelect, PawnIoSmbus.IoctlSleepMode);
        lib.Calls[0].OutCells.Should().Be(3);
        lib.Calls[1].Input.Should().Equal(1UL);
        lib.Calls[1].OutCells.Should().Be(1);
        lib.Calls[2].Input.Should().Equal(2UL);
        lib.Calls[2].OutCells.Should().Be(0);
        mutex.Waits.Should().Be(1, "port selection runs under the global mutex");
        mutex.Releases.Should().Be(1);
        port.Port.Should().Be(1);
        port.Identity.IsAmd790B.Should().BeTrue();
        port.Identity.Type.Should().Be("PIIX4");
        port.Identity.IoBase.Should().Be(0xB00UL);
        port.Identity.SubsystemVendor.Should().Be(0x1043);
        lib.Closes.Should().Be(0);
    }

    // ---------------------------------------------------------------- NATIVE-DYNAMIC-SYNC-FIX §A: sleep mode

    [Theory]
    [InlineData(0, "AlwaysBusy")]
    [InlineData(1, "ShortBusy")]
    [InlineData(2, "AlwaysSleep")]
    public void Each_supported_sleep_mode_reaches_the_ioctl_exactly_and_is_reported_applied(int mode, string name)
    {
        var lib = new FakeLibrary();
        var mutex = new FakeMutex();
        using var port = PawnIoSmbus.Open(lib, Module, 0, mutex, 50, mode);
        lib.Calls.Select(c => c.Function).Should().Equal(PawnIoSmbus.IoctlIdentity, PawnIoSmbus.IoctlPortSelect, PawnIoSmbus.IoctlSleepMode);
        lib.Calls[2].Input.Should().Equal((ulong)mode);
        lib.Calls[2].OutCells.Should().Be(0);
        port.AppliedSleepMode.Should().Be(mode);
        ((IPawnIoPort)port).AppliedSleepMode.Should().Be(mode, "the interface reports what the ioctl accepted for this handle");
        PawnIoSmbus.IsSleepMode(mode).Should().BeTrue();
        PawnIoSmbus.SleepModeName(mode).Should().Be(name);
        lib.Closes.Should().Be(0);
    }

    [Fact]
    public void The_default_sleep_mode_is_two_the_value_always_sent_before()
    {
        PawnIoSmbus.DefaultSleepMode.Should().Be(2);
        PawnIoSmbus.SleepModeAlwaysSleep.Should().Be(2);
        var lib = new FakeLibrary();
        using var port = Open(lib, new FakeMutex());
        lib.Calls.Single(c => c.Function == PawnIoSmbus.IoctlSleepMode).Input.Should().Equal(2UL);
        port.AppliedSleepMode.Should().Be(2);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void An_unsupported_sleep_mode_throws_before_any_native_call(int mode)
    {
        var lib = new FakeLibrary();
        var mutex = new FakeMutex();
        var act = () => PawnIoSmbus.Open(lib, Module, 0, mutex, 50, mode);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("sleepMode");
        lib.Opens.Should().Be(0, "no pawnio_open for a mode the module would refuse");
        lib.Loads.Should().Be(0);
        lib.Calls.Should().BeEmpty();
        mutex.Waits.Should().Be(0);
        PawnIoSmbus.IsSleepMode(mode).Should().BeFalse();
        var name = () => PawnIoSmbus.SleepModeName(mode);
        name.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_refused_sleep_mode_ioctl_blocks_and_closes_the_handle()
    {
        var lib = new FakeLibrary { SleepHr = unchecked((int)0xC000000D) };
        var mutex = new FakeMutex();
        var act = () => PawnIoSmbus.Open(lib, Module, 0, mutex, 50, 1);
        act.Should().Throw<DeviceBlockedException>().WithMessage("*ioctl_set_sleep_mode(1) failed*");
        lib.Calls.Select(c => c.Function).Should().Equal(PawnIoSmbus.IoctlIdentity, PawnIoSmbus.IoctlPortSelect, PawnIoSmbus.IoctlSleepMode);
        lib.Calls.Should().NotContain(c => c.Function == PawnIoSmbus.IoctlXfer, "no transfer after a refused mode");
        lib.Closes.Should().Be(1, "the opened handle is closed");
    }

    [Theory]
    [InlineData("pawnio_open failed")]
    [InlineData("pawnio_load failed")]
    [InlineData("is not 1022:790B")]
    [InlineData("returned 2 cells, expected 3")]
    [InlineData("returned 0 cells, expected 1")]
    [InlineData("mutex not acquired")]
    public void Open_failures_block_and_close_the_handle(string reason)
    {
        var lib = new FakeLibrary();
        var mutex = new FakeMutex();
        switch (reason)
        {
            case "pawnio_open failed": lib.OpenHr = unchecked((int)0x80070005); break;
            case "pawnio_load failed": lib.LoadHr = unchecked((int)0x80004005); break;
            case "is not 1022:790B": lib.IdentityOut = [0, 0, FakeLibrary.Ids(0x8086, 0x7A23)]; break;
            case "returned 2 cells, expected 3": lib.IdentityCells = 2; break;
            case "returned 0 cells, expected 1": lib.PortSelCells = 0; break;
            default: mutex.Acquire = false; break;
        }

        var act = () => Open(lib, mutex);
        act.Should().Throw<DeviceBlockedException>().WithMessage($"*{reason}*");
        lib.Closes.Should().Be(lib.OpenHr == 0 ? 1 : 0, "an opened handle is closed on every failure");
        lib.Xfers.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Only_ports_0_and_1(int port)
    {
        var act = () => PawnIoSmbus.Open(new FakeLibrary(), Module, port, new FakeMutex(), 50);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Register_write_is_a_pointer_word_then_a_byte_write_in_one_mutex_hold()
    {
        var lib = new FakeLibrary();
        var mutex = new FakeMutex();
        using var port = Open(lib, mutex);
        var r = port.Execute(SmbusCompound.RegWrite(0x71, 0x8020, 1));
        r.Ok.Should().BeTrue();
        mutex.Waits.Should().Be(2, "one for the port selection, one for the compound");
        var x = lib.Xfers.ToList();
        x.Should().HaveCount(2);
        x[0].Input.Should().Equal(0x71UL, SmbusConstants.Write, 0x00UL, SmbusConstants.WordData, 0x2080UL);
        x[0].OutCells.Should().Be(0);
        x[1].Input.Should().Equal(0x71UL, SmbusConstants.Write, 0x01UL, SmbusConstants.ByteData, 0x01UL);
        x[1].OutCells.Should().Be(0);
    }

    [Fact]
    public void Register_read_returns_the_byte_from_command_0x81()
    {
        var lib = new FakeLibrary { Xfer = (n, _) => (0, null, n == 1 ? 0x07UL : 0) };
        using var port = Open(lib, new FakeMutex());
        var r = port.Execute(SmbusCompound.RegRead(0x77, 0x1C02));
        r.Should().Match<SmbusResult>(m => m.Ok && m.Value == 0x07 && m.Status == 0);
        var x = lib.Xfers.ToList();
        x[0].Input.Should().Equal(0x77UL, SmbusConstants.Write, 0x00UL, SmbusConstants.WordData, 0x021CUL);
        x[1].Input.Should().Equal(0x77UL, SmbusConstants.Read, 0x81UL, SmbusConstants.ByteData);
        x[1].OutCells.Should().Be(1);
    }

    [Fact]
    public void Probe_and_signature_are_single_reads_without_a_pointer()
    {
        var lib = new FakeLibrary();
        using var port = Open(lib, new FakeMutex());
        port.Execute(SmbusCompound.Probe(0x77)).Ok.Should().BeTrue();
        port.Execute(SmbusCompound.ReadSig(0x77, 0xA5)).Ok.Should().BeTrue();
        var x = lib.Xfers.ToList();
        x.Should().HaveCount(2);
        x[0].Input.Should().Equal(0x77UL, SmbusConstants.Read, 0UL, SmbusConstants.Byte);
        x[1].Input.Should().Equal(0x77UL, SmbusConstants.Read, 0xA5UL, SmbusConstants.ByteData);
    }

    [Fact]
    public void Block_write_marshals_length_and_rbg_bytes_into_nine_cells()
    {
        var lib = new FakeLibrary();
        using var port = Open(lib, new FakeMutex());
        port.Execute(SmbusCompound.RegBlock(0x70, 0x8103, [0x11, 0x33, 0x22])).Ok.Should().BeTrue();
        var x = lib.Xfers.ToList();
        x[0].Input.Should().Equal(0x70UL, SmbusConstants.Write, 0x00UL, SmbusConstants.WordData, 0x0381UL);
        x[1].Input.Should().HaveCount(9);
        x[1].Input.Take(4).Should().Equal(0x70UL, SmbusConstants.Write, 0x03UL, SmbusConstants.BlockData);
        x[1].Input[4].Should().Be(0x22_33_11_03UL);
        x[1].Input.Skip(5).Should().AllSatisfy(c => c.Should().Be(0UL));
    }

    [Theory]
    [InlineData(0, 0x42UL, "returned 0 cells")]
    [InlineData(1, 0x100UL, "not one byte")]
    public void A_read_without_one_returned_byte_is_no_data(int cells, ulong value, string why)
    {
        var lib = new FakeLibrary { Xfer = (n, _) => (0, n == 1 ? (nuint?)cells : null, value) };
        using var port = Open(lib, new FakeMutex());
        var r = port.Execute(SmbusCompound.RegRead(0x77, 0x1000));
        r.Ok.Should().BeFalse();
        r.Status.Should().Be(0);
        r.Value.Should().Be(0);
        r.Step.Should().Be("read");
        r.Failure.Should().Contain(why).And.Contain("no data");
    }

    [Fact]
    public void A_write_returning_cells_is_not_success()
    {
        var lib = new FakeLibrary { Xfer = (_, _) => (0, (nuint?)1, 0) };
        using var port = Open(lib, new FakeMutex());
        var r = port.Execute(SmbusCompound.Probe(0x77));
        r.Ok.Should().BeTrue("a read returning its one cell");
        lib.Xfer = (n, _) => (0, n == 1 ? (nuint?)1 : null, 0);
        var w = port.Execute(SmbusCompound.RegRead(0x77, 0x1000));
        w.Ok.Should().BeFalse("the pointer write returned a cell it must not");
        w.Step.Should().Be("pointer");
    }

    [Fact]
    public void A_failed_pointer_stops_the_compound_and_reports_its_status()
    {
        var lib = new FakeLibrary { Xfer = (n, _) => (n == 0 ? unchecked((int)0x8007001F) : 0, null, 0) };
        using var port = Open(lib, new FakeMutex());
        var r = port.Execute(SmbusCompound.RegWrite(0x71, 0x8020, 1));
        r.Ok.Should().BeFalse();
        r.Status.Should().Be(unchecked((int)0x8007001F));
        r.Step.Should().Be("pointer");
        r.Dropped.Should().BeFalse();
        lib.Xfers.Should().HaveCount(1, "the data half is never sent after a failed pointer");
    }

    [Fact]
    public void A_failed_block_reports_step_block()
    {
        var lib = new FakeLibrary { Xfer = (n, _) => (n == 1 ? unchecked((int)0x80070079) : 0, null, 0) };
        using var port = Open(lib, new FakeMutex());
        var r = port.Execute(SmbusCompound.RegBlock(0x71, 0x8100, [1, 2, 3]));
        r.Ok.Should().BeFalse();
        r.Step.Should().Be("block");
        r.Status.Should().Be(unchecked((int)0x80070079));
    }

    [Fact]
    public void Mutex_timeout_drops_the_compound_whole_and_counts_it()
    {
        var lib = new FakeLibrary();
        var mutex = new FakeMutex();
        using var port = Open(lib, mutex);
        mutex.Acquire = false;
        var r = port.Execute(SmbusCompound.RegWrite(0x71, 0x8020, 1));
        r.Dropped.Should().BeTrue();
        r.Ok.Should().BeFalse();
        r.Status.Should().Be(-1);
        port.MutexTimeouts.Should().Be(1);
        lib.Xfers.Should().BeEmpty();
        mutex.Releases.Should().Be(1, "only the port selection's hold was released");
    }

    [Fact]
    public void Dispose_closes_once_and_later_compounds_are_dropped()
    {
        var lib = new FakeLibrary();
        var port = Open(lib, new FakeMutex());
        port.Dispose();
        port.Dispose();
        lib.Closes.Should().Be(1);
        var r = port.Execute(SmbusCompound.Probe(0x77));
        r.Dropped.Should().BeTrue();
        r.Failure.Should().Be("port closed");
        lib.Xfers.Should().BeEmpty();
    }

    [Fact]
    public void Xfer_input_bounds_and_layout()
    {
        var act = () => PawnIoSmbus.XferInput(0x77, 0, 0, 0, [], 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => PawnIoSmbus.XferInput(0x77, 0, 0, 0, [], 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => PawnIoSmbus.XferInput(0x77, 0, 0, 0, new byte[35], 9);
        act.Should().Throw<ArgumentOutOfRangeException>();
        PawnIoSmbus.XferInput(0x77, 1, 0x81, 2, [], 4).Should().Equal(0x77UL, 1UL, 0x81UL, 2UL);
        var full = new byte[34];
        full[0] = 0xAA;
        full[33] = 0xBB;
        var cells = PawnIoSmbus.XferInput(0x77, 0, 3, 5, full, 9);
        var blob = MemoryMarshal.AsBytes(cells.AsSpan(4)).ToArray();
        blob.Should().HaveCount(40);
        blob[0].Should().Be(0xAA);
        blob[33].Should().Be(0xBB);
        blob.Skip(34).Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Identity_decoding_needs_three_cells()
    {
        var act = () => PawnIoIdentity.Decode([1, 2]);
        act.Should().Throw<ArgumentException>();
        var id = PawnIoIdentity.Decode([0x3458494950, 0xB00, FakeLibrary.Ids(0x1022, 0x790B)]);
        id.Should().Be(new PawnIoIdentity("PIIX4", 0xB00, 0x1022, 0x790B, 0x1043, 0x8877));
    }

    [Fact]
    public void Pins_refuse_missing_and_mismatched_files_and_wrong_versions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"urgb-pin-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var sha = PawnIoPin.Sha256Of(path);
            PawnIoPin.LoadModule(path, sha.ToLowerInvariant()).Should().Equal(1, 2, 3);
            var wrong = () => PawnIoPin.LoadModule(path, new string('0', 64));
            wrong.Should().Throw<DeviceBlockedException>().WithMessage("*is not the pinned*");
            var missing = () => PawnIoPin.LoadModule(path + ".missing", sha);
            missing.Should().Throw<DeviceBlockedException>().WithMessage("*not found*");
            var empty = () => PawnIoPin.VerifyFile(string.Empty, sha, "PawnIOLib.dll");
            empty.Should().Throw<DeviceBlockedException>();
        }
        finally
        {
            File.Delete(path);
        }

        PawnIoPin.VerifyVersion(new FakeLibrary(), "2.0.0").Admitted.Should().BeTrue();
        var old = () => PawnIoPin.VerifyVersion(new FakeLibrary { LibVersion = 0x020100 }, "2.0.0");
        old.Should().Throw<DeviceBlockedException>().WithMessage("*exported API version 2.1.0 is not the pinned 2.0.0*");
        var failed = () => PawnIoPin.VerifyVersion(new FakeLibrary { VersionHr = -1 }, "2.0.0");
        failed.Should().Throw<DeviceBlockedException>().WithMessage("*pawnio_version failed*");
    }

    [Fact]
    public void The_api_gate_admits_the_exported_2_0_0_and_returns_its_evidence()
    {
        var v = PawnIoPin.VerifyVersion(new FakeLibrary { LibVersion = 0x00020000 }, " 2.0.0 ", "2.2.0.0");
        v.Should().Be(new PawnIoVersionEvidence("2.0.0", 0, 0x00020000, "2.2.0.0"));
        (v.Actual, v.RawHex, v.Admitted, v.Refusal).Should().Be(("2.0.0", "0x00020000", true, (string?)null));
        v.Note.Should().Be("api 2.0.0 (raw 0x00020000) == expected 2.0.0; file 2.2.0.0 evidence only");
        v.Fields.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["libApiActual"] = "2.0.0", ["libApiRaw"] = "0x00020000", ["libFileVersion"] = "2.2.0.0", ["driverVersion"] = "not-observed",
        });
        PawnIoPin.VerifyVersion(new FakeLibrary(), "2.0.0", " ").FileVersion.Should().Be("unavailable");
    }

    [Theory]
    [InlineData(0x00020200u, "2.0.0", "2.2.0")] // the conflated file release number
    [InlineData(0x00020100u, "2.0.0", "2.1.0")]
    [InlineData(0x00030000u, "2.0.0", "3.0.0")]
    [InlineData(0x00020000u, "2.2.0", "2.0.0")] // the old pin against the real export
    [InlineData(0x00020000u, "", "2.0.0")]
    public void Any_other_api_version_is_refused_and_the_refusal_carries_both_numbers(uint raw, string expected, string actual)
    {
        var library = new FakeLibrary { LibVersion = raw };
        var act = () => PawnIoPin.VerifyVersion(library, expected, "2.2.0.0");
        var ex = act.Should().Throw<DeviceBlockedException>().Which;
        ex.Reason.Should().Be($"PawnIOLib exported API version {actual} is not the pinned {expected} (file version 2.2.0.0 is evidence only)");
        var v = PawnIoVersionEvidence.Of(ex);
        v.Should().Be(new PawnIoVersionEvidence(expected, 0, raw, "2.2.0.0"));
        (v!.Actual, v.RawHex, v.Admitted).Should().Be((actual, $"0x{raw:X8}", false));
        v.Note.Should().Be($"api {actual} (raw 0x{raw:X8}) != expected {expected}; file 2.2.0.0 evidence only");
        library.Opens.Should().Be(0);
        PawnIoVersionEvidence.Of(new DeviceBlockedException("other")).Should().BeNull();
    }

    [Fact]
    public void A_failing_pawnio_version_blocks_whatever_value_it_wrote()
    {
        var library = new FakeLibrary { VersionHr = unchecked((int)0x80070005), LibVersion = 0x00020000 };
        var act = () => PawnIoPin.VerifyVersion(library, "2.0.0");
        var ex = act.Should().Throw<DeviceBlockedException>().Which;
        ex.Reason.Should().Be("pawnio_version failed: HRESULT 0x80070005 (file version unavailable is evidence only)");
        var v = PawnIoVersionEvidence.Of(ex)!;
        (v.Actual, v.RawHex, v.Hr, v.Admitted).Should().Be(("unavailable", "unavailable", unchecked((int)0x80070005), false));
        v.Note.Should().Be("pawnio_version failed: HRESULT 0x80070005; file unavailable evidence only");
    }

    [Fact]
    public void Load_pins_the_file_records_its_version_as_evidence_and_gates_on_the_exported_api_only()
    {
        // no version resource vs a real one (a copy of this test assembly): the file version differs, the gate outcome does not
        var plain = Path.Combine(Path.GetTempPath(), $"urgb-lib-{Guid.NewGuid():N}.bin");
        var pe = Path.Combine(Path.GetTempPath(), $"urgb-lib-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(plain, [1, 2, 3]);
        File.Copy(typeof(PawnIoSmbusTests).Assembly.Location, pe);
        try
        {
            var peVersion = FileVersionInfo.GetVersionInfo(pe).FileVersion;
            peVersion.Should().NotBeNullOrWhiteSpace();
            foreach (var (path, file) in new[] { (plain, "unavailable"), (pe, peVersion!) })
            {
                PawnIoPin.FileVersionOf(path).Should().Be(file);
                var sha = PawnIoPin.Sha256Of(path);
                var good = new FakeLibrary();
                using (var loaded = PawnIoNative.Load(path, sha, "2.0.0", p => p == path ? good : throw new InvalidOperationException(p)))
                {
                    loaded.Library.Should().BeSameAs(good);
                    (loaded.ApiVersion, loaded.ApiRaw, loaded.FileVersion).Should().Be(("2.0.0", 0x00020000u, file));
                    loaded.Version.Admitted.Should().BeTrue();
                    good.Disposed.Should().BeFalse();
                }

                good.Disposed.Should().BeTrue();
                good.Opens.Should().Be(0);

                var conflated = new FakeLibrary { LibVersion = 0x00020200 };
                var refused = () => PawnIoNative.Load(path, sha, "2.0.0", _ => conflated);
                var ex = refused.Should().Throw<DeviceBlockedException>().Which;
                ex.Reason.Should().Be($"PawnIOLib exported API version 2.2.0 is not the pinned 2.0.0 (file version {file} is evidence only)");
                PawnIoVersionEvidence.Of(ex)!.FileVersion.Should().Be(file);
                conflated.Disposed.Should().BeTrue("a refused library is released");
                conflated.Opens.Should().Be(0, "the gate refuses before pawnio_open");
            }

            var bound = false;
            var wrong = () => PawnIoNative.Load(plain, new string('0', 64), "2.0.0", _ =>
            {
                bound = true;
                return new FakeLibrary();
            });
            wrong.Should().Throw<DeviceBlockedException>().WithMessage("*is not the pinned*");
            bound.Should().BeFalse("the SHA-256 pin runs before anything is bound");
        }
        finally
        {
            File.Delete(plain);
            File.Delete(pe);
        }

        PawnIoPin.FileVersionOf(plain).Should().Be("unavailable");
        PawnIoPin.FileVersionOf(string.Empty).Should().Be("unavailable");
    }
}
