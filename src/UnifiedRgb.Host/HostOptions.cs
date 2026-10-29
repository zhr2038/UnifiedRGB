namespace UnifiedRgb.Host;

/// <summary>
/// Bound from the "UnifiedRgb" configuration section. Hardware adapters default to off: each is enabled only after its
/// [HW] bring-up (IMPLEMENTATION_PLAN M3–M5) with the vendor writers stopped.
/// </summary>
public sealed class UnifiedRgbOptions
{
    public const string Section = "UnifiedRgb";
    internal string? StartupRecoveryNotice { get; set; }
    /// <summary>Additional local process-only blockers. Built-in vendor and legacy compatibility rules remain effective.</summary>
    public UnifiedRgb.Transports.Process.ExternalProcessWriterOptions[] ExternalProcessWriters { get; set; } = [];

    public int Fps { get; set; } = 60;

    public bool UseVirtualAdapter { get; set; } = true;

    public bool EnableLianLi { get; set; }

    public bool EnableMsiGpu { get; set; }

    public bool EnableOpenRgb { get; set; }

    /// <summary>Native ASUS Aura HID (NATIVE-2); off by default. Writers additionally need AsusAura:ControlVerified.</summary>
    public bool EnableAsusAura { get; set; }

    /// <summary>ROG receiver metadata/leased diagnostics. Off by default; keyboard output also needs explicit local pins and verification.</summary>
    public bool EnableAsusRog { get; set; }

    public UnifiedRgb.Devices.AsusRog.RogOptions AsusRog { get; set; } = new();

    /// <summary>Optional local same-tick fallback for aggregate peripherals. Empty preserves all existing scenes.</summary>
    public ZoneFollowerOptions[] ZoneFollowers { get; set; } = [];

    public KeyboardDisplayOptions KeyboardDisplay { get; set; } = new();

    public KeyboardNativeMenuOptions KeyboardNativeMenu { get; set; } = new();

    /// <summary>
    /// Native ENE DRAM over PawnIO SMBus (NATIVE-3); off by default. Writers additionally need EneDram:PawnIoModuleVerified
    /// and EneDram:ControlVerified.
    /// </summary>
    public bool EnableEneDram { get; set; }

    /// <summary>MSI GPU safety gate (register map verification), bound from UnifiedRgb:MsiGpu.</summary>
    public UnifiedRgb.Devices.MsiGpu.MsiGpuOptions MsiGpu { get; set; } = new();

    /// <summary>Headless OpenRGB (port, pinned binary, config), bound from UnifiedRgb:OpenRgb.</summary>
    public UnifiedRgb.Devices.OpenRgbSdk.OpenRgbSdkOptions OpenRgb { get; set; } = new();

    /// <summary>Native Aura gates, HID timeout and owner-entered header LED counts, bound from UnifiedRgb:AsusAura.</summary>
    public UnifiedRgb.Devices.AsusAura.AsusAuraOptions AsusAura { get; set; } = new();

    /// <summary>Native ENE gates, PawnIO pins, module path, mutex bound and candidate narrowing, bound from UnifiedRgb:EneDram.</summary>
    public UnifiedRgb.Devices.EneDram.EneDramOptions EneDram { get; set; } = new();

    /// <summary>Lian Li safety gate, receiver ids and the configured wireless devices, bound from UnifiedRgb:LianLi.</summary>
    public UnifiedRgb.Devices.LianLi.LianLiOptions LianLi { get; set; } = new();

    /// <summary>Native bring-up sessions (NATIVE design §6) and the overnight hold-black option, bound from UnifiedRgb:Bringup.</summary>
    public BringupOptions Bringup { get; set; } = new();

    /// <summary>
    /// Screen-only LCD backend (OPUS-SCREENS), bound from UnifiedRgb:Screens. Output is off by default (Enabled plus a per-family
    /// Output gate); discovery, telemetry and preview work regardless. Independent of every RGB adapter and writer.
    /// </summary>
    public UnifiedRgb.Screens.ScreensOptions Screens { get; set; } = new();

    public bool TracePayload { get; set; }

    /// <summary>WriterGuard re-scan period (ARCHITECTURE §7.5).</summary>
    public int GuardRescanSeconds { get; set; } = 5;

    public string DataDirectory { get; set; } = @"%ProgramData%\UnifiedRgb";

    public long LogFileSizeLimitBytes { get; set; } = 5 * 1024 * 1024;

    public int LogRetainedFileCount { get; set; } = 10;

    public string PipeName { get; set; } = "UnifiedRgb";

    public string ResolvedDataDirectory => Environment.ExpandEnvironmentVariables(DataDirectory);
}

/// <summary>Bound from UnifiedRgb:Bringup (NATIVE design §6, §8; overnight addendum §4). Read once at start; never reloaded live.</summary>
public sealed class BringupOptions
{
    /// <summary>Lease of a session begun with leaseSeconds ≤ 0.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Longest lease a session may ask for; larger requests are clamped.</summary>
    public int MaxLeaseSeconds { get; set; } = 600;

    /// <summary>
    /// Overnight addendum §4 (default false): every native release keeps the device dark instead of handing it back. GPU: one
    /// all-black frame, no 0x06 ← 1, handle kept (plus the MsiGpu:KeepAliveMs resend); ENE: direct-off skipped; Aura: nothing
    /// (unchanged). Only black markers are accepted, and a clean Host stop defers the vendor-service restore (records kept).
    /// </summary>
    public bool HoldBlackOnRelease { get; set; }
}
