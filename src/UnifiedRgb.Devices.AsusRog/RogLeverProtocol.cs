namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// Owner-observed function-button cycle names on the verified Azoth X: Volume0 -> Media1 -> OLED brightness3 -> Custom7.
/// Values 2, 4, 5 and 6 are inside the getter's 0..7 range but have no observed meaning and therefore no name.
/// </summary>
public enum RogLeverFunction { Volume = 0, Media = 1, OledBrightness = 3, Custom = 7 }

/// <summary>Current lever function (0..7) from the read-only 22/01 getter or a 91 notification.</summary>
public sealed record RogLeverModeState(byte Mode)
{
    public RogLeverFunction? Function => RogLeverProtocol.KnownFunction(Mode);
}

/// <summary>Enabled lever functions from the read-only 25/0B getter: bit <c>i</c> set means function <c>i</c> is in the cycle.</summary>
public sealed record RogLeverEnabledModes(byte Mask)
{
    public bool IsEnabled(byte mode) => mode <= RogLeverProtocol.MaximumMode && (Mask & (1 << mode)) != 0;

    public IReadOnlyList<byte> Modes => Enumerable.Range(0, RogLeverProtocol.MaximumMode + 1)
        .Where(m => (Mask & (1 << m)) != 0).Select(m => (byte)m).ToArray();
}

/// <summary>
/// Two read-only Azoth X lever getters positively tested on the owner's unit (keyboard 1.00.24, receiver 7.00.08, RF route
/// 1ACE/1C25/control RID2): <c>[02,22,01,00,00,0...]</c> returns the current function in native byte5 (0..7), and
/// <c>[02,25,0B,00,00,0...]</c> returns eight 0/1 enable flags in native bytes5..12. Replies must be complete 64-byte exact
/// echoes; FFAA, non-echo headers, unknown argument bytes and out-of-range values are errors, never a default 0. The
/// trailing bytes after the value are not interpreted. There is no setter, binding query or binding write here.
/// </summary>
public static class RogLeverProtocol
{
    public const byte ControlReportId = 2;
    public const byte MaximumMode = 7;

    /// <summary>Unrelated queued reports a getter may discard before its echo; FFAA for the route is never discarded.</summary>
    public const int MaximumGetterReads = 16;

    public static byte[] CurrentModeQuery(byte reportId) => Getter(reportId, 0x22, 0x01);

    public static byte[] EnabledModesQuery(byte reportId) => Getter(reportId, 0x25, 0x0B);

    public static RogLeverModeState ParseCurrentMode(ReadOnlySpan<byte> reply, byte reportId)
    {
        RequireGetterEcho(reply, reportId, 0x22, 0x01);
        if (reply[5] > MaximumMode) throw new InvalidDataException("Unknown lever function value; no default is substituted.");
        return new RogLeverModeState(reply[5]);
    }

    public static RogLeverEnabledModes ParseEnabledModes(ReadOnlySpan<byte> reply, byte reportId)
    {
        RequireGetterEcho(reply, reportId, 0x25, 0x0B);
        var mask = 0;
        for (var mode = 0; mode <= MaximumMode; mode++)
        {
            var flag = reply[5 + mode];
            if (flag > 1) throw new InvalidDataException("Unknown lever enable flag; no default is substituted.");
            mask |= flag << mode;
        }

        return new RogLeverEnabledModes((byte)mask);
    }

    /// <summary>The paired route's FFAA status: a refusal of the getter, never skipped as an old status report.</summary>
    public static bool IsRefusal(ReadOnlySpan<byte> reply, byte reportId) =>
        reply.Length >= 3 && reply[0] == reportId && reply[1] == 0xFF && reply[2] == 0xAA;

    public static RogLeverFunction? KnownFunction(byte mode) => mode switch
    {
        0 => RogLeverFunction.Volume,
        1 => RogLeverFunction.Media,
        3 => RogLeverFunction.OledBrightness,
        7 => RogLeverFunction.Custom,
        _ => null,
    };

    private static byte[] Getter(byte reportId, byte command, byte key)
    {
        RequireControlReportId(reportId);
        var report = new byte[RogProtocol.ReportLength];
        report[0] = reportId;
        report[1] = command;
        report[2] = key;
        return report;
    }

    private static void RequireGetterEcho(ReadOnlySpan<byte> reply, byte reportId, byte command, byte key)
    {
        RequireControlReportId(reportId);
        if (reply.Length != RogProtocol.ReportLength) throw new InvalidDataException("A lever getter reply must be one complete 64-byte vendor report.");
        if (IsRefusal(reply, reportId)) throw new InvalidDataException("The lever getter was refused (FFAA); no retry.");
        RogProtocol.RequireEcho(reply, reportId, command, key);
        if (reply[3] != 0 || reply[4] != 0) throw new InvalidDataException("The lever getter echo has unknown argument bytes.");
    }

    private static void RequireControlReportId(byte reportId)
    {
        if (reportId != ControlReportId)
            throw new ArgumentOutOfRangeException(nameof(reportId), "Lever getters are verified only on the Azoth X control report id 2.");
    }
}
