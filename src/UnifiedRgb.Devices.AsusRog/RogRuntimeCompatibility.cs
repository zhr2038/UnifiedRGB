namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// Normal runtime admission by known identity layout, report grammar and live capability grammar/geometry. Every OEM version
/// byte (patch and family), the configured header/image/build/contract text and the GYM1 build tag are diagnostic, so a first
/// identity of any version on the selected device needs no repinning. Image/flash tooling and immutable evidence receipts keep
/// their own exact checks; nothing here discovers, adopts or persists firmware. Recorded scope binds the transport through a
/// neutral route label; device, profile and same-session generation are bound by their own checks.
/// </summary>
public static class RogRuntimeCompatibility
{
    /// <summary>Capability flag bits this grammar defines; other bits are optional and ignored.</summary>
    public const byte KnownCapabilityFlags = 63;
    private static readonly byte[] IdentityPrefix = [0, 0x12, 0, 0, 0];
    private const string WiredLabelPrefix = "0012000000";

    /// <summary>Wired BasicInfo: <c>00 12 00 00 00 [oem x4]</c> in a 65-byte reply. The anchor and all OEM bytes are diagnostic.</summary>
    public static bool IsCompatibleWiredIdentity(ReadOnlySpan<byte> identity, string? anchor) =>
        identity.Length == 65 && identity[..5].SequenceEqual(IdentityPrefix);

    /// <summary>16-hex wireless label: receiver <c>[oem x4]</c> then keyboard <c>[oem x4]</c>; the hex values are diagnostic.</summary>
    public static bool IsWirelessAnchor(string? anchor) => anchor is { Length: 16 } && anchor.All(char.IsAsciiHexDigit);

    public static bool HasWirelessIdentityEnvelope(ReadOnlySpan<byte> identity) =>
        identity.Length == 65 && identity[..5].SequenceEqual(IdentityPrefix);

    /// <summary>All-zero keyboard bytes mean the receiver answered without its keyboard (asleep/disconnected), not a version.</summary>
    public static bool WirelessKeyboardIdentityAvailable(ReadOnlySpan<byte> identity) =>
        identity.Length == 65 && identity.Slice(12, 4).ContainsAnyExcept((byte)0);

    /// <summary>Valid receiver envelope with its keyboard present. Receiver [5..9), keyboard [12..16) and the anchor are diagnostic.</summary>
    public static bool IsCompatibleWirelessIdentity(ReadOnlySpan<byte> identity, string? anchor) =>
        HasWirelessIdentityEnvelope(identity) && WirelessKeyboardIdentityAvailable(identity);

    /// <summary>
    /// Recorded scope (journal, native preference, lighting route): both labels name the same transport. OEM bytes are never
    /// compared; wired and wireless labels never match each other, and unlabelled text (blank, arbitrary, malformed) matches nothing.
    /// </summary>
    public static bool IsCompatibleAnchor(string? recorded, string? current) =>
        LabelTransport(recorded) is { } transport && transport == LabelTransport(current);

    /// <summary>
    /// Neutral transport labels used where a route needs a stable label: wired <c>0012000000 00 000100</c>, wireless
    /// <c>00 000700 00 000100</c>. They name a transport format, never a version lock or an observed header (that stays in
    /// <c>ObservedFirmwareHeader</c>); configured native headers are diagnostics and never consulted.
    /// </summary>
    public static string? NativeFamilyAnchor(string? connectionKind) => connectionKind switch
    {
        "wired" => "001200000000000100",
        "wireless" => "0000070000000100",
        _ => null,
    };

    /// <summary>
    /// Route label for a connection of <paramref name="connectionKind"/>: configured text already in that transport's label form
    /// is kept verbatim (existing journals and scope stay byte-identical); blank, stale or arbitrary text becomes the neutral label.
    /// </summary>
    public static string? RouteLabel(string? connectionKind, string? configured) => connectionKind switch
    {
        "wired" or "wireless" => LabelTransport(configured) == connectionKind ? configured : NativeFamilyAnchor(connectionKind),
        _ => null,
    };

    private static string? LabelTransport(string? label) =>
        label is { Length: 18 } && label.StartsWith(WiredLabelPrefix, StringComparison.Ordinal) && label.All(char.IsAsciiHexDigit) ? "wired"
        : IsWirelessAnchor(label) ? "wireless" : null;
}
