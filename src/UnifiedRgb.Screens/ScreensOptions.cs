namespace UnifiedRgb.Screens;

/// <summary>
/// Bound from UnifiedRgb:Screens. Every switch that can write to a screen is off by default: output needs <see cref="Enabled"/>
/// AND the family's <see cref="ScreenFamilyOptions.Output"/>. Discovery (read-only SetupAPI), telemetry and preview work
/// regardless. There is no automatic takeover: a running vendor program blocks its screens and is never stopped.
/// </summary>
public sealed class ScreensOptions
{
    /// <summary>Master output gate. While false no screen device handle is ever opened.</summary>
    public bool Enabled { get; set; }

    public ScreenFamilyOptions TlLcd { get; set; } = new();

    public ScreenFamilyOptions H2 { get; set; } = new();

    public ScreenFamilyOptions Turzx { get; set; } = new();

    /// <summary>Native volatile backlight wake target (0..255), independent of image brightness. Model reference default170; no proven hardware readback.</summary>
    public byte TurzxRestoreBacklight { get; set; } = 170;

    /// <summary>Native LCD-only wake target, percentage0..100; not image brightness or a hardware readback.</summary>
    public byte TlLcdRestoreBacklight { get; set; } = 100;

    /// <summary>Native LCD-only wake target, percentage0..100; does not set pump/fan parameters.</summary>
    public byte H2RestoreBacklight { get; set; } = 100;

    public byte RestoreBacklightOf(ScreenFamily family) => family switch
    {
        ScreenFamily.Turzx => TurzxRestoreBacklight,
        ScreenFamily.TlLcd when TlLcdRestoreBacklight <= 100 => TlLcdRestoreBacklight,
        ScreenFamily.H2 when H2RestoreBacklight <= 100 => H2RestoreBacklight,
        _ => throw new ArgumentOutOfRangeException(nameof(family), "Invalid LCD backlight family or percentage."),
    };

    /// <summary>
    /// Clockwise rotation from the landscape 1920x480 authoring canvas to the native 480x1920 TURZX raster (90 or 270). This is
    /// our own pixel transform; no firmware orientation command is ever sent. The physically correct value is unverified.
    /// </summary>
    public int TurzxLandscapeRotation { get; set; } = 90;

    /// <summary>Hotplug re-enumeration period while output is enabled.</summary>
    public int DiscoveryIntervalMs { get; set; } = 3000;

    /// <summary>Optional read-only GPU metrics through the driver's System32 nvml.dll.</summary>
    public bool NvmlTelemetry { get; set; } = true;

    public long AssetStoreMaxBytes { get; set; } = 1L << 30;

    public int AssetStoreMaxCount { get; set; } = 256;

    /// <summary>First reopen delay after a transfer/open failure; doubles per failure up to <see cref="BackoffMaxMs"/>.</summary>
    public int BackoffInitialMs { get; set; } = 1000;

    public int BackoffMaxMs { get; set; } = 30000;

    /// <summary>The manual fan-LCD group restart (RestartFanLcdAsync). Disabled and unmapped by default.</summary>
    public FanLcdRestartOptions FanLcdRestart { get; set; } = new();

    /// <summary>
    /// Exact screen ids ("screen:&lt;family&gt;:&lt;instance&gt;", ordinal match) whose 8-bit indexed (palette) output the owner verified
    /// on the hardware. Empty by default: hardware Indexed8 output is unproven, so a palette encoding policy is refused for every
    /// screen. Configuration only; nothing verifies or adds an id automatically.
    /// </summary>
    public string[] Indexed8VerifiedScreenIds { get; set; } = [];
    /// <summary>Exact H2/TURZX ids individually accepted for clock-driven all-IDR H.264. TURZX has a separate live PNG layer;
    /// H2 composites live information into each picture. Empty by default; a bounded trial does not save verification.</summary>
    public string[] NativeH264VerifiedScreenIds { get; set; } = [];

    public ScreenFamilyOptions For(ScreenFamily family) => family switch
    {
        ScreenFamily.TlLcd => TlLcd,
        ScreenFamily.H2 => H2,
        ScreenFamily.Turzx => Turzx,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    /// <summary>True when frames may be written to screens of <paramref name="family"/>.</summary>
    public bool OutputAllowed(ScreenFamily family) => Enabled && For(family).Output;
}

public sealed class ScreenFamilyOptions
{
    /// <summary>Per-family output gate (in addition to <see cref="ScreensOptions.Enabled"/>).</summary>
    public bool Output { get; set; }

    /// <summary>The owner accepted this family's physical playback; reported only, it gates nothing.</summary>
    public bool ControlVerified { get; set; }
}

/// <summary>
/// UnifiedRgb:Screens:FanLcdRestart. <see cref="ControllerId"/> is the RGB controller id of ONE Lian Li three-fan TL LCD group
/// ("lianli:rf:&lt;MAC&gt;") and <see cref="ScreenIds"/> the exact TL-LCD screen ids ("screen:tl-lcd:&lt;instance&gt;") of its fans.
/// Nothing is hardcoded or inferred: the owner maps them. Invalid or incomplete mapping refuses the restart before any I/O.
/// </summary>
public sealed class FanLcdRestartOptions
{
    public bool Enabled { get; set; }

    public string ControllerId { get; set; } = string.Empty;

    public string[] ScreenIds { get; set; } = [];
}
