namespace UnifiedRgb.Devices.AsusRog;

public enum RogControlNotificationKind
{
    /// <summary>Not a verified control layout (for example the common 61/71 key reports); its body was not read.</summary>
    Dropped,

    /// <summary>Verified <c>[04,72,mask,00*18]</c>: mask 0 neutral, 1 Up, 2 Middle, 4 Down.</summary>
    LeverLevel,

    /// <summary>Verified <c>[04,91,00,00,mode,00*16]</c> with mode 0..7.</summary>
    Mode,

    /// <summary>A 72/91 report whose layout differs from the verified one, or a wrong native length/report id.</summary>
    Malformed,
}

/// <summary>Typed classification only; no report bytes are retained. <see cref="Value"/> is the lever mask or the mode.</summary>
public readonly record struct RogControlNotification(RogControlNotificationKind Kind, byte Value);

/// <summary>
/// Strict classifier for the owner-verified Azoth X RF notification collection (native 21 bytes including RID4). Only the
/// report id and the type byte are read first. Bytes after the type byte are read solely for the two verified types 72 and
/// 91; every other type (including the ordinary-key 61/71 reports) is <see cref="RogControlNotificationKind.Dropped"/>
/// without inspecting its body. No keycode, text or raw byte leaves this method.
/// </summary>
public static class RogControlNotificationParser
{
    public const int NativeLength = 21;
    public const byte NotifyReportId = 4;
    public const byte LeverType = 0x72;
    public const byte ModeType = 0x91;

    public static RogControlNotification Classify(ReadOnlySpan<byte> nativeReport)
    {
        if (nativeReport.Length != NativeLength || nativeReport[0] != NotifyReportId) return new(RogControlNotificationKind.Malformed, 0);
        switch (nativeReport[1])
        {
            case LeverType:
            {
                var mask = nativeReport[2];
                return mask is 0 or 1 or 2 or 4 && !nativeReport[3..].ContainsAnyExcept((byte)0)
                    ? new(RogControlNotificationKind.LeverLevel, mask)
                    : new(RogControlNotificationKind.Malformed, 0);
            }

            case ModeType:
            {
                var mode = nativeReport[4];
                return nativeReport[2] == 0 && nativeReport[3] == 0 && mode <= RogLeverProtocol.MaximumMode && !nativeReport[5..].ContainsAnyExcept((byte)0)
                    ? new(RogControlNotificationKind.Mode, mode)
                    : new(RogControlNotificationKind.Malformed, 0);
            }

            default:
                return new(RogControlNotificationKind.Dropped, 0);
        }
    }
}
