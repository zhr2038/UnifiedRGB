using System.Net;

namespace UnifiedRgb.Host;

public sealed class KeyboardHomeAssistantOptions
{
    public string? Url { get; set; }
    public KeyboardHomeAssistantScene[] Scenes { get; set; } = [];

    internal Uri? Endpoint => ParseEndpoint(Url);

    internal static Uri? ParseEndpoint(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IsPrivateAddress(uri.Host)) ? uri : null;

    private static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        return address.GetAddressBytes() is [10, _, _, _] or [192, 168, _, _] or [172, >= 16 and <= 31, _, _];
    }
}

public sealed class KeyboardHomeAssistantScene
{
    public string EntityId { get; set; } = "";
    public string? Label { get; set; }
    public bool ReadScriptResult { get; set; }

    internal bool IsScript => EntityId.StartsWith("script.", StringComparison.Ordinal);
    internal bool IsValid => EntityId is { Length: > 0 and <= 100 }
        && (IsScript || !ReadScriptResult && EntityId.StartsWith("scene.", StringComparison.Ordinal))
        && EntityId.Length > EntityId.IndexOf('.') + 1
        && EntityId[(EntityId.IndexOf('.') + 1)..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
