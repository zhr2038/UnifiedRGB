using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Runtime;

internal sealed partial class ScreenWorker
{
    private MfH264IntraEncoder? _videoEncoder;
    private (bool SingleSlice, int Quality)? _videoEncoderVariant;
    private INativeVideoSession? _videoSession;
    private readonly OverlayRenderer _videoOverlay = new();
    private PixelFrame? _lastVideoOverlay;
    private ScreenAssignmentDto? _lastVideoOverlayAssignment;
    private byte[]? _transparentVideoLayer;
    private long _videoPosition, _previousVideoPosition;
    private int _videoFrameIndex;
    private long _videoWraps;
    private NativeVideoPerformanceDto? _videoFacts;
    private double _videoOverlayComposeMs, _videoOverlayEncodeMs;
    private bool _lastVideoOverlaySimplePng;
    private string? _videoOverlayEncodingNote;

    private ScreenImage EncodeVideo(PixelFrame canvas)
    {
        var turzx = _device.Family.Family == ScreenFamily.Turzx;
        if (_device.Family.Family is not (ScreenFamily.Turzx or ScreenFamily.H2) || !_ctx.NativeVideoAllowed(_device.Id))
            throw new ScreenEncodeException("Native H.264 needs a verified H2/TURZX or an explicit bounded trial.");
        var native = turzx ? PixelOps.Rotate(canvas, _ctx.Options.TurzxLandscapeRotation) : canvas;
        var trial = _ctx.VideoTrialOf(_device.Id)?.Info;
        var variant = (SingleSlice: turzx && trial?.SingleSlice == true, Quality: turzx ? trial?.Quality ?? 95 : 95);
        if (_videoEncoderVariant != variant)
        {
            _videoEncoder?.Dispose(); _videoEncoder = null;
            _videoEncoderVariant = variant;
        }
        _videoEncoder ??= new MfH264IntraEncoder(native.Width, native.Height, fullRange: turzx,
            singleSlice: variant.SingleSlice, quality: variant.Quality);
        var bytes = _videoEncoder.Encode(native);
        return new(Protocol.TurzxVideoProtocol.DataCommand, bytes, turzx
            ? $"H.264 all-IDR Baseline Q{variant.Quality}, {_videoEncoder.LastSliceCount} slices; host/shared-clock-selected picture; live PNG overlay"
            : "H2 H.264 all-IDR Baseline; host/shared-clock-selected picture; telemetry composited before encoding");
    }

    private bool SendVideo(ScreenImage frame, ScreenAssignmentDto assignment, ScreenTimestamp stamp, bool allowCorrelatedReplies)
    {
        if (_channel is null) throw new InvalidOperationException("No video channel.");
        if (!_ctx.NativeVideoAllowed(_device.Id)) return false; // normal trial expiry/cancel is not a dropped/error frame
        if (_videoSession is null)
        {
            if (_device.Family.Family == ScreenFamily.Turzx)
            {
                _transparentVideoLayer ??= WpfCodec.EncodePng(new PixelFrame(_device.Family.Height, _device.Family.Width));
                _videoSession = new TurzxVideoSession(_channel, stamp, () => _stopping, allowCorrelatedReplies);
            }
            else _videoSession = new H2VideoSession(_channel, stamp, () => _stopping, _ctx.VideoTrialOf(_device.Id)?.Info.Fps ?? assignment.Fps);
            _videoSession.Begin(_transparentVideoLayer ?? []);
            _lastVideoOverlay = null; _lastVideoOverlayAssignment = null; _videoOverlayEncodingNote = null;
            OnVideoSessionBegan();
        }
        _videoSession.SendFrame(frame.Image);
        var overlayStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var layer = _device.Family.Family == ScreenFamily.Turzx
            ? RenderVideoOverlay(_videoOverlay, _device.Family, assignment, _ctx.Telemetry(), _ctx.Time.GetUtcNow()) : null;
        _videoOverlayComposeMs += System.Diagnostics.Stopwatch.GetElapsedTime(overlayStart).TotalMilliseconds;
        var trial = _ctx.VideoTrialOf(_device.Id)?.Info;
        var simplePng = trial?.SimpleOverlayPng == true;
        if (!PreparedOverlayStep(_videoSession, layer, assignment, simplePng, trial)
            && (!ReferenceEquals(layer, _lastVideoOverlay) || assignment != _lastVideoOverlayAssignment || simplePng != _lastVideoOverlaySimplePng))
        {
            if (layer is not null)
            {
                var encodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var pixels = OverlayCanvas(layer, assignment, _ctx.Options.TurzxLandscapeRotation);
                var (encoded, note) = EncodeOverlayPng(pixels, simplePng);
                _videoOverlayEncodeMs += System.Diagnostics.Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                SendVideoOverlay(_videoSession, encoded, clear: false, prepared: false, trial);
                _videoOverlayEncodingNote = note;
            }
            else
            {
                if (_lastVideoOverlay is not null) SendVideoOverlay(_videoSession, _transparentVideoLayer!, clear: true, prepared: false, trial);
                _videoOverlayEncodingNote = null;
            }
            _lastVideoOverlay = layer; _lastVideoOverlayAssignment = assignment; _lastVideoOverlaySimplePng = simplePng;
        }
        if (assignment.TimelineId is not null && _videoPosition < _previousVideoPosition) _videoWraps++;
        _previousVideoPosition = _videoPosition;
        Volatile.Write(ref _videoFacts, new(_videoSession.AccessUnits, _videoSession.OverlayWrites, _videoSession.LastQueueDepth,
            _videoPosition, _videoFrameIndex, TimelineSource ?? "local-epoch", _videoWraps)
            { EncodingNote = frame.Note, OverlayEncodingNote = _videoOverlayEncodingNote });
        return true;
    }

    private void StopVideo()
    {
        var session = _videoSession; _videoSession = null;
        Volatile.Write(ref _videoFacts, null); _previousVideoPosition = 0; _videoWraps = 0;
        _lastVideoOverlay = null; _lastVideoOverlayAssignment = null; _videoOverlayEncodingNote = null;
        StopPreparedOverlay(); // invalidates older prepared work before the stop controls; no join here
        session?.Stop();
    }

    internal static PixelFrame? RenderVideoOverlay(OverlayRenderer renderer, ScreenFamilyInfo family, ScreenAssignmentDto assignment,
        PcTelemetryDto? telemetry, DateTimeOffset now)
    {
        if (assignment.Overlay == "none") return null;
        var (r, g, b) = ScreenColor.Parse(assignment.AccentColor);
        var swap = assignment.Rotation is 90 or 270;
        return renderer.Render(swap ? family.Height : family.Width, swap ? family.Width : family.Height, OverlayShape.Rectangle,
            assignment.Overlay, System.Windows.Media.Color.FromRgb(r, g, b), telemetry, now, assignment.OverlayLayout)
            ?? throw new ScreenEncodeException("Live overlay was not rendered; native video is not sendable.");
    }

    private static PixelFrame OverlayCanvas(PixelFrame layer, ScreenAssignmentDto assignment, int internalRotation)
    {
        var canvas = PixelOps.Rotate(layer, assignment.Rotation);
        PixelOps.ApplyBrightness(canvas, assignment.Brightness); // keep alpha; do not flatten the independent PNG overlay
        return PixelOps.Rotate(canvas, internalRotation);
    }

    /// <summary>Only encoder-created RGBA8 PNG enters the experimental repacker; never arbitrary source/upload bytes.</summary>
    private static (byte[] Bytes, string Note) EncodeOverlayPng(PixelFrame pixels, bool simplePng)
    {
        var original = WpfCodec.EncodePng(pixels);
        if (!simplePng)
        {
            if (original.Length > ScreenProtocol.TurzxMaxPngBytes) throw new ScreenEncodeException("The live PNG overlay exceeds the TURZX image budget.");
            return (original, "RGBA8 WIC PNG");
        }
        var repacked = LosslessOverlayPng.Repack(original);
        if (repacked.Candidate is { } bytes) return (bytes, "RGBA8 filter NONE PNG; exact lossless repack");
        if (repacked.OriginalWithinBudget) return (original, "RGBA8 WIC PNG; filter NONE candidate over budget, explicit fallback");
        throw new ScreenEncodeException("Neither the original live overlay nor its lossless repack fits the TURZX image budget.");
    }

    internal static (PixelFrame Shown, string Note) PreviewVideo(ScreenFamilyInfo family, ScreenAssignmentDto assignment,
        PixelFrame background, PcTelemetryDto? telemetry, DateTimeOffset now, int internalRotation,
        bool singleSlice = false, int quality = 95, bool simpleOverlayPng = false)
    {
        if (family.Family is not (ScreenFamily.Turzx or ScreenFamily.H2)) throw new ScreenEncodeException("Native H.264 protocol is not verified for this family.");
        var turzx = family.Family == ScreenFamily.Turzx;
        var native = turzx ? PixelOps.Rotate(background, internalRotation) : background;
        using var encoder = new MfH264IntraEncoder(native.Width, native.Height, fullRange: turzx, singleSlice: singleSlice, quality: quality);
        var data = encoder.Encode(native);
        var decoded = MfH264IntraEncoder.Decode(data, native.Width, native.Height, fullRange: turzx);
        var shown = turzx ? PixelOps.Rotate(decoded, 360 - internalRotation) : decoded;
        var layer = turzx ? RenderVideoOverlay(new OverlayRenderer(), family, assignment, telemetry, now) : null;
        string? overlayNote = null;
        if (layer is not null)
        {
            var pixels = PixelOps.Rotate(layer, assignment.Rotation); PixelOps.ApplyBrightness(pixels, assignment.Brightness);
            // Match the PNG layer through WIC too; no original/unencoded background masquerades as the H.264 result.
            var (encoded, note) = EncodeOverlayPng(pixels, simpleOverlayPng);
            overlayNote = note;
            PixelOps.BlendOver(shown, WpfCodec.DecodePng(encoded, 1920L * 1920), 0, 0);
        }
        return (shown, turzx ? $"H.264 Baseline all-IDR Q{quality}, {encoder.LastSliceCount} slices, {data.Length} B; decoded actual access unit + lossless live PNG overlay ({overlayNote ?? "none"}); not sent"
            : $"H2 H.264 Baseline all-IDR {data.Length} B; decoded actual complete picture including telemetry; not sent");
    }
}
