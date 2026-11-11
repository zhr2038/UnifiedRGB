using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UnifiedRgb.Host.Services;

internal sealed record HomeAssistantSceneState(KeyboardHomeAssistantScene Scene, string Name, bool Available);

internal sealed class HomeAssistantSceneClient : IDisposable
{
    private readonly Uri? _endpoint;
    private readonly HttpClient _http;
    private readonly Func<string, string?> _readToken;

    public HomeAssistantSceneClient(KeyboardHomeAssistantOptions options, HttpMessageHandler? handler = null,
        Func<string, string?>? readToken = null)
    {
        _endpoint = options.Endpoint;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(45),
            MaxResponseContentBufferSize = 65536,
        };
        _readToken = readToken ?? HomeAssistantCredential.Read;
    }

    internal static string CredentialTarget(Uri endpoint) => "UnifiedRgb/HomeAssistant/" + endpoint.GetLeftPart(UriPartial.Authority);

    public async Task<HomeAssistantSceneState> ReadAsync(KeyboardHomeAssistantScene scene, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "api/states/" + scene.EntityId);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var fallback = CleanLabel(scene.Label, scene.EntityId);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(scene, fallback, false);
        response.EnsureSuccessStatusCode();
        using var state = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = state.RootElement;
        if (root.GetProperty("entity_id").GetString() != scene.EntityId) throw new InvalidDataException("Unexpected HA scene entity.");
        var name = root.GetProperty("attributes").TryGetProperty("friendly_name", out var friendly) && friendly.ValueKind == JsonValueKind.String
            ? friendly.GetString() : null;
        var label = string.IsNullOrWhiteSpace(scene.Label) ? name : scene.Label;
        var value = root.GetProperty("state").GetString();
        // A native scene with no previous activation can legitimately have state "unknown".
        var available = scene.IsScript ? value == "off" : value is not (null or "" or "unavailable");
        return new(scene, CleanLabel(label, fallback), available);
    }

    public async Task<string> ActivateAsync(KeyboardHomeAssistantScene scene, Func<bool> stillAdmitted, CancellationToken ct)
    {
        var path = scene.ReadScriptResult ? "script/" + scene.EntityId[7..] + "?return_response"
            : scene.IsScript ? "script/turn_on" : "scene/turn_on";
        using var request = Request(HttpMethod.Post, "api/services/" + path);
        request.Content = scene.ReadScriptResult ? JsonContent.Create(new { }) : JsonContent.Create(new { entity_id = scene.EntityId });
        ct.ThrowIfCancellationRequested();
        if (!stillAdmitted()) return "已取消";
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500) return "结果未知";
        if (!response.IsSuccessStatusCode) return "HA 拒绝执行";
        if (!scene.ReadScriptResult) return "HA 已接收";
        using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        if (!result.RootElement.TryGetProperty("service_response", out var service) || service.ValueKind != JsonValueKind.Object
            || !service.TryGetProperty("unconfirmed", out var pending) || pending.ValueKind != JsonValueKind.Array) return "结果未知";
        return pending.GetArrayLength() == 0 ? "执行完成" : "部分设备未确认";
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var endpoint = _endpoint ?? throw new InvalidOperationException("Home Assistant address is missing or invalid.");
        var token = _readToken(CredentialTarget(endpoint));
        if (string.IsNullOrWhiteSpace(token)) throw new HomeAssistantCredentialMissingException();
        return new HttpRequestMessage(method, new Uri(endpoint, path)) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
    }

    private static string CleanLabel(string? label, string fallback)
    {
        if (string.IsNullOrWhiteSpace(label)) return fallback;
        var value = new string(label.Take(64).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return value.Length == 0 ? fallback : value;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class HomeAssistantCredentialMissingException : InvalidOperationException;

internal static partial class HomeAssistantCredential
{
    internal static string? Read(string target)
    {
        if (CredRead(target, 1, 0, out var pointer) == 0) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.BlobSize is > 0 and <= 2560 && credential.BlobSize % 2 == 0
                ? Marshal.PtrToStringUni(credential.Blob, credential.BlobSize / 2) : null;
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int BlobSize;
        public IntPtr Blob;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr credential);
}
