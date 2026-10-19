using System.Globalization;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using Color = System.Windows.Media.Color;

namespace UnifiedRgb.Screens.Composition;

/// <summary>The composed opaque device canvas (authoring orientation) and any non-fatal warning (e.g. a missing asset).</summary>
internal sealed record ComposeResult(PixelFrame Canvas, string? Warning);

/// <summary>
/// Composes one assignment into its device canvas (STA thread only): background colour, asset fitted by cover/contain and
/// centred, telemetry overlay (round layout inside the visible circle on TL-LCD/H2, see <see cref="OverlayShape"/>), clockwise
/// rotation (content is laid out at the swapped size for 90/270; an overlay region is placed on that content canvas, so it turns
/// with the content), brightness, opaque. Background colour and asset still fill
/// the whole raster on every family.
/// Fitted asset frames are cached up to <paramref name="cacheBytes"/>; once full, further frames are recomputed per use.
/// </summary>
internal sealed class ScreenCompositor(AssetStore assets, long cacheBytes, OverlayRenderer? overlay = null)
{
    private readonly Dictionary<(string Asset, int Frame, int W, int H, string Fit), PixelFrame> _cache = [];
    private readonly OverlayRenderer _overlay = overlay ?? new();
    private long _cachedBytes;

    /// <summary>True when the result can change over time for this assignment (animation or live telemetry).</summary>
    public bool IsDynamic(ScreenAssignmentDto a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.Overlay != "none" || (a.BackgroundAssetId is { } id && assets.Find(id) is { Animated: true });
    }

    /// <summary>Prefix of the warning of a frame composed for strict (TimelineId) content that cannot play: black, no overlay.</summary>
    public const string BlockedPrefix = "BLOCKED: ";

    /// <summary>
    /// The frame of strict package content (an assignment with a TimelineId) that cannot play: opaque black at the device canvas
    /// size, no asset, no overlay, no invented background, labelled "BLOCKED: why". A package screen never shows substitute content.
    /// </summary>
    public static ComposeResult ComposeBlocked(ScreenFamilyInfo family, string why)
    {
        ArgumentNullException.ThrowIfNull(family);
        var canvas = PixelFrame.Solid(family.Width, family.Height, 0, 0, 0);
        PixelOps.Flatten(canvas);
        return new ComposeResult(canvas, BlockedPrefix + why);
    }

    public void ClearCache()
    {
        _cache.Clear();
        _cachedBytes = 0;
    }

    public ComposeResult Compose(ScreenFamilyInfo family, ScreenAssignmentDto a, long elapsedMs, PcTelemetryDto? telemetry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(a);
        var swap = a.Rotation is 90 or 270;
        var cw = swap ? family.Height : family.Width;
        var ch = swap ? family.Width : family.Height;
        var (r, g, b) = ScreenColor.Parse(a.BackgroundColor);
        var content = PixelFrame.Solid(cw, ch, r, g, b);
        string? warning = null;

        if (a.BackgroundAssetId is { } assetId)
        {
            if (assets.Find(assetId) is { } asset)
            {
                var fitted = Fitted(asset, asset.FrameAt(elapsedMs), cw, ch, a.Fit);
                PixelOps.BlendOver(content, fitted, (cw - fitted.Width) / 2, (ch - fitted.Height) / 2);
            }
            else if (a.TimelineId is not null)
            {
                // strict package content: its normalized media must be installed before output; never a background substitute
                return ComposeBlocked(family, $"package media asset {assetId} is not installed");
            }
            else
            {
                warning = $"asset {assetId} is missing; background colour only";
            }
        }

        var shape = OverlayRenderer.ShapeOf(family.Family);
        if (a.OverlayLayout is not null && (shape != OverlayShape.Rectangle || a.Overlay != OverlayLayouts.RegionOverlay))
        {
            // validation refuses this before any commit; a region is never dropped or widened to the full screen
            return ComposeBlocked(family, "an overlay region needs a rectangular screen and the dashboard overlay");
        }

        if (a.Overlay != "none")
        {
            var (ar, ag, ab) = ScreenColor.Parse(a.AccentColor);
            // the region is in content-canvas fractions (before the local rotation below)
            if (_overlay.Render(cw, ch, shape, a.Overlay, Color.FromRgb(ar, ag, ab), telemetry, now, a.OverlayLayout) is { } layer)
            {
                PixelOps.BlendOver(content, layer, 0, 0);
            }
            else
            {
                warning = Join(warning, "telemetry overlay rendered nothing (WPF offscreen rendering unavailable"
                    + (HeadlessRendering.Enabled ? ")" : $"; {HeadlessRendering.SwitchName} is not set)"));
            }
        }

        var canvas = a.Rotation == 0 ? content : PixelOps.Rotate(content, a.Rotation);
        PixelOps.ApplyBrightness(canvas, a.Brightness);
        PixelOps.Flatten(canvas);
        return new ComposeResult(canvas, warning);
    }

    private PixelFrame Fitted(StoredAsset asset, int frame, int cw, int ch, string fit)
    {
        var key = (asset.Id, frame, cw, ch, fit);
        if (_cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var src = WpfCodec.DecodePng(assets.ReadFrame(asset, frame), 1920L * 1920);
        PixelFrame fitted;
        if (fit == "cover")
        {
            // crop the source to the canvas aspect first (centred), so the resample never exceeds the canvas
            var s = Math.Max(cw / (double)src.Width, ch / (double)src.Height);
            var sw = Math.Clamp((int)Math.Round(cw / s), 1, src.Width);
            var sh = Math.Clamp((int)Math.Round(ch / s), 1, src.Height);
            var cropped = sw == src.Width && sh == src.Height ? src : Crop(src, (src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh);
            fitted = PixelOps.Resize(cropped, cw, ch);
        }
        else
        {
            var (w, h) = PixelOps.FitSize(src.Width, src.Height, cw, ch, cover: false);
            fitted = PixelOps.Resize(src, w, h);
        }

        if (_cachedBytes + fitted.ByteCount <= cacheBytes)
        {
            _cache[key] = fitted;
            _cachedBytes += fitted.ByteCount;
        }

        return fitted;
    }

    private static string Join(string? first, string next) => first is null ? next : first + "; " + next;

    private static PixelFrame Crop(PixelFrame src, int x, int y, int w, int h)
    {
        var dst = new PixelFrame(w, h);
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(src.Pixels, (((y + row) * src.Width) + x) * 4, dst.Pixels, row * dst.Stride, dst.Stride);
        }

        return dst;
    }
}

/// <summary>"#RRGGBB" colours (the only accepted form).</summary>
internal static class ScreenColor
{
    public static bool IsValid(string? s) =>
        s is { Length: 7 } && s[0] == '#' && int.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);

    public static (byte R, byte G, byte B) Parse(string s)
    {
        if (!IsValid(s))
        {
            throw new ArgumentException($"'{s}' is not a #RRGGBB colour.", nameof(s));
        }

        var v = int.Parse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

public sealed class ScreenEncodeException : Exception
{
    public ScreenEncodeException()
    {
    }

    public ScreenEncodeException(string message)
        : base(message)
    {
    }

    public ScreenEncodeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>An encoded frame: the command and image bytes for the wire, what the screen will show (authoring orientation), and a note.</summary>
internal sealed record EncodedFrame(byte Command, byte[] Image, PixelFrame Shown, string Note);

/// <summary>
/// Per-family encoding within the protocol's size limit, or a clear failure (never a truncated or oversized packet).
/// TL: baseline JPEG, quality stepped 90→40 until it fits 101888 bytes. H2: the same ladder within 2 MiB. TURZX: RGBA PNG of
/// the canvas rotated by the configured landscape rotation to the native 480x1920 raster; if it exceeds 524288 bytes the colour
/// depth steps 8→6→5 bits per channel (the approved bounded ladder; the preview shows the posterized result), then the frame
/// fails clearly. There is no 4-bit step. The default legacy policy preserves that output byte-for-byte. The opt-in
/// lossless-first-v1 policy tries a bounded RGBA8 lossless optimization before each depth reduction, with encoded-byte caching.
/// Indexed8 is research-only and is never emitted.
/// </summary>
internal static class ScreenFrameEncoder
{
    private static readonly int[] JpegQualities = [90, 80, 70, 60, 50, 40];
    private static readonly int[] PngBits = [8, 6, 5];
    private static readonly Encoding.PngFrameCache PngCache = new(96L * 1024 * 1024, 512);
    private static readonly object OptimizationGate = new();

    public static EncodedFrame Encode(ScreenFamilyInfo family, PixelFrame canvas, int turzxLandscapeRotation,
        string quality = Encoding.ScreenEncodingPolicies.Legacy, bool indexed8Verified = false)
    {
        if (!Encoding.ScreenEncodingPolicies.IsKnown(quality))
            throw new ScreenEncodeException("Unsupported PNG quality policy. Indexed8 remains research-only: firmware acceptance and a quality benefit are unproved.");
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(canvas);
        if (canvas.Width != family.Width || canvas.Height != family.Height)
        {
            throw new ArgumentException($"canvas {canvas.Width}x{canvas.Height} is not the {family.Width}x{family.Height} {family.Token} canvas", nameof(canvas));
        }

        var max = ScreenProtocol.MaxImageBytes(family.Family);
        if (family.Codec == ScreenCodec.TurzxPng)
        {
            if (turzxLandscapeRotation is not (90 or 270))
            {
                throw new ScreenEncodeException($"TurzxLandscapeRotation must be 90 or 270 (got {turzxLandscapeRotation}).");
            }

            if (quality == Encoding.ScreenEncodingPolicies.LosslessFirstV1)
            {
                var key = Encoding.PngFrameCache.Key(canvas, turzxLandscapeRotation, quality);
                if (PngCache.Find(key) is { } hit) return Cached(hit, canvas);
                // At most one optimizer owns its temporary RGBA/filter/compression buffers in this process.
                // It runs on a screen STA, never on the RGB scheduler/shared clock; hits do no optimization or I/O.
                lock (OptimizationGate)
                {
                    if (PngCache.Find(key) is { } raced) return Cached(raced, canvas);
                    var value = EncodeLosslessFirst(canvas, turzxLandscapeRotation, max);
                    PngCache.Add(key, value);
                    return Cached(value, canvas);
                }
            }

            var smallest = int.MaxValue;
            foreach (var bits in PngBits)
            {
                var shown = canvas.Clone();
                PixelOps.Posterize(shown, bits);
                var png = WpfCodec.EncodePng(PixelOps.Rotate(shown, turzxLandscapeRotation));
                if (png.Length <= max)
                {
                    var note = string.Create(CultureInfo.InvariantCulture,
                        $"PNG {canvas.Height}x{canvas.Width} rot{turzxLandscapeRotation} {(bits == 8 ? "full colour" : bits + "-bit posterized")} {png.Length} B <= {max}");
                    return new EncodedFrame(ScreenProtocol.PushPngCommand, png, shown, note);
                }

                smallest = Math.Min(smallest, png.Length);
            }

            throw new ScreenEncodeException($"The TURZX PNG is {smallest} bytes even at {PngBits[^1]} bits per channel; the limit is {max}. Simplify the content.");
        }

        var best = int.MaxValue;
        foreach (var q in JpegQualities)
        {
            var jpeg = WpfCodec.EncodeJpeg(canvas, q);
            if (jpeg.Length <= max)
            {
                return new EncodedFrame(ScreenProtocol.PushJpegCommand, jpeg, canvas,
                    string.Create(CultureInfo.InvariantCulture, $"JPEG q{q} {jpeg.Length} B <= {max}"));
            }

            best = Math.Min(best, jpeg.Length);
        }

        throw new ScreenEncodeException($"The {family.Token} JPEG is {best} bytes even at quality 40; the limit is {max}.");
    }
    private static EncodedFrame Cached(Encoding.PngFrameCache.Value value, PixelFrame canvas)
    {
        var shown = canvas;
        if (value.Bits != 8) { shown = canvas.Clone(); PixelOps.Posterize(shown, value.Bits); }
        return new EncodedFrame(ScreenProtocol.PushPngCommand, value.Image, shown, value.Note);
    }

    private static Encoding.PngFrameCache.Value EncodeLosslessFirst(PixelFrame canvas, int rotation, int max)
    {
        var smallest = int.MaxValue;
        foreach (var bits in PngBits)
        {
            var shown = canvas.Clone(); PixelOps.Posterize(shown, bits);
            var native = PixelOps.Rotate(shown, rotation);
            var png = WpfCodec.EncodePng(native);
            var optimized = false;
            // The full-colour WIC PNG is returned byte-for-byte when it fits. Optimization never quantizes pixels.
            // At each lower depth try the established WIC output first, so a new policy cannot regress the old size ladder.
            if (png.Length > max)
            {
                var candidate = Encoding.BoundedPngEncoder.Optimize(native, max, png);
                if (candidate.Length < png.Length) { png = candidate; optimized = true; }
            }
            smallest = Math.Min(smallest, png.Length);
            if (png.Length <= max)
            {
                var note = string.Create(CultureInfo.InvariantCulture,
                    $"PNG {canvas.Height}x{canvas.Width} rot{rotation} {(bits == 8 ? "full colour" : bits + "-bit posterized")} {png.Length} B <= {max}; lossless-first-v1 {(optimized ? "RGBA lossless optimization" : "original WIC")}");
                return new Encoding.PngFrameCache.Value(png, bits, note);
            }
        }
        throw new ScreenEncodeException($"The TURZX PNG is {smallest} bytes even after bounded lossless optimization at 5 bits per channel; the limit is {max}. Simplify the content.");
    }
}


