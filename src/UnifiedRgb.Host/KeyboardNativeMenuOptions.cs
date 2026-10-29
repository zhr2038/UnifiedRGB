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
}
