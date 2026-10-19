using System.Globalization;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.EneDram;

/// <summary>Options of the native ENE DRAM adapter (NATIVE design §5, §8). Every gate defaults to closed.</summary>
public sealed record EneDramOptions
{
    /// <summary>The independently installed PawnIOLib.dll; loaded from this path only (never copied).</summary>
    public string PawnIoLibPath { get; init; } = EneDramDefaults.PawnIoLibPath;

    /// <summary>Pinned SHA-256 of <see cref="PawnIoLibPath"/>; a mismatch blocks the class.</summary>
    public string PawnIoLibSha256 { get; init; } = EneDramDefaults.PawnIoLibSha256;

    /// <summary>
    /// Pinned exported API version: the decoded <c>pawnio_version</c> result after load, compared ordinally (exact); a mismatch
    /// or a failing HRESULT blocks the class before <c>pawnio_open</c>. The pinned DLL exports 2.0.0 (PAWNIO-VERSION-CORRECTION).
    /// </summary>
    public string PawnIoApiVersion { get; init; } = EneDramDefaults.PawnIoApiVersion;

    /// <summary>
    /// Installer/file release label of the pinned DLL ("2.2.0"), evidence only, no gate. The key is kept for compatibility; it
    /// is not the exported API version (<see cref="PawnIoApiVersion"/>) and says nothing about the kernel driver.
    /// </summary>
    public string PawnIoLibVersion { get; init; } = EneDramDefaults.PawnIoLibVersion;

    /// <summary>
    /// The official PawnIO.Modules 0.2.11 <c>SmbusPIIX4.bin</c> (tools/pawnio). A relative path is resolved against the
    /// Host's base directory. Empty = not configured: every port is blocked until it is set.
    /// </summary>
    public string ModulePath { get; init; } = string.Empty;

    /// <summary>Pinned SHA-256 of the module blob; any other blob is refused.</summary>
    public string ModuleSha256 { get; init; } = EneDramDefaults.ModuleSha256;

    /// <summary>
    /// Module identity gate, set by Codex after probe pawnio-id reported 1022:790B with the pinned SHAs. Not a control gate,
    /// and not needed by the pawnio-id / ene-scan probes themselves; writers need it and <see cref="ControlVerified"/>.
    /// </summary>
    public bool PawnIoModuleVerified { get; init; }

    /// <summary>
    /// Set by Codex only after the owner's E3c (design §5.4). False: the ports are discoverable bring-up targets and the
    /// controllers of a scan are listed, but no scene writer starts. No packet status or read-back sets it.
    /// </summary>
    public bool ControlVerified { get; init; }

    /// <summary>Bound of every acquisition of the global SMBus mutex; a timeout drops the whole compound.</summary>
    public int MutexWaitMs { get; init; } = EneDramDefaults.MutexWaitMs;

    /// <summary>
    /// The PawnIO module sleep mode sent once per opened port with <c>ioctl_set_sleep_mode</c> (NATIVE-DYNAMIC-SYNC-FIX §A):
    /// 0 AlwaysBusy, 1 ShortBusy, 2 AlwaysSleep (upstream sleepmode.inc). Source default 2, the value always sent before.
    /// Any other value blocks the port before the library, the module or the device is opened; there is no fallback. It
    /// changes only how the module waits between SMBus status polls, never a register or a packet.
    /// </summary>
    public int SleepMode { get; init; } = EneDramDefaults.SleepMode;

    /// <summary>Configured per-frame budget in ms (ceiling = ⌊1000 / ms⌋); 0 = the class ceiling of 10 fps per port (design §9).</summary>
    public int FrameBudgetMs { get; init; }

    /// <summary>
    /// The address-remap gate (design §5.4): admits the reference bootstrap (0x80F8/0x80F9 at 0x77 only). Whether a remap
    /// persists is unknown (RQ-E7). Source default false; set in OUTPUT appsettings by Codex only.
    /// </summary>
    public bool RemapOnOpen { get; init; }

    /// <summary>Narrows the physical candidate list (0x70..0x77, 0x4F, 0x66, 0x67, 0x39..0x3D); empty = all of it. Never widens it.</summary>
    public IReadOnlyList<int> CandidateAddresses { get; init; } = [];

    /// <summary>
    /// Copied by the Host from UnifiedRgb:Bringup:HoldBlackOnRelease (overnight addendum §4): release, close and Host stop
    /// skip direct-off (a stick in direct mode stays there, black); only black markers are accepted; a failed black send
    /// stops all further sends to that controller.
    /// </summary>
    public bool HoldBlackOnRelease { get; init; }
}

public static class EneDramDefaults
{
    public const int CeilingFps = 10;
    public const int MutexWaitMs = 200;

    /// <summary>Source default of <see cref="EneDramOptions.SleepMode"/>: 2 (AlwaysSleep).</summary>
    public const int SleepMode = PawnIoSmbus.DefaultSleepMode;
    public const string PawnIoLibPath = @"C:\Program Files\PawnIO\PawnIOLib.dll";
    public const string PawnIoLibSha256 = "D71F62627D66983BB9F5B1C269F27BCD8C1B8A46E794377A6330F84C198F4443";
    /// <summary>The exported API version of the pinned DLL (all three version exports report 0x00020000).</summary>
    public const string PawnIoApiVersion = "2.0.0";

    /// <summary>The installer/file release label of the pinned DLL: evidence only, no gate.</summary>
    public const string PawnIoLibVersion = "2.2.0";

    /// <summary>tools/pawnio/SmbusPIIX4.bin of the official PawnIO.Modules 0.2.11 release (SHA256SUMS, PROVENANCE.md).</summary>
    public const string ModuleSha256 = "91F9B4B1C39E3D399CE48477A89D8F6BD3E58A2241064A4DAEC3A1513DFF56E5";

    /// <summary>The PIIX4 ports offered as bring-up targets and scanned (design §5.4).</summary>
    public static IReadOnlyList<int> Ports { get; } = [0, 1];

    /// <summary>
    /// The configured candidates in the reference order of <see cref="EneRegisters.PhysicalCandidates"/>; empty = all.
    /// Throws for an address outside the physical list (incl. SPD 0x50..0x57): configuration never widens it.
    /// </summary>
    public static IReadOnlyList<byte> Candidates(IReadOnlyList<int>? configured)
    {
        if (configured is null || configured.Count == 0)
        {
            return EneRegisters.PhysicalCandidates;
        }

        foreach (var a in configured)
        {
            if (a is < 0 or > 0x7F || EneRegisters.IsSpd((byte)a) || !EneRegisters.PhysicalCandidates.Contains((byte)a))
            {
                throw new ArgumentOutOfRangeException(nameof(configured), a, $"EneDram:CandidateAddresses: 0x{a:X2} is not an ENE DRAM candidate (physical list only; SPD 0x50..0x57 never).");
            }
        }

        return [.. EneRegisters.PhysicalCandidates.Where(c => configured.Contains(c))];
    }
}

/// <summary>Opens PIIX4 ports for the adapter: the real one pins PawnIOLib.dll and the module; tests pass a scripted bus.</summary>
public interface IEneSmbusProvider : IDisposable
{
    /// <summary>
    /// Opens <paramref name="port"/>: one handle, identity 1022:790B, port selected once. Throws
    /// <see cref="DeviceBlockedException"/> on any pin, load, identity or selection failure (nothing is left open).
    /// </summary>
    IPawnIoPort Open(int port);

    /// <summary>
    /// The pins as configured, for probe rows: library path/SHA, the gated API version (<c>libApiExpected</c>), the file release
    /// label (<c>libFileRelease</c>, evidence only), module path/SHA.
    /// </summary>
    IReadOnlyDictionary<string, string> Pins { get; }

    /// <summary>The <c>pawnio_version</c> evidence of the loaded library (admitted by the API gate); null until a load got that far.</summary>
    PawnIoVersionEvidence? Version { get; }
}

/// <summary>
/// The real provider: PawnIOLib.dll from its pinned installed path (SHA-256 before loading, exported API version after; the
/// file version is recorded as evidence only), the module blob from <see cref="EneDramOptions.ModulePath"/> after its SHA-256,
/// one global-mutex object per port. Nothing is loaded before a probe or a verified discovery asks for a port; the library
/// stays loaded until disposal. Tests pass <paramref name="bind"/> (replaces only the binding of the DLL; the pins and the API
/// gate still run) and <paramref name="mutex"/>.
/// </summary>
public sealed class PawnIoSmbusProvider(EneDramOptions options, Func<string, IPawnIoLibrary>? bind = null, Func<ISmbusMutex>? mutex = null) : IEneSmbusProvider
{
    private readonly object _gate = new();
    private PawnIoLoaded? _library;
    private byte[]? _module;
    private bool _disposed;

    public IReadOnlyDictionary<string, string> Pins => new Dictionary<string, string>
    {
        ["libPath"] = options.PawnIoLibPath,
        ["libSha256"] = options.PawnIoLibSha256,
        ["libApiExpected"] = options.PawnIoApiVersion,
        ["libFileRelease"] = options.PawnIoLibVersion,
        ["modulePath"] = ResolveModulePath(options.ModulePath),
        ["moduleSha256"] = options.ModuleSha256,
    };

    public PawnIoVersionEvidence? Version
    {
        get
        {
            lock (_gate)
            {
                return _library?.Version;
            }
        }
    }

    /// <summary>Rooted paths as given; relative ones against the Host's base directory; empty stays empty (not configured).</summary>
    public static string ResolveModulePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    public IPawnIoPort Open(int port)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // §A: validated before the module, the library or the device is touched; an unsupported mode never falls back
            if (!PawnIoSmbus.IsSleepMode(options.SleepMode))
            {
                throw new DeviceBlockedException(string.Create(CultureInfo.InvariantCulture,
                    $"EneDram:SleepMode {options.SleepMode} is not a PawnIO sleep mode (0 AlwaysBusy, 1 ShortBusy, 2 AlwaysSleep)"));
            }

            if (_module is null)
            {
                var path = ResolveModulePath(options.ModulePath);
                if (path.Length == 0)
                {
                    throw new DeviceBlockedException("PawnIO module not configured (EneDram:ModulePath)");
                }

                _module = PawnIoPin.LoadModule(path, options.ModuleSha256);
            }

            _library ??= PawnIoNative.Load(options.PawnIoLibPath, options.PawnIoLibSha256, options.PawnIoApiVersion, bind);
            var m = mutex?.Invoke() ?? new GlobalSmbusMutex();
            try
            {
                return new OwnedMutexPort(PawnIoSmbus.Open(_library.Library, _module, port, m, options.MutexWaitMs, options.SleepMode), m);
            }
            catch
            {
                m.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _library?.Dispose();
            _library = null;
        }
    }

    /// <summary>A port that also disposes its mutex object.</summary>
    private sealed class OwnedMutexPort(PawnIoSmbus port, ISmbusMutex mutex) : IPawnIoPort
    {
        public int Port => port.Port;

        public PawnIoIdentity Identity => port.Identity;

        public long MutexTimeouts => port.MutexTimeouts;

        public int? AppliedSleepMode => port.AppliedSleepMode;

        public SmbusResult Execute(SmbusCompound compound) => port.Execute(compound);

        public void Dispose()
        {
            port.Dispose();
            mutex.Dispose();
        }
    }
}

public sealed class EneDramAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.EneDram;

    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var options = sp.GetService(typeof(EneDramOptions)) as EneDramOptions ?? new EneDramOptions();
        var loggers = sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        return new EneDramAdapter(new PawnIoSmbusProvider(options), options, loggers?.CreateLogger<EneDramAdapter>(), sp.GetService(typeof(INativeMapSource)) as INativeMapSource);
    }
}
