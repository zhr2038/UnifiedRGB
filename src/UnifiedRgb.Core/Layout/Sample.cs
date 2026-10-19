namespace UnifiedRgb.Core.Layout;

/// <summary>
/// Normalized coordinates of one LED. U: position along the zone/group in [0,1];
/// Theta: ring angle in radians (0 for non-ring zones); X/Y: planar position in [0,1].
/// </summary>
public readonly record struct Sample(float U, float Theta, float X, float Y);
