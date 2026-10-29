namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Power policy is separate from RGB. Only an explicit owner operation calls this; no lighting frame changes it.</summary>
public sealed record RogPowerState(ushort ProductId, byte IdleCode, byte LowBatteryWarning, byte SavingMode,
    byte FullChargingEffect, byte WakeOnMotion, byte ChargingLighting, string ReplyHex)
{
    public bool SameOtherSettings(RogPowerState other) => ProductId == other.ProductId
        && LowBatteryWarning == other.LowBatteryWarning && SavingMode == other.SavingMode
        && FullChargingEffect == other.FullChargingEffect && WakeOnMotion == other.WakeOnMotion
        && ChargingLighting == other.ChargingLighting;
}

public sealed record RogPowerChange(RogPowerState Before, RogPowerState After);

public static class RogPowerProtocol
{
    public const byte Never = 255;

    public static byte[] Query(RogPairedDevice target)
    {
        RequireTarget(target);
        var report = new byte[RogProtocol.ReportLength];
        report[0] = target.ReportId;
        report[1] = 0x12;
        report[2] = target.IsAzothX ? (byte)1 : (byte)7;
        return report;
    }

    public static RogPowerState Parse(RogPairedDevice target, ReadOnlySpan<byte> reply)
    {
        RequireTarget(target);
        RogProtocol.RequireEcho(reply, target.ReportId, 0x12, target.IsAzothX ? (byte)1 : (byte)7);
        if (reply.Length != RogProtocol.ReportLength) throw new InvalidDataException("ROG power reply must be a complete vendor report.");
        var idle = reply[target.IsAzothX ? 7 : 6];
        var battery = reply[target.IsAzothX ? 6 : 5];
        if (!IsKnownIdle(idle) || battery is < 1 or > 100) throw new InvalidDataException("ROG power state is unavailable, asleep or outside known ranges.");
        if (target.IsAzothX)
            return new(target.ProductId, idle, reply[10], reply[8], 0, 0, 0, Convert.ToHexString(reply));
        if (reply[7] > 100 || reply[12] > 1 || reply[13] > 1 || reply[14] is not (0 or 1 or 255))
            throw new InvalidDataException("Unknown P725 power fields; refusing to overwrite them.");
        return new(target.ProductId, idle, reply[7], 0, reply[12], reply[13], reply[14], Convert.ToHexString(reply));
    }

    /// <summary>Mouse power setter preserves all non-sleep fields from the actual readback; keyboard writes only idle.</summary>
    public static byte[] IdleReport(RogPairedDevice target, RogPowerState snapshot, byte idleCode)
    {
        RequireTarget(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsKnownIdle(idleCode)) throw new ArgumentOutOfRangeException(nameof(idleCode));
        if (snapshot.ProductId != target.ProductId) throw new ArgumentException("Power snapshot belongs to a different model.", nameof(snapshot));
        var report = new byte[RogProtocol.ReportLength];
        report[0] = target.ReportId;
        report[1] = 0x51;
        report[2] = target.IsAzothX ? (byte)0x38 : (byte)0x37;
        report[5] = idleCode;
        if (target.IsHarpeEdition20)
        {
            report[7] = snapshot.LowBatteryWarning;
            report[9] = snapshot.FullChargingEffect;
            report[10] = snapshot.WakeOnMotion;
            report[11] = snapshot.ChargingLighting;
        }
        return report;
    }

    public static bool IsKnownIdle(byte code) => code <= 4 || code == Never;

    private static void RequireTarget(RogPairedDevice target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ReportId is not (2 or 3) || !target.IsAzothX && !target.IsHarpeEdition20)
            throw new DeviceBlockedException("Power control requires an identified Azoth X or P725 receiver route.");
    }
}
