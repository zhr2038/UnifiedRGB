using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Smbus;

/// <summary>
/// The PawnIOLib.dll surface we use (shipped header <c>C:\Program Files\PawnIO\PawnIOLib.h</c>: <c>pawnio_version</c>,
/// <c>pawnio_open</c>, <c>pawnio_load</c>, <c>pawnio_execute</c>, <c>pawnio_close</c>; all return an HRESULT, 0 = S_OK).
/// Abstracted so tests never call PawnIO.
/// </summary>
public interface IPawnIoLibrary
{
    /// <summary>(major &lt;&lt; 16) | (minor &lt;&lt; 8) | patch.</summary>
    int Version(out uint version);

    int Open(out IntPtr handle);

    int Load(IntPtr handle, ReadOnlySpan<byte> blob);

    /// <summary>
    /// <paramref name="input"/>/<paramref name="output"/> lengths are the ULONG64 counts passed as in_size/out_size;
    /// <paramref name="returned"/> is the header's <c>return_size</c> ("entries written"), which callers validate against
    /// the module contract before decoding any output cell.
    /// </summary>
    int Execute(IntPtr handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output, out nuint returned);

    int Close(IntPtr handle);
}

/// <summary>The global SMBus mutex (<see cref="SmbusConstants.GlobalMutexName"/>), abstracted for tests.</summary>
public interface ISmbusMutex : IDisposable
{
    /// <summary>True when acquired within <paramref name="milliseconds"/> (an abandoned mutex counts as acquired).</summary>
    bool Wait(int milliseconds);

    void Release();
}

/// <summary>Decoded <c>ioctl_identity</c> output: controller type, I/O base and PCI ids.</summary>
public sealed record PawnIoIdentity(string Type, ulong IoBase, ushort PciVendor, ushort PciDevice, ushort SubsystemVendor, ushort SubsystemDevice)
{
    public static PawnIoIdentity Decode(ReadOnlySpan<ulong> o)
    {
        if (o.Length < 3)
        {
            throw new ArgumentException("ioctl_identity returns three cells.", nameof(o));
        }

        Span<byte> name = stackalloc byte[8];
        BitConverter.TryWriteBytes(name, o[0]);
        var type = System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0');
        var ids = o[2];
        return new PawnIoIdentity(type, o[1], (ushort)ids, (ushort)(ids >> 16), (ushort)(ids >> 32), (ushort)(ids >> 48));
    }

    /// <summary>The AMD SMBus controller of this machine (NATIVE design §5.1): 1022:790B.</summary>
    public bool IsAmd790B => PciVendor == 0x1022 && PciDevice == 0x790B;
}

/// <summary>
/// What <c>pawnio_version</c> reported against the pin (PAWNIO-VERSION-CORRECTION-20261018 §3). Only the exported API version
/// is gated. <see cref="FileVersion"/>, the pinned DLL's version resource (the installer/file release, a different number), is
/// evidence only and never compared; neither number reports the kernel driver (<see cref="DriverNotObserved"/>).
/// </summary>
/// <param name="Expected">The pinned API version ("2.0.0").</param>
/// <param name="Hr">The HRESULT of <c>pawnio_version</c>.</param>
/// <param name="Raw">The reported (major &lt;&lt; 16) | (minor &lt;&lt; 8) | patch; null when the call failed.</param>
/// <param name="FileVersion">FileVersionInfo.FileVersion of the pinned path, or <see cref="Unavailable"/>.</param>
public sealed record PawnIoVersionEvidence(string Expected, int Hr, uint? Raw, string FileVersion)
{
    public const string Unavailable = "unavailable";

    /// <summary>Literal: nothing on this path observes the PawnIO kernel driver's version.</summary>
    public const string DriverNotObserved = "not-observed";

    /// <summary>The decoded API version, or <see cref="Unavailable"/> when the call failed.</summary>
    public string Actual => Raw is { } v ? $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}" : Unavailable;

    public string RawHex => Raw is { } v ? $"0x{v:X8}" : Unavailable;

    /// <summary>S_OK and the decoded API version equals <see cref="Expected"/> (ordinal, exact); the file version is not consulted.</summary>
    public bool Admitted => Hr == 0 && Raw is not null && string.Equals(Actual, Expected, StringComparison.Ordinal);

    /// <summary>The block reason, or null when <see cref="Admitted"/>.</summary>
    public string? Refusal => Hr != 0
        ? $"pawnio_version failed: HRESULT 0x{Hr:X8} (file version {FileVersion} is evidence only)"
        : Admitted ? null : $"PawnIOLib exported API version {Actual} is not the pinned {Expected} (file version {FileVersion} is evidence only)";

    /// <summary>The note of the <c>pawnio_version</c> probe row.</summary>
    public string Note => Hr != 0
        ? $"pawnio_version failed: HRESULT 0x{Hr:X8}; file {FileVersion} evidence only"
        : $"api {Actual} (raw {RawHex}) {(Admitted ? "==" : "!=")} expected {Expected}; file {FileVersion} evidence only";

    /// <summary>The observed probe fields; the expected values are the provider's pins (<c>libApiExpected</c>, <c>libFileRelease</c>).</summary>
    public IReadOnlyDictionary<string, string> Fields => new Dictionary<string, string>
    {
        ["libApiActual"] = Actual,
        ["libApiRaw"] = RawHex,
        ["libFileVersion"] = FileVersion,
        ["driverVersion"] = DriverNotObserved,
    };

    /// <summary>The evidence carried by a version-gate refusal (<see cref="PawnIoPin.VerifyVersion"/>), or null.</summary>
    public static PawnIoVersionEvidence? Of(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PawnIoVersionException { Evidence: { } evidence })
            {
                return evidence;
            }
        }

        return null;
    }
}

/// <summary>The inner exception of a version-gate <see cref="DeviceBlockedException"/>: carries the refused evidence.</summary>
public sealed class PawnIoVersionException : Exception
{
    public PawnIoVersionException()
    {
    }

    public PawnIoVersionException(string message)
        : base(message)
    {
    }

    public PawnIoVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PawnIoVersionException(PawnIoVersionEvidence evidence)
        : base(evidence?.Refusal)
    {
        Evidence = evidence;
    }

    public PawnIoVersionEvidence? Evidence { get; }
}

/// <summary>Pinning of the installed PawnIOLib.dll and of the module blob (NATIVE design §5.2): mismatch → Blocked.</summary>
public static class PawnIoPin
{
    /// <summary>Throws <see cref="DeviceBlockedException"/> unless <paramref name="path"/> exists and hashes to <paramref name="sha256"/>.</summary>
    public static void VerifyFile(string path, string sha256, string what)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sha256);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new DeviceBlockedException($"{what} not found at '{path}'");
        }

        var actual = Sha256Of(path);
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceBlockedException($"{what} SHA-256 {actual} is not the pinned {sha256}");
        }
    }

    /// <summary>Reads the module blob after verifying its pin; any other blob is refused.</summary>
    public static byte[] LoadModule(string path, string sha256)
    {
        VerifyFile(path, sha256, "PawnIO module");
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// The exported API gate: returns the evidence when <c>pawnio_version</c> succeeds and decodes to <paramref name="expected"/>
    /// ("2.0.0"); otherwise throws <see cref="DeviceBlockedException"/> whose inner <see cref="PawnIoVersionException"/> carries the
    /// same evidence. <paramref name="fileVersion"/> is recorded only, never compared.
    /// </summary>
    public static PawnIoVersionEvidence VerifyVersion(IPawnIoLibrary library, string expected, string fileVersion = PawnIoVersionEvidence.Unavailable)
    {
        ArgumentNullException.ThrowIfNull(library);
        var hr = library.Version(out var v);
        var evidence = new PawnIoVersionEvidence(
            expected?.Trim() ?? string.Empty, hr, hr == 0 ? v : null, string.IsNullOrWhiteSpace(fileVersion) ? PawnIoVersionEvidence.Unavailable : fileVersion);
        return evidence.Refusal is { } refusal ? throw new DeviceBlockedException(refusal, new PawnIoVersionException(evidence)) : evidence;
    }

    /// <summary>FileVersionInfo.FileVersion of <paramref name="path"/>, or "unavailable" on any failure: evidence only, never a gate.</summary>
    public static string FileVersionOf(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(v) ? PawnIoVersionEvidence.Unavailable : v.Trim();
        }
#pragma warning disable CA1031 // evidence only: no failure to read it may change the gate outcome
        catch (Exception)
#pragma warning restore CA1031
        {
            return PawnIoVersionEvidence.Unavailable;
        }
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}

/// <summary>
/// One PIIX4 port through one loaded PawnIO module handle (NATIVE design §5.2): open, load the pinned blob,
/// <c>ioctl_identity</c> (must be 1022:790B), <c>ioctl_piix4_port_sel(port)</c> once, <c>ioctl_set_sleep_mode(mode)</c> with the
/// configured mode (0..2, default 2; upstream sleepmode.inc: 0 AlwaysBusy, 1 ShortBusy, 2 AlwaysSleep; the module's own
/// default before the ioctl is 0).
/// No handle is shared between ports. Each compound runs under one bounded acquisition of the global SMBus mutex; on a
/// timeout nothing is sent and the compound is dropped whole. Buffer sizes per operation follow the module source
/// (<c>ioctl_smbus_xfer</c>: in 4..9, out 0..5), and the data union is marshalled as one 34-byte blob from in[4].
/// </summary>
public sealed class PawnIoSmbus : IPawnIoPort
{
    public const string IoctlIdentity = "ioctl_identity";
    public const string IoctlPortSelect = "ioctl_piix4_port_sel";
    public const string IoctlSleepMode = "ioctl_set_sleep_mode";
    public const string IoctlXfer = "ioctl_smbus_xfer";
    /// <summary>SleepMode_AlwaysBusy: both the short and the long module waits busy-wait.</summary>
    public const int SleepModeAlwaysBusy = 0;

    /// <summary>SleepMode_ShortBusy: short waits busy-wait, long waits sleep.</summary>
    public const int SleepModeShortBusy = 1;

    /// <summary>SleepMode_AlwaysSleep: both waits sleep (the value this class has always sent).</summary>
    public const int SleepModeAlwaysSleep = 2;

    public const int DefaultSleepMode = SleepModeAlwaysSleep;

    /// <summary><c>ioctl_identity</c>: out_size 3 (type, I/O base, PCI ids).</summary>
    public const int IdentityCells = 3;

    private readonly IPawnIoLibrary _library;
    private readonly ISmbusMutex _mutex;
    private readonly int _mutexWaitMs;
    private readonly object _gate = new();
    private IntPtr _handle;
    private long _mutexTimeouts;

    private PawnIoSmbus(IPawnIoLibrary library, IntPtr handle, int port, ISmbusMutex mutex, int mutexWaitMs, PawnIoIdentity identity, int sleepMode)
    {
        _library = library;
        _handle = handle;
        Port = port;
        _mutex = mutex;
        _mutexWaitMs = mutexWaitMs;
        Identity = identity;
        AppliedSleepMode = sleepMode;
    }

    public int Port { get; }

    public PawnIoIdentity Identity { get; }

    /// <summary>The mode <c>ioctl_set_sleep_mode</c> returned S_OK for on this handle (an opened port always has one).</summary>
    public int? AppliedSleepMode { get; }

    /// <summary>True for the modes the module accepts (0..2); anything else is refused before any native call.</summary>
    public static bool IsSleepMode(int mode) => mode is >= SleepModeAlwaysBusy and <= SleepModeAlwaysSleep;

    /// <summary>The upstream name of a supported mode.</summary>
    public static string SleepModeName(int mode) => mode switch
    {
        SleepModeAlwaysBusy => "AlwaysBusy",
        SleepModeShortBusy => "ShortBusy",
        SleepModeAlwaysSleep => "AlwaysSleep",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a PawnIO sleep mode (0..2)."),
    };

    public long MutexTimeouts => Interlocked.Read(ref _mutexTimeouts);

    /// <summary>
    /// Opens one handle for <paramref name="port"/>. Throws <see cref="DeviceBlockedException"/> when PawnIO refuses, the
    /// identity is not 1022:790B, the port cannot be selected or <paramref name="sleepMode"/> is not accepted; the handle is
    /// closed on every failure. A mode outside 0..2 throws <see cref="ArgumentOutOfRangeException"/> before any native call.
    /// </summary>
    public static PawnIoSmbus Open(IPawnIoLibrary library, ReadOnlySpan<byte> module, int port, ISmbusMutex mutex, int mutexWaitMs, int sleepMode = DefaultSleepMode)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(mutex);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 1); // ports 0/1 only (design §5.4)
        ArgumentOutOfRangeException.ThrowIfLessThan(mutexWaitMs, 1);
        if (!IsSleepMode(sleepMode))
        {
            throw new ArgumentOutOfRangeException(nameof(sleepMode), sleepMode, "Not a PawnIO sleep mode (0 AlwaysBusy, 1 ShortBusy, 2 AlwaysSleep).");
        }

        var hr = library.Open(out var handle);
        if (hr != 0 || handle == IntPtr.Zero)
        {
            throw new DeviceBlockedException($"pawnio_open failed: HRESULT 0x{hr:X8}");
        }

        try
        {
            hr = library.Load(handle, module);
            if (hr != 0)
            {
                throw new DeviceBlockedException($"pawnio_load failed: HRESULT 0x{hr:X8}");
            }

            Span<ulong> id = stackalloc ulong[IdentityCells];
            id.Clear();
            hr = library.Execute(handle, IoctlIdentity, ReadOnlySpan<ulong>.Empty, id, out var idCells);
            if (hr != 0)
            {
                throw new DeviceBlockedException($"{IoctlIdentity} failed: HRESULT 0x{hr:X8}");
            }

            // module contract: out_size 3, all three written; a success without them establishes nothing
            if (idCells != IdentityCells)
            {
                throw new DeviceBlockedException($"{IoctlIdentity} returned {idCells} cells, expected {IdentityCells}; identity not established");
            }

            var identity = PawnIoIdentity.Decode(id);
            if (!identity.IsAmd790B)
            {
                throw new DeviceBlockedException($"SMBus controller {identity.PciVendor:X4}:{identity.PciDevice:X4} is not 1022:790B");
            }

            // port selection changes controller routing; the module asks for the global mutex around it (reference does)
            if (!mutex.Wait(mutexWaitMs))
            {
                throw new DeviceBlockedException($"{IoctlPortSelect}({port}): SMBus mutex not acquired within {mutexWaitMs} ms");
            }

            nuint portCells;
            try
            {
                Span<ulong> prev = stackalloc ulong[1];
                prev.Clear();
                hr = library.Execute(handle, IoctlPortSelect, [(ulong)port], prev, out portCells);
            }
            finally
            {
                mutex.Release();
            }

            if (hr != 0)
            {
                throw new DeviceBlockedException($"{IoctlPortSelect}({port}) failed: HRESULT 0x{hr:X8}");
            }

            if (portCells != 1)
            {
                throw new DeviceBlockedException($"{IoctlPortSelect}({port}) returned {portCells} cells, expected 1; port selection not established");
            }

            hr = library.Execute(handle, IoctlSleepMode, [(ulong)sleepMode], Span<ulong>.Empty, out _);
            if (hr != 0)
            {
                throw new DeviceBlockedException($"{IoctlSleepMode}({sleepMode}) failed: HRESULT 0x{hr:X8}");
            }

            return new PawnIoSmbus(library, handle, port, mutex, mutexWaitMs, identity, sleepMode);
        }
        catch
        {
            library.Close(handle);
            throw;
        }
    }

    /// <summary>
    /// The <c>ioctl_smbus_xfer</c> input for one transaction: [addr, rw, command, protocol] plus, when
    /// <paramref name="union"/> is given, the 34-byte data union copied as one blob from in[4] (cells = 4 + ⌈bytes/8⌉
    /// actually needed by the protocol: 5 for byte/word writes, 9 for block writes).
    /// </summary>
    public static ulong[] XferInput(byte address, ulong readWrite, byte command, ulong protocol, ReadOnlySpan<byte> union, int cells)
    {
        if (cells < 4 || cells > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(cells), "ioctl_smbus_xfer takes 4..9 input cells.");
        }

        if (union.Length > SmbusConstants.DataUnionBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(union));
        }

        var input = new ulong[cells];
        input[0] = address;
        input[1] = readWrite;
        input[2] = command;
        input[3] = protocol;
        if (cells > 4)
        {
            Span<byte> blob = stackalloc byte[SmbusConstants.DataUnionBytes];
            blob.Clear();
            union.CopyTo(blob);
            var dst = MemoryMarshal.AsBytes(input.AsSpan(4));
            blob[..Math.Min(blob.Length, dst.Length)].CopyTo(dst);
        }

        return input;
    }

    public SmbusResult Execute(SmbusCompound compound)
    {
        var started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                return new SmbusResult(false, -1, 0, "port closed", Dropped: true);
            }

            if (!_mutex.Wait(_mutexWaitMs))
            {
                Interlocked.Increment(ref _mutexTimeouts);
                return new SmbusResult(false, -1, 0, $"SMBus mutex not acquired within {_mutexWaitMs} ms; compound dropped", Dropped: true);
            }

            try
            {
                var r = Run(compound);
                return r with { WallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
            }
            finally
            {
                _mutex.Release();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
            {
                _library.Close(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private SmbusResult Run(SmbusCompound c)
    {
        switch (c.Kind)
        {
            case SmbusCompoundKind.Probe:
                return Read(c.Address, 0, SmbusConstants.Byte);

            case SmbusCompoundKind.ReadSig:
                return Read(c.Address, c.Command, SmbusConstants.ByteData);

            case SmbusCompoundKind.RegRead:
            {
                var p = Pointer(c.Address, c.Register);
                return p.Ok ? Read(c.Address, EneRegisters.ReadCommand, SmbusConstants.ByteData) : p;
            }

            case SmbusCompoundKind.RegWrite:
            {
                var p = Pointer(c.Address, c.Register);
                return p.Ok ? WriteValue(c.Address, EneRegisters.WriteCommand, SmbusConstants.ByteData, [c.Command]) : p;
            }

            case SmbusCompoundKind.RegBlock:
            {
                var p = Pointer(c.Address, c.Register);
                if (!p.Ok)
                {
                    return p;
                }

                // block[0] = length, block[1..] = data (union i2c_smbus_data); 4 + 5 cells as the module requires
                Span<byte> union = stackalloc byte[1 + c.Data.Length];
                union[0] = (byte)c.Data.Length;
                c.Data.CopyTo(union[1..]);
                return Xfer(XferInput(c.Address, SmbusConstants.Write, EneRegisters.BlockCommand, SmbusConstants.BlockData, union, 9), 0, "block");
            }

            default:
                return new SmbusResult(false, -1, 0, $"unknown compound {c.Kind}");
        }
    }

    private SmbusResult Pointer(byte address, ushort register)
    {
        var word = EneRegisters.PointerWord(register);
        return WriteValue(address, EneRegisters.PointerCommand, SmbusConstants.WordData, [(byte)word, (byte)(word >> 8)], "pointer");
    }

    private SmbusResult WriteValue(byte address, byte command, ulong protocol, ReadOnlySpan<byte> union, string step = "write") =>
        Xfer(XferInput(address, SmbusConstants.Write, command, protocol, union, 5), 0, step);

    private SmbusResult Read(byte address, byte command, ulong protocol) =>
        Xfer(XferInput(address, SmbusConstants.Read, command, protocol, ReadOnlySpan<byte>.Empty, 4), 1, "read");

    private SmbusResult Xfer(ulong[] input, int outCells, string step)
    {
        Span<ulong> output = stackalloc ulong[Math.Max(outCells, 1)];
        output.Clear();
        var hr = _library.Execute(_handle, IoctlXfer, input, output[..outCells], out var returned);
        if (hr != 0)
        {
            return new SmbusResult(false, hr, 0, $"{step} failed: HRESULT 0x{hr:X8}", Step: step);
        }

        // a read must return its one cell holding one byte; success without it is not a zero-valued register
        if (returned != (nuint)outCells)
        {
            return new SmbusResult(false, 0, 0, $"{step} returned {returned} cells, expected {outCells}; no data", Step: step);
        }

        if (outCells == 0)
        {
            return new SmbusResult(true, 0, 0, null);
        }

        return output[0] <= 0xFF
            ? new SmbusResult(true, 0, (byte)output[0], null)
            : new SmbusResult(false, 0, 0, $"{step} returned 0x{output[0]:X} which is not one byte; no data", Step: step);
    }
}

/// <summary>The real global mutex <see cref="SmbusConstants.GlobalMutexName"/>.</summary>
public sealed class GlobalSmbusMutex : ISmbusMutex
{
    private readonly Mutex _mutex = new(false, SmbusConstants.GlobalMutexName);

    public bool Wait(int milliseconds)
    {
        try
        {
            return _mutex.WaitOne(milliseconds);
        }
        catch (AbandonedMutexException)
        {
            return true; // reference treats WAIT_ABANDONED as acquired
        }
    }

    public void Release() => _mutex.ReleaseMutex();

    public void Dispose() => _mutex.Dispose();
}

/// <summary>A pinned library admitted by the API gate, with its version evidence; disposing releases the library.</summary>
public sealed class PawnIoLoaded(IPawnIoLibrary library, PawnIoVersionEvidence version) : IDisposable
{
    public IPawnIoLibrary Library { get; } = library;

    public PawnIoVersionEvidence Version { get; } = version;

    /// <summary>The decoded exported API version (gated).</summary>
    public string ApiVersion => Version.Actual;

    /// <summary>The raw <c>pawnio_version</c> value (admitted, so always reported).</summary>
    public uint ApiRaw => Version.Raw ?? 0;

    /// <summary>The pinned file's FileVersion or "unavailable" (evidence only).</summary>
    public string FileVersion => Version.FileVersion;

    public void Dispose() => (Library as IDisposable)?.Dispose();
}

/// <summary>
/// PawnIOLib.dll bound by <see cref="NativeLibrary.Load(string)"/> from its pinned installed path only (never copied):
/// file SHA-256 verified before loading, exported API version verified after. Exports per the shipped PawnIOLib.h.
/// </summary>
public sealed unsafe class PawnIoNative : IPawnIoLibrary, IDisposable
{
    private readonly IntPtr _lib;
    private readonly delegate* unmanaged<uint*, int> _version;
    private readonly delegate* unmanaged<IntPtr*, int> _open;
    private readonly delegate* unmanaged<IntPtr, byte*, nuint, int> _load;
    private readonly delegate* unmanaged<IntPtr, byte*, ulong*, nuint, ulong*, nuint, nuint*, int> _execute;
    private readonly delegate* unmanaged<IntPtr, int> _close;

    private PawnIoNative(IntPtr lib)
    {
        _lib = lib;
        _version = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "pawnio_version");
        _open = (delegate* unmanaged<IntPtr*, int>)NativeLibrary.GetExport(lib, "pawnio_open");
        _load = (delegate* unmanaged<IntPtr, byte*, nuint, int>)NativeLibrary.GetExport(lib, "pawnio_load");
        _execute = (delegate* unmanaged<IntPtr, byte*, ulong*, nuint, ulong*, nuint, nuint*, int>)NativeLibrary.GetExport(lib, "pawnio_execute");
        _close = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(lib, "pawnio_close");
    }

    /// <summary>
    /// Verifies the file pin, records the file version (evidence only), binds the DLL at <paramref name="path"/> (tests pass
    /// <paramref name="bind"/>; the pins and the gate still run), then verifies the exported API version against
    /// <paramref name="apiVersion"/>. Any refusal releases the library; a version refusal carries its evidence.
    /// </summary>
    public static PawnIoLoaded Load(string path, string sha256, string apiVersion, Func<string, IPawnIoLibrary>? bind = null)
    {
        PawnIoPin.VerifyFile(path, sha256, "PawnIOLib.dll");
        var fileVersion = PawnIoPin.FileVersionOf(path);
        var library = (bind ?? Bind)(path);
        try
        {
            return new PawnIoLoaded(library, PawnIoPin.VerifyVersion(library, apiVersion, fileVersion));
        }
        catch
        {
            (library as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>Binds the DLL at <paramref name="path"/> and its exports; no pin check (<see cref="Load"/> runs them around it).</summary>
    private static PawnIoNative Bind(string path)
    {
        IntPtr lib;
        try
        {
            lib = NativeLibrary.Load(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            throw new DeviceBlockedException($"PawnIOLib.dll could not be loaded: {ex.Message}", ex);
        }

        try
        {
            return new PawnIoNative(lib);
        }
        catch (EntryPointNotFoundException ex)
        {
            NativeLibrary.Free(lib);
            throw new DeviceBlockedException($"PawnIOLib.dll lacks an export: {ex.Message}", ex);
        }
    }

    public int Version(out uint version)
    {
        uint v = 0;
        var hr = _version(&v);
        version = v;
        return hr;
    }

    public int Open(out IntPtr handle)
    {
        IntPtr h = IntPtr.Zero;
        var hr = _open(&h);
        handle = h;
        return hr;
    }

    public int Load(IntPtr handle, ReadOnlySpan<byte> blob)
    {
        fixed (byte* p = blob)
        {
            return _load(handle, p, (nuint)blob.Length);
        }
    }

    public int Execute(IntPtr handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output, out nuint returned)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(function + "\0");
        nuint ret = 0;
        int hr;
        fixed (byte* n = name)
        fixed (ulong* i = input)
        fixed (ulong* o = output)
        {
            hr = _execute(handle, n, i, (nuint)input.Length, o, (nuint)output.Length, &ret);
        }

        returned = ret;
        return hr;
    }

    public int Close(IntPtr handle) => _close(handle);

    public void Dispose() => NativeLibrary.Free(_lib);
}
