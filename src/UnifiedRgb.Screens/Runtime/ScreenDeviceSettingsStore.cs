using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Screens.Encoding;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>One screen's saved local settings: clockwise content rotation and the versioned local encoder policy.</summary>
internal sealed record ScreenDeviceSetting(int Rotation, string EncodingQuality)
{
    public static ScreenDeviceSetting Default { get; } = new(0, ScreenEncodingPolicies.Legacy);
}

/// <summary>
/// What a display worker encodes with: the screen's saved policy and whether this exact screen id is configured as hardware
/// Indexed8-verified. Read from memory only (no I/O on the render path).
/// </summary>
internal sealed record ScreenEncodingSelection(string Quality, bool Indexed8Verified)
{
    public static ScreenEncodingSelection Legacy { get; } = new(ScreenEncodingPolicies.Legacy, false);
}

/// <summary>
/// Local per-screen settings (&lt;DataDirectory&gt;\screens\device-settings.json), keyed by screen id and owned by this machine:
/// never part of a screen scene, profile, scene package, package binding or content hash. Bounded (at most
/// <see cref="MaxEntries"/> screens, <see cref="MaxBytes"/> bytes), written atomically (temp file + rename) as UTF-8 without a BOM.
/// The file existing at all marks the one-time migration from the persisted screen scene as done (see ScreenManager).
/// </summary>
internal sealed partial class ScreenDeviceSettingsStore(string path, ILogger log)
{
    public const int SchemaVersion = 1;
    public const int MaxEntries = 64;
    public const int MaxBytes = 64 * 1024;

    public string Path => path;

    /// <summary>
    /// The saved settings; null when the file does not exist (first run). An unreadable, oversized or invalid file is logged and
    /// read as no settings (it is NOT migrated over; the next explicit save replaces it).
    /// </summary>
    public Dictionary<string, ScreenDeviceSetting>? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes)
            {
                LogIgnored(log, path, $"larger than {MaxBytes} bytes");
                return new(StringComparer.Ordinal);
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LogIgnored(log, path, ex.Message);
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Atomic replace (temp file + rename). I/O errors propagate to the caller.</summary>
    public void Save(IReadOnlyDictionary<string, ScreenDeviceSetting> settings)
    {
        var bytes = Serialize(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The problem with one setting, or null when it is valid. The policy must be selectable per
    /// <see cref="ScreenEncodingPolicies.IsKnown"/> (validation never consults the Indexed8 capability).
    /// </summary>
    public static string? Problem(string? screenId, int rotation, string? quality)
    {
        if (!ScreenIds.TryParse(screenId, out _, out _))
        {
            return $"'{screenId}' is not a screen id (screen:tl-lcd|h2|turzx:<instance>).";
        }

        if (rotation is not (0 or 90 or 180 or 270))
        {
            return $"{screenId}: rotation must be 0, 90, 180 or 270.";
        }

        return ScreenEncodingPolicies.IsKnown(quality)
            ? null
            : $"{screenId}: '{quality}' is not a selectable encoding quality ({ScreenEncodingPolicies.Legacy}, {ScreenEncodingPolicies.LosslessFirstV1}).";
    }

    /// <summary>Deterministic indented JSON (screens sorted by id), UTF-8 without a BOM.</summary>
    public static byte[] Serialize(IReadOnlyDictionary<string, ScreenDeviceSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count > MaxEntries)
        {
            throw new ArgumentException($"At most {MaxEntries} screens can have local settings.", nameof(settings));
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteStartArray("screens");
            foreach (var (id, s) in settings.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (Problem(id, s.Rotation, s.EncodingQuality) is { } problem)
                {
                    throw new ArgumentException(problem, nameof(settings));
                }

                w.WriteStartObject();
                w.WriteString("screenId", id);
                w.WriteNumber("rotation", s.Rotation);
                w.WriteString("encodingQuality", s.EncodingQuality);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        var bytes = ms.ToArray();
        return bytes.Length <= MaxBytes ? bytes : throw new ArgumentException($"The local screen settings exceed {MaxBytes} bytes.", nameof(settings));
    }

    /// <summary>Strict parse of schema 1 (a leading UTF-8 BOM from a hand edit is tolerated); anything else is InvalidDataException.</summary>
    public static Dictionary<string, ScreenDeviceSetting> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException($"larger than {MaxBytes} bytes");
        }

        using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("the root is not an object");
        }

        var version = 0;
        JsonElement? screens = null;
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "schemaVersion" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v):
                    version = v;
                    break;
                case "screens" when p.Value.ValueKind == JsonValueKind.Array:
                    screens = p.Value;
                    break;
                default:
                    throw new InvalidDataException($"unexpected member '{p.Name}'");
            }
        }

        if (version != SchemaVersion || screens is not { } list)
        {
            throw new InvalidDataException($"schemaVersion {SchemaVersion} with a screens array is required");
        }

        if (list.GetArrayLength() > MaxEntries)
        {
            throw new InvalidDataException($"more than {MaxEntries} screens");
        }

        var result = new Dictionary<string, ScreenDeviceSetting>(StringComparer.Ordinal);
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("a screen entry is not an object");
            }

            string? id = null, quality = null;
            int? rotation = null;
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "screenId" when p.Value.ValueKind == JsonValueKind.String:
                        id = p.Value.GetString();
                        break;
                    case "rotation" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var r):
                        rotation = r;
                        break;
                    case "encodingQuality" when p.Value.ValueKind == JsonValueKind.String:
                        quality = p.Value.GetString();
                        break;
                    default:
                        throw new InvalidDataException($"unexpected or malformed screen member '{p.Name}'");
                }
            }

            if (rotation is null)
            {
                throw new InvalidDataException($"{id}: rotation is missing");
            }

            if (Problem(id, rotation.Value, quality) is { } problem)
            {
                throw new InvalidDataException(problem);
            }

            if (!result.TryAdd(id!, new ScreenDeviceSetting(rotation.Value, quality!)))
            {
                throw new InvalidDataException($"{id} is listed twice");
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local screen settings {Path} ignored (no local settings in effect until the next explicit save): {Reason}")]
    private static partial void LogIgnored(ILogger logger, string path, string reason);
}
