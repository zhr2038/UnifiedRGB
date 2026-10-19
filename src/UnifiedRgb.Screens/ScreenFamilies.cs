using System.Globalization;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Screens;

public enum ScreenFamily
{
    TlLcd,
    H2,
    Turzx,
}

/// <summary>How a family's frames are encoded and framed (OPUS-SCREENS work order, source-proven layouts only).</summary>
public enum ScreenCodec
{
    /// <summary>TL-LCD 1CBE:0006: JPEG cmd 101 in a fixed 102400-byte buffer (vendor STRController/WinUsb).</summary>
    TlJpeg,

    /// <summary>H2 1CBE:A021: JPEG cmd 101, 512-byte header + image (vendor WinUsbH2.PushImage).</summary>
    H2Jpeg,

    /// <summary>TURZX 1CBE:0088: PNG cmd 102, 512-byte header + image (MIT turing-smart-screen-cli ffc652ab).</summary>
    TurzxPng,
}

/// <summary>
/// One supported screen family. Width/Height are the authoring canvas at rotation 0. Expected endpoints are checked at open
/// when the primary source fixes them (vendor OUT 0x01 / IN 0x81); TURZX's reference discovers the first bulk pair of
/// interface 0, so it has none. Timeouts bound every transfer.
/// </summary>
public sealed record ScreenFamilyInfo(
    ScreenFamily Family,
    string Token,
    ushort ProductId,
    int Width,
    int Height,
    ScreenCodec Codec,
    int WriteTimeoutMs,
    int ReadTimeoutMs,
    byte? ExpectedOutEndpoint,
    byte? ExpectedInEndpoint,
    string DisplayName)
{
    public const ushort VendorId = 0x1CBE;

    public static ScreenFamilyInfo TlLcd { get; } =
        new(ScreenFamily.TlLcd, "tl-lcd", 0x0006, 400, 400, ScreenCodec.TlJpeg, 500, 50, 0x01, 0x81, "TL-LCD fan screen");

    public static ScreenFamilyInfo H2 { get; } =
        new(ScreenFamily.H2, "h2", 0xA021, 480, 480, ScreenCodec.H2Jpeg, 1000, 2000, 0x01, 0x81, "H2 pump screen");

    public static ScreenFamilyInfo Turzx { get; } =
        new(ScreenFamily.Turzx, "turzx", 0x0088, 1920, 480, ScreenCodec.TurzxPng, 2000, 2000, null, null, "TURZX case screen");

    public static IReadOnlyList<ScreenFamilyInfo> All { get; } = [TlLcd, H2, Turzx];

    public static ScreenFamilyInfo Of(ScreenFamily family) => family switch
    {
        ScreenFamily.TlLcd => TlLcd,
        ScreenFamily.H2 => H2,
        ScreenFamily.Turzx => Turzx,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    /// <summary>Exact VID/PID match; anything else (other Lian Li or TURZX models included) is not a supported screen.</summary>
    public static ScreenFamilyInfo? FromUsb(ushort vid, ushort pid) =>
        vid == VendorId ? All.FirstOrDefault(f => f.ProductId == pid) : null;
}

/// <summary>Screen ids: "screen:&lt;token&gt;:&lt;instance&gt;", lower case. The instance is the USB instance id (the serial here).</summary>
public static partial class ScreenIds
{
    public static string Create(ScreenFamilyInfo family, string instance)
    {
        ArgumentNullException.ThrowIfNull(family);
        var id = string.Create(CultureInfo.InvariantCulture, $"screen:{family.Token}:{instance.ToLowerInvariant()}");
        return TryParse(id, out _, out _) ? id : throw new ArgumentException($"'{instance}' is not a valid screen instance id.", nameof(instance));
    }

    public static bool TryParse(string? id, out ScreenFamilyInfo family, out string instance)
    {
        family = ScreenFamilyInfo.TlLcd;
        instance = string.Empty;
        if (id is null)
        {
            return false;
        }

        var m = IdPattern().Match(id);
        if (!m.Success)
        {
            return false;
        }

        family = ScreenFamilyInfo.All.Single(f => f.Token == m.Groups[1].Value);
        instance = m.Groups[2].Value;
        return true;
    }

    [GeneratedRegex("^screen:(tl-lcd|h2|turzx):([a-z0-9&_.\\-]{1,64})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
