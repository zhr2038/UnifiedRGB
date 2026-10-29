namespace UnifiedRgb.Devices.AsusRog;

public enum RogNativeDecision { Ignore, EnsureOn, EnsureOff, Disarmed, Toggle }
public readonly record struct RogNativeIntent(uint Epoch, ulong Cookie, ushort Sequence, bool On, ushort NeutralSequence = 0);

/// <summary>Main actor state; caller serializes access. No I/O, typing data, synthetic neutral, or cached-mode enable.</summary>
public sealed class RogNativeMenuState
{
    public RogNativeCapability? Scope { get; private set; }
    public bool Neutral { get; private set; }
    public ushort Sequence { get; private set; }
    private byte _level;
    public void Arm(RogNativeCapability previous, RogNativeCapability next, ulong cookie)
    {
        if (previous.Epoch >= uint.MaxValue - 1 || next.Epoch != previous.Epoch + 1 || next.Cookie != cookie || cookie == 0
            || next.Mode != RogNativeMenuCodec.NativeMode || next.Profile != previous.Profile || next.Transport != previous.Transport
            || next.Status != 1 || (next.Flags & 3) != 1 || next.Sequence != 0) throw new InvalidDataException("Native ARM reply does not establish a fresh scope.");
        Scope = next; Neutral = false; Sequence = 0; _level = 0;
    }
    public void Disarm() { Scope = null; Neutral = false; Sequence = 0; _level = 0; }
    public bool Matches(RogNativeCapability next) => Scope is { } s && next.Mode == RogNativeMenuCodec.NativeMode && next.Status != 0
        && next.Epoch == s.Epoch && next.Cookie == s.Cookie && next.Profile == s.Profile && next.Transport == s.Transport;
    public bool Matches(RogNativeIntent intent) => Scope is { } s && s.Epoch == intent.Epoch && s.Cookie == intent.Cookie;
    public RogNativeDecision Accept(RogNativeNoticeKind kind, RogNativeNotice n)
    {
        if (kind == RogNativeNoticeKind.Ignore) return RogNativeDecision.Ignore;
        if (kind is RogNativeNoticeKind.Malformed or RogNativeNoticeKind.OutsideNative) return Close();
        if (Scope is not { } s) return RogNativeDecision.Ignore;
        if ((n.Flags & 4) != 0)
        {
            if (n.Cookie != 0) return Close();
            return n.Epoch >= s.Epoch ? Close() : RogNativeDecision.Ignore;
        }
        if (n.Epoch < s.Epoch) return RogNativeDecision.Ignore;
        if (n.Epoch != s.Epoch || n.Cookie != s.Cookie || n.Profile != s.Profile || n.Mode != RogNativeMenuCodec.NativeMode || (n.Flags & 1) == 0
            || n.Sequence == 0 || n.Sequence == ushort.MaxValue || n.Sequence != Sequence + 1) return Close();
        Sequence = n.Sequence;
        if (n.Mask == 0)
        {
            if (!Neutral && (n.Flags & 2) == 0) return Close();
            Neutral = true; _level = 0; return RogNativeDecision.Ignore;
        }
        // FreshNeutral marks the neutral EVENT, not every later press (the C-produced press vector has flags1).
        if (!Neutral) { _level = n.Mask; return RogNativeDecision.Ignore; }
        if (_level != 0) return _level == n.Mask ? RogNativeDecision.Ignore : Close();
        _level = n.Mask;
        return n.Mask == 2 ? RogNativeDecision.Toggle : RogNativeDecision.Ignore;
    }
    private RogNativeDecision Close() { Disarm(); return RogNativeDecision.Disarmed; }
}
