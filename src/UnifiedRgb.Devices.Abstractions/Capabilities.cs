namespace UnifiedRgb.Devices;

/// <summary>A hardware (firmware-driven) lighting mode, e.g. MSI GPU "Breathing". Not a firmware update.</summary>
public sealed record FirmwareModeInfo(string Name, int Value, int MinColors, int MaxColors, bool HasSpeed, bool HasBrightness);

/// <summary>Selected hardware lighting mode with parameters.</summary>
public sealed record FirmwareMode(string Name, IReadOnlyList<Rgb> Colors, int Speed, int Brightness);

/// <summary>
/// How colour reaches a controller (ARCHITECTURE-D2 §1). Streamed controllers take a frame per MasterClock tick through
/// <see cref="IDeviceAdapter.WriteFrame"/>; UploadedLoop controllers take a pre-rendered loop through
/// <see cref="ILoopUploadAdapter.UploadLoopAsync"/> on scene change and play it themselves.
/// </summary>
public enum Delivery
{
    Streamed,
    UploadedLoop,
}

/// <param name="MaxLoopFrames">UploadedLoop only: most frames one loop may hold.</param>
/// <param name="MinIntervalMs">UploadedLoop only: shortest (default) per-frame interval.</param>
/// <param name="MaxUploadBytes">UploadedLoop only: largest compressed upload the device accepts.</param>
public sealed record Capabilities(
    bool DirectMode,
    int MaxFps,
    IReadOnlyList<FirmwareModeInfo> FirmwareModes,
    int BrightnessLevels,
    int SpeedLevels,
    int MaxColors,
    Delivery Delivery = Delivery.Streamed,
    int MaxLoopFrames = 0,
    double MinIntervalMs = 0,
    int MaxUploadBytes = 0)
{
    public static Capabilities DirectOnly(int maxFps) => new(true, maxFps, Array.Empty<FirmwareModeInfo>(), 0, 0, 0);

    public static Capabilities UploadedLoopOnly(int maxLoopFrames, double minIntervalMs, int maxUploadBytes) =>
        new(false, 0, Array.Empty<FirmwareModeInfo>(), 0, 0, 0, Delivery.UploadedLoop, maxLoopFrames, minIntervalMs, maxUploadBytes);
}
