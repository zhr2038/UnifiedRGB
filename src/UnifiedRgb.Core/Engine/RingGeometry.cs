namespace UnifiedRgb.Core.Engine;

/// <summary>
/// WP-14: a ring device's calibration as the engine sees it (H2-VERTICAL-FILL §3): vertical fill on the block face, the
/// half-LED bottom seam, and the fine clockwise offset in LEDs (applied by the adapter's wire map; reported only).
/// </summary>
public readonly record struct RingGeometry(bool FaceOnFill, bool HalfStep, int OffsetLeds);
