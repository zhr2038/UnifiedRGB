namespace UnifiedRgb.Devices;

public enum LayoutKind
{
    Linear,
    Ring,
    Matrix,
    Point,
}

/// <summary>
/// Physical LED arrangement of a zone: Linear(n) | Ring(n, startAngle, cw) | Matrix(w, h) | Point.
/// A value type so projection never allocates.
/// </summary>
public readonly record struct ZoneLayout
{
    public LayoutKind Kind { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Ring only: angle of LED 0 in radians.</summary>
    public float StartAngle { get; init; }

    /// <summary>Ring only: LED index increases clockwise.</summary>
    public bool Clockwise { get; init; }

    public int LedCount => Kind switch
    {
        LayoutKind.Point => 1,
        LayoutKind.Matrix => Width * Height,
        _ => Width,
    };

    public static ZoneLayout Linear(int n) =>
        n > 0 ? new() { Kind = LayoutKind.Linear, Width = n, Height = 1 } : throw new ArgumentOutOfRangeException(nameof(n));

    public static ZoneLayout Ring(int n, float startAngle = 0f, bool clockwise = true) =>
        n > 0 ? new() { Kind = LayoutKind.Ring, Width = n, Height = 1, StartAngle = startAngle, Clockwise = clockwise }
              : throw new ArgumentOutOfRangeException(nameof(n));

    public static ZoneLayout Matrix(int w, int h) =>
        w > 0 && h > 0 ? new() { Kind = LayoutKind.Matrix, Width = w, Height = h }
                       : throw new ArgumentOutOfRangeException(nameof(w));

    public static ZoneLayout Point { get; } = new() { Kind = LayoutKind.Point, Width = 1, Height = 1 };

    public override string ToString() => Kind switch
    {
        LayoutKind.Linear => $"Linear({Width})",
        LayoutKind.Ring => $"Ring({Width})",
        LayoutKind.Matrix => $"Matrix({Width}x{Height})",
        _ => "Point",
    };
}
