using System.Buffers.Binary;

namespace UnifiedRgb.Devices.AsusRog;

public readonly record struct RogNativeCapability(byte Status, byte Mode, byte Profile, byte Transport,
    byte Flags, uint Epoch, ulong Cookie, ushort Sequence);
public readonly record struct RogNativeNotice(byte Mask, byte Mode, byte Profile, byte Flags, uint Epoch, ulong Cookie, ushort Sequence);
public enum RogNativeNoticeKind { Ignore, Tagged, OutsideNative, Malformed }

/// <summary>
/// The frozen GYM1 contract; control uses existing normalized65/RID0, input remains native21/RID3 or4. Capability admission
/// is by grammar, geometry and the required in-epoch artwork capability only; the bytes [29..33) build tag is a diagnostic
/// that never admits or vetoes. Unknown optional flag bits are ignored and not surfaced.
/// </summary>
public static class RogNativeMenuCodec
{
    /// <summary>The reviewed reference build; diagnostic only, never a runtime admission value.</summary>
    public const string InstalledBuild = "GYM1:25314D49";
    public const uint BuildTag = 0x25314D49;
    public const byte NativeMode = 9;
    public const byte InEpochArtworkFlag = 32;
    public static bool SupportsInEpochArtwork(RogNativeCapability capability) => (capability.Flags & InEpochArtworkFlag) != 0;
    public static byte[] CapabilityRequest() { var p = new byte[65]; p[1] = 0x12; p[2] = 0x7D; return p; }
    public static byte[] ArmRequest(RogNativeCapability expected, ulong cookie, bool arm = true)
    {
        if (expected.Epoch == 0 || expected.Transport is not (1 or 2) || expected.Mode != NativeMode || expected.Profile > 5 || arm && cookie == 0)
            throw new InvalidDataException("Native ARM scope is invalid.");
        var p = new byte[65]; p[1] = 0x71; p[2] = 0x7D; "GYM1"u8.CopyTo(p.AsSpan(5));
        p[9] = 2; p[10] = arm ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), arm ? cookie : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(19), expected.Epoch);
        p[23] = expected.Profile; p[24] = expected.Transport; p[25] = NativeMode;
        return p;
    }
    public static RogNativeCapability ParseCapability(ReadOnlySpan<byte> p, byte command, byte transport)
    {
        if (p.Length != 65 || p[0] != 0 || p[1] != command || p[2] != 0x7D || p[3] != 0 || p[4] != 0
            || !p.Slice(5, 4).SequenceEqual("GYM1"u8) || p[9] != 2 || p[10] > 2 || p[11] > NativeMode || p[12] > 5 || p[13] != transport
            || transport is not (1 or 2) || (p[14] & InEpochArtworkFlag) == 0
            || BinaryPrimitives.ReadUInt16LittleEndian(p[33..]) != 208 || p[35] != 64 || p[36] != (transport == 1 ? 60 : 59)
            || p[37] != 3 || p[38..].ContainsAnyExcept((byte)0)) throw new InvalidDataException("Supported GYM1 capability family was not verified.");
        var c = new RogNativeCapability(p[10], p[11], p[12], p[13], (byte)(p[14] & RogRuntimeCompatibility.KnownCapabilityFlags),
            BinaryPrimitives.ReadUInt32LittleEndian(p[15..]), BinaryPrimitives.ReadUInt64LittleEndian(p[19..]), BinaryPrimitives.ReadUInt16LittleEndian(p[27..]));
        if (c.Epoch == 0 || c.Epoch == uint.MaxValue || c.Sequence == ushort.MaxValue
            || c.Status == 0 && (c.Cookie != 0 || (c.Flags & 3) != 0)
            || c.Status != 0 && (c.Cookie == 0 || (c.Flags & 1) == 0)
            || c.Status == 1 && (c.Flags & 2) != 0 || c.Status == 2 && (c.Flags & 2) == 0)
            throw new InvalidDataException("Native capability lifecycle is inconsistent.");
        return c;
    }
    public static RogNativeNoticeKind ParseNotice(ReadOnlySpan<byte> p, byte transport, out RogNativeNotice notice)
    {
        notice = default;
        if (p.Length != 21 || p[0] != (transport == 1 ? 3 : 4)) return RogNativeNoticeKind.Malformed;
        if (p[1] == 0x91)
        {
            if (p[2] != 0 || p[3] != 0 || p[4] > NativeMode || p[5..].ContainsAnyExcept((byte)0)) return RogNativeNoticeKind.Malformed;
            return p[4] == NativeMode ? RogNativeNoticeKind.Ignore : RogNativeNoticeKind.OutsideNative;
        }
        if (p[1] != 0x72) return RogNativeNoticeKind.Ignore; // Never inspect ordinary key/consumer bodies.
        if (p[3] == 0) return RogNativeNoticeKind.Ignore; // Ordinary72 is forever non-actionable.
        if (p[3] != 0xD5 || p[2] is not (0 or 1 or 2 or 4) || p[4] > NativeMode || (p[6] & ~7) != 0) return RogNativeNoticeKind.Malformed;
        notice = new(p[2], p[4], p[5], p[6], BinaryPrimitives.ReadUInt32LittleEndian(p[7..]),
            BinaryPrimitives.ReadUInt64LittleEndian(p[11..]), BinaryPrimitives.ReadUInt16LittleEndian(p[19..]));
        return RogNativeNoticeKind.Tagged;
    }
}
