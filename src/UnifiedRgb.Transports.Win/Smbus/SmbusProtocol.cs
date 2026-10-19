namespace UnifiedRgb.Transports.Smbus;

/// <summary>
/// SMBus constants, taken from the pinned <c>analysis/upstream-native-m4/i2c_smbus.h</c> (SHA-256 4180EC7E…, NATIVE design
/// §5.1) and identical in the official PawnIO.Modules 0.2.11 <c>SmbusPIIX4.p</c>. Not remembered Linux values.
/// </summary>
public static class SmbusConstants
{
    /// <summary><c>I2C_SMBUS_READ</c>.</summary>
    public const ulong Read = 1;

    /// <summary><c>I2C_SMBUS_WRITE</c>.</summary>
    public const ulong Write = 0;

    /// <summary><c>I2C_SMBUS_QUICK</c>.</summary>
    public const ulong Quick = 0;

    /// <summary><c>I2C_SMBUS_BYTE</c> (receive/send byte).</summary>
    public const ulong Byte = 1;

    /// <summary><c>I2C_SMBUS_BYTE_DATA</c>.</summary>
    public const ulong ByteData = 2;

    /// <summary><c>I2C_SMBUS_WORD_DATA</c>.</summary>
    public const ulong WordData = 3;

    /// <summary><c>I2C_SMBUS_BLOCK_DATA</c>.</summary>
    public const ulong BlockData = 5;

    /// <summary><c>I2C_SMBUS_I2C_BLOCK_DATA</c> (not used).</summary>
    public const ulong I2cBlockData = 8;

    /// <summary><c>I2C_SMBUS_BLOCK_MAX</c>.</summary>
    public const int BlockMax = 32;

    /// <summary><c>sizeof(union i2c_smbus_data)</c>: the <c>block[I2C_SMBUS_BLOCK_MAX + 2]</c> member.</summary>
    public const int DataUnionBytes = BlockMax + 2;

    /// <summary>Global mutex every SMBus user on Windows takes around a transaction (reference wrapper).</summary>
    public const string GlobalMutexName = @"Global\Access_SMBUS.HTP.Method";
}

/// <summary>ENE SMBus register map (NATIVE design §5.1; upstream ENESMBusController.h, GPL reference, not copied).</summary>
public static class EneRegisters
{
    public const ushort DeviceName = 0x1000;
    public const int DeviceNameLength = 16;
    public const ushort MicronCheck = 0x1030;
    public const int MicronCheckLength = 6;
    public const ushort ConfigTable = 0x1C00;
    public const int ConfigTableLength = 64;
    public const ushort DirectV1 = 0x8000;
    public const ushort DirectV2 = 0x8100;

    /// <summary>Effect colour bases; each bounds the direct colour range below it (V1 0x8000..0x800F, V2 0x8100..0x815F).</summary>
    public const ushort EffectV1 = 0x8010;
    public const ushort EffectV2 = 0x8160;
    public const ushort Direct = 0x8020;
    public const ushort Mode = 0x8021;
    public const ushort Apply = 0x80A0;
    public const ushort SlotIndex = 0x80F8;
    public const ushort I2cAddress = 0x80F9;
    public const byte ApplyValue = 0x01;

    /// <summary>The address every ENE DRAM module answers on before the bootstrap remap.</summary>
    public const byte DefaultDramAddress = 0x77;

    /// <summary>
    /// The physical candidate allow-list (design §5.1): 0x70..0x77, 0x4F, 0x66, 0x67, 0x39..0x3D. Configuration may narrow
    /// it, never widen it; SPD/EEPROM 0x50..0x57 is outside it by construction.
    /// </summary>
    public static IReadOnlyList<byte> PhysicalCandidates { get; } =
        [0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D];

    /// <summary>True for the SPD/EEPROM address family 0x50..0x57, never touched.</summary>
    public static bool IsSpd(byte address) => address is >= 0x50 and <= 0x57;

    /// <summary>The largest LED count whose direct range [base, base + 3n) stays below the effect colours of that version.</summary>
    public static int MaxDirectLeds(ushort directBase) => directBase switch
    {
        DirectV1 => (EffectV1 - DirectV1) / 3,
        DirectV2 => (EffectV2 - DirectV2) / 3,
        _ => 0,
    };

    /// <summary>SMBus command bytes of the ENE register interface.</summary>
    public const byte PointerCommand = 0x00;
    public const byte ReadCommand = 0x81;
    public const byte WriteCommand = 0x01;
    public const byte BlockCommand = 0x03;
    public const int MaxBlock = 3;

    /// <summary>The 16-bit register byte-swapped as the pointer word: 0x8000 → 0x0080, 0x1C00 → 0x001C.</summary>
    public static ushort PointerWord(ushort register) => (ushort)(((register << 8) & 0xFF00) | ((register >> 8) & 0x00FF));
}

public enum SmbusCompoundKind : byte
{
    /// <summary>Receive byte at the address (ACK test).</summary>
    Probe = 1,

    /// <summary>Plain read-byte-data at a command (no pointer): 0x00 fallback probe and the 0xA0..0xAF signature.</summary>
    ReadSig = 2,

    /// <summary>Pointer write + read-byte-data 0x81.</summary>
    RegRead = 3,

    /// <summary>Pointer write + write-byte-data 0x01.</summary>
    RegWrite = 4,

    /// <summary>Pointer write + write-block-data 0x03 (1..3 bytes).</summary>
    RegBlock = 5,
}

/// <summary>
/// One compound SMBus operation, the unit of work of the ENE bus (NATIVE design §5.2): the pointer write and the data
/// transaction run under one mutex acquisition, so no pointer is left armed between calls and the firewall validates the
/// pair in one stateless decision. <see cref="ToWire"/> is the frame the validator and packet log see:
/// Probe <c>[1, a]</c>, ReadSig <c>[2, a, cmd]</c>, RegRead <c>[3, a, hi, lo]</c>, RegWrite <c>[4, a, hi, lo, v]</c>,
/// RegBlock <c>[5, a, hi, lo, n, d…]</c>.
/// </summary>
public readonly struct SmbusCompound : IEquatable<SmbusCompound>
{
    private readonly byte[]? _data;

    private SmbusCompound(SmbusCompoundKind kind, byte address, ushort register, byte command, byte[]? data)
    {
        Kind = kind;
        Address = address;
        Register = register;
        Command = command;
        _data = data;
    }

    public SmbusCompoundKind Kind { get; }

    public byte Address { get; }

    public ushort Register { get; }

    /// <summary>ReadSig: the command byte. RegWrite: the value.</summary>
    public byte Command { get; }

    /// <summary>RegBlock data (1..3 bytes); empty otherwise.</summary>
    public ReadOnlySpan<byte> Data => _data;

    public static SmbusCompound Probe(byte address) => new(SmbusCompoundKind.Probe, address, 0, 0, null);

    public static SmbusCompound ReadSig(byte address, byte command) => new(SmbusCompoundKind.ReadSig, address, 0, command, null);

    public static SmbusCompound RegRead(byte address, ushort register) => new(SmbusCompoundKind.RegRead, address, register, 0, null);

    public static SmbusCompound RegWrite(byte address, ushort register, byte value) => new(SmbusCompoundKind.RegWrite, address, register, value, null);

    public static SmbusCompound RegBlock(byte address, ushort register, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data.Length > EneRegisters.MaxBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"A block carries 1..{EneRegisters.MaxBlock} bytes.");
        }

        return new(SmbusCompoundKind.RegBlock, address, register, 0, data.ToArray());
    }

    public static bool operator ==(SmbusCompound left, SmbusCompound right) => left.Equals(right);

    public static bool operator !=(SmbusCompound left, SmbusCompound right) => !left.Equals(right);

    public byte[] ToWire() => Kind switch
    {
        SmbusCompoundKind.Probe => [(byte)Kind, Address],
        SmbusCompoundKind.ReadSig => [(byte)Kind, Address, Command],
        SmbusCompoundKind.RegRead => [(byte)Kind, Address, (byte)(Register >> 8), (byte)Register],
        SmbusCompoundKind.RegWrite => [(byte)Kind, Address, (byte)(Register >> 8), (byte)Register, Command],
        _ => [(byte)Kind, Address, (byte)(Register >> 8), (byte)Register, (byte)Data.Length, .. Data],
    };

    public bool Equals(SmbusCompound other) =>
        Kind == other.Kind && Address == other.Address && Register == other.Register && Command == other.Command && Data.SequenceEqual(other.Data);

    public override bool Equals(object? obj) => obj is SmbusCompound c && Equals(c);

    public override int GetHashCode() => HashCode.Combine(Kind, Address, Register, Command, Data.Length);

    public override string ToString() => Kind switch
    {
        SmbusCompoundKind.Probe => $"Probe(0x{Address:X2})",
        SmbusCompoundKind.ReadSig => $"ReadSig(0x{Address:X2}, 0x{Command:X2})",
        SmbusCompoundKind.RegRead => $"RegRead(0x{Address:X2}, 0x{Register:X4})",
        SmbusCompoundKind.RegWrite => $"RegWrite(0x{Address:X2}, 0x{Register:X4} <- 0x{Command:X2})",
        _ => $"RegBlock(0x{Address:X2}, 0x{Register:X4}, {Convert.ToHexString(Data)})",
    };
}

/// <summary>Result of one compound: success, the native status verbatim (0 = OK), the byte read (reads only).</summary>
/// <param name="Dropped">True when nothing was sent (mutex timeout); the compound is dropped whole.</param>
/// <param name="Step">On failure the transaction that failed: "pointer", "read", "write" or "block" (null otherwise).</param>
public sealed record SmbusResult(bool Ok, int Status, byte Value, string? Failure, bool Dropped = false, double WallMs = 0, string? Step = null)
{
    public static SmbusResult Success(byte value, double wallMs) => new(true, 0, value, null, false, wallMs);
}

/// <summary>One PIIX4 port of the SMBus, executing whole compounds (NATIVE design §5.2). One instance per port.</summary>
public interface ISmbusPort
{
    int Port { get; }

    /// <summary>Executes one compound under one bounded mutex acquisition; never throws for a bus error (status in the result).</summary>
    SmbusResult Execute(SmbusCompound compound);

    /// <summary>Compounds dropped whole because the global mutex was not acquired within the bound.</summary>
    long MutexTimeouts { get; }
}

/// <summary>An opened PawnIO SMBus port: identity established (1022:790B), port selected once; disposing closes its handle.</summary>
public interface IPawnIoPort : ISmbusPort, IDisposable
{
    PawnIoIdentity Identity { get; }

    /// <summary>
    /// The sleep mode <c>ioctl_set_sleep_mode</c> accepted (S_OK) for this opened handle; null when the port does not observe
    /// it (scripted test ports). Probe evidence only, never inferred from configuration.
    /// </summary>
    int? AppliedSleepMode => null;
}
