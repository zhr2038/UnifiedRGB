using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Process;

[Flags]
public enum WriterKind
{
    None = 0,
    Process = 1,
    Service = 2,
}

/// <summary>A vendor/third-party program that writes to lighting hardware we also want to own.</summary>
/// <param name="ServiceName">SCM service name when it differs from the process name
/// (e.g. service <c>LConnectService</c> vs process <c>L-Connect-Service</c>).</param>
public sealed record KnownWriter(string Name, WriterKind Kind, IReadOnlyList<DeviceClass> Affects, string? ServiceName = null)
{
    /// <summary>The name SCM knows this writer by.</summary>
    public string ScmName => ServiceName ?? Name;
}

/// <summary>
/// A running program that is <b>not</b> a confirmed lighting writer but is worth showing (NATIVE design §8):
/// Diagnostics lists it as "running, not a confirmed writer"; the guard verdict ignores it.
/// </summary>
public sealed record WriterCandidate(string Name, IReadOnlyList<DeviceClass> Affects, string Reason);

/// <summary>
/// ARCHITECTURE §7.1 + NATIVE design §8: the confirmed writers WriterGuard checks before any OpenAsync. A row blocks the
/// classes it affects until stopped/closed. Rows are confirmed writers only; suspicion goes to <see cref="Candidates"/>.
/// </summary>
public static class KnownWriters
{
    private static readonly DeviceClass[] LianLiAndThirdParty = [DeviceClass.LianLi, DeviceClass.OpenRgb];

    /// <summary>MSI rows affect the GPU and, through LEDKeeper2's <c>DRAM_SetRGB</c> path (G.Skill over SMBus), the DRAM.</summary>
    private static readonly DeviceClass[] Msi = [DeviceClass.MsiGpu, DeviceClass.EneDram];

    /// <summary>The ASUS AacMB motherboard HAL: motherboard lighting access (Aura HID and the ENE SMBus).</summary>
    private static readonly DeviceClass[] AsusHal = [DeviceClass.AsusAura, DeviceClass.EneDram];

    private static readonly DeviceClass[] AllHardware =
        [DeviceClass.LianLi, DeviceClass.MsiGpu, DeviceClass.OpenRgb, DeviceClass.AsusAura, DeviceClass.EneDram];

    /// <summary>Process name of any OpenRGB instance; it is a writer unless its PID is one we supervise.</summary>
    public const string OpenRgbProcessName = "OpenRGB";

    /// <summary>Process name of the ASUS AacMB motherboard HAL (kind Process: listed in ProcessesToClose, never killed by the guard).</summary>
    public const string AsusHalProcessName = "Aac3572MbHal_x86";

    public static IReadOnlyList<KnownWriter> All { get; } =
    [
        new("L-Connect-Service", WriterKind.Process | WriterKind.Service, LianLiAndThirdParty, ServiceName: "LConnectService"),
        new("L-Connect-Service-Watcher", WriterKind.Process | WriterKind.Service, LianLiAndThirdParty, ServiceName: "LConnectServiceWatcher"),
        new("L-Connect 3", WriterKind.Process, LianLiAndThirdParty),
        new("Mystic_Light_Service", WriterKind.Process | WriterKind.Service, Msi),
        new("LightKeeperService", WriterKind.Process | WriterKind.Service, Msi),
        new("LEDKeeper2", WriterKind.Process, Msi),
        new(OpenRgbProcessName, WriterKind.Process, AllHardware),
        new("LConnectMSI5090Mirror", WriterKind.Process | WriterKind.Service, Msi),
        new("LianliToMSIGPU-OpenRGB", WriterKind.Process, Msi),
        new(AsusHalProcessName, WriterKind.Process, AsusHal),
    ];

    /// <summary>Informational only (NATIVE design §8): never stopped, never part of the verdict.</summary>
    public static IReadOnlyList<WriterCandidate> Candidates { get; } =
    [
        new("ArmouryCrateService", [DeviceClass.AsusAura, DeviceClass.EneDram], "ASUS Armoury Crate host service; no lighting write established"),
        new("asComSvc", [DeviceClass.AsusAura, DeviceClass.EneDram], "ASUS common service; no lighting write established"),
    ];
}
