namespace UnifiedRgb.Host;

/// <summary>Local installation pins; never authored scene content. Empty/off defaults perform no HID I/O.</summary>
public sealed record KeyboardDisplayOptions
{
    public bool Enabled { get; init; }
    public bool ControlVerified { get; init; }
    public bool LightingVerified { get; init; }
    public string DeviceKey { get; init; } = string.Empty;
    public string FirmwareHeader { get; init; } = string.Empty;
    public bool WirelessControlVerified { get; init; }
    public bool WirelessLightingVerified { get; init; }
    public string WirelessReceiverKey { get; init; } = string.Empty;
    public string WirelessFirmwareHeader { get; init; } = string.Empty;
    public byte Brightness { get; init; } = 100;

    /// <summary>The installed keyboard already draws the native entry Logo. Do not upload a second Host Logo;
    /// ordinary, non-native display output keeps its existing entry presentation. Local presentation preference only.</summary>
    public bool DeviceShowsNativeEntryLogo { get; init; }

    /// <summary>
    /// Host admission of the local keyboard OLED animation (stock/legacy owner only; suppressed under installed native-menu
    /// ownership). Off by default: no preparation, timeline read or animation frame happens. An out-of-range bound below keeps
    /// it unadmitted. The interval is a minimum time between animation uploads, not a measured or promised frame rate.
    /// </summary>
    public bool AnimationAdmitted { get; init; }
    public int AnimationMinimumIntervalMs { get; init; } = 2000;
    /// <summary>A completed animation upload (selection to ACK) slower than this stops new animation selections.</summary>
    public int AnimationMaximumUploadMs { get; init; } = 1000;
    public long AnimationCacheBytes { get; init; } = 16L * 1024 * 1024;
    public int AnimationGridFloorMs { get; init; } = 100;
    public int AnimationPrepareTimeoutMs { get; init; } = 30_000;

    internal const int AnimationMaximumIntervalMs = 10_000;

    internal string? AnimationAdmissionProblem =>
        !AnimationAdmitted ? "not admitted"
        : AnimationMinimumIntervalMs is < 1000 or > AnimationMaximumIntervalMs ? "AnimationMinimumIntervalMs must be 1000..10000"
        : AnimationMaximumUploadMs is < 250 or > 4000 ? "AnimationMaximumUploadMs must be 250..4000"
        : AnimationCacheBytes is < UnifiedRgb.Contracts.KeyboardOledFormat.PackedBytes or > 32L * 1024 * 1024 ? "AnimationCacheBytes must be 6656..33554432"
        : AnimationGridFloorMs is < 20 or > 1000 ? "AnimationGridFloorMs must be 20..1000"
        : AnimationPrepareTimeoutMs is < 500 or > 120_000 ? "AnimationPrepareTimeoutMs must be 500..120000" : null;

    // Manual verification of the selected device key is the admission gate. FirmwareHeader/WirelessFirmwareHeader are
    // diagnostics (blank, stale or arbitrary); live reply grammar, profile and settings are checked on the open connection.
    internal bool HasVerifiedWiredIdentity => ControlVerified && DeviceKey.Length == 64 && DeviceKey.All(char.IsAsciiHexDigit);
    internal bool HasVerifiedWirelessIdentity => WirelessControlVerified && WirelessReceiverKey.Length == 64
        && WirelessReceiverKey.All(char.IsAsciiHexDigit);
    internal bool HasVerifiedWiredLighting => HasVerifiedWiredIdentity && LightingVerified;
    internal bool HasVerifiedWirelessLighting => HasVerifiedWirelessIdentity && WirelessLightingVerified;
    internal bool HasVerifiedLightingRoute => HasVerifiedWiredLighting || HasVerifiedWirelessLighting;
}
