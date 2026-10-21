using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>
/// The external scene package (OPUS-GENERIC-SCENE-PACKAGES): exact identifiers and bounds. schemaVersion 1 and 2 share one
/// playback contract; 2 ONLY adds the optional screen fields "accentColor" and "overlayLayout" (a telemetry region), which a
/// schemaVersion 1 manifest still rejects in any form, including null. Anything else — another format, schema version or
/// playback contract, including the "...Draft" / "...-draft" authoring drafts — is rejected explicitly; a package is never
/// upgraded, migrated, regenerated or reinterpreted.
/// </summary>
public static partial class ScenePackageFormat
{
    public const string Format = "UnifiedRgb.ScenePackage";
    public const int SchemaVersion = 1;

    /// <summary>The newest schema read: <see cref="SchemaVersion"/> plus the optional screen accentColor/overlayLayout.</summary>
    public const int SchemaVersionRegions = 2;
    /// <summary>Explicit video schema: adds MP4/H.264 screen media and up to 60 FPS for those tracks. v1/v2 are unchanged.</summary>
    public const int SchemaVersionVideo = 3;

    /// <summary>RGB24 frames held (no interpolation) for exactly frameDurationMs PHYSICAL milliseconds each; screens on the same timeline.</summary>
    public const string PlaybackContract = "rgb24-hold-physical-ms-v1";

    public const string ManifestName = "scene.json";

    /// <summary>The only file suffix of a lighting track (raw RGB24 bytes, frames × ledCount × 3).</summary>
    public const string LightingSuffix = ".rgb24";
    public const long MaxZipBytes = 64L * 1024 * 1024;
    public const int MaxEntries = 128;
    public const long MaxExpandedBytes = 256L * 1024 * 1024;
    public const int MaxManifestBytes = 256 * 1024;
    public const long MaxRgbBytes = 32L * 1024 * 1024;
    public const int MaxFramesPerTrack = 4096;
    public const int MaxLedsPerTrack = 4096;
    public const int MaxLightingTracks = 64;
    public const int MaxScreenTracks = 16;

    /// <summary>The existing screen upload limit (ImportLimits.MaxBytes); frames/duration/normalization are the importer's own.</summary>
    public const int MaxScreenFileBytes = 12 * 1024 * 1024;

    public const int MaxInertFileBytes = 256 * 1024;
    public const double MaxDurationMs = 600_000;
    public const double MaxFrameDurationMs = 60_000;
    public const int MaxPathLength = 200;
    public const int MaxNameLength = 128;
    public const int MaxLabelLength = 64;
    public const int MaxScreenFps = 15;

    /// <summary>Inert root files a package may carry without referencing them (never interpreted).</summary>
    public static IReadOnlySet<string> InertFiles { get; } = new HashSet<string>(StringComparer.Ordinal) { "README.md", "README.txt", "bindings.template.json" };

    public static IReadOnlySet<string> Overlays { get; } = new HashSet<string>(StringComparer.Ordinal) { "none", "compact", "dashboard" };

    public static IReadOnlySet<string> Fits { get; } = new HashSet<string>(StringComparer.Ordinal) { "contain", "cover" };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool IsSha256Hex(string? s) => s is { Length: 64 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static bool IsPackageId(string? s) => s is not null && PackageIdRegex().IsMatch(s);

    public static bool IsRole(string? s) => s is not null && RoleRegex().IsMatch(s);

    /// <summary>
    /// Null when <paramref name="path"/> is a safe relative package path: 1..200 of [A-Za-z0-9._-/], '/'-separated, no empty,
    /// '.', '..' or dot-ended segment, no reserved device name, no absolute/drive/UNC/backslash form; else why not.
    /// </summary>
    public static string? PathProblem(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
        {
            return $"a package path is 1..{MaxPathLength} characters";
        }

        foreach (var c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/'))
            {
                return $"path '{Printable(path)}' contains '{Printable(c.ToString())}' (allowed: A-Z a-z 0-9 . _ - /)";
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.'))
            {
                return $"path '{path}' has an empty, '.', '..' or dot-ended segment";
            }

            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                return $"path '{path}' uses the reserved device name '{stem}'";
            }
        }

        return null;
    }

    /// <summary>Control characters escaped, length bounded: for error text that quotes untrusted input.</summary>
    public static string Printable(string? s)
    {
        if (s is null)
        {
            return "(null)";
        }

        var t = s.Length > 80 ? s[..80] + "…" : s;
        return string.Concat(t.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()));
    }

    /// <summary>
    /// Parses and validates <paramref name="utf8"/> as a v1 manifest. Rejected (ArgumentException, reason first): not strict JSON
    /// (comments, trailing commas, duplicate keys), unknown format/schema/contract, unknown properties, bad ids/roles/paths,
    /// out-of-bounds counts and durations, inconsistent tracks, files that are unreferenced or unhashed.
    /// </summary>
    public static ScenePackageManifest ParseManifest(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxManifestBytes)
        {
            throw Bad($"{ManifestName} must be 1..{MaxManifestBytes} bytes");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw Bad($"{ManifestName} is not valid JSON ({ex.Message})");
        }

        using (doc)
        {
            var root = Obj(doc.RootElement, ManifestName);
            var format = Str(root, "format", ManifestName);
            if (!string.Equals(format, Format, StringComparison.Ordinal))
            {
                throw Bad(format.EndsWith("Draft", StringComparison.OrdinalIgnoreCase)
                    ? $"format '{Printable(format)}' is an authoring DRAFT; drafts are never imported or upgraded (author an explicit '{Format}' v{SchemaVersion} or v{SchemaVersionRegions} package)"
                    : $"unsupported format '{Printable(format)}' (this program reads '{Format}' only)");
            }

            var schema = Int(root, "schemaVersion", ManifestName);
            if (schema is not (SchemaVersion or SchemaVersionRegions or SchemaVersionVideo))
            {
                throw Bad($"unsupported schemaVersion {schema} (this program reads {SchemaVersion}, {SchemaVersionRegions} and {SchemaVersionVideo} only)");
            }

            var contract = Str(root, "playbackContract", ManifestName);
            if (!string.Equals(contract, PlaybackContract, StringComparison.Ordinal))
            {
                throw Bad($"unsupported playbackContract '{Printable(contract)}' (this program plays '{PlaybackContract}' only; never reinterpreted)");
            }

            Only(root, ManifestName, schema, "format", "schemaVersion", "id", "name", "revision", "durationMs", "loop", "playbackContract", "lighting", "screens", "files");
            var id = Str(root, "id", ManifestName);
            if (!IsPackageId(id))
            {
                throw Bad($"id '{Printable(id)}' must match [A-Za-z0-9][A-Za-z0-9._-]{{0,127}}");
            }

            var name = Text(Str(root, "name", ManifestName), "name", MaxNameLength);
            var revision = Int(root, "revision", ManifestName);
            if (revision < 1)
            {
                throw Bad("revision must be a positive integer");
            }

            var duration = Num(root, "durationMs", ManifestName);
            if (!(duration > 0) || duration > MaxDurationMs)
            {
                throw Bad($"durationMs must be > 0 and ≤ {MaxDurationMs}");
            }

            if (!Bool(root, "loop", ManifestName))
            {
                throw Bad("loop must be true (v1 packages are loops)");
            }

            var lighting = Arr(root, "lighting").Select((e, i) => Lighting(e, $"lighting[{i}]", schema)).ToArray();
            var screens = Arr(root, "screens").Select((e, i) => Screen(e, $"screens[{i}]", schema)).ToArray();
            var files = Files(root);
            var manifest = new ScenePackageManifest(id, name, revision, duration, lighting, screens, files) { SchemaVersion = schema };
            Check(manifest);
            return manifest;
        }
    }

    private static void Check(ScenePackageManifest m)
    {
        if (m.Lighting.Count + m.Screens.Count == 0)
        {
            throw Bad("a package needs at least one lighting or screen track");
        }

        if (m.Lighting.Count > MaxLightingTracks || m.Screens.Count > MaxScreenTracks)
        {
            throw Bad($"at most {MaxLightingTracks} lighting and {MaxScreenTracks} screen tracks");
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in m.Lighting.Select(l => l.Role).Concat(m.Screens.Select(s => s.Role)))
        {
            if (!roles.Add(role))
            {
                throw Bad($"role '{role}' is not unique (roles are unique across lighting and screens)");
            }
        }

        if (m.Lighting.Count > 0)
        {
            var first = m.Lighting[0];
            foreach (var l in m.Lighting)
            {
                if (l.Frames != first.Frames || l.FrameDurationMs != first.FrameDurationMs)
                {
                    throw Bad($"lighting '{l.Role}': every lighting track has the same frames and frameDurationMs ({first.Frames} × {first.FrameDurationMs} ms)");
                }
            }

            var total = first.Frames * first.FrameDurationMs;
            if (Math.Abs(total - m.DurationMs) > 1e-9 * m.DurationMs)
            {
                throw Bad($"lighting is {first.Frames} × {first.FrameDurationMs} ms = {total} ms, not the package durationMs {m.DurationMs}");
            }

            var bytes = m.Lighting.Sum(l => l.ByteLength);
            if (bytes > MaxRgbBytes)
            {
                throw Bad($"lighting totals {bytes} bytes (at most {MaxRgbBytes})");
            }
        }

        foreach (var s in m.Screens)
        {
            if (s.DurationMs != m.DurationMs)
            {
                throw Bad($"screen '{s.Role}': durationMs {s.DurationMs} must equal the package durationMs {m.DurationMs}");
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in m.Lighting.Select(l => (l.Role, l.File)).Concat(m.Screens.Select(s => (s.Role, s.File))))
        {
            if (!m.Files.ContainsKey(file.File))
            {
                throw Bad($"'{file.Role}' file '{file.File}' has no entry in files (every file is hash-pinned)");
            }

            referenced.Add(file.File);
        }

        foreach (var s in m.Screens)
        {
            if (m.Lighting.Any(l => string.Equals(l.File, s.File, StringComparison.Ordinal)))
            {
                throw Bad($"file '{s.File}' cannot be both lighting and screen media");
            }
        }

        foreach (var path in m.Files.Keys)
        {
            if (!referenced.Contains(path) && !InertFiles.Contains(path))
            {
                throw Bad($"files entry '{path}' is not referenced by any track (only {string.Join(", ", InertFiles)} may be carried unreferenced)");
            }
        }
    }

    private static LightingTrack Lighting(JsonElement e, string where, int schema)
    {
        var o = Obj(e, where);
        Only(o, where, schema, "role", "label", "file", "ledCount", "frames", "frameDurationMs", "pixelFormat", "pixelOrder", "brightnessBaked");
        var role = Role(o, where);
        var label = Label(o, where);
        var file = FilePath(o, where);
        if (!file.EndsWith(LightingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            // inert data only: a track is never declared as e.g. .exe/.dll/.ps1 just because its byte length happens to fit
            throw Bad($"{where}: a lighting track is a raw RGB24 file named *{LightingSuffix} ('{file}')");
        }

        var leds = Int(o, "ledCount", where);
        var frames = Int(o, "frames", where);
        var fd = Num(o, "frameDurationMs", where);
        if (leds is < 1 or > MaxLedsPerTrack)
        {
            throw Bad($"{where}: ledCount must be 1..{MaxLedsPerTrack}");
        }

        if (frames is < 1 or > MaxFramesPerTrack)
        {
            throw Bad($"{where}: frames must be 1..{MaxFramesPerTrack}");
        }

        if (!(fd > 0) || fd > MaxFrameDurationMs)
        {
            throw Bad($"{where}: frameDurationMs must be > 0 and ≤ {MaxFrameDurationMs}");
        }

        if (Str(o, "pixelFormat", where) != "RGB24")
        {
            throw Bad($"{where}: pixelFormat must be \"RGB24\"");
        }

        if (Str(o, "pixelOrder", where) != "logical-layout")
        {
            throw Bad($"{where}: pixelOrder must be \"logical-layout\"");
        }

        double? baked = null;
        if (o.ContainsKey("brightnessBaked"))
        {
            baked = Num(o, "brightnessBaked", where);
            if (baked is < 0 or > 1)
            {
                throw Bad($"{where}: brightnessBaked is metadata in 0..1");
            }
        }

        return new LightingTrack(role, label, file, leds, frames, fd, baked);
    }

    private static ScreenTrack Screen(JsonElement e, string where, int schema)
    {
        var o = Obj(e, where);
        if (schema == SchemaVersion)
        {
            Only(o, where, schema, "role", "label", "file", "durationMs", "overlay", "fit", "fps");
        }
        else
        {
            Only(o, where, schema, "role", "label", "file", "durationMs", "overlay", "fit", "fps", "accentColor", "overlayLayout");
        }

        var role = Role(o, where);
        var label = Label(o, where);
        var file = FilePath(o, where);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var video = schema == SchemaVersionVideo && ext == ".mp4";
        if (!video && ext is not (".png" or ".jpg" or ".jpeg" or ".gif"))
        {
            throw Bad($"{where}: screen media is PNG, JPEG or GIF; MP4 requires schemaVersion 3 ('{file}')");
        }

        var duration = Num(o, "durationMs", where);
        var overlay = Str(o, "overlay", where);
        if (!Overlays.Contains(overlay))
        {
            throw Bad($"{where}: overlay must be none, compact or dashboard");
        }

        var fit = Str(o, "fit", where);
        if (!Fits.Contains(fit))
        {
            throw Bad($"{where}: fit must be contain or cover");
        }

        var fps = Int(o, "fps", where);
        var maxFps = video ? 60 : MaxScreenFps;
        if (fps < 1 || fps > maxFps)
        {
            throw Bad($"{where}: fps must be 1..{maxFps}");
        }

        string? accent = null;
        if (o.ContainsKey("accentColor"))
        {
            accent = Str(o, "accentColor", where);
            if (!IsColor(accent))
            {
                throw Bad($"{where}: accentColor must be #RRGGBB");
            }

            if (overlay == "none")
            {
                throw Bad($"{where}: accentColor needs a compact or dashboard overlay (overlay is none)");
            }
        }

        ScreenOverlayLayoutDto? layout = null;
        if (o.TryGetValue("overlayLayout", out var layoutElement))
        {
            if (overlay != "dashboard")
            {
                throw Bad($"{where}: overlayLayout needs overlay dashboard (overlay is {overlay}; a compact region is not supported)");
            }

            layout = Layout(layoutElement, $"{where}.overlayLayout", schema);
        }

        return new ScreenTrack(role, label, file, duration, overlay, fit, fps, accent, layout);
    }

    /// <summary>A v2 telemetry region: fractions of the content canvas before local rotation, exact property names.</summary>
    private static ScreenOverlayLayoutDto Layout(JsonElement e, string where, int schema)
    {
        var o = Obj(e, where);
        Only(o, where, schema, "x", "y", "width", "height", "columns", "backgroundOpacity");
        var layout = new ScreenOverlayLayoutDto(Num(o, "x", where), Num(o, "y", where), Num(o, "width", where), Num(o, "height", where),
            Int(o, "columns", where), Num(o, "backgroundOpacity", where));
        return OverlayLayouts.Problem(layout) is { } why
            ? throw Bad($"{where}: {why}")
            : layout;
    }

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789ABCDEFabcdef");

    /// <summary>Strict #RRGGBB (exactly six hex digits).</summary>
    private static bool IsColor(string s) => s.Length == 7 && s[0] == '#' && s.AsSpan(1).IndexOfAnyExcept(HexDigits) < 0;

    private static Dictionary<string, string> Files(Dictionary<string, JsonElement> root)
    {
        var o = Obj(root.TryGetValue("files", out var f) ? f : throw Bad("files is required"), "files");
        if (o.Count == 0 || o.Count > MaxEntries - 1)
        {
            throw Bad($"files must list 1..{MaxEntries - 1} entries");
        }

        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, value) in o)
        {
            if (PathProblem(path) is { } why)
            {
                throw Bad($"files: {why}");
            }

            if (string.Equals(path, ManifestName, StringComparison.OrdinalIgnoreCase))
            {
                throw Bad($"files must not list {ManifestName}");
            }

            if (!folded.Add(path))
            {
                throw Bad($"files: '{path}' collides with another path ignoring case");
            }

            if (value.ValueKind != JsonValueKind.String || !IsSha256Hex(value.GetString()))
            {
                throw Bad($"files['{path}'] must be a full lowercase SHA-256 hex string");
            }

            files.Add(path, value.GetString()!);
        }

        return files;
    }

    private static string Role(Dictionary<string, JsonElement> o, string where)
    {
        var role = Str(o, "role", where);
        return IsRole(role) ? role : throw Bad($"{where}: role '{Printable(role)}' must match [a-z0-9][a-z0-9-]{{0,63}}");
    }

    private static string? Label(Dictionary<string, JsonElement> o, string where) =>
        o.ContainsKey("label") ? Text(Str(o, "label", where), $"{where}.label", MaxLabelLength) : null;

    private static string FilePath(Dictionary<string, JsonElement> o, string where)
    {
        var file = Str(o, "file", where);
        return PathProblem(file) is { } why ? throw Bad($"{where}: {why}") : file;
    }

    private static string Text(string s, string what, int max) =>
        string.IsNullOrWhiteSpace(s) || s.Length > max || s.Any(char.IsControl)
            ? throw Bad($"{what} must be 1..{max} printable characters")
            : s;

    private static Dictionary<string, JsonElement> Obj(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Bad($"{where} must be a JSON object");
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            if (!map.TryAdd(p.Name, p.Value))
            {
                throw Bad($"{where}: duplicate property '{Printable(p.Name)}'");
            }
        }

        return map;
    }

    private static void Only(Dictionary<string, JsonElement> o, string where, int schema, params string[] allowed)
    {
        foreach (var key in o.Keys)
        {
            if (Array.IndexOf(allowed, key) < 0)
            {
                throw Bad($"{where}: unknown property '{Printable(key)}' (v{schema} defines no such field)");
            }
        }
    }

    private static JsonElement Required(Dictionary<string, JsonElement> o, string key, string where) =>
        o.TryGetValue(key, out var v) ? v : throw Bad($"{where}: '{key}' is required");

    private static string Str(Dictionary<string, JsonElement> o, string key, string where)
    {
        var v = Required(o, key, where);
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Bad($"{where}: '{key}' must be a string");
    }

    private static int Int(Dictionary<string, JsonElement> o, string key, string where)
    {
        var v = Required(o, key, where);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && !v.GetRawText().Contains('.', StringComparison.Ordinal)
            && !v.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase)
            ? i
            : throw Bad($"{where}: '{key}' must be an integer");
    }

    private static double Num(Dictionary<string, JsonElement> o, string key, string where)
    {
        var v = Required(o, key, where);
        return v.ValueKind == JsonValueKind.Number && double.TryParse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d
            : throw Bad($"{where}: '{key}' must be a finite number");
    }

    private static bool Bool(Dictionary<string, JsonElement> o, string key, string where)
    {
        var v = Required(o, key, where);
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Bad($"{where}: '{key}' must be true or false");
    }

    private static JsonElement[] Arr(Dictionary<string, JsonElement> o, string key)
    {
        if (!o.TryGetValue(key, out var v))
        {
            return [];
        }

        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : throw Bad($"'{key}' must be an array");
    }

    public static ArgumentException Bad(string why) => new($"Scene package rejected: {why}.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleRegex();
}

/// <summary>A validated v1/v2 manifest (see <see cref="SchemaVersion"/>). Lighting files are frame-major RGB24, <see cref="LightingTrack.ByteLength"/> bytes each.</summary>
public sealed record ScenePackageManifest(
    string Id,
    string Name,
    int Revision,
    double DurationMs,
    IReadOnlyList<LightingTrack> Lighting,
    IReadOnlyList<ScreenTrack> Screens,
    IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The manifest's declared schemaVersion (1 or 2); never upgraded.</summary>
    public int SchemaVersion { get; init; } = ScenePackageFormat.SchemaVersion;

    /// <summary>Roles in manifest order: lighting first, then screens.</summary>
    public IEnumerable<string> Roles => Lighting.Select(l => l.Role).Concat(Screens.Select(s => s.Role));
}

/// <summary>One authored RGB24 track. <see cref="BrightnessBaked"/> is metadata only: it is never multiplied again.</summary>
public sealed record LightingTrack(string Role, string? Label, string File, int LedCount, int Frames, double FrameDurationMs, double? BrightnessBaked)
{
    public long ByteLength => (long)Frames * LedCount * 3;
}

/// <summary>
/// One screen medium on the package timeline. <see cref="AccentColor"/> and <see cref="OverlayLayout"/> exist only in schemaVersion 2
/// (null = the historical behaviour).
/// </summary>
public sealed record ScreenTrack(
    string Role, string? Label, string File, double DurationMs, string Overlay, string Fit, int Fps, string? AccentColor = null,
    ScreenOverlayLayoutDto? OverlayLayout = null);

/// <summary>Package timeline ids ("package:&lt;contentId&gt;:&lt;bindingId&gt;"): the frame-set id and the screens' TimelineId.</summary>
public static class ScenePackageIds
{
    public const string Prefix = "package:";

    public static string TimelineIdOf(string contentId, string bindingId) => $"{Prefix}{contentId}:{bindingId}";

    public static bool IsTimelineId(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string? id, out string contentId, out string bindingId)
    {
        contentId = bindingId = string.Empty;
        if (!IsTimelineId(id))
        {
            return false;
        }

        var rest = id![Prefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        contentId = rest[..colon];
        bindingId = rest[(colon + 1)..];
        return ScenePackageFormat.IsSha256Hex(contentId) && ScenePackageFormat.IsSha256Hex(bindingId);
    }
}
