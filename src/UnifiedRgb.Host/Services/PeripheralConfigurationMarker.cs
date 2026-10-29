using System.Text.Json;

namespace UnifiedRgb.Host.Services;

internal sealed class PeripheralConfigurationMarker(string path)
{
    internal sealed record LoadResult(bool Requested, string? Error);
    private sealed record Marker(int Schema, bool Requested);

    internal static PeripheralConfigurationMarker For(UnifiedRgbOptions options) =>
        new(Path.Combine(options.ResolvedDataDirectory, "peripherals", "peripheral-configuration.json"));

    internal LoadResult Load()
    {
        try
        {
            if (!File.Exists(path)) return new(false, null);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is 0 or > 4096) throw new InvalidDataException("Peripheral configuration marker exceeds its bound.");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
                || !root.TryGetProperty("Requested", out var requested) || requested.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Peripheral configuration marker is invalid.");
            return new(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or FormatException or OverflowException)
        {
            return new(true, "Invalid peripheral configuration marker; ROG acquisition is fail-closed. " + ex.Message);
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Marker(1, true));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    internal void Remove()
    {
        if (File.Exists(path)) File.Delete(path);
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}
