using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host;

/// <summary>
/// Reviewed local installation policy; all defaults produce zero native feature queries or input opens. Admission is the owner's
/// opt-in plus the captured descriptor route (device key and transport); the connection checks the live identity against the
/// known native family, and actual support is the live GYM1 capability grammar/geometry.
/// </summary>
public sealed record KeyboardNativeMenuOptions
{
    public bool Enabled { get; init; }
    public bool SourceControlNotifications { get; init; }
    /// <summary>Diagnostic build note only; blank, stale or mismatched values never admit or veto.</summary>
    public string NativeBuild { get; init; } = string.Empty;
    /// <summary>Diagnostic image receipt only; never a runtime pin, allow-list or deny-list.</summary>
    public string CandidateSha256 { get; init; } = string.Empty;
    /// <summary>Diagnostic contract receipt only; never a runtime pin.</summary>
    public string ContractSha256 { get; init; } = string.Empty;
    /// <summary>Separately qualified native control route. It does not grant legacy OLED or lighting verification.</summary>
    public string DeviceKey { get; init; } = string.Empty;
    /// <summary>Diagnostic header note only; blank, stale or arbitrary values never admit, veto, open or label the route.</summary>
    public string FirmwareHeader { get; init; } = string.Empty;
    public string ConnectionKind { get; init; } = string.Empty;

    internal bool Admitted => Enabled && SourceControlNotifications;
    internal bool HasQualifiedRoute => Admitted && DeviceKey is { Length: 64 } && DeviceKey.All(char.IsAsciiHexDigit)
        && RogRuntimeCompatibility.NativeFamilyAnchor(ConnectionKind) is not null;
    /// <summary>Anchor the native route opens with: the known identity family of its transport, never the configured header.</summary>
    internal string RouteAnchor => RogRuntimeCompatibility.NativeFamilyAnchor(ConnectionKind) ?? string.Empty;
    /// <summary>The same captured route, and a connection anchored to (so live-checked against) the known native family.</summary>
    internal bool MatchesScope(string key, string header, string kind) => HasQualifiedRoute
        && string.Equals(DeviceKey, key, StringComparison.OrdinalIgnoreCase) && ConnectionKind == kind
        && RogRuntimeCompatibility.IsCompatibleAnchor(header, RouteAnchor);

    /// <summary>
    /// Use the existing OLED owner's exact locally verified identities on either transport. The native opt-in still needs its
    /// qualified route; an alternate route never inherits legacy display/lighting admission or a configured firmware version.
    /// USB has the same priority as the ordinary OLED owner. A missing route may select the other known transport, while a
    /// failed open or failed live capability must not fall through to another owner.
    /// </summary>
    internal KeyboardNativeMenuOptions[] QualifiedRoutes(KeyboardDisplayOptions? display)
    {
        if (!HasQualifiedRoute) return [];
        var wired = ConnectionKind == "wired" ? this : display?.HasVerifiedWiredIdentity == true
            ? this with { DeviceKey = display.DeviceKey, ConnectionKind = "wired", FirmwareHeader = display.FirmwareHeader } : null;
        var wireless = ConnectionKind == "wireless" ? this : display?.HasVerifiedWirelessIdentity == true
            ? this with { DeviceKey = display.WirelessReceiverKey, ConnectionKind = "wireless", FirmwareHeader = display.WirelessFirmwareHeader } : null;
        return wired is not null ? wireless is not null ? [wired, wireless] : [wired]
            : wireless is not null ? [wireless] : [];
    }
}
