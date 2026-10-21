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
    private TurzxVideoSession? _videoSession;
    private readonly OverlayRenderer _videoOverlay = new();
    private PixelFrame? _lastVideoOverlay;
    private ScreenAssignmentDto? _lastVideoOverlayAssignment;
    private byte[]? _transparentVideoLayer;
    private long _videoPosition, _previousVideoPosition;
    private int _videoFrameIndex;
    private long _videoWraps;
    private NativeVideoPerformanceDto? _videoFacts;

    private ScreenImage EncodeVideo(PixelFrame canvas)
    {
        if (_device.Family.Family != ScreenFamily.Turzx || !_ctx.NativeVideoAllowed(_device.Id))
            throw new ScreenEncodeException("Native H.264 is not individually verified for this TURZX (or an explicit trial is not active).");
        var native = PixelOps.Rotate(canvas, _ctx.Options.TurzxLandscapeRotation);
        _videoEncoder ??= new MfH264IntraEncoder(native.Width, native.Height);
        var bytes = _videoEncoder.Encode(native);
        return new(Protocol.TurzxVideoProtocol.DataCommand, bytes, "H.264 all-IDR Baseline; host/shared-clock-selected picture; live PNG overlay");
    }

    private bool SendVideo(ScreenImage frame, ScreenAssignmentDto assignment, ScreenTimestamp stamp)
    {
        if (_channel is null) throw new InvalidOperationException("No video channel.");
        if (!_ctx.NativeVideoAllowed(_device.Id)) return false; // normal trial expiry/cancel is not a dropped/error frame
        if (_videoSession is null)
        {
            _transparentVideoLayer ??= WpfCodec.EncodePng(new PixelFrame(_device.Family.Height, _device.Family.Width));
            _videoSession = new(_channel, stamp, () => _stopping);
            _videoSession.Begin(_transparentVideoLayer);
            _lastVideoOverlay = null; _lastVideoOverlayAssignment = null;
        }
        _videoSession.SendFrame(frame.Image);
        var layer = RenderVideoOverlay(_videoOverlay, _device.Family, assignment, _ctx.Telemetry(), _ctx.Time.GetUtcNow());
        if (!ReferenceEquals(layer, _lastVideoOverlay) || assignment != _lastVideoOverlayAssignment)
        {
            if (layer is not null)
            {
                var pixels = OverlayCanvas(layer, assignment, _ctx.Options.TurzxLandscapeRotation);
                _videoSession.SendOverlay(WpfCodec.EncodePng(pixels));
            }
            else if (_lastVideoOverlay is not null) _videoSession.SendOverlay(_transparentVideoLayer!);
            _lastVideoOverlay = layer; _lastVideoOverlayAssignment = assignment;
        }
        if (assignment.TimelineId is not null && _videoPosition < _previousVideoPosition) _videoWraps++;
        _previousVideoPosition = _videoPosition;
        Volatile.Write(ref _videoFacts, new(_videoSession.AccessUnits, _videoSession.OverlayWrites, _videoSession.LastQueueDepth,
            _videoPosition, _videoFrameIndex, TimelineSource ?? "local-epoch", _videoWraps));
        return true;
    }

    private void StopVideo()
    {
        var session = _videoSession; _videoSession = null;
        Volatile.Write(ref _videoFacts, null); _previousVideoPosition = 0; _videoWraps = 0;
        _lastVideoOverlay = null; _lastVideoOverlayAssignment = null;
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

    internal static (PixelFrame Shown, string Note) PreviewVideo(ScreenFamilyInfo family, ScreenAssignmentDto assignment,
        PixelFrame background, PcTelemetryDto? telemetry, DateTimeOffset now, int internalRotation)
    {
        if (family.Family != ScreenFamily.Turzx) throw new ScreenEncodeException("Native H.264 is only supported on verified TURZX.");
        var native = PixelOps.Rotate(background, internalRotation);
        using var encoder = new MfH264IntraEncoder(native.Width, native.Height);
        var data = encoder.Encode(native);
        var decoded = MfH264IntraEncoder.Decode(data, native.Width, native.Height);
        var shown = PixelOps.Rotate(decoded, 360 - internalRotation);
        var layer = RenderVideoOverlay(new OverlayRenderer(), family, assignment, telemetry, now);
        if (layer is not null)
        {
            var pixels = PixelOps.Rotate(layer, assignment.Rotation); PixelOps.ApplyBrightness(pixels, assignment.Brightness);
            // Match the PNG layer through WIC too; no original/unencoded background masquerades as the H.264 result.
            var encoded = WpfCodec.EncodePng(pixels);
            if (encoded.Length > ScreenProtocol.TurzxMaxPngBytes) throw new ScreenEncodeException("The live PNG overlay exceeds the TURZX image budget.");
            PixelOps.BlendOver(shown, WpfCodec.DecodePng(encoded, 1920L * 1920), 0, 0);
        }
        return (shown, $"H.264 Baseline all-IDR {data.Length} B; decoded actual access unit + lossless live PNG overlay; not sent");
    }
}
