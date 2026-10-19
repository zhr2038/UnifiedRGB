using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// GUI mounting overrides in <c>&lt;DataDirectory&gt;\mounting.json</c>, keyed by stable controller ID (WP-11,
/// DEBUG-SESSION-1 §3.2). Kept outside lighting profiles so applying a profile never undoes calibration; overrides the
/// appsettings defaults (UnifiedRgb:LianLi:Devices[].Mounting). Written atomically like <see cref="ProfileStore"/>: unique temp
/// file in the same directory, flushed, then renamed over the target.
/// </summary>
public sealed class MountingStore
{
    public const string FileName = "mounting.json";
    public const long MaxFileBytes = 256 * 1024;
    public const int MaxIdLength = 128;

    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions { WriteIndented = true });

    private readonly object _gate = new();

    public MountingStore(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

    public MountingStore(UnifiedRgbOptions options)
        : this(System.IO.Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).ResolvedDataDirectory, FileName))
    {
    }

    public string Path { get; }

    /// <summary>Lian Li RF controller IDs only (<c>lianli:rf:&lt;mac&gt;</c>), bounded, no control characters.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && id.StartsWith("lianli:rf:", StringComparison.Ordinal) && !id.Any(char.IsControl);

    /// <summary>All stored overrides; empty when the file is absent. Throws InvalidDataException when unreadable.</summary>
    public IReadOnlyDictionary<string, MountingDto> Load()
    {
        lock (_gate)
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                return new Dictionary<string, MountingDto>(StringComparer.Ordinal);
            }

            if (info.Length > MaxFileBytes)
            {
                throw new InvalidDataException($"{FileName} exceeds {MaxFileBytes} bytes.");
            }

            MountingFileDto? file;
            try
            {
                file = JsonSerializer.Deserialize<MountingFileDto>(File.ReadAllBytes(Path), Json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
            }

            if (file?.Devices is null || file.SchemaVersion is < 1 or > MountingFileDto.CurrentSchema)
            {
                throw new InvalidDataException($"{FileName} is not a supported mounting file.");
            }

            var map = new Dictionary<string, MountingDto>(StringComparer.Ordinal);
            foreach (var d in file.Devices.Where(d => d is not null && IsValidId(d.ControllerId)))
            {
                map[d.ControllerId] = Normalize(d);
            }

            return map;
        }
    }

    /// <summary>Sets (or with null removes) one device's override and rewrites the file atomically.</summary>
    public void Save(string controllerId, MountingDto? mounting)
    {
        if (!IsValidId(controllerId))
        {
            throw new ArgumentException($"'{controllerId}' is not a Lian Li controller ID.", nameof(controllerId));
        }

        lock (_gate)
        {
            var all = new Dictionary<string, MountingDto>(Load(), StringComparer.Ordinal);
            if (mounting is null)
            {
                all.Remove(controllerId);
            }
            else
            {
                all[controllerId] = Normalize(mounting with { ControllerId = controllerId });
            }

            Write(new MountingFileDto(MountingFileDto.CurrentSchema, all.Values.OrderBy(d => d.ControllerId, StringComparer.Ordinal).ToArray()));
        }
    }

    /// <summary>Only the settings are stored; Host-filled read-only fields are dropped.</summary>
    private static MountingDto Normalize(MountingDto d) =>
        new(d.ControllerId, d.RotationDegrees, d.Reverse, (d.Fans ?? []).OrderBy(f => f.Fan).ToArray(), ReverseChain: d.ReverseChain,
            RingOffsetLeds: d.RingOffsetLeds, HalfStep: d.HalfStep, FaceOnFill: d.FaceOnFill);

    private void Write(MountingFileDto file)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(dir);
        var tmp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
