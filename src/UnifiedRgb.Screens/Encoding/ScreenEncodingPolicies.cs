namespace UnifiedRgb.Screens.Encoding;

/// <summary>Versioned local device policies; never part of authored scene-package content.</summary>
public static class ScreenEncodingPolicies
{
    public const string Legacy = "legacy";
    public const string LosslessFirstV1 = "lossless-first-v1";
    public const string PaletteFirstV1 = "palette-first-v1";
    public const string NativeH264IntraV1 = "h264-intra-v1";
    public const string JpegDetailV1 = "jpeg-detail-v1";
    // Reserved research token: no proved firmware acceptance or quality benefit yet; never a selectable runtime policy.
    public static bool IsKnown(string? value) => value is Legacy or LosslessFirstV1 or NativeH264IntraV1 or JpegDetailV1;
}

