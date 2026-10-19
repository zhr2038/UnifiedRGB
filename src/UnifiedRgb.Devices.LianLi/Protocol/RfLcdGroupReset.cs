using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.LianLi.Protocol;

/// <summary>
/// The LCD-group reset of <c>RFController.RebootLcdGroup</c> (analysis/il/il-RFController.cs 1448: selected = 0, reboot_lcd = 10,
/// targe_cmd_seq + 1, wrapping at 255 to 1) as <c>MasterDevice.SyncControlInfo</c> builds it (array8, 240 bytes): [0] = 18,
/// [1] = 22, [2..8) target MAC, [8..14) master MAC, [14] target rx, [15] target channel, [16] zero-based <c>rfList</c> index,
/// [17] the new command sequence, [18..240) zero. It restarts the group's LCD screens, not the fan firmware. RF 22 is
/// deliberately NOT an <see cref="RfCmd"/> member: only this builder emits it, for the one verified LCD fan type. It is not
/// RF 16 (bind + fan PWM), carries no PWM bytes, and the OpcodeFirewall deny lists are unchanged.
/// </summary>
public static class RfLcdGroupReset
{
    public const byte Opcode = 22;

    /// <summary>The only fan type this reset is verified for: TL LCD (RecType 28). Other LCD types are not assumed.</summary>
    public const byte LcdFanType = 35;

    public const byte TlRecType = 28;

    /// <summary><c>MasterDevice.RefreshList</c>: a record with this dev type is the master (masterList), never in rfList.</summary>
    public const byte MasterDevType = 255;

    /// <summary><c>targe_cmd_seq++</c>, then a value of 255 or more becomes 1 (never 0).</summary>
    public static byte NextSequence(byte current) => current >= 254 ? (byte)1 : (byte)(current + 1);

    public static byte[] Packet(RfMac target, RfMac master, RfAddress at, byte slaveIndex, byte sequence)
    {
        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "The command sequence is 1..254.");
        }

        var p = new byte[LianLiWire.RfPacketLength];
        p[0] = OpcodeLayout.RfPacketTag;
        p[1] = Opcode;
        target.Write(p.AsSpan(2));
        master.Write(p.AsSpan(8));
        p[14] = at.Rx;
        p[15] = at.Channel;
        p[16] = slaveIndex;
        p[17] = sequence;
        return p;
    }

    /// <summary>
    /// Pins the live target in a complete receiver list: exactly one master record (dev type 255) whose MAC is the sender's
    /// master; every bound device on that master; the target present once with the expected address, dev type 0, RecType 28,
    /// <paramref name="fans"/> fans all of type 35. Its slave index is its position after removing master records, as
    /// <c>RefreshList</c> builds rfList: never the configured adapter order. Throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static LcdGroupTarget Resolve(IReadOnlyList<RfReceiverReport> list, RfMac target, RfMac master, RfAddress address, int fans)
    {
        ArgumentNullException.ThrowIfNull(list);
        var masters = list.Where(r => r.Device.DevType == MasterDevType).ToArray();
        if (masters.Length != 1 || masters[0].Device.Mac != master)
        {
            throw new InvalidDataException($"The receiver list must hold exactly one master record {master}; found {masters.Length}.");
        }

        var devices = list.Where(r => r.Device.DevType != MasterDevType).ToArray();
        if (devices.Any(r => r.Device.MasterMac != master))
        {
            throw new InvalidDataException("A receiver record is bound to a different master; the slave index would be ambiguous.");
        }

        var index = Array.FindIndex(devices, r => r.Device.Mac == target);
        if (index < 0)
        {
            throw new InvalidDataException($"{target} is not in the live receiver list.");
        }

        var d = devices[index].Device;
        if (d.Address != address)
        {
            throw new InvalidDataException($"{target} reports channel {d.Address.Channel}/rx {d.Address.Rx}, configured {address.Channel}/{address.Rx}.");
        }

        if (d.DevType != 0 || d.RecType != TlRecType || d.FanCount != fans || d.FanTypes.Length < fans || d.FanTypes.Take(fans).Any(t => t != LcdFanType))
        {
            throw new InvalidDataException(
                $"{target} is not a verified {fans}-fan TL LCD group (dev type {d.DevType}, RecType {d.RecType}, {d.FanCount} fans, types {Convert.ToHexString(d.FanTypes)}).");
        }

        return index > byte.MaxValue
            ? throw new InvalidDataException("Slave index exceeds one byte.")
            : new LcdGroupTarget(devices[index], (byte)index);
    }
}

/// <summary>A pinned live target record and its zero-based rfList index.</summary>
public sealed record LcdGroupTarget(RfReceiverReport Report, byte SlaveIndex);
